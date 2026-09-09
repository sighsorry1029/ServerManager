using System;
using System.IO;
using System.Text;

namespace ServerManager.Commands;

internal enum AdminPacketKind : byte
{
    Request = 1, Result = 2, PlayerAction = 3, PlayerResult = 4, Shout = 6
}

// This is an application message on the existing authenticated peer connection,
// not a new listener or an arbitrary console/RPC invocation protocol.
internal sealed class AdminPacket
{
    internal AdminPacketKind Kind;
    internal byte[] SessionId = Array.Empty<byte>();
    internal byte[] Nonce = Array.Empty<byte>();
    internal uint Sequence;
    internal Guid RequestId;
    internal long Revision;
    internal string[] Arguments = Array.Empty<string>();
}

internal static class AdminCommandCodec
{
    internal const int MaximumPacketBytes = 16384;
    private const int Magic = 0x43414D53;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static ZPackage Encode(AdminPacket value)
    {
        Validate(value);
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Utf8, true);
        writer.Write(Magic);
        writer.Write((byte)1);
        writer.Write((byte)value.Kind);
        writer.Write(value.SessionId);
        writer.Write(value.Nonce);
        writer.Write(value.Sequence);
        writer.Write(value.RequestId.ToByteArray());
        writer.Write(value.Revision);
        writer.Write((byte)value.Arguments.Length);
        foreach (string argument in value.Arguments)
        {
            byte[] bytes = Utf8.GetBytes(argument);
            if (bytes.Length > 8192) throw new ArgumentException("Admin argument is too large.");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }
        writer.Flush();
        if (stream.Length > MaximumPacketBytes) throw new ArgumentException("Admin packet is too large.");
        return new ZPackage(stream.ToArray());
    }

    internal static bool TryDecode(ZPackage package, out AdminPacket value)
    {
        value = null!;
        try
        {
            if (package == null || package.Size() < 64 || package.Size() > MaximumPacketBytes) return false;
            using MemoryStream stream = new(package.GetArray(), false);
            using BinaryReader reader = new(stream, Utf8, true);
            if (reader.ReadInt32() != Magic || reader.ReadByte() != 1) return false;
            AdminPacket parsed = new()
            {
                Kind = (AdminPacketKind)reader.ReadByte(),
                SessionId = ReadExact(reader, ConnectionProtocolLimits.SessionIdBytes),
                Nonce = ReadExact(reader, ConnectionProtocolLimits.NonceBytes),
                Sequence = reader.ReadUInt32(),
                RequestId = new Guid(ReadExact(reader, 16)),
                Revision = reader.ReadInt64()
            };
            int count = reader.ReadByte();
            if (count > 16) return false;
            parsed.Arguments = new string[count];
            for (int index = 0; index < count; ++index)
            {
                int length = reader.ReadUInt16();
                if (length > 8192 || length > stream.Length - stream.Position) return false;
                parsed.Arguments[index] = Utf8.GetString(ReadExact(reader, length));
            }
            if (stream.Position != stream.Length) return false;
            Validate(parsed);
            value = parsed;
            return true;
        }
        catch (Exception error) when (error is IOException || error is ArgumentException || error is OverflowException)
        {
            return false;
        }
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return bytes;
    }

    private static void Validate(AdminPacket value)
    {
        if (value == null || value.Kind < AdminPacketKind.Request || value.Kind > AdminPacketKind.Shout ||
            value.SessionId.Length != ConnectionProtocolLimits.SessionIdBytes ||
            value.Nonce.Length != ConnectionProtocolLimits.NonceBytes || value.Sequence == 0 ||
            value.Sequence == uint.MaxValue || value.RequestId == Guid.Empty || value.Revision < 0 ||
            value.Arguments == null || value.Arguments.Length > 16)
            throw new ArgumentException("Invalid admin packet header.");
        foreach (string argument in value.Arguments)
            if (argument == null || argument.Length > 4096 || argument.IndexOf('\0') >= 0)
                throw new ArgumentException("Invalid admin packet argument.");
        bool valid = value.Kind switch
        {
            AdminPacketKind.Request => value.Arguments.Length == 1 && value.Arguments[0].Length <= 2048,
            AdminPacketKind.Result or AdminPacketKind.PlayerResult =>
                value.Arguments.Length == (value.Kind == AdminPacketKind.Result ? 4 : 3) &&
                (value.Arguments[0] == "1" || value.Arguments[0] == "0") && value.Arguments[1].Length <= 64 &&
                value.Arguments[2].Length <= 1800 &&
                (value.Kind != AdminPacketKind.Result || value.Arguments[3].Length <= 128),
            AdminPacketKind.Shout => value.Arguments.Length == 2 && value.Arguments[0].Length <= 100 &&
                value.Arguments[1].Length <= 500,
            AdminPacketKind.PlayerAction => value.Arguments.Length >= 2 && value.Arguments.Length <= 6,
            _ => false
        };
        if (!valid) throw new ArgumentException("Invalid admin packet body.");
    }
}
