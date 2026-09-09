#nullable disable

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace ServerManager
{
    /// <summary>
    /// Preflights vanilla's Register&lt;ZPackage&gt; PeerInfo method before its
    /// length-prefixed inner package can be allocated.
    ///
    /// RawProtocolRpcTransport locates the body after the method hash (and the
    /// optional debug method name). That body starts with ZRpc's serialized
    /// ZPackage length prefix and remains untouched when validation succeeds.
    /// </summary>
    public static class BoundedPeerInfoRpcTransport
    {
        private const string PeerInfoMethodName = "PeerInfo";
        private const int OuterLengthPrefixBytes = sizeof(int);
        private const int MaximumPeerInfoBytes = 512 * 1024;
        private const int MaximumStringBytes = 4 * 1024;
        private const int MaximumSteamTicketBytes = 64 * 1024;
        private static readonly ConditionalWeakTable<ZRpc, object> Installed = new();

        /// <summary>
        /// Attaches a preflight to the vanilla PeerInfo handler registered by
        /// ZNet.OnNewConnection. Calling this method again while it is already
        /// installed is a no-op.
        /// </summary>
        public static void Install(
            ZNet znet,
            ZRpc rpc,
            ConnectionProtocolLimits limits,
            RawProtocolTransportErrorHandler errorHandler = null)
        {
            if (znet == null)
            {
                throw new ArgumentNullException(nameof(znet));
            }

            if (rpc == null)
            {
                throw new ArgumentNullException(nameof(rpc));
            }

            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            if (Installed.TryGetValue(rpc, out _))
            {
                return;
            }

            int maximumBytes = Math.Min(
                MaximumPeerInfoBytes,
                limits.MaxPeerInfoBytes);
            bool receivingOnServer = znet.IsServer();
            RawProtocolRpcTransport.RegisterPreflight(
                rpc,
                PeerInfoMethodName,
                checked(maximumBytes + OuterLengthPrefixBytes),
                (ZRpc _,
                    byte[] source,
                    int offset,
                    int length,
                    out ProtocolRejection rejection) =>
                    TryPreflightPeerInfoEnvelope(
                        source,
                        offset,
                        length,
                        receivingOnServer,
                        maximumBytes,
                        out rejection),
                errorHandler);
            Installed.Add(rpc, new object());
        }

        private static bool TryPreflightPeerInfoEnvelope(
            byte[] source,
            int offset,
            int length,
            bool receivingOnServer,
            int maximumPeerInfoBytes,
            out ProtocolRejection rejection)
        {
            if (length < OuterLengthPrefixBytes)
            {
                return Malformed(
                    "The PeerInfo RPC package length was missing.",
                    out rejection);
            }

            if (length > maximumPeerInfoBytes + OuterLengthPrefixBytes)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PayloadTooLarge,
                    "The PeerInfo RPC body exceeded the allowed size.");
                return false;
            }

            int declaredLength = ReadInt32LittleEndian(source, offset);
            if (declaredLength < 0)
            {
                return Malformed(
                    "The PeerInfo package length was negative.",
                    out rejection);
            }

            if (declaredLength > maximumPeerInfoBytes)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PayloadTooLarge,
                    "The PeerInfo package exceeded the allowed size.");
                return false;
            }

            int actualLength = length - OuterLengthPrefixBytes;
            if (declaredLength != actualLength)
            {
                rejection = new ProtocolRejection(
                    declaredLength < actualLength
                        ? ProtocolRejectCode.TrailingPacketData
                        : ProtocolRejectCode.MalformedPacket,
                    "The PeerInfo package length did not match its RPC body.");
                return false;
            }

            return TryPreflightPeerInfo(
                source,
                offset + OuterLengthPrefixBytes,
                declaredLength,
                receivingOnServer,
                out rejection);
        }

        private static bool TryPreflightPeerInfo(
            byte[] source,
            int offset,
            int length,
            bool receivingOnServer,
            out ProtocolRejection rejection)
        {
            BoundedReader reader = new BoundedReader(source, offset, length);

            if (!reader.TrySkip(sizeof(long)))
            {
                return Malformed(
                    "PeerInfo ended before the peer UID.",
                    out rejection);
            }

            if (!reader.TryReadStringBytes(
                    MaximumStringBytes,
                    out int versionOffset,
                    out int versionLength))
            {
                return Malformed(
                    "PeerInfo contained an invalid game version string.",
                    out rejection);
            }

            string versionString = Encoding.UTF8.GetString(
                source,
                versionOffset,
                versionLength);
            if (GameVersion.TryParseGameVersion(
                    versionString,
                    out GameVersion gameVersion) &&
                ValheimPrivateAccess.UsesNetworkVersion(gameVersion) &&
                !reader.TrySkip(sizeof(uint)))
            {
                return Malformed(
                    "PeerInfo ended before the network version.",
                    out rejection);
            }

            // UnityEngine.Vector3 is serialized as three IEEE-754 single values.
            if (!reader.TrySkip(sizeof(float) * 3))
            {
                return Malformed(
                    "PeerInfo ended before the reference position.",
                    out rejection);
            }

            if (!reader.TrySkipString(MaximumStringBytes))
            {
                return Malformed(
                    "PeerInfo contained an invalid player name.",
                    out rejection);
            }

            if (receivingOnServer)
            {
                if (!reader.TrySkipString(MaximumStringBytes))
                {
                    return Malformed(
                        "PeerInfo contained an invalid password field.",
                        out rejection);
                }

                // SendPeerInfo serializes the Steam session-ticket byte array in
                // this wire position. The server consumes it during Steam admission.
                if (!reader.TrySkipByteArray(MaximumSteamTicketBytes))
                {
                    return Malformed(
                        "PeerInfo contained an invalid Steam session ticket.",
                        out rejection);
                }
            }
            else
            {
                if (!reader.TrySkipString(MaximumStringBytes))
                {
                    return Malformed(
                        "PeerInfo contained an invalid world name.",
                        out rejection);
                }

                if (!reader.TrySkip(sizeof(int)))
                {
                    return Malformed(
                        "PeerInfo ended before the world seed.",
                        out rejection);
                }

                if (!reader.TrySkipString(MaximumStringBytes))
                {
                    return Malformed(
                        "PeerInfo contained an invalid world seed name.",
                        out rejection);
                }

                if (!reader.TrySkip(
                        sizeof(long) +
                        sizeof(int) +
                        sizeof(double)))
                {
                    return Malformed(
                        "PeerInfo ended before the world metadata.",
                        out rejection);
                }
            }

            if (!reader.AtEnd)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.TrailingPacketData,
                    "PeerInfo contained trailing data.");
                return false;
            }

            rejection = null;
            return true;
        }

        private static bool Malformed(
            string safeMessage,
            out ProtocolRejection rejection)
        {
            rejection = new ProtocolRejection(
                ProtocolRejectCode.MalformedPacket,
                safeMessage);
            return false;
        }

        private static int ReadInt32LittleEndian(byte[] source, int offset)
        {
            return
                source[offset] |
                source[offset + 1] << 8 |
                source[offset + 2] << 16 |
                source[offset + 3] << 24;
        }

        /// <summary>
        /// Allocation-free reader for the serialized inner PeerInfo payload.
        /// </summary>
        private struct BoundedReader
        {
            private readonly byte[] source;
            private readonly int end;
            private int position;

            internal BoundedReader(byte[] bytes, int offset, int length)
            {
                source = bytes;
                position = offset;
                end = checked(offset + length);
            }

            internal bool AtEnd
            {
                get { return position == end; }
            }

            internal bool TrySkip(int byteCount)
            {
                if (byteCount < 0 || byteCount > end - position)
                {
                    return false;
                }

                position += byteCount;
                return true;
            }

            internal bool TrySkipString(int maximumBytes)
            {
                return TryReadStringBytes(
                    maximumBytes,
                    out _,
                    out _);
            }

            internal bool TryReadStringBytes(
                int maximumBytes,
                out int valueOffset,
                out int valueLength)
            {
                valueOffset = 0;
                valueLength = 0;

                if (!TryRead7BitEncodedLength(out int byteCount) ||
                    byteCount > maximumBytes ||
                    byteCount > end - position)
                {
                    return false;
                }

                valueOffset = position;
                valueLength = byteCount;
                position += byteCount;
                return true;
            }

            internal bool TrySkipByteArray(int maximumBytes)
            {
                if (end - position < sizeof(int))
                {
                    return false;
                }

                int byteCount = ReadInt32LittleEndian(source, position);
                position += sizeof(int);
                if (byteCount < 0 ||
                    byteCount > maximumBytes ||
                    byteCount > end - position)
                {
                    return false;
                }

                position += byteCount;
                return true;
            }

            private bool TryRead7BitEncodedLength(out int value)
            {
                value = 0;
                uint accumulated = 0;

                for (int byteIndex = 0; byteIndex < 5; ++byteIndex)
                {
                    if (position >= end)
                    {
                        return false;
                    }

                    byte current = source[position++];
                    if (byteIndex == 4 && (current & 0xF0) != 0)
                    {
                        return false;
                    }

                    accumulated |=
                        (uint)(current & 0x7F) <<
                        (byteIndex * 7);
                    if ((current & 0x80) == 0)
                    {
                        if (accumulated > int.MaxValue)
                        {
                            return false;
                        }

                        value = (int)accumulated;
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
