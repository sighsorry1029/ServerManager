using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Bootstrap;
using HarmonyLib;
using ServerManager.Commands;
using ServerManager.Events;
using ServerManager.PlayerLogging;
using Steamworks;

namespace ServerManager;

/// <summary>
/// Composes the integrity, connection-gating, and authoritative-character
/// subsystems. All Unity and ZRpc mutations are performed on the main thread.
/// </summary>
internal static partial class ServerManagerRuntime
{
    private const string ServerCharactersPluginGuid =
        "org.bepinex.plugins.servercharacters";
    private const string LegacyOrbEventsPluginGuid =
        "blizz.OrbOfDiscord.Events";
    internal const string ProtocolRpcName = "sighsorry.ServerManager.Protocol.v1";
    internal const string DetectionRpcName = "sighsorry.ServerManager.Detection.v1";
    internal const string AdminEntitlementRpcName =
        "sighsorry.ServerManager.AdminEntitlement.v1";
    internal const string EventReportRpcName =
        "sighsorry.ServerManager.EventReport.v1";
    internal const string EventDisplayRpcName =
        "sighsorry.ServerManager.EventDisplay.v2";

    private static readonly Dictionary<ZRpc, BufferedWorldSocket> WorldBuffers = new();
    private static readonly Dictionary<ZRpc, long> PendingDisconnects = new();
    private static readonly Dictionary<ZRpc, ServerDetectionState>
        ServerDetectionStates = new();
    private static readonly Dictionary<ZRpc, ServerFinalSaveDrain>
        ServerFinalSaveDrains = new();
    private static readonly Dictionary<ZRpc, uint>
        ServerEventDisplaySequences = new();
    private static readonly ClientDetectionAgent ClientDetection = new();
    private static readonly object SteamAuthenticationGate = new();
    private static readonly Dictionary<ZRpc, SteamAuthenticationAttempt>
        SteamAuthenticationsByRpc = new();
    private static readonly Dictionary<ulong, SteamAuthenticationAttempt>
        SteamAuthenticationsById = new();
    private static readonly Dictionary<ulong, long> RetiredSteamIds = new();
    private static readonly HashSet<ulong>
        QuarantinedIncompleteSteamIds = new();
    private static readonly ConcurrentQueue<SteamAuthenticationCallbackEvent>
        SteamAuthenticationCallbacks = new();
    private static readonly ConcurrentQueue<WorldSaveWorkerResult>
        WorldSaveWorkerResults = new();
    private static readonly object WorldSaveCheckpointGate = new();
    private static WorldSaveAttempt? _invokedWorldSaveAttempt;
    private static WorldSaveAttempt? _preparedWorldSaveAttempt;
    private static readonly Dictionary<string, PendingCharacterDiskWrite>
        PendingCharacterDiskWrites = new(CharacterStorageLayout.StorageKeyComparer);
    private static readonly List<PendingCharacterCheckpointAdoption>
        PendingCharacterCheckpointAdoptions = new();
    [ThreadStatic]
    private static WorldSaveAttempt? _activeWorldSaveWorkerAttempt;
    private static int _queuedSteamAuthenticationCallbackCount;
    private static int _queuedWorldSaveWorkerResultCount;
    private static readonly Dictionary<string, SaveAdmissionWindow>
        SaveAdmissionHistory = new(CharacterStorageLayout.StorageKeyComparer);
    private static readonly Queue<SaveByteAdmission> GlobalSaveAdmissions = new();
    private static long _globalSaveAdmissionBytes;
    private static readonly HashSet<ZRpc> RegistrationFailures = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static IntegrityLimits _integrityLimits = null!;
    private static ConnectionProtocolLimits _connectionLimits = null!;
    private static FragmentTransportLimits _fragmentLimits = null!;
    private static FragmentTransportLimits _serverInboundFragmentLimits = null!;
    private static CharacterStorageOptions _serverCharacterOptions = null!;
    private static CharacterStorageOptions _clientCharacterOptions = null!;
    private static CharacterSemanticPolicy? _storedSnapshotSemanticPolicy;
    private static CharacterSemanticPolicy? _incomingSaveSemanticPolicy;
    private static ConnectionSessionCoordinator _coordinator = null!;
    private static BoundedFragmentReassembler _serverReassembler = null!;
    private static BoundedFragmentReassembler _clientReassembler = null!;
    private static RuntimeManifestValidator _manifestValidator = null!;

    private static ServerIntegrityService? _integrityService;
    private static CharacterEnvelopeCodec? _characterEnvelopeCodec;
    private static ValheimPlayerProfileCodec? _profileCodec;
    private static CharacterSnapshotService? _serverCharacterService;
    private static ClientConnection? _client;
    private static ZNet? _manifestPreparationNetwork;
    private static PluginManifestScanner.Preparation? _pendingManifestPreparation;
    private static DeferredClientExit? _deferredClientExit;
    private static ClientGameplayQuiescence? _clientGameplayQuiescence;
    private static Callback<ValidateAuthTicketResponse_t>? _steamAuthenticationCallback;
    private static bool _steamAuthenticationCallbackUnavailable;
    private static bool _initialized;
    private static bool _shuttingDown;
    private static long _nextSaveAdmissionCleanupTimestamp;
    private static long _nextCharacterSaveHealthCheckTimestamp;

    private const string UnsupportedTransportMessage =
        "ServerManager is Steamworks-only and is incompatible with " +
        "PlayFab/crossplay or custom socket transports. Disable -crossplay " +
        "on the server and connect through Steamworks.";
    private const string SteamAuthenticationUnavailableMessage =
        "ServerManager could not register the final Steam authentication " +
        "callback. The server listener will remain closed.";
    internal const string ClientSecurityPolicyRejectionMessage =
        "The server security policy ended this connection. " +
        "If you believe this is an error, contact a server administrator.";
    private const int MaximumSteamAuthenticationReservations = 64;
    private const int MaximumQuarantinedIncompleteSteamIds = 1024;
    private const int MaximumQueuedSteamCallbacksPerAttempt = 2;
    private const int MaximumQueuedSteamAuthenticationCallbacks =
        MaximumSteamAuthenticationReservations *
        MaximumQueuedSteamCallbacksPerAttempt;
    private const int MaximumQueuedWorldSaveWorkerResults = 64;
    private const int MaximumClientDetectionReportsPerTick = 4;
    private const int MaximumCommandReportsPerWindow = 64;
    private const int CommandReportWindowSeconds = 10;
    private const int AdminEntitlementGrantMilliseconds = 5000;
    private const int GracefulExitSaveTimeoutSeconds = 65;
    private const int OperationalKickSaveTimeoutSeconds = 5;
    private const int MaximumBufferedWorldBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan ConnectionHandshakeTimeout =
        TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CharacterFragmentAssemblyTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CharacterShadowHealthWarningThreshold =
        TimeSpan.FromMinutes(45);
    private static readonly long RetiredSteamIdQuietTicks =
        checked(2L * Stopwatch.Frequency);
    private static readonly long DetectionKickAggregationTicks =
        Math.Max(1L, Stopwatch.Frequency / 4L);
    private static readonly long AdminEntitlementRefreshTicks =
        Math.Max(1L, checked(2L * Stopwatch.Frequency));
    private static readonly long ClientSavePacingWindowTicks =
        checked(11L * Stopwatch.Frequency);
    private static readonly long CharacterSaveHealthCheckTicks =
        checked(30L * Stopwatch.Frequency);
    private static readonly long CharacterCheckpointShutdownRetryTicks =
        checked(5L * Stopwatch.Frequency);
    private static readonly long ClientSaveAcknowledgementTimeoutTicks =
        checked(30L * Stopwatch.Frequency);
    private const int FullProfileHeartbeatIntervalSeconds = 300;
    private const int FullProfileHeartbeatInitialSpreadSeconds = 30;
    private static readonly long FullProfileHeartbeatIntervalTicks =
        checked((long)FullProfileHeartbeatIntervalSeconds * Stopwatch.Frequency);
    private static readonly long FullProfileSafetySaveMinimumIntervalTicks =
        checked(30L * Stopwatch.Frequency);
    private static readonly long InventoryFastSaveCoalesceTicks =
        Math.Max(1L, Stopwatch.Frequency / 4L);
    private static readonly long InventoryFastSaveMinimumIntervalTicks =
        Math.Max(1L, Stopwatch.Frequency);
    private static readonly long GracefulExitSaveTimeoutTicks =
        checked((long)GracefulExitSaveTimeoutSeconds * Stopwatch.Frequency);
    private static readonly long OperationalKickSaveTimeoutTicks =
        checked((long)OperationalKickSaveTimeoutSeconds * Stopwatch.Frequency);
    private static bool _steamAuthenticationGenerationCapacityExhausted;
    private static bool _continueLogoutPassThrough;
    private static bool _applicationQuitResumePending;
    private static bool _suppressClientSaveCapture;
    private static int _inventoryLoadSuppressionDepth;
    private static bool _listenServerDamageLimitLogged;
    private static bool _serverDataRootStartupFailed;
    private static ZNet? _preparedServerNetwork;
    private static ZNet? _localHostNetwork;
    private static bool _localHostRequested;
    private static bool _localHostStartupFailed;
    private static bool _localHostFailureExitPending;
    private static ServerCharacterStorageStartupState
        _serverCharacterStorageStartupState;

    private enum ServerCharacterStorageStartupState
    {
        NotRun = 0,
        Ready = 1,
        Failed = 2
    }

    internal enum SteamAuthenticationPhase
    {
        Reserved = 0,
        PeerInfoPending = 1,
        VanillaAccepted = 2,
        Active = 3,
        Rejected = 4
    }

    internal sealed class SteamAuthenticationAttempt
    {
        internal SteamAuthenticationAttempt(
            ZNetPeer peer,
            ZRpc rpc,
            ZSteamSocket socket,
            CSteamID steamId,
            long deadlineTimestamp)
        {
            Peer = peer;
            Rpc = rpc;
            Socket = socket;
            SteamId = steamId;
            DeadlineTimestamp = deadlineTimestamp;
        }

        internal ZNetPeer Peer { get; }

        internal ZRpc Rpc { get; }

        internal ZSteamSocket Socket { get; }

        internal CSteamID SteamId { get; }

        internal long DeadlineTimestamp { get; set; }

        internal SteamAuthenticationPhase Phase { get; set; }

        internal int EnqueuedCallbackCount { get; set; }

        internal int ProcessedCallbackCount { get; set; }

        internal int PendingCallbackCount { get; set; }

        internal bool CallbackOverflowed { get; set; }

        internal bool StaleCallbackCaptured { get; set; }

        internal bool LateCallbackCaptured { get; set; }

        internal long FirstCallbackReceivedTimestamp { get; set; }

        internal bool BeginAuthInvocationObserved { get; set; }

        internal bool BeginAuthResultRecorded { get; set; }

        internal bool BeginAuthImmediateAccepted { get; set; }

        internal bool BeginAuthExecutionFaulted { get; set; }

        internal bool DuplicateBeginAuthInvocation { get; set; }

        internal bool ReachedActive { get; set; }

        internal bool ActiveRevocationCaptured { get; set; }

        internal EAuthSessionResponse LatestResponse { get; set; }
    }

    private readonly struct SteamAuthenticationCallbackEvent
    {
        internal SteamAuthenticationCallbackEvent(
            SteamAuthenticationAttempt attempt,
            ValidateAuthTicketResponse_t response,
            SteamAuthenticationPhase phaseAtCapture,
            bool beginAuthObservedAtCapture,
            long receivedTimestamp)
        {
            Attempt = attempt;
            Response = response;
            PhaseAtCapture = phaseAtCapture;
            BeginAuthObservedAtCapture = beginAuthObservedAtCapture;
            ReceivedTimestamp = receivedTimestamp;
        }

        internal SteamAuthenticationAttempt Attempt { get; }

        internal ValidateAuthTicketResponse_t Response { get; }

        internal SteamAuthenticationPhase PhaseAtCapture { get; }

        internal bool BeginAuthObservedAtCapture { get; }

        internal long ReceivedTimestamp { get; }
    }

    private sealed class SaveAdmissionWindow
    {
        internal Queue<SaveByteAdmission> Admissions { get; } = new();

        internal long DecodedBytes { get; set; }

        internal long LastTouched { get; set; }
    }

    private sealed class SaveByteAdmission
    {
        internal SaveByteAdmission(long timestamp, int byteCount)
        {
            Timestamp = timestamp;
            ByteCount = byteCount;
        }

        internal long Timestamp { get; }

        internal int ByteCount { get; }
    }

    private sealed class ServerDetectionState
    {
        internal ServerDetectionState(
            ProtocolChallengeOptions policy,
            DetectionAction cheatDetectionResponse,
            DetectionAction statLimitResponse,
            int cheatCommandThreshold,
            int cheatCommandWindowSeconds)
        {
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            CheatDetectionResponse = cheatDetectionResponse;
            StatLimitResponse = statLimitResponse;
            CheatCommandThreshold = cheatCommandThreshold;
            CheatCommandWindowSeconds = cheatCommandWindowSeconds;
        }

        internal ProtocolChallengeOptions Policy { get; set; }

        // Admission mode is fixed for this connection; YAML reload only changes
        // the mode latched by later connections.
        internal bool BackupOnly { get; set; }
        internal bool BackupCaptureRequested { get; set; }
        internal string BackupCaptureStorageKey { get; set; } = string.Empty;
        internal IReadOnlyList<CharacterStatLimitFinding> BackupCaptureStatFindings { get; set; } =
            Array.Empty<CharacterStatLimitFinding>();

        internal DetectionAction CheatDetectionResponse { get; set; }

        internal DetectionAction StatLimitResponse { get; set; }

        internal uint PolicyGeneration { get; set; } = 1;
        internal uint SentPolicyGeneration { get; set; } = 1;
        internal uint AcknowledgedPolicyGeneration { get; set; } = 1;
        internal long PolicyAcknowledgementDeadline { get; set; }

        internal int CheatCommandThreshold { get; }

        internal int CheatCommandWindowSeconds { get; }

        internal uint LastSequence { get; set; }

        internal bool CheatCommandThresholdActionApplied { get; set; }
        internal uint CheatCommandActionGeneration { get; set; }

        internal bool TerminalActionApplied { get; set; }

        internal bool UnattributedOversizedDamageLogged { get; set; }

        internal long PendingKickTimestamp { get; set; }

        internal DetectionEvidence TerminalEvidence { get; set; }

        internal string TerminalSource { get; set; } = "server_observed";

        internal uint NextAdminEntitlementSequence { get; set; } = 1;

        internal long NextAdminEntitlementRefreshTimestamp { get; set; }

        internal bool? LastAdminEntitlementGranted { get; set; }

        internal Dictionary<DetectionEvidence, uint> StickyEvidence { get; } = new();

        internal Dictionary<DetectionEvidence, uint> ServerObservedEvidence { get; } = new();

        internal Queue<long> CheatCommandTimestamps { get; } = new();

        internal Queue<long> CommandReportTimestamps { get; } = new();
    }

    private sealed class ServerFinalSaveDrain
    {
        internal ServerFinalSaveDrain(long deadlineTimestamp)
            : this(deadlineTimestamp, false)
        {
        }

        internal ServerFinalSaveDrain(long deadlineTimestamp, bool operationalKick)
        {
            DeadlineTimestamp = deadlineTimestamp;
            OperationalKick = operationalKick;
            GateStarted = !operationalKick;
        }

        internal long DeadlineTimestamp { get; }

        internal bool OperationalKick { get; }

        internal bool GateStarted { get; set; }

        internal bool SaveAcknowledged { get; set; }

        internal bool InboundBarrierCompleted { get; set; }

        internal bool SaveAssemblyAdmitted { get; set; }

        internal bool TimeoutRejected { get; set; }
    }

    private sealed class WorldSaveAttempt
    {
        internal WorldSaveAttempt(string operationId)
        {
            OperationId = operationId ?? string.Empty;
        }

        internal string OperationId { get; }

        internal CharacterCheckpointBatch? CharacterCheckpoint { get; set; }

        internal CharacterSnapshotService? CharacterService { get; set; }

        internal string CharacterCheckpointWarning { get; set; } = string.Empty;

        internal bool PrimaryWorldSaveSucceeded { get; set; }
    }

    private readonly struct WorldSaveWorkerResult
    {
        internal WorldSaveWorkerResult(WorldSaveAttempt attempt)
        {
            Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        }

        internal WorldSaveAttempt Attempt { get; }
    }

    private sealed class PendingCharacterCheckpointEntry
    {
        internal PendingCharacterCheckpointEntry(
            string operationId,
            CharacterSnapshotService service,
            CharacterCheckpointBatch checkpoint,
            CharacterCheckpointEntry entry)
        {
            OperationId = operationId ?? string.Empty;
            Service = service ?? throw new ArgumentNullException(nameof(service));
            Checkpoint = checkpoint ??
                throw new ArgumentNullException(nameof(checkpoint));
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        }

        internal string OperationId { get; }

        internal CharacterSnapshotService Service { get; }

        internal CharacterCheckpointBatch Checkpoint { get; }

        internal CharacterCheckpointEntry Entry { get; }
    }

    private sealed class PendingCharacterDiskWrite
    {
        internal PendingCharacterDiskWrite(
            PendingCharacterCheckpointEntry head,
            long nextAttemptTimestamp)
        {
            Head = head ?? throw new ArgumentNullException(nameof(head));
            NextAttemptTimestamp = nextAttemptTimestamp;
        }

        internal PendingCharacterCheckpointEntry Head { get; set; }

        internal PendingCharacterCheckpointEntry? Tail { get; set; }

        internal long NextAttemptTimestamp { get; set; }

        internal int FailureCount { get; set; }

        internal string LastError { get; set; } = string.Empty;
    }

    private sealed class PendingCharacterCheckpointAdoption
    {
        internal PendingCharacterCheckpointAdoption(
            PendingCharacterCheckpointEntry entry,
            Exception exception)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            RecordFailure(exception);
        }

        internal PendingCharacterCheckpointEntry Entry { get; }

        internal long NextAttemptTimestamp { get; private set; }

        internal int FailureCount { get; private set; }

        internal string LastError { get; private set; } = string.Empty;

        internal void RecordFailure(Exception exception)
        {
            if (FailureCount < int.MaxValue)
            {
                ++FailureCount;
            }

            LastError = exception.GetType().Name + ": " + exception.Message;
            NextAttemptTimestamp = AddStopwatchDuration(
                Stopwatch.GetTimestamp(),
                GetCharacterCheckpointRetryTicks(FailureCount));
        }
    }

    private sealed class RuntimeManifestValidator : IManifestValidator
    {
        private readonly Dictionary<ZRpc, (string HostId, IntegrityManifest Manifest)>
            _pendingAdminManifests = new();

        internal void RemovePeer(ZRpc rpc)
        {
            _pendingAdminManifests.Remove(rpc);
        }

        internal void Clear()
        {
            _pendingAdminManifests.Clear();
        }

        public ManifestValidationDecision Validate(
            ServerPeerIdentity peerIdentity,
            byte[] manifestPayload)
        {
            try
            {
                _pendingAdminManifests.Remove(peerIdentity.Rpc);
                ManifestValidationDecision decision =
                    EnsureIntegrityService().ValidateForAdmission(
                        peerIdentity, manifestPayload,
                        allowAuthenticatedAdminReview: true,
                        out IntegrityManifest? pendingManifest);
                if (decision.Accepted && pendingManifest != null)
                {
                    if (_pendingAdminManifests.Count >= MaximumSteamAuthenticationReservations)
                    {
                        return ManifestValidationDecision.Reject(
                            "The server has too many pending authenticated mod checks.");
                    }
                    _pendingAdminManifests.Add(peerIdentity.Rpc,
                        (peerIdentity.HostId, pendingManifest));
                }
                return decision;
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogError(
                    "Mod folder policy validation could not start: " +
                    exception.GetType().Name +
                    ": " +
                    exception.Message);
                return ManifestValidationDecision.Reject(
                    "The server could not build a valid mod folder policy.",
                    ProtocolRejectCode.ManifestValidatorFailed);
            }
        }

        internal ManifestValidationDecision ConfirmAuthenticated(
            ZNet server,
            ServerPeerIdentity identity)
        {
            if (!_pendingAdminManifests.TryGetValue(identity.Rpc, out var pending))
            {
                return ManifestValidationDecision.Accept();
            }
            _pendingAdminManifests.Remove(identity.Rpc);
            if (!string.Equals(pending.HostId, identity.HostId, StringComparison.Ordinal))
            {
                return ManifestValidationDecision.Reject(
                    "The connection identity changed during mod validation.");
            }

            ManifestValidationDecision decision =
                EnsureIntegrityService().ConfirmAdminManifest(
                    identity, pending.Manifest, IsCurrentServerAdmin(server, identity),
                    out IReadOnlyList<IntegrityDiagnostic> exemptions);
            if (decision.Accepted)
            {
                RecordAdminExemptions(identity, exemptions);
            }
            return decision;
        }

        private static void RecordAdminExemptions(ServerPeerIdentity identity,
            IReadOnlyList<IntegrityDiagnostic> exemptions)
        {
            // Observations are client reported; only the exception's account
            // and authorization come from authenticated Steam.
            foreach (IntegrityDiagnostic diagnostic in exemptions.Take(20))
                ServerEventRuntime.RecordSecurityEvent(
                    ServerManagerEventKinds.SecurityAdminBypass,
                    CharacterSteamIdentity.AccountPrefix + identity.HostId,
                    identity.PlayerName, "client_reported", diagnostic.Code,
                    DetectionAction.Log, "mod_policy_bypassed",
                    diagnostic.PluginGuid + ": " + diagnostic.Message);
        }
    }

    private sealed class ClientConnection
    {
        internal ClientConnection(ZRpc rpc)
        {
            Rpc = rpc;
            long now = Stopwatch.GetTimestamp();
            DeadlineTimestamp = now +
                (long)Math.Ceiling(
                    _connectionLimits.OverallHandshakeTimeout.TotalSeconds *
                    Stopwatch.Frequency);
            SavePipeline = new ClientCharacterSavePipeline(
                ClientSavePacingWindowTicks,
                ClientSaveAcknowledgementTimeoutTicks);
        }

        internal ZRpc Rpc { get; }

        internal long DeadlineTimestamp { get; }

        internal bool Failed { get; set; }

        internal byte[]? SessionId { get; set; }

        internal byte[]? Nonce { get; set; }

        internal bool ChallengeReceived { get; set; }

        internal ZNet? Network { get; set; }
        internal PluginManifestScanner.Preparation? ManifestPreparation { get; set; }
        internal IntegrityLimits? ManifestResponseLimits { get; set; }
        internal bool ManifestResponseSent { get; set; }

        internal bool ManifestAccepted { get; set; }

        internal bool PeerInfoSent { get; set; }

        internal string? HeldPassword { get; set; }

        internal bool InitialTransferCompleted { get; set; }

        internal bool ReadyAcknowledgementSent { get; set; }
        internal bool FreshCharacterRejectionReported { get; set; }
        internal bool BackupOnly { get; set; }
        internal bool BackupCaptureCommitted { get; set; }
        internal byte[]? BackupCaptureHash { get; set; }

        internal uint NextDetectionSequence { get; set; } = 1;

        internal uint PolicyGeneration { get; set; } = 1;

        internal uint NextAdminEntitlementSequence { get; set; } = 1;

        internal uint NextEventReportSequence { get; set; } = 1;

        internal uint LastEventDisplaySequence { get; set; }

        internal bool ServerCharacterActive { get; set; }

        internal PlayerProfile? OriginalProfile { get; set; }

        internal PlayerProfile? ManagedProfile { get; set; }

        internal CharacterClientState? CharacterState { get; set; }

        internal ClientCharacterSavePipeline SavePipeline { get; }

        internal long FullProfileSafetySaveDueTimestamp { get; set; }

        internal long NextFullProfileSafetySaveAllowedTimestamp { get; set; }

        internal long NextFullProfileHeartbeatTimestamp { get; set; }

        internal long InventoryFastSaveDueTimestamp { get; set; }

        internal long NextInventoryFastSaveAllowedTimestamp { get; set; }

        internal bool InventoryFastPathReady { get; set; }

        internal byte[]? AcknowledgedProfileBytes { get; set; }

        internal long AcknowledgedProfileRevision { get; set; }
    }

    private enum DeferredClientExitKind
    {
        Logout = 1,
        ApplicationQuit = 2,
        OperationalKick = 3
    }

    private sealed class DeferredClientExit
    {
        internal DeferredClientExit(
            DeferredClientExitKind kind,
            long deadlineTimestamp,
            Game? game = null,
            bool save = false,
            bool shouldExit = false,
            bool changeToStartScene = false)
        {
            Kind = kind;
            DeadlineTimestamp = deadlineTimestamp;
            Game = game;
            Save = save;
            ShouldExit = shouldExit;
            ChangeToStartScene = changeToStartScene;
        }

        internal DeferredClientExitKind Kind { get; set; }

        internal long DeadlineTimestamp { get; }

        internal Game? Game { get; }

        internal bool Save { get; }

        internal bool ShouldExit { get; }

        internal bool ChangeToStartScene { get; }

        internal ulong TargetCaptureId { get; set; }

        internal bool ServerGateAcknowledged { get; set; }

        internal bool FinalCaptureAttempted { get; set; }

        internal bool ClientWorldBarrierSent { get; set; }

        internal bool InventoryChangedAfterQuiescence { get; set; }

        internal bool ReadyStateResolved { get; set; }

        internal bool OperationalKickRequested { get; set; }

        internal bool OperationalCompletionSent { get; set; }
    }

    private sealed class ClientGameplayQuiescence
    {
        internal ClientGameplayQuiescence(
            Player player,
            bool playerWasEnabled,
            ZSyncTransform? syncTransform,
            bool syncTransformWasEnabled,
            UnityEngine.Rigidbody? body,
            UnityEngine.RigidbodyConstraints bodyConstraints,
            UnityEngine.Vector3 bodyVelocity,
            UnityEngine.Vector3 bodyAngularVelocity)
        {
            Player = player;
            PlayerWasEnabled = playerWasEnabled;
            SyncTransform = syncTransform;
            SyncTransformWasEnabled = syncTransformWasEnabled;
            Body = body;
            BodyConstraints = bodyConstraints;
            BodyVelocity = bodyVelocity;
            BodyAngularVelocity = bodyAngularVelocity;
        }

        internal Player Player { get; }

        internal bool PlayerWasEnabled { get; }

        internal ZSyncTransform? SyncTransform { get; }

        internal bool SyncTransformWasEnabled { get; }

        internal UnityEngine.Rigidbody? Body { get; }

        internal UnityEngine.RigidbodyConstraints BodyConstraints { get; }

        internal UnityEngine.Vector3 BodyVelocity { get; }

        internal UnityEngine.Vector3 BodyAngularVelocity { get; }
    }

    internal sealed class PeerInfoPatchState
    {
        internal bool IsServerPeerInfo { get; set; }

        internal BufferedWorldSocket? Buffer { get; set; }

        internal SteamAuthenticationAttempt? SteamAuthentication { get; set; }
    }

    internal static void Initialize()
    {
        if (_initialized)
        {
            throw new InvalidOperationException("ServerManager runtime is already initialized.");
        }

        ValheimPrivateAccess.ValidateRequiredMembers();

        try
        {
            _shuttingDown = false;
            StopOptionalModPublication();
            _deferredClientExit = null;
            _clientGameplayQuiescence = null;
            _continueLogoutPassThrough = false;
            _applicationQuitResumePending = false;
            _suppressClientSaveCapture = false;
            _inventoryLoadSuppressionDepth = 0;
            _listenServerDamageLimitLogged = false;
            _serverDataRootStartupFailed = false;
            _serverCharacterStorageStartupState =
                ServerCharacterStorageStartupState.NotRun;
            ResetWorldSaveCheckpointState();
            ResetServerSettings();
            ServerDataRoot.Release();
            int maximumManifestBytes = ConnectionProtocolLimits.AbsoluteMaxManifestBytes;
            int maximumCharacterBytes = CharacterStorageOptions.DefaultMaxPayloadBytes;
            int serverEnvelopeLimit = checked(
                maximumCharacterBytes +
                CharacterStorageOptions.DefaultMaxEnvelopeOverheadBytes);
            int maximumNetworkCharacterBytes = 32 * 1024 * 1024;
            int networkEnvelopeLimit = checked(
                maximumNetworkCharacterBytes +
                CharacterStorageOptions.DefaultMaxEnvelopeOverheadBytes);
            int networkManifestLimit =
                ConnectionProtocolLimits.AbsoluteMaxManifestBytes;
            int packetLimit = Math.Max(
                512 * 1024,
                checked(networkManifestLimit + ProtocolPacketCodec.FixedHeaderBytes));
            packetLimit = Math.Min(packetLimit, ConnectionProtocolLimits.AbsoluteMaxPacketBytes);

            _integrityLimits = new IntegrityLimits(maxPayloadBytes: maximumManifestBytes);
            _connectionLimits = new ConnectionProtocolLimits(
                maxPacketBytes: packetLimit,
                maxManifestBytes: networkManifestLimit,
                maxPeerInfoBytes: 512 * 1024,
                maxRejectMessageBytes: 1024,
                phaseTimeout: ConnectionHandshakeTimeout,
                overallHandshakeTimeout: ConnectionHandshakeTimeout);

            int reservedBytes = (int)Math.Min(
                128L * 1024L * 1024L,
                Math.Max(
                    (long)networkEnvelopeLimit,
                    (long)networkEnvelopeLimit * 4L));
            _fragmentLimits = new FragmentTransportLimits(
                maxFragmentDataBytes: ConnectionProtocolLimits.AbsoluteMaxFragmentDataBytes,
                maxFragmentCount: 256,
                maxEncodedMessageBytes: networkEnvelopeLimit,
                maxDecodedMessageBytes: networkEnvelopeLimit,
                maxConcurrentAssemblies: 32,
                maxConcurrentAssembliesPerPeer: 1,
                maxReservedBytes: reservedBytes,
                assemblyTimeout: CharacterFragmentAssemblyTimeout);
            int serverReservedBytes = (int)Math.Min(
                128L * 1024L * 1024L,
                Math.Max(
                    (long)serverEnvelopeLimit,
                    (long)serverEnvelopeLimit * 4L));
            _serverInboundFragmentLimits = new FragmentTransportLimits(
                maxFragmentDataBytes: ConnectionProtocolLimits.AbsoluteMaxFragmentDataBytes,
                maxFragmentCount: 256,
                maxEncodedMessageBytes: serverEnvelopeLimit,
                maxDecodedMessageBytes: serverEnvelopeLimit,
                maxConcurrentAssemblies: 32,
                maxConcurrentAssembliesPerPeer: 1,
                maxReservedBytes: serverReservedBytes,
                assemblyTimeout: CharacterFragmentAssemblyTimeout);

            _serverCharacterOptions = new CharacterStorageOptions
            {
                MaxPayloadBytes = maximumCharacterBytes,
                MaxEnvelopeBytes = serverEnvelopeLimit,
                MaxBackups = ServerSettings.Defaults.BackupsPerProfile,
                MaxCharactersPerAccount =
                    ServerSettings.Defaults.MaxCharactersPerAccount
            };
            _serverCharacterOptions.Validate();
            _clientCharacterOptions = new CharacterStorageOptions
            {
                MaxPayloadBytes = maximumNetworkCharacterBytes,
                MaxEnvelopeBytes = networkEnvelopeLimit,
                MaxBackups = 1
            };
            _clientCharacterOptions.Validate();

            _coordinator = new ConnectionSessionCoordinator(
                _connectionLimits,
                _fragmentLimits);
            _serverReassembler = new BoundedFragmentReassembler(
                _serverInboundFragmentLimits,
                allowCompressed: true,
                assemblyAdmission: AdmitClientSaveAssembly);
            _clientReassembler = new BoundedFragmentReassembler(
                _fragmentLimits);
            _manifestValidator = new RuntimeManifestValidator();
            _nextSaveAdmissionCleanupTimestamp = Stopwatch.GetTimestamp();
            _nextCharacterSaveHealthCheckTimestamp = Stopwatch.GetTimestamp();
            ServerDetectionStates.Clear();
            ServerFinalSaveDrains.Clear();
            ClientDetection.Reset();
            CheatCommandGuard.Reset();
            ServerEventRuntime.Initialize();
            Commands.ServerCommands.Initialize();
            _initialized = true;
        }
        catch
        {
            CleanupFailedInitialization();
            throw;
        }
    }

    private static void CleanupFailedInitialization()
    {
        StopOptionalModPublication();
        ServerScheduleRuntime.Stop();
        ShutdownAdminCommands();
        _initialized = false;
        _shuttingDown = true;

        RunInitializationCleanup(
            "event runtime",
            ServerEventRuntime.Shutdown);
        RunInitializationCleanup(
            "player activity runtime",
            () => PlayerActivityRuntime.Stop("initialization_rollback"));
        RunInitializationCleanup(
            "client detection",
            ClientDetection.Shutdown);
        RunInitializationCleanup(
            "cheat-command state",
            CheatCommandGuard.Reset);
        RunInitializationCleanup(
            "server fragment reassembler",
            () => _serverReassembler?.Dispose());
        RunInitializationCleanup(
            "client fragment reassembler",
            () => _clientReassembler?.Dispose());
        RunInitializationCleanup(
            "connection coordinator",
            () => _coordinator?.Dispose());
        RunInitializationCleanup(
            "server settings",
            ResetServerSettings);
        RunInitializationCleanup(
            "server data root",
            ServerDataRoot.Release);

        ResetWorldSaveCheckpointState();

        _serverReassembler = null!;
        _clientReassembler = null!;
        _coordinator = null!;
        _manifestValidator = null!;
        _shuttingDown = false;
    }

    private static void RunInitializationCleanup(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "ServerManager " + name + " initialization rollback failed: " +
                exception.Message);
        }
    }

    internal static void Tick()
    {
        if (!_initialized || _shuttingDown)
        {
            return;
        }

        RecoverCancelledApplicationQuit();
        TickServerSettings();
        SynchronizeServerPolicies();
        ProcessLocalHostStartupFailure();
        LocalHostCharacterRuntime.Tick();
        ProcessCompletedWorldSaveCheckpoints();
        _integrityService?.Tick();
        TickOptionalModPublication();
        CleanupSaveAdmissionHistory();
        ProcessExpiredFragmentAssemblies();
        MonitorServerCharacterSaveHealth();
        ProcessSteamAuthenticationCallbacks();
        ExpireSteamAuthentications();
        PruneRetiredSteamIds();
        RefreshAdminCommandEntitlements();
        ProcessPendingDetectionKicks();
        ProcessExpiredServerFinalSaveDrains();
        PlayerActivityRuntime.Tick();
        Discord.DiscordRuntime.Tick();
        ServerEventRuntime.Tick();
        ServerScheduleRuntime.Tick();
        TickAdminCommands();
        ClientConnection? activeClient = _client;
        ClientDetection.Tick(
            activeClient != null &&
            !activeClient.Failed &&
            activeClient.ReadyAcknowledgementSent);
        DrainClientDetectionSignals();

        ClientConnection? client = _client;
        if (client != null)
        {
            ProcessClientCharacterSavePipeline(client);
        }

        ProcessDeferredClientExit();
        client = _client;
        if (client != null &&
            !client.Failed &&
            !client.ReadyAcknowledgementSent &&
            Stopwatch.GetTimestamp() > client.DeadlineTimestamp)
        {
            ServerManagerPlugin.Log.LogWarning(
                "Connection handshake deadline reached: manifestAccepted=" +
                client.ManifestAccepted + ", peerInfoSent=" + client.PeerInfoSent +
                ", initialTransferCompleted=" + client.InitialTransferCompleted + ".");
            FailClient(
                client.Rpc,
                "The ServerManager connection handshake timed out.");
        }

        // Poll only after the timeout check; a late worker cannot revive an
        // expired handshake. Unity/RPC work stays on this owning thread.
        if (_client != null)
        {
            ProcessClientManifestPreparation(_client);
        }

        foreach (ConnectionSessionSnapshot expired in _coordinator.ExpireTimedOutSessions())
        {
            ProtocolRejection rejection =
                expired.Rejection ??
                new ProtocolRejection(
                    ProtocolRejectCode.HandshakeTimedOut,
                    "The connection handshake timed out.");
            SendServerRejection(expired.Rpc, rejection, markRejected: false);
        }

        foreach (KeyValuePair<ZRpc, BufferedWorldSocket> pair in
                 WorldBuffers.ToArray())
        {
            if (pair.Value.Quarantined)
            {
                continue;
            }

            if (pair.Value.Overflowed)
            {
                SendServerRejection(
                    pair.Key,
                    new ProtocolRejection(
                        ProtocolRejectCode.PayloadTooLarge,
                        "World synchronization exceeded the server buffer limit."));
            }
            else if (pair.Value.InboundViolation)
            {
                SendServerRejection(
                    pair.Key,
                    new ProtocolRejection(
                        ProtocolRejectCode.InvalidTransition,
                        "Gameplay traffic arrived before character readiness."));
            }
        }

        long now = Stopwatch.GetTimestamp();
        foreach (KeyValuePair<ZRpc, long> pair in PendingDisconnects.ToArray())
        {
            if (pair.Value > now)
            {
                continue;
            }

            PendingDisconnects.Remove(pair.Key);
            DisconnectServerPeer(pair.Key);
        }
    }

    private static void ProcessExpiredFragmentAssemblies()
    {
        foreach (ExpiredFragmentAssembly expired in
                 _serverReassembler.CleanupExpired())
        {
            ServerManagerPlugin.Log.LogWarning(
                "Server character fragment assembly timed out: message=" +
                expired.MessageIdDiagnostic +
                ". The peer session will be terminated.");
            SendServerRejection(
                expired.PeerRpc,
                new ProtocolRejection(
                    ProtocolRejectCode.FragmentAssemblyTimedOut,
                    "The character transfer timed out."));
        }

        foreach (ExpiredFragmentAssembly expired in
                 _clientReassembler.CleanupExpired())
        {
            ClientConnection? client = _client;
            if (client == null ||
                client.Failed ||
                !ReferenceEquals(client.Rpc, expired.PeerRpc))
            {
                continue;
            }

            ServerManagerPlugin.Log.LogWarning(
                "Client character fragment assembly timed out: message=" +
                expired.MessageIdDiagnostic +
                ". The connection will be terminated.");
            FailClient(
                expired.PeerRpc,
                "The server character transfer timed out.");
        }
    }

    private static void MonitorServerCharacterSaveHealth()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextCharacterSaveHealthCheckTimestamp)
        {
            return;
        }

        _nextCharacterSaveHealthCheckTimestamp =
            AddStopwatchDuration(now, CharacterSaveHealthCheckTicks);
        CharacterSnapshotService? service = _serverCharacterService;
        if (service == null)
        {
            return;
        }

        foreach (CharacterSessionSaveHealthSnapshot snapshot in
                 service.ClaimLongUnsavedWarnings(CharacterShadowHealthWarningThreshold))
        {
            ServerEventRuntime.RecordCharacterShadowWarning(
                snapshot,
                service.TryGetLocalHostSession(snapshot.SessionId, out _)
                    ? "incoming_host"
                    : "incoming");
        }
    }

    internal static void Shutdown()
    {
        StopOptionalModPublication();
        if (!_initialized || _shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        ServerScheduleRuntime.Stop();
        CancelPendingManifestPreparation();
        CancelClientManifestPreparation(_client);
        ShutdownAdminCommands();
        DisposeSteamAuthenticationCallback();
        _client?.SavePipeline.Close();
        _deferredClientExit = null;
        ReleaseClientGameplayQuiescence(restorePlayer: true);
        _continueLogoutPassThrough = false;
        _applicationQuitResumePending = false;
        _suppressClientSaveCapture = false;
        _inventoryLoadSuppressionDepth = 0;
        _listenServerDamageLimitLogged = false;
        RestoreClientProfile();

        foreach (KeyValuePair<ZRpc, BufferedWorldSocket> pair in
                 WorldBuffers.ToArray())
        {
            RestoreAndDiscardBuffer(pair.Key, pair.Value);
        }

        PlayerActivityRuntime.Stop("runtime_shutdown");

        WorldBuffers.Clear();
        PendingDisconnects.Clear();
        ServerDetectionStates.Clear();
        ServerFinalSaveDrains.Clear();
        ServerEventDisplaySequences.Clear();
        SaveAdmissionHistory.Clear();
        GlobalSaveAdmissions.Clear();
        _globalSaveAdmissionBytes = 0;
        RegistrationFailures.Clear();
        ProcessCompletedWorldSaveCheckpoints();
        DrainPendingCharacterCheckpointBeforeShutdown();
        LocalHostCharacterRuntime.Close();
        ResetLocalHostLifecycle();
        _serverCharacterService?.Dispose();
        _serverCharacterService = null;
        _storedSnapshotSemanticPolicy = null;
        _incomingSaveSemanticPolicy = null;
        _characterEnvelopeCodec = null;
        _profileCodec = null;
        _client = null;
        _serverCharacterStorageStartupState =
            ServerCharacterStorageStartupState.NotRun;
        _serverDataRootStartupFailed = false;
        ClientDetection.Shutdown();
        CheatCommandGuard.Reset();
        ServerEventRuntime.Shutdown();

        _integrityService?.Dispose();
        _integrityService = null;
        _manifestValidator?.Clear();
        _serverReassembler.Dispose();
        _clientReassembler.Dispose();
        _coordinator.Dispose();
        ServerDataRoot.Release();
        ResetWorldSaveCheckpointState();

        _initialized = false;
    }

    internal static bool BeforeNetworkStart(ZNet znet)
    {
        if (!ReferenceEquals(znet, _unsafePlayerLoadNetwork))
        {
            _unsafePlayerLoadGame = null;
            _unsafePlayerLoadNetwork = null;
        }
        BeginOptionalModPublication(znet);
        CaptureLocalHostIntent(znet);
        if (ZNet.m_onlineBackend == OnlineBackendType.Steamworks)
        {
            if (_initialized && !_shuttingDown && !znet.IsServer())
            {
                BeginClientManifestPreparation(znet);
            }
            if (znet.IsServer() && !ReferenceEquals(_preparedServerNetwork, znet))
            {
                _preparedServerNetwork = znet;
                try
                {
                    ServerDataRoot.BindAndPrepare(ServerManagerPlugin.Log);
                    ResetServerSettings();
                    EnsureIntegrityService();

                    _serverDataRootStartupFailed = false;
                    ServerManagerPlugin.ConnectionError = string.Empty;
                    try
                    {
                        PlayerActivityRuntime.Start(ServerManagerPlugin.DataRoot);
                    }
                    catch (Exception exception) when (
                        !IntegrityCanonical.IsFatal(exception))
                    {
                        ServerManagerPlugin.Log.LogWarning(
                            "Per-player activity logging could not start; " +
                            "server startup will continue without it. " +
                            exception);
                    }

                    ServerEventRuntime.OnServerStarted(znet);
                }
                catch (Exception exception) when (
                    !IntegrityCanonical.IsFatal(exception))
                {
                    PlayerActivityRuntime.Stop("server_start_failed");
                    _serverDataRootStartupFailed = true;
                    ValheimPrivateAccess.SetOpenServer(false);
                    ServerManagerPlugin.ConnectionError =
                        "ServerManager could not prepare its server data or mod folder policy. " +
                        "Review the server log and correct the storage or policy error.";
                    ServerManagerPlugin.Log.LogFatal(
                        "ServerManager server-data or mod-folder-policy initialization failed closed: " +
                        exception);
                    return true;
                }
            }

            if (!znet.IsServer() || EnsureSteamAuthenticationCallback(znet))
            {
                return true;
            }

            ValheimPrivateAccess.SetOpenServer(false);
            ServerManagerPlugin.ConnectionError =
                SteamAuthenticationUnavailableMessage;
            ServerManagerPlugin.Log.LogFatal(
                SteamAuthenticationUnavailableMessage);
            return true;
        }

        RejectUnsupportedTransport(znet, peer: null, startup: true);
        if (znet.IsServer())
        {
            // ZNet.Awake already marks a local server Connected. Skipping Start
            // here would leave Game in a half-initialized state, so allow the
            // world lifecycle to finish but prevent OnGenerationFinished from
            // advertising or opening any listener.
            ValheimPrivateAccess.SetOpenServer(false);
            return true;
        }

        // ClientConnect has not yet constructed a PlayFab/custom socket.
        return false;
    }

    internal static bool BeforeNewConnection(ZNet znet, ZNetPeer peer)
    {
        bool hasSteamworksBackend =
            ZNet.m_onlineBackend == OnlineBackendType.Steamworks;
        ISocket? concreteSocket = peer?.m_socket;
        if (concreteSocket == null && peer?.m_rpc != null)
        {
            try
            {
                concreteSocket = peer.m_rpc.GetSocket();
            }
            catch
            {
                // An uninspectable socket cannot establish a Steam identity.
            }
        }

        if (!hasSteamworksBackend ||
            (peer != null && concreteSocket is not ZSteamSocket))
        {
            RejectUnsupportedTransport(znet, peer, startup: false);
            return false;
        }

        if (!_initialized || _shuttingDown || peer?.m_rpc == null)
        {
            return true;
        }

        ZRpc rpc = peer.m_rpc;
        if (znet.IsServer() &&
            !TryReserveSteamAuthentication(znet, peer, rpc))
        {
            RegistrationFailures.Add(rpc);
            return false;
        }

        bool protocolRegistered = false;
        bool detectionRegistered = false;
        bool adminEntitlementRegistered = false;
        bool eventReportRegistered = false;
        bool eventDisplayRegistered = false;
        bool adminCommandRegistered = false;
        try
        {
            RawProtocolRpcTransport.Register(
                rpc,
                ProtocolRpcName,
                _connectionLimits,
                OnProtocolPacket,
                OnRawTransportError);
            protocolRegistered = true;
            RawProtocolRpcTransport.Register(
                rpc,
                DetectionRpcName,
                DetectionReportCodec.MaximumPacketBytes,
                OnDetectionPacket,
                OnSecurityRawTransportError);
            detectionRegistered = true;
            RawProtocolRpcTransport.Register(
                rpc,
                AdminEntitlementRpcName,
                AdminCommandEntitlementCodec.MaximumPacketBytes,
                OnAdminEntitlementPacket,
                OnSecurityRawTransportError);
            adminEntitlementRegistered = true;
            RawProtocolRpcTransport.Register(
                rpc,
                EventReportRpcName,
                EventClientReportCodec.MaximumPacketBytes,
                OnEventReportPacket,
                OnRawTransportError);
            eventReportRegistered = true;
            RawProtocolRpcTransport.Register(
                rpc,
                EventDisplayRpcName,
                EventDisplayCodec.MaximumPacketBytes,
                OnEventDisplayPacket,
                OnRawTransportError);
            eventDisplayRegistered = true;
            RawProtocolRpcTransport.Register(rpc, AdminCommandRpcName,
                Commands.AdminCommandCodec.MaximumPacketBytes, OnAdminCommandPacket, OnRawTransportError);
            adminCommandRegistered = true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            if (adminCommandRegistered)
                RawProtocolRpcTransport.Unregister(rpc, AdminCommandRpcName);
            if (eventDisplayRegistered)
            {
                RawProtocolRpcTransport.Unregister(rpc, EventDisplayRpcName);
            }

            if (eventReportRegistered)
            {
                RawProtocolRpcTransport.Unregister(rpc, EventReportRpcName);
            }

            if (adminEntitlementRegistered)
            {
                RawProtocolRpcTransport.Unregister(
                    rpc,
                    AdminEntitlementRpcName);
            }

            if (detectionRegistered)
            {
                RawProtocolRpcTransport.Unregister(rpc, DetectionRpcName);
            }

            if (protocolRegistered)
            {
                RawProtocolRpcTransport.Unregister(rpc, ProtocolRpcName);
            }

            RegistrationFailures.Add(rpc);
            RemoveSteamAuthentication(rpc);
            ServerManagerPlugin.Log.LogError(
                "Could not register the ServerManager protocol RPC: " +
                exception.Message);
            DisposeUnregisteredPeer(peer);
            return false;
        }

        if (!znet.IsServer())
        {
            RestoreClientProfile();
            ClientDetection.Reset();
            CheatCommandGuard.Reset();
            ServerManagerPlugin.ConnectionError = string.Empty;
            _client = CreateClientConnection(rpc, znet);
        }

        return true;
    }

    internal static bool BeforeServerOpen(ZNet znet)
    {
        if (znet.IsServer() && !znet.IsDedicated() &&
            !BeforeLocalHostGameplay(Game.instance))
        {
            if (_localHostWaitingForSettings)
            {
                DeferServerOpenForSettings(znet);
                return false;
            }
            ValheimPrivateAccess.SetOpenServer(false);
            return false;
        }

        if (_serverDataRootStartupFailed)
        {
            ValheimPrivateAccess.SetOpenServer(false);
            return false;
        }

        if (ZNet.m_onlineBackend == OnlineBackendType.Steamworks &&
            EnsureSteamAuthenticationCallback(znet))
        {
            if (!znet.IsServer())
            {
                return true;
            }

            if (RejectLoadedIncompatiblePlugins(znet))
            {
                return false;
            }

            if (!EnsureServerSettingsReady())
            {
                DeferServerOpenForSettings(znet);
                return false;
            }
            return EnsureServerCharacterStorageReady(znet);
        }

        ValheimPrivateAccess.SetOpenServer(false);
        if (ZNet.m_onlineBackend == OnlineBackendType.Steamworks)
        {
            ServerManagerPlugin.ConnectionError =
                SteamAuthenticationUnavailableMessage;
            ServerManagerPlugin.Log.LogFatal(
                SteamAuthenticationUnavailableMessage);
        }
        else
        {
            RejectUnsupportedTransport(znet, peer: null, startup: true);
        }

        return false;
    }

    // Hosted intent is latched before any fail-closed path clears m_openServer.
    // IsServer alone also includes ordinary singleplayer and is not sufficient.
    internal static bool BeforeLocalHostGameplay(Game? game)
    {
        _localHostWaitingForSettings = false;
        ZNet? network = ZNet.instance;
        if (!_initialized || _shuttingDown || network == null ||
            !network.IsServer() || network.IsDedicated()) return true;

        CaptureLocalHostIntent(network);
        if (!_localHostRequested) return true;
        if (_localHostStartupFailed) return false;
        if (LocalHostCharacterRuntime.IsActive) return true;

        try
        {
            if (game == null || ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
                throw new CharacterProtocolException(
                    "The hosted character requires an initialized Steamworks game.");

            // ZNet can open its listener synchronously from Start, before
            // Game.Start. The same guarded preparation is safe in either order.
            BeforeNetworkStart(network);
            if (_serverDataRootStartupFailed || !EnsureSteamAuthenticationCallback(network))
                throw new CharacterProtocolException("The server data or Steam authentication could not be prepared.");
            if (RejectLoadedIncompatiblePlugins(network))
                throw new CharacterProtocolException(ServerManagerPlugin.ConnectionError);
            if (!EnsureServerSettingsReady())
            {
                // Game.Start may precede the item catalog. Keep the selected
                // profile untouched and FixedUpdate gated until settings load.
                _localHostWaitingForSettings = true;
                return false;
            }
            if (!EnsureServerCharacterStorageReady(network))
                throw new CharacterProtocolException(ServerManagerPlugin.ConnectionError);

            LocalHostCharacterRuntime.Prepare(game, EnsureServerCharacterService(),
                new ValheimPlayerProfileCodec(_serverCharacterOptions));
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            _localHostStartupFailed = true;
            _localHostFailureExitPending = true;
            ValheimPrivateAccess.SetOpenServer(false);
            ServerManagerPlugin.ConnectionError = PlayerConnectionMessages.FromException(exception);
            ServerManagerPlugin.Log.LogError(
                "Local-host character startup was refused before gameplay. " + exception.Message);
            return false;
        }
    }

    internal static bool AllowLocalHostSave()
    {
        return !CharacterLoadSaveSuppressed && !_localHostStartupFailed &&
               (!_localHostRequested || LocalHostCharacterRuntime.IsActive);
    }

    private static void CaptureLocalHostIntent(ZNet network)
    {
        if (!network.IsServer() || network.IsDedicated() ||
            ReferenceEquals(_localHostNetwork, network)) return;
        _localHostNetwork = network;
        _localHostRequested = ValheimPrivateAccess.GetOpenServer();
        _localHostStartupFailed = false;
        _localHostFailureExitPending = false;
    }

    internal static void BeforeGameShutdown(ref bool save)
    {
        if (!AllowLocalHostSave()) save = false;
    }

    private static void ProcessLocalHostStartupFailure()
    {
        if (!_localHostFailureExitPending || Game.instance == null) return;
        _localHostFailureExitPending = false;
        string reason = ServerManagerPlugin.ConnectionError;
        try
        {
            // Game.Logout(false, true) recomputes its save argument from free
            // disk space in vanilla. Invoke the actual no-save continuation.
            MethodInfo continuation = AccessTools.DeclaredMethod(typeof(Game),
                "ContinueLogout", new[] { typeof(bool), typeof(bool), typeof(bool) }) ??
                throw new MissingMethodException("Game.ContinueLogout(bool,bool,bool)");
            _continueLogoutPassThrough = true;
            continuation.Invoke(Game.instance, new object[] { false, true, true });
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogError(
                "Could not return the refused local host to the lobby without saving: " + exception);
        }
        finally
        {
            _continueLogoutPassThrough = false;
            ServerManagerPlugin.ConnectionError = reason;
            ValheimPrivateAccess.SetConnectionStatus(ZNet.ConnectionStatus.ErrorConnectFailed);
        }
    }

    private static void ResetLocalHostLifecycle()
    {
        ResetServerSettings();
        _preparedServerNetwork = null;
        _localHostNetwork = null;
        _localHostRequested = false;
        _localHostStartupFailed = false;
        _localHostFailureExitPending = false;
    }

    private static bool RejectLoadedIncompatiblePlugins(ZNet znet)
    {
        string loadedGuid = string.Empty;
        try
        {
            if (Chainloader.PluginInfos.ContainsKey(ServerCharactersPluginGuid))
            {
                loadedGuid = ServerCharactersPluginGuid;
            }
            else if (Chainloader.PluginInfos.ContainsKey(LegacyOrbEventsPluginGuid))
            {
                loadedGuid = LegacyOrbEventsPluginGuid;
            }
            else
            {
                foreach (string guid in new[] { "sighsorry.OrbOfDiscord", "blizz.OrbOfDiscord", "blizz.OrbOfDiscord.Control.Server" })
                {
                    if (!Chainloader.PluginInfos.ContainsKey(guid))
                        continue;
                    loadedGuid = guid;
                    break;
                }
            }
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            ValheimPrivateAccess.SetOpenServer(false);
            ServerManagerPlugin.ConnectionError =
                "ServerManager could not verify incompatible loaded plugins.";
            ServerManagerPlugin.Log.LogFatal(
                "Could not inspect incompatible BepInEx plugins before " +
                "opening the server listener: " + exception);
            return true;
        }

        if (string.IsNullOrEmpty(loadedGuid))
        {
            return false;
        }

        ValheimPrivateAccess.SetOpenServer(false);
        ServerManagerPlugin.ConnectionError = loadedGuid ==
            ServerCharactersPluginGuid
            ? "ServerCharacters and ServerManager cannot run together. Remove " +
              "ServerCharacters.dll and restart. To use its saved characters, copy " +
              "their primary .fch files into the matching SteamID subfolder under ServerManager/characters while the server is stopped."
            : "Discord events and control are integrated into ServerManager. Remove " +
              "the separate OrbOfDiscord DLLs and restart the server.";
        ServerManagerPlugin.Log.LogFatal(
            "Refusing to open the server listener because the incompatible " +
            "BepInEx plugin '" + loadedGuid + "' is loaded. " +
            "ServerManager remains active so this conflict fails closed.");
        return true;
    }

    private static bool EnsureServerCharacterStorageReady(ZNet znet)
    {
        if (!EnsureServerSettingsReady())
        {
            ServerManagerPlugin.ConnectionError =
                "ServerManager.yml is invalid or its item catalog is not ready. " +
                "Correct the server configuration before opening the server.";
            return false;
        }

        if (_serverCharacterStorageStartupState ==
            ServerCharacterStorageStartupState.Ready)
        {
            return true;
        }

        if (_serverCharacterStorageStartupState ==
            ServerCharacterStorageStartupState.Failed)
        {
            ValheimPrivateAccess.SetOpenServer(false);
            return false;
        }

        try
        {
            // The native primary files are already the authoritative storage.
            // Construction validates every filename/identity and stored PlayerID
            // before either a remote listener or the local host can use them.
            EnsureServerCharacterService();
            ServerManagerPlugin.Log.LogInfo(
                "Validated native character storage before server admission: " +
                ServerManagerPlugin.CharacterRoot);
            _serverCharacterStorageStartupState =
                ServerCharacterStorageStartupState.Ready;
            return true;
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            _serverCharacterStorageStartupState =
                ServerCharacterStorageStartupState.Failed;
            ValheimPrivateAccess.SetOpenServer(false);
            ServerManagerPlugin.ConnectionError =
                "Character storage validation failed before the server listener opened. " +
                "Review the server log, correct the characters folder while the server is stopped, and restart.";
            ServerManagerPlugin.Log.LogFatal(
                "Character storage validation failed closed; the server listener " +
                "will remain disabled until restart: " + exception);
            return false;
        }
    }

    private static bool EnsureSteamAuthenticationCallback(ZNet znet)
    {
        if (_steamAuthenticationCallbackUnavailable)
        {
            return false;
        }

        try
        {
            // Valheim's dedicated binary uses SteamGameServer.BeginAuthSession
            // and GameServer.RunCallbacks; listen hosts use SteamUser and
            // SteamAPI.RunCallbacks. Subscribe to the same native API channel.
            bool gameServer = znet.IsDedicated();
            if (_steamAuthenticationCallback != null)
            {
                if (_steamAuthenticationCallback.IsGameServer == gameServer)
                {
                    return true;
                }

                // Do not replace an existing subscription while its pending
                // or active authentication generations may still receive events.
                _steamAuthenticationCallbackUnavailable = true;
                ServerManagerPlugin.Log.LogError(
                    "The final Steam authentication callback channel changed; " +
                    "new connections will remain closed until network shutdown.");
                return false;
            }

            _steamAuthenticationCallback = gameServer
                ? Callback<ValidateAuthTicketResponse_t>.CreateGameServer(
                    OnSteamAuthenticationCallback)
                : Callback<ValidateAuthTicketResponse_t>.Create(
                    OnSteamAuthenticationCallback);
            ServerManagerPlugin.Log.LogInfo(
                "Registered final Steam authentication callback on the " +
                (gameServer ? "game-server" : "client") + " channel.");
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            _steamAuthenticationCallbackUnavailable = true;
            ServerManagerPlugin.Log.LogError(
                "Could not register the final Steam authentication callback: " +
                exception.Message);
            return false;
        }
    }

    private static void OnSteamAuthenticationCallback(
        ValidateAuthTicketResponse_t response)
    {
        if (!_initialized || _shuttingDown)
        {
            return;
        }

        ulong steamId = response.m_SteamID.m_SteamID;
        if (steamId == 0UL)
        {
            return;
        }

        lock (SteamAuthenticationGate)
        {
            long receivedTimestamp = Stopwatch.GetTimestamp();
            if (!SteamAuthenticationsById.TryGetValue(
                    steamId,
                    out SteamAuthenticationAttempt attempt) ||
                attempt.Phase == SteamAuthenticationPhase.Rejected)
            {
                if (RetiredSteamIds.ContainsKey(steamId))
                {
                    RetiredSteamIds[steamId] =
                        Stopwatch.GetTimestamp() + RetiredSteamIdQuietTicks;
                }

                // This may belong to another Steam consumer or a retired
                // connection. Never cache an unmatched response for a future
                // peer.
                return;
            }

            if (attempt.PendingCallbackCount >=
                    MaximumQueuedSteamCallbacksPerAttempt ||
                Volatile.Read(
                    ref _queuedSteamAuthenticationCallbackCount) >=
                MaximumQueuedSteamAuthenticationCallbacks)
            {
                attempt.CallbackOverflowed = true;
                return;
            }

            ++attempt.PendingCallbackCount;
            if (attempt.EnqueuedCallbackCount < int.MaxValue)
            {
                ++attempt.EnqueuedCallbackCount;
            }
            // Capture the attempt generation now. Looking up only by SteamID
            // in Tick could attach a stale callback to a rapid reconnect.
            Interlocked.Increment(
                ref _queuedSteamAuthenticationCallbackCount);
            SteamAuthenticationCallbacks.Enqueue(
                new SteamAuthenticationCallbackEvent(
                    attempt,
                    response,
                    attempt.Phase,
                    attempt.BeginAuthInvocationObserved,
                    receivedTimestamp));
        }
    }

    private static bool TryReserveSteamAuthentication(
        ZNet server,
        ZNetPeer peer,
        ZRpc rpc)
    {
        if (!EnsureSteamAuthenticationCallback(server))
        {
            ServerManagerPlugin.ConnectionError =
                SteamAuthenticationUnavailableMessage;
            ServerManagerPlugin.Log.LogError(
                SteamAuthenticationUnavailableMessage);
            RecordUnregisteredConnectionRejection(rpc, peer, "steam_authentication_unavailable",
                SteamAuthenticationUnavailableMessage);
            DisposeUnregisteredPeer(peer);
            return false;
        }

        if (peer.m_socket is not ZSteamSocket socket)
        {
            ServerManagerPlugin.Log.LogWarning(
                "Rejected a server connection without a concrete Steam socket.");
            RecordUnregisteredConnectionRejection(rpc, peer, "steam_socket_required",
                "The incoming server connection did not have a concrete Steam socket.");
            DisposeUnregisteredPeer(peer);
            return false;
        }

        CSteamID steamId;
        try
        {
            steamId = socket.GetPeerID();
            if (!steamId.IsValid() ||
                !ReferenceEquals(rpc.GetSocket(), socket))
            {
                throw new InvalidDataException(
                    "The Steam socket identity was not connection-bound.");
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "Rejected an invalid Steam connection identity: " +
                exception.Message);
            RecordUnregisteredConnectionRejection(rpc, peer, "steam_identity_unavailable",
                exception.GetType().Name + ": " + exception.Message);
            DisposeUnregisteredPeer(peer);
            return false;
        }

        long now = Stopwatch.GetTimestamp();
        long deadline = now +
            checked(
                (long)Math.Ceiling(
                    _connectionLimits.OverallHandshakeTimeout.TotalSeconds *
                    Stopwatch.Frequency));
        SteamAuthenticationAttempt candidate = new(
            peer,
            rpc,
            socket,
            steamId,
            deadline);
        SteamAuthenticationAttempt? conflicting = null;
        bool retired;
        bool quarantined;
        bool generationCapacityExhausted;
        bool capacityExceeded = false;
        lock (SteamAuthenticationGate)
        {
            PruneRetiredSteamIdsLocked(now);
            generationCapacityExhausted =
                _steamAuthenticationGenerationCapacityExhausted;
            quarantined =
                QuarantinedIncompleteSteamIds.Contains(
                    steamId.m_SteamID);
            retired =
                RetiredSteamIds.TryGetValue(
                    steamId.m_SteamID,
                    out long quietUntil) &&
                quietUntil > now;
            if (!generationCapacityExhausted &&
                !quarantined &&
                !retired)
            {
                if (SteamAuthenticationsByRpc.TryGetValue(
                        rpc,
                        out SteamAuthenticationAttempt byRpc))
                {
                    conflicting = byRpc;
                }
                else if (SteamAuthenticationsById.TryGetValue(
                             steamId.m_SteamID,
                             out SteamAuthenticationAttempt byId))
                {
                    conflicting = byId;
                }
                else if (SteamAuthenticationsByRpc.Count >=
                         MaximumSteamAuthenticationReservations)
                {
                    capacityExceeded = true;
                }
                else
                {
                    SteamAuthenticationsByRpc.Add(rpc, candidate);
                    SteamAuthenticationsById.Add(
                        steamId.m_SteamID,
                        candidate);
                }
            }
        }

        if (generationCapacityExhausted)
        {
            ServerManagerPlugin.Log.LogError(
                "Rejected a Steam connection because the bounded incomplete-" +
                "authentication generation quarantine is exhausted. Restart " +
                "the server process to establish a new callback generation.");
            RecordUnregisteredConnectionRejection(rpc, peer, "steam_generation_capacity_exhausted",
                "The incomplete-authentication generation quarantine is exhausted.");
            DisposeUnregisteredPeer(peer);
            return false;
        }

        if (quarantined)
        {
            ServerManagerPlugin.Log.LogWarning(
                "Rejected a Steam reconnect whose previous BeginAuthSession " +
                "did not reach Active with a generation-bound final OK. The " +
                "ID is quarantined until the server process restarts.");
            RecordUnregisteredConnectionRejection(rpc, peer, "steam_identity_quarantined",
                "The previous authentication attempt did not reach a generation-bound final OK.");
            DisposeUnregisteredPeer(peer);
            return false;
        }

        if (retired)
        {
            ServerManagerPlugin.Log.LogWarning(
                "Rejected a Steam reconnect during the retired authentication " +
                "callback drain window.");
            RecordUnregisteredConnectionRejection(rpc, peer, "steam_authentication_retired",
                "A reconnect arrived during the retired authentication callback drain window.");
            DisposeUnregisteredPeer(peer);
            return false;
        }

        if (conflicting == null)
        {
            if (capacityExceeded)
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Rejected a Steam connection because the bounded " +
                    "authentication reservation table is full.");
                RecordUnregisteredConnectionRejection(rpc, peer, "steam_reservation_capacity_exceeded",
                    "The authentication reservation table is full.");
                DisposeUnregisteredPeer(peer);
                return false;
            }

            return true;
        }

        ProtocolRejection duplicate = new(
            ProtocolRejectCode.DuplicateConnection,
            "A duplicate connection for the same Steam account was rejected.");
        RejectSteamAuthentication(conflicting, duplicate);
        // Closing the new socket calls EndAuthSession for this SteamID even
        // though it never submitted a ticket. The existing connection must
        // therefore be disconnected too instead of remaining falsely active.
        DisconnectServerPeer(conflicting.Rpc);
        if (!ReferenceEquals(conflicting.Peer, peer))
        {
            RecordUnregisteredConnectionRejection(rpc, peer, "duplicate_steam_connection",
                "A duplicate connection claimed the same Steam account; both attempts were rejected.");
            DisposeUnregisteredPeer(peer);
        }

        ServerManagerPlugin.Log.LogWarning(
            "Rejected both connections for a duplicate Steam account.");
        return false;
    }

    private static void DisposeUnregisteredPeer(ZNetPeer? peer)
    {
        try
        {
            // OnNewConnection has not added this peer to ZNet, so there will
            // be no later ZNet.Disconnect cleanup. Mirror vanilla peer cleanup
            // to dispose the ZRpc and remove the socket from its global list.
            peer?.Dispose();
        }
        catch
        {
            // A rejected pre-registration peer may already be disposing.
        }
    }

    internal static void BeforeVanillaSteamTicketVerification(
        CSteamID steamId)
    {
        if (!_initialized ||
            _shuttingDown ||
            !steamId.IsValid())
        {
            return;
        }

        lock (SteamAuthenticationGate)
        {
            if (!SteamAuthenticationsById.TryGetValue(
                    steamId.m_SteamID,
                    out SteamAuthenticationAttempt attempt) ||
                attempt.SteamId != steamId ||
                attempt.Phase == SteamAuthenticationPhase.Rejected)
            {
                return;
            }

            if (attempt.Phase !=
                    SteamAuthenticationPhase.PeerInfoPending ||
                attempt.BeginAuthInvocationObserved)
            {
                // ValidateAuthTicketResponse_t has only a SteamID correlation
                // key. A second or out-of-phase Begin wrapper call makes the
                // callback ownership ambiguous and must invalidate the peer.
                attempt.DuplicateBeginAuthInvocation = true;
                return;
            }

            attempt.BeginAuthInvocationObserved = true;
        }
    }

    internal static void AfterVanillaSteamTicketVerification(
        CSteamID steamId,
        bool accepted)
    {
        if (!_initialized ||
            _shuttingDown ||
            !steamId.IsValid())
        {
            return;
        }

        lock (SteamAuthenticationGate)
        {
            if (!SteamAuthenticationsById.TryGetValue(
                    steamId.m_SteamID,
                    out SteamAuthenticationAttempt attempt) ||
                attempt.SteamId != steamId ||
                attempt.Phase == SteamAuthenticationPhase.Rejected)
            {
                return;
            }

            if (attempt.Phase !=
                    SteamAuthenticationPhase.PeerInfoPending ||
                !attempt.BeginAuthInvocationObserved ||
                attempt.BeginAuthResultRecorded)
            {
                attempt.DuplicateBeginAuthInvocation = true;
                return;
            }

            attempt.BeginAuthResultRecorded = true;
            attempt.BeginAuthImmediateAccepted = accepted;
        }
    }

    internal static void AfterVanillaSteamTicketVerificationFaulted(
        CSteamID steamId)
    {
        if (!_initialized ||
            _shuttingDown ||
            !steamId.IsValid())
        {
            return;
        }

        lock (SteamAuthenticationGate)
        {
            if (!SteamAuthenticationsById.TryGetValue(
                    steamId.m_SteamID,
                    out SteamAuthenticationAttempt attempt) ||
                attempt.SteamId != steamId ||
                attempt.Phase == SteamAuthenticationPhase.Rejected)
            {
                return;
            }

            // A managed/native failure can occur after BeginAuthSession has
            // already affected Steam. Treat the result as uncertain rather
            // than equating it with a normal synchronous rejection.
            attempt.BeginAuthExecutionFaulted = true;
            if (attempt.Phase !=
                    SteamAuthenticationPhase.PeerInfoPending ||
                !attempt.BeginAuthInvocationObserved)
            {
                attempt.DuplicateBeginAuthInvocation = true;
                return;
            }

            if (!attempt.BeginAuthResultRecorded)
            {
                attempt.BeginAuthResultRecorded = true;
                attempt.BeginAuthImmediateAccepted = false;
            }
        }
    }

    // Transport identity only: Reserved is valid during manifest exchange.
    // Callers must still require the final Steam callback and their protocol
    // phase before exposing characters, accepting saves or granting authority.
    // Server-installed wrappers may change peer.m_socket/rpc.GetSocket(); they
    // are neither an identity source nor an authentication failure by themselves.
    internal static bool TryGetSteamConnection(
        ZNet server,
        ZRpc rpc,
        out SteamAuthenticationAttempt attempt,
        out ProtocolRejection rejection)
    {
        attempt = null!;
        if (!ServerPeerResolver.TryResolvePeer(server, rpc, out ZNetPeer peer,
                out rejection))
        {
            return false;
        }

        SteamAuthenticationAttempt reserved;
        lock (SteamAuthenticationGate)
        {
            if (!SteamAuthenticationsByRpc.TryGetValue(rpc, out reserved) ||
                !IsCurrentSteamAuthenticationLocked(reserved) ||
                reserved.Phase == SteamAuthenticationPhase.Rejected ||
                !ReferenceEquals(reserved.Peer, peer) ||
                !ReferenceEquals(reserved.Rpc, rpc))
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PeerIdentityUnavailable,
                    "The connection-bound Steam reservation was unavailable.");
                return false;
            }
        }

        try
        {
            CSteamID currentSteamId = reserved.Socket.GetPeerID();
            if (!reserved.Socket.IsConnected() || !currentSteamId.IsValid() ||
                currentSteamId != reserved.SteamId)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PeerIdentityUnavailable,
                    "The reserved Steam connection closed or changed identity.");
                return false;
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            rejection = new ProtocolRejection(
                ProtocolRejectCode.PeerIdentityUnavailable,
                "The reserved Steam connection could not be revalidated.");
            return false;
        }

        lock (SteamAuthenticationGate)
        {
            if (!IsCurrentSteamAuthenticationLocked(reserved) ||
                reserved.Phase == SteamAuthenticationPhase.Rejected ||
                !ReferenceEquals(peer.m_rpc, rpc))
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PeerIdentityUnavailable,
                    "The Steam connection reservation was retired.");
                return false;
            }
        }

        attempt = reserved;
        rejection = null!;
        return true;
    }

    private static bool TryEnterSteamPeerInfo(
        ZNet server,
        ZRpc rpc,
        out SteamAuthenticationAttempt? attempt,
        out ProtocolRejection? rejection)
    {
        attempt = null;
        rejection = null;
        if (!TryGetSteamConnection(server, rpc,
                out SteamAuthenticationAttempt reserved, out rejection))
        {
            return false;
        }

        lock (SteamAuthenticationGate)
        {
            if (!IsCurrentSteamAuthenticationLocked(reserved) ||
                reserved.Phase != SteamAuthenticationPhase.Reserved)
            {
                reserved.Phase = SteamAuthenticationPhase.Rejected;
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                    "The Steam authentication reservation was invalid.");
                return false;
            }

            if (_coordinator.TryGetSnapshot(
                    rpc,
                    out ConnectionSessionSnapshot snapshot))
            {
                reserved.DeadlineTimestamp = snapshot.DeadlineTimestamp;
            }

            reserved.Phase = SteamAuthenticationPhase.PeerInfoPending;
            attempt = reserved;
            return true;
        }
    }

    private static bool TryMarkVanillaSteamAuthenticationAccepted(
        ZNet server,
        ZNetPeer peer,
        SteamAuthenticationAttempt attempt,
        out ProtocolRejection? rejection)
    {
        rejection = null;
        if (!TryGetSteamConnection(server, attempt.Rpc,
                out SteamAuthenticationAttempt current, out rejection))
        {
            return false;
        }

        lock (SteamAuthenticationGate)
        {
            if (!IsCurrentSteamAuthenticationLocked(attempt) ||
                !ReferenceEquals(current, attempt) ||
                !ReferenceEquals(attempt.Peer, peer) ||
                !attempt.BeginAuthInvocationObserved ||
                !attempt.BeginAuthResultRecorded ||
                !attempt.BeginAuthImmediateAccepted ||
                attempt.BeginAuthExecutionFaulted ||
                attempt.DuplicateBeginAuthInvocation ||
                attempt.Phase != SteamAuthenticationPhase.PeerInfoPending)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                    "The Steam peer changed during vanilla authentication.");
                return false;
            }

            attempt.Phase = SteamAuthenticationPhase.VanillaAccepted;
        }

        ServerManagerPlugin.Log.LogInfo(
            "Vanilla Steam ticket verification accepted for peer " +
            attempt.SteamId + "; waiting for final Steam authentication.");
        return true;
    }

    private static void RejectUnsupportedTransport(
        ZNet znet,
        ZNetPeer? peer,
        bool startup)
    {
        ZRpc? rejectedRpc = peer?.m_rpc;
        if (rejectedRpc != null && _initialized && !_shuttingDown)
        {
            // Harmony postfixes still execute when this Prefix skips the
            // original. This marker prevents our OnNewConnection postfix from
            // installing any protocol handlers on the rejected RPC.
            RegistrationFailures.Add(rejectedRpc);
        }

        ServerManagerPlugin.ConnectionError = PlayerLocalizer.Text("sm_steam_required");
        if (startup)
        {
            ServerManagerPlugin.Log.LogFatal(UnsupportedTransportMessage);
        }
        else
        {
            ServerManagerPlugin.Log.LogWarning(UnsupportedTransportMessage);
        }

        if (!znet.IsServer())
        {
            try
            {
                ValheimPrivateAccess.SetConnectionStatus(
                    ZNet.ConnectionStatus.ErrorConnectFailed);
            }
            catch
            {
                // Closing/skipping the connection remains fail-closed.
            }
        }

        if (peer != null)
        {
            if (znet.IsServer() && rejectedRpc != null)
                RecordUnregisteredConnectionRejection(rejectedRpc, peer, "unsupported_transport",
                    UnsupportedTransportMessage);
            DisposeUnregisteredPeer(peer);
        }
    }

    internal static void AfterNewConnection(ZNet znet, ZNetPeer peer)
    {
        if (!_initialized || _shuttingDown || peer?.m_rpc == null)
        {
            return;
        }

        ZRpc rpc = peer.m_rpc;
        if (RegistrationFailures.Remove(rpc))
        {
            return;
        }

        if (znet.IsServer())
        {
            ZNetPeer? registeredPeer;
            try
            {
                registeredPeer = ValheimPrivateAccess.FindPeer(znet, rpc);
            }
            catch
            {
                registeredPeer = null;
            }

            if (!ReferenceEquals(registeredPeer, peer))
            {
                // Another Harmony prefix may have skipped vanilla
                // OnNewConnection after our reservation prefix ran. No later
                // ZNet.Disconnect will own this peer, so retire the generation
                // and dispose the unregistered peer immediately.
                RemoveSteamAuthentication(rpc);
                DisposeUnregisteredPeer(peer);
                return;
            }
        }

        try
        {
            BoundedPeerInfoRpcTransport.Install(
                znet,
                rpc,
                _connectionLimits,
                OnRawTransportError);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogError(
                "Could not install the bounded PeerInfo decoder: " +
                exception.Message);
            if (znet.IsServer())
            {
                SendServerRejection(
                    rpc,
                    new ProtocolRejection(
                        ProtocolRejectCode.InternalError,
                        "The server could not secure the PeerInfo decoder."));
            }
            else
            {
                FailClient(
                    rpc,
                    "The client could not secure the PeerInfo decoder.",
                    exception);
            }

            return;
        }

        if (!znet.IsServer())
        {
            if (_client == null || !ReferenceEquals(_client.Rpc, rpc))
            {
                _client = CreateClientConnection(rpc, znet);
            }

            return;
        }

        ProtocolOperationResult registered = _coordinator.RegisterConnected(znet, rpc);
        if (!registered.Succeeded)
        {
            SendServerRejection(rpc, registered.Rejection);
            return;
        }

        try
        {
            InstallWorldGate(rpc);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogError(
                "Could not install the pre-ready connection gate: " +
                exception.Message);
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.InternalError,
                    "The server could not initialize the connection gate."));
            return;
        }

        // Initialize the server-only watcher lazily. Registering first lets an
        // initialization failure use the authenticated protocol rejection path.
        try
        {
            EnsureIntegrityService();
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogError(
                "Mod folder policy service failed to initialize: " + exception);
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.ManifestValidatorFailed,
                    "The server could not build a valid mod folder policy."));
            return;
        }

        if (!EnsureServerSettingsReady())
        {
            SendServerRejection(rpc, new ProtocolRejection(ProtocolRejectCode.InternalError,
                "The server configuration is unavailable. Contact the administrator."));
            return;
        }

        DetectionAction cheatDetectionResponse = NormalizeDetectionAction(
            CurrentServerSettings.CheatDetectionResponse,
            "Cheat Detection Response");
        DetectionAction statLimitResponse = NormalizeDetectionAction(
            CurrentServerSettings.StatLimitResponse,
            "Stat Limit Response");
        const int cheatCommandThreshold = 3;
        const int cheatCommandWindowSeconds = 60;

        ProtocolChallengeOptions challengeOptions = new(
            enforceManifest: ServerManagerPlugin.DefaultEnforceModPolicy,
            serverCharactersEnabled: true,
            maximumManifestBytes:
                ConnectionProtocolLimits.AbsoluteMaxManifestBytes,
            detectCheatEngine: true,
            detectExternalTools: true,
            detectGenericProcessNames: true,
            detectValheimTooler: true,
            monitorCheatCommands: true,
            blockCheatCommands: true,
            allowAdminCheatCommands: true,
            processScanIntervalSeconds: 30,
            enforceCarryWeightLimit: true,
            maximumCarryWeight:
                CurrentServerSettings.MaximumCarryWeight,
            enforceMaximumDamageLimit: true,
            maximumDamage:
                CurrentServerSettings.MaximumDamage);
        ProtocolOperationResult challenged =
            _coordinator.DispatchChallenge(
                znet,
                rpc,
                challengeOptions,
                SendProtocolOrThrow);
        if (!challenged.Succeeded)
        {
            SendServerRejection(rpc, challenged.Rejection);
            return;
        }
        ServerDetectionStates[rpc] = new ServerDetectionState(
            challengeOptions,
            cheatDetectionResponse,
            statLimitResponse,
            cheatCommandThreshold,
            cheatCommandWindowSeconds)
        {
            BackupOnly = !CurrentServerSettings.LoadServerCharacterOnJoin
        };
    }

    internal static bool BeforeClientSendPeerInfo(
        ZNet znet,
        ZRpc rpc,
        string password)
    {
        if (!_initialized || _shuttingDown || znet.IsServer())
        {
            return true;
        }

        ClientConnection session = GetOrCreateClient(rpc);
        if (session.Failed)
        {
            return false;
        }

        if (session.ManifestAccepted)
        {
            if (session.PeerInfoSent)
            {
                FailClient(
                    rpc,
                    "Duplicate peer authentication was blocked.");
                return false;
            }

            session.PeerInfoSent = true;
            session.HeldPassword = null;
            return true;
        }

        session.HeldPassword = password ?? string.Empty;
        return false;
    }

    internal static bool BeforeServerPeerInfo(
        ZNet znet,
        ZRpc rpc,
        ZPackage package,
        out PeerInfoPatchState state)
    {
        state = new PeerInfoPatchState();
        if (!_initialized || _shuttingDown || !znet.IsServer())
        {
            return true;
        }

        state.IsServerPeerInfo = true;
        PeerInfoGateResult gate = _coordinator.GatePeerInfo(
            znet,
            rpc);
        if (gate.Action != PeerInfoGateAction.Allow)
        {
            SendServerRejection(
                rpc,
                gate.Rejection ??
                new ProtocolRejection(
                    ProtocolRejectCode.PeerInfoTooEarly,
                    "Peer authentication arrived before manifest validation."));
            return false;
        }

        try
        {
            if (!WorldBuffers.TryGetValue(
                    rpc,
                    out BufferedWorldSocket buffer) ||
                buffer.Released || buffer.Quarantined ||
                buffer.Overflowed || buffer.InboundViolation)
            {
                SendServerRejection(
                    rpc,
                    new ProtocolRejection(
                        ProtocolRejectCode.InternalError,
                        "The pre-ready connection gate was unavailable."));
                return false;
            }

            if (!TryEnterSteamPeerInfo(
                    znet,
                    rpc,
                    out SteamAuthenticationAttempt? attempt,
                    out ProtocolRejection? authenticationError))
            {
                SendServerRejection(rpc, authenticationError);
                return false;
            }

            state.SteamAuthentication = attempt;
            buffer.MarkPeerInfoAdmitted();
            state.Buffer = buffer;
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.InternalError,
                    "The server could not initialize connection buffering."));
            ServerManagerPlugin.Log.LogError(
                "Could not install the world synchronization buffer: " +
                exception.Message);
            return false;
        }
    }

    internal static void AfterServerPeerInfo(
        ZNet znet,
        ZRpc rpc,
        PeerInfoPatchState? state)
    {
        if (state == null ||
            !state.IsServerPeerInfo ||
            state.Buffer == null ||
            state.SteamAuthentication == null)
        {
            return;
        }

        SteamAuthenticationAttempt attempt = state.SteamAuthentication;
        try
        {
            ZNetPeer? peer = ValheimPrivateAccess.FindPeer(znet, rpc);
            if (peer == null ||
                !peer.IsReady() ||
                peer.m_uid == 0L ||
                string.IsNullOrWhiteSpace(peer.m_playerName))
            {
                // Vanilla already sent its precise password/version/auth error.
                RejectSteamAuthentication(
                    attempt,
                    new ProtocolRejection(
                        ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                        "Vanilla peer authentication did not complete."));
                return;
            }

            if (!TryMarkVanillaSteamAuthenticationAccepted(
                    znet,
                    peer,
                    attempt,
                    out ProtocolRejection? rejection))
            {
                RejectSteamAuthentication(attempt, rejection);
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            HandlePeerInfoException(znet, rpc, state, exception);
        }
    }

    internal static void HandlePeerInfoException(
        ZNet znet,
        ZRpc rpc,
        PeerInfoPatchState? state,
        Exception exception)
    {
        ServerManagerPlugin.Log.LogWarning(
            "Rejected malformed or failed vanilla PeerInfo processing: " +
            exception.GetType().Name +
            ": " +
            exception.Message);
        if (state?.SteamAuthentication != null)
        {
            RejectSteamAuthentication(
                state.SteamAuthentication,
                new ProtocolRejection(
                    ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                    "Vanilla peer authentication could not be completed."));
            return;
        }

        SendServerRejection(
            rpc,
            new ProtocolRejection(
                ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                "Vanilla peer authentication could not be completed."));
    }

    internal static void AbortPeerInfoAuthentication(
        PeerInfoPatchState? state)
    {
        if (state?.SteamAuthentication == null)
        {
            return;
        }

        RejectSteamAuthentication(
            state.SteamAuthentication,
            new ProtocolRejection(
                ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                "Vanilla peer authentication terminated unexpectedly."));
    }

    internal static void AfterGameSave(Game game)
    {
        if (_initialized && !_shuttingDown && !_localHostStartupFailed)
            LocalHostCharacterRuntime.AfterGameSave(game);
        if (!_initialized ||
            _shuttingDown ||
            _suppressClientSaveCapture ||
            game == null ||
            ZNet.instance == null ||
            ZNet.instance.IsServer())
        {
            return;
        }

        ClientConnection? session = _client;
        if (session == null ||
            !session.ServerCharacterActive ||
            !session.ReadyAcknowledgementSent ||
            session.CharacterState == null ||
            session.ManagedProfile == null ||
            session.SavePipeline.Closed ||
            !ReferenceEquals(
                ValheimPrivateAccess.GetGamePlayerProfile(game),
                session.ManagedProfile))
        {
            return;
        }

        if (_deferredClientExit != null)
        {
            return;
        }

        try
        {
            EnsureCharacterCodecs();
            byte[] profileBytes =
                _profileCodec!.SerializeProfileToBytes(session.ManagedProfile);
            long now = Stopwatch.GetTimestamp();
            session.FullProfileSafetySaveDueTimestamp = 0;
            session.NextFullProfileSafetySaveAllowedTimestamp =
                AddStopwatchDuration(
                    now,
                    FullProfileSafetySaveMinimumIntervalTicks);
            session.InventoryFastSaveDueTimestamp = 0;
            session.NextInventoryFastSaveAllowedTimestamp =
                AddStopwatchDuration(
                    now,
                    InventoryFastSaveMinimumIntervalTicks);
            OfferClientSave(
                session,
                profileBytes,
                ClientCharacterSaveReason.Vanilla);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            FailClient(
                session.Rpc,
                "The server-managed character could not be saved.",
                exception);
        }
    }

    internal static void AfterInventoryChanged(Inventory inventory)
    {
        if (_initialized && !_shuttingDown && _inventoryLoadSuppressionDepth == 0)
            LocalHostCharacterRuntime.AfterInventoryChanged(inventory);
        if (!_initialized ||
            _shuttingDown ||
            _inventoryLoadSuppressionDepth != 0 ||
            inventory == null ||
            ZNet.instance == null ||
            ZNet.instance.IsServer())
        {
            return;
        }

        ClientConnection? session = _client;
        if (session == null ||
            !session.ServerCharacterActive ||
            !session.ReadyAcknowledgementSent ||
            session.CharacterState == null ||
            session.ManagedProfile == null ||
            session.SavePipeline.Closed)
        {
            return;
        }

        Player? player = Player.m_localPlayer;
        if (player == null ||
            !ReferenceEquals(player.GetInventory(), inventory))
        {
            return;
        }

        if (_deferredClientExit != null)
        {
            if (_clientGameplayQuiescence != null &&
                _deferredClientExit.ClientWorldBarrierSent)
            {
                _deferredClientExit.InventoryChangedAfterQuiescence = true;
            }

            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (session.InventoryFastSaveDueTimestamp == 0)
        {
            long due = AddStopwatchDuration(
                now,
                InventoryFastSaveCoalesceTicks);
            if (session.NextInventoryFastSaveAllowedTimestamp > due)
            {
                due = session.NextInventoryFastSaveAllowedTimestamp;
            }

            session.InventoryFastSaveDueTimestamp = due;
        }

        // A profile without player data cannot use the inventory-only path yet.
        // Promote its first inventory materialization to a prompt full baseline.
        // Once that baseline exists, routine inventory changes rely on the
        // one-second fast path plus the independent five-minute full heartbeat.
        if (!session.InventoryFastPathReady &&
            session.FullProfileSafetySaveDueTimestamp == 0)
        {
            long due = AddStopwatchDuration(
                now,
                InventoryFastSaveCoalesceTicks);
            if (session.NextFullProfileSafetySaveAllowedTimestamp > due)
            {
                due = session.NextFullProfileSafetySaveAllowedTimestamp;
            }

            session.FullProfileSafetySaveDueTimestamp = due;
        }
    }

    internal static bool BeforeLocalPlayerDamage(object target, HitData hit)
    {
        if (!_initialized || _shuttingDown || hit == null)
        {
            return true;
        }

        ZNet? network = ZNet.instance;
        if (network == null)
        {
            return true;
        }

        if (!network.IsServer())
        {
            return ClientDetection.ShouldAllowLocalPlayerDamage(hit);
        }

        if (_currentServerSettings == null) return true;

        Player? localPlayer = Player.m_localPlayer;
        bool attributedLocalPlayerHit =
            localPlayer != null && hit.GetAttacker() == localPlayer;
        bool blocked = attributedLocalPlayerHit &&
            GameplayLimitValidation.ShouldBlockDamage(
                hit,
                CurrentServerSettings.MaximumDamage,
                IsCurrentLocalHostStatLimitAdmin(network));
        PlayerActivityRuntime.ObserveServerLocalDamage(
            target,
            hit,
            blocked,
            attributedLocalPlayerHit);
        if (!attributedLocalPlayerHit)
        {
            return true;
        }

        if (!blocked)
        {
            return true;
        }

        if (!_listenServerDamageLimitLogged)
        {
            _listenServerDamageLimitLogged = true;
            ServerManagerPlugin.Log.LogWarning(
                "Blocked an oversized local-host player damage call. " +
                "The listen-server host has no remote peer to kick or ban.");
            LocalHostCharacterRuntime.RecordStatLimitObservation(
                DetectionEvidence.MaximumDamageLimitExceeded,
                "attack_blocked; local host has no remote peer to sanction; " +
                DescribeDamageLimit(hit, CurrentServerSettings.MaximumDamage));
        }

        return false;
    }

    internal static bool BeforeServerRoutedRpcDamage(
        ZRpc rpc,
        ZPackage package)
    {
        if (!_initialized || _shuttingDown || rpc == null || package == null)
        {
            return true;
        }

        ZNet? server = ZNet.instance;
        if (server == null || !server.IsServer())
        {
            return true;
        }

        RoutedDamageInspection inspection =
            GameplayLimitValidation.InspectRoutedDamage(
                package,
                out RoutedDamageObservation? damageObservation);
        HitData? hit = damageObservation?.Hit;
        if (inspection == RoutedDamageInspection.NotDamage)
        {
            return true;
        }

        if (!ServerDetectionStates.TryGetValue(
                rpc,
                out ServerDetectionState state))
        {
            // Generic malformed framing must never reach vanilla ReadPackage,
            // even during a short pre-policy or teardown window. Other damage
            // inspection requires a pinned session policy.
            return inspection != RoutedDamageInspection.MalformedRoutedRpc;
        }

        if (!_coordinator.TryGetSnapshot(
                rpc,
                out ConnectionSessionSnapshot session) ||
            session.State != ConnectionSessionState.Ready ||
            !session.PeerInfoAuthenticated)
        {
            // Gameplay RPCs are not valid before readiness. Do not let a
            // forged early damage message pass merely because identity setup
            // is incomplete.
            return false;
        }

        if (!TryResolveActiveDetectionPeer(
                server,
                rpc,
                out ServerPeerIdentity identity,
                out _))
        {
            return false;
        }

        if (inspection == RoutedDamageInspection.MalformedRoutedRpc)
        {
            if (!state.TerminalActionApplied)
            {
                state.TerminalEvidence = DetectionEvidence.MalformedGameplayTraffic;
                state.TerminalSource = "server_observed";
                LogDetection(identity,
                    new DetectionReport(session.SessionId, session.Nonce, 1,
                        DetectionEvidence.MalformedGameplayTraffic, string.Empty, state.PolicyGeneration),
                    DetectionAction.Kick,
                    "Rejected a malformed routed RPC envelope before vanilla " +
                    "could allocate its declared inner package.",
                    "server_observed");
            }

            ExecuteTerminalDetectionAction(
                server,
                rpc,
                identity,
                state,
                DetectionAction.Kick);
            return false;
        }

        bool malformed =
            inspection == RoutedDamageInspection.MalformedDamage ||
            hit != null && !GameplayLimitValidation.IsValidDamage(hit);
        bool attributedPlayerHit =
            !malformed &&
            hit != null &&
            hit.m_attacker.Equals(identity.Peer.m_characterID);
        bool exceedsDamageLimit =
            !malformed &&
            hit != null &&
            state.Policy.EnforceMaximumDamageLimit &&
            GameplayLimitValidation.ExceedsDamage(
                hit,
                state.Policy.MaximumDamage);
        // The exception belongs to the authenticated sending Steam socket,
        // never to a client-claimed HitData attacker or admin flag. Malformed
        // data above is deliberately ineligible for this numerical exception.
        bool adminBypass = exceedsDamageLimit &&
            HasNumericStatLimitAdminBypass(server, identity, state,
                DetectionEvidence.MaximumDamageLimitExceeded);
        if (!malformed && damageObservation != null)
        {
            PlayerActivityRuntime.ObserveRoutedDamage(
                rpc,
                damageObservation,
                exceedsDamageLimit && !adminBypass,
                attributedPlayerHit);
        }

        if (!malformed && (!state.Policy.EnforceMaximumDamageLimit || adminBypass))
        {
            return true;
        }

        if (!malformed)
        {
            if (hit == null || !exceedsDamageLimit)
            {
                return true;
            }

            if (!attributedPlayerHit)
            {
                // A client can forge HitData.m_attacker. Never use a mismatch
                // to permit oversized damage. Avoid a terminal action because
                // a client may legitimately own an NPC that originated the
                // hit, but record one connection-bound observation.
                if (!state.UnattributedOversizedDamageLogged)
                {
                    state.UnattributedOversizedDamageLogged = true;
                    ServerManagerPlugin.Log.LogWarning(
                        "Blocked unattributed oversized routed damage steam=" +
                        DetectionLog.QuoteValue(identity.HostId, 32) +
                        " player=" +
                        DetectionLog.QuoteValue(identity.PlayerName, 64) +
                        ". The HitData attacker did not match this peer's " +
                        "current character, so no Kick/Ban action was applied.");
                    ServerEventRuntime.RecordSecurityEvent(
                        ServerManagerEventKinds.SecurityDetection,
                        "steamworks:" + identity.HostId, identity.PlayerName,
                        "server_observed", DetectionEvidence.MaximumDamageLimitExceeded.ToString(),
                        DetectionAction.Log, "unattributed_attack_blocked",
                        DescribeDamageLimit(hit, state.Policy.MaximumDamage));
                }

                return false;
            }
        }

        // Blocking is applied to every matching oversized/malformed hit. Log
        // and terminal policy are intentionally de-duplicated per connection.
        DetectionEvidence serverEvidence = malformed
            ? DetectionEvidence.MalformedGameplayTraffic
            : DetectionEvidence.MaximumDamageLimitExceeded;
        uint observationGeneration = malformed ? state.PolicyGeneration : state.AcknowledgedPolicyGeneration;
        if (!state.ServerObservedEvidence.TryGetValue(serverEvidence, out uint observedGeneration) ||
            observedGeneration != observationGeneration)
        {
            state.ServerObservedEvidence[serverEvidence] = observationGeneration;
            DetectionReport report = new(
                session.SessionId,
                session.Nonce,
                1,
                serverEvidence,
                string.Empty,
                state.PolicyGeneration);
            ApplyDetectionAction(
                server,
                rpc,
                identity,
                state,
                report,
                malformed ? state.StatLimitResponse : CurrentNumericLimitResponse(state),
                malformed
                    ? "malformed server-routed RPC_Damage"
                    : "attack_blocked; " + DescribeDamageLimit(hit, state.Policy.MaximumDamage),
                "server_observed");
        }

        return false;
    }

    private static string DescribeDamageLimit(HitData? hit, float limit)
    {
        string measured = hit != null &&
            GameplayLimitValidation.TryMeasureRawDamage(hit, out double raw)
                ? (raw * Math.Max(1d, hit.m_backstabBonus)).ToString("R", CultureInfo.InvariantCulture)
                : "invalid";
        return "raw_potential=" + measured + "; limit=" +
            limit.ToString("R", CultureInfo.InvariantCulture);
    }

    private static Game? _unsafePlayerLoadGame;
    private static ZNet? _unsafePlayerLoadNetwork;

    internal static bool CharacterLoadSaveBlocked => _unsafePlayerLoadGame is not null &&
        ReferenceEquals(Game.instance, _unsafePlayerLoadGame);

    // Also cover synchronous saves from other mods' Load postfixes, before
    // our finalizer has a chance to handle a load failure.
    internal static bool CharacterLoadSaveSuppressed => CharacterLoadSaveBlocked ||
        (IsPlayerLoadInProgress && ShouldValidatePlayerLoad(Player.m_localPlayer));

    internal static bool ShouldValidatePlayerLoad(Player player) =>
        _initialized && !_shuttingDown && player != null &&
        ReferenceEquals(player, Player.m_localPlayer) && Game.instance != null &&
        (CharacterLoadSaveBlocked || LocalHostCharacterRuntime.IsActive ||
         (_client is { Failed: false, ServerCharacterActive: true } session &&
          ReferenceEquals(session.Network, ZNet.instance) &&
          ReferenceEquals(session.ManagedProfile, ValheimPrivateAccess.GetGamePlayerProfile(Game.instance))));

    internal static void RejectUnsafePlayerLoad(Exception error)
    {
        // Latch before logging, closing sessions or disconnecting. Keep this
        // guard through StopAll/scene teardown; only a new network clears it.
        if (CharacterLoadSaveBlocked) return;
        _unsafePlayerLoadGame = Game.instance;
        _unsafePlayerLoadNetwork = ZNet.instance;
        _localHostFailureExitPending = true;
        string notice = PlayerLocalizer.Text("sm_character_load_unsafe");
        ServerManagerPlugin.ConnectionError = notice;
        try
        {
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                _localHostStartupFailed = true;
                ValheimPrivateAccess.SetOpenServer(false);
                LocalHostCharacterRuntime.Close();
            }
            else if (_client != null)
                FailClient(_client.Rpc, "Character loading did not complete successfully.", error, notice);
        }
        finally
        {
            ServerManagerPlugin.ConnectionError = notice;
            ServerManagerPlugin.Log.LogError("Character loading was aborted without saving; returning to the lobby. " + error);
        }
    }

    internal static bool IsPlayerLoadInProgress => _inventoryLoadSuppressionDepth != 0;

    internal static bool CanPersistCharacterPoison(Player player, bool restoring)
    {
        if (!_initialized || _shuttingDown || player == null ||
            !ReferenceEquals(player, Player.m_localPlayer) || Game.instance == null) return false;
        if (LocalHostCharacterRuntime.CanPersistCharacterPoison(player, restoring)) return true;
        ClientConnection? session = _client;
        ZNet network = ZNet.instance;
        return network != null && !network.IsServer() &&
            ZNet.m_onlineBackend == OnlineBackendType.Steamworks &&
            session != null && ReferenceEquals(session.Network, network) && !session.Failed &&
            session.ServerCharacterActive && session.ReadyAcknowledgementSent &&
            session.CharacterState != null && session.ManagedProfile != null &&
            (!restoring || !session.BackupOnly) &&
            ReferenceEquals(ValheimPrivateAccess.GetGamePlayerProfile(Game.instance), session.ManagedProfile) &&
            player.GetPlayerID() == session.ManagedProfile.GetPlayerID() &&
            string.Equals(player.GetPlayerName(), session.ManagedProfile.GetName(), StringComparison.Ordinal);
    }

    internal static bool BeforePlayerLoad(Player player)
    {
        if (_initialized &&
            !_shuttingDown &&
            player != null &&
            ReferenceEquals(player, Player.m_localPlayer))
        {
            ++_inventoryLoadSuppressionDepth;
            LocalHostCharacterRuntime.BeforePlayerLoad(player);
            return true;
        }

        return false;
    }

    internal static void AfterPlayerLoad(bool entered)
    {
        if (!entered || _inventoryLoadSuppressionDepth == 0)
        {
            return;
        }

        --_inventoryLoadSuppressionDepth;
        LocalHostCharacterRuntime.AfterPlayerLoad(true);
    }

    internal static bool BeforeContinueLogout(
        Game game,
        bool save,
        bool shouldExit,
        bool changeToStartScene)
    {
        if (_continueLogoutPassThrough)
        {
            return true;
        }

        if (!_initialized ||
            _shuttingDown ||
            game == null ||
            (!save && !shouldExit))
        {
            return true;
        }

        DeferredClientExit requested = new(
            DeferredClientExitKind.Logout,
            AddStopwatchDuration(
                Stopwatch.GetTimestamp(),
                GracefulExitSaveTimeoutTicks),
            game,
            save,
            shouldExit,
            changeToStartScene);
        return !TryBeginDeferredClientExit(requested);
    }

    internal static bool BeforeApplicationQuit()
    {
        if (_applicationQuitResumePending ||
            !_initialized ||
            _shuttingDown)
        {
            return true;
        }

        ZNet? znet = ZNet.instance;
        if (znet == null || znet.IsServer())
        {
            return true;
        }

        if (_deferredClientExit != null)
        {
            _deferredClientExit.Kind = DeferredClientExitKind.ApplicationQuit;
            return false;
        }

        DeferredClientExit requested = new(
            DeferredClientExitKind.ApplicationQuit,
            AddStopwatchDuration(
                Stopwatch.GetTimestamp(),
                GracefulExitSaveTimeoutTicks));
        return !TryBeginDeferredClientExit(requested);
    }

    internal static void BeforeApplicationQuitting()
    {
        if (!_initialized)
        {
            return;
        }

        _applicationQuitResumePending = false;
    }

    internal static void BeforeDisconnect(ZNet znet, ZNetPeer peer)
    {
        if (!_initialized || peer?.m_rpc == null)
        {
            return;
        }

        CleanupPeer(znet, peer.m_rpc);
    }

    /// <summary>
    /// Starts bookkeeping for one actual ZNet.SaveWorld invocation. This is
    /// deliberately separate from character capture: Valheim may still join a
    /// previous worker or abort before it reaches ZDOMan.PrepareSave.
    /// </summary>
    internal static void BeforeWorldSaveInvocation()
    {
        ZNet? server = ZNet.instance;
        if (!_initialized || _shuttingDown || server == null ||
            !server.IsServer())
        {
            return;
        }

        string operationId = ServerEventRuntime.OnWorldSaveStarted();
        if (string.IsNullOrEmpty(operationId))
        {
            operationId = Guid.NewGuid().ToString("N");
        }

        WorldSaveAttempt attempt = new WorldSaveAttempt(operationId);
        WorldSaveAttempt? displaced;
        lock (WorldSaveCheckpointGate)
        {
            displaced = _invokedWorldSaveAttempt;
            _invokedWorldSaveAttempt = attempt;
        }

        if (displaced != null)
        {
            const string displacedError =
                "A newer world save started before the previous invocation " +
                "reached the world snapshot boundary.";
            ServerManagerPlugin.Log.LogError(displacedError);
            ServerEventRuntime.OnWorldSaveCheckpointFailed(
                displaced.OperationId,
                displacedError);
        }
    }

    /// <summary>
    /// Runs on the main thread immediately before Valheim clones its ZDO world
    /// state. Freeze the same process-local character overlay generation here;
    /// later ACKed revisions remain dirty for the next world checkpoint.
    /// </summary>
    internal static void BeforeWorldSnapshotPrepared()
    {
        try
        {
            BeforeWorldSnapshotPreparedCore();
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            TryWriteCheckpointLog(
                () => ServerManagerPlugin.Log.LogError(
                    "Character checkpoint observation failed at the world " +
                    "snapshot boundary. The vanilla world save will continue: " +
                    exception));
        }
    }

    private static void BeforeWorldSnapshotPreparedCore()
    {
        if (!_initialized || _shuttingDown)
        {
            return;
        }

        WorldSaveAttempt? attempt;
        lock (WorldSaveCheckpointGate)
        {
            attempt = _invokedWorldSaveAttempt;
            _invokedWorldSaveAttempt = null;
        }

        if (attempt == null)
        {
            return;
        }

        try
        {
            CharacterSnapshotService? service = _serverCharacterService;
            if (service != null)
            {
                try
                {
                    LocalHostCharacterRuntime.CaptureForWorldCheckpoint();
                }
                catch (Exception hostException) when (!IntegrityCanonical.IsFatal(hostException))
                {
                    TryWriteCheckpointLog(() => ServerManagerPlugin.Log.LogWarning(
                        "Local-host capture failed; the last accepted shadow and other players " +
                        "remain eligible for this world checkpoint: " + hostException.Message));
                }
                attempt.CharacterService = service;
                attempt.CharacterCheckpoint = service.BeginCheckpoint();
            }
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            attempt.CharacterCheckpointWarning =
                "Could not freeze the character RAM shadow generation: " +
                exception.GetType().Name + ": " + exception.Message;
            TryWriteCheckpointLog(
                () => ServerManagerPlugin.Log.LogError(
                    attempt.CharacterCheckpointWarning +
                    " The vanilla world save will continue without a " +
                    "character cutoff for this operation."));
        }

        WorldSaveAttempt? displaced;
        lock (WorldSaveCheckpointGate)
        {
            displaced = _preparedWorldSaveAttempt;
            _preparedWorldSaveAttempt = attempt;
        }

        if (displaced != null)
        {
            const string error =
                "A prepared world-save checkpoint was replaced before its " +
                "worker thread started.";
            DiscardCharacterCheckpoint(displaced, error);
            ServerManagerPlugin.Log.LogError(error);
            ServerEventRuntime.OnWorldSaveCheckpointFailed(
                displaced.OperationId,
                error);
        }
    }

    /// <summary>
    /// Binds the prepared main-thread checkpoint to Valheim's save worker.
    /// No repository or Unity work is performed on this thread.
    /// </summary>
    internal static void BeforeWorldSaveWorker()
    {
        WorldSaveAttempt? attempt;
        lock (WorldSaveCheckpointGate)
        {
            attempt = _preparedWorldSaveAttempt;
            _preparedWorldSaveAttempt = null;
        }

        _activeWorldSaveWorkerAttempt = attempt;
    }

    /// <summary>
    /// Injected at the exact Valheim primary DB/metadata success branch. The
    /// method is intentionally thread-local and allocation-free.
    /// </summary>
    internal static void MarkWorldSaveWorkerSucceeded()
    {
        WorldSaveAttempt? attempt = _activeWorldSaveWorkerAttempt;
        if (attempt != null)
        {
            attempt.PrimaryWorldSaveSucceeded = true;
        }
    }

    internal static void AfterWorldSaveWorker()
    {
        WorldSaveAttempt? attempt = _activeWorldSaveWorkerAttempt;
        _activeWorldSaveWorkerAttempt = null;
        if (attempt == null)
        {
            return;
        }

        int queued = Interlocked.Increment(
            ref _queuedWorldSaveWorkerResultCount);
        if (queued > MaximumQueuedWorldSaveWorkerResults)
        {
            Interlocked.Decrement(ref _queuedWorldSaveWorkerResultCount);
            DiscardCharacterCheckpoint(
                attempt,
                "The world-save completion queue was full.");
            ZLog.LogError(
                "ServerManager world-save completion queue is full; the " +
                "matching character checkpoint will remain only in RAM.");
            return;
        }

        WorldSaveWorkerResults.Enqueue(new WorldSaveWorkerResult(attempt));
    }

    internal static void AfterWorldSaveInvocationFailed(Exception exception)
    {
        if (!_initialized)
        {
            return;
        }

        WorldSaveAttempt? attempt;
        lock (WorldSaveCheckpointGate)
        {
            attempt = _invokedWorldSaveAttempt;
            _invokedWorldSaveAttempt = null;
            if (attempt == null)
            {
                attempt = _preparedWorldSaveAttempt;
                _preparedWorldSaveAttempt = null;
            }
        }

        if (attempt != null)
        {
            DiscardCharacterCheckpoint(
                attempt,
                "The world-save invocation failed before checkpoint completion.");
            ServerEventRuntime.OnWorldSaveCheckpointFailed(
                attempt.OperationId,
                exception);
        }
    }

    /// <summary>
    /// Consumes worker results on the main thread. A primary world-save failure
    /// never advances character disk state. A successful world save persists
    /// exactly the immutable overlay generation captured at PrepareSave.
    /// </summary>
    internal static void ProcessCompletedWorldSaveCheckpoints()
    {
        while (WorldSaveWorkerResults.TryDequeue(
                   out WorldSaveWorkerResult result))
        {
            Interlocked.Decrement(ref _queuedWorldSaveWorkerResultCount);
            WorldSaveAttempt attempt = result.Attempt;
            if (!attempt.PrimaryWorldSaveSucceeded)
            {
                DiscardCharacterCheckpoint(
                    attempt,
                    "Valheim's primary world save did not reach its verified " +
                    "success branch.");
                TryWriteCheckpointLog(
                    () => ServerManagerPlugin.Log.LogError(
                        "VerifiedWorldSaveFailed operation=" +
                        attempt.OperationId +
                        ". Character disk state was left unchanged."));
                try
                {
                    ServerEventRuntime.OnWorldSaveCheckpointFailed(
                        attempt.OperationId,
                        "Valheim's primary world DB/metadata save did not reach " +
                        "its verified success branch. Character disk state was " +
                        "left unchanged.");
                }
                catch (Exception exception) when (
                    !IntegrityCanonical.IsFatal(exception))
                {
                    TryWriteCheckpointLog(
                        () => ServerManagerPlugin.Log.LogError(
                            "World-save failure status publication failed: " +
                            exception));
                }

                continue;
            }

            try
            {
                CharacterCheckpointBatch? checkpoint =
                    attempt.CharacterCheckpoint;
                if (checkpoint == null)
                {
                    ServerManagerCharacterCommitScope scope =
                        string.IsNullOrEmpty(attempt.CharacterCheckpointWarning)
                            ? ServerManagerCharacterCommitScope
                                .AllRetainedShadowsAtCutoff
                            : ServerManagerCharacterCommitScope.NotIncluded;
                    TryPublishWorldSaveCheckpointCompleted(
                        attempt.OperationId,
                        scope,
                        0,
                        0,
                        0,
                        attempt.CharacterCheckpointWarning);
                    continue;
                }

                CharacterSnapshotService? service = attempt.CharacterService;
                if (service == null)
                {
                    string warning =
                        "The successful world cutoff lost its owning character " +
                        "service reference and cannot be persisted.";
                    TryWriteCheckpointLog(
                        () => ServerManagerPlugin.Log.LogError(warning));
                    TryPublishWorldSaveCheckpointCompleted(
                        attempt.OperationId,
                        ServerManagerCharacterCommitScope.NotIncluded,
                        checkpoint.Count,
                        0,
                        0,
                        warning);
                    continue;
                }

                HashSet<string> affectedStorageKeys =
                    new HashSet<string>(CharacterStorageLayout.StorageKeyComparer);
                for (int index = 0; index < checkpoint.Entries.Count; ++index)
                {
                    CharacterCheckpointEntry entry = checkpoint.Entries[index];
                    PendingCharacterCheckpointEntry incoming =
                        new PendingCharacterCheckpointEntry(
                            attempt.OperationId,
                            service,
                            checkpoint,
                            entry);
                    try
                    {
                        AdoptCharacterCheckpointEntry(incoming);
                        affectedStorageKeys.Add(entry.StorageKey);
                    }
                    catch (Exception exception) when (
                        !IntegrityCanonical.IsFatal(exception))
                    {
                        PendingCharacterCheckpointAdoptions.Add(
                            new PendingCharacterCheckpointAdoption(
                                incoming,
                                exception));
                        TryWriteCheckpointLog(
                            () => ServerManagerPlugin.Log.LogError(
                                "Could not adopt one character checkpoint entry " +
                                "for isolated persistence; adoption itself will " +
                                "be retried: key=" + entry.StorageKey +
                                ", error=" + exception));
                    }
                }

                string[] orderedStorageKeys = affectedStorageKeys.ToArray();
                Array.Sort(orderedStorageKeys, StringComparer.Ordinal);
                for (int index = 0; index < orderedStorageKeys.Length; ++index)
                {
                    TryCommitPendingCharacterCheckpoint(
                        orderedStorageKeys[index],
                        force: false);
                }

                int pendingCount = 0;
                for (int index = 0; index < checkpoint.Entries.Count; ++index)
                {
                    CharacterCheckpointEntry entry = checkpoint.Entries[index];
                    if (IsCheckpointEntryPending(entry))
                    {
                        ++pendingCount;
                    }
                }

                pendingCount = Math.Min(checkpoint.Count, pendingCount);
                int persistedCount = checkpoint.Count - pendingCount;
                ServerManagerCharacterCommitScope completionScope =
                    pendingCount == 0
                        ? ServerManagerCharacterCommitScope
                            .AllRetainedShadowsAtCutoff
                        : ServerManagerCharacterCommitScope
                            .PartialRetainedShadowsAtCutoff;
                string warningText = pendingCount == 0
                    ? attempt.CharacterCheckpointWarning
                    : pendingCount.ToString(CultureInfo.InvariantCulture) +
                      " character snapshot(s) remain pending for isolated " +
                      "disk retry.";
                PublishWorldCharacterCheckpointResult(
                    attempt.OperationId,
                    checkpoint,
                    completionScope,
                    persistedCount,
                    pendingCount,
                    warningText);
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                TryWriteCheckpointLog(
                    () => ServerManagerPlugin.Log.LogError(
                        "The world disk save completed, but character checkpoint " +
                        "result processing encountered an isolated error. The " +
                        "vanilla world result remains successful: " + exception));
                CharacterCheckpointBatch? checkpoint =
                    attempt.CharacterCheckpoint;
                TryPublishWorldSaveCheckpointCompleted(
                    attempt.OperationId,
                    ServerManagerCharacterCommitScope
                        .PartialRetainedShadowsAtCutoff,
                    checkpoint?.Count ?? 0,
                    0,
                    checkpoint?.Count ?? 0,
                    "Character checkpoint processing failed after the world " +
                    "save: " + exception.GetType().Name + ": " +
                    exception.Message);
            }
        }

        TryAdoptDueCharacterCheckpoints(force: false);
        TryCommitDueCharacterCheckpoints();
    }

    private static void AdoptCharacterCheckpointEntry(
        PendingCharacterCheckpointEntry incoming)
    {
        CharacterSnapshotService service = incoming.Service;
        CharacterCheckpointBatch checkpoint = incoming.Checkpoint;
        CharacterCheckpointEntry entry = incoming.Entry;
        if (!PendingCharacterDiskWrites.TryGetValue(
                entry.StorageKey,
                out PendingCharacterDiskWrite pending))
        {
            PendingCharacterDiskWrites.Add(
                entry.StorageKey,
                new PendingCharacterDiskWrite(
                    incoming,
                    Stopwatch.GetTimestamp()));
            return;
        }

        if (!entry.Identity.EqualsIdentity(pending.Head.Entry.Identity))
        {
            throw new CharacterStorageException(
                "Different character identities cannot share a pending storage path.");
        }

        int headComparison = CompareCheckpointTargets(
            incoming.Entry,
            pending.Head.Entry);
        if (headComparison <= 0)
        {
            service.DiscardCheckpointEntry(checkpoint, entry);
            if (headComparison == 0 &&
                !incoming.Entry.Snapshot.MatchesSnapshot(
                    pending.Head.Entry.Snapshot))
            {
                TryWriteCheckpointLog(
                    () => ServerManagerPlugin.Log.LogError(
                        "Discarded a conflicting character cutoff with the same " +
                        "revision as the retained head: key=" + entry.StorageKey +
                        ", revision=" + entry.Snapshot.Revision.ToString(
                            CultureInfo.InvariantCulture)));
            }
            else if (headComparison < 0)
            {
                TryWriteCheckpointLog(
                    () => ServerManagerPlugin.Log.LogWarning(
                        "Discarded an older character cutoff while a newer " +
                        "checkpoint for the same storage key is pending: key=" +
                        entry.StorageKey + ", revision=" +
                        entry.Snapshot.Revision.ToString(
                            CultureInfo.InvariantCulture)));
            }

            return;
        }

        PendingCharacterCheckpointEntry? tail = pending.Tail;
        if (tail == null)
        {
            pending.Tail = incoming;
            return;
        }

        int tailComparison = CompareCheckpointTargets(incoming.Entry, tail.Entry);
        if (tailComparison <= 0)
        {
            service.DiscardCheckpointEntry(checkpoint, entry);
            if (tailComparison == 0 &&
                !incoming.Entry.Snapshot.MatchesSnapshot(
                    tail.Entry.Snapshot))
            {
                TryWriteCheckpointLog(
                    () => ServerManagerPlugin.Log.LogError(
                        "Discarded a conflicting character cutoff with the same " +
                        "revision as the retained tail: key=" + entry.StorageKey +
                        ", revision=" + entry.Snapshot.Revision.ToString(
                            CultureInfo.InvariantCulture)));
            }
            else if (tailComparison < 0)
            {
                TryWriteCheckpointLog(
                    () => ServerManagerPlugin.Log.LogWarning(
                        "Discarded an intermediate character cutoff because a " +
                        "newer tail is already retained: key=" + entry.StorageKey +
                        ", revision=" + entry.Snapshot.Revision.ToString(
                            CultureInfo.InvariantCulture)));
            }

            return;
        }

        tail.Service.DiscardCheckpointEntry(tail.Checkpoint, tail.Entry);
        pending.Tail = incoming;
    }

    private static int CompareCheckpointTargets(
        CharacterCheckpointEntry left,
        CharacterCheckpointEntry right)
    {
        int revisionComparison = left.Snapshot.Revision.CompareTo(
            right.Snapshot.Revision);
        return revisionComparison;
    }

    private static bool IsCheckpointEntryPending(
        CharacterCheckpointEntry entry)
    {
        for (int index = 0;
             index < PendingCharacterCheckpointAdoptions.Count;
             ++index)
        {
            if (ReferenceEquals(
                    PendingCharacterCheckpointAdoptions[index].Entry.Entry,
                    entry))
            {
                return true;
            }
        }

        if (!PendingCharacterDiskWrites.TryGetValue(
                entry.StorageKey,
                out PendingCharacterDiskWrite pending))
        {
            return false;
        }

        long newestPendingRevision = pending.Tail?.Entry.Snapshot.Revision ??
                                     pending.Head.Entry.Snapshot.Revision;
        return entry.Snapshot.Revision <= newestPendingRevision;
    }

    private static void TryAdoptDueCharacterCheckpoints(bool force)
    {
        PendingCharacterCheckpointAdoption[] due =
            PendingCharacterCheckpointAdoptions
                .Where(pending =>
                    force || Stopwatch.GetTimestamp() >=
                    pending.NextAttemptTimestamp)
                .OrderBy(
                    pending => pending.Entry.Entry.StorageKey,
                    StringComparer.Ordinal)
                .ThenBy(pending => pending.Entry.Entry.Snapshot.Revision)
                .ToArray();
        for (int index = 0; index < due.Length; ++index)
        {
            PendingCharacterCheckpointAdoption pending = due[index];
            if (!PendingCharacterCheckpointAdoptions.Contains(pending))
            {
                continue;
            }

            try
            {
                AdoptCharacterCheckpointEntry(pending.Entry);
                PendingCharacterCheckpointAdoptions.Remove(pending);
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                pending.RecordFailure(exception);
                if (pending.FailureCount == 4 ||
                    pending.FailureCount % 10 == 0)
                {
                    TryWriteCheckpointLog(
                        () => ServerManagerPlugin.Log.LogError(
                            "Character checkpoint adoption remains pending: " +
                            "key=" + pending.Entry.Entry.StorageKey +
                            ", revision=" +
                            pending.Entry.Entry.Snapshot.Revision.ToString(
                                CultureInfo.InvariantCulture) +
                            ", failureCount=" +
                            pending.FailureCount.ToString(
                                CultureInfo.InvariantCulture) + ", error=" +
                            pending.LastError));
                }
            }
        }
    }

    private static void TryCommitDueCharacterCheckpoints()
    {
        string[] storageKeys = PendingCharacterDiskWrites.Keys.ToArray();
        Array.Sort(storageKeys, StringComparer.Ordinal);
        for (int index = 0; index < storageKeys.Length; ++index)
        {
            TryCommitPendingCharacterCheckpoint(storageKeys[index], force: false);
        }
    }

    private static bool TryCommitPendingCharacterCheckpoint(
        string storageKey,
        bool force)
    {
        if (!PendingCharacterDiskWrites.TryGetValue(
                storageKey,
                out PendingCharacterDiskWrite pending) ||
            (!force && Stopwatch.GetTimestamp() <
                pending.NextAttemptTimestamp))
        {
            return false;
        }

        while (true)
        {
            PendingCharacterCheckpointEntry head = pending.Head;
            CharacterSnapshotService service = head.Service;
            string backupWarning;
            try
            {
                backupWarning = service.CommitCheckpointEntry(
                    head.Checkpoint,
                    head.Entry);
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                RegisterCharacterCheckpointFailure(pending, exception);
                return false;
            }

            int recoveredFailureCount = pending.FailureCount;
            PendingCharacterCheckpointEntry? tail = pending.Tail;
            if (tail == null)
            {
                PendingCharacterDiskWrites.Remove(storageKey);
            }
            else
            {
                pending.Head = tail;
                pending.Tail = null;
                pending.FailureCount = 0;
                pending.LastError = string.Empty;
                pending.NextAttemptTimestamp = Stopwatch.GetTimestamp();
            }

            // Persistence and payload-pin consumption are complete before any
            // observability work. A logger failure must never resurrect a
            // consumed checkpoint handle.
            TryWriteCheckpointLog(
                () =>
                {
                    if (!string.IsNullOrEmpty(backupWarning))
                    {
                        ServerManagerPlugin.Log.LogWarning(
                            "Character checkpoint backup warning: key=" +
                            storageKey + ", " + backupWarning);
                    }

                    if (recoveredFailureCount > 0)
                    {
                        ServerManagerPlugin.Log.LogInfo(
                            "CharacterCheckpointRecovered key=" + storageKey +
                            ", revision=" +
                            head.Entry.Snapshot.Revision.ToString(
                                CultureInfo.InvariantCulture) +
                            ", failures=" +
                            recoveredFailureCount.ToString(
                                CultureInfo.InvariantCulture));
                    }
                });

            if (tail == null)
            {
                return true;
            }
        }
    }

    private static void RegisterCharacterCheckpointFailure(
        PendingCharacterDiskWrite pending,
        Exception exception)
    {
        if (pending.FailureCount < int.MaxValue)
        {
            ++pending.FailureCount;
        }

        pending.LastError = exception.GetType().Name + ": " + exception.Message;
        pending.NextAttemptTimestamp = AddStopwatchDuration(
            Stopwatch.GetTimestamp(),
            GetCharacterCheckpointRetryTicks(pending.FailureCount));
        if (pending.FailureCount == 1 ||
            pending.FailureCount == 4 ||
            pending.FailureCount % 10 == 0)
        {
            TryWriteCheckpointLog(
                () => ServerManagerPlugin.Log.LogError(
                    "Character checkpoint disk write remains pending: key=" +
                    pending.Head.Entry.StorageKey + ", revision=" +
                    pending.Head.Entry.Snapshot.Revision.ToString(
                        CultureInfo.InvariantCulture) + ", failureCount=" +
                    pending.FailureCount.ToString(CultureInfo.InvariantCulture) +
                    ", nextRetrySeconds=" +
                    GetCharacterCheckpointRetrySeconds(pending.FailureCount)
                        .ToString(CultureInfo.InvariantCulture) + ", error=" +
                    pending.LastError));
        }
    }

    private static void TryWriteCheckpointLog(Action write)
    {
        try
        {
            write();
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            // Logging is intentionally outside the persistence state machine.
        }
    }

    private static long GetCharacterCheckpointRetryTicks(int failureCount)
    {
        return checked(
            (long)GetCharacterCheckpointRetrySeconds(failureCount) *
            Stopwatch.Frequency);
    }

    private static int GetCharacterCheckpointRetrySeconds(int failureCount)
    {
        return failureCount switch
        {
            <= 1 => 5,
            2 => 15,
            3 => 30,
            _ => 60
        };
    }

    private static void DrainPendingCharacterCheckpointBeforeShutdown()
    {
        long deadline = AddStopwatchDuration(
            Stopwatch.GetTimestamp(),
            CharacterCheckpointShutdownRetryTicks);
        bool firstPass = true;
        while ((PendingCharacterDiskWrites.Count != 0 ||
                PendingCharacterCheckpointAdoptions.Count != 0) &&
               (firstPass || Stopwatch.GetTimestamp() < deadline))
        {
            firstPass = false;
            TryAdoptDueCharacterCheckpoints(force: true);
            string[] storageKeys = PendingCharacterDiskWrites.Keys.ToArray();
            Array.Sort(storageKeys, StringComparer.Ordinal);
            for (int index = 0; index < storageKeys.Length; ++index)
            {
                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    break;
                }

                TryCommitPendingCharacterCheckpoint(
                    storageKeys[index],
                    force: true);
            }

            if ((PendingCharacterDiskWrites.Count == 0 &&
                 PendingCharacterCheckpointAdoptions.Count == 0) ||
                Stopwatch.GetTimestamp() >= deadline)
            {
                break;
            }

            Thread.Sleep(250);
        }

        int unresolvedCount = checked(
            PendingCharacterDiskWrites.Count +
            PendingCharacterCheckpointAdoptions.Count);
        if (unresolvedCount != 0)
        {
            IEnumerable<string> diskEntries =
                PendingCharacterDiskWrites
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair =>
                        pair.Key + "@" +
                        pair.Value.Head.Entry.Snapshot.Revision.ToString(
                            CultureInfo.InvariantCulture) + "(disk)");
            IEnumerable<string> adoptionEntries =
                PendingCharacterCheckpointAdoptions
                    .OrderBy(
                        pending => pending.Entry.Entry.StorageKey,
                        StringComparer.Ordinal)
                    .ThenBy(pending => pending.Entry.Entry.Snapshot.Revision)
                    .Select(pending =>
                        pending.Entry.Entry.StorageKey + "@" +
                        pending.Entry.Entry.Snapshot.Revision.ToString(
                            CultureInfo.InvariantCulture) + "(adoption)");
            string unresolved = string.Join(
                ", ",
                diskEntries.Concat(adoptionEntries));
            TryWriteCheckpointLog(
                () => ServerManagerPlugin.Log.LogError(
                    "CharacterCheckpointShutdownUnresolved count=" +
                    unresolvedCount.ToString(
                        CultureInfo.InvariantCulture) + ", entries=" + unresolved +
                    ". Vanilla shutdown will continue; inspect character backups " +
                    "before reopening the server."));
        }
    }

    private static void PublishWorldCharacterCheckpointResult(
        string operationId,
        CharacterCheckpointBatch checkpoint,
        ServerManagerCharacterCommitScope scope,
        int persistedCount,
        int pendingCount,
        string warning)
    {
        string token = scope ==
            ServerManagerCharacterCommitScope.AllRetainedShadowsAtCutoff
            ? "Completed"
            : "Partial";
        TryWriteCheckpointLog(
            () => ServerManagerPlugin.Log.LogInfo(
                "WorldCharacterCheckpoint" + token + " operation=" +
                operationId + ", checkpoint=" +
                checkpoint.CheckpointId.ToString("N") + ", captured=" +
                checkpoint.Count.ToString(CultureInfo.InvariantCulture) +
                ", persisted=" +
                persistedCount.ToString(CultureInfo.InvariantCulture) +
                ", pending=" +
                pendingCount.ToString(CultureInfo.InvariantCulture)));
        TryPublishWorldSaveCheckpointCompleted(
            operationId,
            scope,
            checkpoint.Count,
            persistedCount,
            pendingCount,
            warning);
    }

    private static void TryPublishWorldSaveCheckpointCompleted(
        string operationId,
        ServerManagerCharacterCommitScope scope,
        int capturedCount,
        int persistedCount,
        int pendingCount,
        string warning)
    {
        try
        {
            ServerEventRuntime.OnWorldSaveCheckpointCompleted(
                operationId,
                scope,
                capturedCount,
                persistedCount,
                pendingCount,
                warning);
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            TryWriteCheckpointLog(
                () => ServerManagerPlugin.Log.LogError(
                    "The world/character checkpoint result was finalized, but " +
                    "its completion telemetry could not be published: " +
                    exception));
        }
    }

    private static void DiscardCharacterCheckpoint(
        WorldSaveAttempt attempt,
        string reason)
    {
        CharacterCheckpointBatch? checkpoint = attempt.CharacterCheckpoint;
        CharacterSnapshotService? service = attempt.CharacterService;
        if (checkpoint == null || service == null)
        {
            return;
        }

        try
        {
            service.DiscardCheckpoint(checkpoint);
            attempt.CharacterCheckpoint = null;
            attempt.CharacterService = null;
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            TryWriteCheckpointLog(
                () => ServerManagerPlugin.Log.LogError(
                    "Could not release an abandoned character checkpoint payload " +
                    "reservation (" + reason + "): " + exception));
        }
    }

    private static void ResetWorldSaveCheckpointState()
    {
        lock (WorldSaveCheckpointGate)
        {
            _invokedWorldSaveAttempt = null;
            _preparedWorldSaveAttempt = null;
        }

        PendingCharacterDiskWrites.Clear();
        PendingCharacterCheckpointAdoptions.Clear();
        _activeWorldSaveWorkerAttempt = null;
        while (WorldSaveWorkerResults.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _queuedWorldSaveWorkerResultCount, 0);
    }

    internal static bool BeforeNetworkShutdown(ZNet znet)
    {
        StopOptionalModPublication(znet);
        bool wasServer = false;
        try
        {
            if (znet == null)
            {
                return false;
            }

            wasServer = znet.IsServer();
            if (_initialized && !wasServer)
            {
                if (ReferenceEquals(_manifestPreparationNetwork, znet))
                    CancelPendingManifestPreparation();
                if (_client != null && ReferenceEquals(_client.Network, znet))
                    CancelClientManifestPreparation(_client);
            }
            if (!wasServer || !_initialized)
            {
                return wasServer;
            }

            ServerScheduleRuntime.Stop();
            ZNetPeer[] peers = znet.GetPeers().ToArray();
            foreach (ZNetPeer peer in peers)
            {
                if (peer?.m_rpc == null)
                {
                    continue;
                }

                try
                {
                    CleanupPeer(znet, peer.m_rpc);
                }
                catch (Exception exception) when (
                    !IntegrityCanonical.IsFatal(exception))
                {
                    ReportNetworkShutdownHookFailure(
                        "pre-shutdown peer cleanup",
                        exception);
                }
            }
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            ReportNetworkShutdownHookFailure(
                "pre-shutdown peer enumeration",
                exception);
        }

        // Harmony carries this state into the finalizer. Even when best-effort
        // cleanup fails, returning the already observed server role lets the
        // finalizer drain joined save work without blocking vanilla StopAll.
        return wasServer;
    }

    internal static void AfterNetworkShutdown(
        ZNet znet,
        bool wasServer,
        Exception? shutdownException)
    {
        StopOptionalModPublication(znet);
        if (!_initialized || znet == null)
        {
            return;
        }

        if (ReferenceEquals(_manifestPreparationNetwork, znet))
            CancelPendingManifestPreparation();
        if (!wasServer && _client != null && ReferenceEquals(_client.Network, znet))
            CancelClientManifestPreparation(_client);

        if (shutdownException != null)
        {
            if (wasServer)
            {
                // StopAll joins the world worker before the fallible ZDO,
                // matchmaking, socket, and peer cleanup stages. Consume any
                // result that did reach us, but do not publish a successful
                // shutdown or dispose ServerManager while vanilla networking
                // may still be live.
                RunNetworkShutdownStep(
                    "failed StopAll world result drain",
                    ProcessCompletedWorldSaveCheckpoints);
                RunNetworkShutdownStep(
                    "failed StopAll character retry drain",
                    DrainPendingCharacterCheckpointBeforeShutdown);
                TryLogNetworkShutdownError(
                    "Valheim StopAll failed. Any completed world/character " +
                    "checkpoint was drained, but ServerManager services remain " +
                    "available because network shutdown did not complete: " +
                    shutdownException);
            }

            return;
        }

        if (wasServer)
        {
            // StopAll has now joined any outstanding world save worker. Consume
            // its verified result before releasing the RAM shadow service.
            RunNetworkShutdownStep(
                "completed StopAll world result drain",
                ProcessCompletedWorldSaveCheckpoints);
            RunNetworkShutdownStep(
                "completed StopAll character retry drain",
                DrainPendingCharacterCheckpointBeforeShutdown);
            int unresolvedCharacterCheckpoints = checked(
                PendingCharacterDiskWrites.Count +
                PendingCharacterCheckpointAdoptions.Count);
            if (unresolvedCharacterCheckpoints != 0)
            {
                TryLogNetworkShutdownError(
                    "Server shutdown is continuing with " +
                    unresolvedCharacterCheckpoints.ToString(
                        CultureInfo.InvariantCulture) +
                    " unresolved character checkpoint storage key(s). The world " +
                    "save was not cancelled; inspect character backups before " +
                    "reopening the server.");
            }
            // Publish shutdown only after any world worker joined by StopAll
            // has been verified and its matching character checkpoint has
            // either committed or exhausted the bounded retry. Consumers then
            // never observe server.shutdown before the final server.saved.
            RunNetworkShutdownStep(
                "server shutdown event publication",
                ServerEventRuntime.OnServerShutdown);
            RunNetworkShutdownStep(
                "player activity log shutdown",
                () => PlayerActivityRuntime.Stop("server_shutdown"));
            RunNetworkShutdownStep(
                "Steam authentication callback disposal",
                DisposeSteamAuthenticationCallback);
            ServerIntegrityService? integrityService = _integrityService;
            _integrityService = null;
            _manifestValidator?.Clear();
            RunNetworkShutdownStep(
                "integrity service disposal",
                () => integrityService?.Dispose());
            CharacterSnapshotService? characterService =
                _serverCharacterService;
            _serverCharacterService = null;
            RunNetworkShutdownStep(
                "local host character restoration",
                LocalHostCharacterRuntime.Close);
            RunNetworkShutdownStep(
                "local host lifecycle reset",
                ResetLocalHostLifecycle);
            RunNetworkShutdownStep(
                "character service disposal",
                () => characterService?.Dispose());
            RunNetworkShutdownStep(
                "server save admission cleanup",
                () =>
                {
                    ServerFinalSaveDrains.Clear();
                    SaveAdmissionHistory.Clear();
                    GlobalSaveAdmissions.Clear();
                    _globalSaveAdmissionBytes = 0;
                    _serverCharacterStorageStartupState =
                        ServerCharacterStorageStartupState.NotRun;
                    _serverDataRootStartupFailed = false;
                });
            RunNetworkShutdownStep(
                "server data-root release",
                ServerDataRoot.Release);
            RunNetworkShutdownStep(
                "world checkpoint state reset",
                ResetWorldSaveCheckpointState);
            return;
        }

        // A delayed teardown of an older ZNet must not clear a newer attempt.
        if (_client?.Network != null && !ReferenceEquals(_client.Network, znet)) return;
        RunNetworkShutdownStep(
            "client profile restoration",
            RestoreClientProfile);
        RunNetworkShutdownStep(
            "client detection reset",
            ClientDetection.Reset);
        RunNetworkShutdownStep(
            "client command guard reset",
            CheatCommandGuard.Reset);
        ClientConnection? client = _client;
        CancelClientManifestPreparation(client);
        _client = null;
        if (client != null)
        {
            RunNetworkShutdownStep(
                "client fragment cleanup",
                () => _clientReassembler.RemovePeer(client.Rpc));
        }
    }

    internal static void ReportNetworkShutdownHookFailure(
        string stage,
        Exception exception)
    {
        try
        {
            ServerManagerPlugin.Log.LogError(
                "ServerManager " + stage + " failed without blocking Valheim " +
                "network shutdown: " + exception);
        }
        catch (Exception loggingException) when (
            !IntegrityCanonical.IsFatal(loggingException))
        {
            // Shutdown must remain owned by Valheim even if logging is gone.
        }
    }

    private static void RunNetworkShutdownStep(
        string stage,
        Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            ReportNetworkShutdownHookFailure(stage, exception);
        }
    }

    private static void TryLogNetworkShutdownError(string message)
    {
        try
        {
            ServerManagerPlugin.Log.LogError(message);
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            // Preserve the original StopAll result when logging is unavailable.
        }
    }

    private static void OnProtocolPacket(ZRpc rpc, ZPackage package)
    {
        if (!_initialized || _shuttingDown)
        {
            return;
        }

        ZNet? znet = ZNet.instance;
        if (znet == null)
        {
            return;
        }

        if (znet.IsServer())
        {
            HandleServerPacket(znet, rpc, package);
        }
        else
        {
            HandleClientPacket(rpc, package);
        }
    }

    private static void OnDetectionPacket(ZRpc rpc, ZPackage package)
    {
        if (!_initialized || _shuttingDown)
        {
            return;
        }

        ZNet? network = ZNet.instance;
        if (network == null)
        {
            return;
        }

        if (!network.IsServer())
        {
            FailClient(
                rpc,
                "The server sent an unexpected anti-cheat report.");
            return;
        }

        HandleServerDetectionReport(network, rpc, package);
    }

    private static void OnEventReportPacket(ZRpc rpc, ZPackage package)
    {
        if (!_initialized || _shuttingDown)
        {
            return;
        }

        ZNet? network = ZNet.instance;
        if (network == null)
        {
            return;
        }

        if (!network.IsServer())
        {
            FailClient(rpc, "The server sent an unexpected client event report.");
            return;
        }

        if (!EventClientReportCodec.TryDecode(
                package,
                out EventClientReport report,
                out ProtocolRejection rejection))
        {
            SendServerRejection(rpc, rejection);
            return;
        }

        ProtocolRejection? identityError = null;
        bool sessionMatched = _coordinator.TryGetSnapshot(
                rpc,
                out ConnectionSessionSnapshot session) &&
            session.State == ConnectionSessionState.Ready &&
            ProtocolByteUtil.FixedTimeEquals(
                session.SessionId,
                report.SessionId) &&
            ProtocolByteUtil.FixedTimeEquals(session.Nonce, report.Nonce);
        if (!sessionMatched ||
            !TryResolveActiveDetectionPeer(
                network,
                rpc,
                out _,
                out identityError))
        {
            SendServerRejection(
                rpc,
                identityError ?? new ProtocolRejection(
                    ProtocolRejectCode.EventProtocolViolation,
                    "The event report did not match a ready authenticated session."));
            return;
        }

        if (!ServerEventRuntime.TryProcessClientReport(
                rpc,
                report,
                out string safeError))
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.EventProtocolViolation,
                    string.IsNullOrWhiteSpace(safeError)
                        ? "The event report violated the session protocol."
                        : safeError));
        }
    }

    private static void OnEventDisplayPacket(ZRpc rpc, ZPackage package)
    {
        if (!_initialized || _shuttingDown)
        {
            return;
        }

        ZNet? network = ZNet.instance;
        if (network == null)
        {
            return;
        }

        if (network.IsServer())
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.EventProtocolViolation,
                    "Clients may not send server display messages."));
            return;
        }

        ClientConnection? client = _client;
        if (!EventDisplayCodec.TryDecode(
                package,
                out EventDisplayPacket display) ||
            client == null || client.Failed ||
            !ReferenceEquals(client.Rpc, rpc) ||
            !client.ReadyAcknowledgementSent ||
            client.SessionId == null || client.Nonce == null ||
            !ProtocolByteUtil.FixedTimeEquals(
                client.SessionId,
                display.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(client.Nonce, display.Nonce) ||
            display.Sequence != client.LastEventDisplaySequence + 1)
        {
            FailClient(rpc, "The server event display protocol was invalid.");
            return;
        }

        client.LastEventDisplaySequence = display.Sequence;
        ShowEventDisplay(display.Kind, display.Message, display.MessageKey, display.Arguments, display.LabelMask);
    }

    internal static void ReportLocalChat(Talker.Type type, string text)
    {
        EventClientReportKind kind;
        switch (type)
        {
            case Talker.Type.Shout:
                kind = EventClientReportKind.Shout;
                break;
            case Talker.Type.Normal:
                kind = EventClientReportKind.Normal;
                break;
            case Talker.Type.Whisper:
                kind = EventClientReportKind.Whisper;
                break;
            default:
                return;
        }

        EventClientReport report = new()
        {
            Kind = kind,
            Text = ClientEventObservation.SafeText(text, 500)
        };
        if (!string.IsNullOrWhiteSpace(report.Text))
        {
            SendLocalEventReport(report);
        }
    }

    internal static void ReportLocalDeath(Player player)
    {
        if (player != null && ReferenceEquals(player, Player.m_localPlayer))
        {
            EventClientReport report =
                ClientEventObservation.CreateDeath(player);
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                PlayerActivityRuntime.ObserveListenHostDeath(report);
            }

            SendLocalEventReport(report);
        }
    }

    internal static void ReportLocalBossKill(Character boss)
    {
        if (boss != null && boss.IsBoss() && boss.IsOwner())
        {
            EventClientReport report =
                ClientEventObservation.CreateBossKill(boss);
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                ServerEventRuntime.ProcessAuthoritativeServerBossDeath(report);
            }
            else
            {
                SendLocalEventReport(report);
            }
        }
    }

    private static void SendLocalEventReport(EventClientReport report)
    {
        if (!_initialized || _shuttingDown || report == null)
        {
            return;
        }

        ZNet? network = ZNet.instance;
        if (network == null)
        {
            return;
        }

        if (network.IsServer())
        {
            ServerEventRuntime.ProcessListenHostReport(report);
            return;
        }

        ClientConnection? client = _client;
        if (client == null || client.Failed ||
            !client.ReadyAcknowledgementSent ||
            client.SessionId == null || client.Nonce == null)
        {
            return;
        }

        uint sequence = client.NextEventReportSequence;
        if (sequence == 0 || sequence == uint.MaxValue)
        {
            FailClient(client.Rpc, "The client event report sequence was exhausted.");
            return;
        }

        try
        {
            report.SessionId = client.SessionId;
            report.Nonce = client.Nonce;
            report.Sequence = sequence;
            ZPackage package = EventClientReportCodec.Encode(report);
            if (!RawProtocolRpcTransport.Send(
                    client.Rpc,
                    EventReportRpcName,
                    package,
                    EventClientReportCodec.MaximumPacketBytes))
            {
                FailClient(
                    client.Rpc,
                    "The client event report connection closed.");
                return;
            }

            client.NextEventReportSequence = sequence + 1;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            FailClient(
                client.Rpc,
                "The client event report could not be sent.",
                exception);
        }
    }

    internal static void BroadcastEventDisplay(
        string kind, string message, string messageKey, string[] arguments, byte labelMask)
    {
        ZNet? server = ZNet.instance;
        if (!_initialized || _shuttingDown || server == null ||
            !server.IsServer() ||
            (string.IsNullOrWhiteSpace(message) && string.IsNullOrEmpty(messageKey)))
        {
            return;
        }

        foreach (KeyValuePair<ZRpc, uint> pair in
                 ServerEventDisplaySequences.ToArray())
        {
            ZRpc rpc = pair.Key;
            uint sequence = pair.Value;
            if (sequence == 0 || sequence == uint.MaxValue ||
                !_coordinator.TryGetSnapshot(
                    rpc,
                    out ConnectionSessionSnapshot session) ||
                session.State != ConnectionSessionState.Ready)
            {
                ServerEventDisplaySequences.Remove(rpc);
                continue;
            }

            try
            {
                EventDisplayPacket display = new()
                {
                    SessionId = session.SessionId,
                    Nonce = session.Nonce,
                    Sequence = sequence,
                    Kind = kind,
                    Message = message,
                    MessageKey = messageKey,
                    Arguments = arguments,
                    LabelMask = labelMask
                };
                ZPackage package = EventDisplayCodec.Encode(display);
                if (RawProtocolRpcTransport.Send(
                        rpc,
                        EventDisplayRpcName,
                        package,
                        EventDisplayCodec.MaximumPacketBytes))
                {
                    ServerEventDisplaySequences[rpc] = sequence + 1;
                }
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogDebug(
                    "Could not send an event overlay to a closing peer: " +
                    exception.Message);
            }
        }

        if (!server.IsDedicated())
        {
            ShowEventDisplay(kind, message, messageKey, arguments, labelMask);
        }
    }

    private static void ShowEventDisplay(
        string kind, string message, string key, string[] arguments, byte labelMask)
    {
        // Remote packets reach here only after authentication and sequence
        // advancement. The real listen host uses the same language-local path.
        ServerEventOverlay.Show(kind, string.IsNullOrEmpty(key)
            ? message
            : EventMessageText.Render(key, arguments, labelMask));
    }

    private static void OnAdminEntitlementPacket(
        ZRpc rpc,
        ZPackage package)
    {
        if (!_initialized || _shuttingDown)
        {
            return;
        }

        ZNet? network = ZNet.instance;
        if (network == null)
        {
            return;
        }

        if (network.IsServer())
        {
            RejectDetectionProtocol(
                rpc,
                "Clients may not send admin command entitlements.");
            return;
        }

        HandleClientAdminEntitlement(rpc, package);
    }

    private static void OnRawTransportError(
        ZRpc rpc,
        ProtocolRejection rejection)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            SendServerRejection(rpc, rejection);
        }
        else
        {
            FailClient(rpc, rejection.SafeMessage, playerMessage: PlayerConnectionMessages.FromRejection(rejection));
        }
    }

    private static void OnSecurityRawTransportError(
        ZRpc rpc,
        ProtocolRejection rejection)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            RejectDetectionProtocol(rpc, rejection.SafeMessage);
        }
        else
        {
            FailClient(rpc, ClientSecurityPolicyRejectionMessage);
        }
    }

    private static void DrainClientDetectionSignals()
    {
        ClientConnection? client = _client;
        if (client == null ||
            client.Failed ||
            !client.ReadyAcknowledgementSent ||
            client.SessionId == null ||
            client.Nonce == null)
        {
            return;
        }

        int sent = 0;
        while (sent < MaximumClientDetectionReportsPerTick)
        {
            ClientDetectionSignal signal;
            if (!ClientDetection.TryDequeue(out signal) &&
                !CheatCommandGuard.TryDequeue(out signal))
            {
                break;
            }

            uint sequence = client.NextDetectionSequence;
            if (sequence == 0 || sequence == uint.MaxValue)
            {
                FailClient(
                    client.Rpc,
                    "The anti-cheat report sequence was exhausted.");
                return;
            }

            try
            {
                DetectionReport report = new(
                    client.SessionId,
                    client.Nonce,
                    sequence,
                    signal.Evidence,
                    signal.Detail,
                    signal.PolicyGeneration);
                ZPackage package = DetectionReportCodec.Encode(report);
                if (!RawProtocolRpcTransport.Send(
                        client.Rpc,
                        DetectionRpcName,
                        package,
                        DetectionReportCodec.MaximumPacketBytes))
                {
                    FailClient(
                        client.Rpc,
                        "The anti-cheat report connection closed.");
                    return;
                }

                client.NextDetectionSequence = sequence + 1;
                ++sent;
            }
            catch (Exception exception)
                when (!IntegrityCanonical.IsFatal(exception))
            {
                FailClient(
                    client.Rpc,
                    "The anti-cheat report could not be sent.",
                    exception);
                return;
            }
        }
    }

    private static void HandleClientAdminEntitlement(
        ZRpc rpc,
        ZPackage package)
    {
        if (!AdminCommandEntitlementCodec.TryDecode(
                package,
                out AdminCommandEntitlement entitlement,
                out ProtocolRejection rejection))
        {
            FailClient(rpc, rejection.SafeMessage, playerMessage: PlayerConnectionMessages.FromRejection(rejection));
            return;
        }

        ClientConnection? client = _client;
        if (client == null ||
            client.Failed ||
            !ReferenceEquals(client.Rpc, rpc) ||
            !client.ReadyAcknowledgementSent ||
            client.SessionId == null ||
            client.Nonce == null)
        {
            FailClient(
                rpc,
                "The admin command entitlement arrived before readiness.");
            return;
        }

        if (!ProtocolByteUtil.FixedTimeEquals(
                client.SessionId,
                entitlement.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(
                client.Nonce,
                entitlement.Nonce))
        {
            FailClient(
                rpc,
                "The admin command entitlement did not belong to this connection.");
            return;
        }

        uint expectedSequence = client.NextAdminEntitlementSequence;
        if (expectedSequence == 0 ||
            expectedSequence == uint.MaxValue ||
            entitlement.Sequence != expectedSequence)
        {
            FailClient(
                rpc,
                "The admin command entitlement sequence was invalid.");
            return;
        }

        client.NextAdminEntitlementSequence = expectedSequence + 1;
        try
        {
            CheatCommandGuard.ApplyAdminEntitlement(
                entitlement.Granted,
                entitlement.ValidForMilliseconds);
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            FailClient(
                rpc,
                "The admin command entitlement could not be applied.",
                exception);
        }
    }

    private static void HandleServerDetectionReport(
        ZNet server,
        ZRpc rpc,
        ZPackage package)
    {
        if (!DetectionReportCodec.TryDecode(
                package,
                out DetectionReport report,
                out ProtocolRejection decodeError))
        {
            RejectDetectionProtocol(rpc, decodeError.SafeMessage);
            return;
        }

        if (!_coordinator.TryGetSnapshot(
                rpc,
                out ConnectionSessionSnapshot session) ||
            session.State != ConnectionSessionState.Ready ||
            !session.PeerInfoAuthenticated)
        {
            RejectDetectionProtocol(
                rpc,
                "Anti-cheat reports are accepted only after connection readiness.");
            return;
        }

        if (!ProtocolByteUtil.FixedTimeEquals(
                session.SessionId,
                report.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(
                session.Nonce,
                report.Nonce))
        {
            RejectDetectionProtocol(
                rpc,
                "The anti-cheat report did not belong to this connection.");
            return;
        }

        if (!TryResolveActiveDetectionPeer(
                server,
                rpc,
                out ServerPeerIdentity identity,
                out ProtocolRejection identityError))
        {
            SendServerRejection(rpc, identityError);
            return;
        }

        if (!ServerDetectionStates.TryGetValue(
                rpc,
                out ServerDetectionState state))
        {
            RejectDetectionProtocol(
                rpc,
                "The anti-cheat session policy was unavailable.");
            return;
        }

        if (!IsExpectedDetectionEvidence(state, report.Evidence))
        {
            RejectDetectionProtocol(
                rpc,
                "The anti-cheat report was not enabled by server policy.");
            return;
        }

        if (state.LastSequence == uint.MaxValue ||
            report.Sequence != state.LastSequence + 1)
        {
            RejectDetectionProtocol(
                rpc,
                "The anti-cheat report sequence was invalid.");
            return;
        }

        if (report.Evidence == DetectionEvidence.CheatCommand &&
            !TryAdmitCommandReport(state))
        {
            RejectDetectionProtocol(rpc, "The anti-cheat command report rate was exceeded.");
            return;
        }

        if (report.PolicyGeneration > state.AcknowledgedPolicyGeneration)
        {
            RejectDetectionProtocol(rpc, "The anti-cheat policy generation was not acknowledged.");
            return;
        }

        // In-flight/queued observations retain their original generation. Consume
        // sequence order, but never reinterpret old evidence under a new Kick/Ban.
        if (report.PolicyGeneration < state.PolicyGeneration)
        {
            state.LastSequence = report.Sequence;
            if (DetectionEvidenceCatalog.IsSticky(report.Evidence))
            {
                if (state.StickyEvidence.TryGetValue(report.Evidence, out uint previousGeneration) &&
                    previousGeneration >= report.PolicyGeneration) return;
                state.StickyEvidence[report.Evidence] = report.PolicyGeneration;
            }
            LogDetection(identity, report, DetectionAction.Log, "stale_policy_observation");
            return;
        }

        if (report.Evidence == DetectionEvidence.CheatCommand &&
            state.Policy.AllowAdminCheatCommands &&
            IsCurrentServerAdmin(server, identity))
        {
            // Admin clients still report every command because the vanilla
            // client state can outlive a server-side revocation. Keep strict
            // sequencing and the same rolling flood cap as every other peer,
            // but do not treat legitimate admin usage as cheat evidence.
            state.LastSequence = report.Sequence;
            return;
        }

        if (DetectionEvidenceCatalog.IsSticky(report.Evidence) &&
            state.StickyEvidence.TryGetValue(report.Evidence, out uint stickyGeneration) &&
            stickyGeneration == report.PolicyGeneration)
        {
            RejectDetectionProtocol(
                rpc,
                "A duplicate anti-cheat report was rejected.");
            return;
        }

        if (DetectionEvidenceCatalog.IsSticky(report.Evidence))
            state.StickyEvidence[report.Evidence] = report.PolicyGeneration;

        state.LastSequence = report.Sequence;
        ProcessDetectionEvidence(
            server,
            rpc,
            identity,
            state,
            report);
    }

    // Shared final-authentication boundary for character access and detection.
    internal static bool TryResolveActiveDetectionPeer(
        ZNet server,
        ZRpc rpc,
        out ServerPeerIdentity identity,
        out ProtocolRejection rejection)
    {
        identity = null!;
        if (!ServerPeerResolver.TryResolve(
                server,
                rpc,
                out identity,
                out rejection) ||
            !identity.HasAuthenticatedIdentity)
        {
            rejection ??= new ProtocolRejection(
                ProtocolRejectCode.PeerIdentityUnavailable,
                "The anti-cheat peer identity was unavailable.");
            return false;
        }

        SteamAuthenticationAttempt? attempt;
        lock (SteamAuthenticationGate)
        {
            if (!SteamAuthenticationsByRpc.TryGetValue(
                    rpc,
                    out attempt) ||
                !IsCurrentSteamAuthenticationLocked(attempt) ||
                attempt.Phase != SteamAuthenticationPhase.Active ||
                !ReferenceEquals(attempt.Rpc, rpc) ||
                !ReferenceEquals(attempt.Peer, identity.Peer) ||
                !attempt.ReachedActive ||
                attempt.EnqueuedCallbackCount < 1 ||
                attempt.ProcessedCallbackCount < 1 ||
                attempt.CallbackOverflowed ||
                attempt.StaleCallbackCaptured ||
                attempt.LateCallbackCaptured ||
                !attempt.BeginAuthInvocationObserved ||
                !attempt.BeginAuthResultRecorded ||
                !attempt.BeginAuthImmediateAccepted ||
                attempt.BeginAuthExecutionFaulted ||
                attempt.DuplicateBeginAuthInvocation ||
                attempt.LatestResponse !=
                EAuthSessionResponse.k_EAuthSessionResponseOK)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                    "Final Steam authentication was not active.");
                return false;
            }
        }

        try
        {
            if (!string.Equals(
                    identity.HostId,
                    attempt.SteamId.m_SteamID.ToString(
                        CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) ||
                !WorldBuffers.TryGetValue(
                    rpc,
                    out BufferedWorldSocket buffer) ||
                buffer.Quarantined || buffer.Overflowed || buffer.InboundViolation)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PeerIdentityUnavailable,
                    "The authenticated Steam session or world gate was unavailable.");
                return false;
            }
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            rejection = new ProtocolRejection(
                ProtocolRejectCode.PeerIdentityUnavailable,
                "The connection-bound Steam peer could not be revalidated.");
            return false;
        }

        rejection = null!;
        return true;
    }

    private static bool IsExpectedDetectionEvidence(
        ServerDetectionState state,
        DetectionEvidence evidence)
    {
        if (DetectionEvidenceCatalog.IsDiagnostic(evidence))
        {
            if (evidence == DetectionEvidence.AssemblyDetectorUnavailable)
            {
                return state.Policy.DetectValheimTooler;
            }

            return state.Policy.DetectCheatEngine ||
                   state.Policy.DetectExternalTools ||
                   state.Policy.DetectGenericProcessNames;
        }

        if (DetectionEvidenceCatalog.IsCheatEngine(evidence))
        {
            return state.Policy.DetectCheatEngine;
        }

        if (DetectionEvidenceCatalog.IsExternalTool(evidence))
        {
            return state.Policy.DetectExternalTools;
        }

        if (DetectionEvidenceCatalog.IsGenericProcessName(evidence))
        {
            return state.Policy.DetectGenericProcessNames;
        }

        if (DetectionEvidenceCatalog.IsValheimTooler(evidence))
        {
            return state.Policy.DetectValheimTooler;
        }

        if (evidence == DetectionEvidence.CarryWeightLimitExceeded)
        {
            return state.Policy.EnforceCarryWeightLimit;
        }

        if (evidence == DetectionEvidence.MaximumDamageLimitExceeded)
        {
            return state.Policy.EnforceMaximumDamageLimit;
        }

        if (evidence == DetectionEvidence.InvalidGameplayValue)
        {
            return state.Policy.EnforceCarryWeightLimit || state.Policy.EnforceMaximumDamageLimit;
        }

        return evidence == DetectionEvidence.CheatCommand &&
               state.Policy.MonitorCheatCommands;
    }

    private static void ProcessDetectionEvidence(
        ZNet server,
        ZRpc rpc,
        ServerPeerIdentity identity,
        ServerDetectionState state,
        DetectionReport report)
    {
        if (DetectionEvidenceCatalog.IsDiagnostic(report.Evidence))
        {
            LogDetection(
                identity,
                report,
                DetectionAction.Log,
                "detector diagnostic");
            return;
        }

        if (DetectionEvidenceCatalog.IsGenericProcessName(report.Evidence))
        {
            // Generic executable names are deliberately observation-only.
            LogDetection(
                identity,
                report,
                DetectionAction.Log,
                "low-confidence observation");
            return;
        }

        if (report.Evidence == DetectionEvidence.CheatCommand)
        {
            ProcessCheatCommandReport(
                server,
                rpc,
                identity,
                state,
                report);
            return;
        }

        DetectionAction action = DetectionEvidenceCatalog.IsGameplayLimit(report.Evidence) ||
            report.Evidence == DetectionEvidence.InvalidGameplayValue
            ? state.StatLimitResponse
            : state.CheatDetectionResponse;
        ApplyDetectionAction(
            server,
            rpc,
            identity,
            state,
            report,
            action,
            DetectionEvidenceCatalog.IsGameplayLimit(report.Evidence)
                ? "session-bound client gameplay-limit report; observed value not transmitted; limit=" +
                  (report.Evidence == DetectionEvidence.CarryWeightLimitExceeded
                      ? state.Policy.MaximumCarryWeight : state.Policy.MaximumDamage)
                  .ToString("R", CultureInfo.InvariantCulture)
                : "client detector report");
    }

    private static void ProcessCheatCommandReport(
        ZNet server,
        ZRpc rpc,
        ServerPeerIdentity identity,
        ServerDetectionState state,
        DetectionReport report)
    {
        long now = Stopwatch.GetTimestamp();
        long windowTicks = checked(
            (long)state.CheatCommandWindowSeconds *
            Stopwatch.Frequency);
        long cutoff = now - windowTicks;
        while (state.CheatCommandTimestamps.Count > 0 &&
               state.CheatCommandTimestamps.Peek() < cutoff)
        {
            state.CheatCommandTimestamps.Dequeue();
        }

        state.CheatCommandTimestamps.Enqueue(now);
        int attempts = state.CheatCommandTimestamps.Count;
        string reason = "cheat command attempt " +
            attempts.ToString(CultureInfo.InvariantCulture) +
            "/" +
            state.CheatCommandThreshold.ToString(
                CultureInfo.InvariantCulture);

        if (attempts < state.CheatCommandThreshold ||
            state.CheatCommandThresholdActionApplied &&
            state.CheatCommandActionGeneration == state.PolicyGeneration)
        {
            LogDetection(identity, report, DetectionAction.Log, reason);
            return;
        }

        state.CheatCommandThresholdActionApplied = true;
        state.CheatCommandActionGeneration = state.PolicyGeneration;
        ApplyDetectionAction(
            server,
            rpc,
            identity,
            state,
            report,
            state.CheatDetectionResponse,
            reason + "; repeated cheat-command threshold");
    }

    private static bool IsCurrentServerAdmin(
        ZNet server,
        ServerPeerIdentity identity)
    {
        try
        {
            // identity.HostId is derived from the currently connected Steam
            // socket and was revalidated immediately before this call. Never
            // accept an account ID or an admin claim from the report payload.
            if (server.IsAdmin(identity.HostId)) return true;

            // Valheim 1.0 maps Steam to display prefix V_ and its native
            // ListContainsId overwrites an earlier bare/Steam_ match with the
            // filtered lookup. Recheck the same live server-owned list so
            // existing administrator files remain effective. This does not
            // trust client data or grant a new source of authority.
            return ServerCommands.GetSteamListEntries(
                ValheimPrivateAccess.GetAdminList(server).GetList(),
                identity.HostId).Length != 0;
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "Server admin status could not be revalidated; " +
                "the administrator exception was denied.");
            return false;
        }
    }

    private static bool HasNumericStatLimitAdminBypass(ZNet server,
        ServerPeerIdentity identity, ServerDetectionState state,
        DetectionEvidence evidence) =>
        DetectionEvidenceCatalog.IsGameplayLimit(evidence) &&
        state.Policy.AllowAdminCheatCommands && IsCurrentServerAdmin(server, identity);

    private static bool IsCurrentLocalHostStatLimitAdmin(ZNet server)
    {
        try
        {
            if (!server.IsServer() || server.IsDedicated() ||
                ZNet.m_onlineBackend != OnlineBackendType.Steamworks) return false;
            string hostId = SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture);
            // Hosting itself grants nothing: use the same canonical Steam
            // account and live server adminlist as the character-policy check.
            return CharacterSteamIdentity.TryParseCanonicalAccountId(
                       "steamworks:" + hostId, out _) && server.IsAdmin(hostId);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    private static void RecordNumericStatLimitAdminBypass(ServerPeerIdentity identity,
        DetectionEvidence evidence, string source, string detail) =>
        ServerEventRuntime.RecordSecurityEvent(ServerManagerEventKinds.SecurityAdminBypass,
            "steamworks:" + identity.HostId, identity.PlayerName, source,
            evidence.ToString(), DetectionAction.Log, "bypassed", detail);

    private static bool TryCancelNumericDetectionResponseForAdmin(ZNet server,
        ZRpc rpc, ServerPeerIdentity identity, ServerDetectionState state)
    {
        if (state.TerminalActionApplied ||
            !HasNumericStatLimitAdminBypass(server, identity, state, state.TerminalEvidence))
            return false;

        state.PendingKickTimestamp = 0;
        if (WorldBuffers.TryGetValue(rpc, out BufferedWorldSocket buffer))
            buffer.ReleaseDetectionOnlyInbound();
        RecordNumericStatLimitAdminBypass(identity, state.TerminalEvidence,
            state.TerminalSource, "pending numeric response cancelled by current admin membership");
        state.TerminalEvidence = DetectionEvidence.None;
        return true;
    }

    // Creation runs before the first remote character session exists. The real
    // listen host is the operator; remote peers require current admin-list membership.
    private static bool IsCharacterCreationAdmin(CharacterIdentity identity)
    {
        try
        {
            if (!_initialized || _shuttingDown || identity == null ||
                !CharacterSteamIdentity.TryParseCanonicalAccountId(identity.AccountId, out ulong steamId))
                return false;
            ZNet server = ZNet.instance;
            if (server == null || !server.IsServer() ||
                ZNet.m_onlineBackend != OnlineBackendType.Steamworks) return false;
            string hostId = steamId.ToString(CultureInfo.InvariantCulture);
            if (!server.IsDedicated() && SteamUser.GetSteamID().m_SteamID == steamId)
            {
                Game game = Game.instance;
                PlayerProfile? profile = game == null ? null : ValheimPrivateAccess.GetGamePlayerProfile(game);
                return _localHostRequested && !_localHostStartupFailed &&
                    ReferenceEquals(_localHostNetwork, server) && profile != null &&
                    string.Equals(identity.CharacterName, CharacterNamePolicy.NormalizeAndValidate(
                        profile.GetName()), StringComparison.Ordinal);
            }

            SteamAuthenticationAttempt? attempt;
            lock (SteamAuthenticationGate)
            {
                if (!SteamAuthenticationsById.TryGetValue(steamId, out attempt) ||
                    attempt.Phase != SteamAuthenticationPhase.Active) return false;
            }
            return TryResolveActiveDetectionPeer(server, attempt.Rpc,
                       out ServerPeerIdentity peer, out _) &&
                string.Equals(peer.HostId, hostId, StringComparison.Ordinal) &&
                string.Equals(identity.CharacterName, CharacterNamePolicy.NormalizeAndValidate(peer.PlayerName),
                    StringComparison.Ordinal) && IsCurrentServerAdmin(server, peer);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    // Character-policy exceptions use server-owned identity: the actual listen
    // host operator or a live authenticated administrator. Offline stored reads
    // do not use this callback; client-provided admin claims grant nothing.
    private static bool IsCharacterPolicyAdmin(CharacterIdentity identity)
    {
        try
        {
            if (identity == null ||
                !CharacterSteamIdentity.TryParseCanonicalAccountId(
                    identity.AccountId, out ulong steamId))
            {
                return false;
            }
            ZNet server = ZNet.instance;
            if (server == null || !server.IsServer() ||
                ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
            {
                return false;
            }

            string hostId = steamId.ToString(CultureInfo.InvariantCulture);
            if (!server.IsDedicated() && SteamUser.GetSteamID().m_SteamID == steamId)
            {
                // Hosted intent is captured before opening the character
                // session, so the initial backup capture receives the same
                // exception as later saves. IsServer alone includes singleplayer.
                return (_localHostRequested && !_localHostStartupFailed &&
                        ReferenceEquals(_localHostNetwork, server)) ||
                       server.IsAdmin(hostId);
            }

            SteamAuthenticationAttempt? attempt;
            lock (SteamAuthenticationGate)
            {
                if (!SteamAuthenticationsById.TryGetValue(steamId, out attempt) ||
                    attempt.Phase != SteamAuthenticationPhase.Active)
                {
                    return false;
                }
            }

            return TryResolveActiveDetectionPeer(server, attempt.Rpc,
                       out ServerPeerIdentity peer, out _) &&
                   string.Equals(peer.HostId, hostId, StringComparison.Ordinal) &&
                   _serverCharacterService != null &&
                   _serverCharacterService.TryGetServerSession(peer.Rpc,
                       out CharacterSession? session) && session != null &&
                   session.Identity.EqualsIdentity(identity) &&
                   IsCurrentServerAdmin(server, peer);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    private static bool TryAdmitCommandReport(
        ServerDetectionState state)
    {
        return DetectionRateLimiter.TryAdmit(
            state.CommandReportTimestamps,
            Stopwatch.GetTimestamp(),
            checked(
                (long)CommandReportWindowSeconds *
                Stopwatch.Frequency),
            MaximumCommandReportsPerWindow);
    }

    private static DetectionAction NormalizeDetectionAction(
        DetectionAction action,
        string settingName)
    {
        if (action == DetectionAction.Log || action == DetectionAction.Kick ||
            action == DetectionAction.Ban)
        {
            return action;
        }

        ServerManagerPlugin.Log.LogError(
            "Invalid " + settingName + " configuration was reduced to Log.");
        return DetectionAction.Log;
    }

    private static void ApplyDetectionAction(
        ZNet server,
        ZRpc rpc,
        ServerPeerIdentity identity,
        ServerDetectionState state,
        DetectionReport report,
        DetectionAction action,
        string reason,
        string source = "client_reported")
    {
        action = NormalizeDetectionAction(action, "detection response");

        if (HasNumericStatLimitAdminBypass(server, identity, state, report.Evidence))
        {
            RecordNumericStatLimitAdminBypass(identity, report.Evidence, source, reason);
            return;
        }

        LogDetection(identity, report, action, reason, source);
        if (action == DetectionAction.Log)
        {
            return;
        }

        if (state.TerminalActionApplied)
        {
            return;
        }

        if (action == DetectionAction.Kick)
        {
            // Do not let later admin promotion cancel a pending non-numeric
            // sanction simply because a numerical report arrived first.
            if (state.PendingKickTimestamp != 0 &&
                !DetectionEvidenceCatalog.IsGameplayLimit(report.Evidence))
            {
                state.TerminalEvidence = report.Evidence;
                state.TerminalSource = source;
            }
            if (state.PendingKickTimestamp == 0)
            {
                state.TerminalEvidence = report.Evidence;
                state.TerminalSource = source;
                if (!WorldBuffers.TryGetValue(
                        rpc,
                        out BufferedWorldSocket buffer) ||
                    !buffer.TryRestrictInboundToDetection())
                {
                    ExecuteTerminalDetectionAction(
                        server,
                        rpc,
                        identity,
                        state,
                        DetectionAction.Kick);
                    return;
                }

                state.PendingKickTimestamp = checked(
                    Stopwatch.GetTimestamp() +
                    DetectionKickAggregationTicks);
            }

            return;
        }

        // Ban is explicit opt-in and stronger than Kick. Apply it immediately,
        // upgrading any Kick waiting for the short same-burst aggregation
        // window.
        state.PendingKickTimestamp = 0;
        state.TerminalEvidence = report.Evidence;
        state.TerminalSource = source;
        ExecuteTerminalDetectionAction(
            server,
            rpc,
            identity,
            state,
            DetectionAction.Ban);
    }

    private static void RefreshAdminCommandEntitlements()
    {
        ZNet? server = ZNet.instance;
        if (server == null || !server.IsServer())
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        foreach (KeyValuePair<ZRpc, ServerDetectionState> pair in
                 ServerDetectionStates.ToArray())
        {
            ServerDetectionState state = pair.Value;
            if (!state.Policy.AllowAdminCheatCommands ||
                state.TerminalActionApplied ||
                state.NextAdminEntitlementRefreshTimestamp > now)
            {
                continue;
            }

            if (!_coordinator.TryGetSnapshot(
                    pair.Key,
                    out ConnectionSessionSnapshot session) ||
                session.State != ConnectionSessionState.Ready ||
                !session.PeerInfoAuthenticated)
            {
                continue;
            }

            if (!TryRefreshAdminCommandEntitlement(
                    server,
                    pair.Key,
                    state,
                    now,
                    out ProtocolRejection rejection))
            {
                SendServerRejection(pair.Key, rejection);
            }
        }
    }

    private static bool TryRefreshAdminCommandEntitlement(
        ZNet server,
        ZRpc rpc,
        ServerDetectionState state,
        long now,
        out ProtocolRejection rejection)
    {
        if (!state.Policy.AllowAdminCheatCommands ||
            !_coordinator.TryGetSnapshot(
                rpc,
                out ConnectionSessionSnapshot session) ||
            session.State != ConnectionSessionState.Ready ||
            !session.PeerInfoAuthenticated)
        {
            rejection = new ProtocolRejection(
                ProtocolRejectCode.InvalidTransition,
                "Admin command authorization was requested before readiness.");
            return false;
        }

        if (!TryResolveActiveDetectionPeer(
                server,
                rpc,
                out ServerPeerIdentity identity,
                out rejection))
        {
            return false;
        }

        uint sequence = state.NextAdminEntitlementSequence;
        if (sequence == 0 || sequence == uint.MaxValue)
        {
            ServerManagerPlugin.Log.LogWarning(
                "The admin command entitlement sequence was exhausted.");
            rejection = new ProtocolRejection(
                ProtocolRejectCode.DetectionProtocolViolation,
                ClientSecurityPolicyRejectionMessage);
            return false;
        }

        bool granted = IsCurrentServerAdmin(server, identity);

        try
        {
            // Release a cancelled numerical kick before advertising a new
            // generation: its detection-only gate would discard PolicyAck.
            if (granted && state.PendingKickTimestamp != 0)
                TryCancelNumericDetectionResponseForAdmin(server, rpc, identity, state);
            if (state.LastAdminEntitlementGranted.HasValue &&
                state.LastAdminEntitlementGranted.Value != granted)
            {
                // Re-advertise the same caps in a new generation. This makes a
                // fresh carry-weight/damage observation reportable after adminlist
                // revocation without retagging queued evidence or losing sequence
                // checks. Numeric sanctions still wait for this policy's ACK.
                state.PolicyGeneration = checked(state.PolicyGeneration + 1);
            }
            state.LastAdminEntitlementGranted = granted;
            AdminCommandEntitlement entitlement = new(
                session.SessionId,
                session.Nonce,
                sequence,
                granted,
                granted ? AdminEntitlementGrantMilliseconds : 0);
            ZPackage package =
                AdminCommandEntitlementCodec.Encode(entitlement);
            if (!RawProtocolRpcTransport.Send(
                    rpc,
                    AdminEntitlementRpcName,
                    package,
                    AdminCommandEntitlementCodec.MaximumPacketBytes))
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.InternalError,
                    "The admin command entitlement connection closed.");
                return false;
            }
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            rejection = new ProtocolRejection(
                ProtocolRejectCode.InternalError,
                "The server could not refresh admin command authorization.");
            return false;
        }

        state.NextAdminEntitlementSequence = sequence + 1;
        state.NextAdminEntitlementRefreshTimestamp = checked(
            now + AdminEntitlementRefreshTicks);
        rejection = null!;
        return true;
    }

    private static void ProcessPendingDetectionKicks()
    {
        ZNet? server = ZNet.instance;
        if (server == null || !server.IsServer())
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        foreach (KeyValuePair<ZRpc, ServerDetectionState> pair in
                 ServerDetectionStates.ToArray())
        {
            ServerDetectionState state = pair.Value;
            if (state.TerminalActionApplied ||
                state.PendingKickTimestamp == 0 ||
                state.PendingKickTimestamp > now)
            {
                continue;
            }

            state.PendingKickTimestamp = 0;
            if (!_coordinator.TryGetSnapshot(
                    pair.Key,
                    out ConnectionSessionSnapshot session) ||
                session.State != ConnectionSessionState.Ready ||
                !session.PeerInfoAuthenticated)
            {
                continue;
            }

            if (!TryResolveActiveDetectionPeer(
                    server,
                    pair.Key,
                    out ServerPeerIdentity identity,
                    out ProtocolRejection identityError))
            {
                SendServerRejection(pair.Key, identityError);
                continue;
            }

            ExecuteTerminalDetectionAction(
                server,
                pair.Key,
                identity,
                state,
                DetectionAction.Kick);
        }
    }

    // Only explicit operational kick entrypoints call this. Ban, authentication,
    // protocol failures and detection sanctions must retain their immediate path.
    internal static bool TryBeginOperationalKick(ZNet server, ZNetPeer peer)
    {
        if (!_initialized || _shuttingDown || server == null ||
            !server.IsServer() || peer?.m_rpc == null)
        {
            return false;
        }

        ZRpc rpc = peer.m_rpc;
        try
        {
            if (PendingDisconnects.ContainsKey(rpc) ||
                !_coordinator.TryGetSnapshot(rpc, out ConnectionSessionSnapshot session) ||
                session.State != ConnectionSessionState.Ready ||
                !TryResolveActiveDetectionPeer(server, rpc, out ServerPeerIdentity identity, out _) ||
                !ReferenceEquals(identity.Peer, peer) ||
                !ServerDetectionStates.TryGetValue(rpc, out ServerDetectionState detection) ||
                detection.PendingKickTimestamp != 0 || detection.TerminalActionApplied ||
                _serverCharacterService == null ||
                !_serverCharacterService.TryGetServerSession(rpc, out CharacterSession? character) ||
                character == null || character.IsClosed ||
                !WorldBuffers.ContainsKey(rpc))
            {
                return false;
            }

            if (ServerFinalSaveDrains.TryGetValue(rpc, out ServerFinalSaveDrain existing))
            {
                // Never restart the deadline or replace an already-running exit.
                if (!existing.OperationalKick) return false;
                if (Stopwatch.GetTimestamp() >= existing.DeadlineTimestamp)
                    CompleteOperationalKick(server, rpc, "timeout");
                return true;
            }

            ServerFinalSaveDrains.Add(rpc, new ServerFinalSaveDrain(
                AddStopwatchDuration(Stopwatch.GetTimestamp(), OperationalKickSaveTimeoutTicks),
                operationalKick: true));
            SendProtocolOrThrow(rpc, ProtocolPacketCodec.CreateOperationalKickRequest(
                session.SessionId, session.Nonce, _connectionLimits));
            ServerManagerPlugin.Log.LogInfo(
                "OperationalKickSaveRequested account=" + identity.HostId +
                ", timeoutSeconds=" + OperationalKickSaveTimeoutSeconds);
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerFinalSaveDrains.Remove(rpc);
            try
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Operational kick could not request final save; disconnecting immediately: " +
                    exception.Message);
            }
            catch (Exception logError) when (!IntegrityCanonical.IsFatal(logError))
            {
                // A broken optional log sink cannot cancel the immediate fallback.
            }
            return false;
        }
    }

    private static void CompleteOperationalKick(ZNet server, ZRpc rpc, string outcome)
    {
        if (!ServerFinalSaveDrains.TryGetValue(rpc, out ServerFinalSaveDrain drain) ||
            !drain.OperationalKick)
        {
            return;
        }

        ServerFinalSaveDrains.Remove(rpc);
        // This is a connection shutdown only. Retained RAM remains eligible for
        // the next world checkpoint; never force a character-only disk commit.
        try
        {
            ServerManagerPlugin.Log.LogInfo(
                "OperationalKickSaveFinished outcome=" + outcome +
                ", ramAckSent=" + drain.SaveAcknowledged + ", diskCommit=false");
        }
        finally
        {
            try
            {
                if (ServerPeerResolver.TryResolve(server, rpc, out ServerPeerIdentity identity, out _))
                {
                    // InternalKick(peer) also queues a second Disconnect after
                    // one second. Send its vanilla notification directly here:
                    // this path already owns a fixed deadline and final cleanup.
                    identity.Peer.m_rpc.Invoke("Kicked");
                }
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Operational kick notification failed: " + exception.Message);
            }
            finally
            {
                // The underlying socket flushes before closing. Notification
                // failure must never extend the deadline or cancel cleanup.
                DisconnectServerPeer(rpc);
            }
        }
    }

    private static void ProcessExpiredServerFinalSaveDrains()
    {
        ZNet? server = ZNet.instance;
        if (server == null || !server.IsServer())
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        foreach (KeyValuePair<ZRpc, ServerFinalSaveDrain> pair in
                 ServerFinalSaveDrains.ToArray())
        {
            ServerFinalSaveDrain drain = pair.Value;
            if (drain.TimeoutRejected ||
                drain.DeadlineTimestamp >= now)
            {
                continue;
            }

            if (drain.OperationalKick)
            {
                CompleteOperationalKick(server, pair.Key, "timeout");
                continue;
            }

            drain.TimeoutRejected = true;
            SendServerRejection(
                pair.Key,
                new ProtocolRejection(
                    ProtocolRejectCode.HandshakeTimedOut,
                    "The bounded final-save drain timed out."));
        }
    }

    private static void ExecuteTerminalDetectionAction(
        ZNet server,
        ZRpc rpc,
        ServerPeerIdentity identity,
        ServerDetectionState state,
        DetectionAction action)
    {
        if (state.TerminalActionApplied)
        {
            return;
        }

        // Revalidate at execution as adminlist membership can change during
        // the short kick aggregation window. Never release a non-numeric kick.
        if (TryCancelNumericDetectionResponseForAdmin(server, rpc, identity, state)) return;

        if (action == DetectionAction.Ban)
        {
            try
            {
                // ZNet.Ban(string) first treats its argument as a player name.
                // A peer named after another account's numeric Steam64 could
                // therefore redirect the ban. The identity here is already
                // pinned to the live Steam socket, so write that exact value.
                ValheimPrivateAccess.GetBannedList(server).Add(
                    ServerCommands.GetCanonicalSteamListEntry(identity.HostId));
                RecordDetectionResponse(identity, state, action, "banlist_add_returned");
            }
            catch (Exception exception)
                when (!IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogError(
                    "Failed to persist an anti-cheat ban for authenticated " +
                    "Steam peer " +
                    DetectionLog.QuoteValue(identity.HostId, 32) +
                    ": " +
                    exception.Message);
                RecordDetectionResponse(identity, state, action, "banlist_add_failed");
            }
        }

        state.TerminalActionApplied = true;
        state.PendingKickTimestamp = 0;
        SendServerRejection(
            rpc,
            new ProtocolRejection(
                ProtocolRejectCode.CheatDetected,
                ClientSecurityPolicyRejectionMessage));

        try
        {
            ValheimPrivateAccess.InternalKick(server, identity.Peer);
            RecordDetectionResponse(identity, state, action, "kick_call_returned");
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogDebug(
                "Exact anti-cheat peer kick required scheduled fallback: " +
                exception.Message);
            RecordDetectionResponse(identity, state, action, "disconnect_fallback_scheduled");
        }
    }

    internal static void RecordCharacterStatLimits(
        ZRpc? rpc,
        string accountId,
        string characterName,
        IReadOnlyList<CharacterStatLimitFinding> findings,
        string source,
        bool applyResponse = false)
    {
        try
        {
            if (findings.Count == 0) return;

            ZNet? server = applyResponse && source == "snapshot_received" && rpc != null
                ? ZNet.instance : null;
            ServerPeerIdentity? identity = null;
            ServerDetectionState? state = null;
            ConnectionSessionSnapshot? session = null;
            if (!ReferenceEquals(server, null) && server != null && server.IsServer() && rpc != null &&
                _coordinator != null &&
                _coordinator.TryGetSnapshot(rpc, out ConnectionSessionSnapshot ready) &&
                ready.State == ConnectionSessionState.Ready && ready.PeerInfoAuthenticated &&
                ServerDetectionStates.TryGetValue(rpc, out ServerDetectionState candidate) &&
                TryResolveActiveDetectionPeer(server, rpc, out ServerPeerIdentity peer, out _) &&
                string.Equals(accountId, "steamworks:" + peer.HostId, StringComparison.Ordinal) &&
                string.Equals(characterName, CharacterNamePolicy.NormalizeAndValidate(peer.PlayerName),
                    StringComparison.Ordinal))
            {
                identity = peer;
                state = candidate;
                session = ready;
            }

            foreach (CharacterStatLimitFinding finding in findings)
            {
                DetectionEvidence evidence = finding.Code switch
                {
                    "maximum_health" => DetectionEvidence.MaximumHealthLimitExceeded,
                    "maximum_stamina" => DetectionEvidence.MaximumStaminaLimitExceeded,
                    "maximum_eitr" => DetectionEvidence.MaximumEitrLimitExceeded,
                    _ => DetectionEvidence.None
                };
                if (evidence == DetectionEvidence.None) continue;
                string detail = finding.Code + " value=" +
                    finding.Value.ToString("R", CultureInfo.InvariantCulture) + " limit=" +
                    finding.Limit.ToString("R", CultureInfo.InvariantCulture) +
                    "; numeric cap does not reject or normalize the snapshot";
                if (identity != null && state != null && session != null)
                {
                    ApplyDetectionAction(server!, rpc!, identity, state,
                        new DetectionReport(session.SessionId, session.Nonce, 1, evidence, string.Empty, state.PolicyGeneration),
                         CurrentNumericLimitResponse(state), detail, source);
                }
                else
                {
                    // Stored data and listen-host snapshots are observations, not
                    // grounds to punish a fabricated or unauthenticated remote peer.
                    ServerEventRuntime.RecordSecurityEvent(
                        ServerManagerEventKinds.SecurityDetection,
                        accountId, characterName, source, evidence.ToString(),
                        DetectionAction.Log, "observed", detail);
                }
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // This hook runs after admission/ACK. A failed observer or optional
            // response must not turn that accepted revision into a save rejection.
            ServerEventRuntime.RecordSecurityEvent(
                ServerManagerEventKinds.SecurityDetection,
                accountId, characterName, source, "stat_limit_processing",
                DetectionAction.Log, "processing_failed", exception.Message);
        }
    }

    private static void RecordDetectionResponse(
        ServerPeerIdentity identity,
        ServerDetectionState state,
        DetectionAction action,
        string outcome)
    {
        ServerEventRuntime.RecordSecurityEvent(
            ServerManagerEventKinds.SecurityResponse,
            "steamworks:" + identity.HostId,
            identity.PlayerName,
            state.TerminalSource,
            state.TerminalEvidence.ToString(),
            action,
            outcome,
            string.Empty);
    }

    private static void LogDetection(
        ServerPeerIdentity identity,
        DetectionReport report,
        DetectionAction action,
        string reason,
        string source = "client_reported")
    {
        string detail = string.IsNullOrEmpty(report.Detail)
            ? "-"
            : report.Detail;
        string message =
            "Anti-cheat event steam=" +
            DetectionLog.QuoteValue(identity.HostId, 32) +
            " player=" +
            DetectionLog.QuoteValue(identity.PlayerName, 64) +
            " evidence=" +
            report.Evidence +
            " detail=" +
            detail +
            " action=" +
            action +
            " reason=" +
            reason;

        ServerEventRuntime.RecordSecurityEvent(
            ServerManagerEventKinds.SecurityDetection,
            "steamworks:" + identity.HostId,
            identity.PlayerName,
            source,
            report.Evidence.ToString(),
            action,
            DetectionEvidenceCatalog.IsDiagnostic(report.Evidence) ? "diagnostic" :
                action == DetectionAction.Log ? "observed" : "response_requested",
            reason + "; detail=" + detail);

        try
        {
            if (DetectionEvidenceCatalog.IsDiagnostic(report.Evidence))
            {
                ServerManagerPlugin.Log.LogInfo(message);
            }
            else
            {
                ServerManagerPlugin.Log.LogWarning(message);
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // A console listener is optional too. Its failure cannot cancel a
            // sanction or invalidate the already-accepted character save.
        }
    }

    private static void RejectDetectionProtocol(
        ZRpc rpc,
        string serverLogDetail)
    {
        ServerManagerPlugin.Log.LogWarning(
            "Anti-cheat protocol rejection detail: " +
            SanitizeRemoteRejectMessage(serverLogDetail ?? string.Empty));
        SendServerRejection(
            rpc,
            new ProtocolRejection(
                ProtocolRejectCode.DetectionProtocolViolation,
                ClientSecurityPolicyRejectionMessage));
    }

    private static void HandleServerPacket(
        ZNet server,
        ZRpc rpc,
        ZPackage package)
    {
        if (!ProtocolPacketCodec.TryDecode(
                package,
                _connectionLimits,
                out ProtocolPacket packet,
                out ProtocolRejection rejection))
        {
            SendServerRejection(rpc, rejection);
            return;
        }

        switch (packet.Kind)
        {
            case ProtocolPacketKind.PolicyAck:
                HandleServerPolicyAck(server, rpc, packet);
                break;

            case ProtocolPacketKind.ClientCharacterRejection:
                HandleServerClientCharacterRejection(server, rpc, packet);
                break;

            case ProtocolPacketKind.ManifestResponse:
                {
                    ProtocolOperationResult result = _coordinator.AcceptManifest(
                        server,
                        rpc,
                        packet,
                        _manifestValidator,
                        SendProtocolOrThrow);
                    if (!result.Succeeded)
                    {
                        SendServerRejection(rpc, result.Rejection);
                    }

                    break;
                }

            case ProtocolPacketKind.ReadyAck:
                {
                    ProtocolOperationResult result =
                        _coordinator.AcceptReadyAck(server, rpc, packet);
                    if (!result.Succeeded)
                    {
                        SendServerRejection(rpc, result.Rejection);
                        return;
                    }

                    bool firstJoin = false;
                    CharacterSession? eventCharacterSession = null;
                    CharacterEnvelope? initialCharacterCommit = null;
                    if (result.Session != null &&
                        result.Session.ServerCharactersEnabled)
                    {
                        try
                        {
                            CharacterSnapshotService service =
                                _serverCharacterService ??
                                throw new InvalidOperationException(
                                    "The authoritative character service was unavailable.");
                            if (service.TryGetServerSession(
                                    rpc,
                                    out CharacterSession? pendingSession) &&
                                pendingSession != null)
                            {
                                firstJoin = pendingSession.BackupOnly
                                    ? pendingSession.BackupCaptureCreatesProfile
                                    : pendingSession.PendingInitialCommit;
                                eventCharacterSession = pendingSession;
                                if (pendingSession.PendingInitialCommit)
                                {
                                    initialCharacterCommit =
                                        pendingSession.GetPendingInitialEnvelope();
                                }
                            }

                            if (service.FinalizePendingInitialSnapshot(rpc))
                            {
                                if (initialCharacterCommit == null)
                                {
                                    throw new CharacterStorageException(
                                        "The committed first-join snapshot metadata was unavailable.");
                                }

                                ServerManagerPlugin.Log.LogInfo(
                                    "Committed a prepared character snapshot " +
                                    "after its authenticated ReadyAck.");
                            }
                            if (eventCharacterSession?.BackupOnly == true)
                            {
                                SendProtocolOrThrow(rpc, ProtocolPacketCodec.Encode(new ProtocolPacket(
                                    ProtocolPacketKind.BackupCaptureCommitted,
                                    ProtocolSequence.BackupCaptureCommitted,
                                    result.Session.SessionId, result.Session.Nonce,
                                    Array.Empty<byte>()), _connectionLimits));
                                ServerManagerPlugin.Log.LogInfo(
                                    "Backup-only local character accepted for " +
                                    eventCharacterSession.Identity.AccountId + "/" +
                                    eventCharacterSession.Identity.CharacterName +
                                    "; subsequent saves use the normal character checkpoint pipeline.");
                                if (ServerDetectionStates.TryGetValue(rpc, out ServerDetectionState committedCaptureState))
                                {
                                    RecordCharacterStatLimits(rpc,
                                        eventCharacterSession.Identity.AccountId,
                                        eventCharacterSession.Identity.CharacterName,
                                        committedCaptureState.BackupCaptureStatFindings, "snapshot_received", applyResponse: true);
                                    committedCaptureState.BackupCaptureStatFindings = Array.Empty<CharacterStatLimitFinding>();
                                    if (committedCaptureState.TerminalActionApplied || committedCaptureState.PendingKickTimestamp != 0)
                                        return;
                                }
                            }
                        }
                        catch (Exception exception)
                            when (!IntegrityCanonical.IsFatal(exception))
                        {
                            _serverCharacterService?.CloseServerSession(rpc);
                            ServerManagerPlugin.Log.LogWarning(
                                "Prepared first-join character persistence failed: " +
                                exception);
                            SendServerRejection(
                                rpc,
                                new ProtocolRejection(
                                    ProtocolRejectCode.CharacterTransferFailed,
                                    "The server could not persist the new character " +
                                    "before opening the world.")
                                    .WithConnectionAudit("character", "initial_character_commit_failed",
                                        exception.GetType().Name + ": " + exception.Message, stage: "character_commit"));
                            return;
                        }
                    }

                    if (ReleaseWorld(server, rpc) &&
                        TryResolveActiveDetectionPeer(
                            server,
                            rpc,
                            out ServerPeerIdentity eventIdentity,
                            out _))
                    {
                        if (eventCharacterSession == null)
                        {
                            _serverCharacterService?.TryGetServerSession(
                                rpc,
                                out eventCharacterSession);
                        }

                        ServerEventDisplaySequences[rpc] = 1;
                        ServerEventRuntime.OnPlayerReady(
                            rpc,
                            eventIdentity,
                            eventCharacterSession,
                            firstJoin);
                        PlayerActivityRuntime.OnPlayerReady(
                            rpc,
                            eventIdentity,
                            eventCharacterSession);
                    }
                    break;
                }

            case ProtocolPacketKind.CharacterFragment:
                if (ServerDetectionStates.TryGetValue(rpc, out ServerDetectionState captureState) &&
                    captureState.BackupCaptureRequested)
                    HandleServerBackupCapture(server, rpc, packet, captureState);
                else
                    HandleServerCharacterFragment(server, rpc, packet);
                break;

            case ProtocolPacketKind.FinalSaveBegin:
                HandleServerFinalSaveBegin(server, rpc, packet);
                break;

            case ProtocolPacketKind.OperationalKickComplete:
                HandleServerOperationalKickComplete(server, rpc, packet);
                break;

            default:
                SendServerRejection(
                    rpc,
                    new ProtocolRejection(
                        ProtocolRejectCode.UnexpectedMessageType,
                        "The client sent an unexpected protocol message."));
                break;
        }
    }

    private static void HandleServerFinalSaveBegin(
        ZNet server,
        ZRpc rpc,
        ProtocolPacket packet)
    {
        if (packet.Sequence != ProtocolSequence.FinalSaveBegin ||
            !_coordinator.TryGetSnapshot(
                rpc,
                out ConnectionSessionSnapshot session) ||
            session.State != ConnectionSessionState.Ready ||
            !ProtocolByteUtil.FixedTimeEquals(
                session.SessionId,
                packet.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(
                session.Nonce,
                packet.Nonce))
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.InvalidTransition,
                    "The final-save request did not match a ready connection."));
            return;
        }

        if (!TryResolveActiveDetectionPeer(
                server,
                rpc,
                out _,
                out ProtocolRejection authenticationError))
        {
            SendServerRejection(rpc, authenticationError);
            return;
        }

        if (ServerDetectionStates.TryGetValue(
                rpc,
                out ServerDetectionState detectionState) &&
            detectionState.PendingKickTimestamp != 0)
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.CheatDetected,
                    ClientSecurityPolicyRejectionMessage));
            return;
        }

        if (_serverCharacterService == null ||
            !_serverCharacterService.TryGetServerSession(
                rpc,
                out CharacterSession? characterSession) ||
            characterSession == null)
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.InvalidTransition,
                    "No authoritative character session was available for final save."));
            return;
        }

        ServerFinalSaveDrains.TryGetValue(rpc, out ServerFinalSaveDrain? drain);
        if (drain?.OperationalKick == true &&
            Stopwatch.GetTimestamp() >= drain.DeadlineTimestamp)
        {
            CompleteOperationalKick(server, rpc, "timeout");
            return;
        }

        if (drain?.GateStarted == true)
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.DuplicateMessage,
                    "A final-save drain was already active."));
            return;
        }

        if (!WorldBuffers.TryGetValue(
                rpc,
                out BufferedWorldSocket buffer))
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.InvalidTransition,
                    "The server could not isolate the peer for final save."));
            return;
        }

        // A correct single-flight client submits every fragment synchronously
        // before FinalSaveBegin. Discard any partial assembly left by a forged
        // interleaving so it cannot commit in addition to the one gated save.
        _serverReassembler.RemovePeer(rpc);
        if (!buffer.TryRestrictOutboundForFinalSave())
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.InvalidTransition,
                    "The server could not isolate the peer for final save."));
            return;
        }

        if (drain == null)
        {
            drain = new ServerFinalSaveDrain(
                Stopwatch.GetTimestamp() + GracefulExitSaveTimeoutTicks);
            ServerFinalSaveDrains.Add(rpc, drain);
        }
        // An operational request owns its original five-second deadline. The
        // client's gate response may advance phase, never extend that deadline.
        drain.GateStarted = true;
        try
        {
            ZPackage ready = ProtocolPacketCodec.CreateFinalSaveReady(
                session.SessionId,
                session.Nonce,
                _connectionLimits);
            SendProtocolOrThrow(rpc, ready);
            ServerManagerPlugin.Log.LogInfo(
                "Started the outbound phase of an authenticated peer's " +
                "final-save barrier.");
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.CharacterTransferFailed,
                    "The server could not confirm the final-save gate."));
            ServerManagerPlugin.Log.LogWarning(
                "Final-save gate confirmation failed: " + exception.Message);
        }
    }

    private static void HandleServerOperationalKickComplete(
        ZNet server, ZRpc rpc, ProtocolPacket packet)
    {
        if (packet.Sequence != ProtocolSequence.OperationalKickComplete ||
            !_coordinator.TryGetSnapshot(rpc, out ConnectionSessionSnapshot session) ||
            session.State != ConnectionSessionState.Ready ||
            !ProtocolByteUtil.FixedTimeEquals(session.SessionId, packet.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(session.Nonce, packet.Nonce) ||
            !ServerFinalSaveDrains.TryGetValue(rpc, out ServerFinalSaveDrain drain) ||
            !drain.OperationalKick || !drain.GateStarted ||
            !drain.InboundBarrierCompleted || !drain.SaveAcknowledged)
        {
            SendServerRejection(rpc, new ProtocolRejection(
                ProtocolRejectCode.InvalidTransition,
                "No acknowledged operational kick save is awaiting completion."));
            return;
        }

        if (!TryResolveActiveDetectionPeer(server, rpc, out _, out ProtocolRejection error))
        {
            SendServerRejection(rpc, error);
            return;
        }

        if (ServerDetectionStates.TryGetValue(rpc, out ServerDetectionState detection) &&
            (detection.PendingKickTimestamp != 0 || detection.TerminalActionApplied))
        {
            SendServerRejection(rpc, new ProtocolRejection(
                ProtocolRejectCode.CheatDetected, ClientSecurityPolicyRejectionMessage));
            return;
        }

        CompleteOperationalKick(server, rpc,
            Stopwatch.GetTimestamp() >= drain.DeadlineTimestamp ? "timeout" : "ram_ack_confirmed");
    }

    private static void HandleServerBackupCapture(
        ZNet server, ZRpc rpc, ProtocolPacket packet, ServerDetectionState state)
    {
        if (!state.BackupOnly || state.TerminalActionApplied ||
            !_coordinator.TryGetSnapshot(rpc, out ConnectionSessionSnapshot connection) ||
            connection.State != ConnectionSessionState.ManifestValidated ||
            !connection.PeerInfoAuthenticated || !connection.ServerCharactersEnabled ||
            connection.CharacterTransferPrepared)
        {
            SendServerRejection(rpc, new ProtocolRejection(
                ProtocolRejectCode.InvalidTransition, "Local character capture was not requested."));
            return;
        }

        FragmentAcceptResult fragment = _serverReassembler.AcceptPackage(
            rpc, connection.SessionId, connection.Nonce, packet);
        if (fragment.Status == FragmentAcceptStatus.Rejected)
        {
            SendServerRejection(rpc, fragment.Rejection);
            return;
        }
        if (fragment.Status != FragmentAcceptStatus.Completed) return;
        // Exactly one raw profile is allowed in this phase. Subsequent fragments
        // must go through the ordinary readiness and save-envelope checks.
        state.BackupCaptureRequested = false;
        try
        {
            if (!ServerPeerResolver.TryResolvePeer(server, rpc, out ZNetPeer peer,
                    out ProtocolRejection resolutionError))
            {
                SendServerRejection(rpc, resolutionError);
                return;
            }
            CharacterSessionOpenResult opened = EnsureServerCharacterService()
                .OpenBackupServerSession(peer, fragment.Payload);
            GetConnectionRejectionMarker(rpc).InitialCharacterRequiresFresh = false;
            ServerEventRuntime.RecordCharacterObservations(opened.Snapshot.AccountId,
                opened.Snapshot.CharacterName, opened.Snapshot.Revision, "incoming",
                opened.AuditFindings.AuditObservations);
            state.BackupCaptureStatFindings = opened.StatLimitFindings;
            ServerManagerPlugin.Log.LogInfo(
                "Prepared backup-only local character; awaiting authenticated ReadyAck.");
            SendInitialCharacterTransfer(server, rpc, opened.NetworkPackage.GetArray());
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            _serverCharacterService?.CloseServerSession(rpc);
            SendServerRejection(rpc, new ProtocolRejection(
                ProtocolRejectCode.CharacterTransferFailed,
                "The server could not accept this local character. Contact the administrator.")
                .WithConnectionAudit("character", "backup_capture_rejected",
                    exception.GetType().Name + ": " + exception.Message, stage: "character_capture"));
        }
    }

    private static void HandleServerCharacterFragment(
        ZNet server,
        ZRpc rpc,
        ProtocolPacket packet)
    {
        if (!_coordinator.TryGetSnapshot(
                rpc,
                out ConnectionSessionSnapshot session) ||
            session.State != ConnectionSessionState.Ready)
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.InvalidTransition,
                    "Character save data arrived before connection readiness."));
            return;
        }

        if (ServerDetectionStates.TryGetValue(
                rpc,
                out ServerDetectionState detectionState) &&
            detectionState.PendingKickTimestamp != 0)
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.CheatDetected,
                    ClientSecurityPolicyRejectionMessage));
            return;
        }

        if (_serverCharacterService == null)
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.UnexpectedMessageType,
                    "The server character service is unavailable."));
            return;
        }

        ServerFinalSaveDrains.TryGetValue(rpc, out ServerFinalSaveDrain? finalDrain);
        // Check here as well as in Tick: a late fragment cannot beat the timeout
        // merely because its callback runs before expiry processing this frame.
        if (finalDrain?.OperationalKick == true &&
            Stopwatch.GetTimestamp() >= finalDrain.DeadlineTimestamp)
        {
            CompleteOperationalKick(server, rpc, "timeout");
            return;
        }

        if (finalDrain?.GateStarted == true && !finalDrain.InboundBarrierCompleted)
        {
            if (!WorldBuffers.TryGetValue(
                    rpc,
                    out BufferedWorldSocket buffer) ||
                !buffer.TryCompleteFinalSaveRestriction())
            {
                SendServerRejection(
                    rpc,
                    new ProtocolRejection(
                        ProtocolRejectCode.InvalidTransition,
                        "The server could not complete the final-save barrier."));
                return;
            }

            finalDrain.InboundBarrierCompleted = true;
        }

        FragmentAcceptResult fragment = _serverReassembler.AcceptPackage(
            rpc,
            session.SessionId,
            session.Nonce,
            packet);
        if (fragment.Status == FragmentAcceptStatus.Rejected)
        {
            SendServerRejection(rpc, fragment.Rejection);
            return;
        }

        if (fragment.Status != FragmentAcceptStatus.Completed)
        {
            return;
        }

        if (finalDrain?.OperationalKick == true &&
            Stopwatch.GetTimestamp() >= finalDrain.DeadlineTimestamp)
        {
            CompleteOperationalKick(server, rpc, "timeout");
            return;
        }

        long saveProcessingStarted = Stopwatch.GetTimestamp();
        _serverCharacterService.TryGetServerSession(
            rpc,
            out CharacterSession? trackedCharacterSession);
        string trackedSessionId = trackedCharacterSession == null
            ? "unknown"
            : trackedCharacterSession.SessionId.ToString("N").Substring(0, 8);
        string trackedIdentity = trackedCharacterSession == null
            ? "unknown"
            : trackedCharacterSession.Identity.AccountId + "/" +
              trackedCharacterSession.Identity.CharacterName;
        ServerManagerPlugin.Log.LogDebug(
            "CharacterShadowValidationStarted identity=" + trackedIdentity +
            ", session=" + trackedSessionId +
            ", baseRevision=" +
            (trackedCharacterSession?.CurrentRevision ?? -1L).ToString(
                CultureInfo.InvariantCulture) +
            ", envelopeBytes=" +
            fragment.Payload.Length.ToString(CultureInfo.InvariantCulture));

        CharacterSaveResult? saveResult = null;
        try
        {
            saveResult = _serverCharacterService.HandleSaveRequest(
                rpc,
                new ZPackage(fragment.Payload),
                requireFullProfile: finalDrain?.OperationalKick == true && finalDrain.GateStarted);
            if (saveResult.Accepted)
            {
                ServerManagerPlugin.Log.LogInfo(
                    "CharacterShadowAccepted identity=" +
                    saveResult.Response.AccountId + "/" +
                    saveResult.Response.CharacterName +
                    ", session=" + trackedSessionId +
                    ", baseRevision=" +
                    saveResult.Response.BaseRevision.ToString(
                        CultureInfo.InvariantCulture) +
                    ", revision=" +
                    saveResult.Response.Revision.ToString(
                        CultureInfo.InvariantCulture) +
                    ", envelopeBytes=" +
                    fragment.Payload.Length.ToString(
                        CultureInfo.InvariantCulture) +
                    ", durationMs=" +
                    FormatStopwatchElapsedMilliseconds(saveProcessingStarted));
            }

            SendCharacterPayload(
                rpc,
                session,
                saveResult.ResponsePackage.GetArray(),
                preferCompression: true);

            if (finalDrain?.GateStarted == true && saveResult.Accepted)
            {
                // Completion must follow a successfully submitted ACK of the
                // isolated final save, not an earlier in-flight heartbeat.
                finalDrain.SaveAcknowledged = true;
            }

            if (saveResult.Accepted)
            {
                ServerManagerPlugin.Log.LogDebug(
                    "CharacterShadowAckSent identity=" +
                    saveResult.Response.AccountId + "/" +
                    saveResult.Response.CharacterName +
                    ", session=" + trackedSessionId +
                    ", revision=" +
                    saveResult.Response.Revision.ToString(
                        CultureInfo.InvariantCulture));

                try
                {
                    PlayerActivityRuntime.OnCharacterShadowAccepted(
                        rpc,
                        saveResult);
                }
                catch (Exception exception)
                    when (!IntegrityCanonical.IsFatal(exception))
                {
                    // Activity logs are best-effort audit output. A formatting
                    // or queueing failure must never change an accepted
                    // character revision or its already-sent acknowledgement.
                    ServerManagerPlugin.Log.LogWarning(
                        "The accepted character state could not be added to " +
                        "the player activity log: " + exception.Message);
                }
            }

            ServerEventRuntime.RecordCharacterObservations(
                saveResult.Response.AccountId,
                saveResult.Response.CharacterName,
                saveResult.Response.Revision,
                "incoming",
                saveResult.AuditFindings.AuditObservations);

            // Admission and its acknowledgement are settled before optional
            // numeric-cap sanctions. Never reject or roll back this revision
            // merely because its finite stats exceed an administrator's cap.
            RecordCharacterStatLimits(
                rpc,
                saveResult.Response.AccountId,
                saveResult.Response.CharacterName,
                saveResult.StatLimitFindings,
                "snapshot_received",
                applyResponse: saveResult.Accepted);

            if (!saveResult.Accepted)
            {
                ServerEventRuntime.RecordCharacterSaveRejected(
                    saveResult.Response.AccountId,
                    saveResult.Response.CharacterName,
                    "snapshot_received",
                    saveResult);
                try
                {
                    PlayerActivityRuntime.OnCharacterSaveRejected(rpc, saveResult);
                }
                catch (Exception exception)
                    when (!IntegrityCanonical.IsFatal(exception))
                {
                    ServerManagerPlugin.Log.LogWarning(
                        "The rejected character state could not be added to " +
                        "the player activity log: " + exception.Message);
                }

                ServerManagerPlugin.Log.LogWarning(
                    "CharacterShadowRejected identity=" + trackedIdentity +
                    ", session=" + trackedSessionId +
                    ", durationMs=" +
                    FormatStopwatchElapsedMilliseconds(saveProcessingStarted) +
                    ", error=" +
                    SanitizeRemoteRejectMessage(saveResult.Error));
                TerminateRejectedCharacterSave(rpc);
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            string failurePhase = saveResult?.Accepted == true
                ? "Character RAM shadow was accepted, but acknowledgement " +
                  "delivery or post-acceptance handling failed"
                : "Character RAM-shadow validation failed";
            ServerManagerPlugin.Log.LogWarning(
                failurePhase +
                ": identity=" + trackedIdentity +
                ", session=" + trackedSessionId +
                ", durationMs=" +
                FormatStopwatchElapsedMilliseconds(saveProcessingStarted) +
                ", error=" + exception);
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.CharacterTransferFailed,
                    "The server could not accept the character save."));
        }
    }

    private static void TerminateRejectedCharacterSave(ZRpc rpc)
    {
        ProtocolRejection terminalRejection = new(
            ProtocolRejectCode.CharacterTransferFailed,
            "The character save was rejected; reconnect is required.");
        try
        {
            _coordinator.RejectSession(rpc, terminalRejection);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogDebug(
                "Could not mark a rejected character save terminal: " +
                exception.Message);
        }

        try
        {
            _serverReassembler.RemovePeer(rpc);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogDebug(
                "Could not clear rejected character fragments: " +
                exception.Message);
        }

        try
        {
            _serverCharacterService?.CloseServerSession(rpc);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogDebug(
                "Could not close a rejected character session: " +
                exception.Message);
        }

        if (WorldBuffers.TryGetValue(rpc, out BufferedWorldSocket buffer))
        {
            buffer.Discard();
        }

        // Keep the short delivery grace period for the authoritative SaveRejected
        // response, but no protocol, character session, or save admission remains live.
        ScheduleDisconnect(rpc, milliseconds: 500);
    }

    private static void HandleClientPacket(ZRpc rpc, ZPackage package)
    {
        ClientConnection session = GetOrCreateClient(rpc);
        if (!ProtocolPacketCodec.TryDecode(
                package,
                _connectionLimits,
                out ProtocolPacket packet,
                out ProtocolRejection rejection))
        {
            FailClient(rpc, rejection.SafeMessage, playerMessage: PlayerConnectionMessages.FromRejection(rejection));
            return;
        }

        if (packet.Kind == ProtocolPacketKind.Reject)
        {
            FailClient(rpc, DecodeRejectMessage(packet), playerMessage: DecodePlayerRejectMessage(packet));
            return;
        }

        switch (packet.Kind)
        {
            case ProtocolPacketKind.PolicyUpdate:
                HandleClientPolicyUpdate(session, packet);
                break;

            case ProtocolPacketKind.Challenge:
                HandleClientChallenge(session, packet);
                break;

            case ProtocolPacketKind.ManifestAccepted:
                HandleClientManifestAccepted(session, packet);
                break;

            case ProtocolPacketKind.BackupCaptureRequest:
                HandleClientBackupCaptureRequest(session, packet);
                break;

            case ProtocolPacketKind.BackupCaptureCommitted:
                HandleClientBackupCaptureCommitted(session, packet);
                break;

            case ProtocolPacketKind.CharacterFragment:
                HandleClientCharacterFragment(session, packet);
                break;

            case ProtocolPacketKind.FinalSaveReady:
                HandleClientFinalSaveReady(session, packet);
                break;

            case ProtocolPacketKind.OperationalKickRequest:
                HandleClientOperationalKickRequest(session, packet);
                break;

            default:
                FailClient(rpc, "The server sent an unexpected protocol message.");
                break;
        }
    }

    private static void HandleClientOperationalKickRequest(
        ClientConnection session, ProtocolPacket packet)
    {
        if (!session.ServerCharacterActive || !session.ReadyAcknowledgementSent ||
            session.Failed || session.SessionId == null || session.Nonce == null ||
            packet.Sequence != ProtocolSequence.OperationalKickRequest ||
            !ProtocolByteUtil.FixedTimeEquals(session.SessionId, packet.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(session.Nonce, packet.Nonce))
        {
            FailClient(session.Rpc, "The server sent an invalid operational kick request.");
            return;
        }

        if (_deferredClientExit != null)
        {
            // Simultaneous local logout/app quit reuses the same capture and ACK.
            // Repeated requests never restart either exit's deadline.
            _deferredClientExit.OperationalKickRequested = true;
            return;
        }

        DeferredClientExit requested = new(
            DeferredClientExitKind.OperationalKick,
            AddStopwatchDuration(Stopwatch.GetTimestamp(), OperationalKickSaveTimeoutTicks),
            Game.instance, save: false, shouldExit: false, changeToStartScene: true)
        {
            OperationalKickRequested = true
        };
        if (!TryBeginDeferredClientExit(requested))
        {
            // The server's independent deadline still disconnects an unavailable
            // client; no new local profile or alternate recovery upload is used.
            ServerManagerPlugin.Log.LogWarning(
                "Operational kick could not enter final-save mode; retaining the last server revision.");
        }
    }

    private static void HandleClientFinalSaveReady(
        ClientConnection session,
        ProtocolPacket packet)
    {
        DeferredClientExit? deferred = _deferredClientExit;
        if (deferred == null ||
            !deferred.ClientWorldBarrierSent ||
            deferred.ServerGateAcknowledged ||
            deferred.FinalCaptureAttempted ||
            session.SavePipeline.HasInFlight ||
            packet.Sequence != ProtocolSequence.FinalSaveReady ||
            session.SessionId == null ||
            session.Nonce == null ||
            !ProtocolByteUtil.FixedTimeEquals(
                session.SessionId,
                packet.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(
                session.Nonce,
                packet.Nonce))
        {
            FailClient(
                session.Rpc,
                "The server sent an invalid final-save gate acknowledgement.");
            return;
        }

        deferred.ServerGateAcknowledged = true;
    }

    private static void HandleClientChallenge(
        ClientConnection session,
        ProtocolPacket packet)
    {
        if (session.ChallengeReceived ||
            packet.Sequence != ProtocolSequence.Challenge)
        {
            FailClient(session.Rpc, "The server sent an invalid duplicate challenge.");
            return;
        }

        if (!ProtocolPacketCodec.TryDecodeChallengeOptions(
                packet,
                out ProtocolChallengeOptions challengeOptions,
                out ProtocolRejection challengeRejection))
        {
            FailClient(session.Rpc, challengeRejection.SafeMessage, playerMessage: PlayerConnectionMessages.FromRejection(challengeRejection));
            return;
        }

        session.ChallengeReceived = true;
        session.SessionId = ProtocolByteUtil.Clone(packet.SessionId);
        session.Nonce = ProtocolByteUtil.Clone(packet.Nonce);

        try
        {
            ClientDetection.Configure(challengeOptions);
            CheatCommandGuard.Configure(
                challengeOptions.MonitorCheatCommands,
                challengeOptions.BlockCheatCommands,
                challengeOptions.AllowAdminCheatCommands);

            if (challengeOptions.EnforceManifest)
            {
                session.ManifestResponseLimits = new IntegrityLimits(
                    maxPayloadBytes: challengeOptions.MaximumManifestBytes);
                session.ManifestPreparation ??= PluginManifestScanner.BeginCurrent(_integrityLimits);
                ProcessClientManifestPreparation(session);
            }
            else
            {
                CancelClientManifestPreparation(session);
                SendClientManifestResponse(session, Array.Empty<byte>());
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            FailClient(
                session.Rpc,
                "The local plugin manifest could not be prepared.",
                exception);
        }
    }

    private static void ProcessClientManifestPreparation(ClientConnection session)
    {
        if (!ReferenceEquals(_client, session) || session.Failed ||
            !session.ChallengeReceived || session.ManifestResponseSent ||
            session.ManifestResponseLimits == null || session.ManifestPreparation == null)
            return;

        try
        {
            if (Stopwatch.GetTimestamp() > session.DeadlineTimestamp)
            {
                FailClient(session.Rpc, "The ServerManager connection handshake timed out.");
                return;
            }
            if (session.Rpc.GetSocket()?.IsConnected() != true)
            {
                CancelClientManifestPreparation(session);
                return;
            }

            PluginManifestScanner.Preparation preparation = session.ManifestPreparation;
            if (!preparation.TryGetResult(out IntegrityManifestBuildResult build)) return;
            IntegrityManifest? manifest = build.Manifest;
            if (!build.Success || manifest == null)
                throw new InvalidDataException("The local plugin manifest could not be built (" +
                    string.Join(", ", build.Diagnostics.Select(item => item.Code).Distinct().Take(4)) + ").");
            if (!preparation.MatchesCurrentPlugins())
                throw new InvalidDataException("The loaded plugin list changed during manifest preparation; reconnect is required.");

            // Only the hash work is speculative. The real server-advertised
            // payload limit, session id and nonce always apply to this response.
            IntegrityManifestEncodeResult encoded = IntegrityManifestCodec.TryEncode(
                manifest, session.ManifestResponseLimits);
            if (!encoded.Success || encoded.Payload == null)
                throw new InvalidDataException("The local plugin manifest could not be encoded (" +
                    string.Join(", ", encoded.Diagnostics.Select(item => item.Code).Distinct().Take(4)) + ").");

            long elapsed = preparation.ElapsedMilliseconds;
            session.ManifestPreparation = null;
            preparation.Dispose();
            SendClientManifestResponse(session, encoded.Payload);
            ServerManagerPlugin.Log.LogInfo(
                "Client manifest prepared asynchronously: " + manifest.Entries.Count +
                " plugin records, " + elapsed.ToString(CultureInfo.InvariantCulture) + " ms (including worker queue).");
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            FailClient(session.Rpc, "The local plugin manifest could not be prepared.", exception);
        }
    }

    private static void SendClientManifestResponse(ClientConnection session, byte[] manifestPayload)
    {
        if (!ReferenceEquals(_client, session) || session.Failed || session.ManifestResponseSent)
            return;
        ZPackage response = ProtocolPacketCodec.CreateManifestResponse(
            session.SessionId!, session.Nonce!, manifestPayload, _connectionLimits);
        SendProtocolOrThrow(session.Rpc, response);
        session.ManifestResponseSent = true;
    }

    private static void HandleClientManifestAccepted(
        ClientConnection session,
        ProtocolPacket packet)
    {
        if (!session.ChallengeReceived || !session.ManifestResponseSent ||
            session.ManifestAccepted ||
            packet.Sequence != ProtocolSequence.ManifestAccepted ||
            session.SessionId == null ||
            session.Nonce == null ||
            !ProtocolByteUtil.FixedTimeEquals(session.SessionId, packet.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(session.Nonce, packet.Nonce))
        {
            FailClient(session.Rpc, "The manifest acknowledgement was invalid.");
            return;
        }

        session.ManifestAccepted = true;
        if (session.HeldPassword == null)
        {
            return;
        }

        string password = session.HeldPassword;
        session.HeldPassword = null;
        try
        {
            ZNet? znet = ZNet.instance;
            if (znet == null || znet.IsServer())
            {
                throw new InvalidOperationException(
                    "The client network instance is unavailable.");
            }

            ValheimPrivateAccess.SendPeerInfo(znet, session.Rpc, password);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            FailClient(
                session.Rpc,
                "Vanilla peer authentication could not be sent.",
                exception);
        }
    }

    private static void HandleClientBackupCaptureRequest(ClientConnection session, ProtocolPacket packet)
    {
        if (!session.ManifestAccepted || !session.PeerInfoSent || session.BackupOnly ||
            session.InitialTransferCompleted || session.Failed ||
            packet.Sequence != ProtocolSequence.BackupCaptureRequest ||
            !ProtocolByteUtil.FixedTimeEquals(packet.SessionId, session.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(packet.Nonce, session.Nonce))
        {
            FailClient(session.Rpc, "The local character capture request was out of order.");
            return;
        }
        try
        {
            EnsureCharacterCodecs();
            PlayerProfile selected = Game.instance?.GetPlayerProfile() ??
                throw new CharacterProtocolException("The selected local character is unavailable.");
            if (Player.m_localPlayer != null)
                throw new CharacterProtocolException("Local character capture must precede world entry.");
            byte[] payload = _profileCodec!.SerializeProfileToBytes(selected);
            if (payload.Length > _clientCharacterOptions.MaxPayloadBytes)
                throw new CharacterProtocolException("The selected local character exceeds the size limit.");
            using var hash = System.Security.Cryptography.SHA256.Create();
            session.BackupCaptureHash = hash.ComputeHash(payload);
            session.BackupOnly = true;
            SendPlan(session.Rpc, BoundedFragmentCodec.CreateCharacterTransfer(
                session.SessionId!, session.Nonce!, payload, preferCompression: true,
                _connectionLimits, _fragmentLimits));
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            FailClient(session.Rpc, "The selected local character could not be captured.", exception);
        }
    }

    private static void HandleClientBackupCaptureCommitted(ClientConnection session, ProtocolPacket packet)
    {
        if (!session.BackupOnly || session.BackupCaptureCommitted ||
            !session.InitialTransferCompleted || !session.ReadyAcknowledgementSent || session.Failed ||
            packet.Sequence != ProtocolSequence.BackupCaptureCommitted ||
            !ProtocolByteUtil.FixedTimeEquals(packet.SessionId, session.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(packet.Nonce, session.Nonce))
        {
            FailClient(session.Rpc, "The local character capture confirmation was out of order.");
            return;
        }
        session.BackupCaptureCommitted = true;
    }

    private static void HandleClientCharacterFragment(
        ClientConnection session,
        ProtocolPacket packet)
    {
        if (!session.ManifestAccepted ||
            session.SessionId == null ||
            session.Nonce == null)
        {
            FailClient(
                session.Rpc,
                "Character data arrived before manifest validation.");
            return;
        }

        FragmentAcceptResult fragment = _clientReassembler.AcceptPackage(
            session.Rpc,
            session.SessionId,
            session.Nonce,
            packet);
        if (fragment.Status == FragmentAcceptStatus.Rejected)
        {
            FailClient(session.Rpc, fragment.Rejection.SafeMessage, playerMessage: PlayerConnectionMessages.FromRejection(fragment.Rejection));
            return;
        }

        if (fragment.Status != FragmentAcceptStatus.Completed)
        {
            return;
        }

        if (!session.InitialTransferCompleted)
        {
            ApplyInitialServerCharacter(session, fragment);
            return;
        }

        ApplySaveResponse(session, fragment.Payload);
    }

    private static void ApplyInitialServerCharacter(
        ClientConnection session,
        FragmentAcceptResult fragment)
    {
        try
        {
            if (fragment.Payload.Length > 0)
            {
                EnsureCharacterCodecs();
                CharacterEnvelope envelope =
                    _characterEnvelopeCodec!.Decode(fragment.Payload);
                if (envelope.Kind != CharacterEnvelopeKind.Snapshot)
                {
                    throw new CharacterProtocolException(
                        "The initial character transfer was not a snapshot.");
                }

                Game? game = Game.instance;
                PlayerProfile? selected = game?.GetPlayerProfile();
                if (game == null || selected == null)
                {
                    throw new CharacterProtocolException(
                        "The selected local profile is unavailable.");
                }

                CharacterIdentity identity = new(
                    envelope.AccountId,
                    envelope.CharacterName);
                byte[] authoritativePayload = envelope.GetPayloadCopy();
                if (session.BackupOnly)
                {
                    using var captureHash = System.Security.Cryptography.SHA256.Create();
                    if (session.BackupCaptureHash == null ||
                        !ProtocolByteUtil.FixedTimeEquals(session.BackupCaptureHash,
                            captureHash.ComputeHash(authoritativePayload)))
                        throw new CharacterProtocolException(
                            "The initial backup response differs from the selected local character.");
                    session.BackupCaptureHash = null;
                }
                // Preserve vanilla local/cloud saving independently of server
                // acknowledgement. Only the server's own state uses its ACKs.
                PlayerProfile managed =
                    _profileCodec!.DeserializeProfileFromBytes(
                        authoritativePayload,
                        selected.GetFilename(),
                        selected.m_fileSource);
                if (!string.Equals(
                        managed.GetName(),
                        selected.GetName(),
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        managed.GetName(),
                        identity.CharacterName,
                        StringComparison.Ordinal))
                {
                    throw new CharacterProtocolException(
                        "The server character name does not match the selected profile.");
                }

                if (!session.BackupOnly && LocalCharacterFirstJoinGuard.ShouldRejectUsedLocalFirstJoin(
                        envelope,
                        ValheimPrivateAccess.GetWorldDataCount(selected) > 0,
                        ValheimPrivateAccess.GetPlayerData(managed) != null))
                {
                    ReportClientFreshCharacterRejection(session, fragment.MessageId);
                    FailClient(
                        session.Rpc,
                        "This server has no saved character for the selected " +
                        "account and name. The selected local character has " +
                        "existing world progress, so ServerManager did not " +
                        "overwrite it. Return to the lobby and create a new " +
                        "character with a different name, or ask the server " +
                        "administrator to place this character's .fch in the matching SteamID subfolder under the server characters folder while the server is stopped.",
                        playerMessage: PlayerLocalizer.Text("sm_fresh_character_required"));
                    return;
                }

                if (!session.BackupOnly && _profileCodec!.PreserveInitialAppearance(envelope, selected, managed))
                {
                    // Capture after Player.Load/spawn through the existing full-save
                    // pipeline. ACK/base bytes remain the actual server snapshot.
                    session.FullProfileSafetySaveDueTimestamp = Stopwatch.GetTimestamp();
                }
                session.OriginalProfile = selected;
                session.ManagedProfile = managed;
                session.CharacterState = new CharacterClientState(
                    identity,
                    envelope.SessionId,
                    envelope.Revision);
                session.AcknowledgedProfileBytes = authoritativePayload;
                session.AcknowledgedProfileRevision = envelope.Revision;
                session.ServerCharacterActive = true;
                ValheimPrivateAccess.SetGamePlayerProfile(game, managed);
                session.InventoryFastPathReady =
                    ValheimPrivateAccess.GetPlayerData(managed) != null &&
                    !envelope.RequiresFreshLocalCharacter;
            }

            session.InitialTransferCompleted = true;
            ZPackage ready = ProtocolPacketCodec.CreateReadyAck(
                session.SessionId!,
                session.Nonce!,
                fragment.MessageId,
                _connectionLimits);
            SendProtocolOrThrow(session.Rpc, ready);
            session.ReadyAcknowledgementSent = true;
            ServerManagerPlugin.Log.LogInfo(
                "Initial server character transfer applied; Ready acknowledgement sent.");
            if (session.ServerCharacterActive)
            {
                session.NextFullProfileHeartbeatTimestamp =
                    CreateInitialFullProfileHeartbeatDeadline(
                        session,
                        Stopwatch.GetTimestamp());
            }

        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            RestoreClientProfile();
            FailClient(
                session.Rpc,
                "The authoritative server character could not be applied.",
                exception, PlayerLocalizer.Text("sm_character_apply_failed"));
        }
    }

    private static void ApplySaveResponse(
        ClientConnection session,
        byte[] payload)
    {
        if (!session.ServerCharacterActive ||
            session.CharacterState == null ||
            !session.SavePipeline.HasInFlight)
        {
            FailClient(
                session.Rpc,
                "An unsolicited character save response was received.");
            return;
        }

        try
        {
            CharacterEnvelope response =
                _characterEnvelopeCodec!.Decode(payload);
            CharacterClientState state = session.CharacterState;
            if (response.SessionId != state.SessionId ||
                !string.Equals(
                    response.AccountId,
                    state.Identity.AccountId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    response.CharacterName,
                    state.Identity.CharacterName,
                    StringComparison.Ordinal))
            {
                throw new CharacterProtocolException(
                    "The character save response identity was invalid.");
            }

            if (response.Kind == CharacterEnvelopeKind.SaveRejected)
            {
                throw new CharacterProtocolException(
                    "The server rejected the character save; reconnect is required.");
            }

            if (response.Kind != CharacterEnvelopeKind.SaveAccepted)
            {
                throw new CharacterProtocolException(
                    "The character save response kind was invalid.");
            }

            if (!session.SavePipeline.TryCompleteAcknowledgement(
                    response.BaseRevision,
                    response.Revision,
                    out byte[] acknowledgedPayloadBytes,
                    out ClientCharacterSaveReason acknowledgedReason,
                    out string acknowledgementError))
            {
                throw new CharacterProtocolException(acknowledgementError);
            }

            byte[] acknowledgedFullProfileBytes;
            EnsureCharacterCodecs();
            if (acknowledgedReason ==
                ClientCharacterSaveReason.InventoryDirty)
            {
                if (!session.InventoryFastPathReady ||
                    session.AcknowledgedProfileBytes == null ||
                    session.AcknowledgedProfileRevision !=
                        response.BaseRevision)
                {
                    throw new CharacterProtocolException(
                        "The acknowledged inventory update has no matching " +
                        "full-profile base.");
                }

                acknowledgedFullProfileBytes =
                    _profileCodec!.ReplaceInventorySnapshot(
                        state.Identity,
                        session.AcknowledgedProfileBytes,
                        acknowledgedPayloadBytes,
                        out _);
            }
            else
            {
                acknowledgedFullProfileBytes = acknowledgedPayloadBytes;
            }

            if (state.Revision != response.BaseRevision ||
                !state.TryAcceptRevision(
                    response.BaseRevision,
                    response.Revision))
            {
                throw new CharacterProtocolException(
                    "The character save acknowledgement revision was invalid.");
            }

            session.AcknowledgedProfileBytes = acknowledgedFullProfileBytes;
            session.AcknowledgedProfileRevision = response.Revision;
            session.InventoryFastPathReady = true;
            // ACKs advance only the server-approved base. A newer local save
            // may already exist, so never write an older capture into its slot.
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            FailClient(
                session.Rpc,
                "The server did not accept the character save.",
                exception, PlayerLocalizer.Text("sm_character_save_failed"));
        }
    }


    private static ulong OfferClientSave(
        ClientConnection session,
        byte[] payloadBytes,
        ClientCharacterSaveReason reason)
    {
        CharacterClientState state =
            session.CharacterState ??
            throw new InvalidOperationException(
                "No client character session is active.");
        PlayerProfile managedProfile = session.ManagedProfile ??
            throw new InvalidOperationException(
                "No managed client profile is active.");
        if (!string.Equals(
                managedProfile.GetName(),
                state.Identity.CharacterName,
                StringComparison.Ordinal))
        {
            throw new CharacterProtocolException(
                "The managed client profile name does not match the server " +
                "character session.");
        }

        return session.SavePipeline.Offer(
            payloadBytes,
            reason);
    }

    private static void ProcessClientCharacterSavePipeline(
        ClientConnection session)
    {
        if (session.Failed ||
            !session.ServerCharacterActive ||
            !session.ReadyAcknowledgementSent ||
            session.CharacterState == null ||
            session.ManagedProfile == null ||
            session.SavePipeline.Closed)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (session.SavePipeline.IsAcknowledgementOverdue(now))
        {
            session.SavePipeline.Close();
            if (_deferredClientExit != null)
            {
                ResumeDeferredClientExit(
                    succeeded: false,
                    "The final character save acknowledgement timed out.");
            }
            else
            {
                FailClient(
                    session.Rpc,
                    "The server did not acknowledge the character save in time.",
                    playerMessage: PlayerLocalizer.Text("sm_character_save_failed"));
            }

            return;
        }

        bool fullProfileFallbackDue =
            session.FullProfileSafetySaveDueTimestamp != 0 &&
            now >= session.FullProfileSafetySaveDueTimestamp;
        bool fullProfileHeartbeatDue =
            session.NextFullProfileHeartbeatTimestamp != 0 &&
            now >= session.NextFullProfileHeartbeatTimestamp;
        if ((fullProfileFallbackDue || fullProfileHeartbeatDue) &&
            _deferredClientExit == null)
        {
            try
            {
                Player? player = Player.m_localPlayer;
                Game? game = Game.instance;
                if (player != null &&
                    game != null &&
                    ReferenceEquals(
                        ValheimPrivateAccess.GetGamePlayerProfile(game),
                        session.ManagedProfile))
                {
                    EnsureCharacterCodecs();
                    Minimap? minimap = Minimap.instance;
                    minimap?.SaveMapData();
                    byte[] profileBytes = _profileCodec!.CaptureProfileToBytes(
                        session.ManagedProfile,
                        player);
                    OfferClientSave(
                        session,
                        profileBytes,
                        ClientCharacterSaveReason.PeriodicFull);
                    session.FullProfileSafetySaveDueTimestamp = 0;
                    session.NextFullProfileSafetySaveAllowedTimestamp =
                        AddStopwatchDuration(
                            now,
                            FullProfileSafetySaveMinimumIntervalTicks);
                    if (fullProfileHeartbeatDue)
                    {
                        session.NextFullProfileHeartbeatTimestamp =
                            AdvanceFullProfileHeartbeatDeadline(
                                session.NextFullProfileHeartbeatTimestamp,
                                now);
                    }

                    session.InventoryFastSaveDueTimestamp = 0;
                    session.NextInventoryFastSaveAllowedTimestamp =
                        AddStopwatchDuration(
                            now,
                            InventoryFastSaveMinimumIntervalTicks);
                }
            }
            catch (Exception exception)
                when (!IntegrityCanonical.IsFatal(exception))
            {
                session.SavePipeline.Close();
                FailClient(
                    session.Rpc,
                    "The periodic server-managed character snapshot could not be saved.",
                    exception);
                return;
            }
        }

        if (session.InventoryFastPathReady &&
            session.InventoryFastSaveDueTimestamp != 0 &&
            now >= session.InventoryFastSaveDueTimestamp &&
            !session.SavePipeline.HasPendingFullProfile &&
            _deferredClientExit == null)
        {
            try
            {
                Player? player = Player.m_localPlayer;
                Game? game = Game.instance;
                if (player != null &&
                    game != null &&
                    ReferenceEquals(
                        ValheimPrivateAccess.GetGamePlayerProfile(game),
                        session.ManagedProfile))
                {
                    EnsureCharacterCodecs();
                    byte[] inventoryBytes;
                    try
                    {
                        inventoryBytes =
                            _profileCodec!.CaptureInventoryToBytes(
                                player.GetInventory());
                    }
                    catch (CharacterProtocolException exception)
                    {
                        // A modded inventory can legitimately exceed the
                        // dedicated 1 MiB fast-path bound. Keep the connection
                        // alive and force the ordinary bounded full-profile
                        // safety path, which remains authoritative.
                        session.FullProfileSafetySaveDueTimestamp =
                            session.NextFullProfileSafetySaveAllowedTimestamp >
                            now
                                ? session.NextFullProfileSafetySaveAllowedTimestamp
                                : now;
                        ServerManagerPlugin.Log.LogWarning(
                            "Inventory fast-path capture was unavailable; " +
                            "falling back to a full character snapshot: " +
                            exception.Message);
                        return;
                    }

                    OfferClientSave(
                        session,
                        inventoryBytes,
                        ClientCharacterSaveReason.InventoryDirty);
                    session.InventoryFastSaveDueTimestamp = 0;
                    session.NextInventoryFastSaveAllowedTimestamp =
                        AddStopwatchDuration(
                            now,
                            InventoryFastSaveMinimumIntervalTicks);
                }
            }
            catch (Exception exception)
                when (!IntegrityCanonical.IsFatal(exception))
            {
                session.SavePipeline.Close();
                FailClient(
                    session.Rpc,
                    "The changed server-managed inventory could not be saved.",
                    exception);
                return;
            }
        }

        if (_deferredClientExit != null &&
            _deferredClientExit.TargetCaptureId == 0)
        {
            return;
        }

        try
        {
            if (session.SavePipeline.TryStartNext(
                    session.CharacterState.Revision,
                    now,
                    out ClientCharacterSaveDispatch? dispatch) &&
                dispatch != null)
            {
                SendClientSave(session, dispatch);
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // A transport failure after any fragment was submitted is
            // ambiguous: the server may have accepted the shadow while the ACK
            // was lost.
            // Never retry in this session; reconnect to reconcile revision.
            session.SavePipeline.Close();
            if (_deferredClientExit != null)
            {
                ResumeDeferredClientExit(
                    succeeded: false,
                    "The final character save could not be submitted.");
            }
            else
            {
                FailClient(
                    session.Rpc,
                    "The server-managed character save could not be submitted.",
                    exception);
            }
        }
    }

    private static void SendClientSave(
        ClientConnection session,
        ClientCharacterSaveDispatch dispatch)
    {
        CharacterClientState state =
            session.CharacterState ??
            throw new InvalidOperationException("No client character session is active.");
        if (session.SessionId == null || session.Nonce == null)
        {
            throw new InvalidOperationException("No connection protocol session is active.");
        }

        if (state.Revision != dispatch.BaseRevision ||
            dispatch.Revision != dispatch.BaseRevision + 1)
        {
            throw new CharacterProtocolException(
                "The character save dispatch does not match the acknowledged revision.");
        }

        CharacterEnvelopeKind requestKind =
            dispatch.Reason == ClientCharacterSaveReason.InventoryDirty
                ? CharacterEnvelopeKind.InventorySaveRequest
                : CharacterEnvelopeKind.SaveRequest;
        CharacterEnvelope request = CharacterEnvelope.Create(
            requestKind,
            dispatch.Revision,
            dispatch.BaseRevision,
            state.SessionId,
            state.Identity,
            DateTime.UtcNow,
            ValheimPlayerProfileCodec.SupportedPlayerProfileVersion,
            dispatch.PayloadBytes);
        byte[] encoded = _characterEnvelopeCodec!.Encode(request);
        CharacterTransferPlan plan = BoundedFragmentCodec.CreateCharacterTransfer(
            session.SessionId,
            session.Nonce,
            encoded,
            preferCompression: true,
            _connectionLimits,
            _fragmentLimits);
        SendPlan(session.Rpc, plan);
    }

    private static void BeginClientGameplayQuiescence(Player player)
    {
        if (_clientGameplayQuiescence != null)
        {
            throw new InvalidOperationException(
                "The local player is already quiesced for final save.");
        }

        InventoryGui? inventoryGui = InventoryGui.instance;
        if (inventoryGui != null)
        {
            // Resolve an in-progress drag and release any open container before
            // the transport barrier is placed.
            inventoryGui.Hide();
        }

        StoreGui? storeGui = StoreGui.instance;
        if (storeGui != null)
        {
            storeGui.Hide();
        }

        ZSyncTransform? syncTransform =
            player.GetComponent<ZSyncTransform>();
        UnityEngine.Rigidbody? body =
            player.GetComponent<UnityEngine.Rigidbody>();
        ClientGameplayQuiescence quiescence = new(
            player,
            player.enabled,
            syncTransform,
            syncTransform != null && syncTransform.enabled,
            body,
            body?.constraints ??
                UnityEngine.RigidbodyConstraints.None,
            body?.linearVelocity ?? UnityEngine.Vector3.zero,
            body?.angularVelocity ?? UnityEngine.Vector3.zero);
        _clientGameplayQuiescence = quiescence;
        try
        {
            player.enabled = false;
            if (body != null)
            {
                body.constraints =
                    UnityEngine.RigidbodyConstraints.FreezeAll;
                body.linearVelocity = UnityEngine.Vector3.zero;
                body.angularVelocity = UnityEngine.Vector3.zero;
                body.Sleep();
            }

            if (syncTransform != null && syncTransform.enabled)
            {
                // Put the frozen transform into its ZDO before the bounded
                // client-world drain, then prevent later owner sync from
                // crossing the final-save marker.
                syncTransform.SyncNow();
                syncTransform.enabled = false;
            }
        }
        catch
        {
            ReleaseClientGameplayQuiescence(restorePlayer: true);
            throw;
        }
    }

    private static bool TryFlushClientWorldMutations()
    {
        ZDOMan? zdoMan = ZDOMan.instance;
        if (zdoMan == null)
        {
            throw new InvalidOperationException(
                "The client world replication manager is unavailable.");
        }

        ValheimPrivateAccess.FlushClientObjects(zdoMan);
        ValheimPrivateAccess.SendDestroyed(zdoMan);
        return zdoMan.GetClientChangeQueue() == 0 &&
               ValheimPrivateAccess.GetDestroySendCount(zdoMan) == 0;
    }

    private static bool TrySendFinalSaveBegin(
        ClientConnection session,
        DeferredClientExit deferred)
    {
        if (deferred.ClientWorldBarrierSent)
        {
            return true;
        }

        if (!TryFlushClientWorldMutations())
        {
            return false;
        }

        if (session.SessionId == null || session.Nonce == null)
        {
            throw new InvalidOperationException(
                "No connection protocol session is active.");
        }

        ZPackage begin = ProtocolPacketCodec.CreateFinalSaveBegin(
            session.SessionId,
            session.Nonce,
            _connectionLimits);
        SendProtocolOrThrow(session.Rpc, begin);
        deferred.ClientWorldBarrierSent = true;
        ServerManagerPlugin.Log.LogInfo(
            "Waiting for the server to isolate this peer for final save.");
        return true;
    }

    private static byte[] CaptureDeferredClientExitSnapshot(
        ClientConnection session,
        Player player)
    {
        ClientGameplayQuiescence? quiescence =
            _clientGameplayQuiescence;
        if (quiescence == null ||
            !ReferenceEquals(quiescence.Player, player) ||
            player.enabled ||
            (quiescence.SyncTransformWasEnabled &&
             (quiescence.SyncTransform == null ||
              quiescence.SyncTransform.enabled)) ||
            (quiescence.Body != null &&
             quiescence.Body.constraints !=
             UnityEngine.RigidbodyConstraints.FreezeAll))
        {
            throw new InvalidOperationException(
                "The local player is not quiesced for final save.");
        }

        PlayerProfile profile =
            session.ManagedProfile ??
            throw new InvalidOperationException(
                "No managed player profile is active.");
        EnsureCharacterCodecs();

        // Ready is ordered after every server gameplay message that preceded
        // the final gate. Capture only now so damage, teleport, and other
        // authoritative inbound state are included.
        profile.SavePlayerData(player);
        Minimap? minimap = Minimap.instance;
        minimap?.SaveMapData();
        profile.SaveLogoutPoint();

        byte[] profileBytes =
            _profileCodec!.SerializeProfileToBytes(profile);
        return profileBytes;
    }

    private static void ReleaseClientGameplayQuiescence(bool restorePlayer)
    {
        ClientGameplayQuiescence? quiescence =
            _clientGameplayQuiescence;
        _clientGameplayQuiescence = null;
        if (!restorePlayer || quiescence == null)
        {
            return;
        }

        UnityEngine.Rigidbody? body = quiescence.Body;
        try
        {
            if (body != null)
            {
                body.constraints = quiescence.BodyConstraints;
                body.linearVelocity = quiescence.BodyVelocity;
                body.angularVelocity = quiescence.BodyAngularVelocity;
                body.WakeUp();
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "Could not restore local player physics after final-save " +
                "quiescence: " +
                exception.Message);
        }

        ZSyncTransform? syncTransform =
            quiescence.SyncTransform;
        try
        {
            if (syncTransform != null)
            {
                syncTransform.enabled =
                    quiescence.SyncTransformWasEnabled;
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "Could not restore local transform synchronization after " +
                "final-save quiescence: " +
                exception.Message);
        }

        Player player = quiescence.Player;
        try
        {
            if (player != null)
            {
                player.enabled = quiescence.PlayerWasEnabled;
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "Could not restore local player updates after final-save " +
                "quiescence: " +
                exception.Message);
        }
    }

    private static bool TryBeginDeferredClientExit(
        DeferredClientExit requested)
    {
        if (_deferredClientExit != null)
        {
            return true;
        }

        ClientConnection? session = _client;
        Game? game = requested.Game ?? Game.instance;
        Player? player = Player.m_localPlayer;
        ZNet? znet = ZNet.instance;
        if (session == null ||
            session.Failed ||
            !session.ServerCharacterActive ||
            !session.ReadyAcknowledgementSent ||
            session.CharacterState == null ||
            session.ManagedProfile == null ||
            session.SavePipeline.Closed ||
            znet == null ||
            znet.IsServer() ||
            ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected ||
            game == null ||
            player == null ||
            !ReferenceEquals(
                ValheimPrivateAccess.GetGamePlayerProfile(game),
                session.ManagedProfile))
        {
            return false;
        }

        _deferredClientExit = requested;
        session.FullProfileSafetySaveDueTimestamp = 0;
        session.NextFullProfileSafetySaveAllowedTimestamp = 0;
        session.NextFullProfileHeartbeatTimestamp = 0;
        session.InventoryFastSaveDueTimestamp = 0;
        session.NextInventoryFastSaveAllowedTimestamp = 0;
        try
        {
            BeginClientGameplayQuiescence(player);
            if (!TrySendFinalSaveBegin(session, requested))
            {
                ServerManagerPlugin.Log.LogInfo(
                    "Waiting for queued client world mutations to drain " +
                    "before final-save isolation.");
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            session.SavePipeline.Close();
            _deferredClientExit = null;
            ServerManagerPlugin.Log.LogWarning(
                "Could not request the server final-save gate: " +
                exception);
            FailClient(
                session.Rpc,
                "The server-managed character could not enter final-save mode.",
                exception);
            return false;
        }

        return true;
    }

    private static void ProcessDeferredClientExit()
    {
        DeferredClientExit? deferred = _deferredClientExit;
        if (deferred == null)
        {
            return;
        }

        ClientConnection? session = _client;
        if (session == null ||
            session.Failed ||
            !session.ServerCharacterActive ||
            session.SavePipeline.Closed)
        {
            ResumeDeferredClientExit(
                succeeded: false,
                "The connection ended before the final character save was acknowledged.");
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (now > deferred.DeadlineTimestamp)
        {
            session.SavePipeline.Close();
            ResumeDeferredClientExit(
                succeeded: false,
                "The bounded final-save drain timed out; the last acknowledged " +
                "server revision will be retained.");
            return;
        }

        if (!deferred.ClientWorldBarrierSent)
        {
            try
            {
                if (!TrySendFinalSaveBegin(session, deferred))
                {
                    return;
                }
            }
            catch (Exception exception)
                when (!IntegrityCanonical.IsFatal(exception))
            {
                session.SavePipeline.Close();
                ServerManagerPlugin.Log.LogWarning(
                    "Could not finish the pre-gate client world drain: " +
                    exception);
                ResumeDeferredClientExit(
                    succeeded: false,
                    "The final-save world barrier could not be submitted.");
                return;
            }
        }

        if (!deferred.ServerGateAcknowledged)
        {
            return;
        }

        Game? deferredGame = deferred.Game ?? Game.instance;
        Player? deferredPlayer = Player.m_localPlayer;
        if (deferredGame == null ||
            deferredPlayer == null ||
            session.ManagedProfile == null ||
            _clientGameplayQuiescence == null ||
            !ReferenceEquals(
                _clientGameplayQuiescence.Player,
                deferredPlayer) ||
            !ReferenceEquals(
                ValheimPrivateAccess.GetGamePlayerProfile(deferredGame),
                session.ManagedProfile))
        {
            session.SavePipeline.Close();
            ResumeDeferredClientExit(
                succeeded: false,
                "The local player disappeared before the final snapshot.");
            return;
        }

        if (!deferred.ReadyStateResolved)
        {
            try
            {
                // Player.OnDisable removes Character.CustomFixedUpdate from
                // Valheim's updater. Process any lethal damage that was ordered
                // before FinalSaveReady explicitly, then drain the resulting
                // tombstone/ZDO mutations while the server still accepts inbound
                // gameplay traffic.
                ValheimPrivateAccess.CheckDeath(deferredPlayer);
                if (!TryFlushClientWorldMutations())
                {
                    return;
                }

                deferred.ReadyStateResolved = true;
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                session.SavePipeline.Close();
                ServerManagerPlugin.Log.LogWarning(
                    "Could not settle the final client world state: " +
                    exception);
                ResumeDeferredClientExit(
                    succeeded: false,
                    "The final character state could not be settled.");
                return;
            }
        }

        if (!deferred.FinalCaptureAttempted)
        {
            deferred.FinalCaptureAttempted = true;
            try
            {
                // Inventory changes caused by pre-gate gameplay or death are
                // part of the settled state. From this point through capture,
                // any synchronous inventory mutation is a consistency failure.
                deferred.InventoryChangedAfterQuiescence = false;
                byte[] profileBytes =
                    CaptureDeferredClientExitSnapshot(
                        session,
                        deferredPlayer);
                if (deferred.InventoryChangedAfterQuiescence)
                {
                    throw new InvalidOperationException(
                        "Client inventory changed while the final snapshot was captured.");
                }

                deferred.TargetCaptureId = OfferClientSave(
                    session,
                    profileBytes,
                    ClientCharacterSaveReason.GracefulExit);
                if (!session.SavePipeline.TryStartNext(
                        session.CharacterState?.Revision ??
                        throw new InvalidOperationException(
                            "No managed character revision is active."),
                        Stopwatch.GetTimestamp(),
                        out ClientCharacterSaveDispatch? dispatch) ||
                    dispatch == null ||
                    dispatch.CaptureId != deferred.TargetCaptureId)
                {
                    throw new InvalidOperationException(
                        "The final snapshot could not cross the client save barrier immediately.");
                }

                // Dispatch in the same main-thread turn as capture. The first
                // fragment completes the server's inbound gate, so later
                // gameplay packets cannot overtake this frozen snapshot.
                SendClientSave(session, dispatch);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                // A send failure is ambiguous once any fragment may have
                // reached the server. Close the session pipeline immediately;
                // never retry or wait for a request that was not dispatched.
                session.SavePipeline.Close();
                ServerManagerPlugin.Log.LogWarning(
                    "Could not capture and queue the final server-character snapshot: " +
                    exception);
            }

            if (deferred.TargetCaptureId == 0 ||
                session.SavePipeline.Closed)
            {
                session.SavePipeline.Close();
                ResumeDeferredClientExit(
                    succeeded: false,
                    "The final server-character snapshot could not be queued.");
                return;
            }

            ServerManagerPlugin.Log.LogInfo(
                "Waiting for validated RAM-shadow acknowledgement of final " +
                "server-character " +
                "capture " +
                deferred.TargetCaptureId +
                ".");
        }

        if (session.SavePipeline.IsDrainedThrough(deferred.TargetCaptureId))
        {
            if (deferred.OperationalKickRequested && !deferred.OperationalCompletionSent)
            {
                try
                {
                    SendProtocolOrThrow(session.Rpc,
                        ProtocolPacketCodec.CreateOperationalKickComplete(
                            session.SessionId!, session.Nonce!, _connectionLimits));
                    deferred.OperationalCompletionSent = true;
                }
                catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                {
                    ResumeDeferredClientExit(false,
                        "The operational kick completion could not be delivered: " + exception.Message);
                    return;
                }
            }

            // Wait for vanilla Kicked/Disconnect so its normal UI is preserved.
            // The top-of-method deadline also bounds a lost completion packet.
            if (deferred.Kind == DeferredClientExitKind.OperationalKick)
                return;

            session.SavePipeline.Close();
            ResumeDeferredClientExit(
                succeeded: true,
                "The final server-character revision was accepted into the " +
                "server RAM shadow.");
        }
    }

    private static void ResumeDeferredClientExit(
        bool succeeded,
        string detail)
    {
        DeferredClientExit? deferred = _deferredClientExit;
        if (deferred == null)
        {
            return;
        }

        _deferredClientExit = null;
        if (_client != null)
        {
            _client.FullProfileSafetySaveDueTimestamp = 0;
            _client.NextFullProfileSafetySaveAllowedTimestamp = 0;
            _client.NextFullProfileHeartbeatTimestamp = 0;
            _client.InventoryFastSaveDueTimestamp = 0;
            _client.NextInventoryFastSaveAllowedTimestamp = 0;
            _client.SavePipeline.Close();
        }

        if (succeeded)
        {
            ServerManagerPlugin.Log.LogInfo(detail);
        }
        else
        {
            ServerManagerPlugin.Log.LogWarning(detail);
        }

        _suppressClientSaveCapture = true;
        if (deferred.Kind == DeferredClientExitKind.ApplicationQuit)
        {
            _applicationQuitResumePending = true;
            try
            {
                UnityEngine.Application.Quit();
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogWarning(
                    "The deferred Unity application quit failed: " +
                    exception);
            }

            return;
        }

        Game? game = deferred.Game;
        if (game == null)
        {
            _suppressClientSaveCapture = false;
            return;
        }

        MethodInfo? continueLogout = AccessTools.DeclaredMethod(
            typeof(Game),
            "ContinueLogout",
            new[]
            {
                typeof(bool),
                typeof(bool),
                typeof(bool)
            });
        if (continueLogout == null)
        {
            _suppressClientSaveCapture = false;
            throw new MissingMethodException(
                typeof(Game).FullName,
                "ContinueLogout(bool,bool,bool)");
        }

        _continueLogoutPassThrough = true;
        bool logoutConfirmed = false;
        try
        {
            continueLogout.Invoke(
                game,
                new object[]
                {
                    deferred.Save,
                    deferred.ShouldExit,
                    deferred.ChangeToStartScene
                });
            logoutConfirmed = game.IsShuttingDown();
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException != null &&
                  !IntegrityCanonical.IsFatal(exception.InnerException))
        {
            ServerManagerPlugin.Log.LogError(
                "The deferred Valheim logout failed: " +
                exception.InnerException);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogError(
                "The deferred Valheim logout failed: " +
                exception);
        }
        finally
        {
            _continueLogoutPassThrough = false;
            _suppressClientSaveCapture = false;
        }

        if (!logoutConfirmed &&
            _client != null &&
            !_client.Failed)
        {
            FailClient(
                _client.Rpc,
                "The deferred logout was cancelled after the server entered " +
                "final-save mode; reconnect is required.");
        }
    }

    private static void RecoverCancelledApplicationQuit()
    {
        if (!_applicationQuitResumePending)
        {
            return;
        }

        _applicationQuitResumePending = false;
        _suppressClientSaveCapture = false;
        ClientConnection? session = _client;
        if (session == null || session.Failed)
        {
            return;
        }

        FailClient(
            session.Rpc,
            "Application quit was cancelled after the server entered final-save " +
            "mode; reconnect is required.");
    }

    private static long CreateInitialFullProfileHeartbeatDeadline(
        ClientConnection session,
        long now)
    {
        CharacterClientState state = session.CharacterState ??
            throw new InvalidOperationException(
                "A managed character is required before starting its full-profile heartbeat.");
        byte[] sessionBytes = state.SessionId.ToByteArray();
        int spreadOffsetSeconds =
            sessionBytes[0] % (FullProfileHeartbeatInitialSpreadSeconds + 1);
        int initialDelaySeconds =
            FullProfileHeartbeatIntervalSeconds -
            FullProfileHeartbeatInitialSpreadSeconds +
            spreadOffsetSeconds;
        return AddStopwatchDuration(
            now,
            checked((long)initialDelaySeconds * Stopwatch.Frequency));
    }

    private static long AdvanceFullProfileHeartbeatDeadline(
        long currentDeadline,
        long now)
    {
        if (currentDeadline <= 0 || now < currentDeadline)
        {
            throw new ArgumentOutOfRangeException(nameof(currentDeadline));
        }

        long elapsed = now - currentDeadline;
        long intervals = elapsed / FullProfileHeartbeatIntervalTicks + 1;
        if (intervals >
            (long.MaxValue - currentDeadline) /
            FullProfileHeartbeatIntervalTicks)
        {
            throw new InvalidOperationException(
                "The full-profile heartbeat clock is exhausted.");
        }

        return currentDeadline + intervals * FullProfileHeartbeatIntervalTicks;
    }

    private static long AddStopwatchDuration(long timestamp, long duration)
    {
        if (timestamp < 0 ||
            duration <= 0 ||
            timestamp > long.MaxValue - duration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return timestamp + duration;
    }

    private static string FormatStopwatchElapsedMilliseconds(long startedTimestamp)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
        double elapsedMilliseconds = elapsedTicks <= 0
            ? 0d
            : (double)elapsedTicks * 1000d / Stopwatch.Frequency;
        return elapsedMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
    }

    private static void ProcessSteamAuthenticationCallbacks()
    {
        while (SteamAuthenticationCallbacks.TryDequeue(
                   out SteamAuthenticationCallbackEvent callbackEvent))
        {
            Interlocked.Decrement(
                ref _queuedSteamAuthenticationCallbackCount);
            SteamAuthenticationAttempt attempt = callbackEvent.Attempt;
            lock (SteamAuthenticationGate)
            {
                if (attempt.PendingCallbackCount > 0)
                {
                    --attempt.PendingCallbackCount;
                }
                if (!IsCurrentSteamAuthenticationLocked(attempt) ||
                    attempt.Phase == SteamAuthenticationPhase.Rejected)
                {
                    continue;
                }

                if (attempt.ProcessedCallbackCount < int.MaxValue)
                {
                    ++attempt.ProcessedCallbackCount;
                }
                attempt.LatestResponse =
                    callbackEvent.Response.m_eAuthSessionResponse;

                // ValidateAuthTicketResponse_t is also a lifetime status
                // callback. Once the generation has reached Active, repeated
                // OK notifications are harmless and the original handshake
                // deadline no longer applies. A later non-OK response still
                // revokes the active session below.
                if (attempt.Phase == SteamAuthenticationPhase.Active)
                {
                    if (attempt.LatestResponse !=
                        EAuthSessionResponse.k_EAuthSessionResponseOK)
                    {
                        attempt.ActiveRevocationCaptured = true;
                    }
                    continue;
                }
                if (attempt.ProcessedCallbackCount == 1)
                {
                    attempt.FirstCallbackReceivedTimestamp =
                        callbackEvent.ReceivedTimestamp;
                }

                if (callbackEvent.ReceivedTimestamp >
                    attempt.DeadlineTimestamp)
                {
                    attempt.LateCallbackCaptured = true;
                }

                if (callbackEvent.PhaseAtCapture ==
                        SteamAuthenticationPhase.Reserved ||
                    !callbackEvent.BeginAuthObservedAtCapture)
                {
                    attempt.StaleCallbackCaptured = true;
                }
            }

            // Only queued, connection-bound responses reach this log (at most
            // two per attempt); unmatched native callbacks are not logged.
            ServerManagerPlugin.Log.LogInfo(
                "Processing final Steam authentication callback for peer " +
                attempt.SteamId + ": response=" +
                callbackEvent.Response.m_eAuthSessionResponse +
                ", capturedPhase=" + callbackEvent.PhaseAtCapture + ".");
        }

        SteamAuthenticationAttempt[] attempts;
        lock (SteamAuthenticationGate)
        {
            attempts = SteamAuthenticationsByRpc.Values.ToArray();
        }

        foreach (SteamAuthenticationAttempt attempt in attempts)
        {
            bool rejectDuplicate = false;
            bool rejectPremature = false;
            bool rejectLate = false;
            bool rejectRevoked = false;
            bool processFirstResponse = false;
            bool revokeActive = false;
            EAuthSessionResponse response =
                EAuthSessionResponse.k_EAuthSessionResponseAuthTicketInvalid;
            lock (SteamAuthenticationGate)
            {
                if (!IsCurrentSteamAuthenticationLocked(attempt) ||
                    attempt.Phase == SteamAuthenticationPhase.Rejected)
                {
                    continue;
                }

                if (attempt.Phase == SteamAuthenticationPhase.Active)
                {
                    if (attempt.CallbackOverflowed)
                    {
                        rejectDuplicate = true;
                        revokeActive = true;
                    }
                    else if (attempt.ActiveRevocationCaptured)
                    {
                        response = attempt.LatestResponse;
                        rejectRevoked = true;
                    }
                    else
                    {
                        continue;
                    }
                }
                else if (attempt.LateCallbackCaptured)
                {
                    rejectLate = true;
                }
                else if (attempt.StaleCallbackCaptured ||
                    attempt.DuplicateBeginAuthInvocation)
                {
                    rejectPremature = true;
                }
                else if (attempt.CallbackOverflowed ||
                    attempt.ProcessedCallbackCount > 1)
                {
                    response = attempt.LatestResponse;
                    rejectDuplicate = true;
                    revokeActive =
                        attempt.Phase == SteamAuthenticationPhase.Active &&
                        response !=
                        EAuthSessionResponse.k_EAuthSessionResponseOK;
                }
                else if (attempt.ProcessedCallbackCount == 1)
                {
                    response = attempt.LatestResponse;
                    if (attempt.Phase ==
                        SteamAuthenticationPhase.Reserved)
                    {
                        // BeginAuthSession has not run yet, so a callback bound
                        // to this ID can only be stale or unrelated.
                        rejectPremature = true;
                    }
                    else if (attempt.Phase ==
                             SteamAuthenticationPhase.VanillaAccepted)
                    {
                        processFirstResponse = true;
                    }
                }
            }

            if (rejectDuplicate)
            {
                RejectSteamAuthentication(
                    attempt,
                    new ProtocolRejection(
                        ProtocolRejectCode.DuplicateMessage,
                        revokeActive
                            ? "Steam revoked the authenticated session."
                            : "Duplicate Steam authentication responses were rejected."),
                    disconnectImmediately: true);
                continue;
            }

            if (rejectRevoked)
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Steam revoked an active peer authentication with response " +
                    response + ".");
                RejectSteamAuthentication(
                    attempt,
                    new ProtocolRejection(
                        ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                        "Steam revoked the authenticated session."),
                    disconnectImmediately: true);
                continue;
            }

            if (rejectLate)
            {
                RejectSteamAuthentication(
                    attempt,
                    new ProtocolRejection(
                        ProtocolRejectCode.HandshakeTimedOut,
                        "Final Steam authentication arrived after the deadline."),
                    disconnectImmediately: true);
                continue;
            }

            if (rejectPremature)
            {
                RejectSteamAuthentication(
                    attempt,
                    new ProtocolRejection(
                        ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                        "A stale Steam authentication response was rejected."),
                    disconnectImmediately: true);
                continue;
            }

            if (!processFirstResponse)
            {
                continue;
            }

            if (response != EAuthSessionResponse.k_EAuthSessionResponseOK)
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Steam rejected final peer authentication with response " +
                    response + ".");
                RejectSteamAuthentication(
                    attempt,
                    new ProtocolRejection(
                        ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                        "Steam rejected final peer authentication."),
                    disconnectImmediately: true);
                continue;
            }

            if (!TryActivateSteamAuthentication(
                    attempt,
                    out ZNet? server,
                    out ProtocolRejection? rejection))
            {
                RejectSteamAuthentication(
                    attempt,
                    rejection,
                    disconnectImmediately: true);
                continue;
            }

            CompleteAuthenticatedPeer(server!, attempt.Rpc);
        }
    }

    private static bool TryActivateSteamAuthentication(
        SteamAuthenticationAttempt attempt,
        out ZNet? server,
        out ProtocolRejection? rejection)
    {
        server = ZNet.instance;
        rejection = null;
        if (server == null ||
            !server.IsServer() ||
            ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
        {
            rejection = new ProtocolRejection(
                ProtocolRejectCode.NotServer,
                "The authoritative Steam server was unavailable.");
            return false;
        }

        if (!TryGetSteamConnection(server, attempt.Rpc,
                out SteamAuthenticationAttempt current, out rejection))
        {
            return false;
        }

        ZNetPeer peer = current.Peer;
        if (!ReferenceEquals(current, attempt) ||
            !peer.IsReady() ||
            peer.m_uid == 0L ||
            string.IsNullOrWhiteSpace(peer.m_playerName) ||
            !WorldBuffers.TryGetValue(
                attempt.Rpc,
                out BufferedWorldSocket buffer) ||
            buffer.Released || buffer.Quarantined ||
            buffer.Overflowed || buffer.InboundViolation)
        {
            rejection = new ProtocolRejection(
                ProtocolRejectCode.PeerIdentityUnavailable,
                "The Steam peer changed before final authentication completed.");
            return false;
        }

        lock (SteamAuthenticationGate)
        {
            if (!IsCurrentSteamAuthenticationLocked(attempt) ||
                attempt.Phase !=
                SteamAuthenticationPhase.VanillaAccepted ||
                attempt.ProcessedCallbackCount != 1 ||
                attempt.LateCallbackCaptured ||
                attempt.FirstCallbackReceivedTimestamp >
                attempt.DeadlineTimestamp ||
                attempt.LatestResponse !=
                EAuthSessionResponse.k_EAuthSessionResponseOK)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.InvalidTransition,
                    "The Steam authentication state changed before activation.");
                return false;
            }

            attempt.Phase = SteamAuthenticationPhase.Active;
            attempt.ReachedActive = true;
        }

        ServerManagerPlugin.Log.LogInfo(
            "Accepted final Steam authentication for a connection-bound peer. Steam ID: " +
            attempt.SteamId + ".");
        return true;
    }

    private static void ExpireSteamAuthentications()
    {
        long now = Stopwatch.GetTimestamp();
        SteamAuthenticationAttempt[] expired;
        lock (SteamAuthenticationGate)
        {
            expired = SteamAuthenticationsByRpc.Values
                .Where(
                    attempt =>
                        attempt.Phase !=
                        SteamAuthenticationPhase.Active &&
                        attempt.Phase !=
                        SteamAuthenticationPhase.Rejected &&
                        attempt.DeadlineTimestamp <= now)
                .ToArray();
        }

        foreach (SteamAuthenticationAttempt attempt in expired)
        {
            ServerManagerPlugin.Log.LogWarning(
                "Final Steam authentication deadline reached for peer " +
                attempt.SteamId + ": phase=" + attempt.Phase +
                ", beginAccepted=" + attempt.BeginAuthImmediateAccepted +
                ", callbacksQueued=" + attempt.EnqueuedCallbackCount +
                ", callbacksProcessed=" + attempt.ProcessedCallbackCount + ".");
            RejectSteamAuthentication(
                attempt,
                new ProtocolRejection(
                    ProtocolRejectCode.HandshakeTimedOut,
                    "Final Steam authentication timed out."),
                disconnectImmediately: true);
        }
    }

    private static void RejectSteamAuthentication(
        SteamAuthenticationAttempt attempt,
        ProtocolRejection? rejection,
        bool disconnectImmediately = false)
    {
        if (attempt == null)
        {
            return;
        }

        bool reject;
        lock (SteamAuthenticationGate)
        {
            reject =
                IsCurrentSteamAuthenticationLocked(attempt) &&
                attempt.Phase != SteamAuthenticationPhase.Rejected;
            if (reject)
            {
                attempt.Phase = SteamAuthenticationPhase.Rejected;
            }
        }

        if (!reject)
        {
            return;
        }

        SendServerRejection(
            attempt.Rpc,
            rejection ??
            new ProtocolRejection(
                ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                "Final Steam authentication failed."));
        if (disconnectImmediately)
        {
            DisconnectServerPeer(attempt.Rpc);
        }
    }

    private static bool IsCurrentSteamAuthenticationLocked(
        SteamAuthenticationAttempt attempt)
    {
        return
            SteamAuthenticationsByRpc.TryGetValue(
                attempt.Rpc,
                out SteamAuthenticationAttempt byRpc) &&
            SteamAuthenticationsById.TryGetValue(
                attempt.SteamId.m_SteamID,
                out SteamAuthenticationAttempt byId) &&
            ReferenceEquals(byRpc, attempt) &&
            ReferenceEquals(byId, attempt);
    }

    private static void PruneRetiredSteamIds()
    {
        lock (SteamAuthenticationGate)
        {
            PruneRetiredSteamIdsLocked(Stopwatch.GetTimestamp());
        }
    }

    private static void PruneRetiredSteamIdsLocked(long now)
    {
        foreach (ulong steamId in RetiredSteamIds
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            RetiredSteamIds.Remove(steamId);
        }
    }

    private static void CompleteAuthenticatedPeer(
        ZNet server,
        ZRpc rpc)
    {
        if (_shuttingDown)
        {
            return;
        }

        if (!ServerPeerResolver.TryResolvePeer(
                server,
                rpc,
                out ZNetPeer peer,
                out ProtocolRejection resolveError))
        {
            SendServerRejection(rpc, resolveError);
            return;
        }

        ProtocolOperationResult authenticated =
            _coordinator.ConfirmPeerInfoAuthenticated(server, rpc);
        if (!authenticated.Succeeded)
        {
            SendServerRejection(rpc, authenticated.Rejection);
            return;
        }

        try
        {
            if (!TryResolveActiveDetectionPeer(server, rpc,
                    out ServerPeerIdentity authenticatedIdentity,
                    out ProtocolRejection identityError))
            {
                SendServerRejection(rpc, identityError);
                return;
            }
            ManifestValidationDecision finalManifest =
                _manifestValidator.ConfirmAuthenticated(server, authenticatedIdentity);
            if (!finalManifest.Accepted)
            {
                SendServerRejection(rpc, finalManifest.Rejection);
                return;
            }

            byte[] payload = Array.Empty<byte>();
            if (authenticated.Session != null &&
                authenticated.Session.ServerCharactersEnabled)
            {
                CharacterSnapshotService service =
                    EnsureServerCharacterService();
                if (ServerDetectionStates.TryGetValue(rpc, out ServerDetectionState captureState) &&
                    captureState.BackupOnly)
                {
                    captureState.BackupCaptureStorageKey = CharacterStorageKeyProvider.FormatStorageKey(
                        new CharacterIdentity(CharacterSteamIdentity.AccountPrefix + authenticatedIdentity.HostId,
                            CharacterNamePolicy.NormalizeAndValidate(authenticatedIdentity.PlayerName)));
                    captureState.BackupCaptureRequested = true;
                    SendProtocolOrThrow(rpc, ProtocolPacketCodec.Encode(new ProtocolPacket(
                        ProtocolPacketKind.BackupCaptureRequest,
                        ProtocolSequence.BackupCaptureRequest,
                        authenticated.Session.SessionId, authenticated.Session.Nonce,
                        Array.Empty<byte>()), _connectionLimits));
                    return;
                }
                CharacterSessionOpenResult opened =
                    service.OpenOrCreateServerSession(peer);
                GetConnectionRejectionMarker(rpc).InitialCharacterRequiresFresh =
                    opened.Snapshot.RequiresFreshLocalCharacter;
                if (!opened.Snapshot.RequiresFreshLocalCharacter &&
                    service.TryGetServerSession(rpc, out CharacterSession? openedSession) && openedSession != null &&
                    openedSession.TryGetCurrentSemanticSnapshot(opened.Snapshot.Revision,
                        opened.Snapshot.PayloadSha256Unsafe, out CharacterSemanticSnapshot openedSemantic))
                {
                    // Match the client's existing revision-1 unmaterialized
                    // fallback too, using the already-validated server snapshot.
                    GetConnectionRejectionMarker(rpc).InitialCharacterRequiresFresh =
                        LocalCharacterFirstJoinGuard.ShouldRejectUsedLocalFirstJoin(opened.Snapshot,
                            selectedHasWorldHistory: true, managedHasPlayerData: openedSemantic.HasPlayerData);
                }
                payload = opened.NetworkPackage.GetArray();
                ServerManagerPlugin.Log.LogInfo(
                    (opened.PendingInitialCommit ? "Prepared" : "Loaded") +
                    " authoritative character revision " +
                    opened.Snapshot.Revision +
                    " for an authenticated peer.");
                ServerEventRuntime.RecordCharacterObservations(
                    opened.Snapshot.AccountId,
                    opened.Snapshot.CharacterName,
                    opened.Snapshot.Revision,
                    "stored",
                    opened.AuditFindings.AuditObservations);
                RecordCharacterStatLimits(
                    null,
                    opened.Snapshot.AccountId,
                    opened.Snapshot.CharacterName,
                    opened.StatLimitFindings,
                    "stored");
            }

            SendInitialCharacterTransfer(server, rpc, payload);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "Authoritative character setup failed: " + exception);
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.CharacterTransferFailed,
                    "The server could not open the authoritative character.")
                    .WithPlayerMessage(PlayerConnectionMessages.ExceptionKey(exception),
                        PlayerConnectionMessages.ExceptionArguments(exception))
                    .WithConnectionAudit("character", "character_open_failed",
                        exception.GetType().Name + ": " + exception.Message, stage: "character_open"));
        }
    }

    private static void SendInitialCharacterTransfer(ZNet server, ZRpc rpc, byte[] payload)
    {
        ProtocolOperationResult<CharacterTransferPlan> prepared =
            _coordinator.PrepareCharacterTransfer(server, rpc, payload, preferCompression: true);
        if (!prepared.Succeeded || prepared.Value == null)
        {
            SendServerRejection(rpc, prepared.Rejection);
            return;
        }
        SendPlan(rpc, prepared.Value);
        ProtocolOperationResult sent = _coordinator.ConfirmCharacterSent(
            server, rpc, prepared.Value.MessageId);
        if (!sent.Succeeded) SendServerRejection(rpc, sent.Rejection);
        else ServerManagerPlugin.Log.LogInfo(
            "Initial server character transfer sent; waiting for Ready acknowledgement.");
    }

    private static void SendCharacterPayload(
        ZRpc rpc,
        ConnectionSessionSnapshot session,
        byte[] payload,
        bool preferCompression)
    {
        CharacterTransferPlan plan =
            BoundedFragmentCodec.CreateCharacterTransfer(
                session.SessionId,
                session.Nonce,
                payload,
                preferCompression,
                _connectionLimits,
                _fragmentLimits);
        SendPlan(rpc, plan);
    }

    private static void SendPlan(
        ZRpc rpc,
        CharacterTransferPlan plan)
    {
        foreach (ZPackage packet in plan.Packets)
        {
            SendProtocolOrThrow(rpc, packet);
        }
    }

    private static void SendProtocolOrThrow(
        ZRpc rpc,
        ZPackage package)
    {
        if (!RawProtocolRpcTransport.Send(
                rpc,
                ProtocolRpcName,
                package,
                _connectionLimits))
        {
            throw new IOException("The protocol peer disconnected before send.");
        }
    }

    private static bool ReleaseWorld(
        ZNet server,
        ZRpc rpc)
    {
        if (!_coordinator.CanReleaseWorld(
                server,
                rpc,
                out ProtocolRejection rejection))
        {
            SendServerRejection(rpc, rejection);
            return false;
        }

        if (!WorldBuffers.TryGetValue(rpc, out BufferedWorldSocket buffer))
        {
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.WorldReleaseNotReady,
                    "The world synchronization buffer was unavailable."));
            return false;
        }

        try
        {
            if (!buffer.TryRelease(out string failure))
            {
                SendServerRejection(
                    rpc,
                    new ProtocolRejection(
                        ProtocolRejectCode.WorldReleaseNotReady,
                        string.IsNullOrWhiteSpace(failure)
                            ? "The world synchronization gate rejected release."
                            : failure));
                return false;
            }

            if (ServerDetectionStates.TryGetValue(
                    rpc,
                    out ServerDetectionState detectionState) &&
                detectionState.Policy.AllowAdminCheatCommands)
            {
                ProtocolRejection? entitlementError = null;
                bool entitlementRefreshed =
                    TryRefreshAdminCommandEntitlement(
                        server,
                        rpc,
                        detectionState,
                        Stopwatch.GetTimestamp(),
                        out entitlementError);
                if (!entitlementRefreshed)
                {
                    SendServerRejection(
                        rpc,
                        entitlementError ??
                        new ProtocolRejection(
                            ProtocolRejectCode.InternalError,
                            "Admin command authorization was unavailable."));
                    return false;
                }
            }

            ServerManagerPlugin.Log.LogInfo(
                "Released world synchronization after character readiness.");
            GetConnectionRejectionMarker(rpc).WorldReleased = true;
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "World synchronization release failed: " + exception.Message);
            SendServerRejection(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.WorldReleaseNotReady,
                    "The server could not release world synchronization."));
            return false;
        }
    }

    private static void SendServerRejection(
        ZRpc rpc,
        ProtocolRejection? rejection,
        bool markRejected = true,
        string source = "server_observed")
    {
        if (rpc == null)
        {
            return;
        }

        rejection ??= new ProtocolRejection(
            ProtocolRejectCode.InternalError,
            "The connection was rejected by ServerManager.");

        RecordConnectionRejection(rpc, rejection, source);
        _manifestValidator?.RemovePeer(rpc);

        if (markRejected)
        {
            try
            {
                _coordinator.RejectSession(rpc, rejection);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogDebug(
                    "Could not mark an already-ending protocol session rejected: " +
                    exception.Message);
            }
        }

        try
        {
            ZPackage rejectPacket = _coordinator.CreateRejectPacket(rpc, rejection);
            RawProtocolRpcTransport.Send(
                rpc,
                ProtocolRpcName,
                rejectPacket,
                _connectionLimits);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogDebug(
                "Could not send the custom rejection packet: " + exception.Message);
        }

        try
        {
            rpc.Invoke("Error", rejection.LegacyDisconnectError);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogDebug(
                "Could not send the legacy connection error: " + exception.Message);
        }

        if (WorldBuffers.TryGetValue(rpc, out BufferedWorldSocket buffer))
        {
            buffer.Discard();
        }

        ServerManagerPlugin.Log.LogWarning(
            "Rejected connection [" +
            rejection.Code +
            "]: " +
            rejection.SafeMessage);
        if (rejection.Disconnect)
        {
            ScheduleDisconnect(rpc);
        }
    }

    private static void InstallWorldGate(ZRpc rpc)
    {
        if (rpc == null)
        {
            throw new ArgumentNullException(nameof(rpc));
        }

        if (WorldBuffers.ContainsKey(rpc) ||
            rpc.GetSocket() is BufferedWorldSocket)
        {
            throw new InvalidOperationException(
                "A connection gate is already installed for this peer.");
        }

        BufferedWorldSocket buffer = new(
            rpc.GetSocket(),
            MaximumBufferedWorldBytes);
        ValheimPrivateAccess.SetSocket(rpc, buffer);
        try
        {
            WorldBuffers.Add(rpc, buffer);
        }
        catch
        {
            ValheimPrivateAccess.SetSocket(rpc, buffer.Original);
            buffer.Discard();
            throw;
        }
    }

    private static ProtocolRejection? AdmitClientSaveAssembly(
        ZRpc rpc,
        int decodedLength)
    {
        const int burstLimit = 13;
        ServerFinalSaveDrains.TryGetValue(
            rpc,
            out ServerFinalSaveDrain? finalDrain);
        if (finalDrain?.GateStarted == true && finalDrain.SaveAssemblyAdmitted)
        {
            return new ProtocolRejection(
                ProtocolRejectCode.DuplicateMessage,
                "Only one character save is allowed after final-save isolation.");
        }

        int globalBurstLimit = Math.Max(
            1024,
            _serverInboundFragmentLimits.MaxConcurrentAssemblies);
        long globalByteLimit = Math.Max(
            64L * 1024L * 1024L,
            _serverInboundFragmentLimits.MaxReservedBytes);
        long perStorageByteLimit = Math.Min(
            globalByteLimit,
            checked(2L * _serverCharacterOptions.MaxEnvelopeBytes));
        bool initialCapture = ServerDetectionStates.TryGetValue(rpc, out ServerDetectionState captureState) &&
            captureState.BackupOnly && captureState.BackupCaptureRequested &&
            !captureState.TerminalActionApplied && captureState.BackupCaptureStorageKey.Length != 0 &&
            _coordinator.TryGetSnapshot(rpc, out ConnectionSessionSnapshot connection) &&
            connection.State == ConnectionSessionState.ManifestValidated &&
            connection.PeerInfoAuthenticated && connection.ServerCharactersEnabled &&
            !connection.CharacterTransferPrepared;
        string storageKey;
        if (initialCapture)
        {
            // This key came from the authenticated peer when requesting capture,
            // never from the raw client profile. Capture and later saves consume
            // the same per-character and global byte/request budgets.
            storageKey = captureState.BackupCaptureStorageKey;
        }
        else if (_serverCharacterService != null &&
            _serverCharacterService.TryGetServerSession(rpc, out CharacterSession? session) &&
            session != null)
        {
            storageKey = session.StorageKey;
        }
        else
        {
            return new ProtocolRejection(
                ProtocolRejectCode.InvalidTransition,
                "No authoritative character session is active for this save.");
        }

        if (decodedLength <= 0 ||
            decodedLength > (initialCapture
                ? _serverCharacterOptions.MaxPayloadBytes
                : _serverCharacterOptions.MaxEnvelopeBytes))
        {
            return new ProtocolRejection(
                ProtocolRejectCode.PayloadTooLarge,
                "The character save envelope exceeded the configured limit.");
        }

        long now = Stopwatch.GetTimestamp();
        long cutoff = now - checked(10L * Stopwatch.Frequency);
        PruneGlobalSaveAdmissions(cutoff);
        if (GlobalSaveAdmissions.Count >= globalBurstLimit ||
            decodedLength > globalByteLimit - _globalSaveAdmissionBytes)
        {
            return new ProtocolRejection(
                ProtocolRejectCode.CharacterSaveRateExceeded,
                "The server is receiving character saves too frequently.");
        }

        if (!SaveAdmissionHistory.TryGetValue(
                storageKey,
                out SaveAdmissionWindow window))
        {
            window = new SaveAdmissionWindow();
            SaveAdmissionHistory.Add(storageKey, window);
        }

        window.LastTouched = now;
        while (window.Admissions.Count > 0 &&
               window.Admissions.Peek().Timestamp < cutoff)
        {
            SaveByteAdmission expired = window.Admissions.Dequeue();
            window.DecodedBytes -= expired.ByteCount;
        }

        if (window.DecodedBytes < 0)
        {
            window.DecodedBytes = 0;
        }

        if (window.Admissions.Count >= burstLimit ||
            decodedLength > perStorageByteLimit - window.DecodedBytes)
        {
            return new ProtocolRejection(
                ProtocolRejectCode.CharacterSaveRateExceeded,
                "Server-character saves were sent too frequently.");
        }

        SaveByteAdmission admitted =
            new SaveByteAdmission(now, decodedLength);
        window.Admissions.Enqueue(admitted);
        window.DecodedBytes += decodedLength;
        GlobalSaveAdmissions.Enqueue(admitted);
        _globalSaveAdmissionBytes += decodedLength;
        if (finalDrain?.GateStarted == true)
        {
            finalDrain.SaveAssemblyAdmitted = true;
        }

        return null;
    }

    private static void PruneGlobalSaveAdmissions(long cutoff)
    {
        while (GlobalSaveAdmissions.Count > 0 &&
               GlobalSaveAdmissions.Peek().Timestamp < cutoff)
        {
            SaveByteAdmission expired = GlobalSaveAdmissions.Dequeue();
            _globalSaveAdmissionBytes -= expired.ByteCount;
        }

        if (_globalSaveAdmissionBytes < 0)
        {
            _globalSaveAdmissionBytes = 0;
        }
    }

    private static void CleanupSaveAdmissionHistory()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextSaveAdmissionCleanupTimestamp)
        {
            return;
        }

        long retention = checked(10L * 60L * Stopwatch.Frequency);
        foreach (KeyValuePair<string, SaveAdmissionWindow> pair in
                 SaveAdmissionHistory.ToArray())
        {
            if (now - pair.Value.LastTouched > retention)
            {
                SaveAdmissionHistory.Remove(pair.Key);
            }
        }

        _nextSaveAdmissionCleanupTimestamp =
            now + checked(60L * Stopwatch.Frequency);
    }

    private static void ScheduleDisconnect(
        ZRpc rpc,
        int milliseconds = 250)
    {
        if (rpc == null)
        {
            return;
        }

        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        long delayTicks = checked(
            (long)Math.Ceiling(
                milliseconds / 1000d * Stopwatch.Frequency));
        long now = Stopwatch.GetTimestamp();
        long due = now > long.MaxValue - delayTicks
            ? long.MaxValue
            : now + delayTicks;
        if (!PendingDisconnects.TryGetValue(rpc, out long existing) ||
            due < existing)
        {
            PendingDisconnects[rpc] = due;
        }
    }

    private static void DisconnectServerPeer(ZRpc rpc)
    {
        ZNet? server = ZNet.instance;
        if (server != null &&
            server.IsServer() &&
            ServerPeerResolver.TryResolvePeer(
                server,
                rpc,
                out ZNetPeer peer,
                out _))
        {
            try
            {
                server.Disconnect(peer);
                return;
            }
            catch (Exception exception)
                when (!IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Steam peer disconnect required fallback cleanup: " +
                    exception.Message);
            }
        }

        SteamAuthenticationAttempt? unregisteredAttempt;
        lock (SteamAuthenticationGate)
        {
            SteamAuthenticationsByRpc.TryGetValue(
                rpc,
                out unregisteredAttempt);
        }

        RemoveSteamAuthentication(rpc);
        if (unregisteredAttempt != null)
        {
            DisposeUnregisteredPeer(unregisteredAttempt.Peer);
            return;
        }

        try
        {
            rpc.GetSocket()?.Close();
        }
        catch
        {
            // The peer is already gone.
        }
    }

    private static void CleanupPeer(
        ZNet znet,
        ZRpc rpc)
    {
        RemoveAdminPeer(rpc);
        _manifestValidator?.RemovePeer(rpc);
        PlayerActivityRuntime.OnPeerDisconnected(rpc);
        ServerEventRuntime.OnPeerDisconnected(rpc);
        RemoveSteamAuthentication(rpc);
        PendingDisconnects.Remove(rpc);
        RegistrationFailures.Remove(rpc);
        ServerDetectionStates.Remove(rpc);
        ServerFinalSaveDrains.Remove(rpc);
        ServerEventDisplaySequences.Remove(rpc);

        if (znet.IsServer())
        {
            if (WorldBuffers.TryGetValue(rpc, out BufferedWorldSocket buffer))
            {
                RestoreAndDiscardBuffer(rpc, buffer);
            }

            _serverReassembler.RemovePeer(rpc);
            _serverCharacterService?.CloseServerSession(rpc);
            _coordinator.RemoveSession(rpc);
            return;
        }

        if (_client != null && ReferenceEquals(_client.Rpc, rpc))
        {
            CancelClientManifestPreparation(_client);
            RestoreClientProfile();
            ClientDetection.Reset();
            CheatCommandGuard.Reset();
            _clientReassembler.RemovePeer(rpc);
            _client = null;
        }
    }

    private static void RemoveSteamAuthentication(ZRpc rpc)
    {
        SteamAuthenticationAttempt? removed = null;
        bool rejectPending = false;
        bool quarantinedIncomplete = false;
        bool quarantineCapacityExhausted = false;
        SteamAuthenticationPhase phaseAtDisconnect;
        lock (SteamAuthenticationGate)
        {
            if (!SteamAuthenticationsByRpc.TryGetValue(
                    rpc,
                    out removed))
            {
                return;
            }

            phaseAtDisconnect = removed.Phase;
            SteamAuthenticationsByRpc.Remove(rpc);
            if (SteamAuthenticationsById.TryGetValue(
                    removed.SteamId.m_SteamID,
                    out SteamAuthenticationAttempt byId) &&
                ReferenceEquals(byId, removed))
            {
                SteamAuthenticationsById.Remove(
                    removed.SteamId.m_SteamID);
            }

            rejectPending =
                removed.Phase != SteamAuthenticationPhase.Active &&
                removed.Phase != SteamAuthenticationPhase.Rejected;
            quarantinedIncomplete =
                QuarantineIncompleteSteamAuthenticationLocked(removed);
            quarantineCapacityExhausted =
                _steamAuthenticationGenerationCapacityExhausted;
            removed.Phase = SteamAuthenticationPhase.Rejected;
            RetiredSteamIds[removed.SteamId.m_SteamID] =
                Stopwatch.GetTimestamp() + RetiredSteamIdQuietTicks;
        }

        if (quarantinedIncomplete)
        {
            ServerManagerPlugin.Log.LogWarning(
                "Quarantined a Steam ID for the remainder of this server " +
                "process because BeginAuthSession was invoked without reaching " +
                "Active on a generation-bound final OK callback. Phase at disconnect=" +
                phaseAtDisconnect + ", beginAccepted=" + removed.BeginAuthImmediateAccepted +
                ", callbacksQueued=" + removed.EnqueuedCallbackCount +
                ", callbacksProcessed=" + removed.ProcessedCallbackCount + ".");
        }

        if (quarantineCapacityExhausted)
        {
            ServerManagerPlugin.Log.LogFatal(
                "The bounded incomplete Steam-authentication quarantine is " +
                "exhausted. New Steam connections will remain fail-closed " +
                "until the server process restarts.");
        }

        if (!rejectPending)
        {
            return;
        }

        try
        {
            _coordinator.RejectSession(
                rpc,
                new ProtocolRejection(
                    ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                    "The connection ended before final Steam authentication."));
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogDebug(
                "Could not mark the ending Steam authentication rejected: " +
                exception.Message);
        }
    }

    private static bool QuarantineIncompleteSteamAuthenticationLocked(
        SteamAuthenticationAttempt attempt)
    {
        if (attempt.ReachedActive ||
            !attempt.BeginAuthInvocationObserved)
        {
            return false;
        }

        ulong steamId = attempt.SteamId.m_SteamID;
        if (QuarantinedIncompleteSteamIds.Contains(steamId))
        {
            return true;
        }

        if (QuarantinedIncompleteSteamIds.Count >=
            MaximumQuarantinedIncompleteSteamIds)
        {
            // An untracked delayed response could otherwise authenticate a
            // future generation. Once the bounded table cannot represent that
            // ambiguity, accepting any new reservation is unsafe.
            _steamAuthenticationGenerationCapacityExhausted = true;
            return true;
        }

        QuarantinedIncompleteSteamIds.Add(steamId);
        return true;
    }

    private static void DisposeSteamAuthenticationCallback()
    {
        Callback<ValidateAuthTicketResponse_t>? callback;
        lock (SteamAuthenticationGate)
        {
            callback = _steamAuthenticationCallback;
            _steamAuthenticationCallback = null;
            _steamAuthenticationCallbackUnavailable = false;
            foreach (SteamAuthenticationAttempt attempt in
                     SteamAuthenticationsByRpc.Values)
            {
                QuarantineIncompleteSteamAuthenticationLocked(attempt);

                attempt.Phase = SteamAuthenticationPhase.Rejected;
                RetiredSteamIds[attempt.SteamId.m_SteamID] =
                    Stopwatch.GetTimestamp() +
                    RetiredSteamIdQuietTicks;
            }

            SteamAuthenticationsByRpc.Clear();
            SteamAuthenticationsById.Clear();

            while (SteamAuthenticationCallbacks.TryDequeue(out _))
            {
            }

            Interlocked.Exchange(
                ref _queuedSteamAuthenticationCallbackCount,
                0);
        }

        try
        {
            callback?.Dispose();
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogDebug(
                "Final Steam authentication callback disposal failed: " +
                exception.Message);
        }
    }

    private static void RestoreAndDiscardBuffer(
        ZRpc rpc,
        BufferedWorldSocket buffer)
    {
        WorldBuffers.Remove(rpc);
        try
        {
            if (ReferenceEquals(rpc.GetSocket(), buffer))
            {
                ValheimPrivateAccess.SetSocket(rpc, buffer.Original);
            }
        }
        catch
        {
            // The RPC may already be disposing.
        }

        buffer.Discard();
    }

    private static void RestoreClientProfile()
    {
        ReleaseClientGameplayQuiescence(restorePlayer: true);
        ClientConnection? session = _client;
        if (session == null)
        {
            return;
        }

        Game? game = Game.instance;
        bool localPlayerIsAlive = Player.m_localPlayer != null;
        // Failed admission restores the untouched selection. Once admitted,
        // keep the current profile bound to that slot through vanilla teardown.
        if (!session.ReadyAcknowledgementSent && !localPlayerIsAlive &&
            game != null &&
            session.OriginalProfile != null &&
            session.ManagedProfile != null &&
            ReferenceEquals(
                ValheimPrivateAccess.GetGamePlayerProfile(game),
                session.ManagedProfile))
        {
            ValheimPrivateAccess.SetGamePlayerProfile(
                game,
                session.OriginalProfile);
        }

        session.ServerCharacterActive = false;
        session.ManagedProfile = null;
        session.OriginalProfile = null;
        session.CharacterState = null;
        session.FullProfileSafetySaveDueTimestamp = 0;
        session.NextFullProfileSafetySaveAllowedTimestamp = 0;
        session.NextFullProfileHeartbeatTimestamp = 0;
        session.InventoryFastSaveDueTimestamp = 0;
        session.NextInventoryFastSaveAllowedTimestamp = 0;
        session.InventoryFastPathReady = false;
        session.AcknowledgedProfileBytes = null;
        session.AcknowledgedProfileRevision = 0;
        session.SavePipeline.Close();
    }

    private static void FailClient(
        ZRpc rpc,
        string safeMessage,
        Exception? exception = null,
        string? playerMessage = null)
    {
        if (_client != null && ReferenceEquals(_client.Rpc, rpc))
        {
            if (_client.Failed)
            {
                return;
            }

            _client.Failed = true;
            CancelClientManifestPreparation(_client);
        }

        string message = string.IsNullOrWhiteSpace(safeMessage)
            ? "The ServerManager connection protocol failed."
            : safeMessage.Trim();
        ServerManagerPlugin.ConnectionError = SanitizePlayerNotice(playerMessage ??
            PlayerLocalizer.Text(message == ClientSecurityPolicyRejectionMessage
                ? "sm_security_ended" : "sm_connection_failed"));

        if (exception == null)
        {
            ServerManagerPlugin.Log.LogWarning(
                "ServerManager connection failed: " + message);
        }
        else
        {
            ServerManagerPlugin.Log.LogWarning(
                "ServerManager connection failed: " +
                message +
                " " +
                exception);
        }

        ReleaseClientGameplayQuiescence(restorePlayer: true);
        RestoreClientProfile();
        ClientDetection.Reset();
        CheatCommandGuard.Reset();
        try
        {
            ValheimPrivateAccess.SetConnectionStatus(
                ZNet.ConnectionStatus.ErrorConnectFailed);
        }
        catch
        {
            // UI state will also transition when the socket closes.
        }

        try
        {
            rpc?.GetSocket()?.Close();
        }
        catch
        {
            // The connection is already closed.
        }
    }

    private static string DecodeRejectMessage(ProtocolPacket packet)
    {
        try
        {
            ProtocolRejection rejection = ProtocolPacketCodec.DecodeRejectPayload(packet, _connectionLimits);
            ProtocolRejectCode code = rejection.Code;
            string message = rejection.SafeMessage;
            if (code == ProtocolRejectCode.CheatDetected ||
                code == ProtocolRejectCode.DetectionProtocolViolation)
            {
                return ClientSecurityPolicyRejectionMessage;
            }

            return "ServerManager [" +
                   code +
                   "]: " +
                   SanitizeRemoteRejectMessage(message);
        }
        catch
        {
            return "The server rejected the ServerManager connection.";
        }
    }

    private static string DecodePlayerRejectMessage(ProtocolPacket packet)
    {
        try
        {
            return PlayerConnectionMessages.FromRejection(
                ProtocolPacketCodec.DecodeRejectPayload(packet, _connectionLimits));
        }
        catch
        {
            return PlayerLocalizer.Text("sm_connection_failed");
        }
    }

    // Only locally selected translations and validated typed arguments reach
    // this presentation path. Raw remote diagnostics keep their separate,
    // bounded sanitizer below. The lobby panel renders this as plain text and
    // pages long notices, so preserve every line rather than silently truncate.
    private static string SanitizePlayerNotice(string message)
    {
        StringBuilder safe = new(message.Length);
        for (int index = 0; index < message.Length; ++index)
        {
            char character = message[index];
            if (character == '\r')
            {
                safe.Append('\n');
                if (index + 1 < message.Length && message[index + 1] == '\n')
                {
                    ++index;
                }
                continue;
            }

            if (character == '\n' || character == '\u2028' || character == '\u2029')
            {
                safe.Append('\n');
                continue;
            }

            if (char.IsHighSurrogate(character) && index + 1 < message.Length &&
                char.IsLowSurrogate(message[index + 1]))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(message, index) == UnicodeCategory.Format)
                {
                    safe.Append(' ');
                }
                else
                {
                    safe.Append(character).Append(message[index + 1]);
                }
                ++index;
                continue;
            }

            bool unsafeFormatting = char.IsControl(character) ||
                                    char.IsSurrogate(character) ||
                                    char.GetUnicodeCategory(character) == UnicodeCategory.Format;
            safe.Append(unsafeFormatting ? ' ' : character);
        }

        return safe.ToString();
    }

    private static string SanitizeRemoteRejectMessage(string message)
    {
        const int maximumCharacters = 512;
        const int maximumLineBreaks = 8;
        StringBuilder safe = new(Math.Min(message.Length, maximumCharacters));
        int acceptedCharacters = 0;
        int acceptedLineBreaks = 0;
        bool previousWasLineBreak = false;
        foreach (char character in message)
        {
            if (acceptedCharacters >= maximumCharacters)
            {
                break;
            }

            if (character == '\r' || character == '\n')
            {
                if (!previousWasLineBreak &&
                    acceptedLineBreaks < maximumLineBreaks)
                {
                    safe.Append('\n');
                    acceptedCharacters++;
                    acceptedLineBreaks++;
                }

                previousWasLineBreak = true;
                continue;
            }

            previousWasLineBreak = false;

            UnicodeCategory category = char.GetUnicodeCategory(character);
            bool unsafeFormatting =
                char.IsControl(character) ||
                char.IsSurrogate(character) ||
                category == UnicodeCategory.Format ||
                category == UnicodeCategory.LineSeparator ||
                category == UnicodeCategory.ParagraphSeparator;
            char normalized = unsafeFormatting ? ' ' : character;
            switch (normalized)
            {
                case '&':
                    safe.Append("&amp;");
                    break;
                case '<':
                    safe.Append("&lt;");
                    break;
                case '>':
                    safe.Append("&gt;");
                    break;
                default:
                    safe.Append(normalized);
                    break;
            }

            acceptedCharacters++;
        }

        return safe.ToString();
    }

    private static ClientConnection GetOrCreateClient(ZRpc rpc)
    {
        if (_client != null && ReferenceEquals(_client.Rpc, rpc))
        {
            return _client;
        }

        _client = CreateClientConnection(rpc, ZNet.instance);
        return _client;
    }

    private static void BeginClientManifestPreparation(ZNet network)
    {
        if (ReferenceEquals(_manifestPreparationNetwork, network)) return;
        CancelPendingManifestPreparation();
        CancelClientManifestPreparation(_client);
        _manifestPreparationNetwork = network;
        _pendingManifestPreparation = PluginManifestScanner.BeginCurrent(_integrityLimits);
    }

    private static ClientConnection CreateClientConnection(ZRpc rpc, ZNet? network)
    {
        CancelClientManifestPreparation(_client);
        PluginManifestScanner.Preparation? preparation = null;
        if (network != null && ReferenceEquals(_manifestPreparationNetwork, network))
        {
            preparation = _pendingManifestPreparation;
            _pendingManifestPreparation = null;
        }
        else
        {
            CancelPendingManifestPreparation();
        }

        return new ClientConnection(rpc)
        {
            Network = network,
            ManifestPreparation = preparation ?? PluginManifestScanner.BeginCurrent(_integrityLimits)
        };
    }

    private static void CancelPendingManifestPreparation()
    {
        PluginManifestScanner.Preparation? preparation = _pendingManifestPreparation;
        _pendingManifestPreparation = null;
        _manifestPreparationNetwork = null;
        preparation?.Dispose();
    }

    private static void CancelClientManifestPreparation(ClientConnection? session)
    {
        if (session == null) return;
        PluginManifestScanner.Preparation? preparation = session.ManifestPreparation;
        session.ManifestPreparation = null;
        session.ManifestResponseLimits = null;
        preparation?.Dispose();
    }

    private static ServerIntegrityService EnsureIntegrityService()
    {
        return _integrityService ??=
            new ServerIntegrityService(
                ServerManagerPlugin.DataRoot,
                _integrityLimits,
                ServerManagerPlugin.Log);
    }

    private static void EnsureCharacterCodecs()
    {
        _characterEnvelopeCodec ??=
            new CharacterEnvelopeCodec(_clientCharacterOptions);
        _profileCodec ??=
            new ValheimPlayerProfileCodec(_clientCharacterOptions);
    }

    private static CharacterSnapshotService EnsureServerCharacterService()
    {
        if (_serverCharacterService != null)
        {
            return _serverCharacterService;
        }

        CharacterStorageLayout layout =
            new(ServerManagerPlugin.CharacterRoot);
        CharacterStorageKeyProvider keys =
            new(layout);
        try
        {
            CharacterSemanticPolicy storedSnapshotPolicy =
                _storedSnapshotSemanticPolicy ??=
                    CreateCharacterSemanticPolicy(
                        CharacterSemanticPolicyMode.Observe);
            CharacterSemanticPolicy incomingSavePolicy =
                _incomingSaveSemanticPolicy ??=
                    CreateCharacterSemanticPolicy(
                        CharacterSemanticPolicyMode.Enforce);
            CharacterEnvelopeCodec serverEnvelopeCodec =
                new(_serverCharacterOptions);
            ValheimPlayerProfileCodec serverProfileCodec =
                new(_serverCharacterOptions);
            CharacterSemanticRevisionValidator storedSnapshotValidator =
                new(
                    serverProfileCodec,
                    new CharacterSemanticEvaluator(storedSnapshotPolicy));
            CharacterSemanticRevisionValidator incomingSaveValidator =
                new(
                    serverProfileCodec,
                    new CharacterSemanticEvaluator(incomingSavePolicy),
                    IsCharacterPolicyAdmin);
            CharacterRepository repository =
                new(
                    layout,
                    _serverCharacterOptions,
                    serverProfileCodec,
                    incomingSaveValidator,
                    IsCharacterCreationAdmin);
            repository.ValidateStorageKeyMappings(keys);
            try
            {
                repository.EnsureRestoreInstructions();
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Could not update characters/HowToRestore.txt; character storage remains available: " +
                    exception.Message);
            }
            _serverCharacterService = new CharacterSnapshotService(
                _serverCharacterOptions,
                new CharacterPeerIdentityResolver(),
                keys,
                serverEnvelopeCodec,
                serverProfileCodec,
                repository,
                semanticValidator: storedSnapshotValidator);
            _serverCharacterService.ApplyServerSettings(CurrentServerSettings);
            ServerManagerPlugin.Log.LogInfo(
                "Character state policy initialized: storedSnapshotMode=" +
                storedSnapshotPolicy.Mode +
                ", incomingSaveMode=" +
                incomingSavePolicy.Mode +
                ", forbiddenPrefabs=" +
                incomingSavePolicy.ForbiddenItemPrefabCount +
                ", skillProgressObserve=true.");
            return _serverCharacterService;
        }
        catch
        {
            keys.Dispose();
            throw;
        }
    }

    private static CharacterSemanticPolicy CreateCharacterSemanticPolicy(
        CharacterSemanticPolicyMode mode)
    {
        return CharacterSemanticPolicy.FromSettings(CurrentServerSettings, mode);
    }
}
