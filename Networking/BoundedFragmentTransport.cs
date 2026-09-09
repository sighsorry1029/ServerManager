#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace ServerManager
{
    public enum FragmentContentEncoding : byte
    {
        Raw = 0,
        GZip = 1
    }

    public enum FragmentAcceptStatus
    {
        InProgress,
        Completed,
        Rejected
    }

    public sealed class FragmentTransportLimits
    {
        public FragmentTransportLimits(
            int maxFragmentDataBytes = ConnectionProtocolLimits.AbsoluteMaxFragmentDataBytes,
            int maxFragmentCount = 64,
            int maxEncodedMessageBytes = 8 * 1024 * 1024,
            int maxDecodedMessageBytes = 16 * 1024 * 1024,
            int maxConcurrentAssemblies = 32,
            int maxConcurrentAssembliesPerPeer = 1,
            int maxReservedBytes = 32 * 1024 * 1024,
            TimeSpan? assemblyTimeout = null)
        {
            MaxFragmentDataBytes = maxFragmentDataBytes;
            MaxFragmentCount = maxFragmentCount;
            MaxEncodedMessageBytes = maxEncodedMessageBytes;
            MaxDecodedMessageBytes = maxDecodedMessageBytes;
            MaxConcurrentAssemblies = maxConcurrentAssemblies;
            MaxConcurrentAssembliesPerPeer = maxConcurrentAssembliesPerPeer;
            MaxReservedBytes = maxReservedBytes;
            AssemblyTimeout = assemblyTimeout ?? TimeSpan.FromSeconds(25);
            Validate();
        }

        public int MaxFragmentDataBytes { get; private set; }

        public int MaxFragmentCount { get; private set; }

        public int MaxEncodedMessageBytes { get; private set; }

        public int MaxDecodedMessageBytes { get; private set; }

        public int MaxConcurrentAssemblies { get; private set; }

        public int MaxConcurrentAssembliesPerPeer { get; private set; }

        /// <summary>
        /// Sum of declared encoded message sizes reserved by in-flight assemblies.
        /// </summary>
        public int MaxReservedBytes { get; private set; }

        public TimeSpan AssemblyTimeout { get; private set; }

        private void Validate()
        {
            if (MaxFragmentDataBytes <= 0 ||
                MaxFragmentDataBytes > ConnectionProtocolLimits.AbsoluteMaxFragmentDataBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxFragmentDataBytes));
            }

            if (MaxFragmentCount <= 0 || MaxFragmentCount > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxFragmentCount));
            }

            if (MaxEncodedMessageBytes <= 0 || MaxEncodedMessageBytes > 64 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxEncodedMessageBytes));
            }

            if (MaxDecodedMessageBytes <= 0 || MaxDecodedMessageBytes > 64 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxDecodedMessageBytes));
            }

            if (MaxConcurrentAssemblies <= 0 || MaxConcurrentAssemblies > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxConcurrentAssemblies));
            }

            if (MaxConcurrentAssembliesPerPeer <= 0 ||
                MaxConcurrentAssembliesPerPeer > MaxConcurrentAssemblies)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxConcurrentAssembliesPerPeer));
            }

            if (MaxReservedBytes < MaxEncodedMessageBytes ||
                MaxReservedBytes > 128 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxReservedBytes));
            }

            if (AssemblyTimeout <= TimeSpan.Zero || AssemblyTimeout > TimeSpan.FromMinutes(2))
            {
                throw new ArgumentOutOfRangeException(nameof(AssemblyTimeout));
            }
        }
    }

    public sealed class CharacterTransferPlan
    {
        internal CharacterTransferPlan(
            byte[] messageId,
            FragmentContentEncoding encoding,
            int encodedLength,
            int decodedLength,
            IList<ZPackage> packets)
        {
            MessageId = ProtocolByteUtil.Clone(messageId);
            Encoding = encoding;
            EncodedLength = encodedLength;
            DecodedLength = decodedLength;
            Packets = new ReadOnlyCollection<ZPackage>(packets);
        }

        public byte[] MessageId { get; private set; }

        public FragmentContentEncoding Encoding { get; private set; }

        public int EncodedLength { get; private set; }

        public int DecodedLength { get; private set; }

        public ReadOnlyCollection<ZPackage> Packets { get; private set; }
    }

    public sealed class FragmentAcceptResult
    {
        private FragmentAcceptResult(
            FragmentAcceptStatus status,
            byte[] messageId,
            byte[] payload,
            ProtocolRejection rejection)
        {
            Status = status;
            MessageId = ProtocolByteUtil.Clone(messageId);
            Payload = ProtocolByteUtil.Clone(payload);
            Rejection = rejection;
        }

        public FragmentAcceptStatus Status { get; private set; }

        public byte[] MessageId { get; private set; }

        /// <summary>
        /// Populated only for Completed. This is the bounded, decompressed character payload.
        /// </summary>
        public byte[] Payload { get; private set; }

        public ProtocolRejection Rejection { get; private set; }

        public static FragmentAcceptResult InProgress(byte[] messageId)
        {
            return new FragmentAcceptResult(
                FragmentAcceptStatus.InProgress,
                messageId,
                ProtocolByteUtil.Empty,
                null);
        }

        public static FragmentAcceptResult Complete(byte[] messageId, byte[] payload)
        {
            return new FragmentAcceptResult(
                FragmentAcceptStatus.Completed,
                messageId,
                payload,
                null);
        }

        public static FragmentAcceptResult Reject(
            ProtocolRejection rejection,
            byte[] messageId = null)
        {
            return new FragmentAcceptResult(
                FragmentAcceptStatus.Rejected,
                messageId,
                ProtocolByteUtil.Empty,
                rejection);
        }
    }

    /// <summary>
    /// Bounded diagnostic information for an assembly removed after its deadline.
    /// The payload and digest are deliberately not retained.
    /// </summary>
    public sealed class ExpiredFragmentAssembly
    {
        internal ExpiredFragmentAssembly(ZRpc peerRpc, byte[] messageId)
        {
            PeerRpc = peerRpc;
            MessageIdDiagnostic =
                messageId != null &&
                messageId.Length == ConnectionProtocolLimits.MessageIdBytes
                    ? Convert.ToBase64String(messageId)
                    : "invalid";
        }

        public ZRpc PeerRpc { get; private set; }

        /// <summary>
        /// Fixed-size Base64 transfer identifier suitable for bounded logging.
        /// </summary>
        public string MessageIdDiagnostic { get; private set; }
    }

    public static class BoundedFragmentCodec
    {
        private const byte FrameVersion = 1;
        public const int FrameHeaderBytes =
            sizeof(byte) + sizeof(byte) +
            ConnectionProtocolLimits.MessageIdBytes +
            sizeof(int) * 5 +
            ConnectionProtocolLimits.DigestBytes;

        public static CharacterTransferPlan CreateCharacterTransfer(
            byte[] sessionId,
            byte[] nonce,
            byte[] decodedPayload,
            bool preferCompression,
            ConnectionProtocolLimits protocolLimits,
            FragmentTransportLimits fragmentLimits)
        {
            if (decodedPayload == null)
            {
                throw new ArgumentNullException(nameof(decodedPayload));
            }

            if (protocolLimits == null)
            {
                throw new ArgumentNullException(nameof(protocolLimits));
            }

            if (fragmentLimits == null)
            {
                throw new ArgumentNullException(nameof(fragmentLimits));
            }

            if (decodedPayload.Length > fragmentLimits.MaxDecodedMessageBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(decodedPayload),
                    "Decoded character payload exceeds the configured limit.");
            }

            int largestPacket =
                ProtocolPacketCodec.FixedHeaderBytes +
                FrameHeaderBytes +
                fragmentLimits.MaxFragmentDataBytes;
            if (largestPacket > protocolLimits.MaxPacketBytes)
            {
                throw new InvalidOperationException(
                    "The protocol packet limit is too small for the configured fragment size.");
            }

            byte[] encodedPayload = decodedPayload;
            FragmentContentEncoding encoding = FragmentContentEncoding.Raw;
            if (preferCompression && decodedPayload.Length > 0)
            {
                byte[] compressed = Compress(decodedPayload);
                if (compressed.Length < decodedPayload.Length)
                {
                    encodedPayload = compressed;
                    encoding = FragmentContentEncoding.GZip;
                }
            }

            if (encodedPayload.Length > fragmentLimits.MaxEncodedMessageBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(decodedPayload),
                    "Encoded character payload exceeds the configured limit.");
            }

            int fragmentCount = GetExpectedFragmentCount(
                encodedPayload.Length,
                fragmentLimits.MaxFragmentDataBytes);
            if (fragmentCount > fragmentLimits.MaxFragmentCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(decodedPayload),
                    "Character payload requires too many fragments.");
            }

            byte[] messageId = ProtocolByteUtil.RandomBytes(
                ConnectionProtocolLimits.MessageIdBytes);
            byte[] digest = ComputeSha256(encodedPayload);
            List<ZPackage> packets = new List<ZPackage>(fragmentCount);

            for (int index = 0; index < fragmentCount; ++index)
            {
                int offset = checked(index * fragmentLimits.MaxFragmentDataBytes);
                int fragmentLength = Math.Min(
                    fragmentLimits.MaxFragmentDataBytes,
                    encodedPayload.Length - offset);
                if (encodedPayload.Length == 0)
                {
                    fragmentLength = 0;
                }

                byte[] frame = EncodeFrame(
                    encoding,
                    messageId,
                    index,
                    fragmentCount,
                    encodedPayload.Length,
                    decodedPayload.Length,
                    digest,
                    encodedPayload,
                    offset,
                    fragmentLength);

                ProtocolPacket packet = new ProtocolPacket(
                    ProtocolPacketKind.CharacterFragment,
                    ProtocolSequence.CharacterData,
                    sessionId,
                    nonce,
                    frame);
                packets.Add(ProtocolPacketCodec.Encode(packet, protocolLimits));
            }

            return new CharacterTransferPlan(
                messageId,
                encoding,
                encodedPayload.Length,
                decodedPayload.Length,
                packets);
        }

        internal static bool TryDecodeFrame(
            byte[] payload,
            FragmentTransportLimits limits,
            out FragmentFrame frame,
            out ProtocolRejection rejection)
        {
            frame = null;
            rejection = null;
            if (payload == null || payload.Length < FrameHeaderBytes)
            {
                rejection = FragmentError("The character fragment was truncated.");
                return false;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(payload, false))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    if (reader.ReadByte() != FrameVersion)
                    {
                        rejection = FragmentError("The character fragment version was invalid.");
                        return false;
                    }

                    byte rawEncoding = reader.ReadByte();
                    if (!Enum.IsDefined(typeof(FragmentContentEncoding), rawEncoding))
                    {
                        rejection = FragmentError("The character fragment encoding was invalid.");
                        return false;
                    }

                    byte[] messageId = ProtocolByteUtil.ReadExact(
                        reader,
                        ConnectionProtocolLimits.MessageIdBytes);
                    int index = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    int encodedLength = reader.ReadInt32();
                    int decodedLength = reader.ReadInt32();
                    int fragmentLength = reader.ReadInt32();
                    byte[] digest = ProtocolByteUtil.ReadExact(
                        reader,
                        ConnectionProtocolLimits.DigestBytes);

                    if (count <= 0 || count > limits.MaxFragmentCount)
                    {
                        rejection = new ProtocolRejection(
                            ProtocolRejectCode.FragmentCountExceeded,
                            "The character transfer used too many fragments.");
                        return false;
                    }

                    if (index < 0 || index >= count)
                    {
                        rejection = new ProtocolRejection(
                            ProtocolRejectCode.FragmentOutOfRange,
                            "The character fragment index was invalid.");
                        return false;
                    }

                    if (encodedLength < 0 || encodedLength > limits.MaxEncodedMessageBytes ||
                        decodedLength < 0 || decodedLength > limits.MaxDecodedMessageBytes)
                    {
                        rejection = new ProtocolRejection(
                            ProtocolRejectCode.PayloadTooLarge,
                            "The character transfer exceeded the allowed size.");
                        return false;
                    }

                    int expectedCount = GetExpectedFragmentCount(
                        encodedLength,
                        limits.MaxFragmentDataBytes);
                    if (count != expectedCount)
                    {
                        rejection = FragmentError(
                            "The character fragment count did not match its declared size.");
                        return false;
                    }

                    int expectedLength = GetExpectedFragmentLength(
                        index,
                        count,
                        encodedLength,
                        limits.MaxFragmentDataBytes);
                    if (fragmentLength != expectedLength ||
                        fragmentLength < 0 ||
                        fragmentLength > limits.MaxFragmentDataBytes)
                    {
                        rejection = FragmentError(
                            "The character fragment length was invalid.");
                        return false;
                    }

                    if (stream.Length - stream.Position != fragmentLength)
                    {
                        rejection = FragmentError(
                            "The character fragment payload length was inconsistent.");
                        return false;
                    }

                    FragmentContentEncoding encoding = (FragmentContentEncoding)rawEncoding;
                    if (encoding == FragmentContentEncoding.Raw &&
                        encodedLength != decodedLength)
                    {
                        rejection = FragmentError(
                            "A raw character transfer declared inconsistent lengths.");
                        return false;
                    }

                    byte[] data = ProtocolByteUtil.ReadExact(
                        reader,
                        fragmentLength);
                    frame = new FragmentFrame(
                        encoding,
                        messageId,
                        index,
                        count,
                        encodedLength,
                        decodedLength,
                        digest,
                        data);
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                rejection = FragmentError("The character fragment was truncated.");
                return false;
            }
            catch (IOException)
            {
                rejection = FragmentError("The character fragment could not be read.");
                return false;
            }
            catch (OverflowException)
            {
                rejection = FragmentError("The character fragment length overflowed.");
                return false;
            }
        }

        internal static int GetExpectedFragmentCount(int totalLength, int fragmentSize)
        {
            if (totalLength == 0)
            {
                return 1;
            }

            return checked((int)(((long)totalLength + fragmentSize - 1L) / fragmentSize));
        }

        internal static byte[] ComputeSha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        internal static byte[] DecompressBounded(
            byte[] compressed,
            int expectedDecodedLength,
            FragmentTransportLimits limits)
        {
            using (MemoryStream source = new MemoryStream(compressed, false))
            using (GZipStream gzip = new GZipStream(source, CompressionMode.Decompress, false))
            using (MemoryStream destination = new MemoryStream(
                       Math.Min(expectedDecodedLength, 64 * 1024)))
            {
                byte[] buffer = new byte[32 * 1024];
                int total = 0;
                while (true)
                {
                    int read = gzip.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    total = checked(total + read);
                    if (total > limits.MaxDecodedMessageBytes ||
                        total > expectedDecodedLength)
                    {
                        throw new OverflowException(
                            "Decompressed character payload exceeded its declared limit.");
                    }

                    destination.Write(buffer, 0, read);
                }

                if (total != expectedDecodedLength)
                {
                    throw new InvalidDataException(
                        "Decompressed character payload length did not match.");
                }

                return destination.ToArray();
            }
        }

        private static byte[] EncodeFrame(
            FragmentContentEncoding encoding,
            byte[] messageId,
            int index,
            int count,
            int encodedLength,
            int decodedLength,
            byte[] digest,
            byte[] source,
            int offset,
            int length)
        {
            using (MemoryStream stream = new MemoryStream(FrameHeaderBytes + length))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(FrameVersion);
                writer.Write((byte)encoding);
                writer.Write(messageId);
                writer.Write(index);
                writer.Write(count);
                writer.Write(encodedLength);
                writer.Write(decodedLength);
                writer.Write(length);
                writer.Write(digest);
                if (length > 0)
                {
                    writer.Write(source, offset, length);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] Compress(byte[] payload)
        {
            using (MemoryStream destination = new MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(
                           destination,
                           CompressionMode.Compress,
                           true))
                {
                    gzip.Write(payload, 0, payload.Length);
                }

                return destination.ToArray();
            }
        }

        private static int GetExpectedFragmentLength(
            int index,
            int count,
            int totalLength,
            int fragmentSize)
        {
            if (totalLength == 0)
            {
                return 0;
            }

            if (index < count - 1)
            {
                return fragmentSize;
            }

            return checked(totalLength - (count - 1) * fragmentSize);
        }

        private static ProtocolRejection FragmentError(string message)
        {
            return new ProtocolRejection(ProtocolRejectCode.FragmentMalformed, message);
        }
    }

    internal sealed class FragmentFrame
    {
        internal FragmentFrame(
            FragmentContentEncoding encoding,
            byte[] messageId,
            int index,
            int count,
            int encodedLength,
            int decodedLength,
            byte[] digest,
            byte[] data)
        {
            Encoding = encoding;
            MessageId = messageId;
            Index = index;
            Count = count;
            EncodedLength = encodedLength;
            DecodedLength = decodedLength;
            Digest = digest;
            Data = data;
        }

        internal FragmentContentEncoding Encoding { get; private set; }
        internal byte[] MessageId { get; private set; }
        internal int Index { get; private set; }
        internal int Count { get; private set; }
        internal int EncodedLength { get; private set; }
        internal int DecodedLength { get; private set; }
        internal byte[] Digest { get; private set; }
        internal byte[] Data { get; private set; }
    }

    /// <summary>
    /// Stateful, bounded receiver for character fragments. Assemblies are keyed by the exact
    /// ZRpc object, protocol session ID, and cryptographically random character message ID.
    /// Fragments must arrive in canonical index order; duplicates and gaps reject the assembly.
    /// </summary>
    public sealed class BoundedFragmentReassembler : IDisposable
    {
        private static readonly ReadOnlyCollection<ExpiredFragmentAssembly> NoExpirations =
            new ReadOnlyCollection<ExpiredFragmentAssembly>(Array.Empty<ExpiredFragmentAssembly>());

        private sealed class AssemblyKey : IEquatable<AssemblyKey>
        {
            internal AssemblyKey(ZRpc rpc, byte[] sessionId, byte[] messageId)
            {
                Rpc = rpc;
                Session = Convert.ToBase64String(sessionId);
                Message = Convert.ToBase64String(messageId);
            }

            internal ZRpc Rpc { get; private set; }
            internal string Session { get; private set; }
            internal string Message { get; private set; }

            public bool Equals(AssemblyKey other)
            {
                return other != null &&
                       ReferenceEquals(Rpc, other.Rpc) &&
                       string.Equals(Session, other.Session, StringComparison.Ordinal) &&
                       string.Equals(Message, other.Message, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as AssemblyKey);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = RuntimeHelpers.GetHashCode(Rpc);
                    hash = (hash * 397) ^ Session.GetHashCode();
                    hash = (hash * 397) ^ Message.GetHashCode();
                    return hash;
                }
            }
        }

        private sealed class AssemblyEntry : IDisposable
        {
            internal FragmentContentEncoding Encoding;
            internal byte[] Nonce;
            internal byte[] Digest;
            internal byte[] MessageId;
            internal int FragmentCount;
            internal int EncodedLength;
            internal int DecodedLength;
            internal int NextIndex;
            internal long Deadline;
            internal MemoryStream Buffer;

            public void Dispose()
            {
                if (Buffer != null)
                {
                    Buffer.Dispose();
                    Buffer = null;
                }
            }
        }

        private readonly object sync = new object();
        private readonly Dictionary<AssemblyKey, AssemblyEntry> entries =
            new Dictionary<AssemblyKey, AssemblyEntry>();
        private readonly Queue<ExpiredFragmentAssembly> pendingExpirations =
            new Queue<ExpiredFragmentAssembly>();
        private readonly FragmentTransportLimits limits;
        private readonly IProtocolClock clock;
        private readonly bool allowCompressedPayloads;
        private readonly Func<ZRpc, int, ProtocolRejection> newAssemblyAdmission;
        private long reservedBytes;
        private bool disposed;

        public BoundedFragmentReassembler(
            FragmentTransportLimits fragmentLimits,
            IProtocolClock protocolClock = null,
            bool allowCompressed = true,
            Func<ZRpc, int, ProtocolRejection> assemblyAdmission = null)
        {
            limits = fragmentLimits ?? throw new ArgumentNullException(nameof(fragmentLimits));
            clock = protocolClock ?? new StopwatchProtocolClock();
            allowCompressedPayloads = allowCompressed;
            newAssemblyAdmission = assemblyAdmission;
            if (clock.Frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(protocolClock));
            }
        }

        public FragmentAcceptResult AcceptPackage(
            ZRpc peerRpc,
            byte[] expectedSessionId,
            byte[] expectedNonce,
            ProtocolPacket packet)
        {
            if (peerRpc == null)
            {
                return FragmentAcceptResult.Reject(new ProtocolRejection(
                    ProtocolRejectCode.PeerNotFound,
                    "The fragment peer was unavailable."));
            }

            if (packet == null)
            {
                return FragmentAcceptResult.Reject(new ProtocolRejection(
                    ProtocolRejectCode.MalformedPacket,
                    "The character fragment packet was missing."));
            }

            if (packet.Kind != ProtocolPacketKind.CharacterFragment)
            {
                return FragmentAcceptResult.Reject(new ProtocolRejection(
                    ProtocolRejectCode.UnexpectedMessageType,
                    "A character fragment message was expected."));
            }

            if (packet.Sequence != ProtocolSequence.CharacterData)
            {
                return FragmentAcceptResult.Reject(new ProtocolRejection(
                    ProtocolRejectCode.OutOfOrderMessage,
                    "The character fragment sequence was invalid."));
            }

            if (!ProtocolByteUtil.FixedTimeEquals(packet.SessionId, expectedSessionId))
            {
                return FragmentAcceptResult.Reject(new ProtocolRejection(
                    ProtocolRejectCode.SessionIdMismatch,
                    "The character fragment session was invalid."));
            }

            if (!ProtocolByteUtil.FixedTimeEquals(packet.Nonce, expectedNonce))
            {
                return FragmentAcceptResult.Reject(new ProtocolRejection(
                    ProtocolRejectCode.NonceMismatch,
                    "The character fragment nonce was invalid."));
            }

            FragmentFrame frame;
            ProtocolRejection rejection;
            if (!BoundedFragmentCodec.TryDecodeFrame(
                    packet.Payload,
                    limits,
                    out frame,
                    out rejection))
            {
                return FragmentAcceptResult.Reject(rejection);
            }

            if (!allowCompressedPayloads &&
                frame.Encoding != FragmentContentEncoding.Raw)
            {
                return FragmentAcceptResult.Reject(
                    new ProtocolRejection(
                        ProtocolRejectCode.FragmentMalformed,
                        "Compressed client character saves are not accepted."),
                    frame.MessageId);
            }

            lock (sync)
            {
                ThrowIfDisposed();
                AssemblyKey key = new AssemblyKey(peerRpc, packet.SessionId, frame.MessageId);
                AssemblyEntry entry;
                long now = clock.GetTimestamp();
                if (entries.TryGetValue(key, out entry) && now > entry.Deadline)
                {
                    RemoveEntry(key, entry);
                    CleanupExpiredLocked(now);
                    return FragmentAcceptResult.Reject(
                        new ProtocolRejection(
                            ProtocolRejectCode.FragmentAssemblyTimedOut,
                            "The character transfer timed out."),
                        frame.MessageId);
                }

                CleanupExpiredLocked(now);
                if (!entries.TryGetValue(key, out entry))
                {
                    if (frame.Index != 0)
                    {
                        return FragmentAcceptResult.Reject(
                            new ProtocolRejection(
                                ProtocolRejectCode.FragmentOutOfOrder,
                                "The first character fragment was missing."),
                            frame.MessageId);
                    }

                    ProtocolRejection admissionRejection =
                        newAssemblyAdmission == null
                            ? null
                            : newAssemblyAdmission(
                                peerRpc,
                                frame.DecodedLength);
                    if (admissionRejection != null)
                    {
                        return FragmentAcceptResult.Reject(
                            admissionRejection,
                            frame.MessageId);
                    }

                    if (entries.Count >= limits.MaxConcurrentAssemblies ||
                        CountPeerAssemblies(peerRpc) >= limits.MaxConcurrentAssembliesPerPeer ||
                        reservedBytes + frame.EncodedLength > limits.MaxReservedBytes)
                    {
                        return FragmentAcceptResult.Reject(
                            new ProtocolRejection(
                                ProtocolRejectCode.FragmentAssemblyLimitExceeded,
                                "Too many character transfers were in progress."),
                            frame.MessageId);
                    }

                    entry = new AssemblyEntry
                    {
                        Encoding = frame.Encoding,
                        Nonce = ProtocolByteUtil.Clone(packet.Nonce),
                        Digest = ProtocolByteUtil.Clone(frame.Digest),
                        MessageId = ProtocolByteUtil.Clone(frame.MessageId),
                        FragmentCount = frame.Count,
                        EncodedLength = frame.EncodedLength,
                        DecodedLength = frame.DecodedLength,
                        NextIndex = 0,
                        Deadline = AddDuration(
                            now,
                            limits.AssemblyTimeout),
                        Buffer = new MemoryStream(
                            Math.Min(frame.EncodedLength, limits.MaxFragmentDataBytes))
                    };
                    entries.Add(key, entry);
                    reservedBytes += frame.EncodedLength;
                }

                ProtocolRejection metadataError = ValidateEntry(entry, packet, frame);
                if (metadataError != null)
                {
                    RemoveEntry(key, entry);
                    return FragmentAcceptResult.Reject(metadataError, frame.MessageId);
                }

                if (frame.Index < entry.NextIndex)
                {
                    RemoveEntry(key, entry);
                    return FragmentAcceptResult.Reject(
                        new ProtocolRejection(
                            ProtocolRejectCode.DuplicateFragment,
                            "A duplicate character fragment was received."),
                        frame.MessageId);
                }

                if (frame.Index > entry.NextIndex)
                {
                    RemoveEntry(key, entry);
                    return FragmentAcceptResult.Reject(
                        new ProtocolRejection(
                            ProtocolRejectCode.FragmentOutOfOrder,
                            "The character fragments arrived out of order."),
                        frame.MessageId);
                }

                entry.Buffer.Write(frame.Data, 0, frame.Data.Length);
                entry.NextIndex++;
                if (entry.NextIndex < entry.FragmentCount)
                {
                    return FragmentAcceptResult.InProgress(frame.MessageId);
                }

                byte[] encoded = entry.Buffer.ToArray();
                RemoveEntry(key, entry);

                if (encoded.Length != frame.EncodedLength)
                {
                    return FragmentAcceptResult.Reject(
                        new ProtocolRejection(
                            ProtocolRejectCode.FragmentMetadataMismatch,
                            "The assembled character payload length was invalid."),
                        frame.MessageId);
                }

                byte[] actualDigest = BoundedFragmentCodec.ComputeSha256(encoded);
                if (!ProtocolByteUtil.FixedTimeEquals(actualDigest, frame.Digest))
                {
                    return FragmentAcceptResult.Reject(
                        new ProtocolRejection(
                            ProtocolRejectCode.FragmentDigestMismatch,
                            "The assembled character payload failed its digest check."),
                        frame.MessageId);
                }

                try
                {
                    byte[] decoded;
                    if (frame.Encoding == FragmentContentEncoding.Raw)
                    {
                        decoded = encoded;
                    }
                    else
                    {
                        decoded = BoundedFragmentCodec.DecompressBounded(
                            encoded,
                            frame.DecodedLength,
                            limits);
                    }

                    return FragmentAcceptResult.Complete(frame.MessageId, decoded);
                }
                catch (InvalidDataException)
                {
                    return FragmentAcceptResult.Reject(
                        new ProtocolRejection(
                            ProtocolRejectCode.DecompressionFailed,
                            "The compressed character payload was invalid."),
                        frame.MessageId);
                }
                catch (IOException)
                {
                    return FragmentAcceptResult.Reject(
                        new ProtocolRejection(
                            ProtocolRejectCode.DecompressionFailed,
                            "The compressed character payload could not be read."),
                        frame.MessageId);
                }
                catch (OverflowException)
                {
                    return FragmentAcceptResult.Reject(
                        new ProtocolRejection(
                            ProtocolRejectCode.DecompressedPayloadTooLarge,
                            "The decompressed character payload exceeded the allowed size."),
                        frame.MessageId);
                }
            }
        }

        public ReadOnlyCollection<ExpiredFragmentAssembly> CleanupExpired()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                List<ExpiredFragmentAssembly> expired = null;
                DrainPendingExpirationsLocked(ref expired);
                CleanupExpiredLocked(clock.GetTimestamp());
                DrainPendingExpirationsLocked(ref expired);
                return expired == null
                    ? NoExpirations
                    : new ReadOnlyCollection<ExpiredFragmentAssembly>(expired);
            }
        }

        public int RemovePeer(ZRpc peerRpc)
        {
            if (peerRpc == null)
            {
                return 0;
            }

            lock (sync)
            {
                ThrowIfDisposed();
                List<KeyValuePair<AssemblyKey, AssemblyEntry>> remove =
                    new List<KeyValuePair<AssemblyKey, AssemblyEntry>>();
                foreach (KeyValuePair<AssemblyKey, AssemblyEntry> pair in entries)
                {
                    if (ReferenceEquals(pair.Key.Rpc, peerRpc))
                    {
                        remove.Add(pair);
                    }
                }

                foreach (KeyValuePair<AssemblyKey, AssemblyEntry> pair in remove)
                {
                    RemoveEntry(pair.Key, pair.Value);
                }

                RemovePendingExpirationsForPeerLocked(peerRpc);

                return remove.Count;
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

                foreach (AssemblyEntry entry in entries.Values)
                {
                    entry.Dispose();
                }

                entries.Clear();
                pendingExpirations.Clear();
                reservedBytes = 0;
                disposed = true;
            }
        }

        private ProtocolRejection ValidateEntry(
            AssemblyEntry entry,
            ProtocolPacket packet,
            FragmentFrame frame)
        {
            if (!ProtocolByteUtil.FixedTimeEquals(entry.Nonce, packet.Nonce) ||
                !ProtocolByteUtil.FixedTimeEquals(entry.Digest, frame.Digest) ||
                !ProtocolByteUtil.FixedTimeEquals(entry.MessageId, frame.MessageId) ||
                entry.Encoding != frame.Encoding ||
                entry.FragmentCount != frame.Count ||
                entry.EncodedLength != frame.EncodedLength ||
                entry.DecodedLength != frame.DecodedLength)
            {
                return new ProtocolRejection(
                    ProtocolRejectCode.FragmentMetadataMismatch,
                    "Character fragment metadata changed during transfer.");
            }

            if (clock.GetTimestamp() > entry.Deadline)
            {
                return new ProtocolRejection(
                    ProtocolRejectCode.FragmentAssemblyTimedOut,
                    "The character transfer timed out.");
            }

            return null;
        }

        private int CountPeerAssemblies(ZRpc peerRpc)
        {
            int count = 0;
            foreach (AssemblyKey key in entries.Keys)
            {
                if (ReferenceEquals(key.Rpc, peerRpc))
                {
                    count++;
                }
            }

            return count;
        }

        private void CleanupExpiredLocked(long now)
        {
            int availableDiagnostics =
                limits.MaxConcurrentAssemblies - pendingExpirations.Count;
            if (availableDiagnostics <= 0)
            {
                return;
            }

            List<KeyValuePair<AssemblyKey, AssemblyEntry>> remove = null;
            foreach (KeyValuePair<AssemblyKey, AssemblyEntry> pair in entries)
            {
                if (now > pair.Value.Deadline)
                {
                    if (remove == null)
                    {
                        remove = new List<KeyValuePair<AssemblyKey, AssemblyEntry>>();
                    }

                    remove.Add(pair);
                    if (remove.Count >= availableDiagnostics)
                    {
                        break;
                    }
                }
            }

            if (remove == null)
            {
                return;
            }

            foreach (KeyValuePair<AssemblyKey, AssemblyEntry> pair in remove)
            {
                pendingExpirations.Enqueue(
                    new ExpiredFragmentAssembly(
                        pair.Key.Rpc,
                        pair.Value.MessageId));
                RemoveEntry(pair.Key, pair.Value);
            }
        }

        private void DrainPendingExpirationsLocked(
            ref List<ExpiredFragmentAssembly> destination)
        {
            if (pendingExpirations.Count == 0)
            {
                return;
            }

            if (destination == null)
            {
                destination = new List<ExpiredFragmentAssembly>(
                    pendingExpirations.Count + entries.Count);
            }

            while (pendingExpirations.Count > 0)
            {
                destination.Add(pendingExpirations.Dequeue());
            }
        }

        private void RemovePendingExpirationsForPeerLocked(ZRpc peerRpc)
        {
            int pendingCount = pendingExpirations.Count;
            for (int index = 0; index < pendingCount; ++index)
            {
                ExpiredFragmentAssembly expired = pendingExpirations.Dequeue();
                if (!ReferenceEquals(expired.PeerRpc, peerRpc))
                {
                    pendingExpirations.Enqueue(expired);
                }
            }
        }

        private void RemoveEntry(AssemblyKey key, AssemblyEntry entry)
        {
            if (entries.Remove(key))
            {
                reservedBytes -= entry.EncodedLength;
                if (reservedBytes < 0)
                {
                    reservedBytes = 0;
                }

                entry.Dispose();
            }
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

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(BoundedFragmentReassembler));
            }
        }
    }
}
