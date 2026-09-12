using System;
using System.IO;
using System.Text;

namespace ServerManager
{
    public sealed class CharacterEnvelopeCodec
    {
        internal const int Magic = 0x31484353; // "SCH1" in little-endian byte order.
        public const int CurrentProtocolVersion = 2;
        private const int Sha256Length = 32;

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        private readonly CharacterStorageOptions _options;

        public CharacterEnvelopeCodec(CharacterStorageOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
        }

        public byte[] Encode(CharacterEnvelope envelope)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            ValidateEnvelope(envelope);

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, StrictUtf8, true))
            {
                writer.Write(Magic);
                writer.Write(CurrentProtocolVersion);
                writer.Write((int)envelope.Kind);
                writer.Write(envelope.Revision);
                writer.Write(envelope.BaseRevision);
                writer.Write(envelope.SessionId.ToByteArray());
                WriteString(writer, envelope.AccountId, _options.MaxAccountIdUtf8Bytes);
                WriteString(
                    writer,
                    envelope.CharacterName,
                    _options.MaxCharacterNameUtf8Bytes);
                writer.Write(envelope.CreatedUtc.Ticks);
                writer.Write(envelope.ValheimProfileVersion);
                writer.Write(envelope.RequiresFreshLocalCharacter);
                writer.Write(Sha256Length);
                writer.Write(envelope.PayloadSha256Unsafe);
                writer.Write(envelope.PayloadLength);
                writer.Write(envelope.PayloadUnsafe);
                writer.Flush();

                if (stream.Length > _options.MaxEnvelopeBytes)
                {
                    throw new CharacterProtocolException(
                        "The encoded character envelope exceeds the configured limit.");
                }

                return stream.ToArray();
            }
        }

        public CharacterEnvelope Decode(byte[] encoded)
        {
            if (encoded == null)
            {
                throw new ArgumentNullException(nameof(encoded));
            }

            if (encoded.Length < 96 || encoded.Length > _options.MaxEnvelopeBytes)
            {
                throw new CharacterProtocolException(
                    "The character envelope has an invalid total length.");
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(encoded, false))
                using (BinaryReader reader = new BinaryReader(stream, StrictUtf8, true))
                {
                    if (reader.ReadInt32() != Magic)
                    {
                        throw new CharacterProtocolException(
                            "The character envelope magic value is invalid.");
                    }

                    int protocolVersion = reader.ReadInt32();
                    if (protocolVersion != CurrentProtocolVersion)
                    {
                        throw new CharacterProtocolException(
                            "Unsupported character envelope protocol version.");
                    }

                    CharacterEnvelopeKind kind =
                        (CharacterEnvelopeKind)reader.ReadInt32();
                    ValidateKind(kind);

                    long revision = reader.ReadInt64();
                    long baseRevision = reader.ReadInt64();
                    ValidateRevisions(kind, revision, baseRevision);

                    byte[] guidBytes = ProtocolByteUtil.ReadExact(reader, 16);
                    Guid sessionId = new Guid(guidBytes);
                    if (sessionId == Guid.Empty)
                    {
                        throw new CharacterProtocolException(
                            "The character envelope has an empty session ID.");
                    }

                    string accountId =
                        ReadString(reader, _options.MaxAccountIdUtf8Bytes);
                    ValidateAccountId(accountId);

                    string characterName =
                        ReadString(reader, _options.MaxCharacterNameUtf8Bytes);
                    string normalizedName =
                        CharacterNamePolicy.NormalizeAndValidate(characterName);
                    if (!string.Equals(
                            normalizedName,
                            characterName,
                            StringComparison.Ordinal))
                    {
                        throw new CharacterProtocolException(
                            "The envelope character name is not canonical.");
                    }

                    long createdUtcTicks = reader.ReadInt64();
                    DateTime createdUtc = ReadUtcTimestamp(createdUtcTicks);

                    int valheimProfileVersion = reader.ReadInt32();
                    if (valheimProfileVersion < 1 || valheimProfileVersion > 100000)
                    {
                        throw new CharacterProtocolException(
                            "The envelope has an invalid Valheim profile version.");
                    }

                    byte initialOrigin = reader.ReadByte();
                    if (initialOrigin > 1 ||
                        (initialOrigin != 0 && kind != CharacterEnvelopeKind.Snapshot))
                    {
                        throw new CharacterProtocolException(
                            "The character envelope initial-origin metadata is invalid.");
                    }

                    int hashLength = reader.ReadInt32();
                    if (hashLength != Sha256Length)
                    {
                        throw new CharacterProtocolException(
                            "The character payload hash length is invalid.");
                    }

                    byte[] declaredHash = ProtocolByteUtil.ReadExact(
                        reader,
                        hashLength);
                    int payloadLength = reader.ReadInt32();
                    if (payloadLength < 0 || payloadLength > _options.MaxPayloadBytes)
                    {
                        throw new CharacterProtocolException(
                            "The character payload exceeds the configured limit.");
                    }

                    ValidateKindSpecificPayloadLength(kind, payloadLength);

                    if (stream.Length - stream.Position != payloadLength)
                    {
                        throw new CharacterProtocolException(
                            "The character payload length does not match the envelope.");
                    }

                    byte[] payload = ProtocolByteUtil.ReadExact(
                        reader,
                        payloadLength);
                    byte[] actualHash = CharacterCrypto.Sha256(payload);
                    if (!CharacterCrypto.FixedTimeEquals(declaredHash, actualHash))
                    {
                        throw new CharacterProtocolException(
                            "The character payload SHA-256 hash is invalid.");
                    }

                    return new CharacterEnvelope(
                        protocolVersion,
                        kind,
                        revision,
                        baseRevision,
                        sessionId,
                        accountId,
                        characterName,
                        createdUtc,
                        valheimProfileVersion,
                        payload,
                        declaredHash,
                        false,
                        initialOrigin != 0);
                }
            }
            catch (CharacterProtocolException)
            {
                throw;
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                throw new CharacterProtocolException(
                    "The character envelope could not be decoded.",
                    exception);
            }
        }

        public ZPackage ToZPackage(CharacterEnvelope envelope)
        {
            return new ZPackage(Encode(envelope));
        }

        public CharacterEnvelope FromZPackage(ZPackage package)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            return Decode(package.GetArray());
        }

        private void ValidateEnvelope(CharacterEnvelope envelope)
        {
            if (envelope.RequiresFreshLocalCharacter &&
                envelope.Kind != CharacterEnvelopeKind.Snapshot)
            {
                throw new CharacterProtocolException(
                    "Only an authoritative snapshot may carry initial-origin metadata.");
            }
            if (envelope.ProtocolVersion != CurrentProtocolVersion)
            {
                throw new CharacterProtocolException(
                    "The envelope protocol version does not match this codec.");
            }

            ValidateKind(envelope.Kind);
            ValidateRevisions(envelope.Kind, envelope.Revision, envelope.BaseRevision);

            if (envelope.SessionId == Guid.Empty)
            {
                throw new CharacterProtocolException("The envelope session ID is empty.");
            }

            ValidateAccountId(envelope.AccountId);
            string normalizedName =
                CharacterNamePolicy.NormalizeAndValidate(envelope.CharacterName);
            if (!string.Equals(
                    normalizedName,
                    envelope.CharacterName,
                    StringComparison.Ordinal))
            {
                throw new CharacterProtocolException(
                    "The envelope character name is not canonical.");
            }

            if (envelope.CreatedUtc.Kind != DateTimeKind.Utc)
            {
                throw new CharacterProtocolException(
                    "Character envelope timestamps must be UTC.");
            }

            if (envelope.PayloadLength > _options.MaxPayloadBytes)
            {
                throw new CharacterProtocolException(
                    "The character payload exceeds the configured limit.");
            }

            ValidateKindSpecificPayloadLength(
                envelope.Kind,
                envelope.PayloadLength);

            if (envelope.PayloadSha256Unsafe == null ||
                envelope.PayloadSha256Unsafe.Length != Sha256Length)
            {
                throw new CharacterProtocolException(
                    "The character payload SHA-256 is missing.");
            }

            byte[] actualHash = CharacterCrypto.Sha256(envelope.PayloadUnsafe);
            if (!CharacterCrypto.FixedTimeEquals(
                    envelope.PayloadSha256Unsafe,
                    actualHash))
            {
                throw new CharacterProtocolException(
                    "The in-memory character payload no longer matches its hash.");
            }
        }

        private static void ValidateKind(CharacterEnvelopeKind kind)
        {
            if (kind != CharacterEnvelopeKind.Snapshot &&
                kind != CharacterEnvelopeKind.SaveRequest &&
                kind != CharacterEnvelopeKind.SaveAccepted &&
                kind != CharacterEnvelopeKind.SaveRejected &&
                kind != CharacterEnvelopeKind.InventorySaveRequest)
            {
                throw new CharacterProtocolException(
                    "The character envelope message kind is invalid.");
            }
        }

        private static void ValidateRevisions(
            CharacterEnvelopeKind kind,
            long revision,
            long baseRevision)
        {
            if (revision < 0 || baseRevision < 0)
            {
                throw new CharacterProtocolException(
                    "Character revisions may not be negative.");
            }

            if ((kind == CharacterEnvelopeKind.SaveRequest ||
                 kind == CharacterEnvelopeKind.InventorySaveRequest) &&
                (baseRevision == long.MaxValue || revision != baseRevision + 1))
            {
                throw new CharacterProtocolException(
                    "A save request revision must be exactly baseRevision + 1.");
            }

            if (kind == CharacterEnvelopeKind.Snapshot && revision < 1)
            {
                throw new CharacterProtocolException(
                    "A persisted character snapshot must have a positive revision.");
            }
        }

        private static void ValidateKindSpecificPayloadLength(
            CharacterEnvelopeKind kind,
            int payloadLength)
        {
            if (kind == CharacterEnvelopeKind.InventorySaveRequest &&
                (payloadLength < sizeof(int) + sizeof(ushort) ||
                 payloadLength >
                     ValheimPlayerProfileCodec.MaximumInventorySnapshotBytes))
            {
                throw new CharacterProtocolException(
                    "The inventory snapshot payload has an invalid length.");
            }
        }

        private void ValidateAccountId(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                StrictUtf8.GetByteCount(value) > _options.MaxAccountIdUtf8Bytes)
            {
                throw new CharacterProtocolException(
                    "The envelope account identity is missing or too long.");
            }

            for (int index = 0; index < value.Length; ++index)
            {
                if (char.IsControl(value[index]) || char.IsSurrogate(value[index]))
                {
                    throw new CharacterProtocolException(
                        "The envelope account identity contains invalid characters.");
                }
            }
        }

        private static DateTime ReadUtcTimestamp(long ticks)
        {
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                throw new CharacterProtocolException(
                    "The character envelope timestamp is out of range.");
            }

            return new DateTime(ticks, DateTimeKind.Utc);
        }

        private static void WriteString(BinaryWriter writer, string value, int maximumBytes)
        {
            if (value == null)
            {
                throw new CharacterProtocolException(
                    "A required envelope string is null.");
            }

            byte[] bytes = StrictUtf8.GetBytes(value);
            if (bytes.Length > maximumBytes)
            {
                throw new CharacterProtocolException(
                    "An envelope string exceeds its configured UTF-8 limit.");
            }

            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader, int maximumBytes)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maximumBytes)
            {
                throw new CharacterProtocolException(
                    "An envelope string has an invalid UTF-8 length.");
            }

            return StrictUtf8.GetString(
                ProtocolByteUtil.ReadExact(reader, length));
        }
    }
}
