#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ServerManager.Events;
using Steamworks;
using UnityEngine;

namespace ServerManager.PlayerLogging;

/// <summary>
/// Produces bounded per-player audit records from state the server already
/// observes. It deliberately adds no client telemetry RPC or trust claim.
/// </summary>
internal static class PlayerActivityRuntime
{
    private const int MaximumInventoryEntries = 512;
    private const int MaximumInventoryListingBytes = 48 * 1024;
    private const int MaximumInventoryDetailEntries = 256;
    private const int MaximumSkillEntries = 256;
    private const int MaximumLoggedReasonCharacters = 512;
    private const int MaximumLogMessageCharacters = 1024;
    private const int CoordinateIntervalSeconds = 300;
    // Remote reference positions need a short grace after joining, not the
    // full recurring log interval. Other activity records use live positions.
    private const int CoordinateInitialDelaySeconds = 5;
    private const int DamageCooldownSeconds = 10;
    private const int InventorySnapshotIntervalSeconds = 300;
    private const long MaximumLogFileBytes = 64L * 1024L * 1024L;
    private const int MaximumLogFilesPerPlayer = 30;
    private static readonly long DeathCorrelationTicks =
        10L * Stopwatch.Frequency;
    private static readonly long _coordinateIntervalTicks =
        SecondsToTicks(CoordinateIntervalSeconds);
    private static readonly long _coordinateInitialDelayTicks =
        SecondsToTicks(CoordinateInitialDelaySeconds);
    private static readonly long _damageCooldownTicks =
        SecondsToTicks(DamageCooldownSeconds);
    // This log timer remains independent of character heartbeat/ACK timing.
    private static readonly long _inventorySnapshotTicks =
        SecondsToTicks(InventorySnapshotIntervalSeconds);
    private static readonly Dictionary<ZRpc, ActivityPeerState> Peers = new();
    private static readonly Dictionary<int, string> ItemPrefabNames = new();

    private static PlayerTelemetryLogWriter? _writer;
    private static ActivityPeerState? _listenHost;
    private static ObjectDB? _itemPrefabNameSource;
    private static bool _started;
    private static long _nextSweepTimestamp;

    internal static void Start(string dataRoot)
    {
        if (_started)
        {
            throw new InvalidOperationException(
                "The player activity runtime is already active.");
        }

        if (_writer != null)
        {
            if (!_writer.Stop(TimeSpan.Zero))
            {
                throw new InvalidOperationException(
                    "The previous player activity writer is still draining.");
            }

            _writer.Dispose();
            _writer = null;
        }

        Peers.Clear();
        _listenHost = null;
        ResetItemPrefabNames();
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException(
                "A server data root is required for player activity logs.",
                nameof(dataRoot));
        }

        PlayerTelemetryLogOptions options = new(
            Path.Combine(dataRoot, "logs"))
        {
            MaximumFileBytes = MaximumLogFileBytes,
            MaximumFilesPerPlayer = MaximumLogFilesPerPlayer,
            ShutdownDrainTimeout = TimeSpan.FromSeconds(10)
        };

        PlayerTelemetryLogWriter writer = new();
        try
        {
            writer.Initialize(options, ReportDiagnostic);
            writer.Start();
        }
        catch
        {
            writer.Dispose();
            throw;
        }

        _writer = writer;
        _nextSweepTimestamp = Stopwatch.GetTimestamp();
        _started = true;
        ServerEventRuntime.AuthenticatedPlayerDeathPublished -=
            OnAuthenticatedPlayerDeath;
        ServerEventRuntime.AuthenticatedPlayerDeathPublished +=
            OnAuthenticatedPlayerDeath;

        ServerManagerPlugin.Log.LogInfo(
            "Per-player activity logging enabled at " +
            options.RootDirectory + ".");
    }

    internal static void Tick()
    {
        if (!_started)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (now < _nextSweepTimestamp)
        {
            return;
        }

        _nextSweepTimestamp = AddTicks(
            now,
            Math.Max(1L, Stopwatch.Frequency));
        ActivityPeerState? listenHost = EnsureListenHost(now);
        foreach (ActivityPeerState state in Peers.Values)
        {
            TickState(state, now);
        }

        if (listenHost != null)
        {
            TickState(listenHost, now);
        }
    }

    internal static void OnPlayerReady(
        ZRpc rpc,
        ServerPeerIdentity identity,
        CharacterSession? characterSession)
    {
        if (!_started || rpc == null || identity == null ||
            !identity.HasAuthenticatedIdentity || Peers.ContainsKey(rpc))
        {
            return;
        }

        if (!TryResolveAuthenticatedSteam64(rpc, identity, out string steamId))
        {
            // This callback is reached only after Ready passed the same final
            // authentication boundary, so a mismatch here is operationally
            // useful and should not disappear as a silent missing log.
            ServerManagerPlugin.Log.LogWarning(
                "Per-player activity log registration skipped because the " +
                "final Steam identity could not be revalidated.");
            return;
        }

        string characterName = characterSession?.Identity.CharacterName ??
                               identity.PlayerName;
        long now = Stopwatch.GetTimestamp();
        DateTime openedAtUtc = DateTime.UtcNow;
        ActivityPeerState state = new(
            steamId,
            characterName,
            characterSession?.PlayerId ?? 0L,
            identity.Peer,
            null,
            now,
            openedAtUtc);
        Peers.Add(rpc, state);
        TryEnsureLoginWritten(state);

        if (characterSession != null)
        {
            characterSession.CaptureCurrentSemanticState(
                out _,
                out CharacterSemanticSnapshot snapshot);
            state.LatestSemanticSnapshot = snapshot;
            WriteInventoryDetailSnapshot(state, snapshot, now);
        }
    }

    internal static void OnPeerDisconnected(ZRpc rpc)
    {
        if (!_started || rpc == null ||
            !Peers.TryGetValue(rpc, out ActivityPeerState state))
        {
            return;
        }

        Peers.Remove(rpc);
        CompleteState(state, "connection_closed");
    }

    internal static void ObserveRoutedDamage(
        ZRpc rpc,
        RoutedDamageObservation observation,
        bool blocked,
        bool attributedPlayerHit)
    {
        if (!_started || rpc == null || observation == null ||
            !Peers.TryGetValue(rpc, out ActivityPeerState sender))
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_listenHost == null)
        {
            EnsureListenHost(now);
        }

        DamageCounterpart target = ResolveRoutedTarget(observation);
        if (attributedPlayerHit && !blocked)
        {
            TryWriteDamage(
                sender,
                observation.Hit,
                outgoing: true,
                target,
                now);
        }

        if (target.PlayerState != null)
        {
            DamageCounterpart source = ResolveRoutedSource(
                sender,
                observation.Hit,
                attributedPlayerHit);
            AddIncomingDamage(
                target.PlayerState,
                source,
                observation.Hit,
                blocked,
                now);
        }
    }

    internal static void ObserveServerLocalDamage(
        object targetObject,
        HitData hit,
        bool blocked,
        bool attributedLocalPlayerHit)
    {
        if (!_started || hit == null)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        DamageCounterpart target = ResolveLocalTarget(targetObject);
        DamageCounterpart source;
        if (attributedLocalPlayerHit)
        {
            ActivityPeerState? listenHost = EnsureListenHost(now);
            if (listenHost == null)
            {
                source = new DamageCounterpart(
                    "player",
                    string.Empty,
                    "Player",
                    "listen_host_identity_unavailable",
                    null);
            }
            else
            {
                source = DamageCounterpart.ForPlayer(
                    listenHost,
                    "listen_host_local_player");
                if (!blocked)
                {
                    TryWriteDamage(
                        listenHost,
                        hit,
                        outgoing: true,
                        target,
                        now);
                }
            }
        }
        else
        {
            source = ResolveServerLocalSource(hit);
        }

        if (target.PlayerState != null)
        {
            AddIncomingDamage(
                target.PlayerState,
                source,
                hit,
                blocked,
                now);
        }
    }

    private static DamageCounterpart ResolveServerLocalSource(HitData hit)
    {
        ZDOID attacker = hit.m_attacker;
        if (attacker == ZDOID.None)
        {
            return DamageCounterpart.Environment;
        }

        if (TryFindPlayerByCharacterId(attacker, out _))
        {
            return new DamageCounterpart(
                "unattributed_player_reference",
                string.Empty,
                "Player",
                "server_local_player_reference",
                null);
        }

        if (TryResolveZdoPrefab(
                attacker,
                out string prefab,
                out string kind))
        {
            return new DamageCounterpart(
                kind,
                string.Empty,
                prefab,
                "server_local_attacker_zdo",
                null);
        }

        return new DamageCounterpart(
            "unknown",
            string.Empty,
            string.Empty,
            "server_local_unresolved_attacker_zdo",
            null);
    }

    internal static void ObserveListenHostDeath(EventClientReport report)
    {
        if (!_started || report == null ||
            report.Kind != EventClientReportKind.Death)
        {
            return;
        }

        ActivityPeerState? state = EnsureListenHost(Stopwatch.GetTimestamp());
        if (state == null)
        {
            return;
        }

        Dictionary<string, string> reportFields = new(StringComparer.Ordinal)
        {
            ["cause"] = Clip(report.Cause, 64),
            ["attacker"] = Clip(report.AttackerName, 96),
            ["attacker_prefab"] = Clip(report.AttackerPrefab, 128),
            ["attacker_is_player"] = report.AttackerIsPlayer
                ? "true"
                : "false"
        };
        WriteDeath(state, reportFields, DateTime.UtcNow);
    }

    internal static void OnCharacterShadowAccepted(
        ZRpc rpc,
        CharacterSaveResult result)
    {
        if (!_started || rpc == null || result == null || !result.Accepted ||
            !Peers.TryGetValue(rpc, out ActivityPeerState state))
        {
            return;
        }

        RecordAcceptedShadow(state, result);
    }

    internal static void OnLocalHostCharacterReady(CharacterSession session)
    {
        ActivityPeerState? state = GetManagedListenHost(session);
        if (state == null || state.ManagedCharacterSessionId == session.SessionId)
        {
            return;
        }

        session.CaptureCurrentSemanticState(out _, out CharacterSemanticSnapshot snapshot);
        state.ManagedCharacterSessionId = session.SessionId;
        state.LatestSemanticSnapshot = snapshot;
        long now = Stopwatch.GetTimestamp();
        WriteInventoryDetailSnapshot(state, snapshot, now);
    }

    internal static void OnLocalHostCharacterShadowAccepted(
        CharacterSession session,
        CharacterSaveResult result)
    {
        if (result == null || !result.Accepted) return;
        ActivityPeerState? state = GetManagedListenHost(session);
        if (state != null) RecordAcceptedShadow(state, result);
    }

    internal static void OnLocalHostCharacterSaveRejected(
        CharacterSession session,
        CharacterSaveResult result)
    {
        if (result == null || result.Accepted) return;
        ActivityPeerState? state = GetManagedListenHost(session);
        if (state != null) RecordRejectedSave(state, result);
    }

    private static ActivityPeerState? GetManagedListenHost(CharacterSession session)
    {
        if (!_started || session == null || session.IsClosed) return null;
        ActivityPeerState? state = EnsureListenHost(Stopwatch.GetTimestamp());
        if (state == null || state.PlayerId != session.PlayerId ||
            !string.Equals(state.CharacterName, session.Identity.CharacterName,
                StringComparison.Ordinal) ||
            !string.Equals("steamworks:" + state.PlayerDirectoryKey,
                session.Identity.AccountId, StringComparison.Ordinal)) return null;
        return state;
    }

    private static void RecordAcceptedShadow(ActivityPeerState state, CharacterSaveResult result)
    {
        if (result.CurrentSemanticSnapshot == null)
        {
            return;
        }

        CharacterSemanticSnapshot current = result.CurrentSemanticSnapshot;
        CharacterSemanticSnapshot? previous = result.PreviousSemanticSnapshot;
        state.LatestSemanticSnapshot = current;
        if (previous != null)
        {
            WriteSkillDelta(state, previous, current);
            WriteInventoryDelta(state, previous, current);
        }
    }

    internal static void OnCharacterSaveRejected(
        ZRpc rpc,
        CharacterSaveResult result)
    {
        if (!_started || rpc == null || result == null || result.Accepted ||
            !Peers.TryGetValue(rpc, out ActivityPeerState state))
        {
            return;
        }

        RecordRejectedSave(state, result);
    }

    private static void RecordRejectedSave(ActivityPeerState state, CharacterSaveResult result)
    {
        TryWrite(
            state,
            ActivityPrefix(state) + " Character save rejected (" +
            Clip(result.Status.ToString(), 64) + ", base revision " +
            Invariant(result.Response.BaseRevision) + "): " +
            Clip(result.Error, MaximumLoggedReasonCharacters) + ".");
    }

    internal static void Stop(string reason)
    {
        ServerEventRuntime.AuthenticatedPlayerDeathPublished -=
            OnAuthenticatedPlayerDeath;
        ResetItemPrefabNames();

        if (!_started && _writer == null)
        {
            Peers.Clear();
            _listenHost = null;
            return;
        }

        foreach (ActivityPeerState state in Peers.Values)
        {
            CompleteState(state, reason);
        }

        Peers.Clear();
        if (_listenHost != null)
        {
            CompleteState(_listenHost, reason);
            _listenHost = null;
        }

        PlayerTelemetryLogWriter? writer = _writer;
        _started = false;
        if (writer == null)
        {
            return;
        }

        bool drained = false;
        try
        {
            drained = writer.Stop();
            if (!drained)
            {
                ServerManagerPlugin.Log.LogWarning(
                    "Per-player activity logs are still draining after the " +
                    "shutdown deadline.");
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "Per-player activity log shutdown failed: " +
                exception.Message);
        }
        finally
        {
            if (drained)
            {
                _writer = null;
                writer.Dispose();
            }
            else
            {
                // Keep the live writer referenced. A later server start must
                // not create a second writer against the same files while the
                // original background worker is still draining.
                _writer = writer;
            }
        }
    }

    private static void TickState(ActivityPeerState state, long now)
    {
        if (!TryEnsureLoginWritten(state))
        {
            return;
        }

        if (state.LastIncomingDamageTimestamp != 0 &&
            now >= state.LastIncomingDamageTimestamp &&
            now - state.LastIncomingDamageTimestamp > DeathCorrelationTicks)
        {
            ClearRecentIncomingDamage(state);
        }

        if (now >= state.NextCoordinateTimestamp)
        {
            WritePosition(state, now, force: false);
        }

        if (state.LatestSemanticSnapshot != null &&
            now >= AddTicks(
                state.LastInventorySnapshotTimestamp,
                _inventorySnapshotTicks))
        {
            WriteInventoryDetailSnapshot(
                state,
                state.LatestSemanticSnapshot,
                now);
        }
    }

    private static ActivityPeerState? EnsureListenHost(long now)
    {
        ZNet? network = ZNet.instance;
        Player? localPlayer = Player.m_localPlayer;
        if (network == null || !network.IsServer() || localPlayer == null)
        {
            if (_listenHost != null)
            {
                CompleteState(_listenHost, "local_player_unavailable");
                _listenHost = null;
            }

            return null;
        }

        string characterName = Clip(localPlayer.GetPlayerName(), 96);
        if (!TryResolveListenHostPlayerId(
                localPlayer,
                characterName,
                out long playerId))
        {
            if (_listenHost != null &&
                ReferenceEquals(_listenHost.LocalPlayer, localPlayer) &&
                string.Equals(
                    _listenHost.CharacterName,
                    characterName,
                    StringComparison.Ordinal))
            {
                return _listenHost;
            }

            if (_listenHost != null)
            {
                CompleteState(_listenHost, "local_character_changed");
                _listenHost = null;
            }

            return null;
        }

        if (_listenHost != null &&
            ReferenceEquals(_listenHost.LocalPlayer, localPlayer) &&
            _listenHost.PlayerId == playerId &&
            string.Equals(
                _listenHost.CharacterName,
                characterName,
                StringComparison.Ordinal))
        {
            return _listenHost;
        }

        if (_listenHost != null)
        {
            CompleteState(_listenHost, "local_character_changed");
        }

        string? steamId = ResolveListenHostSteam64();
        if (steamId == null)
        {
            _listenHost = null;
            return null;
        }

        ActivityPeerState state = new(
            steamId,
            characterName,
            playerId,
            null,
            localPlayer,
            now,
            DateTime.UtcNow);
        _listenHost = state;
        TryEnsureLoginWritten(state);
        return state;
    }

    private static bool TryResolveListenHostPlayerId(
        Player localPlayer,
        string characterName,
        out long playerId)
    {
        playerId = 0L;
        try
        {
            Game? game = Game.instance;
            PlayerProfile? profile = game == null
                ? null
                : ValheimPrivateAccess.GetGamePlayerProfile(game);
            if (profile == null ||
                !string.Equals(
                    profile.GetName(),
                    characterName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            long profilePlayerId = profile.GetPlayerID();
            long livePlayerId = localPlayer.GetPlayerID();
            if (profilePlayerId == 0L || livePlayerId != profilePlayerId)
            {
                return false;
            }

            playerId = profilePlayerId;
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    private static void CompleteState(ActivityPeerState state, string reason)
    {
        long now = Stopwatch.GetTimestamp();
        WritePosition(state, now, force: true);

        TryWrite(
            state,
            ActivityPrefix(state) + " Logged out (" +
            FormatReason(reason) + ").");
    }

    private static void WritePosition(
        ActivityPeerState state,
        long now,
        bool force)
    {
        if (force && state.Peer != null && !state.HasLoggedPosition &&
            now < state.FirstCoordinateEligibleTimestamp)
        {
            return;
        }

        state.NextCoordinateTimestamp = AddTicks(now, _coordinateIntervalTicks);

        if (!TryGetPosition(state, out Vector3 position) ||
            !IsFinitePosition(position))
        {
            return;
        }

        if (TryWrite(state, ActivityPrefix(state, position) + " Position."))
        {
            state.HasLoggedPosition = true;
            state.LastLoggedPosition = position;
        }
    }

    private static void TryWriteDamage(
        ActivityPeerState state,
        HitData hit,
        bool outgoing,
        DamageCounterpart counterpart,
        long now)
    {
        long nextEligibleTimestamp = outgoing
            ? state.NextOutgoingDamageLogTimestamp
            : state.NextIncomingDamageLogTimestamp;
        if (now < nextEligibleTimestamp ||
            !GameplayLimitValidation.TryMeasureRawDamage(
                hit,
                out double rawDamage))
        {
            return;
        }

        string message;
        if (outgoing)
        {
            string weapon = ResolveCurrentWeapon(state);
            message = ActivityPrefix(state) + " hit " +
                      DamageCounterpartName(counterpart) + " for " +
                      FormatDamageAmount(rawDamage) + " raw damage with " +
                      weapon;
        }
        else
        {
            message = ActivityPrefix(state) + " was hit by " +
                      DamageCounterpartName(counterpart) + " for " +
                      FormatDamageAmount(rawDamage) + " raw damage";
        }

        if (TryWrite(state, message))
        {
            long next = AddTicks(now, _damageCooldownTicks);
            if (outgoing)
            {
                state.NextOutgoingDamageLogTimestamp = next;
            }
            else
            {
                state.NextIncomingDamageLogTimestamp = next;
            }
        }
    }

    private static void AddIncomingDamage(
        ActivityPeerState target,
        DamageCounterpart source,
        HitData hit,
        bool blocked,
        long now)
    {
        bool valid = GameplayLimitValidation.TryMeasureRawDamage(
            hit,
            out double rawDamage);
        if (valid && !blocked)
        {
            TryWriteDamage(
                target,
                hit,
                outgoing: false,
                source,
                now);
        }

        if (valid && !blocked && rawDamage > 0d)
        {
            target.LastIncomingDamageTimestamp = now;
            target.LastIncomingSource = source.Detached();
        }
    }

    private static DamageCounterpart ResolveRoutedTarget(
        RoutedDamageObservation observation)
    {
        if (TryFindPlayerByCharacterId(
                observation.Target,
                out ActivityPeerState? player))
        {
            return DamageCounterpart.ForPlayer(
                player!,
                "server_resolved_target_zdo");
        }

        string targetPrefab = Clip(observation.TargetPrefabName, 128);
        if (TryResolveZdoPrefab(
                observation.Target,
                out string livePrefab,
                out _))
        {
            targetPrefab = livePrefab;
        }

        return new DamageCounterpart(
            FormatTargetKind(observation.TargetKind),
            string.Empty,
            targetPrefab,
            "server_resolved_target_zdo",
            null);
    }

    private static DamageCounterpart ResolveRoutedSource(
        ActivityPeerState sender,
        HitData hit,
        bool attributedPlayerHit)
    {
        if (attributedPlayerHit)
        {
            return DamageCounterpart.ForPlayer(
                sender,
                "connection_character");
        }

        ZDOID attacker = hit.m_attacker;
        if (attacker == ZDOID.None)
        {
            return DamageCounterpart.Environment;
        }

        if (TryFindPlayerByCharacterId(
                attacker,
                out ActivityPeerState? claimedPlayer))
        {
            // A mismatched HitData attacker is not allowed to frame another
            // authenticated player. Preserve only that a player ZDO was
            // claimed; never attach the other player's account or name.
            return new DamageCounterpart(
                "unattributed_player_claim",
                string.Empty,
                "Player",
                "mismatched_attacker_zdo_claim",
                null);
        }

        if (TryResolveZdoPrefab(
                attacker,
                out string prefab,
                out string kind))
        {
            return new DamageCounterpart(
                kind,
                string.Empty,
                prefab,
                "attacker_zdo_server_resolved",
                null);
        }

        return new DamageCounterpart(
            "unknown",
            string.Empty,
            string.Empty,
            "unresolved_attacker_zdo_claim",
            null);
    }

    private static DamageCounterpart ResolveLocalTarget(object targetObject)
    {
        try
        {
            if (targetObject is Character character)
            {
                ZDOID characterId = character.GetZDOID();
                if (TryFindPlayerByCharacterId(
                        characterId,
                        out ActivityPeerState? player))
                {
                    return DamageCounterpart.ForPlayer(
                        player!,
                        "server_local_target_object");
                }
            }

            if (targetObject is Component component)
            {
                string prefab = Clip(
                    component.gameObject.name,
                    128);
                string kind = ClassifyTargetObject(targetObject);
                return new DamageCounterpart(
                    kind,
                    string.Empty,
                    prefab,
                    "server_local_target_object",
                    null);
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // A Unity object may disappear between the patched call and here.
        }

        return DamageCounterpart.UnknownTarget;
    }

    private static bool TryFindPlayerByCharacterId(
        ZDOID characterId,
        out ActivityPeerState? state)
    {
        if (characterId == ZDOID.None)
        {
            state = null;
            return false;
        }

        foreach (ActivityPeerState candidate in Peers.Values)
        {
            try
            {
                if (candidate.Peer != null &&
                    candidate.Peer.m_characterID.Equals(characterId))
                {
                    state = candidate;
                    return true;
                }
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                // A disconnect can invalidate the peer while iterating.
            }
        }

        try
        {
            if (_listenHost?.LocalPlayer != null &&
                _listenHost.LocalPlayer.GetZDOID().Equals(characterId))
            {
                state = _listenHost;
                return true;
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // The listen-host player can be destroyed during world teardown.
        }

        state = null;
        return false;
    }

    private static bool TryResolveZdoPrefab(
        ZDOID id,
        out string prefab,
        out string kind)
    {
        prefab = string.Empty;
        kind = "unknown";
        try
        {
            ZDO? zdo = ZDOMan.instance?.GetZDO(id);
            GameObject? value = ZNetScene.instance?.FindInstance(id);
            if (value == null && zdo != null)
            {
                value = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
            }

            if (value == null)
            {
                return false;
            }

            prefab = Clip(value.name, 128);
            kind = ClassifyTargetObject(value);
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    private static string ClassifyTargetObject(object value)
    {
        GameObject? gameObject = value as GameObject;
        if (value is Component component)
        {
            gameObject = component.gameObject;
        }

        if (gameObject?.GetComponent<Character>() != null)
        {
            return "character";
        }

        if (gameObject?.GetComponent<WearNTear>() != null)
        {
            return "structure";
        }

        if (gameObject?.GetComponent<MineRock5>() != null)
        {
            return "mine_rock";
        }

        if (gameObject?.GetComponent<TreeLog>() != null)
        {
            return "tree_log";
        }

        if (gameObject?.GetComponent<TreeBase>() != null)
        {
            return "tree_base";
        }

        if (gameObject?.GetComponent<Destructible>() != null)
        {
            return "destructible";
        }

        return "unknown";
    }

    private static string FormatTargetKind(RoutedDamageTargetKind value)
    {
        return value switch
        {
            RoutedDamageTargetKind.Character => "character",
            RoutedDamageTargetKind.Structure => "structure",
            RoutedDamageTargetKind.MineRock => "mine_rock",
            RoutedDamageTargetKind.Destructible => "destructible",
            RoutedDamageTargetKind.TreeLog => "tree_log",
            RoutedDamageTargetKind.TreeBase => "tree_base",
            _ => "unknown"
        };
    }

    private static void OnAuthenticatedPlayerDeath(
        ZRpc rpc,
        ServerManagerEvent value)
    {
        if (!_started || rpc == null || value == null ||
            !Peers.TryGetValue(rpc, out ActivityPeerState state) ||
            (value.Kind != ServerManagerEventKinds.PlayerDeath &&
             value.Kind != ServerManagerEventKinds.CombatPvpKill) ||
            !string.Equals(
                value.Reliability,
                ServerManagerEventReliability.ClientReported,
                StringComparison.Ordinal))
        {
            return;
        }

        Dictionary<string, string> reportFields = new(StringComparer.Ordinal)
        {
            ["cause"] = GetField(value.Fields, "cause", "environment"),
            ["attacker"] = GetField(value.Fields, "attacker", string.Empty),
            ["attacker_prefab"] = GetField(
                value.Fields,
                "attacker_prefab",
                string.Empty)
        };
        reportFields["attacker_is_player"] =
            value.Kind == ServerManagerEventKinds.CombatPvpKill
                ? "true"
                : "false";
        WriteDeath(state, reportFields, value.OccurredAtUtc);
    }

    private static void WriteDeath(
        ActivityPeerState state,
        IReadOnlyDictionary<string, string> reportFields,
        DateTime occurredAtUtc)
    {
        long now = Stopwatch.GetTimestamp();
        bool corroborated = false;
        DamageCounterpart? source = state.LastIncomingSource;
        if (source != null && state.LastIncomingDamageTimestamp != 0 &&
            now >= state.LastIncomingDamageTimestamp &&
            now - state.LastIncomingDamageTimestamp <= DeathCorrelationTicks)
        {
            corroborated = DeathReportMatchesSource(reportFields, source);
        }

        string attacker = ResolveReportedAttacker(reportFields);
        TryWrite(
            state,
            occurredAtUtc,
            ActivityPrefix(state) + " Killed by: " + attacker +
            (corroborated ? "." : " (unverified client report)."));
        ClearRecentIncomingDamage(state);
    }

    private static string ResolveReportedAttacker(
        IReadOnlyDictionary<string, string> reportFields)
    {
        string attacker = Clip(
            GetField(reportFields, "attacker", string.Empty),
            96);
        if (!string.IsNullOrEmpty(attacker))
        {
            return attacker;
        }

        string prefab = Clip(
            ClientEventObservation.CleanPrefabInstanceName(GetField(
                reportFields,
                "attacker_prefab",
                string.Empty)),
            128);
        if (!string.IsNullOrEmpty(prefab))
        {
            return prefab;
        }

        string cause = Clip(
            GetField(reportFields, "cause", "environment"),
            64);
        return string.IsNullOrEmpty(cause) ? "environment" : cause;
    }

    private static bool DeathReportMatchesSource(
        IReadOnlyDictionary<string, string> reportFields,
        DamageCounterpart source)
    {
        string attacker = GetField(reportFields, "attacker", string.Empty);
        bool serverBoundPlayer = string.Equals(
                                     source.Attribution,
                                     "connection_character",
                                     StringComparison.Ordinal) ||
                                 string.Equals(
                                     source.Attribution,
                                     "listen_host_local_player",
                                     StringComparison.Ordinal);
        return source.Kind == "player" &&
               serverBoundPlayer &&
               string.Equals(
                   GetField(
                       reportFields,
                       "attacker_is_player",
                       "false"),
                   "true",
                   StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrEmpty(attacker) &&
               !string.IsNullOrEmpty(source.Name) &&
               string.Equals(
                   attacker,
                   source.Name,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void ClearRecentIncomingDamage(ActivityPeerState state)
    {
        state.LastIncomingDamageTimestamp = 0;
        state.LastIncomingSource = null;
    }

    private static void WriteInventoryDelta(
        ActivityPeerState state,
        CharacterSemanticSnapshot previous,
        CharacterSemanticSnapshot current)
    {
        SortedSet<string> prefabs = new(
            previous.ItemTotalsByPrefab.Keys,
            StringComparer.Ordinal);
        prefabs.UnionWith(current.ItemTotalsByPrefab.Keys);

        string prefix = ActivityPrefix(state);
        int included = 0;
        int bytes = 0;
        foreach (string prefab in prefabs)
        {
            previous.ItemTotalsByPrefab.TryGetValue(prefab, out long oldValue);
            current.ItemTotalsByPrefab.TryGetValue(prefab, out long newValue);
            if (oldValue == newValue)
            {
                continue;
            }

            string message = prefix + " Inv: " +
                             InventoryDeltaItemName(prefab) + " " +
                             Invariant(oldValue) + " -> " +
                             Invariant(newValue) + ".";
            if (included >= MaximumInventoryEntries ||
                !TryReserveListingBytes(message, ref bytes))
            {
                break;
            }

            TryWrite(state, message);
            ++included;
        }
    }

    private static void WriteInventoryDetailSnapshot(
        ActivityPeerState state,
        CharacterSemanticSnapshot snapshot,
        long now)
    {
        List<string> lines = new();
        int included = 0;
        IEnumerable<CharacterSemanticItemState> ordered = snapshot.Items
            .OrderBy(item => item.PositionY)
            .ThenBy(item => item.PositionX)
            .ThenBy(item => item.PrefabName, StringComparer.Ordinal)
            .ThenBy(item => item.Quality);
        foreach (CharacterSemanticItemState item in ordered)
        {
            if (included >= MaximumInventoryDetailEntries)
            {
                break;
            }

            AppendInventoryDetail(lines, item);
            ++included;
        }

        if (snapshot.Items.Count == 0)
        {
            lines.Add("  - [empty]");
        }

        if (TryWriteBlock(
                state,
                ActivityPrefix(state) + " Inventory:",
                lines))
        {
            state.LastInventorySnapshotTimestamp = now;
        }
    }

    private static void AppendInventoryDetail(
        List<string> lines,
        CharacterSemanticItemState item)
    {
        lines.Add(
            "  - " + InventoryItemName(item) +
            (item.Stack == 1 ? string.Empty : " x" + Invariant(item.Stack)) +
            (item.Quality == 1 ? string.Empty : " Q" + Invariant(item.Quality)));

        if (item.CustomData.Count == 0)
        {
            return;
        }

        lines.Add("    CustomData:");
        foreach (KeyValuePair<string, string> pair in item.CustomData)
        {
            lines.Add(
                "      " + QuoteJsonString(pair.Key) + ": " +
                QuoteJsonString(pair.Value));
        }
    }

    private static string InventoryItemName(CharacterSemanticItemState item)
    {
        if (!TryReadPrefabHashLabel(item.PrefabName, out int prefabHash) ||
            prefabHash != item.PrefabHash)
        {
            return Clip(item.PrefabName, 256);
        }

        return ResolveInventoryItemName(prefabHash);
    }

    private static string InventoryDeltaItemName(string savedIdentity)
    {
        return TryReadPrefabHashLabel(savedIdentity, out int prefabHash)
            ? ResolveInventoryItemName(prefabHash)
            : Clip(savedIdentity, 256);
    }

    private static string ResolveInventoryItemName(int prefabHash)
    {
        try
        {
            ObjectDB? objectDb = ObjectDB.instance;
            if (objectDb != null)
            {
                if (!ReferenceEquals(_itemPrefabNameSource, objectDb))
                {
                    ItemPrefabNames.Clear();
                    _itemPrefabNameSource = objectDb;
                }

                if (ItemPrefabNames.TryGetValue(prefabHash, out string cached))
                {
                    return cached;
                }

                if (objectDb.TryGetItemPrefab(prefabHash, out GameObject prefab) &&
                    prefab != null)
                {
                    string name = ClientEventObservation.CleanPrefabInstanceName(
                        Clip(prefab.name, 256));
                    if (!string.IsNullOrEmpty(name))
                    {
                        ItemPrefabNames[prefabHash] = name;
                        return name;
                    }
                }
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // The ObjectDB can disappear during world teardown. Player logging
            // remains best effort and keeps a stable identity for unresolved items.
        }

        return "unknown:" + unchecked((uint)prefabHash).ToString(
            "X8", CultureInfo.InvariantCulture);
    }

    private static bool TryReadPrefabHashLabel(string value, out int prefabHash)
    {
        prefabHash = 0;
        if (value.Length != 13 ||
            !value.StartsWith("hash:", StringComparison.Ordinal) ||
            !uint.TryParse(value.Substring(5), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out uint raw))
        {
            return false;
        }

        prefabHash = unchecked((int)raw);
        return true;
    }

    private static void ResetItemPrefabNames()
    {
        ItemPrefabNames.Clear();
        _itemPrefabNameSource = null;
    }

    private static string QuoteJsonString(string value)
    {
        StringBuilder escaped = new StringBuilder(value.Length + 2);
        escaped.Append('"');
        for (int index = 0; index < value.Length; ++index)
        {
            char character = value[index];
            switch (character)
            {
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '\b':
                    escaped.Append("\\b");
                    break;
                case '\f':
                    escaped.Append("\\f");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                default:
                    // Preserve controls through the log writer's sanitization,
                    // and keep YAML line breaks inside their quoted scalar.
                    if (char.IsControl(character) ||
                        character == '\u2028' || character == '\u2029')
                    {
                        escaped.Append("\\u")
                            .Append(((int)character).ToString(
                                "x4",
                                CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        escaped.Append(character);
                    }

                    break;
            }
        }

        return escaped.Append('"').ToString();
    }

    private static bool TryReserveListingBytes(
        string message,
        ref int currentBytes)
    {
        int bytes = Encoding.UTF8.GetByteCount(message);
        if (currentBytes + bytes > MaximumInventoryListingBytes)
        {
            return false;
        }

        currentBytes += bytes;
        return true;
    }

    private static void WriteSkillDelta(
        ActivityPeerState state,
        CharacterSemanticSnapshot previous,
        CharacterSemanticSnapshot current)
    {
        SortedSet<int> skillTypes = new(previous.Skills.Keys);
        skillTypes.UnionWith(current.Skills.Keys);
        string prefix = ActivityPrefix(state);
        int included = 0;
        foreach (int skillType in skillTypes)
        {
            previous.Skills.TryGetValue(
                skillType,
                out CharacterSemanticSkillState? oldSkill);
            current.Skills.TryGetValue(
                skillType,
                out CharacterSemanticSkillState? newSkill);
            double oldLevel = IntegerSkillLevel(oldSkill);
            double newLevel = IntegerSkillLevel(newSkill);
            if (oldLevel == newLevel)
            {
                continue;
            }

            if (included >= MaximumSkillEntries)
            {
                break;
            }

            TryWrite(
                state,
                prefix + " Skill " + GetSkillName(skillType) + ": " +
                Invariant(oldLevel) + " -> " + Invariant(newLevel) + ".");
            ++included;
        }
    }

    private static double IntegerSkillLevel(CharacterSemanticSkillState? skill)
    {
        return skill == null || !IsFinite(skill.Level)
            ? 0
            : Math.Floor((double)skill.Level);
    }

    private static string GetSkillName(int skillType)
    {
        return Enum.GetName(typeof(Skills.SkillType), skillType) ??
               skillType.ToString(CultureInfo.InvariantCulture);
    }

    private static string ActivityPrefix(ActivityPeerState state)
    {
        if (state.Peer != null && !state.HasLoggedPosition &&
            Stopwatch.GetTimestamp() < state.FirstCoordinateEligibleTimestamp)
        {
            return "[unknown]";
        }

        if (TryGetPosition(state, out Vector3 position) &&
            IsFinitePosition(position))
        {
            return ActivityPrefix(state, position);
        }

        return state.HasObservedPosition
            ? ActivityPrefix(state, state.LastObservedPosition)
            : state.HasLoggedPosition
                ? ActivityPrefix(state, state.LastLoggedPosition)
                : "[unknown]";
    }

    private static string ActivityPrefix(
        ActivityPeerState state,
        Vector3 position)
    {
        state.HasObservedPosition = true;
        state.LastObservedPosition = position;
        return "[" + FormatFloat(position.x) + ", " +
               FormatFloat(position.y) + ", " +
               FormatFloat(position.z) + "]";
    }

    private static string FormatDamageAmount(double value)
    {
        return value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static Player? ResolvePlayerObject(ActivityPeerState state)
    {
        if (state.LocalPlayer != null)
        {
            return state.LocalPlayer;
        }

        if (state.Peer == null)
        {
            return null;
        }

        GameObject? instance = ZNetScene.instance?.FindInstance(
            state.Peer.m_characterID);
        return instance?.GetComponent<Player>();
    }

    private static string ResolveCurrentWeapon(ActivityPeerState state)
    {
        try
        {
            ItemDrop.ItemData? weapon = ResolvePlayerObject(state)?
                .GetCurrentWeapon();
            if (weapon == null)
            {
                return "unknown";
            }

            if (weapon.m_dropPrefab != null &&
                !string.IsNullOrEmpty(weapon.m_dropPrefab.name))
            {
                string prefab = ClientEventObservation.CleanPrefabInstanceName(
                    Clip(weapon.m_dropPrefab.name, 128));
                return string.IsNullOrEmpty(prefab) ? "unknown" : prefab;
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // Player objects can disappear while a hit is being observed.
        }

        return "unknown";
    }

    private static string DamageCounterpartName(DamageCounterpart counterpart)
    {
        if (!string.IsNullOrEmpty(counterpart.Name))
        {
            // Character names are user data; only Unity prefab instance names
            // receive the synthetic "(Clone)" suffix.
            return counterpart.Name;
        }

        if (!string.IsNullOrEmpty(counterpart.Prefab))
        {
            string prefab = ClientEventObservation.CleanPrefabInstanceName(
                counterpart.Prefab);
            return string.IsNullOrEmpty(prefab) ? "unknown" : prefab;
        }

        return string.IsNullOrEmpty(counterpart.Kind)
            ? "unknown"
            : counterpart.Kind;
    }

    private static string FormatReason(string reason)
    {
        string value = Clip(reason, 64).Replace('_', ' ');
        return string.IsNullOrEmpty(value) ? "unknown reason" : value;
    }

    private static bool TryGetPosition(
        ActivityPeerState state,
        out Vector3 position)
    {
        try
        {
            if (state.LocalPlayer != null)
            {
                position = state.LocalPlayer.transform.position;
                return true;
            }

            if (state.Peer != null)
            {
                position = state.Peer.GetRefPos();
                return true;
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // A disconnect can invalidate the Unity object between callbacks.
        }

        position = default;
        return false;
    }

    private static bool IsFinitePosition(Vector3 position)
    {
        const float maximumCoordinate = 10000000f;
        return IsFinite(position.x) && IsFinite(position.y) &&
               IsFinite(position.z) &&
               Math.Abs(position.x) <= maximumCoordinate &&
               Math.Abs(position.y) <= maximumCoordinate &&
               Math.Abs(position.z) <= maximumCoordinate;
    }

    private static bool TryWrite(ActivityPeerState state, string message)
    {
        return TryWrite(state, DateTime.UtcNow, message);
    }

    private static bool TryWrite(
        ActivityPeerState state,
        DateTime occurredAtUtc,
        string message)
    {
        return TryEnsureLoginWritten(state) &&
               TryWriteCore(state, occurredAtUtc, message);
    }

    private static bool TryWriteCore(
        ActivityPeerState state,
        DateTime occurredAtUtc,
        string message)
    {
        PlayerTelemetryLogWriter? writer = _writer;
        if (!_started || writer == null || state.PlayerId == 0L)
        {
            return false;
        }

        try
        {
            return writer.TryWrite(
                state.PlayerDirectoryKey,
                state.CharacterName,
                state.PlayerId,
                occurredAtUtc,
                Clip(message, MaximumLogMessageCharacters));
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "A per-player activity record could not be queued: " +
                exception.Message);
            return false;
        }
    }

    private static bool TryWriteBlock(
        ActivityPeerState state,
        string header,
        IReadOnlyList<string> continuationLines)
    {
        PlayerTelemetryLogWriter? writer = _writer;
        if (!_started || writer == null || !TryEnsureLoginWritten(state))
        {
            return false;
        }

        try
        {
            return writer.TryWriteBlock(
                state.PlayerDirectoryKey,
                state.CharacterName,
                state.PlayerId,
                DateTime.UtcNow,
                Clip(header, MaximumLogMessageCharacters),
                continuationLines);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            ServerManagerPlugin.Log.LogWarning(
                "A per-player activity block could not be queued: " +
                exception.Message);
            return false;
        }
    }

    private static bool TryEnsureLoginWritten(ActivityPeerState state)
    {
        if (state.LoginWritten)
        {
            return true;
        }

        if (!TryResolvePlayerId(state))
        {
            return false;
        }

        if (!TryWriteCore(
                state,
                state.OpenedAtUtc,
                "[unknown] Logged in."))
        {
            return false;
        }

        state.LoginWritten = true;
        return true;
    }

    private static bool TryResolvePlayerId(ActivityPeerState state)
    {
        if (state.PlayerId != 0L)
        {
            return true;
        }

        try
        {
            Player? player = ResolvePlayerObject(state);
            if (player == null ||
                !string.Equals(
                    player.GetPlayerName(),
                    state.CharacterName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            long playerId = player.GetPlayerID();
            if (playerId == 0L)
            {
                return false;
            }

            // With Server Characters disabled this value is client-originated
            // display metadata. The authenticated Steam64 directory remains
            // the only routing/security boundary.
            state.PlayerId = playerId;
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    private static void ReportDiagnostic(PlayerTelemetryDiagnostic diagnostic)
    {
        if (diagnostic == null)
        {
            return;
        }

        string message = diagnostic.Message + " occurrences=" +
                         diagnostic.OccurrenceCount.ToString(
                             CultureInfo.InvariantCulture);
        if (diagnostic.Kind == PlayerTelemetryDiagnosticKind.WriteFailure ||
            diagnostic.Kind == PlayerTelemetryDiagnosticKind.WorkerFailure ||
            diagnostic.Kind == PlayerTelemetryDiagnosticKind.ShutdownTimeout)
        {
            ServerManagerPlugin.Log.LogWarning(message);
        }
        else if (diagnostic.Kind != PlayerTelemetryDiagnosticKind.Started &&
                 diagnostic.Kind != PlayerTelemetryDiagnosticKind.Stopped)
        {
            ServerManagerPlugin.Log.LogDebug(message);
        }
    }

    private static string GetField(
        IReadOnlyDictionary<string, string> fields,
        string name,
        string fallback)
    {
        return fields.TryGetValue(name, out string? value) &&
               !string.IsNullOrEmpty(value)
            ? value
            : fallback;
    }

    private static bool TryResolveAuthenticatedSteam64(
        ZRpc rpc,
        ServerPeerIdentity identity,
        out string steamId)
    {
        steamId = string.Empty;
        try
        {
            ZNet? server = ZNet.instance;
            if (server == null || !server.IsServer() ||
                ZNet.m_onlineBackend != OnlineBackendType.Steamworks ||
                !ReferenceEquals(identity.Rpc, rpc) ||
                identity.Peer == null ||
                !ReferenceEquals(identity.Peer.m_rpc, rpc))
            {
                return false;
            }

            // Ready registration already passed this generation-bound final
            // authentication gate. Resolve it again so the log key comes from
            // the pinned Steam reservation, while allowing server-installed
            // socket wrappers to remain around the live transport.
            if (!ServerManagerRuntime.TryResolveActiveDetectionPeer(
                    server,
                    rpc,
                    out ServerPeerIdentity current,
                    out _) ||
                !ReferenceEquals(current.Rpc, rpc) ||
                !ReferenceEquals(current.Peer, identity.Peer) ||
                !string.Equals(
                    current.HostId,
                    identity.HostId,
                    StringComparison.Ordinal) ||
                !PlayerTelemetryLogWriter.IsValidIndividualSteam64(
                    current.HostId))
            {
                return false;
            }

            steamId = current.HostId;
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    private static string? ResolveListenHostSteam64()
    {
        try
        {
            if (TryFormatIndividualSteam64(
                    SteamUser.GetSteamID(),
                    out string steamId))
            {
                return steamId;
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // A local host can exist briefly before Steam exposes its identity.
        }

        return null;
    }

    private static bool TryFormatIndividualSteam64(
        CSteamID value,
        out string steamId)
    {
        steamId = string.Empty;
        string canonical = value.m_SteamID.ToString(
            CultureInfo.InvariantCulture);
        if (!PlayerTelemetryLogWriter.IsValidIndividualSteam64(canonical))
        {
            return false;
        }

        steamId = canonical;
        return true;
    }

    private static long SecondsToTicks(int seconds)
    {
        return checked((long)seconds * Stopwatch.Frequency);
    }

    private static long AddTicks(long value, long increment)
    {
        return value > long.MaxValue - increment
            ? long.MaxValue
            : value + increment;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static string Invariant(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Invariant(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Invariant(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0", CultureInfo.InvariantCulture);
    }

    private static string Clip(string? value, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string source = value!;
        StringBuilder safe = new(Math.Min(source.Length, maximumCharacters));
        for (int index = 0;
             index < source.Length && safe.Length < maximumCharacters;
             ++index)
        {
            char character = source[index];
            if (char.IsHighSurrogate(character) &&
                index + 1 < source.Length &&
                char.IsLowSurrogate(source[index + 1]))
            {
                if (safe.Length + 2 > maximumCharacters)
                {
                    break;
                }

                safe.Append(character).Append(source[++index]);
            }
            else if (!char.IsSurrogate(character) && !char.IsControl(character))
            {
                safe.Append(character);
            }
        }

        return safe.ToString();
    }

    private sealed class ActivityPeerState
    {
        internal ActivityPeerState(
            string playerDirectoryKey,
            string characterName,
            long playerId,
            ZNetPeer? peer,
            Player? localPlayer,
            long openedTimestamp,
            DateTime openedAtUtc)
        {
            PlayerDirectoryKey = playerDirectoryKey;
            CharacterName = Clip(characterName, 96);
            PlayerId = playerId;
            Peer = peer;
            LocalPlayer = localPlayer;
            OpenedAtUtc = openedAtUtc;
            FirstCoordinateEligibleTimestamp = peer == null
                ? openedTimestamp
                : AddTicks(openedTimestamp, _coordinateInitialDelayTicks);
            NextCoordinateTimestamp = FirstCoordinateEligibleTimestamp;
        }

        internal string PlayerDirectoryKey { get; }
        internal string CharacterName { get; }
        internal long PlayerId { get; set; }
        internal ZNetPeer? Peer { get; }
        internal Player? LocalPlayer { get; }
        internal DateTime OpenedAtUtc { get; }
        internal bool LoginWritten { get; set; }
        internal long FirstCoordinateEligibleTimestamp { get; }
        internal long NextCoordinateTimestamp { get; set; }
        internal bool HasLoggedPosition { get; set; }
        internal Vector3 LastLoggedPosition { get; set; }
        internal bool HasObservedPosition { get; set; }
        internal Vector3 LastObservedPosition { get; set; }
        internal long LastInventorySnapshotTimestamp { get; set; }
        internal CharacterSemanticSnapshot? LatestSemanticSnapshot { get; set; }
        internal Guid ManagedCharacterSessionId { get; set; }
        internal long NextOutgoingDamageLogTimestamp { get; set; }
        internal long NextIncomingDamageLogTimestamp { get; set; }
        internal long LastIncomingDamageTimestamp { get; set; }
        internal DamageCounterpart? LastIncomingSource { get; set; }
    }

    private sealed class DamageCounterpart
    {
        internal static readonly DamageCounterpart Environment = new(
            "environment",
            string.Empty,
            string.Empty,
            "no_attacker_zdo",
            null);

        internal static readonly DamageCounterpart UnknownTarget = new(
            "unknown",
            string.Empty,
            string.Empty,
            "target_object_unavailable",
            null);

        internal DamageCounterpart(
            string kind,
            string name,
            string prefab,
            string attribution,
            ActivityPeerState? playerState)
        {
            Kind = Clip(kind, 48);
            Name = Clip(name, 96);
            Prefab = Clip(prefab, 128);
            Attribution = Clip(attribution, 64);
            PlayerState = playerState;
        }

        internal string Kind { get; }
        internal string Name { get; }
        internal string Prefab { get; }
        internal string Attribution { get; }
        internal ActivityPeerState? PlayerState { get; }

        internal static DamageCounterpart ForPlayer(
            ActivityPeerState state,
            string attribution)
        {
            return new DamageCounterpart(
                "player",
                state.CharacterName,
                "Player",
                attribution,
                state);
        }

        internal DamageCounterpart Detached()
        {
            return PlayerState == null
                ? this
                : new DamageCounterpart(
                    Kind,
                    Name,
                    Prefab,
                    Attribution,
                    null);
        }
    }

}
