#nullable disable

using System;
using System.IO;
using System.Text;

namespace ServerManager
{
    internal sealed class AdminCommandEntitlement
    {
        internal AdminCommandEntitlement(
            byte[] sessionId,
            byte[] nonce,
            uint sequence,
            bool granted,
            int validForMilliseconds)
        {
            if (sessionId == null ||
                sessionId.Length != ConnectionProtocolLimits.SessionIdBytes)
            {
                throw new ArgumentException(
                    "Invalid admin entitlement session ID.",
                    nameof(sessionId));
            }

            if (nonce == null ||
                nonce.Length != ConnectionProtocolLimits.NonceBytes)
            {
                throw new ArgumentException(
                    "Invalid admin entitlement nonce.",
                    nameof(nonce));
            }

            if (sequence == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence));
            }

            if (granted)
            {
                if (validForMilliseconds <
                    AdminCommandEntitlementCodec.MinimumGrantMilliseconds ||
                    validForMilliseconds >
                    AdminCommandEntitlementCodec.MaximumGrantMilliseconds)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(validForMilliseconds));
                }
            }
            else if (validForMilliseconds != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(validForMilliseconds));
            }

            SessionId = ProtocolByteUtil.Clone(sessionId);
            Nonce = ProtocolByteUtil.Clone(nonce);
            Sequence = sequence;
            Granted = granted;
            ValidForMilliseconds = validForMilliseconds;
        }

        internal byte[] SessionId { get; }

        internal byte[] Nonce { get; }

        internal uint Sequence { get; }

        internal bool Granted { get; }

        internal int ValidForMilliseconds { get; }
    }

    internal static class AdminCommandEntitlementCodec
    {
        private const int Magic = 0x45414D53; // "SMAE" as little-endian bytes.
        private const ushort WireVersion = 1;
        internal const int MinimumGrantMilliseconds = 1000;
        internal const int MaximumGrantMilliseconds = 10000;
        internal const int FixedPacketBytes =
            sizeof(int) +
            sizeof(ushort) +
            ConnectionProtocolLimits.SessionIdBytes +
            ConnectionProtocolLimits.NonceBytes +
            sizeof(uint) +
            sizeof(byte) +
            sizeof(ushort);
        internal const int MaximumPacketBytes = FixedPacketBytes;

        internal static ZPackage Encode(AdminCommandEntitlement entitlement)
        {
            if (entitlement == null)
            {
                throw new ArgumentNullException(nameof(entitlement));
            }

            using (MemoryStream stream =
                   new MemoryStream(FixedPacketBytes))
            using (BinaryWriter writer =
                   new BinaryWriter(stream, Encoding.ASCII, true))
            {
                writer.Write(Magic);
                writer.Write(WireVersion);
                writer.Write(entitlement.SessionId);
                writer.Write(entitlement.Nonce);
                writer.Write(entitlement.Sequence);
                writer.Write((byte)(entitlement.Granted ? 1 : 0));
                writer.Write((ushort)entitlement.ValidForMilliseconds);
                writer.Flush();
                return new ZPackage(stream.ToArray());
            }
        }

        internal static bool TryDecode(
            ZPackage package,
            out AdminCommandEntitlement entitlement,
            out ProtocolRejection rejection)
        {
            entitlement = null;
            rejection = null;
            if (package == null)
            {
                rejection = Malformed(
                    "The admin command entitlement was missing.");
                return false;
            }

            byte[] bytes;
            try
            {
                int size = package.Size();
                if (size != FixedPacketBytes)
                {
                    rejection = Malformed(
                        "The admin command entitlement size was invalid.");
                    return false;
                }

                bytes = package.GetArray();
                if (bytes.Length != size)
                {
                    rejection = Malformed(
                        "The admin command entitlement size was inconsistent.");
                    return false;
                }
            }
            catch (Exception exception)
                when (exception is IOException ||
                      exception is ArgumentOutOfRangeException)
            {
                rejection = Malformed(
                    "The admin command entitlement could not be inspected.");
                return false;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(bytes, false))
                using (BinaryReader reader =
                       new BinaryReader(stream, Encoding.ASCII, true))
                {
                    if (reader.ReadInt32() != Magic)
                    {
                        rejection = Malformed(
                            "The admin command entitlement magic was invalid.");
                        return false;
                    }

                    if (reader.ReadUInt16() != WireVersion)
                    {
                        rejection = new ProtocolRejection(
                            ProtocolRejectCode.ProtocolVersionMismatch,
                            "The admin entitlement protocol version was incompatible.");
                        return false;
                    }

                    byte[] sessionId = ProtocolByteUtil.ReadExact(
                        reader,
                        ConnectionProtocolLimits.SessionIdBytes);
                    byte[] nonce = ProtocolByteUtil.ReadExact(
                        reader,
                        ConnectionProtocolLimits.NonceBytes);
                    uint sequence = reader.ReadUInt32();
                    byte grantedValue = reader.ReadByte();
                    int validForMilliseconds = reader.ReadUInt16();
                    if (sequence == 0 || grantedValue > 1)
                    {
                        rejection = Malformed(
                            "The admin command entitlement fields were invalid.");
                        return false;
                    }

                    bool granted = grantedValue == 1;
                    if (granted)
                    {
                        if (validForMilliseconds <
                            MinimumGrantMilliseconds ||
                            validForMilliseconds >
                            MaximumGrantMilliseconds)
                        {
                            rejection = Malformed(
                                "The admin command grant lifetime was invalid.");
                            return false;
                        }
                    }
                    else if (validForMilliseconds != 0)
                    {
                        rejection = Malformed(
                            "The admin command revocation lifetime was invalid.");
                        return false;
                    }

                    entitlement = new AdminCommandEntitlement(
                        sessionId,
                        nonce,
                        sequence,
                        granted,
                        validForMilliseconds);
                    return true;
                }
            }
            catch (Exception exception)
                when (exception is IOException ||
                      exception is EndOfStreamException ||
                      exception is ArgumentException)
            {
                rejection = Malformed(
                    "The admin command entitlement was malformed.");
                return false;
            }
        }

        private static ProtocolRejection Malformed(string message)
        {
            return new ProtocolRejection(
                ProtocolRejectCode.MalformedPacket,
                message);
        }
    }
}
