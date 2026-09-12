#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServerManager.Events
{
    internal enum ServerEventCommandKind
    {
        Save = 1,
        Announce = 2,
        Kick = 3,
        Ban = 4,
        Unban = 5
    }

    internal static class ServerEventRuntime
    {
        private const int MaximumQueuedCommands = 64;
        private const int MaximumCommandsPerTick = 8;
        private const int MaximumReportsPerWindow = 256;
        private const int MaximumServerReportsPerWindow = 2048;
        private static readonly TimeSpan ReportWindow = TimeSpan.FromSeconds(10);
        private static readonly object CommandGate = new();
        private static readonly Queue<QueuedCommand> Commands = new();
        private static readonly Dictionary<ZRpc, EventPeerState> Peers = new();
        private static readonly HashSet<string> SeenBossDeaths =
            new(StringComparer.Ordinal);
        private static readonly EventLogWriter LogWriter = new();
        private const int MaximumCharacterObservationKeys = 2048;
        private const int MaximumCharacterObservationsPerCall = 16;
        private static readonly long CharacterObservationCooldownTicks =
            (long)(Stopwatch.Frequency * TimeSpan.FromMinutes(5).TotalSeconds);
        private static readonly object CharacterAuditGate = new();
        private static readonly Dictionary<string, long> CharacterObservationTimes =
            new(StringComparer.Ordinal);
        private static readonly RateWindow ServerReports = new();
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly FieldInfo ServerNameField = typeof(ZNet).GetField(
            "m_ServerName",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo BannedListField = typeof(ZNet).GetField(
            "m_bannedList",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly long RaidPollTicks = Stopwatch.Frequency;
        private static readonly TimeSpan WorldSaveCompletionTimeout =
            TimeSpan.FromMinutes(5);

        private static volatile ServerManagerStatusSnapshot _status =
            UnavailableStatus();
        private static bool _initialized;
        private static bool _serverStarted;
        private static bool _acceptCommands;
        private static bool _worldReady;
        private static bool _shutdownStarted;
        private static bool _saveSubscribed;
        private static long _commandWorldEpoch;
        private static string _serverId = string.Empty;
        private static string _activeRaid;
        private static RandomEvent _activeRaidInstance;
        private static string _activeRaidCoordinates;
        private static long _nextRaidPollTimestamp;
        private static ServerManagerSaveOperationSnapshot _latestSave;
        private static EventPeerState _listenHostState;

        /// <summary>
        /// Delivers the exact death event accepted from an authenticated remote
        /// session to internal server-side consumers. The RPC is the connection
        /// identity: consumers must use it to resolve their own ready-session
        /// state instead of trusting names carried by the client report.
        /// </summary>
        internal static event Action<ZRpc, ServerManagerEvent>
            AuthenticatedPlayerDeathPublished;

        internal static void Initialize()
        {
            SetCommandAdmission(false);
            _initialized = true;
            _serverStarted = false;
            _worldReady = false;
            _shutdownStarted = false;
            ResetRaidState();
            _serverId = string.Empty;
            _latestSave = null;
            ResetObservationState();
            lock (CommandGate)
            {
                while (Commands.Count != 0)
                {
                    Commands.Dequeue().Complete(Failure(
                        "runtime_reset",
                        "ServerManager event integration restarted."));
                }
            }

            try
            {
                ServerManagerIntegrationApi.StartDispatcher();
                RefreshStatus();
            }
            catch
            {
                _initialized = false;
                _status = UnavailableStatus();
                TryStopDispatcher("initialization rollback");
                throw;
            }
        }

        internal static void Tick()
        {
            if (!_initialized)
            {
                return;
            }

            ProcessCommands();
            ExpireWorldSaveOperation();
            PollRaid();
        }

        internal static void Shutdown()
        {
            SetCommandAdmission(false);
            if (!_initialized)
            {
                ResetRaidState();
                // Also retire a dispatcher left by an interrupted startup.
                RunShutdownStep("clan chat integration", ClanChatIntegration.Stop);
                RunShutdownStep("Discord integration", Discord.DiscordRuntime.Stop);
                TryStopDispatcher("inactive runtime cleanup");
                return;
            }

            try
            {
                RunShutdownStep("server shutdown event", OnServerShutdown);
                RunShutdownStep("Discord integration", Discord.DiscordRuntime.Stop);
                RunShutdownStep("clan chat integration", ClanChatIntegration.Stop);
                RunShutdownStep("world-save unsubscription", UnsubscribeWorldSave);
                RunShutdownStep("event log", LogWriter.Stop);
                RunShutdownStep("event overlay", ServerEventOverlay.Shutdown);
                RunShutdownStep(
                    "event peer state",
                    ResetObservationState);
                RunShutdownStep(
                    "queued integration commands",
                    () =>
                    {
                        lock (CommandGate)
                        {
                            while (Commands.Count != 0)
                            {
                                Commands.Dequeue().Complete(Failure(
                                    "runtime_stopped",
                                    "ServerManager event integration stopped."));
                            }
                        }
                    });
            }
            finally
            {
                _initialized = false;
                ResetRaidState();
                try
                {
                    try
                    {
                        RefreshStatus();
                    }
                    catch (Exception exception) when (
                        !IntegrityCanonical.IsFatal(exception))
                    {
                        _status = UnavailableStatus();
                        ServerManagerPlugin.Log.LogWarning(
                            "Server event status cleanup failed: " +
                            exception.Message);
                    }
                }
                finally
                {
                    TryStopDispatcher("runtime shutdown");
                }
            }
        }

        private static void ResetObservationState()
        {
            Peers.Clear();
            SeenBossDeaths.Clear();
            ServerReports.Clear();
            ClearCharacterAuditState();
            _listenHostState = null;
        }

        private static void RunShutdownStep(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                try
                {
                    ServerManagerPlugin.Log.LogWarning(
                        "Server event " + name + " cleanup failed: " +
                        exception.Message);
                }
                catch (Exception loggingException) when (
                    !IntegrityCanonical.IsFatal(loggingException))
                {
                    // A broken log sink must not skip later shutdown steps.
                }
            }
        }

        private static void TryStopDispatcher(string context)
        {
            try
            {
                ServerManagerIntegrationApi.StopDispatcher();
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogWarning(
                    "ServerManager integration dispatcher " + context +
                    " failed: " + exception.Message);
            }
        }

        internal static void OnServerStarted(ZNet server)
        {
            if (!_initialized || server == null || !server.IsServer() ||
                (_serverStarted && !_shutdownStarted))
            {
                return;
            }

            // A listen host may stop one world and start another without the
            // plugin being recreated. Retire any previous world's resources
            // before binding the writer and optional bridge to the new world.
            if (_shutdownStarted)
            {
                RunShutdownStep("previous-world clan chat integration", ClanChatIntegration.Stop);
                RunShutdownStep("previous-world event log", LogWriter.Stop);
            }

            _serverStarted = false;
            _worldReady = false;
            _shutdownStarted = false;
            ResetRaidState();
            _latestSave = null;
            ResetObservationState();
            _serverId = CreateServerId();
            _serverStarted = true;
            SetCommandAdmission(true);
            try
            {
                LogWriter.Start(ServerManagerPlugin.DataRoot);
                ClanChatIntegration.Start();
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Server event log initialization failed: " +
                    exception.Message);
            }

            if (!_saveSubscribed)
            {
                ZNet.WorldSaveFinished += OnWorldSaveFinished;
                _saveSubscribed = true;
            }

            Discord.DiscordRuntime.Start();

            Publish(
                ServerManagerEventKinds.ServerStarted,
                ServerManagerEventReliability.Authoritative,
                null,
                null,
                Fields(
                    "title", "Server started",
                    "message", "The Valheim server process started.",
                    "server_name", ServerName()));
            RefreshStatus();
        }

        internal static void OnWorldReady()
        {
            ZNet server = ZNet.instance;
            if (!_initialized || server == null || !server.IsServer() ||
                _worldReady)
            {
                return;
            }

            _worldReady = true;
            _serverId = CreateServerId();
            Publish(
                ServerManagerEventKinds.ServerReady,
                ServerManagerEventReliability.Authoritative,
                null,
                null,
                Fields(
                    "title", "Server ready",
                    "message", "The world is loaded and ready for players.",
                    "world", WorldName()));
            RefreshStatus();
        }

        internal static string OnWorldSaveStarted()
        {
            ZNet server = ZNet.instance;
            if (!_initialized || server == null || !server.IsServer())
            {
                return string.Empty;
            }

            string operationId = Guid.NewGuid().ToString("N");
            _latestSave = new ServerManagerSaveOperationSnapshot(
                operationId,
                ServerManagerSaveState.InProgress,
                DateTime.UtcNow,
                null,
                string.Empty);
            RefreshStatus();
            return operationId;
        }

        internal static void OnWorldSaveCheckpointCompleted(
            string operationId,
            ServerManagerCharacterCommitScope characterCommitScope,
            int capturedCharacterCount,
            int persistedCharacterCount,
            int pendingCharacterCount,
            string warning)
        {
            operationId = SafeToken(operationId, 64);
            if (operationId.Length == 0)
            {
                return;
            }

            capturedCharacterCount = Math.Max(0, capturedCharacterCount);
            persistedCharacterCount = Math.Max(0, persistedCharacterCount);
            pendingCharacterCount = Math.Max(0, pendingCharacterCount);
            warning = Safe(warning, 512);
            if (characterCommitScope !=
                    ServerManagerCharacterCommitScope.AllRetainedShadowsAtCutoff &&
                characterCommitScope !=
                    ServerManagerCharacterCommitScope
                        .PartialRetainedShadowsAtCutoff)
            {
                characterCommitScope =
                    ServerManagerCharacterCommitScope.NotIncluded;
            }

            string scope = CharacterCommitScopeToken(characterCommitScope);
            ServerManagerSaveState completedState = characterCommitScope ==
                ServerManagerCharacterCommitScope.AllRetainedShadowsAtCutoff
                ? ServerManagerSaveState.CheckpointCompleted
                : ServerManagerSaveState.WorldDiskCompleted;
            DateTime completed = DateTime.UtcNow;
            ServerManagerSaveOperationSnapshot active = _latestSave;
            if (active != null && string.Equals(
                    active.OperationId,
                    operationId,
                    StringComparison.Ordinal))
            {
                _latestSave = new ServerManagerSaveOperationSnapshot(
                    operationId,
                    completedState,
                    active.StartedAtUtc,
                    completed,
                    string.Empty,
                    characterCommitScope,
                    capturedCharacterCount,
                    persistedCharacterCount,
                    pendingCharacterCount);
                RefreshStatus();
            }

            string message;
            string completionScope;
            if (characterCommitScope ==
                ServerManagerCharacterCommitScope.AllRetainedShadowsAtCutoff)
            {
                message =
                    "The Valheim world disk save and retained-character " +
                    "checkpoint completed.";
                completionScope = "world_disk_and_retained_characters";
            }
            else if (characterCommitScope ==
                     ServerManagerCharacterCommitScope
                         .PartialRetainedShadowsAtCutoff)
            {
                message =
                    "The Valheim world disk save completed; one or more " +
                    "character snapshots remain pending for an isolated retry.";
                completionScope =
                    "world_disk_with_partial_retained_characters";
            }
            else
            {
                message =
                    "The Valheim world disk save completed without a " +
                    "character checkpoint.";
                completionScope = "world_disk_only";
            }

            Publish(
                ServerManagerEventKinds.ServerSaved,
                ServerManagerEventReliability.Authoritative,
                null,
                null,
                Fields(
                    "title", "World saved",
                    "message", message,
                    "operation_id", operationId,
                    "completion_scope", completionScope,
                    "character_commit_scope", scope,
                    "captured_character_count",
                    capturedCharacterCount.ToString(CultureInfo.InvariantCulture),
                    "persisted_character_count",
                    persistedCharacterCount.ToString(CultureInfo.InvariantCulture),
                    "pending_character_count",
                    pendingCharacterCount.ToString(CultureInfo.InvariantCulture),
                    "warning", warning),
                completed);
        }

        internal static void OnWorldSaveCheckpointFailed(
            string operationId,
            Exception exception)
        {
            OnWorldSaveCheckpointFailed(operationId, exception?.Message);
        }

        internal static void OnWorldSaveCheckpointFailed(
            string operationId,
            string error)
        {
            operationId = SafeToken(operationId, 64);
            ServerManagerSaveOperationSnapshot active = _latestSave;
            if (operationId.Length == 0 || active == null ||
                !string.Equals(
                    active.OperationId,
                    operationId,
                    StringComparison.Ordinal))
            {
                return;
            }

            _latestSave = new ServerManagerSaveOperationSnapshot(
                operationId,
                ServerManagerSaveState.Failed,
                active.StartedAtUtc,
                DateTime.UtcNow,
                Safe(error, 512));
            RefreshStatus();
        }

        private static void OnWorldSaveFinished()
        {
            // Valheim raises this callback after its worker exits even when the
            // worker caught an internal save exception. Only the instrumented
            // worker result is authoritative.
            ServerManagerRuntime.ProcessCompletedWorldSaveCheckpoints();
        }

        internal static void OnServerShutdown()
        {
            ZNet server = ZNet.instance;
            if (!_initialized || !_serverStarted || _shutdownStarted ||
                server == null || !server.IsServer())
            {
                return;
            }

            _shutdownStarted = true;
            ServerScheduleRuntime.Stop();
            SetCommandAdmission(false);
            try
            {
                Publish(
                    ServerManagerEventKinds.ServerShutdown,
                    ServerManagerEventReliability.Authoritative,
                    null,
                    null,
                    Fields(
                        "title", "Server shutdown",
                        "message", "The Valheim server is shutting down."));
            }
            finally
            {
                // Callers reach this only after successful StopAll and the
                // final world/character checkpoint drains (or the equivalent
                // full-runtime drain). The final saved and shutdown records
                // are therefore queued before the writer is stopped.
                _serverStarted = false;
                _worldReady = false;
                ResetRaidState();
                ClearCharacterAuditState();
                RunShutdownStep("Discord integration", Discord.DiscordRuntime.Stop);
                RunShutdownStep("clan chat integration", ClanChatIntegration.Stop);
                RunShutdownStep("world-save unsubscription", UnsubscribeWorldSave);
                RunShutdownStep("event log", LogWriter.Stop);
                RunShutdownStep("world shutdown status", RefreshStatus);
            }
        }

        internal static void OnPlayerReady(
            ZRpc rpc,
            ServerPeerIdentity identity,
            CharacterSession characterSession,
            bool firstJoin)
        {
            if (!_initialized || rpc == null || identity == null ||
                !identity.HasAuthenticatedIdentity)
            {
                return;
            }

            string accountId = characterSession?.Identity?.AccountId;
            if (string.IsNullOrWhiteSpace(accountId))
            {
                accountId = identity.HostId;
            }

            string playerName = characterSession?.Identity?.CharacterName;
            if (string.IsNullOrWhiteSpace(playerName))
            {
                playerName = identity.PlayerName;
            }

            ServerManagerActor actor = Actor(
                accountId,
                playerName,
                "player").WithPlayerAccount(
                    CharacterSteamIdentity.TryParseCanonicalAccountId(accountId, out _)
                        ? accountId : CharacterSteamIdentity.AccountPrefix + identity.HostId);
            if (Peers.TryGetValue(rpc, out EventPeerState existing) &&
                existing.Ready)
            {
                return;
            }

            EventPeerState state = existing ?? new EventPeerState();
            state.Ready = true;
            state.Rpc = rpc;
            state.AccountId = accountId;
            state.TransportId = identity.HostId;
            state.PlayerName = Safe(playerName, 96);
            state.Actor = actor;
            state.PeerUid = identity.PeerUid;
            state.IsAdmin = ZNet.instance != null &&
                            ZNet.instance.IsAdmin(identity.HostId);
            Peers[rpc] = state;

            DateTime now = DateTime.UtcNow;
            if (firstJoin)
            {
                Publish(
                    ServerManagerEventKinds.PlayerFirstJoin,
                    ServerManagerEventReliability.Authoritative,
                    actor,
                    null,
                    Fields(
                        "title", "First join",
                        "message", state.PlayerName +
                                   " joined this world for the first time.",
                        "player_name", state.PlayerName),
                    now);
            }

            Publish(
                ServerManagerEventKinds.PlayerLogin,
                ServerManagerEventReliability.Authoritative,
                actor,
                null,
                Fields(
                    "title", "Player login",
                    "message", state.PlayerName + " joined the server.",
                    "player_name", state.PlayerName,
                    "first_join", firstJoin ? "true" : "false"),
                now);
            RefreshStatus();
        }

        internal static void OnPeerDisconnected(ZRpc rpc)
        {
            if (!_initialized || rpc == null ||
                !Peers.TryGetValue(rpc, out EventPeerState state))
            {
                return;
            }

            Peers.Remove(rpc);
            if (!_shutdownStarted && state.Ready && state.Actor != null)
            {
                Publish(
                    ServerManagerEventKinds.PlayerLeave,
                    ServerManagerEventReliability.Authoritative,
                    state.Actor,
                    null,
                    Fields(
                        "title", "Player left",
                        "message", state.PlayerName + " left the server.",
                        "player_name", state.PlayerName));
            }

            RefreshStatus();
        }

        internal static bool TryProcessClientReport(
            ZRpc rpc,
            EventClientReport report,
            out string safeError)
        {
            safeError = string.Empty;
            if (!_initialized || rpc == null || report == null ||
                !Peers.TryGetValue(rpc, out EventPeerState state) ||
                !state.Ready || state.Actor == null)
            {
                safeError = "The event report arrived before player readiness.";
                return false;
            }

            uint expected = state.LastReportSequence + 1;
            if (expected == 0 || report.Sequence != expected)
            {
                safeError = "The event report sequence was invalid.";
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (!state.Reports.TryConsume(
                    MaximumReportsPerWindow,
                    ReportWindow,
                    now))
            {
                safeError = "The event report rate exceeded the session limit.";
                return false;
            }

            state.LastReportSequence = report.Sequence;
            // Server-wide overload drops reports without blaming an otherwise
            // valid session. Consume its sequence so the next packet remains valid.
            if (ServerReports.TryConsume(
                    MaximumServerReportsPerWindow,
                    ReportWindow,
                    now))
            {
                ProcessReport(state, report, now);
            }

            return true;
        }

        internal static void ProcessListenHostReport(EventClientReport report)
        {
            if (!_initialized || report == null || ZNet.instance == null ||
                !ZNet.instance.IsServer() || Player.m_localPlayer == null)
            {
                return;
            }

            EventPeerState state = _listenHostState ??= new EventPeerState
            {
                Ready = true,
                AccountId = "listen-host"
            };
            state.PlayerName = Safe(Player.m_localPlayer.GetPlayerName(), 96);
            state.Actor = Actor("listen-host", state.PlayerName, "player")
                .WithPlayerAccount(GetListenHostPlayerAccount());
            DateTime now = DateTime.UtcNow;
            if (state.Reports.TryConsume(
                    MaximumReportsPerWindow,
                    ReportWindow,
                    now) &&
                ServerReports.TryConsume(
                    MaximumServerReportsPerWindow,
                    ReportWindow,
                    now))
            {
                ProcessReport(state, report, now);
            }
        }

        private static string GetListenHostPlayerAccount()
        {
            try
            {
                ZNet server = ZNet.instance;
                if (server == null || !server.IsServer() || server.IsDedicated() ||
                    ZNet.m_onlineBackend != OnlineBackendType.Steamworks) return string.Empty;
                string account = CharacterSteamIdentity.AccountPrefix +
                    Steamworks.SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture);
                return CharacterSteamIdentity.TryParseCanonicalAccountId(account, out _)
                    ? account : string.Empty;
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Records a chat message already accepted by the optional Clan server
        /// integration after authentication, membership, text and rate checks.
        /// This is not a client RPC and must never forward private chat to adapters.
        /// </summary>
        internal static void RecordClanChat(
            string platformId,
            long characterPlayerId,
            string playerName,
            string clanId,
            string clanName,
            string message)
        {
            ZNet server = ZNet.instance;
            if (!_initialized || !_serverStarted || _shutdownStarted ||
                server == null || !server.IsServer() || characterPlayerId == 0)
            {
                return;
            }

            string account = BoundedChatText(platformId, 128);
            string player = BoundedChatText(playerName, 96);
            string clan = BoundedChatText(clanId, 128);
            string name = BoundedChatText(clanName, 96);
            string text = BoundedChatText(message, 500);
            if (account.Length == 0 || player.Length == 0 || clan.Length == 0 ||
                name.Length == 0 || text.Length == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!ServerReports.TryConsume(
                    MaximumServerReportsPerWindow,
                    ReportWindow,
                    now))
            {
                return;
            }

            Publish(
                ServerManagerEventKinds.ChatClan,
                ServerManagerEventReliability.Observed,
                Actor(account, player, "player").WithPlayerAccount(
                    CharacterSteamIdentity.TryParseCanonicalAccountId(account, out _)
                        ? account : CharacterSteamIdentity.AccountPrefix + account),
                null,
                Fields(
                    "title", "Clan",
                    "message", player + " [" + name + "]: " + text,
                    "player_name", player,
                    "character_player_id", characterPlayerId.ToString(
                        CultureInfo.InvariantCulture),
                    "clan_id", clan,
                    "clan_name", name,
                    "text", text),
                now);
        }

        internal static void ProcessAuthoritativeServerBossDeath(
            EventClientReport report)
        {
            if (!_initialized || report == null ||
                report.Kind != EventClientReportKind.BossKilled ||
                ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            string attacker = Safe(report.FinalAttacker, 96, "Server");
            EventPeerState state = new()
            {
                Ready = true,
                AccountId = "server",
                PlayerName = attacker,
                Actor = new ServerManagerActor(
                    string.Equals(attacker, "Server", StringComparison.Ordinal)
                        ? _serverId
                        : string.Empty,
                    attacker,
                    "player")
            };
            PublishBoss(
                state,
                report,
                DateTime.UtcNow,
                ServerManagerEventReliability.Authoritative);
        }

        private static void ProcessReport(
            EventPeerState state,
            EventClientReport report,
            DateTime now)
        {
            switch (report.Kind)
            {
                case EventClientReportKind.Shout:
                case EventClientReportKind.Normal:
                case EventClientReportKind.Whisper:
                    if (!state.Chats.TryConsume(32, ReportWindow, now) ||
                        (report.Kind == EventClientReportKind.Shout &&
                         !state.Shouts.TryConsume(8, ReportWindow, now)))
                    {
                        return;
                    }

                    string text = Safe(report.Text, 500);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }

                    bool isShout = report.Kind == EventClientReportKind.Shout;
                    bool isWhisper = report.Kind == EventClientReportKind.Whisper;
                    string chatKind = isShout
                        ? ServerManagerEventKinds.ChatShout
                        : isWhisper
                            ? ServerManagerEventKinds.ChatWhisper
                            : ServerManagerEventKinds.ChatNormal;
                    Dictionary<string, string> chatFields = Fields(
                        "title", isShout ? "Shout" : isWhisper ? "Whisper" : "Normal",
                        "message", state.PlayerName + ": " + text,
                        "player_name", state.PlayerName,
                        "text", text);
                    if (isShout)
                    {
                        chatFields["shout"] = text;
                    }

                    Publish(
                        chatKind,
                        ServerManagerEventReliability.ClientReported,
                        state.Actor,
                        null,
                        chatFields,
                        now);
                    break;

                case EventClientReportKind.Death:
                    if (!state.Deaths.TryConsume(4, TimeSpan.FromSeconds(20), now) ||
                        now - state.LastDeathUtc < TimeSpan.FromSeconds(3))
                    {
                        return;
                    }

                    state.LastDeathUtc = now;
                    PublishDeath(state, report, now);
                    break;

                case EventClientReportKind.BossKilled:
                    if (!state.Bosses.TryConsume(4, TimeSpan.FromMinutes(1), now))
                    {
                        return;
                    }

                    PublishBoss(
                        state,
                        report,
                        now,
                        ServerManagerEventReliability.ClientReported);
                    break;
            }
        }

        private static void PublishDeath(
            EventPeerState state,
            EventClientReport report,
            DateTime now)
        {
            string cause = SafeToken(report.Cause, 64, "environment");
            string attackerName = Safe(report.AttackerName, 96);
            bool attackerKnown = !string.IsNullOrWhiteSpace(attackerName) &&
                !string.Equals(
                    attackerName,
                    "Unknown",
                    StringComparison.OrdinalIgnoreCase);
            string attacker = attackerKnown ? attackerName : cause;
            string kind = report.AttackerIsPlayer
                ? ServerManagerEventKinds.CombatPvpKill
                : ServerManagerEventKinds.PlayerDeath;
            string title = report.AttackerIsPlayer
                ? "PvP kill"
                : "Player death";
            string message = report.AttackerIsPlayer
                ? state.PlayerName + " was defeated by " + attacker + "."
                : attackerKnown
                    ? state.PlayerName + " died to " + attacker + "."
                    : state.PlayerName + " died from " + cause + ".";
            ServerManagerActor target = new(
                string.Empty,
                attacker,
                report.AttackerIsPlayer ? "player" : "cause");
            ServerManagerEvent value = Publish(
                kind,
                ServerManagerEventReliability.ClientReported,
                state.Actor,
                target,
                Fields(
                    "title", title,
                    "message", message,
                    "player_name", state.PlayerName,
                    "attacker", attacker,
                    "attacker_prefab", SafeToken(
                        report.AttackerPrefab,
                        128),
                    "cause", cause,
                    "damage_tags", ClientEventObservation.FormatDamageTags(
                        report.DamageTags),
                    "hit_type", SafeToken(report.HitType, 64),
                    "final_damage", report.FinalDamage.ToString(
                        "0.##",
                        CultureInfo.InvariantCulture)),
                now);
            NotifyAuthenticatedPlayerDeath(state, value);
        }

        private static void NotifyAuthenticatedPlayerDeath(
            EventPeerState state,
            ServerManagerEvent value)
        {
            if (state == null || state.Rpc == null || !state.Ready ||
                string.IsNullOrWhiteSpace(state.TransportId) ||
                string.IsNullOrWhiteSpace(state.AccountId) || value == null ||
                !string.Equals(
                    value.Reliability,
                    ServerManagerEventReliability.ClientReported,
                    StringComparison.Ordinal) ||
                (value.Kind != ServerManagerEventKinds.PlayerDeath &&
                 value.Kind != ServerManagerEventKinds.CombatPvpKill))
            {
                return;
            }

            Action<ZRpc, ServerManagerEvent> handlers =
                AuthenticatedPlayerDeathPublished;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<ZRpc, ServerManagerEvent> handler in
                     handlers.GetInvocationList())
            {
                try
                {
                    handler(state.Rpc, value);
                }
                catch (Exception exception) when (
                    !IntegrityCanonical.IsFatal(exception))
                {
                    ServerManagerPlugin.Log.LogWarning(
                        "An authenticated player-death observer failed: " +
                        exception.GetType().Name + ": " +
                        exception.Message);
                }
            }
        }

        private static void PublishBoss(
            EventPeerState state,
            EventClientReport report,
            DateTime now,
            string reliability)
        {
            string prefab = SafeToken(report.BossPrefab, 128);
            string id = SafeToken(report.BossZdoId, 128);
            string dedupe = string.IsNullOrEmpty(id)
                ? prefab + ":" + now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)
                : id;
            if (!SeenBossDeaths.Add(dedupe))
            {
                return;
            }

            if (SeenBossDeaths.Count > 2048)
            {
                SeenBossDeaths.Clear();
                SeenBossDeaths.Add(dedupe);
            }

            string boss = Safe(report.BossName, 96, "Unknown");
            // Receiving a report does not establish who landed the final hit.
            string killer = Safe(report.FinalAttacker, 96, "Unknown");
            ServerManagerActor killerActor = string.Equals(
                killer,
                state.PlayerName,
                StringComparison.OrdinalIgnoreCase)
                ? state.Actor
                : new ServerManagerActor(string.Empty, killer, "player");
            Publish(
                ServerManagerEventKinds.BossKilled,
                reliability,
                killerActor,
                new ServerManagerActor(dedupe, boss, "boss"),
                Fields(
                    "title", "Boss killed",
                    "message", boss + " was defeated by " + killer + ".",
                    "player_name", state.PlayerName,
                    "player", killer,
                    "boss", boss,
                    "boss_prefab", prefab),
                now);
        }

        internal static Task<ServerManagerCommandResult> EnqueueCommand(
            ServerEventCommandKind kind,
            string primary,
            string secondary,
            CancellationToken cancellation = default,
            Func<bool> executionGuard = null)
        {
            if (cancellation.IsCancellationRequested)
                return Task.FromResult(Failure("cancelled", "The requesting integration session was retired."));
            QueuedCommand command;
            try
            {
                command = new QueuedCommand(
                    kind,
                    ValidateCommandText(primary, MaximumPrimary(kind),
                        allowEmpty: kind == ServerEventCommandKind.Save),
                    ValidateCommandText(secondary, 300, allowEmpty: true),
                    cancellation,
                    executionGuard);
            }
            catch (ArgumentException exception)
            {
                return Task.FromResult(Failure(
                    "invalid_argument",
                    exception.Message));
            }

            lock (CommandGate)
            {
                if (cancellation.IsCancellationRequested)
                    return Task.FromResult(Failure("cancelled", "The requesting integration session was retired."));
                if (!_initialized || !_acceptCommands || Commands.Count >= MaximumQueuedCommands)
                {
                    return Task.FromResult(Failure(
                        _initialized && _acceptCommands ? "queue_full" : "runtime_unavailable",
                        _initialized && _acceptCommands
                            ? "The ServerManager command queue is full."
                            : "ServerManager has no active server world."));
                }

                Commands.Enqueue(command);
            }

            return command.Task;
        }

        private static void SetCommandAdmission(bool enabled)
        {
            lock (CommandGate)
            {
                Interlocked.Increment(ref _commandWorldEpoch);
                _acceptCommands = enabled;
                while (Commands.Count != 0)
                    Commands.Dequeue().Complete(Failure("world_changed", "The server world stopped or changed."));
            }
        }

        // A command admitted for one world must never execute in another.
        internal static long CommandWorldEpoch => Interlocked.Read(ref _commandWorldEpoch);

        // Called by authorized server command adapters on Unity's main thread.
        internal static void RecordCommand(
            string source, string actorId, string actorName,
            string action, string target, string result, bool success,
            IReadOnlyDictionary<string, string> data = null)
        {
            if (!_serverStarted || _shutdownStarted) return;
            Dictionary<string, string> fields = Fields("title", "Administrative command", "source", Safe(source, 32),
                    "command", Safe(action, 64), "target", Safe(target, 96),
                    "result_code", Safe(result, 64), "success", success ? "true" : "false",
                    "message", Safe(source, 32) + " " + Safe(action, 64) +
                        "; actor_id=" + Safe(actorId, 96) + "; target=" + Safe(target, 96) +
                        "; result_code=" + Safe(result, 64) + "; success=" + (success ? "true" : "false"));
            AddCommandResultFields(action, data, fields);
            Publish("command.executed", ServerManagerEventReliability.Authoritative,
                new ServerManagerActor(Safe(actorId, 96), Safe(actorName, 96), Safe(source, 32))
                    .WithPlayerAccount(source == "ingame" ? CharacterSteamIdentity.AccountPrefix + actorId : string.Empty),
                string.IsNullOrEmpty(target) ? null :
                    new ServerManagerActor(Safe(target, 96), Safe(target, 96), "command_target")
                        .WithPlayerAccount(CommandTargetPlayerAccount(action, target, success, data)), fields);
        }

        private static string CommandTargetPlayerAccount(string action, string target, bool success,
            IReadOnlyDictionary<string, string> data)
        {
            // This result comes from the server's resolved offline character,
            // not from a client result packet or a guessed character name.
            if (action == "characterrestore" && data != null &&
                data.TryGetValue("target_account", out string account)) return account;
            // These successful commands define their offline target as a
            // literal Steam64. Name-only or unresolved targets remain unknown.
            if (success && (action == "ban" || action == "unban" ||
                action == "adminadd" || action == "adminremove" ||
                action == "accessadd" || action == "accessremove") &&
                ValidateOfflinePlatformId(target) != null)
                return CharacterSteamIdentity.AccountPrefix + target;
            return string.Empty;
        }

        // Service result data is not an audit schema. Only bounded selected IDs
        // and target identity are admitted; item data, paths and output stay out.
        private static void AddCommandResultFields(string action, IReadOnlyDictionary<string, string> data,
            Dictionary<string, string> fields)
        {
            if (action == "giveitem" && data != null && data.TryGetValue("data_id", out string dataId) &&
                global::ServerManager.Commands.ServerCommands.IsItemDataId(dataId))
            {
                fields["data_id"] = dataId;
                if (fields.TryGetValue("message", out string text)) fields["message"] = text + "; data_id=" + dataId;
            }
            if (action == "characterrestore" && data != null)
            {
                if (data.TryGetValue("backup_id", out string backupId) && global::ServerManager.Commands.ServerCommands.IsBackupId(backupId))
                    fields["backup_id"] = backupId;
                foreach (string key in new[] { "target_account", "target_character" })
                    if (data.TryGetValue(key, out string value)) fields[key] = Safe(value, 128);
                // The human-readable audit file renders message, not every
                // structured field. Mirror only this same bounded allowlist.
                if (fields.TryGetValue("message", out string message))
                {
                    foreach (string key in new[] { "backup_id", "target_account",
                        "target_character" })
                        if (fields.TryGetValue(key, out string value)) message += "; " + key + "=" + value;
                    fields["message"] = message;
                }
            }
        }

        // Invoked by the embedded Discord runtime on Unity's thread. Keep its
        // command audit in the existing server audit stream, not a parallel log.
        internal static void RecordDiscordCommand(ServerManagerEvent value)
        {
            if (!_serverStarted || _shutdownStarted || value.Kind != "command.executed")
                return;
            Publish("command.executed", value.Reliability, value.Actor, value.Target,
                value.Fields.ToDictionary(pair => pair.Key, pair => pair.Value), value.OccurredAtUtc);
        }

        internal static ServerManagerStatusSnapshot GetStatusSnapshot()
        {
            return _status;
        }

        private static void ProcessCommands()
        {
            for (int index = 0; index < MaximumCommandsPerTick; ++index)
            {
                QueuedCommand command;
                lock (CommandGate)
                {
                    if (Commands.Count == 0)
                    {
                        return;
                    }

                    command = Commands.Dequeue();
                }

                try
                {
                    command.Complete(ExecuteCommand(command));
                }
                catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                {
                    command.Complete(Failure(
                        "command_failed",
                        "The ServerManager command failed: " +
                        exception.GetType().Name));
                    ServerManagerPlugin.Log.LogWarning(
                        "ServerManager integration command failed: " + exception);
                }
            }
        }

        private static ServerManagerCommandResult ExecuteCommand(
            QueuedCommand command,
            Func<string, bool> consoleKick = null)
        {
            // Check before even touching Unity state. A Discord reload can
            // revoke a command while it waits behind other integration work.
            if (command.Cancellation.IsCancellationRequested)
                return Failure("cancelled", "The requesting integration session was retired.");
            if (command.ExecutionGuard != null && !command.ExecutionGuard())
                return Failure("unauthorized", "The caller is no longer authorized for this command.");
            ZNet server = ZNet.instance;
            if (server == null || !server.IsServer() || !_serverStarted ||
                _shutdownStarted)
            {
                return Failure(
                    "server_not_ready",
                    "The Valheim server is not ready for this command.");
            }

            switch (command.Kind)
            {
                case ServerEventCommandKind.Save:
                    string previousOperationId =
                        _latestSave?.OperationId ?? string.Empty;
                    try
                    {
                        server.SaveWorldAndPlayerProfiles();
                    }
                    catch (Exception exception)
                    {
                        ServerManagerSaveOperationSnapshot failed = _latestSave;
                        if (failed != null && !string.Equals(
                                failed.OperationId,
                                previousOperationId,
                                StringComparison.Ordinal))
                        {
                            OnWorldSaveCheckpointFailed(
                                failed.OperationId,
                                exception);
                        }

                        throw;
                    }

                    return DescribeWorldSaveRequest(previousOperationId);

                case ServerEventCommandKind.Announce:
                    Publish(
                        ServerManagerEventKinds.ServerAnnouncement,
                        ServerManagerEventReliability.Authoritative,
                        new ServerManagerActor(
                            _serverId,
                            "Server",
                            "server"),
                        null,
                        Fields(
                            "title", "Announcement",
                            "message", command.Primary));
                    return Success(
                        "announcement_sent",
                        "Announcement sent.");

                case ServerEventCommandKind.Kick:
                case ServerEventCommandKind.Ban:
                case ServerEventCommandKind.Unban:
                    return ExecuteModeration(server, command, consoleKick);

                default:
                    return Failure("unsupported_command", "Unsupported command.");
            }
        }

        // Both the integration API and vanilla-console bridge report the same
        // observed save operation. Dispatch is never presented as disk success.
        internal static ServerManagerCommandResult DescribeWorldSaveRequest(string previousOperationId)
        {
            ServerManagerSaveOperationSnapshot requested = _latestSave;
            if (requested == null || string.Equals(
                    requested.OperationId, previousOperationId, StringComparison.Ordinal))
            {
                ZNet server = ZNet.instance;
                if (server != null && !server.IsDedicated())
                {
                    return Success(
                        "save_scheduled",
                        "Valheim scheduled the listen-server world save " +
                        "for its next-frame coroutine; an operation ID " +
                        "will be assigned only when SaveWorld actually starts.");
                }
                return Failure("save_not_started", "Valheim did not start a world save operation.");
            }
            string operationId = requested.OperationId;
            if (requested.State == ServerManagerSaveState.Failed)
            {
                return Failure(
                    "save_blocked",
                    string.IsNullOrWhiteSpace(requested.Error)
                        ? "The world save was blocked before its disk operation started."
                        : requested.Error,
                    operationId,
                    Fields("operation_id", operationId,
                        "completion_scope", "failed_before_world_disk",
                        "character_commit_scope", "not_completed"));
            }
            return Success(
                "save_requested",
                "World save requested; completion has not yet been claimed.",
                operationId,
                Fields("operation_id", operationId,
                    "completion_scope", "pending_verified_checkpoint",
                    "character_commit_scope", "pending"));
        }

        // Called synchronously on Unity's thread after Discord reauthorization.
        // Keep the existing target checks/audit, but run the actual vanilla
        // console kick with the authenticated transport ID, never a nickname.
        internal static ServerManagerCommandResult ExecuteConsoleKick(
            string target, string reason, Func<string, bool> invoke)
        {
            return ExecuteCommand(new QueuedCommand(ServerEventCommandKind.Kick, target, reason), invoke);
        }

        private static ServerManagerCommandResult ExecuteModeration(
            ZNet server,
            QueuedCommand command,
            Func<string, bool> consoleKick)
        {
            bool found = TryResolveTarget(
                command.Primary,
                out ZRpc targetRpc,
                out EventPeerState player,
                out bool ambiguous);
            if (ambiguous)
            {
                return Failure(
                    "ambiguous_target",
                    "More than one connected player matched that target.");
            }

            ZNetPeer connectedPeer = null;
            string exactConnectedTransportId = null;
            if (found)
            {
                if (!ServerPeerResolver.TryResolve(
                        server,
                        targetRpc,
                        out ServerPeerIdentity liveIdentity,
                        out _) ||
                    !string.Equals(
                        liveIdentity.HostId,
                        player.TransportId,
                        StringComparison.Ordinal))
                {
                    return Failure(
                        "target_not_found",
                        "The connected player was no longer available.");
                }

                connectedPeer = liveIdentity.Peer;
                exactConnectedTransportId = liveIdentity.HostId;
            }

            if (command.Kind == ServerEventCommandKind.Kick)
            {
                if (!found || connectedPeer == null)
                {
                    return Failure(
                        "target_not_found",
                        "No connected player matched that target.");
                }

                // Only an explicit operational kick may wait for a bounded
                // final character save. Ban/security disconnects below must
                // remain immediate and never enter this path.
                if (consoleKick != null)
                {
                    if (!consoleKick(exactConnectedTransportId))
                        return Failure("kick_failed", "The vanilla console rejected the kick command.");
                }
                else server.Kick(exactConnectedTransportId);
            }
            else if (command.Kind == ServerEventCommandKind.Ban)
            {
                string banTarget = found
                    ? exactConnectedTransportId
                    : ValidateOfflinePlatformId(command.Primary);
                if (banTarget == null)
                {
                    return Failure(
                        "target_not_found",
                        "Ban requires an exact connected player or Steam64 ID.");
                }

                // ZNet.Ban(string) resolves player names before IDs. A player
                // whose name is another account's Steam64 ID could therefore
                // redirect a moderation command. The live socket has already
                // pinned connected identities, so persist the exact transport
                // ID and disconnect the already-resolved peer directly.
                if (!TrySetBanListEntry(server, banTarget, true))
                {
                    return Failure(
                        "ban_list_unavailable",
                        "The server ban list could not be updated.");
                }

                if (connectedPeer != null)
                {
                    server.Disconnect(connectedPeer);
                }
            }
            else
            {
                string unbanTarget = found
                    ? exactConnectedTransportId
                    : ValidateOfflinePlatformId(command.Primary);
                if (unbanTarget == null)
                {
                    return Failure(
                        "invalid_target",
                        "Unban requires an exact connected player or Steam64 ID.");
                }

                // Use literal list removal for the same reason as Ban: never
                // reinterpret an authenticated or validated ID as a name.
                if (!TrySetBanListEntry(server, unbanTarget, false))
                {
                    return Failure(
                        "ban_list_unavailable",
                        "The server ban list could not be updated.");
                }
            }

            string action = command.Kind.ToString().ToLowerInvariant();
            string targetName = found ? player.PlayerName : command.Primary;
            Publish(
                ServerManagerEventKinds.ModerationAction,
                ServerManagerEventReliability.Authoritative,
                new ServerManagerActor(_serverId, "Server", "server"),
                found ? player.Actor :
                    new ServerManagerActor(command.Primary, targetName, "player")
                        .WithPlayerAccount(CharacterSteamIdentity.AccountPrefix + ValidateOfflinePlatformId(command.Primary)),
                Fields(
                    "title", "Moderation action",
                    "message", action + " requested for " + targetName + ".",
                    "action", action,
                    "target", targetName,
                    "reason", Safe(command.Secondary, 300)));
            return Success(
                action + "_requested",
                action + " requested for " + targetName + ".");
        }

        private static bool TryResolveTarget(
            string target,
            out ZRpc rpc,
            out EventPeerState state,
            out bool ambiguous)
        {
            rpc = null;
            state = null;
            ambiguous = false;
            foreach (KeyValuePair<ZRpc, EventPeerState> pair in Peers)
            {
                if (!pair.Value.Ready ||
                    !IsExactModerationTargetMatch(
                        target,
                        pair.Value.AccountId,
                        pair.Value.TransportId,
                        pair.Value.PlayerName))
                {
                    continue;
                }

                if (state != null)
                {
                    ambiguous = true;
                    rpc = null;
                    state = null;
                    return false;
                }

                rpc = pair.Key;
                state = pair.Value;
            }

            return state != null;
        }

        private static bool IsExactModerationTargetMatch(
            string target,
            string accountId,
            string transportId,
            string playerName)
        {
            // A Steam64-shaped target is always an ID. Do not let a numeric
            // player name shadow it and redirect kick/ban/unban operations.
            if (ValidateOfflinePlatformId(target) != null)
            {
                return string.Equals(
                    transportId,
                    target,
                    StringComparison.Ordinal);
            }

            return string.Equals(
                       accountId,
                       target,
                       StringComparison.Ordinal) ||
                   string.Equals(
                       transportId,
                       target,
                       StringComparison.Ordinal) ||
                   string.Equals(
                       playerName,
                       target,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string ValidateOfflinePlatformId(string target)
        {
            if (target == null || target.Length < 16 || target.Length > 20 ||
                target.Any(character => character < '0' || character > '9'))
            {
                return null;
            }

            return target;
        }

        private static void PollRaid()
        {
            if (!_serverStarted || !_worldReady || _shutdownStarted ||
                ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            long nowTicks = Stopwatch.GetTimestamp();
            if (nowTicks < _nextRaidPollTimestamp)
            {
                return;
            }

            _nextRaidPollTimestamp = nowTicks + RaidPollTicks;
            RandEventSystem system = RandEventSystem.instance;
            RandomEvent random = system == null
                ? null
                : system.GetCurrentRandomEvent();
            RandomEvent active = random ?? (system == null ? null : system.GetActiveEvent());
            string current = active == null ||
                             string.IsNullOrWhiteSpace(active.m_name)
                ? null
                : Safe(active.m_name, 128);
            if (current == null) active = null;
            if (ReferenceEquals(_activeRaidInstance, active) &&
                string.Equals(_activeRaid, current, StringComparison.Ordinal))
            {
                return;
            }

            if (_activeRaid != null)
            {
                PublishRaid(false, _activeRaid, _activeRaidCoordinates);
            }

            _activeRaid = current;
            _activeRaidInstance = active;
            // Capture only the actual random raid's center. A forced active
            // event's default position is not evidence of a center at zero.
            // Never reread an ended/reused event's mutated position for its end.
            _activeRaidCoordinates = current != null && random != null &&
                TryFormatRaidCoordinates(random.m_pos, out string coordinates) ? coordinates : null;
            if (current != null)
            {
                PublishRaid(true, current, _activeRaidCoordinates);
            }
        }

        private static void ResetRaidState()
        {
            _activeRaid = null;
            _activeRaidInstance = null;
            _activeRaidCoordinates = null;
            _nextRaidPollTimestamp = Stopwatch.GetTimestamp();
        }

        private static bool TryFormatRaidCoordinates(UnityEngine.Vector3 position, out string coordinates)
        {
            coordinates = null;
            if (!TryRoundRaidCoordinate(position.x, out int x) ||
                !TryRoundRaidCoordinate(position.y, out int y) ||
                !TryRoundRaidCoordinate(position.z, out int z)) return false;
            coordinates = x.ToString(CultureInfo.InvariantCulture) + ", " +
                y.ToString(CultureInfo.InvariantCulture) + ", " + z.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryRoundRaidCoordinate(float value, out int coordinate)
        {
            coordinate = 0;
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            double rounded = Math.Round((double)value, MidpointRounding.AwayFromZero);
            if (rounded < int.MinValue || rounded > int.MaxValue) return false;
            coordinate = (int)rounded;
            return true;
        }

        private static void ExpireWorldSaveOperation()
        {
            ServerManagerSaveOperationSnapshot active = _latestSave;
            if (active == null ||
                active.State != ServerManagerSaveState.InProgress ||
                DateTime.UtcNow - active.StartedAtUtc <=
                    WorldSaveCompletionTimeout)
            {
                return;
            }

            _latestSave = new ServerManagerSaveOperationSnapshot(
                active.OperationId,
                ServerManagerSaveState.Failed,
                active.StartedAtUtc,
                DateTime.UtcNow,
                "Verified world-save worker completion was not observed within five minutes.",
                active.CharacterCommitScope);
            ServerManagerPlugin.Log.LogWarning(
                "World save operation " + active.OperationId +
                " expired without verified worker completion. No world or " +
                "character checkpoint completion was inferred.");
            RefreshStatus();
        }

        private static string CharacterCommitScopeToken(
            ServerManagerCharacterCommitScope scope)
        {
            switch (scope)
            {
                case ServerManagerCharacterCommitScope
                    .AllRetainedShadowsAtCutoff:
                    return "all_retained_shadows_at_cutoff";
                case ServerManagerCharacterCommitScope
                    .PartialRetainedShadowsAtCutoff:
                    return "partial_retained_shadows_at_cutoff";
                default:
                    return "not_included";
            }
        }

        private static void PublishRaid(bool started, string raid, string coordinates)
        {
            Dictionary<string, string> fields = Fields(
                "title", started ? "Raid started" : "Raid ended",
                "message", "Raid " + raid + (started ? " started" : " ended") +
                    (coordinates == null ? "." : " at [" + coordinates + "]."),
                "raid", raid);
            if (coordinates != null) fields["raid_coordinates"] = coordinates;
            Publish(
                started
                    ? ServerManagerEventKinds.RaidStarted
                    : ServerManagerEventKinds.RaidEnded,
                ServerManagerEventReliability.Authoritative,
                null,
                null,
                fields);
        }

        /// <summary>
        /// Records findings against a server-authenticated character identity.
        /// These are operator diagnostics, never public gameplay events. The
        /// caller must supply the identity from its server session/snapshot,
        /// not identity fields from an incoming client message.
        /// </summary>
        internal static void RecordCharacterObservations(
            string accountId,
            string characterName,
            long revision,
            string source,
            IReadOnlyList<CharacterAuditObservation> observations)
        {
            try
            {
                if (!_initialized || !_serverStarted || _shutdownStarted ||
                    observations == null)
                {
                    return;
                }

                int count = Math.Min(
                    observations.Count,
                    MaximumCharacterObservationsPerCall);
                for (int index = 0; index < count; ++index)
                {
                    CharacterAuditObservation observation = observations[index];
                    if (observation == null) continue;
                    string kind = observation.Kind switch
                    {
                        CharacterAuditObservationKind.ValidationObserved => ServerManagerEventKinds.CharacterValidationObserved,
                        CharacterAuditObservationKind.AdminBypass => ServerManagerEventKinds.SecurityAdminBypass,
                        CharacterAuditObservationKind.RevisionObserved => ServerManagerEventKinds.CharacterRevisionObserved,
                        _ => string.Empty
                    };
                    if (kind.Length == 0 || string.IsNullOrEmpty(observation.DedupeKey)) continue;

                    TryWriteCharacterAudit(
                        kind,
                        accountId,
                        characterName,
                        revision,
                        source,
                        SafeCharacterAuditValue(observation.Detail, 512),
                        SafeCharacterAuditValue(observation.DedupeKey, 256),
                        observation.ReasonCode);
                }
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                // Logging failure must never alter a login, accepted shadow,
                // save response or ACK. The writer owns its failure reporting.
            }
        }

        internal static void RecordCharacterShadowWarning(
            CharacterSessionSaveHealthSnapshot snapshot,
            string source)
        {
            try
            {
                if (snapshot?.Identity == null)
                {
                    return;
                }

                string finding =
                    "validated RAM-shadow upload stalled; age_seconds=" +
                    snapshot.ShadowAcceptanceAge.TotalSeconds.ToString(
                        "F1", CultureInfo.InvariantCulture) +
                    "; session=" + snapshot.SessionId.ToString("N") +
                    "; accepted_shadows=" + snapshot.SuccessfulAcceptanceCount.ToString(
                        CultureInfo.InvariantCulture) +
                    "; consecutive_failures=" + snapshot.ConsecutiveFailureCount.ToString(
                        CultureInfo.InvariantCulture) +
                    "; last_request_seconds=" +
                    (snapshot.TimeSinceLastRequest?.TotalSeconds.ToString(
                        "F1", CultureInfo.InvariantCulture) ?? "never") +
                    "; active_save_seconds=" +
                    (snapshot.ActiveSaveAge?.TotalSeconds.ToString(
                        "F1", CultureInfo.InvariantCulture) ?? "none") +
                    "; last_error=" + SafeCharacterAuditValue(snapshot.LastError, 192);
                TryWriteCharacterAudit(
                    ServerManagerEventKinds.CharacterShadowStalled,
                    snapshot.Identity.AccountId,
                    snapshot.Identity.CharacterName,
                    snapshot.CurrentRevision,
                    source,
                    finding,
                    null,
                    "shadow_upload_stalled");
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                // ClaimLongUnsavedWarnings owns stall-warning deduplication.
                // Do not let this optional sink change that health state.
            }
        }

        /// <summary>
        /// Operator evidence, with optional summary-only webhooks. Identity must come from the server's
        /// authenticated peer/snapshot; response is the configured action,
        /// while outcome describes what actually happened, including failures.
        /// </summary>
        internal static void RecordSecurityEvent(
            string kind,
            string accountId,
            string characterName,
            string source,
            string evidence,
            DetectionAction response,
            string outcome,
            string detail)
        {
            RecordSecurityEventCore(kind, accountId, characterName, source, evidence,
                response, outcome, detail, "character_validation_failed");
        }

        internal static void RecordCharacterSaveRejected(string accountId, string characterName,
            string source, CharacterSaveResult result)
        {
            if (result == null || result.Accepted) return;
            RecordSecurityEventCore(ServerManagerEventKinds.CharacterSaveRejected,
                accountId, characterName, source, "character_validation", DetectionAction.Log,
                "save_rejected", result.Error, result.AuditFindings.RejectionReasonCode);
        }

        private static void RecordSecurityEventCore(string kind, string accountId,
            string characterName, string source, string evidence, DetectionAction response,
            string outcome, string detail, string rejectionReasonCode)
        {
            try
            {
                if (!_initialized || !_serverStarted || _shutdownStarted ||
                    (kind != ServerManagerEventKinds.SecurityDetection &&
                     kind != ServerManagerEventKinds.SecurityResponse &&
                     kind != ServerManagerEventKinds.SecurityAdminBypass &&
                     kind != ServerManagerEventKinds.CharacterSaveRejected) ||
                    accountId == null || accountId.Length > 30 ||
                    !CharacterSteamIdentity.TryParseCanonicalAccountId(accountId, out _) ||
                    (source != "client_reported" && source != "snapshot_received" &&
                     source != "server_observed" && source != "stored" &&
                     source != "stored_host" && source != "incoming" &&
                     source != "incoming_host") ||
                    response < DetectionAction.Off || response > DetectionAction.Ban)
                {
                    return;
                }

                characterName = SafeCharacterAuditValue(characterName, 96);
                evidence = SafeCharacterAuditValue(evidence, 96);
                outcome = SafeCharacterAuditValue(outcome, 64);
                detail = SafeCharacterAuditValue(detail, 384);
                if (evidence.Length == 0 || outcome.Length == 0)
                {
                    return;
                }

                string responseText = response.ToString();
                string message = "account=\"" + accountId + "\" character=\"" +
                    characterName + "\" source=" + source + " evidence=\"" +
                    evidence + "\" response=" + responseText + " outcome=\"" +
                    outcome + "\" detail=\"" + detail + "\"";
                ServerManagerEvent value = new(
                    Guid.NewGuid().ToString("N"), DateTime.UtcNow, _serverId, kind,
                    source == "client_reported"
                        ? ServerManagerEventReliability.ClientReported
                        : ServerManagerEventReliability.Observed,
                    new ServerManagerActor(accountId, characterName, "player").WithPlayerAccount(accountId),
                    null,
                    Fields(
                        "message", message, "account_id", accountId,
                        "character", characterName, "source", source,
                        "evidence", evidence, "response", responseText,
                        "outcome", outcome, "detail", detail,
                        "identity_status", "authenticated",
                        "reason_code", kind == ServerManagerEventKinds.CharacterSaveRejected
                            ? AuditCode(rejectionReasonCode, "character_validation_failed")
                            : AuditCode(evidence, "security_evidence")));

                // Changing detection magnitudes or diagnostics cannot evade
                // cooldown. Terminal response/rejected-save calls are bounded
                // by their session lifecycle; keep distinct reconnect attempts
                // even when they have identical identity and outcome fields.
                string key = kind != ServerManagerEventKinds.SecurityDetection
                    ? null
                    : kind + "\0" + accountId + "\0" + characterName +
                      "\0" + source + "\0" + evidence + "\0" + responseText +
                      "\0" + outcome;
                TryQueueOperatorAudit(value, key);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                // A missing/full/broken log sink must not cancel detection,
                // rejection, save processing, or the caller's ACK pipeline.
            }
        }

        // A rejected connection may precede Steam authentication or character
        // selection. Do not manufacture a trusted identity for such failures.
        internal static void RecordConnectionRejected(
            string accountId,
            string characterName,
            string category,
            string stage,
            string reasonCode,
            string source,
            string identityStatus,
            string detail,
            string pluginSummary = "")
        {
            try
            {
                if (!_initialized || !_serverStarted || _shutdownStarted ||
                    (category != "mod_policy" && category != "character" &&
                     category != "protocol" && category != "authentication") ||
                    (source != "server_observed" && source != "client_reported") ||
                    (identityStatus != "authenticated" && identityStatus != "unverified" &&
                     identityStatus != "unavailable")) return;
                bool canonical = accountId != null && accountId.Length <= 30 &&
                    CharacterSteamIdentity.TryParseCanonicalAccountId(accountId, out _);
                if (identityStatus == "authenticated" && !canonical) return;
                if (identityStatus == "unavailable" || !canonical)
                {
                    accountId = "";
                    characterName = "";
                    identityStatus = "unavailable";
                }
                characterName = SafeCharacterAuditValue(characterName, 96);
                stage = AuditCode(stage, "unknown");
                reasonCode = AuditCode(reasonCode, "connection_rejected");
                detail = SafeCharacterAuditValue(detail, 4096);
                pluginSummary = SafeCharacterAuditValue(pluginSummary, 1024);
                Dictionary<string, string> fields = Fields(
                    "account_id", accountId, "character", characterName,
                    "category", category, "stage", stage, "reason_code", reasonCode,
                    "source", source, "identity_status", identityStatus,
                    "outcome", source == "client_reported" ? "client_aborted" : "rejected",
                    "detail", detail,
                    "message", "category=" + category + " stage=" + stage + " reason=" + reasonCode +
                        " source=" + source + " identity_status=" + identityStatus +
                        " account=\"" + accountId + "\" character=\"" + characterName + "\" detail=\"" + detail + "\"");
                if (pluginSummary.Length != 0) fields.Add("plugin_details", pluginSummary);
                ServerManagerEvent value = new(
                    Guid.NewGuid().ToString("N"), DateTime.UtcNow, _serverId,
                    ServerManagerEventKinds.ConnectionRejected,
                    source == "client_reported" ? ServerManagerEventReliability.ClientReported :
                        ServerManagerEventReliability.Authoritative,
                    accountId.Length == 0 ? null : new ServerManagerActor(accountId, characterName,
                        identityStatus == "authenticated" ? "player" : "unverified_peer")
                        .WithPlayerAccount(identityStatus == "authenticated" ? accountId : string.Empty),
                    null, fields);
                // The caller owns once-per-attempt admission. Webhooks add their
                // own reconnect/flood bounds; no cross-attempt audit suppression.
                TryQueueOperatorAudit(value, null);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                // Logging or Discord failures never prevent the actual rejection.
            }
        }

        private static string AuditCode(string value, string fallback) =>
            !string.IsNullOrEmpty(value) && value.Length <= 96 &&
            value.All(character => (character >= 'a' && character <= 'z') ||
                (character >= 'A' && character <= 'Z') || (character >= '0' && character <= '9') ||
                character == '_' || character == '.' || character == '-') ? value : fallback;

        private static void TryWriteCharacterAudit(
            string kind,
            string accountId,
            string characterName,
            long revision,
            string source,
            string finding,
            string findingCode,
            string reasonCode)
        {
            if (!_initialized || !_serverStarted || _shutdownStarted ||
                accountId == null || accountId.Length > 30 ||
                !CharacterSteamIdentity.TryParseCanonicalAccountId(accountId, out _) ||
                string.IsNullOrWhiteSpace(characterName) || revision < 1 ||
                (source != "stored" && source != "incoming" &&
                 source != "stored_host" && source != "incoming_host"))
            {
                return;
            }

            accountId = SafeCharacterAuditValue(accountId, 96);
            characterName = SafeCharacterAuditValue(characterName, 96);
            finding = SafeCharacterAuditValue(finding, 512);
            string revisionText = revision.ToString(CultureInfo.InvariantCulture);
            string message = "account=\"" + accountId + "\" character=\"" +
                characterName + "\" revision=" + revisionText +
                " source=" + source + " finding=\"" + finding + "\"";
            ServerManagerEvent value = new(
                Guid.NewGuid().ToString("N"),
                DateTime.UtcNow,
                _serverId,
                kind,
                ServerManagerEventReliability.Observed,
                new ServerManagerActor(accountId, characterName, "player").WithPlayerAccount(accountId),
                null,
                Fields(
                    "message", message,
                    "account_id", accountId,
                    "character", characterName,
                    "revision", revisionText,
                    "source", source,
                    "finding", finding,
                    "identity_status", "authenticated",
                    "reason_code", AuditCode(reasonCode, "character_validation_failed"),
                    "outcome", kind == ServerManagerEventKinds.SecurityAdminBypass ? "bypassed" :
                        kind == ServerManagerEventKinds.CharacterValidationObserved ? "would_reject" : "observed"));

            // Dedupe by stable code, not the changing quantity or revision in
            // the diagnostic text: inventory fast-path saves may arrive each
            // second. Only a successfully queued record starts the cooldown.
            string key = findingCode == null ? null :
                accountId + "\0" + characterName + "\0" + source + "\0" + findingCode;
            TryQueueOperatorAudit(value, key);
        }

        private static void TryQueueOperatorAudit(ServerManagerEvent value, string key)
        {
            lock (CharacterAuditGate)
            {
                if (!_initialized || !_serverStarted || _shutdownStarted)
                {
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                if (key != null && CharacterObservationTimes.TryGetValue(
                        key, out long previous) && now >= previous &&
                    now - previous < CharacterObservationCooldownTicks)
                {
                    return;
                }

                // Log and webhook are independent best-effort sinks. Retain the
                // existing rule: only local queue acceptance starts audit dedupe.
                bool logged = false;
                try { logged = LogWriter.TryWrite(value); }
                catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                {
                    // A disposed/full writer or failing secondary logger must
                    // not block the independent optional notification sink.
                }
                if (logged && key != null && !CharacterObservationTimes.ContainsKey(key) &&
                    CharacterObservationTimes.Count >= MaximumCharacterObservationKeys)
                {
                    string oldestKey = null;
                    long oldestTimestamp = long.MaxValue;
                    foreach (KeyValuePair<string, long> entry in CharacterObservationTimes)
                    {
                        if (oldestKey == null || entry.Value < oldestTimestamp)
                        {
                            oldestKey = entry.Key;
                            oldestTimestamp = entry.Value;
                        }
                    }

                    if (oldestKey != null)
                    {
                        CharacterObservationTimes.Remove(oldestKey);
                    }
                }

                if (logged && key != null) CharacterObservationTimes[key] = now;
            }
            TryPublishOperatorWebhook(value);
        }

        private static void TryPublishOperatorWebhook(ServerManagerEvent value)
        {
            try { Discord.DiscordRuntime.Publish(value); }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                // Do not report failures through this sink or trigger recursion.
            }
        }

        private static string SafeCharacterAuditValue(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder result = new(Math.Min(value.Length, maximumLength));
            for (int index = 0; index < value.Length && index < maximumLength; ++index)
            {
                char character = value[index];
                UnicodeCategory category = char.GetUnicodeCategory(character);
                if (char.IsControl(character) || char.IsSurrogate(character) ||
                    category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator ||
                    category == UnicodeCategory.ParagraphSeparator)
                {
                    result.Append(' ');
                }
                else
                {
                    result.Append(character == '"' ? '\'' :
                        character == '\\' ? '/' : character);
                }
            }

            return result.ToString().Trim();
        }

        private static void ClearCharacterAuditState()
        {
            lock (CharacterAuditGate)
            {
                CharacterObservationTimes.Clear();
            }
        }

        private static ServerManagerEvent Publish(
            string kind,
            string reliability,
            ServerManagerActor actor,
            ServerManagerActor target,
            Dictionary<string, string> fields,
            DateTime? occurredAtUtc = null)
        {
            ServerManagerEvent value = new(
                Guid.NewGuid().ToString("N"),
                occurredAtUtc ?? DateTime.UtcNow,
                _serverId,
                kind,
                reliability,
                actor,
                target,
                fields);
            if (ServerManagerEventKinds.IsOperatorAudit(kind))
            {
                TryQueueOperatorAudit(value, null);
                return value;
            }
            LogWriter.TryWrite(value);
            // These channels are intentionally log-only. Keep this boundary
            // ahead of every public subscriber and in-game broadcast path.
            if (IsLogOnlyChatKind(kind))
            {
                return value;
            }

            ServerManagerIntegrationApi.Publish(value);
            Discord.DiscordRuntime.Publish(value);

            if (fields.TryGetValue("message", out string message) &&
                IsDisplayKind(kind))
            {
                // Choose one token for the event, not one language for every
                // client. Audit/API fields remain the original factual data.
                string messageKey = string.Empty;
                string[] arguments = Array.Empty<string>();
                byte labelMask = 0;
                if (EventMessageText.TryCreate(
                        value,
                        EventMessageText.PlayerName(value),
                        EventMessageText.OtherName(value),
                        out messageKey, out arguments, out labelMask))
                {
                    message = string.Empty;
                }
                ServerManagerRuntime.BroadcastEventDisplay(
                    kind,
                    Safe(message, 500), messageKey, arguments, labelMask);
            }

            return value;
        }

        // An already-Discord-origin message is deliberately local-log-only:
        // do not feed it through Publish, subscribers, or outbound webhooks.
        internal static void RecordDiscordShout(string userId, string userName, string text)
        {
            if (!_serverStarted || _shutdownStarted || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string name = "[Discord] " + Safe(userName, 80);
            string content = Safe(text, 500);
            LogWriter.TryWrite(new ServerManagerEvent(
                Guid.NewGuid().ToString("N"), DateTime.UtcNow, _serverId,
                ServerManagerEventKinds.ChatShout, ServerManagerEventReliability.Observed,
                new ServerManagerActor("discord:" + Safe(userId, 20), name, "discord"), null,
                Fields("source", "discord", "message", name + " (ID: " + Safe(userId, 20) + "): " + content, "text", content)));
        }

        private static bool IsLogOnlyChatKind(string kind)
        {
            return kind == ServerManagerEventKinds.ChatNormal ||
                   kind == ServerManagerEventKinds.ChatWhisper ||
                   kind == ServerManagerEventKinds.ChatClan;
        }

        private static bool IsDisplayKind(string kind)
        {
            return kind == ServerManagerEventKinds.ServerAnnouncement ||
                   kind == ServerManagerEventKinds.PlayerDeath ||
                   kind == ServerManagerEventKinds.CombatPvpKill ||
                   kind == ServerManagerEventKinds.BossKilled;
        }

        private static ServerManagerActor Actor(
            string stableId,
            string name,
            string kind)
        {
            string id;
            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Utf8.GetBytes(
                        _serverId + "\0" + (stableId ?? string.Empty)));
                    StringBuilder builder = new(32);
                    for (int index = 0; index < 16; ++index)
                    {
                        builder.Append(hash[index].ToString(
                            "x2",
                            CultureInfo.InvariantCulture));
                    }

                    id = builder.ToString();
                }
            }
            catch
            {
                id = string.Empty;
            }

            return new ServerManagerActor(id, Safe(name, 96), kind);
        }

        private static void RefreshStatus()
        {
            ZNet server = ZNet.instance;
            bool isServer = server != null && server.IsServer();
            ServerManagerPlayerSnapshot[] players = Peers.Values
                .Where(state => state.Ready)
                .OrderBy(state => state.PlayerName, StringComparer.OrdinalIgnoreCase)
                .Select(state => new ServerManagerPlayerSnapshot(
                    state.AccountId,
                    state.PlayerName,
                    state.PeerUid,
                    state.IsAdmin))
                .ToArray();
            _status = new ServerManagerStatusSnapshot(
                _initialized,
                isServer,
                isServer && server.IsDedicated(),
                _serverStarted,
                _worldReady,
                _shutdownStarted,
                ServerName(),
                WorldName(),
                new ReadOnlyCollection<ServerManagerPlayerSnapshot>(players),
                _latestSave);
        }

        private static ServerManagerStatusSnapshot UnavailableStatus()
        {
            return new ServerManagerStatusSnapshot(
                false,
                false,
                false,
                false,
                false,
                false,
                string.Empty,
                string.Empty,
                Array.Empty<ServerManagerPlayerSnapshot>(),
                null);
        }

        private static string CreateServerId()
        {
            string source = ServerName() + "\0" + WorldName();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Utf8.GetBytes(source));
                StringBuilder builder = new(32);
                for (int index = 0; index < 16; ++index)
                {
                    builder.Append(digest[index].ToString(
                        "x2",
                        CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static string ServerName()
        {
            try
            {
                return Safe(
                    ServerNameField?.GetValue(null) as string,
                    128);
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                return string.Empty;
            }
        }

        private static bool TrySetBanListEntry(
            ZNet server,
            string accountId,
            bool banned)
        {
            try
            {
                SyncedList list =
                    BannedListField?.GetValue(server) as SyncedList;
                if (list == null)
                {
                    return false;
                }

                if (banned)
                {
                    string canonical = ServerManager.Commands.ServerCommands.GetCanonicalSteamListEntry(accountId);
                    if (!list.GetList().Contains(canonical)) list.Add(canonical);
                }
                else
                {
                    string[] existing = ServerManager.Commands.ServerCommands.GetSteamListEntries(list.GetList(), accountId);
                    foreach (string entry in existing) list.Remove(entry);
                }

                return (ServerManager.Commands.ServerCommands.GetSteamListEntries(list.GetList(), accountId).Length != 0) == banned;
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Server ban-list update failed: " + exception.Message);
                return false;
            }
        }

        private static string WorldName()
        {
            try
            {
                return Safe(ZNet.World?.m_name, 128);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void UnsubscribeWorldSave()
        {
            if (!_saveSubscribed)
            {
                return;
            }

            ZNet.WorldSaveFinished -= OnWorldSaveFinished;
            _saveSubscribed = false;
        }

        private static int MaximumPrimary(ServerEventCommandKind kind)
        {
            return kind == ServerEventCommandKind.Announce ? 500 : 128;
        }

        private static string ValidateCommandText(
            string value,
            int maximumLength,
            bool allowEmpty)
        {
            value ??= string.Empty;
            value = value.Trim();
            if ((!allowEmpty && value.Length == 0) ||
                value.Length > maximumLength ||
                value.Any(char.IsControl))
            {
                throw new ArgumentException("The command argument was invalid.");
            }

            return value;
        }

        private static string Safe(
            string value,
            int maximumLength,
            string fallback = "")
        {
            return ClientEventObservation.SafeText(
                value,
                maximumLength,
                fallback);
        }

        private static string SafeToken(
            string value,
            int maximumLength,
            string fallback = "")
        {
            string safe = ClientEventObservation.SafeToken(
                value,
                maximumLength);
            return string.IsNullOrEmpty(safe) ? fallback : safe;
        }

        private static string BoundedChatText(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new(Math.Min(value.Length, maximumLength));
            for (int index = 0;
                 index < value.Length && builder.Length < maximumLength;
                 ++index)
            {
                char character = value[index];
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 < value.Length &&
                        char.IsLowSurrogate(value[index + 1]) &&
                        builder.Length + 2 <= maximumLength)
                    {
                        builder.Append(character).Append(value[++index]);
                    }
                    else
                    {
                        builder.Append(' ');
                    }
                }
                else
                {
                    builder.Append(
                        char.IsControl(character) || char.IsLowSurrogate(character)
                            ? ' '
                            : character);
                }
            }

            return builder.ToString().Trim();
        }

        private static Dictionary<string, string> Fields(
            params string[] pairs)
        {
            Dictionary<string, string> fields = new(StringComparer.Ordinal);
            for (int index = 0; index + 1 < pairs.Length; index += 2)
            {
                fields[pairs[index]] = pairs[index + 1] ?? string.Empty;
            }

            return fields;
        }

        private static ServerManagerCommandResult Success(
            string code,
            string message,
            string operationId = "",
            IDictionary<string, string> data = null)
        {
            return new ServerManagerCommandResult(
                true,
                code,
                message,
                operationId,
                data);
        }

        private static ServerManagerCommandResult Failure(
            string code,
            string message,
            string operationId = "",
            IDictionary<string, string> data = null)
        {
            return new ServerManagerCommandResult(
                false,
                code,
                message,
                operationId,
                data);
        }

        private sealed class EventPeerState
        {
            internal bool Ready;
            internal ZRpc Rpc;
            internal string AccountId = string.Empty;
            internal string TransportId = string.Empty;
            internal string PlayerName = string.Empty;
            internal long PeerUid;
            internal bool IsAdmin;
            internal ServerManagerActor Actor;
            internal uint LastReportSequence;
            internal DateTime LastDeathUtc = DateTime.MinValue;
            internal RateWindow Reports { get; } = new();
            internal RateWindow Chats { get; } = new();
            internal RateWindow Shouts { get; } = new();
            internal RateWindow Deaths { get; } = new();
            internal RateWindow Bosses { get; } = new();
        }

        private sealed class RateWindow
        {
            private readonly Queue<DateTime> _acceptedUtc = new();

            internal bool TryConsume(
                int maximum,
                TimeSpan window,
                DateTime now)
            {
                while (_acceptedUtc.Count != 0 &&
                       now - _acceptedUtc.Peek() >= window)
                {
                    _acceptedUtc.Dequeue();
                }

                if (_acceptedUtc.Count >= maximum)
                {
                    return false;
                }

                // The queue never exceeds maximum, including rejected floods.
                _acceptedUtc.Enqueue(now);
                return true;
            }

            internal void Clear()
            {
                _acceptedUtc.Clear();
            }
        }

        private sealed class QueuedCommand
        {
            private readonly TaskCompletionSource<ServerManagerCommandResult>
                _completion = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            internal QueuedCommand(
                ServerEventCommandKind kind,
                string primary,
                string secondary,
                CancellationToken cancellation = default,
                Func<bool> executionGuard = null)
            {
                Kind = kind;
                Primary = primary;
                Secondary = secondary;
                Cancellation = cancellation;
                ExecutionGuard = executionGuard;
            }

            internal ServerEventCommandKind Kind { get; }
            internal string Primary { get; }
            internal string Secondary { get; }
            internal CancellationToken Cancellation { get; }
            internal Func<bool> ExecutionGuard { get; }
            internal Task<ServerManagerCommandResult> Task => _completion.Task;

            internal void Complete(ServerManagerCommandResult result)
            {
                _completion.TrySetResult(result);
            }
        }
    }

    internal sealed class EventLogWriter
    {
        private const int QueueCapacity = 1024;
        private const long MaximumFileBytes = 10L * 1024L * 1024L;
        private readonly object _gate = new();
        private BlockingCollection<ServerManagerEvent> _queue;
        private Thread _thread;
        private int _dropped;

        internal void Start(string dataRoot)
        {
            lock (_gate)
            {
                if (_queue != null)
                {
                    return;
                }

                string directory = Path.Combine(dataRoot, "logs");
                Directory.CreateDirectory(directory);
                BlockingCollection<ServerManagerEvent> queue = new(
                    new ConcurrentQueue<ServerManagerEvent>(),
                    QueueCapacity);
                _queue = queue;
                // Bind this worker to its world before scheduling. Stop may
                // detach the active queue before the worker begins running.
                _thread = new Thread(() => WriteLoop(queue, directory))
                {
                    IsBackground = true,
                    Name = "ServerManager event log"
                };
                _thread.Start();
            }
        }

        internal bool TryWrite(ServerManagerEvent value)
        {
            BlockingCollection<ServerManagerEvent> queue = _queue;
            if (queue == null || queue.IsAddingCompleted)
            {
                return false;
            }

            bool queued;
            try
            {
                queued = queue.TryAdd(value);
            }
            catch (InvalidOperationException)
            {
                // Stop may complete the queue immediately after the lock-free
                // snapshot. Dropping a shutdown-racing log entry is safer than
                // throwing back onto the publisher's main-thread path.
                return false;
            }

            if (queued)
            {
                return true;
            }

            int dropped = Interlocked.Increment(ref _dropped);
            if (dropped == 1 || dropped % 100 == 0)
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Server event log queue dropped " + dropped +
                    " event(s).");
            }

            return false;
        }

        internal void Stop()
        {
            BlockingCollection<ServerManagerEvent> queue;
            Thread thread;
            lock (_gate)
            {
                queue = _queue;
                thread = _thread;
                _queue = null;
                _thread = null;
            }

            if (queue == null)
            {
                return;
            }

            queue.CompleteAdding();
            if (thread != null && !thread.Join(TimeSpan.FromSeconds(2)))
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Timed out while draining the server event log queue.");
            }
        }

        private static void WriteLoop(
            BlockingCollection<ServerManagerEvent> queue,
            string directory)
        {
            int failures = 0;
            foreach (ServerManagerEvent value in
                     queue.GetConsumingEnumerable())
            {
                try
                {
                    Write(value, directory);
                }
                catch (Exception exception) when (
                    !IntegrityCanonical.IsFatal(exception))
                {
                    ++failures;
                    if (failures == 1 || failures % 100 == 0)
                    {
                        try
                        {
                            ServerManagerPlugin.Log.LogError(
                                "Server event log write failed " + failures +
                                " time(s): " + exception.GetType().Name + ": " +
                                exception.Message);
                        }
                        catch (Exception loggingException) when (
                            !IntegrityCanonical.IsFatal(loggingException))
                        {
                            // A failed secondary log sink must not escape this
                            // background worker or terminate the game process.
                        }
                    }
                }
            }
        }

        private static void Write(
            ServerManagerEvent value,
            string directory)
        {
            string fileName = IsChatKind(value.Kind)
                ? "events-chat.log"
                : "events-audit.log";
            string path = Path.Combine(directory, fileName);
            Rotate(path);
            string actor = value.Actor?.Name ?? string.Empty;
            string message = value.Fields.TryGetValue(
                "message",
                out string found)
                ? found
                : string.Empty;
            string line = value.OccurredAtUtc.ToString(
                              "O",
                              CultureInfo.InvariantCulture) + "\t" +
                          Clean(value.Kind) + "\t" +
                          Clean(value.Reliability) + "\t" +
                          Clean(actor) + "\t" + Clean(message,
                              value.Kind == ServerManagerEventKinds.ConnectionRejected ? 8192 : 1000) +
                          Environment.NewLine;
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }

        private static bool IsChatKind(string kind)
        {
            return kind == ServerManagerEventKinds.ChatShout ||
                   kind == ServerManagerEventKinds.ChatNormal ||
                   kind == ServerManagerEventKinds.ChatWhisper ||
                   kind == ServerManagerEventKinds.ChatClan;
        }

        private static void Rotate(string path)
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length < MaximumFileBytes)
            {
                return;
            }

            string previous = path + ".1";
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }

            File.Move(path, previous);
        }

        private static string Clean(string value, int maximumLength = 1000)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new(Math.Min(value.Length, maximumLength));
            for (int index = 0; index < value.Length && index < maximumLength; ++index)
            {
                char character = value[index];
                builder.Append(char.IsControl(character) ? ' ' : character);
            }

            return builder.ToString();
        }
    }
}
