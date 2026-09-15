#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ServerManager.Events
{
    public static class ServerManagerEventKinds
    {
        public const string ServerStarted = "server.started";
        public const string ServerReady = "server.ready";
        public const string ServerSaved = "server.saved";
        public const string ServerShutdown = "server.shutdown";
        public const string ServerAnnouncement = "server.announcement";
        public const string PlayerFirstJoin = "player.first_join";
        public const string PlayerLogin = "player.login";
        public const string PlayerLeave = "player.leave";
        public const string ChatShout = "chat.shout";
        public const string DiscordShout = "discord.shout";
        public const string ChatNormal = "chat.normal";
        public const string ChatWhisper = "chat.whisper";
        public const string ChatClan = "chat.clan";
        public const string RaidStarted = "raid.started";
        public const string RaidEnded = "raid.ended";
        public const string PlayerDeath = "player.death";
        public const string CombatPvpKill = "combat.pvp_kill";
        public const string BossKilled = "boss.killed";
        public const string ModerationAction = "moderation.action";

        // Operator diagnostics remain outside the public event/subscriber and
        // in-game display contract. Explicit webhook routes receive summaries.
        internal const string CharacterShadowStalled = "character.shadow_stalled";
        internal const string CharacterValidationObserved = "character.validation_observed";
        internal const string CharacterRevisionObserved = "character.revision_observed";
        internal const string SecurityDetection = "security.detection";
        internal const string SecurityResponse = "security.response";
        internal const string CharacterSaveRejected = "character.save_rejected";
        internal const string SecurityAdminBypass = "security.admin_bypass";
        internal const string ConnectionRejected = "connection.rejected";

        internal static bool IsOperatorAudit(string kind)
        {
            return kind == CharacterShadowStalled ||
                   kind == CharacterValidationObserved ||
                   kind == CharacterRevisionObserved ||
                   kind == SecurityDetection ||
                   kind == SecurityResponse ||
                   kind == SecurityAdminBypass ||
                   kind == CharacterSaveRejected ||
                   kind == ConnectionRejected;
        }
    }

    public static class ServerManagerEventReliability
    {
        public const string Authoritative = "authoritative";
        public const string Observed = "observed";
        public const string ClientReported = "client_reported";
    }

    public enum ServerManagerSaveState
    {
        None = 0,
        InProgress = 1,
        WorldDiskCompleted = 2,
        Failed = 3,
        CheckpointCompleted = 4
    }

    /// <summary>
    /// Describes what a verified save-operation completion actually proves.
    /// </summary>
    public enum ServerManagerCharacterCommitScope
    {
        NotIncluded = 0,
        AllRetainedShadowsAtCutoff = 1,
        PartialRetainedShadowsAtCutoff = 2
    }

    public sealed class ServerManagerActor
    {
        internal ServerManagerActor(string id, string name, string kind)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
            Kind = kind ?? string.Empty;
        }

        public string Id { get; }
        public string Name { get; }
        public string Kind { get; }

        // Adapter-only attribution, never a public event field. Callers must
        // supply a verified account, not infer one from Id or a display name.
        internal string PlayerAccountId { get; private set; } = string.Empty;

        internal ServerManagerActor WithPlayerAccount(string canonicalAccountId)
        {
            const string prefix = "steamworks:";
            ServerManagerActor copy = new ServerManagerActor(Id, Name, Kind);
            if (canonicalAccountId == null || canonicalAccountId.Length != prefix.Length + 17 ||
                !canonicalAccountId.StartsWith(prefix, StringComparison.Ordinal)) return copy;
            string steam64 = canonicalAccountId.Substring(prefix.Length);
            foreach (char character in steam64)
                if (character < '0' || character > '9') return copy;
            // Public-universe, desktop-instance individual accounts only;
            // keep this DTO BCL-only for public-API source-linked consumers.
            if (ulong.TryParse(steam64, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) &&
                id >= 76561197960265729UL && id <= 76561202255233023UL)
                copy.PlayerAccountId = canonicalAccountId;
            return copy;
        }
    }

    public sealed class ServerManagerEvent
    {
        public const int CurrentSchemaVersion = 1;

        internal ServerManagerEvent(
            string eventId,
            DateTime occurredAtUtc,
            string serverId,
            string kind,
            string reliability,
            ServerManagerActor actor,
            ServerManagerActor target,
            IDictionary<string, string> fields)
        {
            SchemaVersion = CurrentSchemaVersion;
            EventId = eventId ?? string.Empty;
            OccurredAtUtc = occurredAtUtc.ToUniversalTime();
            ServerId = serverId ?? string.Empty;
            Kind = kind ?? string.Empty;
            Reliability = reliability ?? string.Empty;
            Actor = actor;
            Target = target;
            Fields = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(
                    fields ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal));
        }

        public int SchemaVersion { get; }
        public string EventId { get; }
        public DateTime OccurredAtUtc { get; }
        public string ServerId { get; }
        public string Kind { get; }
        public string Reliability { get; }
        public ServerManagerActor Actor { get; }
        public ServerManagerActor Target { get; }
        public IReadOnlyDictionary<string, string> Fields { get; }
    }

    public sealed class ServerManagerEventArgs : EventArgs
    {
        internal ServerManagerEventArgs(ServerManagerEvent value)
        {
            Event = value ?? throw new ArgumentNullException(nameof(value));
        }

        public ServerManagerEvent Event { get; }
    }

    public sealed class ServerManagerSaveOperationSnapshot
    {
        internal ServerManagerSaveOperationSnapshot(
            string operationId,
            ServerManagerSaveState state,
            DateTime startedAtUtc,
            DateTime? completedAtUtc,
            string error,
            ServerManagerCharacterCommitScope characterCommitScope =
                ServerManagerCharacterCommitScope.NotIncluded,
            int capturedCharacterCount = 0,
            int persistedCharacterCount = 0,
            int pendingCharacterCount = 0)
        {
            OperationId = operationId ?? string.Empty;
            State = state;
            StartedAtUtc = startedAtUtc.ToUniversalTime();
            CompletedAtUtc = completedAtUtc?.ToUniversalTime();
            Error = error ?? string.Empty;
            CharacterCommitScope = characterCommitScope;
            CapturedCharacterCount = Math.Max(0, capturedCharacterCount);
            PersistedCharacterCount = Math.Max(0, persistedCharacterCount);
            PendingCharacterCount = Math.Max(0, pendingCharacterCount);
        }

        public string OperationId { get; }
        public ServerManagerSaveState State { get; }
        public DateTime StartedAtUtc { get; }
        public DateTime? CompletedAtUtc { get; }
        public string Error { get; }
        public ServerManagerCharacterCommitScope CharacterCommitScope { get; }
        public int CapturedCharacterCount { get; }
        public int PersistedCharacterCount { get; }
        public int PendingCharacterCount { get; }
    }

    public sealed class ServerManagerPlayerSnapshot
    {
        internal ServerManagerPlayerSnapshot(
            string accountId,
            string playerName,
            long peerUid,
            bool isAdmin)
        {
            AccountId = accountId ?? string.Empty;
            PlayerName = playerName ?? string.Empty;
            PeerUid = peerUid;
            IsAdmin = isAdmin;
        }

        public string AccountId { get; }
        public string PlayerName { get; }
        public long PeerUid { get; }
        public bool IsAdmin { get; }
    }

    public sealed class ServerManagerStatusSnapshot
    {
        internal ServerManagerStatusSnapshot(
            bool isInitialized,
            bool isServer,
            bool isDedicated,
            bool serverStarted,
            bool worldReady,
            bool shutdownStarted,
            string serverName,
            string worldName,
            IReadOnlyList<ServerManagerPlayerSnapshot> players,
            ServerManagerSaveOperationSnapshot latestSave)
        {
            IsInitialized = isInitialized;
            IsServer = isServer;
            IsDedicated = isDedicated;
            ServerStarted = serverStarted;
            WorldReady = worldReady;
            ShutdownStarted = shutdownStarted;
            ServerName = serverName ?? string.Empty;
            WorldName = worldName ?? string.Empty;
            Players = players ?? Array.Empty<ServerManagerPlayerSnapshot>();
            ReadyPlayerCount = Players.Count;
            LatestSave = latestSave;
        }

        public bool IsInitialized { get; }
        public bool IsServer { get; }
        public bool IsDedicated { get; }
        public bool ServerStarted { get; }
        public bool WorldReady { get; }
        public bool ShutdownStarted { get; }
        public string ServerName { get; }
        public string WorldName { get; }
        public int ReadyPlayerCount { get; }
        public IReadOnlyList<ServerManagerPlayerSnapshot> Players { get; }
        public ServerManagerSaveOperationSnapshot LatestSave { get; }
    }

    public sealed class ServerManagerCommandResult
    {
        internal ServerManagerCommandResult(
            bool success,
            string code,
            string message,
            string operationId,
            IDictionary<string, string> data)
        {
            Success = success;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            OperationId = operationId ?? string.Empty;
            Data = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(
                    data ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal));
        }

        public bool Success { get; }
        public string Code { get; }
        public string Message { get; }
        public string OperationId { get; }
        public IReadOnlyDictionary<string, string> Data { get; }
    }

    /// <summary>
    /// Versioned, dependency-free integration surface for server-side adapters. Calls that
    /// mutate Valheim are queued and executed by ServerManager on Unity's main thread.
    /// </summary>
    public static class ServerManagerIntegrationApi
    {
        private const int EventDispatchCapacity = 512;
        private static readonly object DispatchGate = new object();
        private static BlockingCollection<EventDispatchItem> _dispatchQueue;
        private static Thread _dispatchThread;
        private static int _droppedEvents;

        public const int ApiMajorVersion = 1;
        public const string ApiVersion = "1.3.0";
        public static string PluginVersion => ServerManagerPlugin.ModVersion;

        public static event EventHandler<ServerManagerEventArgs> EventPublished;

        public static ServerManagerStatusSnapshot GetStatus()
        {
            return ServerEventRuntime.GetStatusSnapshot();
        }

        public static IReadOnlyList<ServerManagerPlayerSnapshot> GetPlayers()
        {
            return GetStatus().Players;
        }

        public static Task<ServerManagerCommandResult> RequestWorldSaveAsync()
        {
            return ServerEventRuntime.EnqueueCommand(
                ServerEventCommandKind.Save,
                string.Empty,
                string.Empty);
        }

        public static Task<ServerManagerCommandResult> AnnounceAsync(
            string message)
        {
            return ServerEventRuntime.EnqueueCommand(
                ServerEventCommandKind.Announce,
                message,
                string.Empty);
        }

        public static Task<ServerManagerCommandResult> KickAsync(
            string target,
            string reason)
        {
            return ServerEventRuntime.EnqueueCommand(
                ServerEventCommandKind.Kick,
                target,
                reason);
        }

        public static Task<ServerManagerCommandResult> BanAsync(
            string target,
            string reason)
        {
            return ServerEventRuntime.EnqueueCommand(
                ServerEventCommandKind.Ban,
                target,
                reason);
        }

        public static Task<ServerManagerCommandResult> UnbanAsync(
            string target,
            string reason)
        {
            return ServerEventRuntime.EnqueueCommand(
                ServerEventCommandKind.Unban,
                target,
                reason);
        }

        internal static void Publish(ServerManagerEvent value)
        {
            if (value == null || ServerManagerEventKinds.IsOperatorAudit(value.Kind))
            {
                return;
            }

            EventHandler<ServerManagerEventArgs> handlers = EventPublished;
            if (handlers == null)
            {
                return;
            }

            BlockingCollection<EventDispatchItem> queue = _dispatchQueue;
            if (queue == null || queue.IsAddingCompleted)
            {
                return;
            }

            EventHandler<ServerManagerEventArgs>[] subscribers =
                Array.ConvertAll(
                    handlers.GetInvocationList(),
                    handler =>
                        (EventHandler<ServerManagerEventArgs>)handler);
            bool queued;
            try
            {
                queued = queue.TryAdd(
                    new EventDispatchItem(value, subscribers));
            }
            catch (InvalidOperationException)
            {
                // StopDispatcher may complete the bounded queue after the
                // lock-free publisher snapshot above. Shutdown must never
                // turn that expected race into a main-thread exception.
                return;
            }

            if (queued)
            {
                return;
            }

            int dropped = Interlocked.Increment(ref _droppedEvents);
            if (dropped == 1 || dropped % 100 == 0)
            {
                ServerManagerPlugin.Log.LogWarning(
                    "ServerManager integration subscribers fell behind; " +
                    dropped + " event(s) were dropped from the bounded queue.");
            }
        }

        internal static void StartDispatcher()
        {
            lock (DispatchGate)
            {
                if (_dispatchQueue != null)
                {
                    return;
                }

                _droppedEvents = 0;
                BlockingCollection<EventDispatchItem> queue =
                    new BlockingCollection<EventDispatchItem>(
                        new ConcurrentQueue<EventDispatchItem>(),
                        EventDispatchCapacity);
                _dispatchQueue = queue;
                // Stop may retire this generation before its worker first runs.
                // The worker must drain the queue it owns, even after a restart.
                _dispatchThread = new Thread(() => DispatchLoop(queue))
                {
                    IsBackground = true,
                    Name = "ServerManager integration events"
                };
                _dispatchThread.Start();
            }
        }

        internal static void StopDispatcher()
        {
            BlockingCollection<EventDispatchItem> queue;
            Thread thread;
            lock (DispatchGate)
            {
                queue = _dispatchQueue;
                thread = _dispatchThread;
                _dispatchQueue = null;
                _dispatchThread = null;
            }

            if (queue == null)
            {
                return;
            }

            queue.CompleteAdding();
            if (thread != null && !thread.Join(TimeSpan.FromSeconds(2)))
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Timed out while draining ServerManager integration events.");
            }
        }

        private static void DispatchLoop(
            BlockingCollection<EventDispatchItem> queue)
        {
            try
            {
                foreach (EventDispatchItem item in
                         queue.GetConsumingEnumerable())
                {
                    ServerManagerEventArgs arguments = new(item.Event);
                    foreach (EventHandler<ServerManagerEventArgs> handler in
                             item.Subscribers)
                    {
                        try
                        {
                            handler(null, arguments);
                        }
                        catch (Exception exception)
                        {
                            ServerManagerPlugin.Log.LogWarning(
                                "A ServerManager event subscriber failed: " +
                                exception.GetType().Name + ": " +
                                exception.Message);
                        }
                    }
                }
            }
            catch (Exception exception) when (
                exception is ObjectDisposedException ||
                exception is InvalidOperationException)
            {
                ServerManagerPlugin.Log.LogDebug(
                    "ServerManager integration event dispatcher stopped: " +
                    exception.GetType().Name);
            }
        }

        private sealed class EventDispatchItem
        {
            internal EventDispatchItem(
                ServerManagerEvent value,
                EventHandler<ServerManagerEventArgs>[] subscribers)
            {
                Event = value;
                Subscribers = subscribers;
            }

            internal ServerManagerEvent Event { get; }

            internal EventHandler<ServerManagerEventArgs>[] Subscribers
            {
                get;
            }
        }
    }
}
