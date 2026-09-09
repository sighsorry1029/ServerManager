// Offline harness: compiled only by DiscordGatewaySmoke.ps1, never into the plugin.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ServerManager.Discord;

internal static class DiscordGatewayHarness
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _checks;

    private static int Main()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS: Discord Gateway offline smoke (" + _checks + " assertions).");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        using (var gateway = NewGateway())
        {
            Check(gateway.ConnectionStatus == "pending", "Unstarted connection status is a fixed pending label");
            typeof(DiscordGateway).GetField("_started", PrivateInstance)!.SetValue(gateway, true);
            Check(gateway.ConnectionStatus == "connecting_or_reconnecting", "Socket-less started gateway never claims connected");
            gateway.Dispose();
            Check(gateway.ConnectionStatus == "stopped", "Disposed gateway status is fixed and secret-free");
        }
        using (var client = new ClientWebSocket())
        {
            client.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            Check(client.Options.KeepAliveInterval == Timeout.InfiniteTimeSpan, "Framework WebSocket option compatibility");
        }
        foreach (string valid in new[]
        {
            "wss://gateway.discord.gg", "wss://gateway.discord.gg/",
            "wss://gateway-us-east1-b.discord.gg/?v=9&compress=zlib-stream"
        })
        {
            Uri url = DiscordGateway.ValidateGatewayUrl(valid);
            Check(url.Scheme == "wss" && url.Port == 443 && url.Query == "?v=10&encoding=json", "Canonical Gateway URL");
        }
        foreach (string invalid in new[]
        {
            "ws://gateway.discord.gg/", "https://gateway.discord.gg/", "wss://localhost/",
            "wss://gateway.discord.gg.evil.example/", "wss://evil.example/", "wss://gateway-.discord.gg/",
            "wss://gateway-foo.evil.discord.gg/", "wss://gateway.discord.gg:444/",
            "wss://secret@gateway.discord.gg/", "wss://gateway.discord.gg/secret",
            "wss://gateway.discord.gg/#secret", "wss://127.0.0.1/"
        })
        {
            Throws<DiscordGateway.GatewayFailure>(() => DiscordGateway.ValidateGatewayUrl(invalid), "Unsafe Gateway URL");
        }
        foreach (int fatal in new[] { 4004, 4010, 4011, 4012, 4013, 4014 })
            Check(DiscordGateway.FailureForCloseCode(fatal).Fatal, "Fatal close classification");
        foreach (int retry in new[] { 0, 1006, 4000, 4001, 4002, 4005, 4008 })
            Check(!DiscordGateway.FailureForCloseCode(retry).Fatal, "Retry close classification");
        foreach (int invalid in new[] { 1000, 1001, 4003, 4007, 4009 })
            Check(DiscordGateway.FailureForCloseCode(invalid).ClearSession, "Invalid session close classification");
        Check(DiscordGateway.FailureForCloseCode(4008).MinimumDelayMs >= 60000, "Rate-limit close cooldown");
        string intentError = DiscordGateway.FailureForCloseCode(4014).Message;
        Check(intentError.Contains("Message Content Intent") && intentError.Contains("Discord Developer Portal") &&
            intentError.Contains("Privileged Gateway Intents") && intentError.Contains("approval if required") &&
            intentError.Contains("Reconnect stopped") && intentError.Contains("4014"),
            "Disallowed privileged intent explains required chat setup and fatal stop");

        string json = "{\"op\":0,\"t\":\"TEST\",\"s\":1,\"d\":{\"content\":\"한글\"}}";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        using (var socket = FakeSocket.Fragmented(bytes, 1))
        {
            DiscordGateway.GatewayMessage message = await DiscordGateway.ReceiveMessageAsync(socket, CancellationToken.None);
            Check(message.Payload["d"]!["content"]!.Value<string>() == "한글", "UTF-8 split across frames");
            Check(message.Bytes == bytes.Length, "Message byte accounting");
        }
        foreach (string malformed in new[]
        {
            "[]", "{\"op\":0,\"op\":1}", "{} {}", "{\"secret\":", ""
        })
        {
            using (var socket = FakeSocket.Text(malformed))
                await ThrowsAsync<DiscordGateway.GatewayFailure>(() => DiscordGateway.ReceiveMessageAsync(socket, CancellationToken.None), "Malformed JSON rejected");
        }
        using (var socket = FakeSocket.Fragmented(new byte[] { 0xff, 0xfe }, 1))
            await ThrowsAsync<DiscordGateway.GatewayFailure>(() => DiscordGateway.ReceiveMessageAsync(socket, CancellationToken.None), "Invalid UTF-8 rejected");
        using (var socket = FakeSocket.Text("{\"x\":" + new string('[', 70) + "0" + new string(']', 70) + "}"))
            await ThrowsAsync<DiscordGateway.GatewayFailure>(() => DiscordGateway.ReceiveMessageAsync(socket, CancellationToken.None), "JSON depth bounded");
        using (var socket = FakeSocket.Fragmented(new byte[DiscordGateway.MaximumMessageBytes + 1], 8192))
            await ThrowsAsync<DiscordGateway.GatewayFailure>(() => DiscordGateway.ReceiveMessageAsync(socket, CancellationToken.None), "Inbound bytes bounded");
        using (var socket = new FakeSocket())
        {
            for (int i = 0; i < 4097; i++) socket.Frames.Enqueue(new Frame(Array.Empty<byte>(), false));
            await ThrowsAsync<DiscordGateway.GatewayFailure>(() => DiscordGateway.ReceiveMessageAsync(socket, CancellationToken.None), "Empty fragments bounded");
        }
        using (var socket = new FakeSocket())
        {
            socket.Frames.Enqueue(new Frame(new byte[] { 1 }, true, WebSocketMessageType.Binary));
            await ThrowsAsync<DiscordGateway.GatewayFailure>(() => DiscordGateway.ReceiveMessageAsync(socket, CancellationToken.None), "Compression/binary rejected");
        }
        using (var socket = new FakeSocket())
        {
            socket.AddClose(4014, "secret token https://secret.example/");
            try { await DiscordGateway.ReceiveMessageAsync(socket, CancellationToken.None); throw new Exception("Expected close failure"); }
            catch (DiscordGateway.GatewayFailure error)
            {
                Check(error.Fatal && !error.ToString().Contains("secret"), "Close reason never leaks");
            }
        }
        using (var socket = new FakeSocket())
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => DiscordGateway.ReceiveMessageAsync(socket, stop.Token), "Receive cancellation");
        }

        await TestSessionAndHeartbeatAsync();
        await TestSendSerializationAsync();
        await TestQueueAndShutdownAsync();
        await TestIdentifyBudgetAsync();
        await TestIdentifyIntentsAsync();
    }

    private static async Task TestIdentifyIntentsAsync()
    {
        using (var gateway = new DiscordGateway("test-token-not-real", (_, _) => Task.CompletedTask, _ => { }, new DiscordHttp()))
        {
            JObject identify = IdentifyPayload(gateway);
            Check(identify["d"]!["intents"]!.Value<int>() == 1, "Existing four-argument constructor remains GUILDS-only");
        }
        foreach (bool receiveMessages in new[] { false, true })
        {
            using (var gateway = NewGateway(receiveMessages: receiveMessages))
            using (var socket = new FakeSocket())
            using (var sendGate = new SemaphoreSlim(1, 1))
            {
                JObject identify = IdentifyPayload(gateway);
                SetField(gateway, "_remainingIdentifies", 1);
                await InvokeTask(gateway, "SendAsync", NewConnection(socket, sendGate), identify, CancellationToken.None, false, true);
                JObject sent = socket.Sent.Single();
                int intents = sent["d"]!["intents"]!.Value<int>();
                Check(sent["op"]!.Value<int>() == 2 && intents == (receiveMessages ? 33281 : 1),
                    "Identify wire payload requests message and content intents only for chat-enabled gateway");
                Check((intents & ((1 << 1) | (1 << 8) | (1 << 12))) == 0,
                    "Chat Identify never requests guild members, presence or direct messages");
                Check(sent["d"]!["token"]!.Value<string>() == "test-token-not-real" &&
                    sent["d"]!["compress"]!.Value<bool>() == false && sent["d"]!["large_threshold"]!.Value<int>() == 50,
                    "Dynamic intents preserve existing Identify authentication and transport fields");
                identify["d"]!["intents"] = 0;
                Check(IdentifyPayload(gateway)["d"]!["intents"]!.Value<int>() == intents,
                    "Each Identify rebuilds configured intents without sharing mutable payloads");
            }
        }
    }

    private static JObject IdentifyPayload(DiscordGateway gateway) =>
        (JObject)typeof(DiscordGateway).GetMethod("CreateIdentifyPayload", PrivateInstance)!.Invoke(gateway, Array.Empty<object>())!;

    private static async Task TestSendSerializationAsync()
    {
        using (var gateway = NewGateway())
        using (var socket = new FakeSocket { SendDelayMs = 3 })
        using (var sendGate = new SemaphoreSlim(1, 1))
        {
            object state = NewConnection(socket, sendGate);
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => InvokeTask(gateway, "SendHeartbeatAsync", state, CancellationToken.None)));
            Check(socket.Sent.Count == 20 && !socket.ConcurrentSend, "Concurrent callers serialize socket sends");
            for (int i = 20; i < 110; i++) await InvokeTask(gateway, "SendHeartbeatAsync", state, CancellationToken.None);
            await ThrowsAsync<DiscordGateway.GatewayFailure>(() => InvokeTask(gateway, "SendHeartbeatAsync", state, CancellationToken.None), "Outbound Gateway rate ceiling enforced");
            Check(socket.Sent.Count == 110, "Rate-limited send never reaches socket");
        }
    }

    private static async Task TestSessionAndHeartbeatAsync()
    {
        using (var gateway = NewGateway())
        using (var socket = new FakeSocket())
        using (var sendGate = new SemaphoreSlim(1, 1))
        {
            object state = NewConnection(socket, sendGate);
            socket.AddText("{\"op\":0,\"s\":42,\"t\":\"READY\",\"d\":{\"session_id\":\"session_test\",\"resume_gateway_url\":\"wss://gateway-us-east1-b.discord.gg/\"}}");
            socket.AddText("{\"op\":1,\"d\":null}");
            socket.AddText("{\"op\":11,\"d\":null}");
            socket.AddText("{\"op\":7,\"d\":null}");
            await ThrowsAsync<DiscordGateway.GatewayFailure>(() => InvokeTask(gateway, "ReceiveEventsAsync", state, CancellationToken.None), "Reconnect event");
            Check((long)GetField(gateway, "_sequence")! == 42, "READY sequence saved");
            Check((string)GetField(gateway, "_sessionId")! == "session_test", "Session ID saved");
            Check(((Uri)GetField(gateway, "_resumeUrl")!).Host == "gateway-us-east1-b.discord.gg", "Resume URL saved");
            Check(socket.Sent.Count == 1 && socket.Sent[0]["op"]!.Value<int>() == 1 && socket.Sent[0]["d"]!.Value<long>() == 42, "Server heartbeat responds with sequence");
            Check((long)state.GetType().GetField("PendingHeartbeatSince", PrivateInstance)!.GetValue(state)! == -1, "ACK clears pending heartbeat");
        }
        using (var gateway = NewGateway())
        using (var socket = new FakeSocket())
        using (var sendGate = new SemaphoreSlim(1, 1))
        {
            object state = NewConnection(socket, sendGate);
            await ThrowsAsync<DiscordGateway.GatewayFailure>(() => InvokeTask(gateway, "HeartbeatLoopAsync", state, 1000, CancellationToken.None), "Missing heartbeat ACK triggers reconnect");
            Check(socket.Sent.Count == 1 && socket.Sent[0]["d"]!.Type == JTokenType.Null, "Initial heartbeat has null sequence");
        }
        foreach (bool resumable in new[] { true, false })
        {
            using (var gateway = NewGateway())
            using (var socket = FakeSocket.Text("{\"op\":9,\"d\":" + resumable.ToString().ToLowerInvariant() + "}"))
            using (var sendGate = new SemaphoreSlim(1, 1))
            {
                try { await InvokeTask(gateway, "ReceiveEventsAsync", NewConnection(socket, sendGate), CancellationToken.None); }
                catch (DiscordGateway.GatewayFailure error)
                {
                    Check(error.ClearSession == !resumable && error.MinimumDelayMs >= 1000, "Invalid Session resumption flag and delay");
                }
            }
        }
    }

    private static async Task TestQueueAndShutdownAsync()
    {
        using (var gateway = NewGateway())
        {
            for (int i = 0; i < 128; i++) Check(Enqueue(gateway, 1), "Queue within count budget");
            Check(!Enqueue(gateway, 1), "Queue count bounded");
            using (var socket = FakeSocket.Text("{\"op\":0,\"s\":99,\"t\":\"TEST\",\"d\":{}}"))
            using (var sendGate = new SemaphoreSlim(1, 1))
            {
                await ThrowsAsync<DiscordGateway.GatewayFailure>(() => InvokeTask(gateway, "ReceiveEventsAsync", NewConnection(socket, sendGate), CancellationToken.None), "Queue overflow reconnects");
                Check((long)GetField(gateway, "_sequence")! == -1, "Rejected event does not advance resume sequence");
            }
        }
        using (var gateway = NewGateway())
        {
            Check(Enqueue(gateway, 2 * 1024 * 1024), "First large queued item");
            Check(Enqueue(gateway, 2 * 1024 * 1024), "Queue exact byte limit");
            Check(!Enqueue(gateway, 1), "Queue bytes bounded");
        }
        var callbackEntered = new TaskCompletionSource<bool>();
        var callbackFinish = new TaskCompletionSource<bool>();
        int active = 0;
        using (var gateway = NewGateway(async (_, _) =>
        {
            Interlocked.Increment(ref active);
            callbackEntered.TrySetResult(true);
            await callbackFinish.Task;
        }))
        using (var stop = new CancellationTokenSource())
        {
            Enqueue(gateway, 1);
            Enqueue(gateway, 1);
            Task worker = InvokeTask(gateway, "DispatchLoopAsync", stop.Token);
            Check(await Task.WhenAny(callbackEntered.Task, Task.Delay(2000)) == callbackEntered.Task, "Dispatch worker starts");
            Check(active == 1, "Exactly one active callback");
            stop.Cancel();
            Check(await Task.WhenAny(worker, Task.Delay(2000)) == worker, "Shutdown does not wait on a stuck callback");
            await worker;
            callbackFinish.TrySetResult(true);
        }
        using (var stop = new CancellationTokenSource())
        using (var gateway = NewGateway())
        {
            stop.Cancel();
            await gateway.RunAsync(stop.Token);
            Throws<InvalidOperationException>(() => gateway.RunAsync(CancellationToken.None), "Run cannot be restarted");
        }
    }

    private static async Task TestIdentifyBudgetAsync()
    {
        var http = new DiscordHttp();
        http.Results.Enqueue(Metadata(2));
        http.Results.Enqueue(Metadata(2));
        using (var gateway = NewGateway(http: http))
        {
            await InvokeTask(gateway, "PrepareIdentifyAsync", CancellationToken.None);
            Check((int)GetField(gateway, "_remainingIdentifies")! == 2, "Get Gateway Bot initial limit");
            SetField(gateway, "_remainingIdentifies", 1);
            await InvokeTask(gateway, "PrepareIdentifyAsync", CancellationToken.None);
            Check((int)GetField(gateway, "_remainingIdentifies")! == 1, "Stale REST metadata cannot replenish local budget");
            Check(http.Calls == 2, "Metadata refreshed before Identify");
            SetField(gateway, "_remainingIdentifies", 0);
            using (var stop = new CancellationTokenSource(40))
                await ThrowsAsync<OperationCanceledException>(() => InvokeTask(gateway, "PrepareIdentifyAsync", stop.Token), "Exhausted Identify budget waits cancellably");
            Check(http.Calls == 2, "No request during exhausted budget wait");
        }
        http = new DiscordHttp();
        http.Results.Enqueue(Metadata(2));
        using (var gateway = NewGateway(http: http))
        using (var socket = new FakeSocket())
        using (var sendGate = new SemaphoreSlim(1, 1))
        {
            await InvokeTask(gateway, "PrepareIdentifyAsync", CancellationToken.None);
            await InvokeTask(gateway, "SendAsync", NewConnection(socket, sendGate), new JObject { ["op"] = 2 }, CancellationToken.None, false, true);
            Check((int)GetField(gateway, "_remainingIdentifies")! == 1, "Identify consumes a session start");
            Check((long)GetField(gateway, "_identifyNotBefore")! >= 5500, "Identify establishes minimum spacing");
            using (var stop = new CancellationTokenSource(40))
                await ThrowsAsync<OperationCanceledException>(() => InvokeTask(gateway, "PrepareIdentifyAsync", stop.Token), "Identify concurrency spacing is cancellable");
            Check(http.Calls == 1, "No metadata/Identify request before minimum spacing");
        }
    }

    private static JObject Metadata(int remaining) => JObject.Parse("{\"url\":\"wss://gateway.discord.gg\",\"session_start_limit\":{\"total\":10,\"remaining\":" + remaining + ",\"reset_after\":60000,\"max_concurrency\":1}}");
    private static DiscordGateway NewGateway(Func<string, JObject, Task>? callback = null, DiscordHttp? http = null, bool receiveMessages = false) =>
        new DiscordGateway("test-token-not-real", callback ?? ((_, _) => Task.CompletedTask), _ => { }, http ?? new DiscordHttp(), receiveMessages);
    private static object? GetField(object value, string name) => value.GetType().GetField(name, PrivateInstance)!.GetValue(value);
    private static void SetField(object value, string name, object data) => value.GetType().GetField(name, PrivateInstance)!.SetValue(value, data);
    private static Task InvokeTask(object value, string name, params object[] args) => (Task)value.GetType().GetMethod(name, PrivateInstance)!.Invoke(value, args)!;
    private static object NewConnection(FakeSocket socket, SemaphoreSlim sendGate) =>
        Activator.CreateInstance(typeof(DiscordGateway).GetNestedType("ConnectionState", BindingFlags.NonPublic)!, PrivateInstance, null, new object[] { socket, sendGate }, null)!;
    private static bool Enqueue(DiscordGateway gateway, int bytes)
    {
        object item = Activator.CreateInstance(typeof(DiscordGateway).GetNestedType("DispatchItem", BindingFlags.NonPublic)!, PrivateInstance, null, new object[] { "TEST", new JObject(), bytes }, null)!;
        return (bool)typeof(DiscordGateway).GetMethod("TryEnqueue", PrivateInstance)!.Invoke(gateway, new[] { item })!;
    }
    private static void Check(bool condition, string title) { if (!condition) throw new Exception("FAILED: " + title); _checks++; }
    private static void Throws<T>(Action action, string title) where T : Exception
    {
        try { action(); }
        catch (T) { _checks++; return; }
        throw new Exception("FAILED (expected " + typeof(T).Name + "): " + title);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string title) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new Exception("FAILED (expected " + typeof(T).Name + "): " + title);
    }

    private sealed class Frame
    {
        internal Frame(byte[] data, bool end, WebSocketMessageType type = WebSocketMessageType.Text) { Data = data; End = end; Type = type; }
        internal byte[] Data { get; }
        internal bool End { get; }
        internal WebSocketMessageType Type { get; }
        internal int CloseCode = 1000;
        internal string? CloseReason;
    }
    private sealed class FakeSocket : WebSocket
    {
        internal readonly Queue<Frame> Frames = new Queue<Frame>();
        internal readonly List<JObject> Sent = new List<JObject>();
        internal int SendDelayMs;
        internal bool ConcurrentSend;
        private int _activeSends;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken ct) => Task.CompletedTask;
        public override async Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _activeSends) > 1) ConcurrentSend = true;
            try
            {
                if (SendDelayMs > 0) await Task.Delay(SendDelayMs, ct);
                lock (Sent) Sent.Add(JObject.Parse(Encoding.UTF8.GetString(bytes.Array!, bytes.Offset, bytes.Count)));
            }
            finally { Interlocked.Decrement(ref _activeSends); }
        }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Frames.Count == 0) return WaitCanceledAsync(ct);
            Frame frame = Frames.Dequeue();
            if (frame.Data.Length > buffer.Count) throw new Exception("Fake frame exceeds receive buffer.");
            Array.Copy(frame.Data, 0, buffer.Array!, buffer.Offset, frame.Data.Length);
            return Task.FromResult(frame.Type == WebSocketMessageType.Close
                ? new WebSocketReceiveResult(0, frame.Type, true, (WebSocketCloseStatus)frame.CloseCode, frame.CloseReason)
                : new WebSocketReceiveResult(frame.Data.Length, frame.Type, frame.End));
        }
        private static async Task<WebSocketReceiveResult> WaitCanceledAsync(CancellationToken ct)
        { await Task.Delay(Timeout.Infinite, ct); throw new OperationCanceledException(ct); }
        internal static FakeSocket Text(string value) { var socket = new FakeSocket(); socket.AddText(value); return socket; }
        internal void AddText(string value)
        {
            foreach (Frame frame in Fragmented(Encoding.UTF8.GetBytes(value), 8192).Frames) Frames.Enqueue(frame);
        }
        internal void AddClose(int code, string description) => Frames.Enqueue(new Frame(Array.Empty<byte>(), true, WebSocketMessageType.Close) { CloseCode = code, CloseReason = description });
        internal static FakeSocket Fragmented(byte[] bytes, int fragmentSize)
        {
            var socket = new FakeSocket();
            if (bytes.Length == 0) socket.Frames.Enqueue(new Frame(Array.Empty<byte>(), true));
            for (int offset = 0; offset < bytes.Length; offset += fragmentSize)
            {
                byte[] fragment = bytes.Skip(offset).Take(Math.Min(fragmentSize, bytes.Length - offset)).ToArray();
                socket.Frames.Enqueue(new Frame(fragment, offset + fragment.Length == bytes.Length));
            }
            return socket;
        }
    }
}

namespace ServerManager.Discord
{
    // Deliberately no real HTTP transport in this harness.
    internal sealed class DiscordHttp
    {
        internal readonly Queue<JToken> Results = new Queue<JToken>();
        internal int Calls;
        internal Task<JToken?> SendAsync(HttpMethod method, string endpoint, JToken? body, bool useBotToken, CancellationToken ct, bool retry = true)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            if (method != HttpMethod.Get || endpoint != "https://discord.com/api/v10/gateway/bot" || !useBotToken)
                throw new Exception("Unexpected HTTP request in offline Gateway test.");
            return Task.FromResult<JToken?>(Results.Dequeue());
        }
    }
    internal sealed class DiscordHttpException : Exception
    {
        internal int StatusCode { get; }
    }
}
