using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ServerManager.Discord;

/// <summary>
/// A single-shard, JSON-only Discord v10 Gateway connection. No game/Unity work
/// belongs here: the dispatch callback is serialized on a background worker.
/// </summary>
internal sealed class DiscordGateway : IDisposable
{
    internal const int MaximumMessageBytes = 2 * 1024 * 1024;
    private const int MaximumDispatchCount = 128;
    private const int MaximumDispatchBytes = 4 * 1024 * 1024;
    private const int MaximumFragments = 4096;
    private const string GatewayBotEndpoint = "https://discord.com/api/v10/gateway/bot";
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly string _botToken;
    private readonly int _intents;
    private readonly Func<string, JObject, Task> _onDispatch;
    private readonly Action<string> _log;
    private readonly DiscordHttp _http;
    private readonly object _lifecycleGate = new object();
    private readonly object _queueGate = new object();
    private readonly Queue<DispatchItem> _dispatchQueue = new Queue<DispatchItem>();
    private readonly SemaphoreSlim _dispatchAvailable = new SemaphoreSlim(0);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new Random();
    private CancellationTokenSource? _lifetime = new CancellationTokenSource();
    private ClientWebSocket? _activeSocket;
    private bool _started;
    private bool _disposed;
    private int _queuedBytes;
    private string? _sessionId;
    private Uri? _resumeUrl;
    private long _sequence = -1;
    private long _establishedAt = -1;
    private int _remainingIdentifies = -1;
    private long _limitResetAt;
    private long _identifyNotBefore;

    internal DiscordGateway(string botToken, Func<string, JObject, Task> onDispatch,
        Action<string> log, DiscordHttp http, bool receiveMessages = false)
    {
        if (string.IsNullOrWhiteSpace(botToken) || botToken.Length > 1024)
            throw new ArgumentException("A valid Discord bot token is required.", nameof(botToken));
        foreach (char value in botToken)
        {
            if (value <= 32 || value >= 127)
                throw new ArgumentException("The Discord bot token contains unsupported characters.", nameof(botToken));
        }

        _botToken = botToken;
        // Slash interactions need only GUILDS. Ordinary guild chat additionally requires
        // GUILD_MESSAGES and the privileged MESSAGE_CONTENT intent, never DMs or members.
        _intents = 1 | (receiveMessages ? (1 << 9) | (1 << 15) : 0);
        _onDispatch = onDispatch ?? throw new ArgumentNullException(nameof(onDispatch));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>Runs once. Calling or disposing this object never waits on a game thread.</summary>
    internal Task RunAsync(CancellationToken ct)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiscordGateway));
            if (_started) throw new InvalidOperationException("The Discord Gateway is already started.");
            _started = true;
        }

        return Task.Run(() => RunCoreAsync(ct));
    }

    // Fixed labels only: this observational snapshot never leaks a token,
    // session identifier, endpoint or raw exception. The caller checks worker liveness.
    internal string ConnectionStatus
    {
        get
        {
            lock (_lifecycleGate)
            {
                if (_disposed) return "stopped";
                if (!_started) return "pending";
                if (_activeSocket?.State != WebSocketState.Open) return "connecting_or_reconnecting";
                return Interlocked.Read(ref _establishedAt) >= 0 ? "connected" : "authenticating";
            }
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        CancellationToken lifetimeToken;
        lock (_lifecycleGate) lifetimeToken = _lifetime!.Token;
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken))
        {
            Task dispatcher = DispatchLoopAsync(stop.Token);
            Task connector = ConnectionLoopAsync(stop.Token);
            try
            {
                Task completed = await Task.WhenAny(connector, dispatcher).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                if (completed == dispatcher && !stop.IsCancellationRequested)
                    SafeLog("Discord event worker stopped; Gateway stopped.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception)
            {
                // Transport exceptions can contain URLs; callback exceptions can contain secrets.
                SafeLog("Discord Gateway stopped after an internal error.");
            }
            finally
            {
                stop.Cancel();
                AbortActiveSocket();
                await ObserveAsync(connector).ConfigureAwait(false);
                await ObserveAsync(dispatcher).ConfigureAwait(false);
                lock (_queueGate)
                {
                    _dispatchQueue.Clear();
                    _queuedBytes = 0;
                }
                _dispatchAvailable.Dispose();
                lock (_lifecycleGate)
                {
                    _lifetime?.Dispose();
                    _lifetime = null;
                }
            }
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            GatewayFailure? failure = null;
            try
            {
                bool resume = CanResume;
                Uri url = resume ? _resumeUrl! : await PrepareIdentifyAsync(ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _establishedAt, -1);
                await RunConnectionAsync(url, resume, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (GatewayFailure error)
            {
                failure = error;
                if (error.ClearSession) ClearSession();
                SafeLog(error.Message); // All GatewayFailure messages are fixed local strings.
                if (error.Fatal) return;
            }
            catch (DiscordHttpException error) when (error.StatusCode == 401 || error.StatusCode == 403)
            {
                SafeLog("Discord Gateway authentication was rejected; check the bot token and application access. Reconnect stopped.");
                return;
            }
            catch (Exception)
            {
                SafeLog("Discord Gateway transport failed; reconnecting with backoff.");
            }

            if (ct.IsCancellationRequested) return;
            if (_establishedAt >= 0 && _clock.ElapsedMilliseconds - _establishedAt >= 60000)
                failures = 0;
            Interlocked.Exchange(ref _establishedAt, -1);
            int delay = GetReconnectDelay(failures, failure?.MinimumDelayMs ?? 0,
                failure?.ReconnectImmediately == true);
            failures = Math.Min(failures + 1, 8);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private bool CanResume => _sessionId != null && _resumeUrl != null && Interlocked.Read(ref _sequence) >= 0;

    private async Task<Uri> PrepareIdentifyAsync(CancellationToken ct)
    {
        // This client has one shard, so spacing every Identify by >5 seconds obeys
        // all positive max_concurrency values. REST refresh also accounts for starts
        // outside this connection; local accounting cannot be replenished by stale data.
        while (true)
        {
            long waitUntil = _identifyNotBefore;
            if (_remainingIdentifies == 0) waitUntil = Math.Max(waitUntil, _limitResetAt);
            await DelayUntilAsync(waitUntil, ct).ConfigureAwait(false);

            JObject? result = await _http.SendAsync(HttpMethod.Get, GatewayBotEndpoint, null, true, ct)
                .ConfigureAwait(false) as JObject;
            if (result == null || result["session_start_limit"] is not JObject limits)
                throw new GatewayFailure("Discord returned invalid Gateway session metadata.");
            Uri gatewayUrl = ValidateGatewayUrl(ReadString(result["url"], 2048));
            long total = ReadInteger(limits["total"], 1, int.MaxValue);
            int remaining = (int)ReadInteger(limits["remaining"], 0, total);
            long resetAfter = ReadInteger(limits["reset_after"], 0, 7L * 24 * 60 * 60 * 1000);
            ReadInteger(limits["max_concurrency"], 1, int.MaxValue);
            long now = _clock.ElapsedMilliseconds;
            if (_remainingIdentifies >= 0 && now < _limitResetAt)
            {
                _remainingIdentifies = Math.Min(_remainingIdentifies, remaining);
                // Keep the later reset to avoid spending an Identify before Discord's window ends.
                _limitResetAt = Math.Max(_limitResetAt, now + resetAfter + 1000);
            }
            else
            {
                _remainingIdentifies = remaining;
                _limitResetAt = now + resetAfter + 1000;
            }

            if (_remainingIdentifies > 0) return gatewayUrl;
            SafeLog("Discord session start allowance is exhausted; waiting for its reset before identifying.");
        }
    }

    private async Task DelayUntilAsync(long deadline, CancellationToken ct)
    {
        // Task.Delay(int) cannot represent arbitrary server-provided reset periods.
        while (deadline > _clock.ElapsedMilliseconds)
        {
            int delay = (int)Math.Min(60000, deadline - _clock.ElapsedMilliseconds);
            if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    private async Task RunConnectionAsync(Uri url, bool resume, CancellationToken ct)
    {
        using (var socket = new ClientWebSocket())
        using (var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (var sendGate = new SemaphoreSlim(1, 1))
        {
            // Gateway application-level heartbeats are authoritative. No compression is requested.
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            lock (_lifecycleGate) _activeSocket = socket;
            var state = new ConnectionState(socket, sendGate);
            Task heartbeat = Task.CompletedTask;
            Task receiver = Task.CompletedTask;
            try
            {
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(connectionStop.Token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await socket.ConnectAsync(url, connectTimeout.Token).ConfigureAwait(false);
                }

                GatewayMessage hello;
                using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(connectionStop.Token))
                {
                    helloTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    hello = await ReceiveMessageAsync(socket, helloTimeout.Token).ConfigureAwait(false);
                }
                int opcode = (int)ReadInteger(hello.Payload["op"], 0, int.MaxValue);
                ThrowIfReconnect(hello.Payload, opcode);
                if (opcode != 10 || hello.Payload["d"] is not JObject helloData)
                    throw new GatewayFailure("Discord Gateway did not provide a valid Hello event.");
                int interval = (int)ReadInteger(helloData["heartbeat_interval"], 1000, 300000);

                heartbeat = HeartbeatLoopAsync(state, interval, connectionStop.Token);
                receiver = ReceiveEventsAsync(state, connectionStop.Token);
                JObject authentication;
                if (resume)
                {
                    authentication = new JObject
                    {
                        ["op"] = 6,
                        ["d"] = new JObject
                        {
                            ["token"] = _botToken,
                            ["session_id"] = _sessionId,
                            ["seq"] = Interlocked.Read(ref _sequence)
                        }
                    };
                }
                else
                {
                    authentication = CreateIdentifyPayload();
                }
                await SendAsync(state, authentication, connectionStop.Token, identify: !resume).ConfigureAwait(false);
                Task finished = await Task.WhenAny(receiver, heartbeat).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
            }
            finally
            {
                connectionStop.Cancel();
                // Abort preserves resumability; close 1000/1001 would invalidate the session.
                TryAbort(socket);
                await ObserveAsync(heartbeat).ConfigureAwait(false);
                await ObserveAsync(receiver).ConfigureAwait(false);
                lock (_lifecycleGate)
                {
                    if (ReferenceEquals(_activeSocket, socket)) _activeSocket = null;
                }
            }
        }
    }

    private JObject CreateIdentifyPayload() => new JObject
    {
        ["op"] = 2,
        ["d"] = new JObject
        {
            ["token"] = _botToken,
            ["intents"] = _intents,
            ["compress"] = false,
            ["large_threshold"] = 50,
            ["properties"] = new JObject
            {
                ["os"] = Environment.OSVersion.Platform == PlatformID.Win32NT ? "windows" : "linux",
                ["browser"] = "ServerManager",
                ["device"] = "ServerManager"
            }
        }
    };

    private async Task ReceiveEventsAsync(ConnectionState state, CancellationToken ct)
    {
        while (true)
        {
            GatewayMessage message = await ReceiveMessageAsync(state.Socket, ct).ConfigureAwait(false);
            JObject payload = message.Payload;
            int opcode = (int)ReadInteger(payload["op"], 0, int.MaxValue);
            ThrowIfReconnect(payload, opcode);
            switch (opcode)
            {
                case 0:
                    string eventName = ReadString(payload["t"], 128);
                    long sequence = ReadInteger(payload["s"], 0, long.MaxValue);
                    if (payload["d"] is not JObject data)
                        throw new GatewayFailure("Discord Gateway sent invalid event data.");
                    string? sessionId = null;
                    Uri? resumeUrl = null;
                    if (eventName == "READY")
                    {
                        sessionId = ReadString(data["session_id"], 512);
                        resumeUrl = ValidateGatewayUrl(ReadString(data["resume_gateway_url"], 2048));
                    }
                    if (!TryEnqueue(new DispatchItem(eventName, data, message.Bytes)))
                        throw new GatewayFailure("Discord event queue is full; reconnecting to replay the unqueued event.");
                    // Commit only after ownership passes to the persistent worker queue.
                    // On queue pressure, Resume asks Discord to replay the rejected event.
                    if (sessionId != null)
                    {
                        _sessionId = sessionId;
                        _resumeUrl = resumeUrl;
                    }
                    Interlocked.Exchange(ref _sequence, sequence);
                    if (eventName == "READY" || eventName == "RESUMED")
                    {
                        Interlocked.Exchange(ref _establishedAt, _clock.ElapsedMilliseconds);
                        SafeLog(eventName == "READY" ? "Discord Gateway connected." : "Discord Gateway session resumed.");
                    }
                    break;
                case 1:
                    // Server-requested heartbeats do not change the scheduled cadence.
                    await SendHeartbeatAsync(state, ct).ConfigureAwait(false);
                    break;
                case 11:
                    lock (state.HeartbeatGate) state.PendingHeartbeatSince = -1;
                    break;
                case 10:
                    throw new GatewayFailure("Discord Gateway sent an unexpected duplicate Hello event.");
                default:
                    // Unknown control opcodes are forward-compatible and do not mutate sequence state.
                    break;
            }
        }
    }

    private static void ThrowIfReconnect(JObject payload, int opcode)
    {
        if (opcode == 7)
            throw new GatewayFailure("Discord requested a Gateway reconnect.", reconnectImmediately: true);
        if (opcode == 9)
        {
            if (payload["d"]?.Type != JTokenType.Boolean)
                throw new GatewayFailure("Discord Gateway sent invalid session data.", clearSession: true);
            bool resumable = payload["d"]!.Value<bool>();
            throw new GatewayFailure("Discord invalidated the Gateway session; reconnecting.",
                clearSession: !resumable, minimumDelayMs: 1000);
        }
    }

    private async Task HeartbeatLoopAsync(ConnectionState state, int interval, CancellationToken ct)
    {
        await Task.Delay(NextRandom(interval), ct).ConfigureAwait(false);
        while (true)
        {
            lock (state.HeartbeatGate)
            {
                // A recent server-requested heartbeat must get its own full ACK window.
                if (state.PendingHeartbeatSince >= 0 &&
                    _clock.ElapsedMilliseconds - state.PendingHeartbeatSince >= interval)
                    throw new GatewayFailure("Discord heartbeat acknowledgement timed out; reconnecting.");
            }
            await SendHeartbeatAsync(state, ct).ConfigureAwait(false);
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private Task SendHeartbeatAsync(ConnectionState state, CancellationToken ct)
    {
        long sequence = Interlocked.Read(ref _sequence);
        return SendAsync(state, new JObject
        {
            ["op"] = 1,
            ["d"] = sequence < 0 ? JValue.CreateNull() : new JValue(sequence)
        }, ct, heartbeat: true);
    }

    private async Task SendAsync(ConnectionState state, JObject payload, CancellationToken ct,
        bool heartbeat = false, bool identify = false)
    {
        byte[] bytes = StrictUtf8.GetBytes(payload.ToString(Formatting.None));
        if (bytes.Length > 4096)
            throw new GatewayFailure("Discord Gateway outbound payload exceeded its protocol limit.", fatal: true);
        using (var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            sendTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await state.SendGate.WaitAsync(sendTimeout.Token).ConfigureAwait(false);
            try
            {
                long now = _clock.ElapsedMilliseconds;
                while (state.SentAt.Count > 0 && now - state.SentAt.Peek() >= 60000)
                    state.SentAt.Dequeue();
                // Only authentication and heartbeats are sent. Keep a margin under
                // Discord's 120 events / 60 seconds, even if heartbeats are requested rapidly.
                if (state.SentAt.Count >= 110)
                    throw new GatewayFailure("Discord Gateway send limit approached; reconnect delayed.", minimumDelayMs: 60000);
                if (identify)
                {
                    if (_remainingIdentifies <= 0)
                        throw new GatewayFailure("Discord Gateway has no available session starts.");
                    --_remainingIdentifies;
                    _identifyNotBefore = now + 5500;
                }
                if (heartbeat)
                {
                    lock (state.HeartbeatGate)
                    {
                        if (state.PendingHeartbeatSince < 0) state.PendingHeartbeatSince = now;
                    }
                }
                state.SentAt.Enqueue(now);
                await state.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                    sendTimeout.Token).ConfigureAwait(false);
            }
            finally { state.SendGate.Release(); }
        }
    }

    internal static async Task<GatewayMessage> ReceiveMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using (var message = new MemoryStream())
        {
            int fragments = 0;
            while (true)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct)
                    .ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    throw FailureForCloseCode(received.CloseStatus.HasValue ? (int)received.CloseStatus.Value : 0);
                if (received.MessageType != WebSocketMessageType.Text)
                    throw new GatewayFailure("Discord Gateway sent unsupported non-JSON data.");
                if (++fragments > MaximumFragments || received.Count > MaximumMessageBytes - message.Length)
                    throw new GatewayFailure("Discord Gateway inbound payload exceeded the safety limit.");
                message.Write(buffer, 0, received.Count);
                if (received.EndOfMessage) break;
            }

            try
            {
                string json = StrictUtf8.GetString(message.GetBuffer(), 0, (int)message.Length);
                using (var text = new StringReader(json))
                using (var reader = new JsonTextReader(text)
                {
                    MaxDepth = 64,
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal
                })
                {
                    JObject payload = JObject.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        CommentHandling = CommentHandling.Ignore
                    });
                    if (reader.Read()) throw new JsonReaderException();
                    return new GatewayMessage(payload, (int)message.Length);
                }
            }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException ||
                                          error is OverflowException || error is FormatException)
            {
                throw new GatewayFailure("Discord Gateway sent malformed JSON data.");
            }
        }
    }

    internal static Uri ValidateGatewayUrl(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new GatewayFailure("Discord returned an unsupported Gateway address.", fatal: true);
        string host = uri.DnsSafeHost.ToLowerInvariant();
        bool allowed = host == "gateway.discord.gg";
        if (!allowed && host.StartsWith("gateway-", StringComparison.Ordinal) &&
            host.EndsWith(".discord.gg", StringComparison.Ordinal))
        {
            string region = host.Substring(8, host.Length - 8 - ".discord.gg".Length);
            allowed = region.Length > 0;
            foreach (char character in region)
            {
                if (!(character >= 'a' && character <= 'z') &&
                    !(character >= '0' && character <= '9') && character != '-') allowed = false;
            }
        }
        if (!allowed)
            throw new GatewayFailure("Discord returned a Gateway address outside the supported Discord hosts.", fatal: true);
        // Ignore any supplied query and install only the protocol parameters we support.
        return new UriBuilder("wss", host, -1, "/", "?v=10&encoding=json").Uri;
    }

    internal static GatewayFailure FailureForCloseCode(int code)
    {
        bool fatal = code == 4004 || (code >= 4010 && code <= 4014);
        bool clearSession = code == 1000 || code == 1001 || code == 4003 || code == 4007 || code == 4009;
        string message = code == 4014
            ? "Discord Gateway rejected a privileged intent. Discord-to-game chat requires Message Content Intent enabled in the Discord Developer Portal (Bot > Privileged Gateway Intents), with approval if required. Reconnect stopped."
            : fatal
                ? "Discord Gateway closed with a non-retryable authentication, shard, version, or intent error. Check the bot configuration."
                : "Discord Gateway connection closed; reconnecting.";
        // Only the numeric close code is safe to append; never use CloseStatusDescription.
        return new GatewayFailure(message + " Code: " + code + ".", fatal, clearSession,
            minimumDelayMs: code == 4008 ? 60000 : 0);
    }

    private bool TryEnqueue(DispatchItem item)
    {
        lock (_queueGate)
        {
            if (_dispatchQueue.Count >= MaximumDispatchCount || item.Bytes > MaximumDispatchBytes - _queuedBytes)
                return false;
            _dispatchQueue.Enqueue(item);
            _queuedBytes += item.Bytes;
            _dispatchAvailable.Release();
            return true;
        }
    }

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        Task stopped = Task.Delay(Timeout.Infinite, ct);
        while (!ct.IsCancellationRequested)
        {
            await _dispatchAvailable.WaitAsync(ct).ConfigureAwait(false);
            DispatchItem item;
            lock (_queueGate)
            {
                item = _dispatchQueue.Dequeue();
                _queuedBytes -= item.Bytes;
            }
            ct.ThrowIfCancellationRequested();
            // One callback task at a time, even when the callback does synchronous work.
            // Shutdown can abandon at most one non-cooperative callback, never a stream of tasks.
            Task dispatch = Task.Run(() => _onDispatch(item.Name, item.Data), ct);
            if (await Task.WhenAny(dispatch, stopped).ConfigureAwait(false) != dispatch)
            {
                ObserveFaultLater(dispatch);
                return;
            }
            try { await dispatch.ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { SafeLog("Discord event handler failed; event details were omitted for safety."); }
        }
    }

    private static string ReadString(JToken? token, int maximumLength)
    {
        if (token?.Type != JTokenType.String)
            throw new GatewayFailure("Discord Gateway sent an invalid string field.");
        string? value = token.Value<string>();
        if (string.IsNullOrEmpty(value) || value!.Length > maximumLength)
            throw new GatewayFailure("Discord Gateway sent an invalid string field.");
        return value;
    }

    private static long ReadInteger(JToken? token, long minimum, long maximum)
    {
        if (token?.Type != JTokenType.Integer)
            throw new GatewayFailure("Discord Gateway sent an invalid numeric field.");
        long value;
        try { value = token.Value<long>(); }
        catch (Exception error) when (error is OverflowException || error is FormatException || error is InvalidCastException)
        { throw new GatewayFailure("Discord Gateway sent an invalid numeric field."); }
        if (value < minimum || value > maximum)
            throw new GatewayFailure("Discord Gateway sent an out-of-range numeric field.");
        return value;
    }

    private int GetReconnectDelay(int failures, int minimumDelay, bool immediately)
    {
        if (immediately && failures == 0) return NextRandom(251);
        int ceiling = Math.Min(60000, 2000 * (1 << Math.Min(failures, 5)));
        int jittered = ceiling / 2 + NextRandom(ceiling / 2 + 1);
        if (minimumDelay == 1000) jittered = Math.Max(jittered, 1000 + NextRandom(4001));
        return Math.Max(minimumDelay, jittered);
    }

    private int NextRandom(int maximum)
    {
        lock (_random) return _random.Next(maximum);
    }

    private void ClearSession()
    {
        _sessionId = null;
        _resumeUrl = null;
        Interlocked.Exchange(ref _sequence, -1);
    }

    private void SafeLog(string message)
    {
        try { _log(message); }
        catch (Exception) { /* A logger must not break the Gateway or expose another exception. */ }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static void ObserveFaultLater(Task task)
    {
        _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void AbortActiveSocket()
    {
        ClientWebSocket? socket;
        lock (_lifecycleGate) socket = _activeSocket;
        if (socket != null) TryAbort(socket);
    }

    private static void TryAbort(WebSocket socket)
    {
        try { socket.Abort(); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        CancellationTokenSource? lifetime;
        bool started;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            started = _started;
            lifetime = _lifetime;
        }
        try { lifetime?.Cancel(); }
        catch (ObjectDisposedException) { }
        AbortActiveSocket();
        if (!started)
        {
            lifetime?.Dispose();
            _dispatchAvailable.Dispose();
            lock (_lifecycleGate) _lifetime = null;
        }
    }

    internal sealed class GatewayMessage
    {
        internal GatewayMessage(JObject payload, int bytes) { Payload = payload; Bytes = bytes; }
        internal JObject Payload { get; }
        internal int Bytes { get; }
    }

    internal sealed class GatewayFailure : Exception
    {
        internal GatewayFailure(string message, bool fatal = false, bool clearSession = false,
            bool reconnectImmediately = false, int minimumDelayMs = 0) : base(message)
        {
            Fatal = fatal;
            ClearSession = clearSession;
            ReconnectImmediately = reconnectImmediately;
            MinimumDelayMs = minimumDelayMs;
        }
        internal bool Fatal { get; }
        internal bool ClearSession { get; }
        internal bool ReconnectImmediately { get; }
        internal int MinimumDelayMs { get; }
    }

    private sealed class DispatchItem
    {
        internal DispatchItem(string name, JObject data, int bytes) { Name = name; Data = data; Bytes = bytes; }
        internal string Name { get; }
        internal JObject Data { get; }
        internal int Bytes { get; }
    }

    private sealed class ConnectionState
    {
        internal ConnectionState(WebSocket socket, SemaphoreSlim sendGate) { Socket = socket; SendGate = sendGate; }
        internal WebSocket Socket { get; }
        internal SemaphoreSlim SendGate { get; }
        internal object HeartbeatGate { get; } = new object();
        internal long PendingHeartbeatSince = -1;
        internal Queue<long> SentAt { get; } = new Queue<long>();
    }
}
