using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ServerManager.Events;
using ServerManager.PlayerLogging;
using Steamworks;

namespace ServerManager;

/// <summary>
/// Main-thread character adapter for the Steam listen host. The caller must
/// latch hosted-multiplayer intent before temporarily closing the listener;
/// standalone single-player must never call Prepare. No peer/RPC is fabricated.
/// </summary>
internal static class LocalHostCharacterRuntime
{
    private static readonly long InventoryCoalesceTicks = Math.Max(1, Stopwatch.Frequency / 4);
    private static readonly long InventoryIntervalTicks = Math.Max(1, Stopwatch.Frequency);
    private static readonly long FullSafetyIntervalTicks = 30 * Stopwatch.Frequency;
    private static readonly long HeartbeatIntervalTicks = 300 * Stopwatch.Frequency;
    private static HostState? _active;
    private static Restoration? _pendingRestoration;
    private static int _operationDepth;
    private static int _playerLoadDepth;

    internal static bool IsActive => _active != null && IsCurrentWorld(_active);

    internal static bool CanPersistCharacterPoison(Player player, bool restoring)
    {
        HostState? state = _active;
        return state != null && IsCurrentWorld(state) && player != null &&
            ReferenceEquals(player, Player.m_localPlayer) &&
            (!restoring || !state.Session.BackupOnly) &&
            ReferenceEquals(ValheimPrivateAccess.GetGamePlayerProfile(state.Game), state.ManagedProfile) &&
            player.GetPlayerID() == state.Session.PlayerId &&
            string.Equals(player.GetPlayerName(), state.Session.Identity.CharacterName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs after storage validation and Game.Awake, before any spawn/world side effects.
    /// Failure is terminal for startup: the caller must return to the lobby
    /// without saving, not continue with the selected local character.
    /// </summary>
    internal static void Prepare(
        Game game,
        CharacterSnapshotService service,
        ValheimPlayerProfileCodec codec)
    {
        if (game == null) throw new ArgumentNullException(nameof(game));
        if (service == null) throw new ArgumentNullException(nameof(service));
        if (codec == null) throw new ArgumentNullException(nameof(codec));
        ZNet network = ZNet.instance;
        if (network == null || !network.IsServer() || network.IsDedicated() ||
            ZNet.m_onlineBackend != OnlineBackendType.Steamworks ||
            !ReferenceEquals(Game.instance, game))
        {
            throw new CharacterProtocolException(
                "A local authoritative character requires an active Steam listen host.");
        }

        HostState? current = _active;
        if (current != null)
        {
            if (ReferenceEquals(current.Game, game) &&
                ReferenceEquals(current.Network, network) &&
                ReferenceEquals(current.Service, service))
            {
                EnsureSession(current);
                return;
            }
            Close();
        }
        Restore();
        if (Player.m_localPlayer != null ||
            (_pendingRestoration != null &&
             ReferenceEquals(_pendingRestoration.Game, game)))
        {
            throw new CharacterProtocolException(
                "The host character must be prepared before the local player spawns.");
        }

        PlayerProfile original = ValheimPrivateAccess.GetGamePlayerProfile(game) ??
            throw new CharacterProtocolException("The selected host character is unavailable.");
        string selectedName = original.GetName();
        string canonicalName = CharacterNamePolicy.NormalizeAndValidate(selectedName);
        if (!string.Equals(selectedName, canonicalName, StringComparison.Ordinal))
        {
            throw new CharacterProtocolException(
                "The selected host character name is not canonical.");
        }
        CharacterIdentity identity = new(GetLocalAccountId(), canonicalName);
        // Capture the admission mode once. A settings reload must not change
        // the source of an already prepared or active host session.
        bool backupOnly = !ServerManagerRuntime.CurrentServerSettings.LoadServerCharacterOnJoin;
        Guid openedSessionId = Guid.Empty;
        PlayerProfile? managed = null;
        ++_operationDepth;
        try
        {
            CharacterSessionOpenResult opened = backupOnly
                ? service.OpenBackupLocalHostSession(identity, codec.SerializeProfileToBytes(original))
                : service.OpenOrCreateLocalHostSession(identity);
            openedSessionId = opened.Snapshot.SessionId;
            if (!service.TryGetLocalHostSession(openedSessionId, out CharacterSession? session) ||
                session == null || !session.IsLocalHost || session.Rpc != null ||
                !session.Identity.EqualsIdentity(identity))
            {
                throw new CharacterProtocolException("The host character session is inconsistent.");
            }
            ValidateSnapshot(opened.Snapshot, session);
            // Local saves use the selected vanilla slot independently of server
            // admission. The server still validates its own revision stream.
            managed = codec.DeserializeProfileFromBytes(
                opened.Snapshot.PayloadUnsafe, original.GetFilename(), original.m_fileSource);
            ValidateProfile(managed, session);
            bool materialized = ValheimPrivateAccess.GetPlayerData(managed) != null;
            if (!session.BackupOnly && LocalCharacterFirstJoinGuard.ShouldRejectUsedLocalFirstJoin(
                    opened.Snapshot,
                    ValheimPrivateAccess.GetWorldDataCount(original) != 0,
                    materialized))
            {
                ServerManagerRuntime.RecordLocalHostConnectionRejection(game, identity,
                    "fresh_local_character_required", "The selected host character has world history and the server requires a fresh local character.");
                throw new CharacterProtocolException(
                    "This selected character has world history, but the server has no " +
                    "authoritative character for it. Have the administrator place its .fch in the matching SteamID subfolder under characters while the server is stopped, or " +
                    "select a new character; the original was not replaced.")
                    .WithPlayerMessage("sm_fresh_character_required");
            }

            if (!session.BackupOnly)
                codec.PreserveInitialAppearance(opened.Snapshot, original, managed);
            ValheimPrivateAccess.SetGamePlayerProfile(game, managed);
            if (opened.PendingInitialCommit &&
                !service.FinalizePendingLocalHostSnapshot(openedSessionId))
            {
                throw new CharacterStorageException(
                    "The pending host character snapshot could not be finalized.");
            }
            CharacterEnvelope accepted = service.GetLocalHostSnapshot(openedSessionId);
            ValidateSnapshot(accepted, session);
            HostState state = new(game, network, service, codec, session,
                managed, accepted, materialized);
            _active = state;
            LogObservations(state, opened.AuditFindings.AuditObservations, stored: true);
            ServerManagerRuntime.RecordCharacterStatLimits(null,
                state.Session.Identity.AccountId, state.Session.Identity.CharacterName,
                opened.StatLimitFindings, session.BackupOnly ? "incoming_host" : "stored_host");
        }
        catch (Exception failure)
        {
            if (!IntegrityCanonical.IsFatal(failure))
                ServerManagerRuntime.RecordLocalHostConnectionRejection(game, identity,
                    "character_open_failed", failure.GetType().Name + ": " + failure.Message);
            _active = null;
            try
            {
                if (managed != null)
                {
                    _pendingRestoration = new Restoration(game, managed, original);
                    Restore();
                }
            }
            finally
            {
                if (openedSessionId != Guid.Empty)
                {
                    try { service.CloseLocalHostSession(openedSessionId); }
                    catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                    {
                        LogWarning("The refused host character session could not be closed cleanly. " +
                            exception.Message);
                    }
                }
            }
            throw;
        }
        finally
        {
            --_operationDepth;
        }
    }

    internal static void Tick()
    {
        Restore();
        HostState? state = _active;
        if (state == null || IsSuppressed) return;
        if (!IsCurrentWorld(state))
        {
            Close();
            return;
        }
        if (Player.m_localPlayer == null) return;

        long now = Stopwatch.GetTimestamp();
        try
        {
            Player player = RequirePlayer(state);
            NotifyReady(state);
            if (!state.PlayerObserved)
            {
                state.PlayerObserved = true;
                // Materialize the spawned player's first full state before
                // admitting inventory-only replacements, even on a fresh join.
                state.FullDue = now + InventoryCoalesceTicks;
            }
            bool heartbeatDue = now >= state.HeartbeatDue;
            if (heartbeatDue || (state.FullDue != 0 && now >= state.FullDue))
            {
                // A failed capture still consumes this deadline; otherwise a
                // malformed/modded profile would be recaptured every frame.
                if (heartbeatDue) AdvanceHeartbeat(state, now);
                CaptureFull(state, player, alreadyCaptured: false);
            }
            else if (state.InventoryFastPathReady && state.InventoryDue != 0 &&
                     now >= state.InventoryDue)
            {
                CaptureInventory(state, player);
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            Warn(state, "The host character capture was not accepted; the last " +
                "accepted state is retained. " + exception.Message);
        }
    }

    internal static void AfterGameSave(Game game)
    {
        HostState? state = _active;
        if (state == null || IsSuppressed || !ReferenceEquals(state.Game, game) ||
            !IsCurrentWorld(state) || Player.m_localPlayer == null) return;
        try
        {
            CaptureFull(state, RequirePlayer(state), alreadyCaptured: true);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            Warn(state, "The host character save was not accepted; the last " +
                "accepted state is retained. " + exception.Message);
        }
    }

    internal static void AfterInventoryChanged(Inventory inventory)
    {
        HostState? state = _active;
        Player player = Player.m_localPlayer;
        if (state == null || IsSuppressed || !IsCurrentWorld(state) || player == null ||
            !ReferenceEquals(player.GetInventory(), inventory)) return;
        long now = Stopwatch.GetTimestamp();
        if (state.InventoryDue == 0)
            state.InventoryDue = Math.Max(now + InventoryCoalesceTicks, state.NextInventoryAllowed);
        if (!state.InventoryFastPathReady) ScheduleFull(state, now);
    }

    internal static bool BeforePlayerLoad(Player player)
    {
        if (_active == null || player == null ||
            !ReferenceEquals(player, Player.m_localPlayer)) return false;
        ++_playerLoadDepth;
        return true;
    }

    internal static void AfterPlayerLoad(bool entered)
    {
        if (entered && _playerLoadDepth > 0) --_playerLoadDepth;
    }

    /// <summary>
    /// Called before the shared service freezes its world checkpoint batch.
    /// A rejection must never cancel a world save or stop the host's gameplay.
    /// </summary>
    internal static bool CaptureForWorldCheckpoint()
    {
        HostState? state = _active;
        if (state == null || IsSuppressed || !IsCurrentWorld(state) ||
            Player.m_localPlayer == null) return false;
        try
        {
            return CaptureFull(state, RequirePlayer(state), alreadyCaptured: false);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            Warn(state, "The host character checkpoint capture failed; the world " +
                "checkpoint retains the last accepted character. " + exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Definitive shutdown only. This does not capture a candidate or save the
    /// world; the caller must capture before Game.SavePlayerProfile/checkpoint.
    /// </summary>
    internal static void Close()
    {
        HostState? state = _active;
        _active = null;
        _playerLoadDepth = 0;
        if (state == null)
        {
            Restore();
            return;
        }
        ++_operationDepth;
        try
        {
            state.Service.CloseLocalHostSession(state.Session.SessionId);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            Warn(state, "The host character session could not be closed cleanly. " +
                exception.Message, force: true);
        }
        finally
        {
            // Keep the current profile bound to the selected slot. Restoring
            // an older profile could roll back a later local save.
            --_operationDepth;
            Restore();
        }
    }

    /// <summary>Never swaps profiles underneath a still-live local player.</summary>
    internal static void Restore()
    {
        Restoration? pending = _pendingRestoration;
        if (pending == null) return;
        if (pending.Game == null || !ReferenceEquals(Game.instance, pending.Game))
        {
            _pendingRestoration = null;
            return;
        }
        if (Player.m_localPlayer != null) return;
        try
        {
            if (ReferenceEquals(ValheimPrivateAccess.GetGamePlayerProfile(pending.Game),
                    pending.ManagedProfile))
                ValheimPrivateAccess.SetGamePlayerProfile(pending.Game, pending.SelectedProfile);
            _pendingRestoration = null;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // Keep the bounded pending restoration so the next safe lifecycle
            // callback can retry, rather than forgetting the user's selection.
            long now = Stopwatch.GetTimestamp();
            if (pending.NextWarningAllowed <= now)
            {
                pending.NextWarningAllowed = now + FullSafetyIntervalTicks;
                LogWarning("The host character selection could not yet be restored. " +
                    exception.Message);
            }
        }
    }

    private static bool IsSuppressed => _operationDepth != 0 || _playerLoadDepth != 0;

    private static bool IsCurrentWorld(HostState state) =>
        state.Game != null && state.Network != null &&
        ReferenceEquals(Game.instance, state.Game) &&
        ReferenceEquals(ZNet.instance, state.Network) &&
        state.Network.IsServer() && !state.Network.IsDedicated() &&
        ZNet.m_onlineBackend == OnlineBackendType.Steamworks;

    private static string GetLocalAccountId() =>
        CharacterPeerIdentityResolver.CreateCanonicalAccountId(
            SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture),
            OnlineBackendType.Steamworks);

    private static void EnsureSession(HostState state)
    {
        if (!state.Service.TryGetLocalHostSession(state.Session.SessionId,
                out CharacterSession? current) || !ReferenceEquals(current, state.Session) ||
            state.Session.CurrentRevision != state.Accepted.Revision ||
            !ReferenceEquals(ValheimPrivateAccess.GetGamePlayerProfile(state.Game),
                state.ManagedProfile))
            throw new CharacterProtocolException("The authoritative host session is no longer current.");
        ValidateProfile(state.ManagedProfile, state.Session);
    }

    private static Player RequirePlayer(HostState state)
    {
        EnsureSession(state);
        Player player = Player.m_localPlayer;
        if (player == null || player.GetPlayerID() != state.Session.PlayerId ||
            !string.Equals(player.GetPlayerName(), state.Session.Identity.CharacterName,
                StringComparison.Ordinal) ||
            SteamUser.GetSteamID().m_SteamID != state.SteamId)
            throw new CharacterProtocolException("The local player does not match the host character session.");
        return player;
    }

    private static void ValidateProfile(PlayerProfile profile, CharacterSession session)
    {
        if (profile.GetPlayerID() != session.PlayerId ||
            !string.Equals(profile.GetName(), session.Identity.CharacterName,
                StringComparison.Ordinal))
            throw new CharacterProtocolException("The host profile identity does not match its session.");
    }

    private static void ValidateSnapshot(CharacterEnvelope snapshot, CharacterSession session)
    {
        if (snapshot.Kind != CharacterEnvelopeKind.Snapshot ||
            snapshot.SessionId != session.SessionId || snapshot.Revision <= 0 ||
            snapshot.Revision != session.CurrentRevision ||
            snapshot.ValheimProfileVersion != ValheimPlayerProfileCodec.SupportedPlayerProfileVersion ||
            !string.Equals(snapshot.AccountId, session.Identity.AccountId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.CharacterName, session.Identity.CharacterName, StringComparison.Ordinal))
            throw new CharacterProtocolException("The accepted host snapshot is inconsistent.");
    }

    private static bool CaptureFull(HostState state, Player player, bool alreadyCaptured)
    {
        long now = Stopwatch.GetTimestamp();
        state.PlayerObserved = true;
        state.FullDue = 0;
        state.NextFullAllowed = now + FullSafetyIntervalTicks;
        state.InventoryDue = 0;
        state.NextInventoryAllowed = now + InventoryIntervalTicks;
        ++_operationDepth;
        try
        {
            NotifyReady(state);
            if (!alreadyCaptured)
            {
                state.ManagedProfile.SavePlayerData(player);
                Minimap.instance?.SaveMapData();
                // A direct world checkpoint may not pass through vanilla's
                // Game.SavePlayerProfile. Capture its guarded logout position
                // too, so the next spawn matches this accepted world boundary.
                state.ManagedProfile.SaveLogoutPoint();
            }
            byte[] payload = state.Codec.SerializeProfileToBytes(state.ManagedProfile);
            bool accepted = Submit(state, CharacterEnvelopeKind.SaveRequest, payload);
            if (!accepted) ScheduleFull(state, now);
            return accepted;
        }
        catch
        {
            // Retry independently of further inventory changes, but at the
            // full-profile safety interval, never once per frame.
            ScheduleFull(state, now);
            throw;
        }
        finally
        {
            --_operationDepth;
        }
    }

    private static void CaptureInventory(HostState state, Player player)
    {
        long now = Stopwatch.GetTimestamp();
        state.InventoryDue = 0;
        state.NextInventoryAllowed = now + InventoryIntervalTicks;
        ++_operationDepth;
        try
        {
            byte[] payload = state.Codec.CaptureInventoryToBytes(player.GetInventory());
            if (!Submit(state, CharacterEnvelopeKind.InventorySaveRequest, payload))
                ScheduleFull(state, now);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ScheduleFull(state, now);
            Warn(state, "The host inventory fast save failed; a bounded full-profile " +
                "save will be attempted. " + exception.Message);
        }
        finally
        {
            --_operationDepth;
        }
    }

    private static bool Submit(HostState state, CharacterEnvelopeKind kind, byte[] payload)
    {
        long baseRevision = state.Accepted.Revision;
        CharacterEnvelope request = CharacterEnvelope.Create(kind,
            checked(baseRevision + 1), baseRevision, state.Session.SessionId,
            state.Session.Identity, DateTime.UtcNow,
            ValheimPlayerProfileCodec.SupportedPlayerProfileVersion, payload);
        // Direct synchronous admission shares all remote validation and RAM
        // shadow/checkpoint rules, without networking or fabricated acknowledgements.
        CharacterSaveResult result = state.Service.HandleLocalHostSaveRequest(
            state.Session.SessionId, request);
        if (!result.Accepted)
        {
            ServerEventRuntime.RecordCharacterSaveRejected(
                state.Session.Identity.AccountId, state.Session.Identity.CharacterName,
                "incoming_host", result);
            LogObservations(state, result.AuditFindings.AuditObservations, stored: false);
            Warn(state, "The host character save was rejected; the last accepted " +
                "state is retained. " + result.Error);
            NotifyRejected(state, result);
            return false;
        }
        CharacterEnvelope response = result.Response;
        if (response.Kind != CharacterEnvelopeKind.SaveAccepted ||
            response.SessionId != state.Session.SessionId ||
            response.BaseRevision != baseRevision || response.Revision != request.Revision ||
            !string.Equals(response.AccountId, state.Session.Identity.AccountId, StringComparison.Ordinal) ||
            !string.Equals(response.CharacterName, state.Session.Identity.CharacterName, StringComparison.Ordinal))
            throw new CharacterProtocolException("The host save acceptance is inconsistent.");

        CharacterEnvelope accepted = state.Service.GetLocalHostSnapshot(state.Session.SessionId);
        ValidateSnapshot(accepted, state.Session);
        if (accepted.Revision != response.Revision)
            throw new CharacterProtocolException("The host accepted snapshot revision is inconsistent.");
        // Update the approved server base before any optional logging.
        // Inventory admission returns the service's complete materialized full
        // profile, so no second local splice can diverge from the server shadow.
        state.Accepted = accepted;
        state.InventoryFastPathReady = true;
        LogObservations(state, result.AuditFindings.AuditObservations, stored: false);
        ServerManagerRuntime.RecordCharacterStatLimits(null,
            state.Session.Identity.AccountId, state.Session.Identity.CharacterName,
            result.StatLimitFindings, "incoming_host");
        NotifyAccepted(state, result);
        return true;
    }

    private static void ScheduleFull(HostState state, long now)
    {
        if (state.FullDue == 0)
            state.FullDue = Math.Max(now + InventoryCoalesceTicks, state.NextFullAllowed);
    }

    private static void AdvanceHeartbeat(HostState state, long now)
    {
        long elapsedIntervals = Math.Max(0, (now - state.HeartbeatDue) / HeartbeatIntervalTicks);
        state.HeartbeatDue += (elapsedIntervals + 1) * HeartbeatIntervalTicks;
    }

    private static void NotifyReady(HostState state)
    {
        try { PlayerActivityRuntime.OnLocalHostCharacterReady(state.Session); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { Warn(state, "Host character activity logging was unavailable. " + exception.Message); }
    }

    private static void NotifyAccepted(HostState state, CharacterSaveResult result)
    {
        try { PlayerActivityRuntime.OnLocalHostCharacterShadowAccepted(state.Session, result); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { Warn(state, "Accepted host character activity logging failed. " + exception.Message); }
    }

    private static void NotifyRejected(HostState state, CharacterSaveResult result)
    {
        try { PlayerActivityRuntime.OnLocalHostCharacterSaveRejected(state.Session, result); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { Warn(state, "Rejected host character activity logging failed. " + exception.Message); }
    }

    internal static void RecordStatLimitObservation(DetectionEvidence evidence, string detail)
    {
        HostState? state = _active;
        if (state == null || !IsCurrentWorld(state)) return;
        ServerEventRuntime.RecordSecurityEvent(
            ServerManagerEventKinds.SecurityDetection,
            state.Session.Identity.AccountId, state.Session.Identity.CharacterName,
            "incoming_host", evidence.ToString(), DetectionAction.Log,
            "observed", detail);
    }

    private static void LogObservations(
        HostState state, IReadOnlyList<CharacterAuditObservation> observations, bool stored)
    {
        ServerEventRuntime.RecordCharacterObservations(
            state.Session.Identity.AccountId,
            state.Session.Identity.CharacterName,
            state.Accepted.Revision,
            stored && !state.Session.BackupOnly ? "stored_host" : "incoming_host",
            observations);
    }

    private static void Warn(HostState state, string message, bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && state.NextWarningAllowed > now) return;
        state.NextWarningAllowed = now + FullSafetyIntervalTicks;
        LogWarning(message);
    }

    private static void LogWarning(string message)
    {
        try { ServerManagerPlugin.Log.LogWarning(message); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { }
    }

    private sealed class HostState
    {
        internal HostState(Game game, ZNet network, CharacterSnapshotService service,
            ValheimPlayerProfileCodec codec, CharacterSession session,
            PlayerProfile managed, CharacterEnvelope accepted, bool materialized)
        {
            if (!CharacterSteamIdentity.TryParseCanonicalAccountId(
                    session.Identity.AccountId, out ulong steamId))
                throw new CharacterProtocolException("The host session Steam identity is not canonical.");
            // The canonical session identity is immutable. Keep its validated
            // value while still reading the current Steam account on every capture check.
            SteamId = steamId;
            Game = game;
            Network = network;
            Service = service;
            Codec = codec;
            Session = session;
            ManagedProfile = managed;
            Accepted = accepted;
            InventoryFastPathReady = materialized && !accepted.RequiresFreshLocalCharacter;
            byte[] jitter = session.SessionId.ToByteArray();
            HeartbeatDue = Stopwatch.GetTimestamp() + HeartbeatIntervalTicks -
                (jitter[0] % 31) * Stopwatch.Frequency;
        }

        internal readonly Game Game;
        internal readonly ZNet Network;
        internal readonly CharacterSnapshotService Service;
        internal readonly ValheimPlayerProfileCodec Codec;
        internal readonly CharacterSession Session;
        internal readonly ulong SteamId;
        internal readonly PlayerProfile ManagedProfile;
        internal CharacterEnvelope Accepted;
        internal bool InventoryFastPathReady;
        internal bool PlayerObserved;
        internal long HeartbeatDue;
        internal long FullDue;
        internal long NextFullAllowed;
        internal long InventoryDue;
        internal long NextInventoryAllowed;
        internal long NextWarningAllowed;
    }

    private sealed class Restoration
    {
        internal Restoration(Game game, PlayerProfile managed, PlayerProfile selected)
        {
            Game = game;
            ManagedProfile = managed;
            SelectedProfile = selected;
        }

        internal readonly Game Game;
        internal readonly PlayerProfile ManagedProfile;
        internal readonly PlayerProfile SelectedProfile;
        internal long NextWarningAllowed;
    }
}
