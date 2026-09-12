#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace ServerManager
{
    public delegate void ProtocolPacketSender(ZRpc rpc, ZPackage package);

    public enum PeerInfoGateAction
    {
        Allow,
        Reject
    }

    public sealed class PeerInfoGateResult
    {
        private PeerInfoGateResult(
            PeerInfoGateAction action,
            ConnectionSessionSnapshot session,
            ProtocolRejection rejection)
        {
            Action = action;
            Session = session;
            Rejection = rejection;
        }

        public PeerInfoGateAction Action { get; private set; }
        public ConnectionSessionSnapshot Session { get; private set; }
        public ProtocolRejection Rejection { get; private set; }

        public static PeerInfoGateResult Allow(ConnectionSessionSnapshot session)
        {
            return new PeerInfoGateResult(PeerInfoGateAction.Allow, session, null);
        }

        public static PeerInfoGateResult Reject(
            ProtocolRejection rejection,
            ConnectionSessionSnapshot session = null)
        {
            return new PeerInfoGateResult(PeerInfoGateAction.Reject, session, rejection);
        }
    }

    public sealed class ConnectionSessionSnapshot
    {
        internal ConnectionSessionSnapshot(
            ZRpc rpc,
            ConnectionSessionState state,
            byte[] sessionId,
            byte[] nonce,
            uint lastSequence,
            bool peerInfoAdmitted,
            bool peerInfoAuthenticated,
            bool serverCharactersEnabled,
            bool characterTransferPrepared,
            byte[] characterMessageId,
            long createdTimestamp,
            long deadlineTimestamp,
            ProtocolRejection rejection)
        {
            Rpc = rpc;
            State = state;
            StateBeforeRejection = state;
            SessionId = ProtocolByteUtil.Clone(sessionId);
            Nonce = ProtocolByteUtil.Clone(nonce);
            LastSequence = lastSequence;
            PeerInfoAdmitted = peerInfoAdmitted;
            PeerInfoAuthenticated = peerInfoAuthenticated;
            ServerCharactersEnabled = serverCharactersEnabled;
            CharacterTransferPrepared = characterTransferPrepared;
            CharacterMessageId = ProtocolByteUtil.Clone(characterMessageId);
            CreatedTimestamp = createdTimestamp;
            DeadlineTimestamp = deadlineTimestamp;
            Rejection = rejection;
        }

        public ZRpc Rpc { get; private set; }
        public ConnectionSessionState State { get; private set; }
        internal ConnectionSessionState StateBeforeRejection { get; set; }
        public byte[] SessionId { get; private set; }
        public byte[] Nonce { get; private set; }
        public uint LastSequence { get; private set; }
        public bool PeerInfoAdmitted { get; private set; }
        public bool PeerInfoAuthenticated { get; private set; }
        public bool ServerCharactersEnabled { get; private set; }
        public bool CharacterTransferPrepared { get; private set; }
        public byte[] CharacterMessageId { get; private set; }
        public long CreatedTimestamp { get; private set; }
        public long DeadlineTimestamp { get; private set; }
        public ProtocolRejection Rejection { get; private set; }
    }

    /// <summary>
    /// Server-side coordinator for:
    /// Connected -> Challenged -> ManifestValidated -> CharacterSent -> Ready.
    ///
    /// Integration contract:
    /// 1. Register RawProtocolRpcTransport handlers in a ZNet.OnNewConnection prefix, then call
    ///    RegisterConnected from its postfix (the peer must already be in ZNet).
    /// 2. DispatchChallenge through RawProtocolRpcTransport.Send.
    /// 3. AcceptManifest validates and sends ManifestAccepted. The client may release its held
    ///    vanilla SendPeerInfo only after receiving that packet.
    /// 4. RPC_PeerInfo prefix calls GatePeerInfo. It is allowed from ManifestValidated onward.
    ///    Install/retain the outbound BufferingSocket on rpc.m_socket before allowing vanilla
    ///    RPC_PeerInfo. Do not replace peer.m_socket before the original method: current Valheim
    ///    hard-casts it to ZSteamSocket during platform admission.
    /// 5. RPC_PeerInfo postfix records vanilla admission. Only after Steam's final ticket
    ///    callback succeeds may ConfirmPeerInfoAuthenticated prepare/send character fragments
    ///    and call ConfirmCharacterSent.
    /// 6. AcceptReadyAck moves to Ready. Flush buffered PeerInfo/RoutedRPC/ZDOData only when
    ///    CanReleaseWorld returns true.
    ///
    /// This deliberate split prevents character data from being exposed before vanilla platform
    /// authentication while still preventing world traffic from escaping before character sync.
    /// </summary>
    public sealed class ConnectionSessionCoordinator : IDisposable
    {
        private static readonly ReadOnlyCollection<ConnectionSessionSnapshot> NoExpiredSessions =
            new ReadOnlyCollection<ConnectionSessionSnapshot>(Array.Empty<ConnectionSessionSnapshot>());

        private sealed class RpcReferenceComparer : IEqualityComparer<ZRpc>
        {
            internal static readonly RpcReferenceComparer Instance = new RpcReferenceComparer();

            public bool Equals(ZRpc x, ZRpc y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(ZRpc obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        private sealed class Session
        {
            internal ZRpc Rpc;
            internal ConnectionSessionState State;
            internal ConnectionSessionState StateBeforeRejection;
            internal byte[] SessionId;
            internal byte[] Nonce;
            internal uint LastSequence;
            internal long CreatedTimestamp;
            internal long AbsoluteDeadline;
            internal long PhaseDeadline;
            internal ProtocolRejection Rejection;
            internal bool PeerInfoAdmitted;
            internal bool PeerInfoAuthenticated;
            internal bool ServerCharactersEnabled;
            internal bool OperationInProgress;
            internal bool CharacterTransferPrepared;
            internal byte[] CharacterMessageId;
        }

        private readonly object sync = new object();
        private readonly Dictionary<ZRpc, Session> sessions =
            new Dictionary<ZRpc, Session>(RpcReferenceComparer.Instance);
        private readonly ConnectionProtocolLimits protocolLimits;
        private readonly FragmentTransportLimits fragmentLimits;
        private readonly IProtocolClock clock;
        private bool disposed;

        public ConnectionSessionCoordinator(
            ConnectionProtocolLimits connectionLimits = null,
            FragmentTransportLimits characterFragmentLimits = null,
            IProtocolClock protocolClock = null)
        {
            protocolLimits = connectionLimits ?? new ConnectionProtocolLimits();
            fragmentLimits = characterFragmentLimits ?? new FragmentTransportLimits();
            clock = protocolClock ?? new StopwatchProtocolClock();
            if (clock.Frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(protocolClock));
            }
        }

        public ProtocolOperationResult RegisterConnected(ZNet server, ZRpc rpc)
        {
            ZNetPeer peer;
            ProtocolRejection resolutionError;
            if (!ServerPeerResolver.TryResolvePeer(
                    server,
                    rpc,
                    out peer,
                    out resolutionError))
            {
                return ProtocolOperationResult.Reject(resolutionError);
            }

            lock (sync)
            {
                ThrowIfDisposed();
                Session existing;
                if (sessions.TryGetValue(rpc, out existing))
                {
                    ProtocolRejection duplicate = new ProtocolRejection(
                        ProtocolRejectCode.DuplicateConnection,
                        "A duplicate connection session was detected.");
                    MarkRejected(existing, duplicate);
                    return ProtocolOperationResult.Reject(duplicate, Snapshot(existing));
                }

                long now = clock.GetTimestamp();
                Session session = new Session
                {
                    Rpc = rpc,
                    State = ConnectionSessionState.Connected,
                    SessionId = ProtocolByteUtil.RandomBytes(
                        ConnectionProtocolLimits.SessionIdBytes),
                    Nonce = ProtocolByteUtil.RandomBytes(
                        ConnectionProtocolLimits.NonceBytes),
                    LastSequence = 0,
                    CreatedTimestamp = now,
                    AbsoluteDeadline = AddDuration(
                        now,
                        protocolLimits.OverallHandshakeTimeout),
                    CharacterMessageId = ProtocolByteUtil.Empty
                };
                RefreshPhaseDeadline(session, now);
                sessions.Add(rpc, session);
                return ProtocolOperationResult.Success(Snapshot(session));
            }
        }

        public ProtocolOperationResult DispatchChallenge(
            ZNet server,
            ZRpc rpc,
            ProtocolChallengeOptions challengeOptions,
            ProtocolPacketSender sender)
        {
            if (challengeOptions == null)
            {
                throw new ArgumentNullException(nameof(challengeOptions));
            }

            if (sender == null)
            {
                throw new ArgumentNullException(nameof(sender));
            }

            ProtocolOperationResult<Session> reserved = ReserveOperation(
                server,
                rpc,
                ConnectionSessionState.Connected);
            if (!reserved.Succeeded)
            {
                return ProtocolOperationResult.Reject(
                    reserved.Rejection,
                    reserved.Session);
            }

            Session session = reserved.Value;
            try
            {
                ZPackage challenge = ProtocolPacketCodec.CreateChallenge(
                    session.SessionId,
                    session.Nonce,
                    challengeOptions,
                    protocolLimits);
                sender(rpc, challenge);
            }
            catch
            {
                return FailReservedOperation(
                    session,
                    new ProtocolRejection(
                        ProtocolRejectCode.InternalError,
                        "The server could not send the connection challenge."));
            }

            lock (sync)
            {
                if (!IsCurrentReservedSession(session, ConnectionSessionState.Connected))
                {
                    return ConcurrentOperationFailure(session);
                }

                session.OperationInProgress = false;
                session.ServerCharactersEnabled =
                    challengeOptions.ServerCharactersEnabled;
                session.State = ConnectionSessionState.Challenged;
                session.LastSequence = ProtocolSequence.Challenge;
                RefreshPhaseDeadline(session, clock.GetTimestamp());
                return ProtocolOperationResult.Success(Snapshot(session));
            }
        }

        /// <summary>
        /// Validates a manifest response and dispatches ManifestAccepted using the same session ID
        /// and nonce. ManifestValidated is committed only after the acceptance packet is sent.
        /// </summary>
        public ProtocolOperationResult AcceptManifest(
            ZNet server,
            ZRpc rpc,
            ProtocolPacket packet,
            IManifestValidator validator,
            ProtocolPacketSender sender)
        {
            if (packet == null)
            {
                return RejectCurrentSession(
                    rpc,
                    new ProtocolRejection(
                        ProtocolRejectCode.MalformedPacket,
                        "The manifest response packet was missing."));
            }

            if (validator == null)
            {
                throw new ArgumentNullException(nameof(validator));
            }

            if (sender == null)
            {
                throw new ArgumentNullException(nameof(sender));
            }

            ServerPeerIdentity peerIdentity;
            ProtocolRejection resolutionError;
            if (!ServerPeerResolver.TryResolve(
                    server,
                    rpc,
                    out peerIdentity,
                    out resolutionError))
            {
                return ProtocolOperationResult.Reject(resolutionError);
            }

            Session session;
            lock (sync)
            {
                ThrowIfDisposed();
                ProtocolOperationResult<Session> validation = ValidateIncomingLocked(
                    rpc,
                    packet,
                    ConnectionSessionState.Challenged,
                    ProtocolPacketKind.ManifestResponse,
                    ProtocolSequence.ManifestResponse);
                if (!validation.Succeeded)
                {
                    return ProtocolOperationResult.Reject(
                        validation.Rejection,
                        validation.Session);
                }

                session = validation.Value;
                if (session.OperationInProgress)
                {
                    ProtocolRejection duplicate = new ProtocolRejection(
                        ProtocolRejectCode.DuplicateMessage,
                        "A duplicate manifest response was received.");
                    MarkRejected(session, duplicate);
                    return ProtocolOperationResult.Reject(duplicate, Snapshot(session));
                }

                session.OperationInProgress = true;
            }

            ManifestValidationDecision decision;
            try
            {
                decision = validator.Validate(
                    peerIdentity,
                    ProtocolByteUtil.Clone(packet.Payload));
            }
            catch
            {
                decision = ManifestValidationDecision.Reject(
                    "The server could not validate the client manifest.",
                    ProtocolRejectCode.ManifestValidatorFailed);
            }

            if (decision == null || !decision.Accepted)
            {
                ProtocolRejection rejection =
                    decision != null && decision.Rejection != null
                        ? decision.Rejection
                        : new ProtocolRejection(
                            ProtocolRejectCode.ManifestRejected,
                            "The client manifest was rejected.");
                return FailReservedOperation(session, rejection);
            }

            try
            {
                ZPackage accepted = ProtocolPacketCodec.CreateManifestAccepted(
                    session.SessionId,
                    session.Nonce,
                    protocolLimits);
                sender(rpc, accepted);
            }
            catch
            {
                return FailReservedOperation(
                    session,
                    new ProtocolRejection(
                        ProtocolRejectCode.InternalError,
                        "The server could not acknowledge the client manifest."));
            }

            lock (sync)
            {
                if (!IsCurrentReservedSession(session, ConnectionSessionState.Challenged))
                {
                    return ConcurrentOperationFailure(session);
                }

                session.OperationInProgress = false;
                session.State = ConnectionSessionState.ManifestValidated;
                session.LastSequence = ProtocolSequence.ManifestAccepted;
                RefreshPhaseDeadline(session, clock.GetTimestamp());
                return ProtocolOperationResult.Success(Snapshot(session));
            }
        }

        /// <summary>
        /// RPC_PeerInfo prefix integration point.
        ///
        /// - Connected/Challenged: reject.
        /// - ManifestValidated/CharacterSent/Ready: allow exactly once.
        /// - Rejected/expired/duplicate: reject.
        ///
        /// Returning Allow does NOT mean world traffic may be released. Keep the peer's outbound
        /// BufferingSocket active until CanReleaseWorld is true.
        /// </summary>
        public PeerInfoGateResult GatePeerInfo(
            ZNet server,
            ZRpc rpc)
        {
            ZNetPeer peer;
            ProtocolRejection resolutionError;
            if (!ServerPeerResolver.TryResolvePeer(
                    server,
                    rpc,
                    out peer,
                    out resolutionError))
            {
                return PeerInfoGateResult.Reject(resolutionError);
            }

            lock (sync)
            {
                ThrowIfDisposed();
                Session session;
                ProtocolRejection sessionError;
                if (!TryGetUsableSessionLocked(rpc, out session, out sessionError))
                {
                    return PeerInfoGateResult.Reject(
                        sessionError,
                        session == null ? null : Snapshot(session));
                }

                if (session.State == ConnectionSessionState.Connected ||
                    session.State == ConnectionSessionState.Challenged)
                {
                    ProtocolRejection early = new ProtocolRejection(
                        ProtocolRejectCode.PeerInfoTooEarly,
                        "Peer information arrived before manifest validation.");
                    MarkRejected(session, early);
                    return PeerInfoGateResult.Reject(early, Snapshot(session));
                }

                if (session.PeerInfoAdmitted)
                {
                    ProtocolRejection duplicate = new ProtocolRejection(
                        ProtocolRejectCode.PeerInfoAlreadyProcessed,
                        "Duplicate peer information was received.");
                    MarkRejected(session, duplicate);
                    return PeerInfoGateResult.Reject(duplicate, Snapshot(session));
                }

                session.PeerInfoAdmitted = true;
                return PeerInfoGateResult.Allow(Snapshot(session));
            }
        }

        /// <summary>
        /// Call only after vanilla RPC_PeerInfo established the selected player name/Steam
        /// identity and Steam's final auth-ticket callback succeeded. Character preparation is
        /// blocked until this succeeds.
        /// </summary>
        public ProtocolOperationResult ConfirmPeerInfoAuthenticated(ZNet server, ZRpc rpc)
        {
            ServerPeerIdentity identity;
            ProtocolRejection resolutionError;
            if (!ServerPeerResolver.TryResolve(
                    server,
                    rpc,
                    out identity,
                    out resolutionError))
            {
                return RejectCurrentSession(rpc, resolutionError);
            }

            lock (sync)
            {
                ThrowIfDisposed();
                Session session;
                ProtocolRejection sessionError;
                if (!TryGetUsableSessionLocked(rpc, out session, out sessionError))
                {
                    return ProtocolOperationResult.Reject(
                        sessionError,
                        session == null ? null : Snapshot(session));
                }

                if (session.State != ConnectionSessionState.ManifestValidated ||
                    !session.PeerInfoAdmitted)
                {
                    ProtocolRejection invalid = new ProtocolRejection(
                        ProtocolRejectCode.InvalidTransition,
                        "Peer authentication completed in an invalid protocol state.");
                    MarkRejected(session, invalid);
                    return ProtocolOperationResult.Reject(invalid, Snapshot(session));
                }

                if (session.PeerInfoAuthenticated)
                {
                    ProtocolRejection duplicate = new ProtocolRejection(
                        ProtocolRejectCode.PeerInfoAlreadyProcessed,
                        "Peer authentication was completed more than once.");
                    MarkRejected(session, duplicate);
                    return ProtocolOperationResult.Reject(duplicate, Snapshot(session));
                }

                if (!identity.HasAuthenticatedIdentity)
                {
                    ProtocolRejection incomplete = new ProtocolRejection(
                        ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                        "Vanilla peer authentication did not establish a server identity.");
                    MarkRejected(session, incomplete);
                    return ProtocolOperationResult.Reject(incomplete, Snapshot(session));
                }

                session.PeerInfoAuthenticated = true;
                RefreshPhaseDeadline(session, clock.GetTimestamp());
                return ProtocolOperationResult.Success(Snapshot(session));
            }
        }

        /// <summary>
        /// Creates bounded character fragments but does not advance to CharacterSent. The caller
        /// may send Packets over multiple coroutine frames and must call ConfirmCharacterSent only
        /// after every packet has been handed to the peer transport.
        /// </summary>
        public ProtocolOperationResult<CharacterTransferPlan> PrepareCharacterTransfer(
            ZNet server,
            ZRpc rpc,
            byte[] characterPayload,
            bool preferCompression)
        {
            ProtocolOperationResult<Session> reserved = ReserveOperation(
                server,
                rpc,
                ConnectionSessionState.ManifestValidated,
                requirePeerInfoAuthentication: true,
                rejectWhenOperationBusy: ProtocolRejectCode.CharacterTransferAlreadyPrepared);
            if (!reserved.Succeeded)
            {
                return ProtocolOperationResult<CharacterTransferPlan>.Reject(
                    reserved.Rejection,
                    reserved.Session);
            }

            Session session = reserved.Value;
            CharacterTransferPlan plan;
            try
            {
                plan = BoundedFragmentCodec.CreateCharacterTransfer(
                    session.SessionId,
                    session.Nonce,
                    characterPayload,
                    preferCompression,
                    protocolLimits,
                    fragmentLimits);
            }
            catch (ArgumentOutOfRangeException)
            {
                ProtocolOperationResult failure = FailReservedOperation(
                    session,
                    new ProtocolRejection(
                        ProtocolRejectCode.PayloadTooLarge,
                        "The server character payload exceeded the allowed size."));
                return ProtocolOperationResult<CharacterTransferPlan>.Reject(
                    failure.Rejection,
                    failure.Session);
            }
            catch
            {
                ProtocolOperationResult failure = FailReservedOperation(
                    session,
                    new ProtocolRejection(
                        ProtocolRejectCode.CharacterTransferFailed,
                        "The server could not prepare the character transfer."));
                return ProtocolOperationResult<CharacterTransferPlan>.Reject(
                    failure.Rejection,
                    failure.Session);
            }

            lock (sync)
            {
                if (!IsCurrentReservedSession(
                        session,
                        ConnectionSessionState.ManifestValidated))
                {
                    ProtocolOperationResult concurrent = ConcurrentOperationFailure(session);
                    return ProtocolOperationResult<CharacterTransferPlan>.Reject(
                        concurrent.Rejection,
                        concurrent.Session);
                }

                session.OperationInProgress = false;
                session.CharacterTransferPrepared = true;
                session.CharacterMessageId = ProtocolByteUtil.Clone(plan.MessageId);
                RefreshPhaseDeadline(session, clock.GetTimestamp());
                return ProtocolOperationResult<CharacterTransferPlan>.Success(
                    plan,
                    Snapshot(session));
            }
        }

        public ProtocolOperationResult ConfirmCharacterSent(
            ZNet server,
            ZRpc rpc,
            byte[] messageId)
        {
            ZNetPeer peer;
            ProtocolRejection resolutionError;
            if (!ServerPeerResolver.TryResolvePeer(
                    server,
                    rpc,
                    out peer,
                    out resolutionError))
            {
                return ProtocolOperationResult.Reject(resolutionError);
            }

            lock (sync)
            {
                ThrowIfDisposed();
                Session session;
                ProtocolRejection sessionError;
                if (!TryGetUsableSessionLocked(rpc, out session, out sessionError))
                {
                    return ProtocolOperationResult.Reject(
                        sessionError,
                        session == null ? null : Snapshot(session));
                }

                if (session.State != ConnectionSessionState.ManifestValidated)
                {
                    return RejectInvalidTransition(session, ConnectionSessionState.ManifestValidated);
                }

                if (!session.CharacterTransferPrepared)
                {
                    ProtocolRejection missing = new ProtocolRejection(
                        ProtocolRejectCode.CharacterTransferNotPrepared,
                        "The character transfer was not prepared.");
                    MarkRejected(session, missing);
                    return ProtocolOperationResult.Reject(missing, Snapshot(session));
                }

                if (!ProtocolByteUtil.FixedTimeEquals(
                        session.CharacterMessageId,
                        messageId))
                {
                    ProtocolRejection mismatch = new ProtocolRejection(
                        ProtocolRejectCode.CharacterTransferIdMismatch,
                        "The character transfer ID was invalid.");
                    MarkRejected(session, mismatch);
                    return ProtocolOperationResult.Reject(mismatch, Snapshot(session));
                }

                session.State = ConnectionSessionState.CharacterSent;
                session.LastSequence = ProtocolSequence.CharacterData;
                RefreshPhaseDeadline(session, clock.GetTimestamp());
                return ProtocolOperationResult.Success(Snapshot(session));
            }
        }

        public ProtocolOperationResult AcceptReadyAck(
            ZNet server,
            ZRpc rpc,
            ProtocolPacket packet)
        {
            if (packet == null)
            {
                return RejectCurrentSession(
                    rpc,
                    new ProtocolRejection(
                        ProtocolRejectCode.MalformedPacket,
                        "The ready acknowledgement packet was missing."));
            }

            ZNetPeer peer;
            ProtocolRejection resolutionError;
            if (!ServerPeerResolver.TryResolvePeer(
                    server,
                    rpc,
                    out peer,
                    out resolutionError))
            {
                return ProtocolOperationResult.Reject(resolutionError);
            }

            lock (sync)
            {
                ThrowIfDisposed();
                ProtocolOperationResult<Session> validation = ValidateIncomingLocked(
                    rpc,
                    packet,
                    ConnectionSessionState.CharacterSent,
                    ProtocolPacketKind.ReadyAck,
                    ProtocolSequence.ReadyAck);
                if (!validation.Succeeded)
                {
                    return ProtocolOperationResult.Reject(
                        validation.Rejection,
                        validation.Session);
                }

                Session session = validation.Value;
                if (!ProtocolByteUtil.FixedTimeEquals(
                        session.CharacterMessageId,
                        packet.Payload))
                {
                    ProtocolRejection mismatch = new ProtocolRejection(
                        ProtocolRejectCode.CharacterTransferIdMismatch,
                        "The ready acknowledgement referenced the wrong character transfer.");
                    MarkRejected(session, mismatch);
                    return ProtocolOperationResult.Reject(mismatch, Snapshot(session));
                }

                session.State = ConnectionSessionState.Ready;
                session.LastSequence = ProtocolSequence.ReadyAck;
                session.PhaseDeadline = long.MaxValue;
                return ProtocolOperationResult.Success(Snapshot(session));
            }
        }

        // A valid refusal is terminal, never an alternative Ready ACK. The
        // fresh flag is pinned by the server when preparing this exact transfer.
        internal ProtocolOperationResult AcceptClientCharacterRejection(ZNet server, ZRpc rpc,
            ProtocolPacket packet, bool requiresFreshLocalCharacter)
        {
            if (packet == null)
                return RejectCurrentSession(rpc, new ProtocolRejection(ProtocolRejectCode.MalformedPacket,
                    "The client character refusal was missing."));
            if (!ServerPeerResolver.TryResolvePeer(server, rpc, out _, out ProtocolRejection resolutionError))
                return ProtocolOperationResult.Reject(resolutionError);
            lock (sync)
            {
                ThrowIfDisposed();
                ProtocolOperationResult<Session> validation = ValidateIncomingLocked(rpc, packet,
                    ConnectionSessionState.CharacterSent, ProtocolPacketKind.ClientCharacterRejection,
                    ProtocolSequence.ClientCharacterRejection);
                if (!validation.Succeeded)
                    return ProtocolOperationResult.Reject(validation.Rejection, validation.Session);
                Session session = validation.Value;
                if (!session.PeerInfoAuthenticated || !session.ServerCharactersEnabled ||
                    !requiresFreshLocalCharacter || packet.Payload.Length != 1 + ConnectionProtocolLimits.MessageIdBytes ||
                    packet.Payload[0] != 1)
                {
                    ProtocolRejection invalid = new ProtocolRejection(ProtocolRejectCode.InvalidTransition,
                        "The client character refusal was not valid for this initial character.");
                    MarkRejected(session, invalid);
                    return ProtocolOperationResult.Reject(invalid, Snapshot(session));
                }
                byte[] messageId = new byte[ConnectionProtocolLimits.MessageIdBytes];
                Buffer.BlockCopy(packet.Payload, 1, messageId, 0, messageId.Length);
                if (!ProtocolByteUtil.FixedTimeEquals(session.CharacterMessageId, messageId))
                {
                    ProtocolRejection mismatch = new ProtocolRejection(ProtocolRejectCode.CharacterTransferIdMismatch,
                        "The client character refusal referenced the wrong transfer.");
                    MarkRejected(session, mismatch);
                    return ProtocolOperationResult.Reject(mismatch, Snapshot(session));
                }
                ProtocolRejection refusal = new ProtocolRejection(ProtocolRejectCode.ClientCharacterRejected,
                    "The client refused the initial character because its selected local character has world history.")
                    .WithConnectionAudit("character", "fresh_local_character_required",
                        "Client reported the used-local-character guard before Ready ACK. This report can be forged or omitted by a modified client.",
                        stage: "character_apply");
                MarkRejected(session, refusal);
                return ProtocolOperationResult.Reject(refusal, Snapshot(session));
            }
        }

        /// <summary>
        /// The BufferingSocket may release outbound PeerInfo/RoutedRPC/ZDOData only when true.
        /// ManifestValidated is intentionally insufficient.
        /// </summary>
        public bool CanReleaseWorld(
            ZNet server,
            ZRpc rpc,
            out ProtocolRejection rejection)
        {
            rejection = null;
            ZNetPeer peer;
            if (!ServerPeerResolver.TryResolvePeer(
                    server,
                    rpc,
                    out peer,
                    out rejection))
            {
                return false;
            }

            lock (sync)
            {
                Session session;
                if (!TryGetUsableSessionLocked(rpc, out session, out rejection))
                {
                    return false;
                }

                if (session.State == ConnectionSessionState.Ready)
                {
                    return true;
                }

                rejection = new ProtocolRejection(
                    ProtocolRejectCode.WorldReleaseNotReady,
                    "World synchronization is waiting for character readiness.",
                    disconnect: false);
                return false;
            }
        }

        // Poll readiness without cloning session secrets. Keep the same expiry
        // observation as a full snapshot; policy polling must not defer it.
        internal bool IsReadyAndAuthenticated(ZRpc rpc)
        {
            if (rpc == null)
            {
                return false;
            }

            lock (sync)
            {
                return TryGetUsableSessionLocked(rpc, out Session session, out _) &&
                       session.State == ConnectionSessionState.Ready &&
                       session.PeerInfoAuthenticated;
            }
        }

        public bool TryGetSnapshot(ZRpc rpc, out ConnectionSessionSnapshot snapshot)
        {
            snapshot = null;
            if (rpc == null)
            {
                return false;
            }

            lock (sync)
            {
                ThrowIfDisposed();
                Session session;
                if (!sessions.TryGetValue(rpc, out session))
                {
                    return false;
                }

                ExpireIfNeeded(session, clock.GetTimestamp());
                snapshot = Snapshot(session);
                return true;
            }
        }

        public ProtocolOperationResult RejectSession(
            ZRpc rpc,
            ProtocolRejection rejection)
        {
            if (rejection == null)
            {
                throw new ArgumentNullException(nameof(rejection));
            }

            return RejectCurrentSession(rpc, rejection);
        }

        public ZPackage CreateRejectPacket(ZRpc rpc, ProtocolRejection rejection)
        {
            if (rejection == null)
            {
                throw new ArgumentNullException(nameof(rejection));
            }

            byte[] sessionId = new byte[ConnectionProtocolLimits.SessionIdBytes];
            byte[] nonce = new byte[ConnectionProtocolLimits.NonceBytes];
            lock (sync)
            {
                ThrowIfDisposed();
                Session session;
                if (rpc != null && sessions.TryGetValue(rpc, out session))
                {
                    sessionId = ProtocolByteUtil.Clone(session.SessionId);
                    nonce = ProtocolByteUtil.Clone(session.Nonce);
                }
            }

            return ProtocolPacketCodec.CreateReject(
                sessionId,
                nonce,
                rejection,
                protocolLimits);
        }

        public ReadOnlyCollection<ConnectionSessionSnapshot> ExpireTimedOutSessions()
        {
            List<ConnectionSessionSnapshot> expired = null;
            lock (sync)
            {
                ThrowIfDisposed();
                long now = clock.GetTimestamp();
                foreach (Session session in sessions.Values)
                {
                    if (session.State != ConnectionSessionState.Ready &&
                        session.State != ConnectionSessionState.Rejected &&
                        IsExpired(session, now))
                    {
                        MarkRejected(
                            session,
                            new ProtocolRejection(
                                ProtocolRejectCode.HandshakeTimedOut,
                                "The connection handshake timed out."));
                        if (expired == null)
                        {
                            expired = new List<ConnectionSessionSnapshot>();
                        }

                        expired.Add(Snapshot(session));
                    }
                }
            }

            return expired == null
                ? NoExpiredSessions
                : new ReadOnlyCollection<ConnectionSessionSnapshot>(expired);
        }

        /// <summary>
        /// Call from ZNet.Disconnect. This zeroes session secrets and releases held PeerInfo bytes.
        /// </summary>
        public bool RemoveSession(ZRpc rpc)
        {
            if (rpc == null)
            {
                return false;
            }

            lock (sync)
            {
                ThrowIfDisposed();
                Session session;
                if (!sessions.TryGetValue(rpc, out session))
                {
                    return false;
                }

                sessions.Remove(rpc);
                ClearSession(session);
                return true;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                foreach (Session session in sessions.Values)
                {
                    ClearSession(session);
                }

                sessions.Clear();
                disposed = true;
            }
        }

        private ProtocolOperationResult<Session> ReserveOperation(
            ZNet server,
            ZRpc rpc,
            ConnectionSessionState requiredState,
            bool requirePeerInfoAuthentication = false,
            ProtocolRejectCode rejectWhenOperationBusy = ProtocolRejectCode.DuplicateMessage)
        {
            ZNetPeer peer;
            ProtocolRejection resolutionError;
            if (!ServerPeerResolver.TryResolvePeer(
                    server,
                    rpc,
                    out peer,
                    out resolutionError))
            {
                return ProtocolOperationResult<Session>.Reject(resolutionError);
            }

            lock (sync)
            {
                ThrowIfDisposed();
                Session session;
                ProtocolRejection sessionError;
                if (!TryGetUsableSessionLocked(rpc, out session, out sessionError))
                {
                    return ProtocolOperationResult<Session>.Reject(
                        sessionError,
                        session == null ? null : Snapshot(session));
                }

                if (session.State != requiredState)
                {
                    ProtocolOperationResult invalid = RejectInvalidTransition(
                        session,
                        requiredState);
                    return ProtocolOperationResult<Session>.Reject(
                        invalid.Rejection,
                        invalid.Session);
                }

                if (requirePeerInfoAuthentication && !session.PeerInfoAuthenticated)
                {
                    ProtocolRejection incomplete = new ProtocolRejection(
                        ProtocolRejectCode.PeerInfoAuthenticationIncomplete,
                        "Character data cannot be sent before vanilla peer authentication.");
                    MarkRejected(session, incomplete);
                    return ProtocolOperationResult<Session>.Reject(
                        incomplete,
                        Snapshot(session));
                }

                if (session.OperationInProgress || session.CharacterTransferPrepared)
                {
                    ProtocolRejection duplicate = new ProtocolRejection(
                        rejectWhenOperationBusy,
                        "A duplicate protocol operation was attempted.");
                    MarkRejected(session, duplicate);
                    return ProtocolOperationResult<Session>.Reject(
                        duplicate,
                        Snapshot(session));
                }

                session.OperationInProgress = true;
                return ProtocolOperationResult<Session>.Success(
                    session,
                    Snapshot(session));
            }
        }

        private ProtocolOperationResult<Session> ValidateIncomingLocked(
            ZRpc rpc,
            ProtocolPacket packet,
            ConnectionSessionState requiredState,
            ProtocolPacketKind requiredKind,
            uint requiredSequence)
        {
            Session session;
            ProtocolRejection sessionError;
            if (!TryGetUsableSessionLocked(rpc, out session, out sessionError))
            {
                return ProtocolOperationResult<Session>.Reject(
                    sessionError,
                    session == null ? null : Snapshot(session));
            }

            if (!ProtocolByteUtil.FixedTimeEquals(session.SessionId, packet.SessionId))
            {
                ProtocolRejection mismatch = new ProtocolRejection(
                    ProtocolRejectCode.SessionIdMismatch,
                    "The protocol session ID was invalid.");
                MarkRejected(session, mismatch);
                return ProtocolOperationResult<Session>.Reject(
                    mismatch,
                    Snapshot(session));
            }

            if (!ProtocolByteUtil.FixedTimeEquals(session.Nonce, packet.Nonce))
            {
                ProtocolRejection mismatch = new ProtocolRejection(
                    ProtocolRejectCode.NonceMismatch,
                    "The protocol nonce was invalid.");
                MarkRejected(session, mismatch);
                return ProtocolOperationResult<Session>.Reject(
                    mismatch,
                    Snapshot(session));
            }

            if (packet.Sequence <= session.LastSequence)
            {
                ProtocolRejection duplicate = new ProtocolRejection(
                    ProtocolRejectCode.DuplicateMessage,
                    "A duplicate protocol message was received.");
                MarkRejected(session, duplicate);
                return ProtocolOperationResult<Session>.Reject(
                    duplicate,
                    Snapshot(session));
            }

            if (packet.Sequence != requiredSequence)
            {
                ProtocolRejection order = new ProtocolRejection(
                    ProtocolRejectCode.OutOfOrderMessage,
                    "A protocol message arrived out of order.");
                MarkRejected(session, order);
                return ProtocolOperationResult<Session>.Reject(
                    order,
                    Snapshot(session));
            }

            if (packet.Kind != requiredKind)
            {
                ProtocolRejection kind = new ProtocolRejection(
                    ProtocolRejectCode.UnexpectedMessageType,
                    "An unexpected protocol message was received.");
                MarkRejected(session, kind);
                return ProtocolOperationResult<Session>.Reject(
                    kind,
                    Snapshot(session));
            }

            if (session.State != requiredState)
            {
                ProtocolOperationResult invalid = RejectInvalidTransition(
                    session,
                    requiredState);
                return ProtocolOperationResult<Session>.Reject(
                    invalid.Rejection,
                    invalid.Session);
            }

            return ProtocolOperationResult<Session>.Success(
                session,
                Snapshot(session));
        }

        private bool TryGetUsableSessionLocked(
            ZRpc rpc,
            out Session session,
            out ProtocolRejection rejection)
        {
            ThrowIfDisposed();
            session = null;
            rejection = null;
            if (rpc == null || !sessions.TryGetValue(rpc, out session))
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.SessionNotFound,
                    "The connection session was not found.");
                return false;
            }

            ExpireIfNeeded(session, clock.GetTimestamp());
            if (session.State == ConnectionSessionState.Rejected)
            {
                rejection = session.Rejection ?? new ProtocolRejection(
                    ProtocolRejectCode.SessionAlreadyRejected,
                    "The connection session was already rejected.");
                return false;
            }

            return true;
        }

        private ProtocolOperationResult RejectInvalidTransition(
            Session session,
            ConnectionSessionState requiredState)
        {
            ProtocolRejection invalid = new ProtocolRejection(
                ProtocolRejectCode.InvalidTransition,
                "The connection protocol state transition was invalid.");
            MarkRejected(session, invalid);
            return ProtocolOperationResult.Reject(invalid, Snapshot(session));
        }

        private ProtocolOperationResult RejectCurrentSession(
            ZRpc rpc,
            ProtocolRejection rejection)
        {
            lock (sync)
            {
                ThrowIfDisposed();
                Session session;
                if (rpc == null || !sessions.TryGetValue(rpc, out session))
                {
                    return ProtocolOperationResult.Reject(rejection);
                }

                MarkRejected(session, rejection);
                return ProtocolOperationResult.Reject(rejection, Snapshot(session));
            }
        }

        private ProtocolOperationResult FailReservedOperation(
            Session session,
            ProtocolRejection rejection)
        {
            lock (sync)
            {
                if (IsCurrentSessionLocked(session))
                {
                    ExpireIfNeeded(session, clock.GetTimestamp());
                    if (session.State == ConnectionSessionState.Rejected &&
                        session.Rejection != null)
                    {
                        return ProtocolOperationResult.Reject(
                            session.Rejection,
                            Snapshot(session));
                    }

                    session.OperationInProgress = false;
                    MarkRejected(session, rejection);
                    return ProtocolOperationResult.Reject(
                        rejection,
                        Snapshot(session));
                }

                return ProtocolOperationResult.Reject(rejection);
            }
        }

        private ProtocolOperationResult ConcurrentOperationFailure(Session session)
        {
            ProtocolRejection rejection = new ProtocolRejection(
                ProtocolRejectCode.DuplicateMessage,
                "The connection session changed during protocol processing.");
            if (IsCurrentSessionLocked(session))
            {
                if (session.State == ConnectionSessionState.Rejected &&
                    session.Rejection != null)
                {
                    return ProtocolOperationResult.Reject(
                        session.Rejection,
                        Snapshot(session));
                }

                MarkRejected(session, rejection);
                return ProtocolOperationResult.Reject(rejection, Snapshot(session));
            }

            return ProtocolOperationResult.Reject(rejection);
        }

        private bool IsCurrentReservedSession(
            Session session,
            ConnectionSessionState state)
        {
            if (!IsCurrentSessionLocked(session))
            {
                return false;
            }

            ExpireIfNeeded(session, clock.GetTimestamp());
            return session.State == state && session.OperationInProgress;
        }

        private bool IsCurrentSessionLocked(Session session)
        {
            Session current;
            return sessions.TryGetValue(session.Rpc, out current) &&
                ReferenceEquals(current, session);
        }

        private void ExpireIfNeeded(Session session, long now)
        {
            if (session.State != ConnectionSessionState.Ready &&
                session.State != ConnectionSessionState.Rejected &&
                IsExpired(session, now))
            {
                MarkRejected(
                    session,
                    new ProtocolRejection(
                        ProtocolRejectCode.HandshakeTimedOut,
                        "The connection handshake timed out."));
            }
        }

        private bool IsExpired(Session session, long now)
        {
            return now > session.PhaseDeadline || now > session.AbsoluteDeadline;
        }

        private void RefreshPhaseDeadline(Session session, long now)
        {
            long phaseDeadline = AddDuration(now, protocolLimits.PhaseTimeout);
            session.PhaseDeadline = Math.Min(phaseDeadline, session.AbsoluteDeadline);
        }

        private long AddDuration(long timestamp, TimeSpan duration)
        {
            double clockTicks = duration.TotalSeconds * clock.Frequency;
            if (clockTicks <= 0 || clockTicks > long.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            long delta = (long)Math.Ceiling(clockTicks);
            if (timestamp > long.MaxValue - delta)
            {
                return long.MaxValue;
            }

            return timestamp + delta;
        }

        private static ConnectionSessionSnapshot Snapshot(Session session)
        {
            return new ConnectionSessionSnapshot(
                session.Rpc,
                session.State,
                session.SessionId,
                session.Nonce,
                session.LastSequence,
                session.PeerInfoAdmitted,
                session.PeerInfoAuthenticated,
                session.ServerCharactersEnabled,
                session.CharacterTransferPrepared,
                session.CharacterMessageId,
                session.CreatedTimestamp,
                session.PhaseDeadline,
                session.Rejection)
            {
                StateBeforeRejection = session.State == ConnectionSessionState.Rejected
                    ? session.StateBeforeRejection : session.State
            };
        }

        private static void MarkRejected(Session session, ProtocolRejection rejection)
        {
            if (session.State != ConnectionSessionState.Rejected)
                session.StateBeforeRejection = session.State;
            session.State = ConnectionSessionState.Rejected;
            session.Rejection = rejection;
            session.OperationInProgress = false;
        }

        private static void ClearSession(Session session)
        {
            ClearBytes(session.SessionId);
            ClearBytes(session.Nonce);
            ClearBytes(session.CharacterMessageId);
            session.Rejection = null;
        }

        private static void ClearBytes(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ConnectionSessionCoordinator));
            }
        }
    }
}
