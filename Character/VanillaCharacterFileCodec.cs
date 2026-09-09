using System;
using System.IO;
using System.Security.Cryptography;

namespace ServerManager
{
    /// <summary>
    /// Encodes and validates the small disk wrapper used by Valheim .fch files.
    /// The payload itself is the raw outer PlayerProfile ZPackage and is parsed
    /// separately by <see cref="ValheimPlayerProfileCodec"/>.
    /// </summary>
    internal static class VanillaCharacterFileCodec
    {
        internal const int ChecksumLength = 64;
        internal const int WrapperOverheadBytes = sizeof(int) + sizeof(int) + ChecksumLength;
        internal const int MinimumPlayerProfileBytes = 16;

        internal static byte[] Encode(byte[] rawPlayerProfile, int maximumPayloadBytes)
        {
            ValidateMaximumPayloadBytes(maximumPayloadBytes);
            if (rawPlayerProfile == null)
            {
                throw new ArgumentNullException(nameof(rawPlayerProfile));
            }

            ValidatePayloadLength(rawPlayerProfile.Length, maximumPayloadBytes);
            byte[] checksum;
            using (SHA512 sha512 = SHA512.Create())
            {
                checksum = sha512.ComputeHash(rawPlayerProfile);
            }

            byte[] encoded = new byte[checked(rawPlayerProfile.Length + WrapperOverheadBytes)];
            WriteLittleEndianInt32(encoded, 0, rawPlayerProfile.Length);
            Buffer.BlockCopy(rawPlayerProfile, 0, encoded, sizeof(int), rawPlayerProfile.Length);
            int checksumLengthOffset = checked(sizeof(int) + rawPlayerProfile.Length);
            WriteLittleEndianInt32(encoded, checksumLengthOffset, ChecksumLength);
            Buffer.BlockCopy(
                checksum,
                0,
                encoded,
                checksumLengthOffset + sizeof(int),
                ChecksumLength);
            return encoded;
        }

        internal static byte[] Decode(byte[] encoded, int maximumPayloadBytes)
        {
            if (encoded == null)
            {
                throw new ArgumentNullException(nameof(encoded));
            }

            using (MemoryStream stream = new MemoryStream(encoded, false))
            {
                return Decode(stream, maximumPayloadBytes);
            }
        }

        /// <summary>
        /// Reads one complete .fch wrapper from the current stream position.
        /// The stream is not closed and no trailing bytes are accepted.
        /// </summary>
        internal static byte[] Decode(Stream stream, int maximumPayloadBytes)
        {
            ValidateMaximumPayloadBytes(maximumPayloadBytes);
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (!stream.CanRead || !stream.CanSeek)
            {
                throw new ArgumentException(
                    "A readable, seekable .fch stream is required.",
                    nameof(stream));
            }

            try
            {
                long remaining = checked(stream.Length - stream.Position);
                long maximumFileBytes = checked((long)maximumPayloadBytes + WrapperOverheadBytes);
                if (remaining < MinimumPlayerProfileBytes + WrapperOverheadBytes ||
                    remaining > maximumFileBytes)
                {
                    throw new CharacterStorageException(
                        "The .fch file length is outside the configured bounds.");
                }

                int payloadLength = ReadLittleEndianInt32(stream, "payload length");
                ValidatePayloadLength(payloadLength, maximumPayloadBytes);
                if (remaining != checked((long)payloadLength + WrapperOverheadBytes))
                {
                    throw new CharacterStorageException(
                        "The .fch declared lengths do not match the exact file length.");
                }

                byte[] payload = ReadExactly(stream, payloadLength, "PlayerProfile payload");
                int checksumLength = ReadLittleEndianInt32(stream, "checksum length");
                if (checksumLength != ChecksumLength)
                {
                    throw new CharacterStorageException(
                        "The .fch checksum length is not exactly 64 bytes.");
                }

                byte[] declaredChecksum = ReadExactly(stream, ChecksumLength, "SHA-512 checksum");
                if (stream.Position != stream.Length)
                {
                    throw new CharacterStorageException(
                        "The .fch file contains trailing data.");
                }

                byte[] actualChecksum;
                using (SHA512 sha512 = SHA512.Create())
                {
                    actualChecksum = sha512.ComputeHash(payload);
                }

                if (!CharacterCrypto.FixedTimeEquals(declaredChecksum, actualChecksum))
                {
                    throw new CharacterStorageException(
                        "The .fch SHA-512 checksum does not match its payload.");
                }

                return payload;
            }
            catch (CharacterStorageException)
            {
                throw;
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                throw new CharacterStorageException(
                    "The .fch file could not be decoded safely.",
                    exception);
            }
        }

        private static void ValidateMaximumPayloadBytes(int maximumPayloadBytes)
        {
            if (maximumPayloadBytes < MinimumPlayerProfileBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
            }
        }

        private static void ValidatePayloadLength(int payloadLength, int maximumPayloadBytes)
        {
            if (payloadLength < MinimumPlayerProfileBytes ||
                payloadLength > maximumPayloadBytes)
            {
                throw new CharacterStorageException(
                    "The .fch payload length is invalid.");
            }
        }

        private static int ReadLittleEndianInt32(Stream stream, string fieldName)
        {
            byte[] bytes = ReadExactly(stream, sizeof(int), fieldName);
            return bytes[0] |
                   (bytes[1] << 8) |
                   (bytes[2] << 16) |
                   (bytes[3] << 24);
        }

        private static byte[] ReadExactly(Stream stream, int byteCount, string fieldName)
        {
            byte[] value = new byte[byteCount];
            int offset = 0;
            while (offset < value.Length)
            {
                int read = stream.Read(value, offset, value.Length - offset);
                if (read <= 0)
                {
                    throw new CharacterStorageException(
                        "The .fch file is truncated inside its " + fieldName + ".");
                }

                offset += read;
            }

            return value;
        }

        private static void WriteLittleEndianInt32(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }
    }
}
