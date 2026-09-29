using System;
using System.Runtime.CompilerServices;
using ServerManager.Events;

namespace ServerManager;

internal static partial class ServerManagerRuntime
{
    // Weak keys preserve once-per-attempt suppression even after disconnect
    // cleanup, without retaining every rejected RPC for the server lifetime.
    private sealed class ConnectionRejectionMarker
    {
        internal bool Recorded;
        internal bool InitialCharacterRequiresFresh;
        internal bool WorldReleased;
    }

    private static readonly ConditionalWeakTable<ZRpc, ConnectionRejectionMarker>
        ConnectionRejections = new();
    private static readonly ConditionalWeakTable<Game, ConnectionRejectionMarker>
        LocalHostConnectionRejections = new();

    private static ConnectionRejectionMarker GetConnectionRejectionMarker(ZRpc rpc) =>
        ConnectionRejections.GetValue(rpc, _ => new ConnectionRejectionMarker());

    private static void RecordConnectionRejection(ZRpc rpc, ProtocolRejection rejection,
        string source = "server_observed", ZNetPeer? unregisteredPeer = null)
    {
        if (rpc == null || !rejection.Disconnect) return;
        try
        {
            _coordinator.TryGetSnapshot(rpc, out ConnectionSessionSnapshot session);
            ConnectionSessionState phase = session == null ? ConnectionSessionState.Connected :
                session.State == ConnectionSessionState.Rejected ? session.StateBeforeRejection : session.State;
            ConnectionRejectionMarker marker = GetConnectionRejectionMarker(rpc);
            lock (marker)
            {
                // Ready ACK precedes initial disk commit and actual world release.
                // Only the successful release marks an admitted player, whose
                // later save/security failures already have their own audits.
                if (marker.Recorded || marker.WorldReleased) return;
                marker.Recorded = true;
            }

            // The manifest's selected name is display-only. It never supplies an
            // account identity and is replaced by vanilla's peer name after auth.
            string accountId = "", characterName = session?.ReportedCharacterName ?? "",
                identityStatus = "unavailable";
            SteamAuthenticationAttempt? attempt;
            lock (SteamAuthenticationGate) SteamAuthenticationsByRpc.TryGetValue(rpc, out attempt);
            ZNetPeer? peer = unregisteredPeer ?? attempt?.Peer;
            if (peer == null && ServerPeerResolver.TryResolvePeer(ZNet.instance, rpc, out ZNetPeer resolved, out _))
                peer = resolved;
            // Never read identity from the rejection/report packet. A reserved
            // socket identity is only a claim until final Steam acceptance.
            try
            {
                if (peer != null && ReferenceEquals(peer.m_rpc, rpc))
                {
                    bool reservedSocket;
                    lock (SteamAuthenticationGate)
                        reservedSocket = attempt != null && IsCurrentSteamAuthenticationLocked(attempt) &&
                            ReferenceEquals(attempt.Rpc, rpc) && ReferenceEquals(attempt.Peer, peer);
                    // ServerSync may replace the public socket with its own
                    // wrapper. Keep the identity bound to our original Steam
                    // reservation, not a wrapper or any client-supplied account.
                    ISocket? socket = reservedSocket ? attempt!.Socket : peer.m_socket;
                    while (socket is BufferedWorldSocket buffered) socket = buffered.Original;
                    if (socket is ZSteamSocket steam)
                    {
                        Steamworks.CSteamID currentSteamId = steam.GetPeerID();
                        if (currentSteamId.IsValid() && (!reservedSocket || currentSteamId == attempt!.SteamId))
                        {
                            accountId = CharacterSteamIdentity.AccountPrefix + currentSteamId.m_SteamID
                                .ToString(System.Globalization.CultureInfo.InvariantCulture);
                            identityStatus = session != null && session.PeerInfoAuthenticated && reservedSocket
                                ? "authenticated" : "unverified";
                            if (identityStatus == "authenticated") characterName = peer.m_playerName ?? "";
                        }
                    }
                }
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            { /* An already-disposing socket has unavailable identity, not a guessed account. */ }
            string category = rejection.AuditCategory.Length != 0 ? rejection.AuditCategory :
                rejection.Code == ProtocolRejectCode.ManifestRejected || rejection.Code == ProtocolRejectCode.ManifestValidatorFailed
                    ? "mod_policy" :
                (int)rejection.Code >= 1500 && (int)rejection.Code < 1600 ? "character" :
                rejection.Code == ProtocolRejectCode.PeerIdentityUnavailable ||
                rejection.Code == ProtocolRejectCode.PeerInfoAuthenticationIncomplete ||
                rejection.Code == ProtocolRejectCode.DuplicateConnection ? "authentication" : "protocol";
            string stage = rejection.AuditStage.Length != 0 ? rejection.AuditStage :
                phase == ConnectionSessionState.Challenged ? "manifest" :
                phase == ConnectionSessionState.ManifestValidated ? "authentication" :
                phase == ConnectionSessionState.CharacterSent ? "character_apply" :
                phase == ConnectionSessionState.Ready ? "world_release" : "connected";
            ServerEventRuntime.RecordConnectionRejected(accountId, characterName, category, stage,
                rejection.AuditReasonCode.Length != 0 ? rejection.AuditReasonCode : rejection.Code.ToString(),
                source, identityStatus,
                rejection.AuditDetail.Length != 0 ? rejection.AuditDetail : rejection.SafeMessage,
                rejection.AuditPluginSummary);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // Optional observability must never cancel an admission rejection.
            // Do not call another optional logging sink from this failure path.
        }
    }

    private static void RecordUnregisteredConnectionRejection(ZRpc rpc, ZNetPeer peer,
        string reasonCode, string detail) =>
        RecordConnectionRejection(rpc, new ProtocolRejection(ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
            "Steam connection admission failed.").WithConnectionAudit("authentication", reasonCode,
                detail, stage: "authentication_reservation"), unregisteredPeer: peer);

    internal static void RecordLocalHostConnectionRejection(Game game, CharacterIdentity identity,
        string reasonCode, string detail)
    {
        try
        {
            ConnectionRejectionMarker marker = LocalHostConnectionRejections.GetValue(game, _ => new ConnectionRejectionMarker());
            lock (marker)
            {
                if (marker.Recorded) return;
                marker.Recorded = true;
            }
            ServerEventRuntime.RecordConnectionRejected(identity.AccountId, identity.CharacterName,
                "character", reasonCode == "fresh_local_character_required" ? "character_apply" : "character_open",
                reasonCode, "server_observed", "authenticated", detail);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // The original host refusal and profile restoration must continue.
        }
    }

    private static void HandleServerClientCharacterRejection(ZNet server, ZRpc rpc, ProtocolPacket packet)
    {
        if (!TryResolveActiveDetectionPeer(server, rpc, out _, out ProtocolRejection authenticationError))
        {
            SendServerRejection(rpc, authenticationError);
            return;
        }
        ProtocolOperationResult result = _coordinator.AcceptClientCharacterRejection(server, rpc, packet,
            GetConnectionRejectionMarker(rpc).InitialCharacterRequiresFresh);
        bool validClientReport = result.Rejection?.Code == ProtocolRejectCode.ClientCharacterRejected;
        SendServerRejection(rpc, result.Rejection,
            source: validClientReport ? "client_reported" : "server_observed");
    }

    private static void ReportClientFreshCharacterRejection(ClientConnection session, byte[] messageId)
    {
        if (session.Failed || session.FreshCharacterRejectionReported || !session.ManifestAccepted ||
            session.InitialTransferCompleted || session.ReadyAcknowledgementSent ||
            session.SessionId == null || session.Nonce == null) return;
        session.FreshCharacterRejectionReported = true;
        try
        {
            SendProtocolOrThrow(session.Rpc, ProtocolPacketCodec.CreateFreshCharacterRejection(
                session.SessionId, session.Nonce, messageId, _connectionLimits));
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // Best effort only: preserve the existing local refusal even if the
            // server cannot receive the observation before the socket closes.
        }
    }
}
