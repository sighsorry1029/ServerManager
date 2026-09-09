using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ServerManager.Commands;
using ServerManager.Events;

namespace ServerManager.Discord;

/// <summary>
/// Gateway callbacks only validate and acknowledge requests. Game access is confined to Tick.
/// Accepted interactions are never replayed, including after an ambiguous HTTP failure.
/// </summary>
internal sealed class DiscordCommands : IDisposable
{
    private const string Api = "https://discord.com/api/v10";
    private const int Capacity = 64;
    private const int SeenCapacity = 4096;
    private const int ChatCapacity = 32;
    private const int RconMinimumIntervalSeconds = 1;
    private const int MaximumRconOutputCharacters = 1800;
    private const int CommandTimeoutSeconds = 15;
    private const long DiscordEpochMilliseconds = 1420070400000L;
    private static readonly HttpMethod Patch = new("PATCH");
    private static readonly Dictionary<string, CommandSpec> Catalog = BuildCatalog();
    private static readonly string[] Names = Catalog.Keys.ToArray();
    private static readonly HashSet<string> ObsoleteNames = new(StringComparer.Ordinal)
    {
        "sm", "save", "kick", "ban", "unban", "importstatus", "playerinfo", "skilladd", "skillreset",
        // Exact previous slash names only, not aliases or a wildcard cleanup.
        "player-info", "ban-list", "admin-list", "admin-add", "admin-remove",
        "access-list", "access-add", "access-remove", "key-list", "key-add", "key-remove",
        "event-start", "event-stop", "character-list", "character-info", "import-status",
        "skill-get", "skill-set", "skill-add", "skill-reset", "mods-status", "mods-reload",
        "discord-status", "discord-test"
    };
    private readonly DiscordSettings _settings;
    private readonly DiscordHttp _http;
    private readonly Action<string> _log;
    private readonly Action<ServerManagerEvent> _audit;
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _token;
    private readonly object _gate = new();
    private readonly Queue<Pending> _queue = new();
    private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _userRate = new(StringComparer.Ordinal);
    private readonly Queue<long> _globalRate = new();
    private readonly Queue<long> _responseRate = new();
    // Ordinary text never shares command admission, rate budgets, authorization or execution.
    private readonly Queue<ChatPending> _chatQueue = new();
    private readonly Dictionary<string, long> _chatSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _chatUserRate = new(StringComparer.Ordinal);
    private readonly Queue<long> _chatGlobalRate = new();
    // Guild commands have distinct IDs even when their names are identical.
    private readonly Dictionary<string, string> _commandIds = new(StringComparer.Ordinal);
    private readonly DiscordRconCapture? _rcon;
    private ZNet? _network;
    private object? _world;
    private bool _bound;
    private bool _ready;
    private bool _retired;
    private int _disposed;
    private int _inFlight;
    private int _registering;
    private bool _sourceDisposed;
    private string _applicationId = string.Empty;
    private long _lastRconCompleted;
    private bool _hasRconCompleted;
    private bool _rconInFlight;

    internal DiscordCommands(DiscordSettings settings, DiscordHttp http,
        Action<string> log, Action<ServerManagerEvent> audit, CancellationToken ct)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _token = _stop.Token;
        _rcon = new DiscordRconCapture(log);
    }

    public Task HandleDispatchAsync(string eventType, JObject data)
    {
        if (_token.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 || !_settings.BotEnabled)
            return Task.CompletedTask;
        if (eventType == "MESSAGE_CREATE")
        {
            AcceptChat(data);
            return Task.CompletedTask;
        }
        if (eventType == "READY")
        {
            string applicationId = Text(data["application"]?["id"]);
            if (IsSnowflake(applicationId) && _settings.GuildIds.Count != 0)
            {
                lock (_gate)
                {
                    if (_disposed != 0 || _token.IsCancellationRequested) return Task.CompletedTask;
                    _applicationId = applicationId;
                    if (_registering == 0 && _settings.CommandChannelIds.Count != 0)
                    {
                        _registering = 1;
                        _ = Task.Run(() => RegisterAsync(applicationId));
                    }
                }
            }
            return Task.CompletedTask;
        }
        if (eventType != "INTERACTION_CREATE" || (int?)data["type"] != 2)
            return Task.CompletedTask;

        // Copy only validated, bounded values; do not retain Gateway payloads or log tokens.
        Pending? pending;
        try { pending = Parse(data); }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidCastException || exception is FormatException)
        {
            SafeLog("Discord ignored malformed interaction data.");
            return Task.CompletedTask;
        }
        if (pending == null) return Task.CompletedTask;
        lock (_gate)
        {
            if (_disposed != 0 || _token.IsCancellationRequested ||
                pending.ApplicationId != _applicationId || _inFlight >= Capacity)
                return Task.CompletedTask;
            long now = Stopwatch.GetTimestamp();
            while (_responseRate.Count > 0 && (now - _responseRate.Peek()) / (double)Stopwatch.Frequency >= 1)
                _responseRate.Dequeue();
            // Even unauthorized traffic cannot create an unbounded stream of REST acknowledgements.
            if (_responseRate.Count >= 16) return Task.CompletedTask;
            Prune(_seen, now, 900);
            if (_seen.ContainsKey(pending.Id) || _seen.Count >= SeenCapacity)
                return Task.CompletedTask;
            _seen.Add(pending.Id, now);
            _responseRate.Enqueue(now);
            ++_inFlight;
            if (!IsAuthorized(pending.Name, pending.GuildId, pending.ChannelId,
                    pending.UserId))
                pending.Rejection = "이 명령을 실행할 권한이 없습니다.";
            else if (!ValidateArguments(pending.Name, pending.Arguments))
                pending.Rejection = "명령 인수가 올바르지 않습니다.";
            else if (!_commandIds.TryGetValue(CommandKey(pending.GuildId, pending.Name), out string commandId) ||
                     commandId != pending.CommandId)
                pending.Rejection = "명령 등록이 완료되지 않았거나 이전 명령입니다.";
            else if (!AcceptRate(pending.UserId, now))
                pending.Rejection = "명령 요청이 너무 많습니다. 잠시 후 다시 시도하세요.";
            else if (!_ready || _retired)
                pending.Rejection = "게임 서버가 명령을 받을 준비가 되지 않았습니다.";
        }
        // Never keep the receive loop waiting for REST, a Unity frame, or a game operation.
        _ = Task.Run(() => ProcessInteractionAsync(pending));
        return Task.CompletedTask;
    }

    internal bool IsAuthorized(string name, string guildId, string channelId,
        string userId)
    {
        if (!_settings.BotEnabled || !Names.Contains(name) ||
            !IsSnowflake(userId) || !IsSnowflake(guildId) ||
            !_settings.GuildIds.Contains(guildId) ||
            !_settings.CommandChannelIds.Contains(channelId)) return false;
        return _settings.AdminUserIds.Contains(userId);
    }

    // A settings reload must not replay already accepted interactions or reset
    // a live world's RCON cooldown. Never inherit authority, command IDs or work.
    internal void InheritRecentState(DiscordCommands previous)
    {
        lock (previous._gate)
        lock (_gate)
        {
            _seen.Clear();
            _userRate.Clear();
            _globalRate.Clear();
            _responseRate.Clear();
            _chatSeen.Clear();
            _chatUserRate.Clear();
            _chatGlobalRate.Clear();
            foreach (KeyValuePair<string, long> item in previous._seen) _seen[item.Key] = item.Value;
            foreach (KeyValuePair<string, long> item in previous._userRate) _userRate[item.Key] = item.Value;
            foreach (long item in previous._globalRate) _globalRate.Enqueue(item);
            foreach (long item in previous._responseRate) _responseRate.Enqueue(item);
            foreach (KeyValuePair<string, long> item in previous._chatSeen) _chatSeen[item.Key] = item.Value;
            foreach (KeyValuePair<string, long> item in previous._chatUserRate) _chatUserRate[item.Key] = item.Value;
            foreach (long item in previous._chatGlobalRate) _chatGlobalRate.Enqueue(item);
            _lastRconCompleted = previous._lastRconCompleted;
            _hasRconCompleted = previous._hasRconCompleted;
        }
    }

    private static string CommandKey(string guildId, string name) => guildId + ":" + name;

    private async Task RegisterAsync(string applicationId)
    {
        try
        {
            // One Gateway and one execution budget serve all guilds. Register
            // sequentially; one inaccessible guild must not disable the others.
            foreach (string guildId in _settings.GuildIds.OrderBy(id => id, StringComparer.Ordinal))
            {
                _token.ThrowIfCancellationRequested();
                await RegisterGuildAsync(applicationId, guildId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally
        {
            lock (_gate) _registering = 0;
            DisposeSourceIfIdle();
        }
    }

    private async Task RegisterGuildAsync(string applicationId, string guildId)
    {
        try
        {
            string endpoint = Api + "/applications/" + applicationId +
                "/guilds/" + guildId + "/commands";
            JArray? existing = await _http.SendAsync(HttpMethod.Get, endpoint, null,
                true, _token).ConfigureAwait(false) as JArray;
            if (existing == null) throw new InvalidOperationException("Invalid command list.");
            // Reconcile only this adapter's retired names, never bulk-overwrite
            // the application's command collection or touch context menus.
            foreach (JObject obsolete in existing.OfType<JObject>().Where(item =>
                (int?)item["type"] == 1 && ObsoleteNames.Contains(Text(item["name"]))))
            {
                string obsoleteId = Text(obsolete["id"]);
                if (!IsSnowflake(obsoleteId)) continue;
                try
                {
                    await _http.SendAsync(HttpMethod.Delete, endpoint + "/" + obsoleteId,
                        null, true, _token, retry: false).ConfigureAwait(false);
                }
                catch (DiscordHttpException exception) when (exception.StatusCode == 404)
                { /* Already removed: this confirmed absence is not a retry. */ }
            }
            foreach (string name in Names)
            {
                JObject definition = Definition(name);
                JObject? previous = existing.OfType<JObject>().FirstOrDefault(item =>
                    Text(item["name"]) == name && (int?)item["type"] == 1);
                string id = Text(previous?["id"]);
                JObject? response;
                if (previous != null && DefinitionMatches(previous, definition))
                    response = previous;
                else
                {
                    if (IsSnowflake(id)) definition.Remove("type"); // Type cannot change on an edit.
                    try
                    {
                        response = await _http.SendAsync(
                            IsSnowflake(id) ? Patch : HttpMethod.Post,
                            IsSnowflake(id) ? endpoint + "/" + id : endpoint,
                            definition, true, _token, retry: false).ConfigureAwait(false) as JObject;
                    }
                    catch (DiscordHttpException exception) when (IsSnowflake(id) && exception.StatusCode == 404)
                    {
                        // A confirmed missing ID is not an ambiguous mutation. Re-read once to recover
                        // a concurrently deleted/recreated command without deleting unrelated commands.
                        JArray? refreshed = await _http.SendAsync(HttpMethod.Get, endpoint, null,
                            true, _token).ConfigureAwait(false) as JArray;
                        if (refreshed == null) throw new InvalidOperationException("Invalid command list.");
                        JObject? replacement = refreshed.OfType<JObject>().FirstOrDefault(item =>
                            Text(item["name"]) == name && (int?)item["type"] == 1);
                        string replacementId = Text(replacement?["id"]);
                        if (replacement != null && DefinitionMatches(replacement, definition))
                            response = replacement;
                        else
                        {
                            if (!IsSnowflake(replacementId)) definition["type"] = 1;
                            response = await _http.SendAsync(IsSnowflake(replacementId) ? Patch : HttpMethod.Post,
                                IsSnowflake(replacementId) ? endpoint + "/" + replacementId : endpoint,
                                definition, true, _token, retry: false).ConfigureAwait(false) as JObject;
                        }
                    }
                }
                id = Text(response?["id"]);
                if (!IsSnowflake(id)) throw new InvalidOperationException("Invalid command registration.");
                lock (_gate)
                {
                    if (_token.IsCancellationRequested || _disposed != 0) return;
                    _commandIds[CommandKey(guildId, name)] = id;
                }
            }
            SafeLog("Discord guild " + guildId + " slash commands registered; unrelated commands were preserved.");
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // A subsequent READY may read the actual server state and reconcile, never replaying mutations.
            SafeLog("Discord guild " + guildId + " command registration failed: " + exception.GetType().Name);
        }
    }

    private async Task ProcessInteractionAsync(Pending pending)
    {
        bool deferred = false;
        try
        {
            if (pending.Rejection.Length != 0)
            {
                await AcknowledgeAsync(pending, false, pending.Rejection).ConfigureAwait(false);
                Audit(pending, false, "rejected");
                return;
            }
            await AcknowledgeAsync(pending, true, string.Empty).ConfigureAwait(false);
            deferred = true;
            lock (_gate)
            {
                if (_token.IsCancellationRequested || !_ready || _retired ||
                    _disposed != 0 || Stopwatch.GetTimestamp() >= pending.Deadline)
                    pending.Completion.TrySetResult(Result.Fail("server_unavailable",
                        "게임 서버가 종료 중이거나 명령 대기 시간이 초과됐습니다."));
                else if (_queue.Count >= Capacity)
                    pending.Completion.TrySetResult(Result.Fail("queue_full", "명령 대기열이 가득 찼습니다."));
                else
                    _queue.Enqueue(pending);
            }
            int remaining = Math.Max(1, (int)Math.Min(CommandTimeoutSeconds * 1000d,
                (pending.Deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency));
            Task timeout = Task.Delay(remaining, _token);
            if (await Task.WhenAny(pending.Completion.Task, timeout).ConfigureAwait(false) != pending.Completion.Task)
            {
                _token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    pending.Expired = true;
                    pending.Completion.TrySetResult(Result.Fail("timeout", pending.Started
                        ? "응답 시간이 초과됐지만 명령이 실행됐을 수 있습니다. 즉시 재시도하지 말고 서버 상태와 로그를 확인하세요."
                        : "명령 대기 시간이 초과되어 실행하지 않았습니다."));
                }
            }
            Result result = await pending.Completion.Task.ConfigureAwait(false);
            if (!pending.Started) Audit(pending, result.Success, result.Code);
            await _http.SendAsync(Patch, Api + "/webhooks/" + pending.ApplicationId + "/" +
                Uri.EscapeDataString(pending.Token) + "/messages/@original", Message(result.Message),
                false, _token, retry: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // A failed/ambiguous ACK never queues a command; failed result delivery never re-executes it.
            SafeLog("Discord interaction " + pending.Id + (deferred ? " response" : " acknowledgement") +
                " failed: " + exception.GetType().Name);
        }
        finally
        {
            lock (_gate) --_inFlight;
            DisposeSourceIfIdle();
        }
    }

    private async Task AcknowledgeAsync(Pending pending, bool defer, string message)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_token);
        // Discord invalidates unacknowledged interactions after three seconds.
        double ageMs = (Stopwatch.GetTimestamp() - pending.ReceivedAt) * 1000d / Stopwatch.Frequency;
        if (ageMs >= 2500) throw new TimeoutException("Interaction acknowledgement deadline elapsed.");
        deadline.CancelAfter(Math.Max(1, (int)(2500 - ageMs)));
        JObject payload = new() { ["type"] = defer ? 5 : 4 };
        payload["data"] = defer ? new JObject { ["flags"] = 64 } : Message(message, true);
        await _http.SendAsync(HttpMethod.Post, Api + "/interactions/" + pending.Id + "/" +
            Uri.EscapeDataString(pending.Token) + "/callback", payload, false,
            deadline.Token, retry: false).ConfigureAwait(false);
    }

    public void Tick()
    {
        if (_token.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            lock (_gate) _chatQueue.Clear();
            return;
        }
        ZNet? network = ZNet.instance;
        object? world = ZNet.World;
        ServerManagerStatusSnapshot status = ServerManagerIntegrationApi.GetStatus();
        lock (_gate)
        {
            if (!_bound && network != null && network.IsServer() && world != null)
            {
                _network = network;
                _world = world;
                _bound = true;
            }
            if (_bound && (!ReferenceEquals(network, _network) ||
                !ReferenceEquals(world, _world) || network == null || !network.IsServer() || status.ShutdownStarted))
                _retired = true;
            _ready = _bound && !_retired && status.IsInitialized && status.IsServer &&
                status.ServerStarted && status.WorldReady && !status.ShutdownStarted;
            if (!_ready || !_settings.BotEnabled) _chatQueue.Clear();
        }
        for (int i = 0; i < 8; i++)
        {
            Pending pending;
            lock (_gate)
            {
                if (_queue.Count == 0) break;
                pending = _queue.Dequeue();
                // A console command can stop/change the world during this same Tick.
                if (!ReferenceEquals(ZNet.instance, _network) || !ReferenceEquals(ZNet.World, _world) ||
                    ServerManagerIntegrationApi.GetStatus().ShutdownStarted)
                    _retired = true;
                if (_token.IsCancellationRequested || !_ready || _retired || pending.Expired ||
                    Stopwatch.GetTimestamp() >= pending.Deadline)
                {
                    pending.Completion.TrySetResult(Result.Fail("stale_command", "만료되었거나 종료된 서버의 명령은 실행하지 않습니다."));
                    continue;
                }
                pending.Started = true;
            }
            // Async APIs synchronously enqueue on the integration boundary; no network work runs here.
            _ = ExecuteAndCompleteAsync(pending);
        }
        TickChat();
    }

    private void TickChat()
    {
        for (int index = 0; index < 4; index++)
        {
            lock (_gate)
            {
                if (_chatQueue.Count == 0) return;
                ChatPending pending = _chatQueue.Dequeue();
                // Admin work (or a previous broadcast) may have changed the world this frame.
                ServerManagerStatusSnapshot status = ServerManagerIntegrationApi.GetStatus();
                if (!ReferenceEquals(ZNet.instance, _network) || !ReferenceEquals(ZNet.World, _world) ||
                    _network == null || !_network.IsServer() || status.ShutdownStarted)
                    _retired = true;
                _ready = _bound && !_retired && status.IsInitialized && status.IsServer &&
                    status.ServerStarted && status.WorldReady && !status.ShutdownStarted;
                if (_token.IsCancellationRequested || _disposed != 0 || !_ready || !_settings.BotEnabled)
                {
                    _chatQueue.Clear();
                    return;
                }
                if (Stopwatch.GetTimestamp() >= pending.Deadline ||
                    !TryAuthorizeChat(pending.GuildId, pending.ChannelId, pending.UserId, out bool adminChannel)) continue;
                // Recheck admin membership before presenting a message as Admin.
                // This remains literal chat, never console input or a CommandCaller.
                bool sent;
                try { sent = ServerManagerRuntime.TryBroadcastDiscordShout(pending.UserId, pending.UserName, pending.Message, adminChannel); }
                catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                {
                    SafeLog("Discord chat relay failed: " + exception.GetType().Name);
                    _chatQueue.Clear();
                    return;
                }
                if (!sent)
                {
                    _chatQueue.Clear(); // Offline/failed delivery is best-effort, never retried.
                    return;
                }
            }
        }
    }

    private async Task ExecuteAndCompleteAsync(Pending pending)
    {
        Result result;
        try
        {
            _token.ThrowIfCancellationRequested();
            result = await ExecuteAsync(pending).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = Result.Fail("command_failed", "명령 처리 실패: " + exception.GetType().Name);
        }
        pending.Completion.TrySetResult(result);
        // The common executor writes its own identity-preserving execution audit.
        if (!pending.SharedExecution) Audit(pending, result.Success, result.Code);
    }

    private Task<Result> ExecuteAsync(Pending pending)
    {
        if (!IsPendingAuthorized(pending))
            return Task.FromResult(Result.Fail("unauthorized", "만료되었거나 권한이 변경된 명령은 실행하지 않습니다."));
        if (!ValidateArguments(pending.Name, pending.Arguments))
            return Task.FromResult(Result.Fail("invalid_arguments", "명령 인수가 올바르지 않습니다."));
        if (pending.Name == "help") return Task.FromResult(new Result(true, "help", HelpText()));
        if (pending.Name == "rcon") return ExecuteRconAsync(pending);
        return ExecuteSharedAsync(pending, SharedLine(pending.Name, pending.Arguments));
    }

    private bool IsPendingAuthorized(Pending pending)
    {
        lock (_gate)
            return !_token.IsCancellationRequested && _disposed == 0 && _bound && _ready && !_retired &&
                !pending.Expired && Stopwatch.GetTimestamp() < pending.Deadline &&
                pending.ApplicationId == _applicationId &&
                _commandIds.TryGetValue(CommandKey(pending.GuildId, pending.Name), out string commandId) && commandId == pending.CommandId &&
                IsAuthorized(pending.Name, pending.GuildId, pending.ChannelId, pending.UserId);
    }

    private Task<Result> ExecuteSharedAsync(Pending pending, string line)
    {
        // The live callback also protects the second queue when admins, bot lifetime,
        // registration or the interaction deadline change after Discord dispatch.
        pending.SharedExecution = true;
        return IntegrationResultAsync(ServerCommands.ExecuteAsync(line,
            new CommandCaller("discord", pending.UserId, pending.UserName,
                () => IsPendingAuthorized(pending), _token)));
    }

    internal static string SharedLine(string name, IDictionary<string, string> args)
    {
        if (name == "rcon" || !ValidateArguments(name, args))
            throw new ArgumentException("Unsupported command or invalid arguments.", nameof(name));
        CommandSpec spec = Catalog[name];
        if (name == "teleport" && args.TryGetValue("to", out string target))
            return "teleport " + Quote(args["player"]) + " to " + Quote(target);
        string line = spec.Name;
        foreach (OptionSpec option in spec.Options)
            if (args.TryGetValue(option.Name, out string value))
                line += " " + Quote(value);
            else if (name == "giveitem" && option.Name == "quality" && args.ContainsKey("data_id"))
                // The positional common grammar requires quality before a preset ID.
                line += " " + Quote("1");
        return line;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    internal static bool IsSharedRconLine(string line)
    {
        return ServerCommands.IsRconManagedLine(line);
    }

    private async Task<Result> ExecuteRconAsync(Pending pending)
    {
        string line = pending.Arguments["command"];
        if (!IsSharedRconLine(line)) return ExecuteRcon(line);
        // Only ban/unban use the protected common moderation handler. All other
        // text retains its vanilla console meaning, including help/event/heal.
        if (!TryBeginRcon()) return Result.Fail("rcon_cooldown", "RCON 실행 또는 재사용 대기 중입니다.");
        try { return await ExecuteSharedAsync(pending, line).ConfigureAwait(false); }
        finally { CompleteRcon(); }
    }

    private Result ExecuteRcon(string line)
    {
        if (_rcon == null || !_rcon.IsAvailable)
            return Result.Fail("rcon_unavailable", "서버 콘솔 출력 캡처를 사용할 수 없습니다.");
        if (!TryBeginRcon())
            return Result.Fail("rcon_cooldown", "RCON 실행 또는 재사용 대기 중입니다.");
        try
        {
            return _rcon.Execute(line, MaximumRconOutputCharacters);
        }
        finally { CompleteRcon(); }
    }

    private bool TryBeginRcon()
    {
        lock (_gate)
        {
            if (_rconInFlight || (_hasRconCompleted &&
                (Stopwatch.GetTimestamp() - _lastRconCompleted) / (double)Stopwatch.Frequency <
                RconMinimumIntervalSeconds)) return false;
            _rconInFlight = true;
            return true;
        }
    }

    private void CompleteRcon()
    {
        lock (_gate)
        {
            _rconInFlight = false;
            _hasRconCompleted = true;
            _lastRconCompleted = Stopwatch.GetTimestamp();
        }
    }

    private static async Task<Result> IntegrationResultAsync(Task<ServerManagerCommandResult> task)
    {
        ServerManagerCommandResult result = await task.ConfigureAwait(false);
        return FormatIntegrationResult(result);
    }

    internal static Result FormatIntegrationResult(ServerManagerCommandResult result)
    {
        bool savePending = result.Success && (result.Code == "save_requested" || result.Code == "save_scheduled");
        string message = (result.Success ? (savePending ? "요청 접수: " : "성공: ") : "실패: ") +
            result.Message + "\nresult_code: " + result.Code;
        if (savePending) message += "\n저장 완료를 의미하지 않습니다. /status에서 저장 상태와 캐릭터 반영 범위를 확인하세요.";
        if (!string.IsNullOrWhiteSpace(result.OperationId)) message += "\noperation_id: " + result.OperationId;
        foreach (KeyValuePair<string, string> pair in result.Data.Take(16))
            if (pair.Key != "operation_id") message += "\n" + pair.Key + ": " + pair.Value;
        return new Result(result.Success, result.Code, message);
    }

    private static string Clip(string text, int maximum) => text.Length <= maximum ? text : text.Substring(0, maximum - 1) + "…";

    private JObject Message(string message, bool ephemeral = false)
    {
        // Arbitrary RCON output can include configuration; never echo this bot's credential.
        if (!string.IsNullOrEmpty(_settings.BotToken)) message = message.Replace(_settings.BotToken, "[redacted]");
        if (message.Length > 1999) message = message.Substring(0, 1998) + "…";
        JObject result = new() { ["content"] = message,
            ["allowed_mentions"] = new JObject { ["parse"] = new JArray() } };
        if (ephemeral) result["flags"] = 64;
        return result;
    }

    private void Audit(Pending pending, bool success, string code)
    {
        // Raw arguments, output, tokens and exception messages are excluded.
        // Only validated selected IDs are retained for early feature failures.
        try
        {
            string resultCode = code.Length > 0 && code.Length <= 64 &&
                code.All(character => (character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') || character == '_')
                ? code : "unknown";
            string outcome = success ? "succeeded" : "failed";
            // The local audit writer renders actor.Name and message rather than all structured fields.
            // Keep this summary useful in both that file and webhook descriptions, without command inputs.
            string message = "Discord /" + pending.Name + " " + outcome +
                "; result_code=" + resultCode + "; actor_discord_user_id=" + pending.UserId + ".";
            Dictionary<string, string> fields = new(StringComparer.Ordinal)
            {
                ["title"] = "Discord command", ["command"] = pending.Name,
                ["message"] = Clip(message, 256), ["actor_discord_user_id"] = pending.UserId,
                ["request_id"] = pending.Id, ["guild_id"] = pending.GuildId,
                ["channel_id"] = pending.ChannelId, ["success"] = success ? "true" : "false",
                ["result_code"] = resultCode
            };
            if (pending.Name == "characterrestore" && pending.Arguments.TryGetValue("backup_id", out string backupId) &&
                ServerCommands.IsBackupId(backupId))
            {
                fields["backup_id"] = backupId;
                fields["message"] += "; backup_id=" + backupId;
            }
            if (pending.Name == "giveitem" && pending.Arguments.TryGetValue("data_id", out string dataId) &&
                ServerCommands.IsItemDataId(dataId))
            {
                fields["data_id"] = dataId;
                fields["message"] += "; data_id=" + dataId;
            }
            _audit(new ServerManagerEvent(Guid.NewGuid().ToString("N"), DateTime.UtcNow,
                string.Empty, "command.executed", ServerManagerEventReliability.Authoritative,
                new ServerManagerActor(pending.UserId, "Discord user " + pending.UserId, "discord"), null!,
                fields));
        }
        catch (Exception exception) { SafeLog("Discord command audit failed: " + exception.GetType().Name); }
    }

    private void SafeLog(string text) { try { _log(text); } catch { /* Logging must not fault workers. */ } }

    private bool TryAuthorizeChat(string guildId, string channelId, string userId, out bool adminChannel)
    {
        adminChannel = _settings.CommandChannelIds.Contains(channelId);
        if (!_settings.BotEnabled || !_settings.GuildIds.Contains(guildId)) return false;
        // The admin rule wins on overlap: never fall back to public chat for
        // an unlisted user who could otherwise impersonate the Admin label.
        return adminChannel ? _settings.AdminUserIds.Contains(userId) : _settings.ChatChannelIds.Contains(channelId);
    }

    private void AcceptChat(JObject data)
    {
        string id = Text(data["id"]), guild = Text(data["guild_id"]), channel = Text(data["channel_id"]);
        JObject? author = data["author"] as JObject;
        string user = Text(author?["id"]), message = Text(data["content"]);
        JToken? type = data["type"];
        if (!IsSnowflake(id) || !IsSnowflake(guild) || !IsSnowflake(channel) || !IsSnowflake(user) ||
            !TryAuthorizeChat(guild, channel, user, out _) ||
            type?.Type != JTokenType.Integer || (type.ToString() != "0" && type.ToString() != "19") ||
            !MissingOrFalse(author?["bot"]) || !MissingOrFalse(author?["system"]) || !MissingOrFalse(data["system"]) ||
            (data["webhook_id"] != null && data["webhook_id"]!.Type != JTokenType.Null) ||
            message.Length > 500 || string.IsNullOrWhiteSpace(message)) return;
        message = ChatText(message, 500, true);
        if (message.Length == 0) return;

        long created = (long)(ulong.Parse(id, CultureInfo.InvariantCulture) >> 22) + DiscordEpochMilliseconds;
        long utcNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Resume can replay old messages. Permit only fresh text, with small forward clock skew.
        if (created < utcNow - 30000 || created > utcNow + 5000) return;
        string userName = ChatText(Text((data["member"] as JObject)?["nick"]), 80, false);
        if (userName.Length == 0) userName = ChatText(Text(author?["global_name"]), 80, false);
        if (userName.Length == 0) userName = ChatText(Text(author?["username"]), 80, false);
        if (userName.Length == 0) userName = "Discord user " + user;

        lock (_gate)
        {
            if (_disposed != 0 || _token.IsCancellationRequested ||
                !TryAuthorizeChat(guild, channel, user, out _)) return;
            long now = Stopwatch.GetTimestamp();
            Prune(_chatSeen, now, 900);
            if (_chatSeen.ContainsKey(id) || _chatSeen.Count >= SeenCapacity) return;
            _chatSeen.Add(id, now);
            // Remember deliberate drops too: a later reconnect must not convert one into delivery.
            if (!_bound || !_ready || _retired || _chatQueue.Count >= ChatCapacity || !AcceptChatRate(user, now)) return;
            _chatQueue.Enqueue(new ChatPending(guild, channel, user, userName, message,
                now + 10L * Stopwatch.Frequency));
        }
    }

    private bool AcceptChatRate(string userId, long now)
    {
        Prune(_chatUserRate, now, 60);
        while (_chatGlobalRate.Count > 0 && (now - _chatGlobalRate.Peek()) / (double)Stopwatch.Frequency >= 1)
            _chatGlobalRate.Dequeue();
        if (_chatGlobalRate.Count >= 10 || _chatUserRate.Count >= SeenCapacity ||
            (_chatUserRate.TryGetValue(userId, out long previous) && (now - previous) / (double)Stopwatch.Frequency < 2))
            return false;
        _chatUserRate[userId] = now;
        _chatGlobalRate.Enqueue(now);
        return true;
    }

    private static bool MissingOrFalse(JToken? token) => token == null ||
        (token.Type == JTokenType.Boolean && !(bool)token);

    private static string ChatText(string value, int maximum, bool message)
    {
        StringBuilder result = new(maximum);
        for (int index = 0; index < Math.Min(message ? 500 : 256, value.Length) && result.Length < maximum; index++)
        {
            char character = value[index];
            UnicodeCategory category = char.GetUnicodeCategory(character);
            if (character == '\r' || character == '\n' || character == '\t' ||
                category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator)
            {
                if (message) character = ' ';
                else continue;
            }
            if (char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format) continue;
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]) && result.Length < maximum - 1)
                    result.Append(character).Append(value[++index]);
                continue;
            }
            if (char.IsLowSurrogate(character)) continue;
            result.Append(character == '<' ? '＜' : character == '>' ? '＞' : character);
        }
        return result.ToString().Trim();
    }

    private bool AcceptRate(string userId, long now)
    {
        Prune(_userRate, now, 60);
        while (_globalRate.Count > 0 && (now - _globalRate.Peek()) / (double)Stopwatch.Frequency >= 1)
            _globalRate.Dequeue();
        if (_globalRate.Count >= 8 || (_userRate.TryGetValue(userId, out long previous) &&
            (now - previous) / (double)Stopwatch.Frequency < 1) || _userRate.Count >= SeenCapacity) return false;
        _userRate[userId] = now;
        _globalRate.Enqueue(now);
        return true;
    }

    private static void Prune(Dictionary<string, long> entries, long now, int ageSeconds)
    {
        foreach (string key in entries.Where(pair => (now - pair.Value) / (double)Stopwatch.Frequency >= ageSeconds)
                     .Select(pair => pair.Key).ToArray()) entries.Remove(key);
    }

    private Pending? Parse(JObject data)
    {
        string id = Text(data["id"]), token = Text(data["token"]), app = Text(data["application_id"]);
        string name = Text(data["data"]?["name"]), user = Text(data["member"]?["user"]?["id"]);
        if (!IsSnowflake(id) || !IsSnowflake(app) || !IsSnowflake(user) ||
            token.Length < 1 || token.Length > 512 || token.Any(char.IsControl) ||
            !Names.Contains(name) || (int?)data["data"]?["type"] != 1) return null;
        Dictionary<string, string> args = new(StringComparer.Ordinal);
        JToken? options = data["data"]?["options"];
        if (options != null && options.Type != JTokenType.Array) return null;
        foreach (JToken option in options as JArray ?? new JArray())
        {
            string key = Text(option["name"]);
            OptionSpec? spec = Catalog[name].Options.FirstOrDefault(value => value.Name == key);
            if (args.Count >= 5 || spec == null || args.ContainsKey(key) ||
                (int?)option["type"] != spec.Type || !spec.TryRead(option["value"], out string value)) return null;
            args.Add(key, value);
        }
        long received = Stopwatch.GetTimestamp();
        string userName = Text(data["member"]?["nick"]);
        if (string.IsNullOrWhiteSpace(userName)) userName = Text(data["member"]?["user"]?["global_name"]);
        if (string.IsNullOrWhiteSpace(userName)) userName = Text(data["member"]?["user"]?["username"]);
        userName = new string(userName.Where(character => !char.IsControl(character)).Take(80).ToArray()).Trim();
        if (userName.Length == 0) userName = "Discord user " + user;
        return new Pending(id, token, app, Text(data["data"]?["id"]), name,
            Text(data["guild_id"]), Text(data["channel_id"] ?? data["channel"]?["id"]), user,
            userName, args, received, received + (long)CommandTimeoutSeconds * Stopwatch.Frequency);
    }

    internal static bool ValidateArguments(string name, IDictionary<string, string> args)
    {
        if (args == null || !Catalog.TryGetValue(name, out CommandSpec spec) ||
            args.Keys.Any(key => !spec.Options.Any(option => option.Name == key))) return false;
        foreach (OptionSpec option in spec.Options)
        {
            if (!args.TryGetValue(option.Name, out string value))
            { if (option.Required) return false; }
            else if (!option.Valid(value)) return false;
        }
        if (name == "teleport")
            return args.ContainsKey("to")
                ? args.Count == 2
                : args.Count == 4 && args.ContainsKey("x") && args.ContainsKey("y") && args.ContainsKey("z");
        if (name == "rcon")
        {
            string first = args["command"].TrimStart();
            int end = 0;
            while (end < first.Length && !char.IsWhiteSpace(first[end])) ++end;
            // ServerManager features have typed slash commands, not an RCON
            // prefix route. Quoting cannot bypass either prefix check.
            string command = first.Substring(0, end).Trim('"', '\'');
            if (command.Equals("sm", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("sm:", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static JObject Definition(string name)
    {
        CommandSpec spec = Catalog[name];
        return new JObject { ["type"] = 1, ["name"] = name, ["description"] = spec.Description,
            ["options"] = new JArray(spec.Options.Select(option => option.Definition())) };
    }

    private static string HelpText()
    {
        IEnumerable<string> lines = Catalog.Values.Where(spec => spec.Name != "rcon").Select(spec =>
            spec.Name == "teleport" ? "/teleport <player> (to:<player> 또는 x:<n> y:<n> z:<n>)" :
            "/" + spec.Name + string.Concat(spec.Options.Select(option =>
                option.Required ? " <" + option.Name + ">" : " [" + option.Name + "]")));
        return string.Join("\n", lines) + "\n/rcon command:<서버 콘솔 명령>\n" +
            "예: /rcon command:\"save\", /rcon command:\"kick Player\",\n" +
            "/rcon command:\"ban 76561198000000001\", /rcon command:\"unban 76561198000000001\"";
    }

    // One descriptor owns Discord registration, admission validation and the
    // ordered, quoted common-command arguments. No free-form feature dispatcher.
    private static Dictionary<string, CommandSpec> BuildCatalog()
    {
        OptionSpec Player(bool required = true) => StringOption("player", "플레이어 이름 또는 ID", 128, required);
        OptionSpec SteamId() => new("steam-id", "SteamID64 (17자리)", 3, true, 17, rule: "steam");
        OptionSpec Skill(bool required = true) => TokenOption("skill", "스킬 이름", required);
        OptionSpec Coordinate(string name, bool required = true) => NumberOption(name, name + " 좌표", -1000000, 1000000, required);
        CommandSpec[] specs =
        {
            new("status", "서버와 저장 상태를 조회합니다."),
            new("players", "현재 접속자 목록 또는 한 플레이어의 위치를 조회합니다.", Player(false)),
            new("announce", "게임 안에 공지를 표시합니다.", StringOption("message", "공지 내용", 500)),
            new("chat", "게임 전체 채팅으로 메시지를 보냅니다.", StringOption("message", "채팅 내용", 500)),
            new("banlist", "서버 차단 계정 목록을 조회합니다."),
            new("adminlist", "게임 관리자 목록을 조회합니다."),
            new("adminadd", "게임 관리자를 추가합니다.", SteamId()),
            new("adminremove", "게임 관리자를 제거합니다.", SteamId()),
            new("accesslist", "서버 접속 허용 목록을 조회합니다."),
            new("accessadd", "서버 접속 허용 계정을 추가합니다.", SteamId()),
            new("accessremove", "서버 접속 허용 계정을 제거합니다.", SteamId()),
            new("keylist", "월드 전역 키를 조회합니다."),
            new("keyadd", "월드 전역 키를 추가합니다.", TokenOption("key", "전역 키")),
            new("keyremove", "월드 전역 키를 제거합니다.", TokenOption("key", "전역 키")),
            new("eventstart", "지정 좌표에서 이벤트를 시작합니다.", TokenOption("event", "이벤트 이름"), Coordinate("x"), Coordinate("y"), Coordinate("z")),
            new("eventstop", "진행 중인 이벤트를 중지합니다."),
            new("characterlist", "서버 캐릭터 목록을 조회합니다."),
            new("characterinfo", "서버 캐릭터 정보를 조회합니다.", Player()),
            new("characterbackups", "서버 캐릭터 백업 목록을 조회합니다.", Player(), IntegerOption("page", "페이지", 1, 1000, false)),
            new("characterrestore", "오프라인 캐릭터를 백업에서 복원합니다.", Player(),
                new OptionSpec("backup_id", "백업 ID (32자리 소문자 16진수)", 3, true, 32, rule: "backup")),
            new("giveitem", "플레이어에게 아이템을 지급합니다.", Player(), TokenOption("prefab", "아이템 프리팹"),
                IntegerOption("amount", "수량", 1, 1000), IntegerOption("quality", "품질", 1, 100, false),
                new OptionSpec("data_id", "서버 아이템 데이터 프리셋 ID (품질 생략 시 1)", 3, false, 64, rule: "item_data")),
            new("teleport", "플레이어를 좌표 또는 다른 플레이어에게 이동합니다.", Player(),
                StringOption("to", "목적지 플레이어 (좌표와 함께 사용할 수 없음)", 128, false), Coordinate("x", false), Coordinate("y", false), Coordinate("z", false)),
            new("skillget", "플레이어의 스킬을 조회합니다.", Player(), Skill(false)),
            new("skillset", "플레이어의 스킬 레벨을 설정합니다.", Player(), Skill(), NumberOption("value", "스킬 레벨", 0, 100)),
            new("heal", "플레이어의 체력을 회복합니다.", Player(), NumberOption("amount", "회복량", 0.001, 100000)),
            new("damage", "플레이어에게 피해를 줍니다.", Player(), NumberOption("amount", "피해량", 0.001, 100000)),
            new("modsstatus", "모드 무결성 상태를 조회합니다."),
            new("modsreload", "모드 무결성 설정을 다시 읽습니다."),
            new("discordstatus", "Discord 연결 상태를 조회합니다."),
            new("discordtest", "Discord 알림 전송을 테스트합니다."),
            new("cronstatus", "예약 실행 상태와 확인이 필요한 작업을 조회합니다."),
            new("cronack", "중단된 예약을 재실행하지 않고 확인 처리합니다.", StringOption("job", "cronstatus에 표시된 작업 ID", 64)),
            new("help", "지원되는 관리 기능의 도움말을 조회합니다."),
            new("rcon", "서버 콘솔 명령을 실행합니다.", StringOption("command", "서버 콘솔 명령 (예: save, kick, ban, unban)", 1000))
        };
        if (!new HashSet<string>(specs.Where(spec => spec.Name != "rcon").Select(spec => spec.Name),
                StringComparer.Ordinal).SetEquals(ServerCommands.FlatCommandNames))
            throw new InvalidOperationException("Discord command names must match the shared flat command catalog.");
        return specs.ToDictionary(spec => spec.Name, StringComparer.Ordinal);
    }

    private static OptionSpec StringOption(string name, string description, int maximum, bool required = true) =>
        new(name, description, 3, required, maximum);
    private static OptionSpec TokenOption(string name, string description, bool required = true) =>
        new(name, description, 3, required, 128, rule: "token");
    private static OptionSpec IntegerOption(string name, string description, double minimum, double maximum, bool required = true) =>
        new(name, description, 4, required, 64, minimum, maximum);
    private static OptionSpec NumberOption(string name, string description, double minimum, double maximum, bool required = true) =>
        new(name, description, 10, required, 64, minimum, maximum);

    private sealed class CommandSpec
    {
        internal CommandSpec(string name, string description, params OptionSpec[] options)
        { Name = name; Description = description; Options = options; }
        internal readonly string Name, Description;
        internal readonly OptionSpec[] Options;
    }

    private sealed class OptionSpec
    {
        internal OptionSpec(string name, string description, int type, bool required,
            int maximumLength, double minimum = 0, double maximum = 0, string rule = "")
        { Name = name; Description = description; Type = type; Required = required;
            MaximumLength = maximumLength; Minimum = minimum; Maximum = maximum; Rule = rule; }
        internal readonly string Name, Description, Rule;
        internal readonly int Type, MaximumLength;
        internal readonly bool Required;
        internal readonly double Minimum, Maximum;
        internal JObject Definition()
        {
            JObject definition = new() { ["type"] = Type, ["name"] = Name,
                ["description"] = Description, ["required"] = Required };
            if (Type == 3) { definition["min_length"] = Rule == "backup" ? 32 : 1; definition["max_length"] = MaximumLength; }
            else { definition["min_value"] = Minimum; definition["max_value"] = Maximum; }
            return definition;
        }
        internal bool Valid(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength || value.Any(char.IsControl)) return false;
            if (Type == 4)
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int integer) && integer >= Minimum && integer <= Maximum;
            if (Type == 10)
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) &&
                    !double.IsNaN(number) && !double.IsInfinity(number) && number >= Minimum && number <= Maximum;
            if (Rule == "steam") return value.Length == 17 && IsSnowflake(value);
            if (Rule == "backup") return ServerCommands.IsBackupId(value);
            if (Rule == "item_data") return ServerCommands.IsItemDataId(value);
            if (Rule == "token") return value.All(character => char.IsLetterOrDigit(character) || character == '_' || character == '-' || character == '.');
            return true;
        }
        internal bool TryRead(JToken? token, out string value)
        {
            value = string.Empty;
            if (token == null || (Type == 3 && token.Type != JTokenType.String) ||
                (Type == 4 && token.Type != JTokenType.Integer) ||
                (Type == 10 && token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
            value = Type == 3 ? Text(token) : token.ToString(Newtonsoft.Json.Formatting.None);
            return Valid(value);
        }
    }

    private static bool DefinitionMatches(JObject existing, JObject expected) =>
        Text(existing["description"]) == Text(expected["description"]) &&
        JToken.DeepEquals(existing["options"] ?? new JArray(), expected["options"]);
    private static string Text(JToken? token) => token?.Type == JTokenType.String ? (string)token! : string.Empty;
    private static bool IsSnowflake(string text) => text.Length >= 1 && text.Length <= 20 &&
        text[0] != '0' && text.All(character => character >= '0' && character <= '9') &&
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        lock (_gate)
        {
            _ready = false;
            _retired = true;
            _chatQueue.Clear();
            while (_queue.Count != 0)
                _queue.Dequeue().Completion.TrySetResult(Result.Fail("shutdown", "봇이 종료 중입니다."));
        }
        _rcon?.Dispose();
        DisposeSourceIfIdle();
    }

    private void DisposeSourceIfIdle()
    {
        lock (_gate)
        {
            // Mono workers may still register cancellation callbacks while unwinding an acknowledgement.
            if (_disposed != 0 && _inFlight == 0 && _registering == 0 && !_sourceDisposed)
            {
                _sourceDisposed = true;
                _stop.Dispose();
            }
        }
    }

    internal sealed class Result
    {
        internal Result(bool success, string code, string message) { Success = success; Code = code; Message = message; }
        internal bool Success { get; }
        internal string Code { get; }
        internal string Message { get; }
        internal static Result Ok(string message) => new(true, "ok", message);
        internal static Result Fail(string code, string message) => new(false, code, message);
    }

    private sealed class ChatPending
    {
        internal ChatPending(string guildId, string channelId, string userId, string userName, string message, long deadline)
        { GuildId = guildId; ChannelId = channelId; UserId = userId; UserName = userName; Message = message; Deadline = deadline; }
        internal readonly string GuildId, ChannelId, UserId, UserName, Message;
        internal readonly long Deadline;
    }

    private sealed class Pending
    {
        internal Pending(string id, string token, string applicationId, string commandId,
            string name, string guildId, string channelId, string userId, string userName,
            Dictionary<string, string> arguments, long receivedAt, long deadline)
        {
            Id = id; Token = token; ApplicationId = applicationId; CommandId = commandId;
            Name = name; GuildId = guildId; ChannelId = channelId; UserId = userId;
            UserName = userName;
            Arguments = arguments; ReceivedAt = receivedAt; Deadline = deadline;
        }
        internal readonly string Id, Token, ApplicationId, CommandId, Name, GuildId, ChannelId, UserId, UserName;
        internal readonly Dictionary<string, string> Arguments;
        internal readonly long ReceivedAt, Deadline;
        internal readonly TaskCompletionSource<Result> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Rejection = string.Empty;
        internal bool Started, Expired, SharedExecution;
    }
}
