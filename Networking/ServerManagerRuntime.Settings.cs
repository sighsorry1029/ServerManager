using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ServerManager.Commands;
using Steamworks;

namespace ServerManager;

internal static partial class ServerManagerRuntime
{
    private static ServerSettingsReloadService? _settingsReload;
    private static ItemDataPresetStore? _itemDataPresets;
    private static ServerSettings? _currentServerSettings;
    private static ZNet? _settingsDeferredOpenNetwork;
    private static Game? _settingsDeferredOpenGame;
    private static int _settingsLifecycleGeneration;
    private static int _settingsDeferredOpenGeneration;
    private static bool _localHostWaitingForSettings;
    private static readonly long PolicyAcknowledgementTimeoutTicks = 10L * Stopwatch.Frequency;

    // Clients never read the server YAML. Server consumers must pass readiness
    // before accessing this snapshot; defaults are only used to construct codecs.
    internal static ServerSettings CurrentServerSettings => _currentServerSettings ??
        throw new InvalidOperationException("The server settings have not been loaded.");

    // Admission is already gated on settings readiness. Steam may advertise
    // earlier during startup, so it uses the same default until settings load.
    internal static int GetServerPlayerLimit() =>
        _currentServerSettings?.MaxPlayers ?? ServerSettings.Defaults.MaxPlayers;

    internal static void RefreshServerPlayerLimitAdvertisement()
    {
        ZNet? server = ZNet.instance;
        if (server == null || !server.IsServer() ||
            ZNet.m_onlineBackend != OnlineBackendType.Steamworks) return;
        try
        {
            if (server.IsDedicated())
            {
                // A listen host initializes SteamAPI, not the GameServer API.
                // Never initialize either API ourselves or probe it per frame.
                if (GameServer.GetHSteamPipe() != (HSteamPipe)0)
                    SteamGameServer.SetMaxPlayerCount(GetServerPlayerLimit());
                return;
            }
            if (!SteamManager.Initialized || ZSteamMatchmaking.instance == null) return;
            CSteamID lobby = ValheimPrivateAccess.GetSteamServerLobby(ZSteamMatchmaking.instance);
            if (lobby != CSteamID.Nil &&
                !SteamMatchmaking.SetLobbyMemberLimit(lobby, GetServerPlayerLimit()))
                ServerManagerPlugin.Log.LogWarning(
                    "Steam could not update the advertised player limit. The configured admission limit remains active.");
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // Advertising is best-effort; a native/listing failure must not
            // partially roll back an otherwise valid YAML policy snapshot.
            ServerManagerPlugin.Log.LogWarning(
                "Steam player-limit advertisement could not be updated: " + exception.Message);
        }
    }

    private static void ResetServerSettings()
    {
        unchecked { ++_settingsLifecycleGeneration; }
        _settingsDeferredOpenNetwork = null;
        _settingsDeferredOpenGame = null;
        _localHostWaitingForSettings = false;
        _settingsReload?.Dispose();
        _settingsReload = null;
        _currentServerSettings = null;
        _itemDataPresets?.Dispose();
        _itemDataPresets = null;
    }

    private static bool EnsureServerSettingsReady()
    {
        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer() ||
            !ReferenceEquals(_preparedServerNetwork, network) || _serverDataRootStartupFailed)
            return false;
        _settingsReload ??= new ServerSettingsReloadService(
            message => ServerManagerPlugin.Log.LogInfo(message),
            message => ServerManagerPlugin.Log.LogWarning(message));
        return _settingsReload.EnsureLoaded(ServerDataRoot.ActivePath, ApplyServerSettings);
    }

    private static void TickServerSettings()
    {
        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer() ||
            !ReferenceEquals(_preparedServerNetwork, network) || _serverDataRootStartupFailed)
            return;
        try
        {
            EnsureServerSettingsReady();
            EnsureItemDataPresetsReady();
            ResumeSettingsDeferredServerOpen();
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning("Server settings reload could not be checked: " + exception.Message);
        }
    }

    private static bool EnsureItemDataPresetsReady()
    {
        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer() ||
            !ReferenceEquals(_preparedServerNetwork, network) || _serverDataRootStartupFailed)
            return false;
        _itemDataPresets ??= new ItemDataPresetStore(
            message => ServerManagerPlugin.Log.LogInfo(message),
            message => ServerManagerPlugin.Log.LogWarning(message));
        return _itemDataPresets.EnsureLoaded(ServerDataRoot.ActivePath);
    }

    private static void DeferServerOpenForSettings(ZNet network)
    {
        // This method is called only by the actual OpenServer prefix. Merely
        // loading a valid file must never open a listener that was not requested.
        if (!ReferenceEquals(_preparedServerNetwork, network) ||
            !network.IsServer() || !ValheimPrivateAccess.GetOpenServer()) return;
        _settingsDeferredOpenNetwork = network;
        _settingsDeferredOpenGame = Game.instance;
        _settingsDeferredOpenGeneration = _settingsLifecycleGeneration;
    }

    private static void ResumeSettingsDeferredServerOpen()
    {
        ZNet? deferred = _settingsDeferredOpenNetwork;
        if (deferred == null) return;
        if (!_initialized || _shuttingDown ||
            _settingsDeferredOpenGeneration != _settingsLifecycleGeneration ||
            !ReferenceEquals(deferred, ZNet.instance) ||
            !ReferenceEquals(deferred, _preparedServerNetwork) ||
            (_settingsDeferredOpenGame != null &&
             !ReferenceEquals(_settingsDeferredOpenGame, Game.instance)) ||
            !deferred.IsServer() || _serverDataRootStartupFailed ||
            _localHostStartupFailed ||
            _serverCharacterStorageStartupState == ServerCharacterStorageStartupState.Failed ||
            ZNet.m_onlineBackend != OnlineBackendType.Steamworks ||
            !ValheimPrivateAccess.GetOpenServer())
        {
            _settingsDeferredOpenNetwork = null;
            _settingsDeferredOpenGame = null;
            return;
        }
        if (_currentServerSettings == null) return;
        // Consume before invoking vanilla: every authentication, storage-validation,
        // host-profile and listener gate runs again, and no failure can loop.
        _settingsDeferredOpenNetwork = null;
        _settingsDeferredOpenGame = null;
        deferred.OpenServer();
    }

    private static bool ApplyServerSettings(ServerSettings settings)
    {
        // Retry the complete YAML edit after the immutable disk operation has
        // finished; do not race its backup policy or block the game thread.
        if (_serverCharacterService?.HasPendingCheckpointWrite == true) return false;
        if (!ValheimPlayerProfileCodec.IsStartItemsCatalogReady(settings)) return false;
        try { new ValheimPlayerProfileCodec(_serverCharacterOptions).ValidateStartItems(settings); }
        catch (InvalidDataException exception)
        {
            ServerManagerPlugin.Log.LogWarning("ServerManager.yml item policy was rejected: " + exception.Message);
            throw;
        }
        CharacterSemanticPolicy storedPolicy = CharacterSemanticPolicy.FromSettings(
            settings, CharacterSemanticPolicyMode.Observe);
        CharacterSemanticPolicy incomingPolicy = CharacterSemanticPolicy.FromSettings(
            settings, CharacterSemanticPolicyMode.Enforce);
        // Preflight every possibly throwing policy construction before mutating
        // the live service. No sessions, leases, detector history or checkpoints
        // are replaced when settings change.
        var updates = ServerDetectionStates.Values.Select(state => new
        {
            State = state,
            Policy = state.Policy.WithGameplayLimits(settings.MaximumCarryWeight, settings.MaximumDamage),
            Generation = checked(state.PolicyGeneration + 1)
        }).ToArray();
        _serverCharacterService?.ApplyServerSettings(settings);
        _serverCharacterOptions.MaxCharactersPerAccount = settings.MaxCharactersPerAccount;
        _serverCharacterOptions.MaxBackups = settings.BackupsPerProfile;
        bool? previousLoadServerCharacterOnJoin = _currentServerSettings?.LoadServerCharacterOnJoin;
        int? previousMaxPlayers = _currentServerSettings?.MaxPlayers;
        _currentServerSettings = settings;
        _storedSnapshotSemanticPolicy = storedPolicy;
        _incomingSaveSemanticPolicy = incomingPolicy;
        foreach (var update in updates)
        {
            update.State.Policy = update.Policy;
            update.State.CheatDetectionResponse = settings.CheatDetectionResponse;
            update.State.StatLimitResponse = settings.StatLimitResponse;
            update.State.PolicyGeneration = update.Generation;
        }
        if (previousLoadServerCharacterOnJoin != settings.LoadServerCharacterOnJoin)
            ServerManagerPlugin.Log.LogInfo(
                "Character loadServerCharacterOnJoin=" + settings.LoadServerCharacterOnJoin +
                "; this setting applies to new connections. Existing character sessions keep their admission mode.");
        if (previousMaxPlayers != settings.MaxPlayers)
        {
            ServerManagerPlugin.Log.LogInfo(
                "Server maxPlayers=" + settings.MaxPlayers +
                "; includes the listen host. Existing players are not kicked when the limit is lowered.");
            RefreshServerPlayerLimitAdvertisement();
        }
        return true;
    }

    private static void SynchronizeServerPolicies()
    {
        ZNet? server = ZNet.instance;
        if (server == null || !server.IsServer()) return;
        long now = Stopwatch.GetTimestamp();
        foreach (var pair in ServerDetectionStates.ToArray())
        {
            ServerDetectionState state = pair.Value;
            // Detection-only inbound filtering cannot receive PolicyAck.
            // Numeric kicks can later be cancelled by fresh admin membership,
            // so leave updates unsent until that gate is released. A new numeric
            // kick itself is admitted only while the current policy is ACKed.
            if (state.TerminalActionApplied || state.PendingKickTimestamp != 0 ||
                !_coordinator.IsReadyAndAuthenticated(pair.Key))
                continue;
            if (state.PolicyAcknowledgementDeadline != 0)
            {
                if (now >= state.PolicyAcknowledgementDeadline)
                {
                    state.PolicyAcknowledgementDeadline = 0;
                    SendServerRejection(pair.Key, new ProtocolRejection(
                        ProtocolRejectCode.HandshakeTimedOut,
                        "The server policy update was not acknowledged in time."));
                }
                continue;
            }
            if (state.AcknowledgedPolicyGeneration == state.PolicyGeneration) continue;
            try
            {
                // Only an actual update needs copies of the session ID/nonce.
                if (!_coordinator.TryGetSnapshot(pair.Key, out ConnectionSessionSnapshot session) ||
                    session.State != ConnectionSessionState.Ready || !session.PeerInfoAuthenticated)
                    continue;
                state.SentPolicyGeneration = state.PolicyGeneration;
                state.PolicyAcknowledgementDeadline = checked(now + PolicyAcknowledgementTimeoutTicks);
                SendProtocolOrThrow(pair.Key, ProtocolPacketCodec.CreatePolicyUpdate(
                    session.SessionId, session.Nonce, state.SentPolicyGeneration,
                    state.Policy.MaximumCarryWeight, state.Policy.MaximumDamage, _connectionLimits));
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                SendServerRejection(pair.Key, new ProtocolRejection(ProtocolRejectCode.InternalError,
                    "The server policy update could not be delivered."));
            }
        }
    }

    private static DetectionAction CurrentNumericLimitResponse(ServerDetectionState state)
    {
        // A hit/profile may have been sent under the old advertised limits.
        // Keep server-side blocking/observation active, but do not sanction the
        // numerical transition until this connection acknowledges its policy.
        return state.AcknowledgedPolicyGeneration == state.PolicyGeneration
            ? state.StatLimitResponse : DetectionAction.Log;
    }

    private static void HandleServerPolicyAck(ZNet server, ZRpc rpc, ProtocolPacket packet)
    {
        if (!_coordinator.TryGetSnapshot(rpc, out ConnectionSessionSnapshot session) ||
            session.State != ConnectionSessionState.Ready || !session.PeerInfoAuthenticated ||
            !ProtocolByteUtil.FixedTimeEquals(session.SessionId, packet.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(session.Nonce, packet.Nonce) ||
            !TryResolveActiveDetectionPeer(server, rpc, out _, out _) ||
            !ServerDetectionStates.TryGetValue(rpc, out ServerDetectionState state) ||
            state.PolicyAcknowledgementDeadline == 0 ||
            Stopwatch.GetTimestamp() >= state.PolicyAcknowledgementDeadline ||
            packet.Sequence != state.SentPolicyGeneration ||
            packet.Sequence <= state.AcknowledgedPolicyGeneration)
        {
            RejectDetectionProtocol(rpc, "The server policy acknowledgement was invalid.");
            return;
        }
        state.AcknowledgedPolicyGeneration = packet.Sequence;
        state.PolicyAcknowledgementDeadline = 0;
    }

    private static void HandleClientPolicyUpdate(ClientConnection session, ProtocolPacket packet)
    {
        if (session.Failed || !session.ReadyAcknowledgementSent ||
            session.SessionId == null || session.Nonce == null ||
            !ProtocolByteUtil.FixedTimeEquals(session.SessionId, packet.SessionId) ||
            !ProtocolByteUtil.FixedTimeEquals(session.Nonce, packet.Nonce) ||
            packet.Sequence <= session.PolicyGeneration)
        {
            FailClient(session.Rpc, "The server policy update was invalid.");
            return;
        }
        try
        {
            using MemoryStream stream = new(packet.Payload, false);
            using BinaryReader reader = new(stream);
            float carryWeight = reader.ReadSingle(), damage = reader.ReadSingle();
            ClientDetection.UpdateGameplayLimits(packet.Sequence, carryWeight, damage);
            CheatCommandGuard.UpdatePolicyGeneration(packet.Sequence);
            session.PolicyGeneration = packet.Sequence;
            SendProtocolOrThrow(session.Rpc, ProtocolPacketCodec.CreatePolicyAck(
                session.SessionId, session.Nonce, packet.Sequence, _connectionLimits));
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            FailClient(session.Rpc, "The server policy update could not be applied.", exception);
        }
    }
}
