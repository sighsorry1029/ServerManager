#nullable disable

using System;
using System.IO;
using System.Text;

namespace ServerManager
{
    internal sealed class DetectionReport
    {
        internal DetectionReport(
            byte[] sessionId,
            byte[] nonce,
            uint sequence,
            DetectionEvidence evidence,
            string detail,
            uint policyGeneration = 1)
        {
            if (sessionId == null ||
                sessionId.Length != ConnectionProtocolLimits.SessionIdBytes)
            {
                throw new ArgumentException(
                    "Invalid detection session ID.",
                    nameof(sessionId));
            }

            if (nonce == null ||
                nonce.Length != ConnectionProtocolLimits.NonceBytes)
            {
                throw new ArgumentException(
                    "Invalid detection nonce.",
                    nameof(nonce));
            }

            if (sequence == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence));
            }

            if (policyGeneration == 0)
                throw new ArgumentOutOfRangeException(nameof(policyGeneration));

            if (!DetectionEvidenceCatalog.IsDefined(evidence))
            {
                throw new ArgumentOutOfRangeException(nameof(evidence));
            }

            detail ??= string.Empty;
            if (!DetectionReportCodec.IsValidDetail(detail))
            {
                throw new ArgumentException(
                    "Detection detail was not a bounded command token.",
                    nameof(detail));
            }

            if (evidence != DetectionEvidence.CheatCommand &&
                detail.Length != 0)
            {
                throw new ArgumentException(
                    "Only command reports may contain detail.",
                    nameof(detail));
            }

            SessionId = ProtocolByteUtil.Clone(sessionId);
            Nonce = ProtocolByteUtil.Clone(nonce);
            Sequence = sequence;
            Evidence = evidence;
            Detail = detail;
            PolicyGeneration = policyGeneration;
        }

        internal byte[] SessionId { get; }

        internal byte[] Nonce { get; }

        internal uint Sequence { get; }

        internal DetectionEvidence Evidence { get; }

        internal string Detail { get; }

        internal uint PolicyGeneration { get; }
    }

    internal static class DetectionReportCodec
    {
        private const int Magic = 0x54444D53; // "SMDT" as little-endian bytes.
        private const ushort WireVersion = 2;
        internal const int MaximumDetailBytes = 48;
        internal const int FixedBytes =
            sizeof(int) +
            sizeof(ushort) +
            ConnectionProtocolLimits.SessionIdBytes +
            ConnectionProtocolLimits.NonceBytes +
            sizeof(uint) +
            sizeof(uint) +
            sizeof(ushort) +
            sizeof(byte);
        internal const int MaximumPacketBytes =
            FixedBytes + MaximumDetailBytes;

        internal static ZPackage Encode(DetectionReport report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            if (!DetectionEvidenceCatalog.IsClientReportable(report.Evidence))
            {
                throw new ArgumentOutOfRangeException(nameof(report));
            }

            byte[] detail = Encoding.ASCII.GetBytes(report.Detail);
            if (detail.Length > MaximumDetailBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(report));
            }

            using (MemoryStream stream =
                   new MemoryStream(FixedBytes + detail.Length))
            using (BinaryWriter writer =
                   new BinaryWriter(stream, Encoding.ASCII, true))
            {
                writer.Write(Magic);
                writer.Write(WireVersion);
                writer.Write(report.SessionId);
                writer.Write(report.Nonce);
                writer.Write(report.Sequence);
                writer.Write(report.PolicyGeneration);
                writer.Write((ushort)report.Evidence);
                writer.Write((byte)detail.Length);
                writer.Write(detail);
                writer.Flush();
                return new ZPackage(stream.ToArray());
            }
        }

        internal static bool TryDecode(
            ZPackage package,
            out DetectionReport report,
            out ProtocolRejection rejection)
        {
            report = null;
            rejection = null;
            if (package == null)
            {
                rejection = Malformed("The detection report was missing.");
                return false;
            }

            int size;
            byte[] bytes;
            try
            {
                size = package.Size();
                if (size < FixedBytes)
                {
                    rejection = Malformed(
                        "The detection report was truncated.");
                    return false;
                }

                if (size > MaximumPacketBytes)
                {
                    rejection = new ProtocolRejection(
                        ProtocolRejectCode.PayloadTooLarge,
                        "The detection report exceeded its size limit.");
                    return false;
                }

                bytes = package.GetArray();
                if (bytes.Length != size)
                {
                    rejection = Malformed(
                        "The detection report size was inconsistent.");
                    return false;
                }
            }
            catch (Exception exception)
                when (exception is IOException ||
                      exception is ArgumentOutOfRangeException)
            {
                rejection = Malformed(
                    "The detection report could not be inspected.");
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
                            "The detection report magic was invalid.");
                        return false;
                    }

                    if (reader.ReadUInt16() != WireVersion)
                    {
                        rejection = new ProtocolRejection(
                            ProtocolRejectCode.ProtocolVersionMismatch,
                            "The detection protocol version was incompatible.");
                        return false;
                    }

                    byte[] sessionId = ProtocolByteUtil.ReadExact(
                        reader,
                        ConnectionProtocolLimits.SessionIdBytes);
                    byte[] nonce = ProtocolByteUtil.ReadExact(
                        reader,
                        ConnectionProtocolLimits.NonceBytes);
                    uint sequence = reader.ReadUInt32();
                    uint policyGeneration = reader.ReadUInt32();
                    DetectionEvidence evidence =
                        (DetectionEvidence)reader.ReadUInt16();
                    int detailLength = reader.ReadByte();

                    if (sequence == 0 || policyGeneration == 0)
                    {
                        rejection = Malformed(
                            "The detection sequence was invalid.");
                        return false;
                    }

                    if (!DetectionEvidenceCatalog.IsClientReportable(evidence))
                    {
                        rejection = Malformed(
                            "The detection evidence code was invalid.");
                        return false;
                    }

                    if (detailLength > MaximumDetailBytes ||
                        detailLength != stream.Length - stream.Position)
                    {
                        rejection = Malformed(
                            "The detection detail length was invalid.");
                        return false;
                    }

                    byte[] detailBytes = ProtocolByteUtil.ReadExact(
                        reader,
                        detailLength);
                    if (!IsValidDetail(detailBytes) ||
                        (evidence != DetectionEvidence.CheatCommand &&
                         detailBytes.Length != 0))
                    {
                        rejection = Malformed(
                            "The detection detail was invalid.");
                        return false;
                    }

                    string detail = Encoding.ASCII.GetString(detailBytes);
                    report = new DetectionReport(
                        sessionId,
                        nonce,
                        sequence,
                        evidence,
                        detail,
                        policyGeneration);
                    return true;
                }
            }
            catch (Exception exception)
                when (exception is IOException ||
                      exception is EndOfStreamException ||
                      exception is ArgumentException)
            {
                rejection = Malformed(
                    "The detection report was malformed.");
                return false;
            }
        }

        internal static bool IsValidDetail(string detail)
        {
            if (detail == null || detail.Length > MaximumDetailBytes)
            {
                return false;
            }

            for (int index = 0; index < detail.Length; ++index)
            {
                char value = detail[index];
                if (!IsDetailCharacter(value))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidDetail(byte[] detail)
        {
            for (int index = 0; index < detail.Length; ++index)
            {
                if (!IsDetailCharacter((char)detail[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsDetailCharacter(char value)
        {
            return (value >= 'a' && value <= 'z') ||
                   (value >= '0' && value <= '9') ||
                   value == '_' ||
                   value == '-';
        }

        private static ProtocolRejection Malformed(string message)
        {
            return new ProtocolRejection(
                ProtocolRejectCode.MalformedPacket,
                message);
        }
    }
}
