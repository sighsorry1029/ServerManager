#nullable disable

using System;
using System.Globalization;

namespace ServerManager
{
    /// <summary>
    /// Identity derived from the server's connection reservation and live ZNetPeer.
    /// Neither RPC payloads nor socket-wrapper host names supply account identity.
    /// </summary>
    public sealed class ServerPeerIdentity
    {
        internal ServerPeerIdentity(
            ZNetPeer peer,
            string hostId,
            string endpoint,
            string playerName)
        {
            Peer = peer;
            Rpc = peer.m_rpc;
            PeerUid = peer.m_uid;
            HostId = hostId;
            Endpoint = endpoint;
            PlayerName = playerName;
        }

        public ZNetPeer Peer { get; private set; }

        public ZRpc Rpc { get; private set; }

        /// <summary>
        /// Vanilla session UID copied from RPC_PeerInfo. It is client-originated and is useful
        /// only as an admission-progress signal; never use it as an account/persistence key.
        /// </summary>
        public long PeerUid { get; private set; }

        /// <summary>
        /// Steamworks identity pinned to the original server transport at reservation.
        /// </summary>
        public string HostId { get; private set; }

        /// <summary>
        /// Logging-only endpoint. Do not use it as a persistence key.
        /// </summary>
        public string Endpoint { get; private set; }

        /// <summary>
        /// Empty before vanilla RPC_PeerInfo successfully establishes the selected profile.
        /// It is never taken from a custom protocol payload.
        /// </summary>
        public string PlayerName { get; private set; }

        public bool HasAuthenticatedPlayerName
        {
            get { return !string.IsNullOrWhiteSpace(PlayerName); }
        }

        /// <summary>
        /// True only after vanilla PeerInfo passed its synchronous admission checks and populated
        /// the session UID and selected profile. This is not proof of the final Steam callback;
        /// authorization still requires the runtime's active authentication and Ready checks.
        /// HostId is reserved by the server; PeerUid and PlayerName are client-originated values.
        /// </summary>
        public bool HasAuthenticatedIdentity
        {
            get
            {
                return PeerUid != 0L &&
                       !string.IsNullOrWhiteSpace(HostId) &&
                       HasAuthenticatedPlayerName;
            }
        }
    }

    public static class ServerPeerResolver
    {
        public static bool TryResolvePeer(
            ZNet server,
            ZRpc rpc,
            out ZNetPeer peer,
            out ProtocolRejection rejection)
        {
            peer = null;
            rejection = null;

            if (server == null || !server.IsServer())
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.NotServer,
                    "The connection protocol can only be handled by the server.");
                return false;
            }

            if (ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PeerIdentityUnavailable,
                    "ServerManager peer identities require the Steamworks backend.");
                return false;
            }

            if (rpc == null)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PeerNotFound,
                    "The connection peer was unavailable.");
                return false;
            }

            try
            {
                peer = ValheimPrivateAccess.FindPeer(server, rpc);
            }
            catch
            {
                peer = null;
            }

            if (peer == null || !ReferenceEquals(peer.m_rpc, rpc))
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PeerNotFound,
                    "The connection peer was no longer registered.");
                return false;
            }

            return true;
        }

        public static bool TryResolve(
            ZNet server,
            ZRpc rpc,
            out ServerPeerIdentity identity,
            out ProtocolRejection rejection)
        {
            identity = null;
            if (!ServerManagerRuntime.TryGetSteamConnection(server, rpc,
                    out ServerManagerRuntime.SteamAuthenticationAttempt connection,
                    out rejection))
            {
                return false;
            }

            ZNetPeer peer = connection.Peer;
            string hostId = connection.SteamId.m_SteamID.ToString(CultureInfo.InvariantCulture);
            string endpoint = string.Empty;
            try
            {
                endpoint = connection.Socket.GetEndPointString() ?? string.Empty;
            }
            catch
            {
                // Endpoint is logging-only. The reserved Steam ID was already
                // revalidated; never fall back to an outer wrapper's host name.
            }

            identity = new ServerPeerIdentity(
                peer,
                hostId,
                endpoint,
                peer.m_playerName ?? string.Empty);
            rejection = null;
            return true;
        }
    }
}
