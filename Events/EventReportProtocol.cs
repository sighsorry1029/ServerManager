#nullable disable

using System;
using System.IO;
using System.Text;

namespace ServerManager.Events
{
    internal enum EventClientReportKind : byte
    {
        Shout = 1,
        Death = 2,
        BossKilled = 3,
        Normal = 4,
        Whisper = 5
    }

    internal sealed class EventClientReport
    {
        internal byte[] SessionId { get; set; }
        internal byte[] Nonce { get; set; }
        internal uint Sequence { get; set; }
        internal EventClientReportKind Kind { get; set; }
        internal string Text { get; set; } = string.Empty;
        internal string Cause { get; set; } = string.Empty;
        internal string AttackerName { get; set; } = string.Empty;
        internal string AttackerPrefab { get; set; } = string.Empty;
        internal string HitType { get; set; } = string.Empty;
        internal uint DamageTags { get; set; }
        internal float FinalDamage { get; set; }
        internal bool AttackerIsPlayer { get; set; }
        internal string BossName { get; set; } = string.Empty;
        internal string BossPrefab { get; set; } = string.Empty;
        internal string BossZdoId { get; set; } = string.Empty;
        internal string FinalAttacker { get; set; } = string.Empty;
    }

    internal static class EventClientReportCodec
    {
        private const int Magic = 0x56454D53; // "SMEV"
        private const ushort WireVersion = 2;
        private const int MaximumChatBytes = 2000;
        private const int MaximumNameBytes = 384;
        private const int MaximumPrefabBytes = 512;
        private const int MaximumTokenBytes = 128;
        private const int MaximumZdoIdBytes = 128;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private const int FixedHeaderBytes =
            sizeof(int) + sizeof(ushort) +
            ConnectionProtocolLimits.SessionIdBytes +
            ConnectionProtocolLimits.NonceBytes +
            sizeof(uint) + sizeof(byte) + sizeof(ushort);

        internal const int MaximumPacketBytes = 4096;

        internal static ZPackage Encode(EventClientReport report)
        {
            ValidateHeader(report);
            using (MemoryStream payload = new())
            using (BinaryWriter payloadWriter =
                   new(payload, StrictUtf8, true))
            {
                WritePayload(payloadWriter, report);
                payloadWriter.Flush();
                if (payload.Length > ushort.MaxValue ||
                    FixedHeaderBytes + payload.Length > MaximumPacketBytes)
                {
                    throw new ArgumentOutOfRangeException(nameof(report));
                }

                using (MemoryStream stream = new(
                           FixedHeaderBytes + (int)payload.Length))
                using (BinaryWriter writer =
                       new(stream, StrictUtf8, true))
                {
                    writer.Write(Magic);
                    writer.Write(WireVersion);
                    writer.Write(report.SessionId);
                    writer.Write(report.Nonce);
                    writer.Write(report.Sequence);
                    writer.Write((byte)report.Kind);
                    writer.Write((ushort)payload.Length);
                    writer.Write(payload.GetBuffer(), 0, (int)payload.Length);
                    writer.Flush();
                    return new ZPackage(stream.ToArray());
                }
            }
        }

        internal static bool TryDecode(
            ZPackage package,
            out EventClientReport report,
            out ProtocolRejection rejection)
        {
            report = null;
            rejection = null;
            byte[] bytes;
            try
            {
                if (package == null ||
                    package.Size() < FixedHeaderBytes ||
                    package.Size() > MaximumPacketBytes)
                {
                    rejection = Malformed(
                        "The event report size was invalid.");
                    return false;
                }

                bytes = package.GetArray();
                if (bytes.Length != package.Size())
                {
                    rejection = Malformed(
                        "The event report size was inconsistent.");
                    return false;
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is ArgumentOutOfRangeException)
            {
                rejection = Malformed(
                    "The event report could not be inspected.");
                return false;
            }

            try
            {
                using (MemoryStream stream = new(bytes, false))
                using (BinaryReader reader =
                       new(stream, StrictUtf8, true))
                {
                    if (reader.ReadInt32() != Magic)
                    {
                        rejection = Malformed(
                            "The event report magic was invalid.");
                        return false;
                    }

                    if (reader.ReadUInt16() != WireVersion)
                    {
                        rejection = new ProtocolRejection(
                            ProtocolRejectCode.ProtocolVersionMismatch,
                            "The event report version was incompatible.");
                        return false;
                    }

                    EventClientReport decoded = new()
                    {
                        SessionId = ProtocolByteUtil.ReadExact(
                            reader,
                            ConnectionProtocolLimits.SessionIdBytes),
                        Nonce = ProtocolByteUtil.ReadExact(
                            reader,
                            ConnectionProtocolLimits.NonceBytes),
                        Sequence = reader.ReadUInt32(),
                        Kind = (EventClientReportKind)reader.ReadByte()
                    };
                    int payloadLength = reader.ReadUInt16();
                    if (decoded.Sequence == 0 ||
                        !IsDefined(decoded.Kind) ||
                        payloadLength != stream.Length - stream.Position)
                    {
                        rejection = Malformed(
                            "The event report header was invalid.");
                        return false;
                    }

                    long payloadEnd = stream.Position + payloadLength;
                    ReadPayload(reader, decoded);
                    if (stream.Position != payloadEnd)
                    {
                        rejection = Malformed(
                            "The event report contained trailing data.");
                        return false;
                    }

                    ValidateHeader(decoded);
                    report = decoded;
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is InvalidDataException ||
                exception is EndOfStreamException ||
                exception is ArgumentException ||
                exception is DecoderFallbackException)
            {
                rejection = Malformed(
                    "The event report payload was malformed.");
                return false;
            }
        }

        private static void WritePayload(
            BinaryWriter writer,
            EventClientReport report)
        {
            switch (report.Kind)
            {
                case EventClientReportKind.Shout:
                case EventClientReportKind.Normal:
                case EventClientReportKind.Whisper:
                    WriteString(
                        writer,
                        report.Text,
                        MaximumChatBytes,
                        allowWhitespace: true);
                    break;

                case EventClientReportKind.Death:
                    WriteString(writer, report.Cause, MaximumTokenBytes, false);
                    WriteString(
                        writer,
                        report.AttackerName,
                        MaximumNameBytes,
                        true);
                    WriteString(
                        writer,
                        report.AttackerPrefab,
                        MaximumPrefabBytes,
                        false);
                    WriteString(
                        writer,
                        report.HitType,
                        MaximumTokenBytes,
                        false);
                    writer.Write(report.DamageTags);
                    writer.Write(report.FinalDamage);
                    writer.Write(report.AttackerIsPlayer);
                    break;

                case EventClientReportKind.BossKilled:
                    WriteString(
                        writer,
                        report.BossName,
                        MaximumNameBytes,
                        true);
                    WriteString(
                        writer,
                        report.BossPrefab,
                        MaximumPrefabBytes,
                        false);
                    WriteString(
                        writer,
                        report.BossZdoId,
                        MaximumZdoIdBytes,
                        false);
                    WriteString(
                        writer,
                        report.FinalAttacker,
                        MaximumNameBytes,
                        true);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(report));
            }
        }

        private static void ReadPayload(
            BinaryReader reader,
            EventClientReport report)
        {
            switch (report.Kind)
            {
                case EventClientReportKind.Shout:
                case EventClientReportKind.Normal:
                case EventClientReportKind.Whisper:
                    report.Text = ReadString(
                        reader,
                        MaximumChatBytes,
                        allowWhitespace: true);
                    break;

                case EventClientReportKind.Death:
                    report.Cause = ReadString(
                        reader,
                        MaximumTokenBytes,
                        allowWhitespace: false);
                    report.AttackerName = ReadString(
                        reader,
                        MaximumNameBytes,
                        allowWhitespace: true);
                    report.AttackerPrefab = ReadString(
                        reader,
                        MaximumPrefabBytes,
                        allowWhitespace: false);
                    report.HitType = ReadString(
                        reader,
                        MaximumTokenBytes,
                        allowWhitespace: false);
                    report.DamageTags = reader.ReadUInt32();
                    report.FinalDamage = reader.ReadSingle();
                    report.AttackerIsPlayer = reader.ReadBoolean();
                    if (float.IsNaN(report.FinalDamage) ||
                        float.IsInfinity(report.FinalDamage) ||
                        report.FinalDamage < 0f ||
                        report.DamageTags >> 11 != 0)
                    {
                        throw new InvalidDataException();
                    }

                    break;

                case EventClientReportKind.BossKilled:
                    report.BossName = ReadString(
                        reader,
                        MaximumNameBytes,
                        allowWhitespace: true);
                    report.BossPrefab = ReadString(
                        reader,
                        MaximumPrefabBytes,
                        allowWhitespace: false);
                    report.BossZdoId = ReadString(
                        reader,
                        MaximumZdoIdBytes,
                        allowWhitespace: false);
                    report.FinalAttacker = ReadString(
                        reader,
                        MaximumNameBytes,
                        allowWhitespace: true);
                    break;

                default:
                    throw new InvalidDataException();
            }
        }

        private static void ValidateHeader(EventClientReport report)
        {
            if (report == null ||
                report.SessionId == null ||
                report.SessionId.Length !=
                    ConnectionProtocolLimits.SessionIdBytes ||
                report.Nonce == null ||
                report.Nonce.Length != ConnectionProtocolLimits.NonceBytes ||
                report.Sequence == 0 ||
                !IsDefined(report.Kind))
            {
                throw new ArgumentException("Invalid event report header.");
            }
        }

        private static bool IsDefined(EventClientReportKind kind)
        {
            return kind == EventClientReportKind.Shout ||
                   kind == EventClientReportKind.Death ||
                   kind == EventClientReportKind.BossKilled ||
                   kind == EventClientReportKind.Normal ||
                   kind == EventClientReportKind.Whisper;
        }

        private static void WriteString(
            BinaryWriter writer,
            string value,
            int maximumBytes,
            bool allowWhitespace)
        {
            value ??= string.Empty;
            byte[] bytes = StrictUtf8.GetBytes(value);
            ValidateString(value, bytes, maximumBytes, allowWhitespace);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(
            BinaryReader reader,
            int maximumBytes,
            bool allowWhitespace)
        {
            int length = reader.ReadUInt16();
            if (length > maximumBytes)
            {
                throw new InvalidDataException();
            }

            byte[] bytes = ProtocolByteUtil.ReadExact(reader, length);
            string value = StrictUtf8.GetString(bytes);
            ValidateString(value, bytes, maximumBytes, allowWhitespace);
            return value;
        }

        private static void ValidateString(
            string value,
            byte[] bytes,
            int maximumBytes,
            bool allowWhitespace)
        {
            if (bytes.Length > maximumBytes ||
                (value.Length != 0 && string.IsNullOrWhiteSpace(value)))
            {
                throw new InvalidDataException();
            }

            for (int index = 0; index < value.Length; ++index)
            {
                char character = value[index];
                if (char.IsControl(character))
                {
                    throw new InvalidDataException();
                }
            }
        }

        private static ProtocolRejection Malformed(string message)
        {
            return new ProtocolRejection(
                ProtocolRejectCode.MalformedPacket,
                message);
        }
    }

    internal sealed class EventDisplayPacket
    {
        internal byte[] SessionId { get; set; }
        internal byte[] Nonce { get; set; }
        internal uint Sequence { get; set; }
        internal string Kind { get; set; }
        internal string Message { get; set; }
        internal string MessageKey { get; set; } = string.Empty;
        internal string[] Arguments { get; set; } = Array.Empty<string>();
        internal byte LabelMask { get; set; }
    }

    internal static class EventDisplayCodec
    {
        private const int Magic = 0x44454D53; // "SMED"
        private const ushort WireVersion = 2;
        private const int MaximumKindBytes = 64;
        private const int MaximumMessageBytes = 2000;
        private const int MaximumKeyBytes = 96;
        private const int MaximumArgumentBytes = 320;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        internal const int MaximumPacketBytes = 2304;

        internal static ZPackage Encode(EventDisplayPacket packet)
        {
            if (packet == null || packet.Sequence == 0 ||
                packet.SessionId?.Length !=
                    ConnectionProtocolLimits.SessionIdBytes ||
                packet.Nonce?.Length != ConnectionProtocolLimits.NonceBytes)
            {
                throw new ArgumentException("Invalid event display packet.");
            }

            byte[] kind = StrictUtf8.GetBytes(packet.Kind ?? string.Empty);
            byte[] message = StrictUtf8.GetBytes(packet.Message ?? string.Empty);
            byte[] key = StrictUtf8.GetBytes(packet.MessageKey ?? string.Empty);
            if (kind.Length == 0 || kind.Length > MaximumKindBytes ||
                message.Length > MaximumMessageBytes || key.Length > MaximumKeyBytes ||
                !IsValidPayload(packet))
            {
                throw new ArgumentOutOfRangeException(nameof(packet));
            }
            byte[][] arguments = new byte[packet.Arguments.Length][];
            for (int index = 0; index < arguments.Length; ++index)
            {
                arguments[index] = StrictUtf8.GetBytes(packet.Arguments[index]);
                if (arguments[index].Length > MaximumArgumentBytes)
                    throw new ArgumentOutOfRangeException(nameof(packet));
            }

            using (MemoryStream stream = new())
            using (BinaryWriter writer = new(stream, StrictUtf8, true))
            {
                writer.Write(Magic);
                writer.Write(WireVersion);
                writer.Write(packet.SessionId);
                writer.Write(packet.Nonce);
                writer.Write(packet.Sequence);
                writer.Write((byte)kind.Length);
                writer.Write((ushort)message.Length);
                writer.Write((byte)key.Length);
                writer.Write((byte)arguments.Length);
                writer.Write(packet.LabelMask);
                writer.Write(kind);
                writer.Write(message);
                writer.Write(key);
                foreach (byte[] argument in arguments)
                {
                    writer.Write((ushort)argument.Length);
                    writer.Write(argument);
                }
                writer.Flush();
                if (stream.Length > MaximumPacketBytes)
                {
                    throw new ArgumentOutOfRangeException(nameof(packet));
                }

                return new ZPackage(stream.ToArray());
            }
        }

        internal static bool TryDecode(
            ZPackage package,
            out EventDisplayPacket packet)
        {
            packet = null;
            try
            {
                if (package == null || package.Size() > MaximumPacketBytes)
                {
                    return false;
                }

                byte[] bytes = package.GetArray();
                using (MemoryStream stream = new(bytes, false))
                using (BinaryReader reader =
                       new(stream, StrictUtf8, true))
                {
                    if (reader.ReadInt32() != Magic ||
                        reader.ReadUInt16() != WireVersion)
                    {
                        return false;
                    }

                    byte[] sessionId = ProtocolByteUtil.ReadExact(
                        reader,
                        ConnectionProtocolLimits.SessionIdBytes);
                    byte[] nonce = ProtocolByteUtil.ReadExact(
                        reader,
                        ConnectionProtocolLimits.NonceBytes);
                    uint sequence = reader.ReadUInt32();
                    int kindLength = reader.ReadByte();
                    int messageLength = reader.ReadUInt16();
                    int keyLength = reader.ReadByte();
                    int argumentCount = reader.ReadByte();
                    byte labelMask = reader.ReadByte();
                    if (sequence == 0 || kindLength == 0 ||
                        kindLength > MaximumKindBytes ||
                        messageLength > MaximumMessageBytes ||
                        keyLength > MaximumKeyBytes || argumentCount > 2 ||
                        kindLength + messageLength + keyLength >
                            stream.Length - stream.Position)
                    {
                        return false;
                    }

                    string kind = StrictUtf8.GetString(
                        ProtocolByteUtil.ReadExact(reader, kindLength));
                    string message = StrictUtf8.GetString(
                        ProtocolByteUtil.ReadExact(reader, messageLength));
                    string key = StrictUtf8.GetString(
                        ProtocolByteUtil.ReadExact(reader, keyLength));
                    string[] arguments = new string[argumentCount];
                    for (int index = 0; index < argumentCount; ++index)
                    {
                        int argumentLength = reader.ReadUInt16();
                        if (argumentLength > MaximumArgumentBytes || argumentLength > stream.Length - stream.Position)
                            return false;
                        arguments[index] = StrictUtf8.GetString(ProtocolByteUtil.ReadExact(reader, argumentLength));
                    }
                    if (stream.Position != stream.Length) return false;

                    EventDisplayPacket candidate = new EventDisplayPacket
                    {
                        SessionId = sessionId,
                        Nonce = nonce,
                        Sequence = sequence,
                        Kind = kind,
                        Message = message,
                        MessageKey = key,
                        Arguments = arguments,
                        LabelMask = labelMask
                    };
                    if (!IsValidPayload(candidate)) return false;
                    packet = candidate;
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is InvalidDataException ||
                exception is EndOfStreamException ||
                exception is ArgumentException ||
                exception is DecoderFallbackException)
            {
                return false;
            }
        }

        private static bool IsValidPayload(EventDisplayPacket packet)
        {
            if (packet.Arguments == null || packet.Arguments.Length > 2 ||
                ContainsControl(packet.Kind ?? string.Empty, allowLineBreaks: false)) return false;
            if (packet.Kind == "server.announcement")
                return !string.IsNullOrWhiteSpace(packet.Message) &&
                       string.IsNullOrEmpty(packet.MessageKey) && packet.Arguments.Length == 0 && packet.LabelMask == 0 &&
                       !ContainsControl(packet.Message, allowLineBreaks: true);
            if (!string.IsNullOrEmpty(packet.Message) ||
                !EventMessageText.IsValidMessage(packet.Kind, packet.MessageKey, packet.Arguments.Length) ||
                !EventMessageText.IsValidLabels(packet.Arguments, packet.LabelMask)) return false;
            foreach (string argument in packet.Arguments)
                if (argument == null || ContainsControl(argument, allowLineBreaks: false)) return false;
            return true;
        }

        private static bool ContainsControl(
            string value,
            bool allowLineBreaks)
        {
            for (int index = 0; index < value.Length; ++index)
            {
                char character = value[index];
                if (char.IsControl(character) &&
                    !(allowLineBreaks &&
                      (character == '\r' || character == '\n')))
                {
                    return true;
                }
            }

            return false;
        }

    }
}
