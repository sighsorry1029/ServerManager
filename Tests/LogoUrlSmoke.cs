using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// Uses the actual built plugin through reflection and an injected handler.
// No Unity objects are created and no request can reach a real HTTP transport.
internal static class LogoUrlSmoke
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const string Secret = "SM_LOGO_DIAGNOSTIC_SECRET";
    private const string OtherSecret = "SM_LOGO_OTHER_PRIVATE_DETAIL";
    private static readonly Uri Logo = new Uri("https://assets.example.test/logo.png?signature=test-private-value");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] OtherPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j6QAAAABJRU5ErkJggg==");
    private static Type _branding = null!;
    private static int _checks;
    private static int _unobservedPrivateFaults;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3) throw new ArgumentException("Expected plugin path, game directory, and owned temporary data directory.");
            string binaryRoot = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
            string[] roots = { binaryRoot, Path.Combine(args[1], "valheim_Data", "Managed"), Path.Combine(args[1], "BepInEx", "core") };
            AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
            {
                string name = new AssemblyName(request.Name).Name + ".dll";
                foreach (string root in roots)
                {
                    string candidate = Path.Combine(root, name);
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                }
                return null;
            };
            TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            {
                if (eventArgs.Exception.ToString().Contains(Secret)) Interlocked.Increment(ref _unobservedPrivateFaults);
                eventArgs.SetObserved();
            };
            _branding = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("ServerManager.ClientMenuBranding", true)!;
            Run(args[2]).GetAwaiter().GetResult();
            Console.WriteLine("Logo URL smoke passed (" + _checks + " assertions; fake HTTP only, actual .NET48 plugin, isolated cache).");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task Run(string dataRoot)
    {
        ValidateUrls();
        await ValidateDownload().ConfigureAwait(false);
        await ValidateRedirects().ConfigureAwait(false);
        await ValidateBounds().ConfigureAwait(false);
        await ValidateCancellation().ConfigureAwait(false);
        await ValidateLateCompletion().ConfigureAwait(false);
        await ValidateDiagnostics().ConfigureAwait(false);
        await ValidateCache(dataRoot).ConfigureAwait(false);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(25).ConfigureAwait(false);
        }
        Check(Volatile.Read(ref _unobservedPrivateFaults) == 0, "A late transport/body fault was abandoned without being observed.");
    }

    private static void ValidateUrls()
    {
        foreach (string? value in new string?[] { null, "", " ", "http://assets.example.test/logo.png", "file:///logo.png", "ftp://assets.example.test/logo.png",
            "//assets.example.test/logo.png", "https://user:password@assets.example.test/logo.png", "https://assets.example.test/logo.png#fragment",
            "https://assets.example.test/lo go.png", "https://assets.example.test/logo.png\n", "https://assets.example.test/\tlogo.png",
            "https://assets.example.test/" + new string('x', 2048) })
        {
            object?[] call = { value, null, null };
            Check(!(bool)Invoke("TryParseLogoUrl", call)!, "Invalid/non-HTTPS logo URL accepted.");
        }
        foreach (string value in new[] { Logo.AbsoluteUri, "HTTPS://ASSETS.EXAMPLE.TEST/logo.png", "https://assets.example.test/image?id=42", "https://assets.example.test/logo%20image.png" })
        {
            object?[] call = { value, null, null };
            Check((bool)Invoke("TryParseLogoUrl", call)! && call[1] is Uri uri && uri.Scheme == Uri.UriSchemeHttps,
                "A supported direct HTTPS logo URL was rejected.");
        }
    }

    private static async Task ValidateDownload()
    {
        using (var handler = new FakeHandler((_, __) => Task.FromResult(Response(Png))))
        {
            byte[] received = await Download(Logo, CancellationToken.None, handler).ConfigureAwait(false);
            Check(received.SequenceEqual(Png) && handler.Requests.Count == 1, "Successful download changed PNG bytes or retried unnecessarily.");
        }
        foreach (HttpStatusCode status in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError, HttpStatusCode.NoContent })
        {
            using (var handler = new FakeHandler((_, __) => Task.FromResult(new HttpResponseMessage(status)
            {
                ReasonPhrase = Secret + " " + Logo.AbsoluteUri,
                Content = new ByteArrayContent(Png)
            })))
            {
                Exception error = await ClassifiedFailure(() => Download(Logo, CancellationToken.None, handler), "HttpStatus", (int)status).ConfigureAwait(false);
                string message = Diagnostic(error);
                Check(Contains(message, "HTTP") && Contains(message, ((int)status).ToString()), "HTTP diagnostics omitted the safe numeric status.");
            }
        }
        using (var handler = new FakeHandler((_, __) => Task.FromResult(Response(new byte[] { 1, 2, 3, 4 }))))
            Check(Contains(Diagnostic(await ClassifiedFailure(() => Download(Logo, CancellationToken.None, handler), "InvalidPng").ConfigureAwait(false)), "PNG"),
                "Invalid PNG diagnostics omitted the validation category.");
        foreach (int dimensionOffset in new[] { 16, 20 })
        {
            byte[] hugeDimensions = (byte[])Png.Clone();
            hugeDimensions[dimensionOffset + 2] = 0x10;
            hugeDimensions[dimensionOffset + 3] = 0x01; // 4097: just beyond the 4096-pixel limit.
            using (var handler = new FakeHandler((_, __) => Task.FromResult(Response(hugeDimensions))))
                await ClassifiedFailure(() => Download(Logo, CancellationToken.None, handler), "InvalidPng").ConfigureAwait(false);
        }
    }

    private static async Task ValidateRedirects()
    {
        foreach (int status in new[] { 301, 302, 303, 307, 308 })
        {
            using (var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/logo.png"
                ? Redirect(status, "/resolved.png") : Response(Png))))
            {
                Check((await Download(Logo, CancellationToken.None, handler).ConfigureAwait(false)).SequenceEqual(Png) && handler.Requests.Count == 2,
                    "A bounded relative HTTPS redirect was not followed.");
            }
        }
        foreach (string destination in new[] { "http://assets.example.test/insecure.png", "https://user:secret@assets.example.test/logo.png", "https://assets.example.test/logo.png#hidden", "file:///private.png" })
        {
            using (var handler = new FakeHandler((_, __) => Task.FromResult(Redirect(302, destination))))
            {
                await ClassifiedFailure(() => Download(Logo, CancellationToken.None, handler), "Redirect").ConfigureAwait(false);
                Check(handler.Requests.Count == 1, "An unsafe redirect destination reached the transport.");
            }
        }
        using (var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/third.png"
            ? Response(Png) : Redirect(302, request.RequestUri.AbsolutePath == "/logo.png" ? "/first.png" : request.RequestUri.AbsolutePath == "/first.png" ? "/second.png" : "/third.png"))))
        {
            Check((await Download(Logo, CancellationToken.None, handler).ConfigureAwait(false)).SequenceEqual(Png) && handler.Requests.Count == 4,
                "The supported three-redirect boundary was rejected.");
        }
        using (var handler = new FakeHandler((_, __) => Task.FromResult(Redirect(302, "/loop.png"))))
        {
            string message = Diagnostic(await ClassifiedFailure(() => Download(Logo, CancellationToken.None, handler), "Redirect").ConfigureAwait(false));
            Check(Contains(message, "redirect") && Contains(message, "3"), "Redirect diagnostics omitted the three-redirect bound.");
            Check(handler.Requests.Count <= 4, "The redirect limit allowed excessive requests.");
        }
        using (var handler = new FakeHandler((_, __) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found))))
            await ClassifiedFailure(() => Download(Logo, CancellationToken.None, handler), "Redirect").ConfigureAwait(false);
    }

    private static async Task ValidateBounds()
    {
        var declared = new StreamContentProbe(new GeneratedStream(1));
        declared.Headers.ContentLength = MaximumBytes + 1L;
        using (var handler = new FakeHandler((_, __) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = declared })))
        {
            string message = Diagnostic(await ClassifiedFailure(() => Download(Logo, CancellationToken.None, handler), "TooLarge").ConfigureAwait(false));
            Check(Contains(message, "8 MiB"), "Oversized-body diagnostics omitted the 8 MiB limit.");
            Check(!declared.Opened, "An oversized declared response was opened before rejecting its length.");
        }
        var body = new GeneratedStream(MaximumBytes + 1024 * 1024L);
        using (var handler = new FakeHandler((_, __) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContentProbe(body) })))
        {
            await ClassifiedFailure(() => Download(Logo, CancellationToken.None, handler), "TooLarge").ConfigureAwait(false);
            Check(body.ReadBytes <= MaximumBytes + 256 * 1024L && body.Disposed, "The over-limit response was read in full or its stream leaked.");
        }
        var validStream = new MemoryStream(Png, false);
        using (var handler = new FakeHandler((_, __) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContentProbe(validStream) })))
            Check((await Download(Logo, CancellationToken.None, handler).ConfigureAwait(false)).SequenceEqual(Png), "A valid unknown-length PNG stream failed.");
    }

    private static async Task ValidateCancellation()
    {
        using (var stop = new CancellationTokenSource())
        using (var handler = new FakeHandler((_, __) => Task.FromResult(Response(Png))))
        {
            stop.Cancel();
            Exception error = await Failure(() => Download(Logo, stop.Token, handler), "A pre-cancelled logo request was accepted.").ConfigureAwait(false);
            Check(error is OperationCanceledException && handler.Requests.Count == 0,
                "A pre-cancelled logo request was misclassified or reached the transport.");
        }
        using (var stop = new CancellationTokenSource())
        using (var handler = new FakeHandler(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation).ConfigureAwait(false);
            return Response(Png);
        }))
        {
            var watch = Stopwatch.StartNew();
            Task<byte[]> pending = Download(Logo, stop.Token, handler);
            stop.CancelAfter(50);
            Exception error = await Failure(() => pending, "Explicit cancellation did not end the logo request.").ConfigureAwait(false);
            Check(error is OperationCanceledException && watch.Elapsed < TimeSpan.FromSeconds(3), "Caller cancellation was not propagated promptly.");
            Check(Contains(Diagnostic(error), "cancel") && !Contains(Diagnostic(error), "timeout"), "Caller cancellation was misreported as a timeout.");
        }
        var blockedBody = new CancellationIgnoringStream();
        using (var stop = new CancellationTokenSource())
        using (var handler = new FakeHandler((_, __) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContentProbe(blockedBody)
        })))
        {
            Task<byte[]> pending = Download(Logo, stop.Token, handler);
            Check(await Task.WhenAny(blockedBody.ReadStarted.Task, Task.Delay(3000)).ConfigureAwait(false) == blockedBody.ReadStarted.Task,
                "The cancellation test did not reach the response body read.");
            var watch = Stopwatch.StartNew();
            stop.Cancel();
            Exception error = await Failure(() => pending, "A body stream ignoring cancellation blocked the logo request.").ConfigureAwait(false);
            Check(error is OperationCanceledException && watch.Elapsed < TimeSpan.FromSeconds(3), "Body cancellation was not independently bounded.");
            Check(blockedBody.Disposed, "The cancelled response body stream was not disposed.");
        }
        // Exercise the production thirty-second policy once, with a body that
        // ignores cancellation. The outer harness deadline terminates only its
        // own process if a regression hangs it.
        var timeoutBody = new CancellationIgnoringStream();
        using (var handler = new FakeHandler((_, __) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContentProbe(timeoutBody)
        })))
        {
            var watch = Stopwatch.StartNew();
            Exception error = await Failure(() => Download(Logo, CancellationToken.None, handler), "The fixed download timeout did not fire.").ConfigureAwait(false);
            Check(error is TimeoutException && !(error is OperationCanceledException), "The owned deadline was not distinguished from caller cancellation.");
            Check(watch.Elapsed >= TimeSpan.FromSeconds(28) && watch.Elapsed < TimeSpan.FromSeconds(40), "The production total timeout is not thirty seconds.");
            Check(timeoutBody.ReadStarted.Task.IsCompleted && timeoutBody.Disposed, "The timed-out body was never read or was not disposed.");
            string message = Diagnostic(error);
            Check(Contains(message, "30") && (Contains(message, "timeout") || Contains(message, "timed out")), "Timeout diagnostics omitted the thirty-second deadline.");
            timeoutBody.Fail(new IOException(Secret + " late timed-out body " + Logo.AbsoluteUri));
        }
    }

    private static async Task ValidateLateCompletion()
    {
        var lateResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var stop = new CancellationTokenSource())
        using (var handler = new FakeHandler((_, __) => lateResponse.Task))
        {
            Task<byte[]> pending = Download(Logo, stop.Token, handler);
            var watch = Stopwatch.StartNew();
            stop.Cancel();
            Exception error = await Failure(() => pending, "A transport ignoring cancellation blocked the logo request.").ConfigureAwait(false);
            Check(error is OperationCanceledException && watch.Elapsed < TimeSpan.FromSeconds(3), "Transport cancellation was not independently bounded.");
            var lateBody = new GeneratedStream(1);
            lateResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContentProbe(lateBody) });
            await Eventually(() => lateBody.Disposed, "An abandoned late HTTP response was not disposed.").ConfigureAwait(false);
        }
        var lateFault = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var stop = new CancellationTokenSource())
        using (var handler = new FakeHandler((_, __) => lateFault.Task))
        {
            Task<byte[]> pending = Download(Logo, stop.Token, handler);
            stop.Cancel();
            Exception error = await Failure(() => pending, "Cancellation did not detach a faulting late transport.").ConfigureAwait(false);
            Check(error is OperationCanceledException, "A caller cancellation changed failure kind before late transport completion.");
            lateFault.SetException(new HttpRequestException(Secret + " late transport " + Logo.AbsoluteUri));
            await Task.Delay(50).ConfigureAwait(false);
            Check(pending.IsCanceled, "An abandoned transport fault changed the already-cancelled download task.");
        }
        var lateBodyFault = new CancellationIgnoringStream();
        using (var stop = new CancellationTokenSource())
        using (var handler = new FakeHandler((_, __) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContentProbe(lateBodyFault)
        })))
        {
            Task<byte[]> pending = Download(Logo, stop.Token, handler);
            Check(await Task.WhenAny(lateBodyFault.ReadStarted.Task, Task.Delay(3000)).ConfigureAwait(false) == lateBodyFault.ReadStarted.Task,
                "The late-fault test never reached the response body.");
            stop.Cancel();
            Check(await Failure(() => pending, "Cancellation did not detach a faulting late body.").ConfigureAwait(false) is OperationCanceledException,
                "A caller cancellation changed failure kind before late body completion.");
            lateBodyFault.Fail(new IOException(Secret + " late body " + Logo.AbsoluteUri));
            await Task.Delay(50).ConfigureAwait(false);
            Check(pending.IsCanceled && lateBodyFault.Disposed, "A late body fault changed the cancellation result or leaked its stream.");
        }
    }

    private static async Task ValidateDiagnostics()
    {
        string privateDetails = Secret + " " + Logo.AbsoluteUri;
        string? networkMessage = null;
        foreach (Exception transportError in new Exception[]
        {
            new HttpRequestException(privateDetails), new IOException(privateDetails),
            new AuthenticationException(privateDetails), new WebException(privateDetails)
        })
        {
            using (var handler = new FakeHandler((_, __) => Task.FromException<HttpResponseMessage>(transportError)))
            {
                Exception error = await ClassifiedFailure(() => Download(Logo, CancellationToken.None, handler), "Network").ConfigureAwait(false);
                Check(error.InnerException == null, "Sanitized transport failures retained private inner exception details.");
                string message = Diagnostic(error);
                Check(Contains(message, "network") && Contains(message, "TLS"), "Transport diagnostics omitted the network/TLS category.");
                Check(networkMessage == null || networkMessage == message, "Transport error details altered the fixed safe diagnostic.");
                networkMessage = message;
            }
        }
        foreach (int unsafeStatus in new[] { 99, 600, 999 })
        {
            using (var handler = new FakeHandler((_, __) => Task.FromResult(Response(Png, (HttpStatusCode)unsafeStatus))))
            {
                string message = Diagnostic(await ClassifiedFailure(() => Download(Logo, CancellationToken.None, handler), "HttpStatus", unsafeStatus).ConfigureAwait(false));
                Check(!Contains(message, unsafeStatus.ToString()), "An out-of-range HTTP status was included in the diagnostic.");
            }
        }
        foreach (Func<string, Exception> makeError in new Func<string, Exception>[]
        {
            detail => new Exception(detail), detail => new InvalidOperationException(detail),
            detail => new HttpRequestException(detail), detail => new TimeoutException(detail),
            detail => new OperationCanceledException(detail)
        })
        {
            Check(Diagnostic(makeError(privateDetails)) == Diagnostic(makeError(OtherSecret)),
                "Exception message details influenced a supposedly fixed safe diagnostic.");
        }
    }

    private static async Task ValidateCache(string root)
    {
        string cacheRoot = Path.Combine(root, "ServerManager", "cache");
        string cache = (string)Invoke("GetLogoCachePath", new object?[] { cacheRoot, Logo })!;
        string prefix = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Check(Path.GetFullPath(cache).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(cache) == Path.Combine(root, "ServerManager", "cache", "logos") &&
            Regex.IsMatch(Path.GetFileName(cache), "^[a-f0-9]{64}\\.png$"), "Cache path is outside the local save cache or exposes the URL/query.");
        Check(!Directory.Exists(cacheRoot), "Computing a cache key performed filesystem writes.");
        string alternate = (string)Invoke("GetLogoCachePath", new object?[] { cacheRoot, new Uri("https://assets.example.test/logo.png?signature=other") })!;
        Check(cache != alternate, "Different URL queries collide in the cache.");
        string canonical = (string)Invoke("GetLogoCachePath", new object?[] { cacheRoot, new Uri("HTTPS://ASSETS.EXAMPLE.TEST:443/logo.png?signature=test-private-value") })!;
        Check(cache == canonical, "Equivalent canonical HTTPS URLs get different cache keys.");
        Invoke("WriteLogoCache", new object?[] { cache, Png });
        Check(!Directory.Exists(Path.Combine(root, "ServerManager", "characters")), "Writing a logo cache prepared server character storage.");
        Check(((byte[])Invoke("ReadLogoBytes", new object?[] { cache })!).SequenceEqual(Png), "A valid PNG did not round-trip through the cache.");
        await Failure(() => { Invoke("WriteLogoCache", new object?[] { cache, new byte[] { 1, 2, 3 } }); return Task.CompletedTask; }, "An invalid PNG replaced the cached logo.").ConfigureAwait(false);
        Check(File.ReadAllBytes(cache).SequenceEqual(Png), "An invalid replacement destroyed the last-good cache.");
        using (var handler = new FakeHandler((_, __) => Task.FromResult(Response(Png, HttpStatusCode.InternalServerError))))
            await Failure(() => Download(Logo, CancellationToken.None, handler), "Failed refresh unexpectedly succeeded.").ConfigureAwait(false);
        Check(File.ReadAllBytes(cache).SequenceEqual(Png), "A failed refresh changed the last-good file.");
        // The .NET48 harness runs on Windows. A reader that denies delete sharing
        // forces File.Replace to fail after its temporary file has been written.
        using (var reader = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Failure(() => { Invoke("WriteLogoCache", new object?[] { cache, OtherPng }); return Task.CompletedTask; },
                "Cache replacement unexpectedly bypassed an existing reader's sharing lock.").ConfigureAwait(false);
            Check(File.ReadAllBytes(cache).SequenceEqual(Png), "A failed atomic replacement destroyed the last-good cache.");
            Check(Directory.EnumerateFiles(Path.GetDirectoryName(cache)!).Count() == 1, "A failed atomic replacement left a temporary file behind.");
        }
        Invoke("WriteLogoCache", new object?[] { cache, OtherPng });
        Check(((byte[])Invoke("ReadLogoBytes", new object?[] { cache })!).SequenceEqual(OtherPng), "A successful cache refresh did not replace the previous bytes.");
        Check(Directory.EnumerateFiles(Path.GetDirectoryName(cache)!).Count() == 1, "Atomic cache replacement left temporary or backup files behind.");
        string tooLarge = Path.Combine(root, "too-large.png");
        using (var file = File.Create(tooLarge)) file.SetLength(MaximumBytes + 1L);
        await Failure(() => { Invoke("ReadLogoBytes", new object?[] { tooLarge }); return Task.CompletedTask; }, "An oversized cache file bypassed the read bound.").ConfigureAwait(false);
        string bad = Path.Combine(root, "invalid.png");
        File.WriteAllBytes(bad, new byte[] { 0, 1, 2 });
        await Failure(() => { Invoke("ReadLogoBytes", new object?[] { bad }); return Task.CompletedTask; }, "A corrupt cached file was treated as a valid image.").ConfigureAwait(false);
    }

    private static MethodInfo Method(string name) => _branding.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Single(value => value.Name == name);
    private static object? Invoke(string name, object?[] arguments)
    {
        try { return Method(name).Invoke(null, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
    private static Task<byte[]> Download(Uri url, CancellationToken cancellation, HttpMessageHandler handler) =>
        (Task<byte[]>)Invoke("DownloadLogoAsync", new object?[] { url, cancellation, handler })!;
    private static object? FailureDetail(Exception error, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo? property = error.GetType().GetProperty(name, flags);
        return property != null ? property.GetValue(error) : error.GetType().GetField(name, flags)?.GetValue(error);
    }
    private static async Task<Exception> ClassifiedFailure(Func<Task> operation, string kind, int? status = null)
    {
        Exception error = await Failure(operation, "A logo download expected to fail as " + kind + " was accepted.").ConfigureAwait(false);
        Check(error.GetType().DeclaringType == _branding && error.GetType().Name == "LogoDownloadException" &&
            string.Equals(FailureDetail(error, "Failure")?.ToString(), kind, StringComparison.Ordinal),
            "A logo download failure did not retain its expected safe category: " + kind + ".");
        if (status.HasValue) Check(FailureDetail(error, "Status") is int actualStatus && actualStatus == status.Value,
            "The typed HTTP failure did not retain its numeric response status.");
        NoPrivateDetails(error.ToString());
        Diagnostic(error);
        return error;
    }
    private static string Diagnostic(Exception error)
    {
        string message = (string)Invoke("LogoDownloadFailureMessage", new object?[] { error })!;
        Check(!string.IsNullOrWhiteSpace(message) && Contains(message, "cached") && Contains(message, "vanilla"),
            "A logo failure diagnostic omitted the retained cached/vanilla fallback.");
        NoPrivateDetails(message);
        return message;
    }
    private static void NoPrivateDetails(string value)
    {
        foreach (string privateDetail in new[] { Secret, OtherSecret, Logo.AbsoluteUri, "assets.example.test", "signature=", "test-private-value" })
            Check(!Contains(value, privateDetail), "A logo failure exposed private URL, reason-phrase, or exception details.");
    }
    private static bool Contains(string value, string fragment) => value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    private static async Task Eventually(Func<bool> completed, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!completed() && watch.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10).ConfigureAwait(false);
        Check(completed(), message);
    }
    private static HttpResponseMessage Response(byte[] data, HttpStatusCode code = HttpStatusCode.OK) => new HttpResponseMessage(code) { Content = new ByteArrayContent(data) };
    private static HttpResponseMessage Redirect(int status, string location)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
    private static async Task<Exception> Failure(Func<Task> operation, string message)
    {
        try { await operation().ConfigureAwait(false); }
        catch (Exception error) { ++_checks; return error; }
        throw new InvalidOperationException(message);
    }
    private static void Check(bool condition, string message)
    {
        ++_checks;
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        internal readonly List<Uri> Requests = new List<Uri>();
        internal FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.Scheme == Uri.UriSchemeHttps, "Unexpected HTTP method or insecure request reached the fake handler.");
            Check(request.Headers.Authorization == null && !request.Headers.Contains("Cookie"), "Logo HTTP carries unrelated credentials or cookies.");
            Requests.Add(request.RequestUri!);
            return _send(request, cancellationToken);
        }
    }
    private sealed class StreamContentProbe : HttpContent
    {
        private readonly Stream _stream;
        internal bool Opened;
        internal StreamContentProbe(Stream stream) => _stream = stream;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() { Opened = true; return Task.FromResult(_stream); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) { Opened = true; return _stream.CopyToAsync(stream); }
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    }
    private sealed class GeneratedStream : Stream
    {
        private readonly long _length;
        internal long ReadBytes;
        internal bool Disposed;
        internal GeneratedStream(long length) => _length = length;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int accepted = (int)Math.Min(count, _length - ReadBytes);
            Array.Clear(buffer, offset, accepted);
            ReadBytes += accepted;
            return accepted;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => ReadBytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class CancellationIgnoringStream : Stream
    {
        internal readonly TaskCompletionSource<bool> ReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _pendingRead = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        internal void Fail(Exception error) => _pendingRead.TrySetException(error);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult(true);
            return _pendingRead.Task;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
