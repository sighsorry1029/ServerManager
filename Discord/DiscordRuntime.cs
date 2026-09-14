using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ServerManager.Events;

namespace ServerManager.Discord;

internal static class DiscordRuntime
{
    private static Session? _session;
    private static ZNet? _network;
    private static object? _world;
    private static string? _dataRoot;
    private static DiscordSettings? _settings;
    private static long _nextReloadPollTimestamp;
    private static Task<ReloadRead>? _reloadRead;
    private static string? _candidateText;
    private static string? _processedText;
    private static string? _lastReadError;
    private static readonly long ReloadPollTicks = 2 * Stopwatch.Frequency;

    // Called on Unity's thread only after server authority and its data root
    // have been established. Clients never open credentials or start workers.
    internal static void Start()
    {
        Stop();
        if (ZNet.instance == null || !ZNet.instance.IsServer())
            return;
        _network = ZNet.instance;
        _world = ZNet.World;
        _dataRoot = ServerManagerPlugin.DataRoot;
        _nextReloadPollTimestamp = Stopwatch.GetTimestamp() + ReloadPollTicks;
        try
        {
            DiscordSettings settings;
            try { settings = DiscordSettings.Load(ServerManagerPlugin.DataRoot, Log); }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                // The settings loader emits only schema/position diagnostics, never raw YAML values.
                Log(ReadError(exception) + " Discord starts disabled; watching for a corrected discord.yml.");
                settings = new DiscordSettings();
            }
            if (!settings.BotEnabled && settings.WebhookRoutes.Count == 0)
            {
                Log("Discord integration is disabled; live reload remains active. Server-only settings: ServerManager/discord.yml.");
            }
            Session session = new(settings);
            try { session.Start(); }
            catch { session.RetireForReload(); throw; }
            _settings = settings;
            Volatile.Write(ref _session, session);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            Stop();
            // Exception messages from configuration/HTTP libraries can contain
            // credential-bearing URLs. Never print them here.
            Log("Discord startup failed (" + exception.GetType().Name + "); the game server remains available.");
        }
    }

    internal static void Tick()
    {
        Session? session = Volatile.Read(ref _session);
        if (session == null)
            return;
        if (ZNet.instance == null || !ZNet.instance.IsServer()
            || !ReferenceEquals(ZNet.instance, _network) || !ReferenceEquals(ZNet.World, _world))
        {
            Stop();
            return;
        }
        // Apply revocations before old command or chat queues get another Unity tick.
        PollReload();
        Volatile.Read(ref _session)?.Tick();
    }

    internal static void Publish(ServerManagerEvent value) =>
        Volatile.Read(ref _session)?.Publish(value);

    // Main-thread common-command boundary. Never exposes configured names, URLs,
    // tokens or a raw HTTP endpoint, and never creates an in-game chat/player event.
    internal static ServerManagerCommandResult ExecuteAdminOperation(string operation)
    {
        if (operation != "status" && operation != "test")
            return AdminResult(false, "invalid_command", "Use discord status or discord test.");
        if (ZNet.instance == null || !ZNet.instance.IsServer())
            return AdminResult(false, "server_only", "Discord operations are available only on the server.");
        Session? session = Volatile.Read(ref _session);
        DiscordSettings? settings = _settings;
        bool active = session != null && settings != null &&
            ReferenceEquals(ZNet.instance, _network) && ReferenceEquals(ZNet.World, _world);
        int routes = active ? settings!.WebhookRoutes.Count : 0;
        int testRoutes = active ? settings!.WebhookRoutes.Count(route => route.Events.Contains("server.announcement")) : 0;
        if (operation == "status")
        {
            return AdminResult(true, "discord_status",
                "runtime_active: " + (active ? "true" : "false") +
                "\nbot_enabled: " + (active && settings!.BotEnabled ? "true" : "false") +
                "\nchat_enabled: " + (active && settings!.BotEnabled && settings.ChatRelayChannelCount != 0 ? "true" : "false") +
                "\nchat_channel_count: " + (active ? settings!.ChatRelayChannelCount : 0).ToString(CultureInfo.InvariantCulture) +
                "\nbot_connection: " + (active ? session!.BotConnectionStatus : "inactive") +
                "\nwebhook_worker_active: " + (active && session!.WebhookWorkerActive ? "true" : "false") +
                "\nconfigured_webhook_routes: " + routes.ToString(CultureInfo.InvariantCulture) +
                "\nannouncement_test_routes: " + testRoutes.ToString(CultureInfo.InvariantCulture));
        }
        if (!active)
            return AdminResult(false, "discord_inactive", "The Discord runtime is not active for this server world.");
        if (testRoutes == 0)
            return AdminResult(false, "discord_no_test_route", "No enabled webhook route selects server.announcement; no test was sent.");
        ServerManagerEvent test = new(Guid.NewGuid().ToString("N"), DateTime.UtcNow,
            string.Empty, "server.announcement", ServerManagerEventReliability.Authoritative,
            new ServerManagerActor("servermanager", "ServerManager webhook test", "system"), null!,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = "ServerManager Discord webhook TEST",
                ["message"] = "Explicit operator-requested webhook test. This is not a game announcement or player message.",
                ["test"] = "true"
            });
        return session!.TryPublishTest(test)
            ? AdminResult(true, "discord_test_queued", "Marked webhook test queued through configured server.announcement routes. Queue acceptance does not confirm delivery.")
            : AdminResult(false, "discord_test_not_queued", "No test delivery was queued; the sender may be stopped, disabled, or full.");
    }

    private static ServerManagerCommandResult AdminResult(bool success, string code, string message) =>
        new(success, code, message, string.Empty, new Dictionary<string, string>());

    internal static void Stop()
    {
        Session? session = Interlocked.Exchange(ref _session, null);
        _network = null;
        _world = null;
        _dataRoot = null;
        _settings = null;
        _reloadRead = null; // The bounded reader never writes runtime state itself.
        _candidateText = _processedText = _lastReadError = null;
        _nextReloadPollTimestamp = 0;
        session?.Dispose();
    }

    private static void PollReload()
    {
        Task<ReloadRead>? pending = _reloadRead;
        if (pending != null)
        {
            if (!pending.IsCompleted) return;
            _reloadRead = null;
            ReloadRead result = pending.GetAwaiter().GetResult();
            if (result.Text == null)
            {
                _candidateText = null;
                if (_lastReadError != result.Error)
                    Log(result.Error + " Keeping the active Discord settings; the file was not recreated.");
                _lastReadError = result.Error;
            }
            else
            {
                _lastReadError = null;
                _candidateText = result.Text;
                if (result.Parsed)
                {
                    _processedText = result.Text;
                    if (result.Error != null)
                        Log(result.Error + " Discord reload rejected; keeping all active settings.");
                    else
                        ApplyReload(result.Settings!);
                }
            }
        }

        long now = Stopwatch.GetTimestamp();
        if (now < _nextReloadPollTimestamp || _dataRoot == null) return;
        _nextReloadPollTimestamp = now + ReloadPollTicks;
        string root = _dataRoot;
        string? candidate = _candidateText;
        string? processed = _processedText;
        // Actual content comparison catches atomic editor replacements and edits
        // that preserve length/timestamp. No file I/O or YAML parsing on Unity ticks.
        DiscordSettings? previous = _settings;
        _reloadRead = Task.Run(() => ReadReload(root, candidate, processed, previous));
    }

    private static ReloadRead ReadReload(string root, string? candidate, string? processed, DiscordSettings? previous)
    {
        string text;
        try { text = DiscordSettings.ReadReloadText(root); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return new ReloadRead(null, false, null, ReadError(exception));
        }
        if (text == processed || text != candidate)
            return new ReloadRead(text, false, null, null);
        try { return new ReloadRead(text, true, previous == null ? DiscordSettings.ParseForReload(text) : DiscordSettings.ParseForPartialReload(text, previous), null); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return new ReloadRead(text, true, null, ReadError(exception));
        }
    }

    private static void ApplyReload(DiscordSettings candidate)
    {
        Session? active = Volatile.Read(ref _session);
        DiscordSettings? previous = _settings;
        if (active == null || previous == null) return;
        foreach (string warning in candidate.ReloadWarnings) Log(warning);
        try
        {
            bool botChanged = !previous.HasSameBotSettings(candidate);
            bool webhookChanged = !previous.HasSameWebhookSettings(candidate);
            if (!botChanged && !webhookChanged) return;
            if (!botChanged)
            {
                if (!active.ReloadWebhooks(candidate))
                    throw new InvalidOperationException("Discord session is retiring.");
            }
            else
            {
                // Prepare before retiring the working session. A constructor or
                // scheduling failure must not disable the old configuration.
                Session replacement = new(candidate);
                try
                {
                    replacement.InheritRecentCommandState(active);
                    replacement.Start(reloading: true);
                }
                catch { replacement.RetireForReload(); throw; }
                active.RetireForReload();
                // Admission may have progressed while the replacement workers
                // were prepared. Copy once more after retirement freezes it.
                // The replacement has not had a Unity Tick or executed work yet.
                replacement.InheritRecentCommandState(active);
                replacement.ActivateReloadedBot();
                Volatile.Write(ref _session, replacement);
            }
            _settings = candidate;
            Log(botChanged
                ? "Discord YAML reloaded; the bot connection was refreshed and old pending work discarded. Game world unchanged."
                : "Discord webhook routes reloaded; old queued notifications discarded. Bot connection unchanged.");
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            Log("Discord reload could not be applied (" + exception.GetType().Name + "); keeping the active settings.");
        }
    }

    private static string ReadError(Exception exception) => exception is InvalidDataException
        ? exception.Message // Only our sanitized settings diagnostics use this type.
        : "Discord configuration read failed (" + exception.GetType().Name + ").";

    private sealed class ReloadRead
    {
        internal ReloadRead(string? text, bool parsed, DiscordSettings? settings, string? error)
        { Text = text; Parsed = parsed; Settings = settings; Error = error; }
        internal string? Text { get; }
        internal bool Parsed { get; }
        internal DiscordSettings? Settings { get; }
        internal string? Error { get; }
    }

    private static void Log(string message)
    {
        try { ServerManagerPlugin.Log.LogInfo("[Discord] " + message); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { }
    }

    private sealed class Session : IDisposable
    {
        private readonly CancellationTokenSource _botStop = new();
        private readonly CancellationTokenSource _webhookStop = new();
        private readonly TaskCompletionSource<bool> _reloadActivation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DiscordHttp _webhookHttp = null!;
        private readonly DiscordHttp _botHttp = null!;
        private readonly DiscordWebhooks _webhooks = null!;
        private readonly DiscordCommands? _commands;
        private readonly DiscordGateway? _gateway;
        // Keep only the last retired history while disabled, so off/on cannot
        // reset same-world command/chat deduplication or their rate limits.
        private DiscordCommands? _recentCommands;
        private readonly ConcurrentQueue<ServerManagerEvent> _audit = new();
        private Task _webhookWorker = Task.CompletedTask;
        private Task _gatewayWorker = Task.CompletedTask;
        private int _auditCount;
        private int _disposed;

        internal Session(DiscordSettings settings)
        {
            try
            {
                _webhookHttp = new DiscordHttp(string.Empty, Log);
                _botHttp = new DiscordHttp(settings.BotToken, Log);
                _webhooks = new DiscordWebhooks(settings, _webhookHttp, Log);
                if (settings.BotEnabled)
                {
                    _commands = new DiscordCommands(settings, _botHttp, Log, QueueAudit, _botStop.Token);
                    _gateway = new DiscordGateway(settings.BotToken, _commands.HandleDispatchAsync, Log, _botHttp,
                        receiveMessages: settings.ChatRelayChannelCount != 0);
                }
            }
            catch { Dispose(); throw; }
        }

        internal void Start(bool reloading = false)
        {
            _webhookWorker = Task.Run(() => RunSafely("webhook sender", () => _webhooks.RunAsync(_webhookStop.Token)));
            if (_gateway != null)
                _gatewayWorker = Task.Run(() => RunSafely("bot connection", async () =>
                {
                    if (reloading)
                    {
                        await _reloadActivation.Task.ConfigureAwait(false);
                        // Old Gateway admission is retired before activation.
                        // Its per-instance Identify limiter must not be bypassed
                        // by successive YAML edits that create new Gateways.
                        await Task.Delay(6000, _botStop.Token).ConfigureAwait(false);
                    }
                    await _gateway.RunAsync(_botStop.Token).ConfigureAwait(false);
                }));
            Log("Embedded Discord integration started; no OrbOfDiscord DLL or bot EXE is required.");
        }

        internal void ActivateReloadedBot() => _reloadActivation.TrySetResult(true);

        internal void Tick()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            for (int index = 0; index < 32 && _audit.TryDequeue(out ServerManagerEvent value); index++)
            {
                Interlocked.Decrement(ref _auditCount);
                ServerEventRuntime.RecordDiscordCommand(value);
            }
            _commands?.Tick();
        }

        internal void Publish(ServerManagerEvent value)
        {
            if (Volatile.Read(ref _disposed) == 0)
                _webhooks.Enqueue(value);
        }

        internal bool WebhookWorkerActive => Volatile.Read(ref _disposed) == 0 && !_webhookWorker.IsCompleted;
        internal string BotConnectionStatus => _gateway == null ? "disabled" :
            (Volatile.Read(ref _disposed) != 0 || _gatewayWorker.IsCompleted ? "stopped" : _gateway.ConnectionStatus);

        internal bool TryPublishTest(ServerManagerEvent value) =>
            WebhookWorkerActive && _webhooks.Enqueue(value);

        internal bool ReloadWebhooks(DiscordSettings settings) =>
            Volatile.Read(ref _disposed) == 0 && _webhooks.Reload(settings);

        internal void InheritRecentCommandState(Session previous)
        {
            _webhooks.InheritRecentOperatorState(previous._webhooks);
            DiscordCommands? history = previous._commands ?? previous._recentCommands;
            if (history == null) return;
            if (_commands != null) _commands.InheritRecentState(history);
            else _recentCommands = history;
        }

        private void QueueAudit(ServerManagerEvent value)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (Interlocked.Increment(ref _auditCount) > 128)
            {
                Interlocked.Decrement(ref _auditCount);
                return;
            }
            _audit.Enqueue(value);
        }

        private async Task RunSafely(string component, Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                Log(component + " stopped (" + exception.GetType().Name + "); gameplay is unaffected.");
            }
        }

        public void Dispose() => Retire(drainWebhooks: true);

        internal void RetireForReload() => Retire(drainWebhooks: false);

        private void Retire(bool drainWebhooks)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            // Retire command/chat admission before a new hosted world can appear.
            Cleanup("bot cancellation", _botStop.Cancel);
            _reloadActivation.TrySetCanceled();
            Cleanup("commands", () => _commands?.Dispose());
            Cleanup("Gateway", () => _gateway?.Dispose());
            try
            {
                // A bounded best-effort flush, never an indefinite network wait
                // in the world/character shutdown pipeline.
                if (drainWebhooks)
                    _webhooks?.StopAsync(TimeSpan.FromSeconds(1.5)).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                Log("Discord shutdown flush did not complete; the server will continue shutting down.");
            }
            Cleanup("webhook cancellation", _webhookStop.Cancel);
            Cleanup("webhook queue", () => _webhooks?.Dispose());
            Cleanup("webhook HTTP", () => _webhookHttp?.Dispose());
            Cleanup("bot HTTP", () => _botHttp?.Dispose());
            // Do not dispose the source while asynchronous workers may still be
            // registering cancellation callbacks during their final unwinding.
            _ = Task.WhenAll(_webhookWorker, _gatewayWorker).ContinueWith(
                _ => { _botStop.Dispose(); _webhookStop.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static void Cleanup(string step, Action action)
        {
            try { action(); }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                Log("Discord " + step + " cleanup failed (" + exception.GetType().Name + "); continuing cleanup.");
            }
        }
    }
}
