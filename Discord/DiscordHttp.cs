using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ServerManager.Discord
{
    /// <summary>Never contains an endpoint, response body, credential, or inner exception.</summary>
    internal sealed class DiscordHttpException : Exception
    {
        public DiscordHttpException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; }
    }

    /// <summary>
    /// Small Mono-compatible Discord transport. A single request is in flight, but
    /// rate-limit waits happen outside the lane so interaction acknowledgements can
    /// pass a throttled unrelated route. The caller owns the lifetime of this client.
    /// </summary>
    internal sealed class DiscordHttp : IDisposable
    {
        private const int MaximumRetries = 5;
        private const int MaximumResponseBytes = 1024 * 1024;
        private const int MaximumRequestBytes = 256 * 1024;
        private const int MaximumRateEntries = 256;
        private const int MaximumJsonDepth = 32;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly HttpClient _client;
        private readonly string _botToken;
        private readonly Action<string> _log;
        private readonly SemaphoreSlim _lane = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Dictionary<string, string> _routeBuckets =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _cooldowns =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private DateTime _botGlobalUntil;
        private DateTime _anonymousGlobalUntil;
        private DateTime _overflowUntil;
        private int _disposed;

        public DiscordHttp(string botToken, Action<string> log, HttpMessageHandler? handler = null)
        {
            _botToken = botToken ?? string.Empty;
            _log = log ?? delegate { };
            if (handler == null)
            {
                handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };
            }
            else if (handler is HttpClientHandler clientHandler)
            {
                clientHandler.AllowAutoRedirect = false;
                clientHandler.UseCookies = false;
            }

            _client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("ServerManager/1.0");
        }

        public async Task<JToken?> SendAsync(
            HttpMethod method, string endpoint, JToken? body, bool useBotToken,
            CancellationToken ct, bool retry = true)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            Uri destination = ValidateEndpoint(endpoint);
            string path = ApiResourcePath(destination);
            // Token-bearing webhooks and interaction callbacks must never receive Bot auth.
            bool authenticate = useBotToken && !path.StartsWith("webhooks/", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("interactions/", StringComparison.OrdinalIgnoreCase)
                && !path.Equals("webhooks", StringComparison.OrdinalIgnoreCase)
                && !path.Equals("interactions", StringComparison.OrdinalIgnoreCase);
            if (authenticate && string.IsNullOrWhiteSpace(_botToken))
                throw new DiscordHttpException(0, "Discord bot authentication is not configured.");
            byte[]? serialized = Serialize(body);
            string route = method.Method + " " + destination.AbsolutePath;
            string partition = ResourcePartition(path);
            int retries = 0;
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(DiscordHttp));

            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token))
            {
                CancellationToken cancellation = linked.Token;
                while (true)
                {
                    TimeSpan delay = TimeSpan.Zero;
                    await _lane.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        DateTime until = authenticate ? _botGlobalUntil : _anonymousGlobalUntil;
                        until = Later(until, _overflowUntil);
                        string key = _routeBuckets.TryGetValue(route, out string? bucket) ? bucket : route;
                        if (_cooldowns.TryGetValue(key, out DateTime routeUntil)) until = Later(until, routeUntil);
                        if (until > DateTime.UtcNow)
                        {
                            delay = until - DateTime.UtcNow;
                        }
                        else
                        {
                            using (CancellationTokenSource requestStop =
                                   CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                            {
                                requestStop.CancelAfter(RequestTimeout);
                                try
                                {
                                    using (HttpRequestMessage request = new HttpRequestMessage(method, destination))
                                    {
                                        if (authenticate)
                                            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _botToken);
                                        if (serialized != null)
                                        {
                                            request.Content = new ByteArrayContent(serialized);
                                            request.Content.Headers.ContentType =
                                                new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                                        }

                                        using (HttpResponseMessage response = await AwaitCancelable(_client.SendAsync(
                                                   request, HttpCompletionOption.ResponseHeadersRead,
                                                   requestStop.Token), requestStop.Token,
                                                   abandoned => abandoned.Dispose()).ConfigureAwait(false))
                                        {
                                            int status = (int)response.StatusCode;
                                            string responseText = await ReadBoundedAsync(
                                                response.Content, requestStop.Token).ConfigureAwait(false);
                                            JToken? responseJson = ParseJson(responseText, response.IsSuccessStatusCode);
                                            key = RememberBucket(route, partition, response);
                                            if (Header(response, "X-RateLimit-Remaining") == "0")
                                            {
                                                double reset = Seconds(Header(response, "X-RateLimit-Reset-After"));
                                                if (reset > 0) RememberCooldown(key, reset);
                                            }

                                            if (status == 429)
                                            {
                                                double seconds = RetryAfterSeconds(response, responseJson);
                                                DateTime next = Deadline(seconds);
                                                bool global = Header(response, "X-RateLimit-Global") == "true"
                                                    || Header(response, "X-RateLimit-Scope") == "global"
                                                    || (responseJson as JObject)?["global"]?.Type == JTokenType.Boolean
                                                    && (bool)((JObject)responseJson!)["global"]!;
                                                if (global)
                                                {
                                                    if (authenticate) _botGlobalUntil = Later(_botGlobalUntil, next);
                                                    else _anonymousGlobalUntil = Later(_anonymousGlobalUntil, next);
                                                }
                                                RememberCooldown(key, seconds);
                                                // A received 429 is an explicit refusal, safe to retry even
                                                // for mutation callers that forbid ambiguous replay.
                                                if (retries++ >= MaximumRetries)
                                                    throw Failure(status);
                                                SafeLog("Discord rate limit encountered; delivery deferred.");
                                                continue;
                                            }

                                            if (response.IsSuccessStatusCode) return responseJson;
                                            if ((status == 408 || status >= 500) && retry && retries++ < MaximumRetries)
                                                delay = Backoff(retries);
                                            else throw Failure(status);
                                        }
                                    }
                                }
                                catch (DiscordHttpException) { throw; }
                                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                                {
                                    throw new OperationCanceledException(cancellation);
                                }
                                catch (Exception)
                                {
                                    // Network errors, timeouts, malformed headers, and Mono IO failures
                                    // may contain a webhook token in their original exception message.
                                    if (cancellation.IsCancellationRequested)
                                        throw new OperationCanceledException(cancellation);
                                    if (!retry || retries++ >= MaximumRetries)
                                        throw new DiscordHttpException(0, "Discord HTTP request failed or timed out.");
                                    delay = Backoff(retries);
                                }
                            }
                        }
                    }
                    finally { _lane.Release(); }

                    if (delay > TimeSpan.Zero)
                    {
                        // Long server-directed cooldowns are honored, never clamped to an
                        // earlier retry. The cancellable chunks also support very long limits.
                        await Task.Delay(delay > TimeSpan.FromMinutes(1)
                            ? TimeSpan.FromMinutes(1) : delay, cancellation).ConfigureAwait(false);
                    }
                }
            }
        }

        internal static Uri ValidateEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2048
                || endpoint.IndexOf('\\') >= 0
                || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, "discord.com", StringComparison.OrdinalIgnoreCase)
                || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || !uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)
                || !ValidRawResource(endpoint)
                || !ValidResourceName(ApiResourcePath(uri)))
                throw new DiscordHttpException(0, "Discord endpoint must be an absolute HTTPS discord.com API URL.");
            return uri;
        }

        private static bool ValidRawResource(string endpoint)
        {
            int pathStart = endpoint.IndexOf('/', endpoint.IndexOf("://", StringComparison.Ordinal) + 3);
            if (pathStart < 0) return false;
            string path = endpoint.Substring(pathStart);
            int query = path.IndexOf('?');
            if (query >= 0) path = path.Substring(0, query);
            return path.StartsWith("/api/", StringComparison.Ordinal)
                && ValidResourceName(StripApiVersion(path.Substring(5)));
        }

        private static bool ValidResourceName(string path)
        {
            // An opaque interaction token can legitimately be percent-escaped.
            // Only the resource segment must be plain ASCII to prevent an encoded
            // separator from obscuring whether the endpoint is a webhook/callback.
            int end = path.IndexOf('/');
            if (end < 0) end = path.Length;
            if (end == 0) return false;
            for (int index = 0; index < end; index++)
                if (path[index] < 'a' || path[index] > 'z') return false;
            return true;
        }

        internal static string ApiResourcePath(Uri uri)
        {
            return StripApiVersion(uri.AbsolutePath.Substring("/api/".Length));
        }

        private static string StripApiVersion(string path)
        {
            int slash = path.IndexOf('/');
            if (slash > 1 && path[0] == 'v'
                && int.TryParse(path.Substring(1, slash - 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int _))
                path = path.Substring(slash + 1);
            return path;
        }

        private static byte[]? Serialize(JToken? body)
        {
            if (body == null) return null;
            try
            {
                using (JsonReader reader = body.CreateReader())
                {
                    reader.MaxDepth = MaximumJsonDepth;
                    int units = 0;
                    while (reader.Read())
                    {
                        if (reader.Depth > MaximumJsonDepth)
                            throw new DiscordHttpException(0, "Discord request JSON is excessively nested.");
                        int next = reader.Value is string text ? text.Length : 8;
                        if (next > MaximumRequestBytes - units)
                            throw new DiscordHttpException(0, "Discord request exceeded the permitted size.");
                        units += next;
                    }
                }
                byte[] bytes = Utf8.GetBytes(body.ToString(Formatting.None));
                if (bytes.Length > MaximumRequestBytes)
                    throw new DiscordHttpException(0, "Discord request exceeded the permitted size.");
                return bytes;
            }
            catch (DiscordHttpException) { throw; }
            catch (Exception) { throw new DiscordHttpException(0, "Discord request JSON is invalid."); }
        }

        private static async Task<string> ReadBoundedAsync(HttpContent? content, CancellationToken ct)
        {
            if (content == null) return string.Empty;
            if (content.Headers.ContentLength > MaximumResponseBytes)
                throw new DiscordHttpException(0, "Discord response exceeded the permitted size.");
            using (Stream stream = await AwaitCancelable(content.ReadAsStreamAsync(), ct,
                       abandoned => abandoned.Dispose()).ConfigureAwait(false))
            using (CancellationTokenRegistration abort = ct.Register(() =>
                   {
                       try { stream.Dispose(); } catch (Exception) { }
                   }))
            using (MemoryStream result = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = await AwaitCancelable(stream.ReadAsync(buffer, 0, buffer.Length, ct), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    if (result.Length + read > MaximumResponseBytes)
                        throw new DiscordHttpException(0, "Discord response exceeded the permitted size.");
                    result.Write(buffer, 0, read);
                }
                return Utf8.GetString(result.ToArray());
            }
        }

        private static async Task<T> AwaitCancelable<T>(Task<T> task, CancellationToken ct, Action<T>? abandon = null)
        {
            if (task.IsCompleted) return await task.ConfigureAwait(false);
            TaskCompletionSource<bool> cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (CancellationTokenRegistration registration = ct.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                {
                    // Some Mono handlers/streams ignore cancellation. Do not let them hold
                    // shutdown forever; dispose a response arriving after its request expired.
                    _ = task.ContinueWith(completed =>
                    {
                        if (completed.IsFaulted) { _ = completed.Exception; return; }
                        if (completed.Status == TaskStatus.RanToCompletion && abandon != null)
                            try { abandon(completed.Result); } catch (Exception) { }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw new OperationCanceledException(ct);
                }
                return await task.ConfigureAwait(false);
            }
        }

        private static JToken? ParseJson(string text, bool required)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                using (StringReader input = new StringReader(text))
                using (JsonTextReader reader = new JsonTextReader(input)
                       { MaxDepth = MaximumJsonDepth, DateParseHandling = DateParseHandling.None })
                {
                    JToken value = JToken.ReadFrom(reader);
                    if (reader.Read()) throw new JsonReaderException();
                    return value;
                }
            }
            catch (JsonException)
            {
                if (!required) return null;
                throw new DiscordHttpException(0, "Discord returned invalid or excessively nested JSON.");
            }
        }

        private string RememberBucket(string route, string partition, HttpResponseMessage response)
        {
            string bucket = Header(response, "X-RateLimit-Bucket");
            if (bucket.Length > 0 && bucket.Length <= 128)
            {
                string key = "bucket:" + bucket + ":" + partition;
                if (_routeBuckets.ContainsKey(route) || _routeBuckets.Count < MaximumRateEntries)
                {
                    _routeBuckets[route] = key;
                    return key;
                }
            }
            return _routeBuckets.TryGetValue(route, out string? known) ? known : route;
        }

        private void RememberCooldown(string key, double seconds)
        {
            DateTime until = Deadline(seconds);
            if (!_cooldowns.ContainsKey(key) && _cooldowns.Count >= MaximumRateEntries)
            {
                // Bounded memory, without evicting a live limit and retrying too early.
                foreach (DateTime existing in _cooldowns.Values) _overflowUntil = Later(_overflowUntil, existing);
                _overflowUntil = Later(_overflowUntil, until);
                _cooldowns.Clear();
                _routeBuckets.Clear();
            }
            _cooldowns[key] = _cooldowns.TryGetValue(key, out DateTime current) ? Later(current, until) : until;
        }

        private static string ResourcePartition(string path)
        {
            string[] parts = path.Split('/');
            if (parts.Length < 2) return string.Empty;
            if (parts[0] == "channels" || parts[0] == "guilds") return parts[0] + "/" + parts[1];
            if (parts[0] == "webhooks") return parts[0] + "/" + parts[1] + "/" + (parts.Length > 2 ? parts[2] : "");
            return string.Empty;
        }

        private static double RetryAfterSeconds(HttpResponseMessage response, JToken? json)
        {
            double seconds = Seconds(Header(response, "Retry-After"));
            if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
                seconds = Math.Max(seconds, (date - DateTimeOffset.UtcNow).TotalSeconds);
            if (json is JObject value)
                seconds = Math.Max(seconds, Seconds(value["retry_after"]?.ToString() ?? string.Empty));
            seconds = Math.Max(seconds, Seconds(Header(response, "X-RateLimit-Reset-After")));
            return seconds > 0 ? seconds : 1;
        }

        private static string Header(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out IEnumerable<string>? values))
                foreach (string value in values) return value;
            return string.Empty;
        }

        private static double Seconds(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                && !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0
                ? seconds : 0;
        }

        private static DateTime Deadline(double seconds)
        {
            DateTime now = DateTime.UtcNow;
            // Never truncate a server-directed cooldown to an earlier retry.
            return seconds >= (DateTime.MaxValue - now).TotalSeconds - 1
                ? DateTime.MaxValue : now.AddSeconds(seconds + 0.025);
        }

        private static DateTime Later(DateTime left, DateTime right) => left > right ? left : right;
        private static TimeSpan Backoff(int retry) => TimeSpan.FromMilliseconds(Math.Min(8000, 250 * Math.Pow(2, retry - 1)));
        private static DiscordHttpException Failure(int status) => new DiscordHttpException(status,
            "Discord HTTP request was rejected (status " + status.ToString(CultureInfo.InvariantCulture) + ").");

        private void SafeLog(string message)
        {
            try { _log(message); } catch (Exception) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _client.Dispose();
            // Waiters may still be unwinding; disposing the semaphore here races their Release.
        }
    }
}
