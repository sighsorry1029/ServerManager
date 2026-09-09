using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ServerManager
{
    /// <summary>
    /// Deterministic binary codec for IntegrityManifest.
    ///
    /// Format (all integers are little-endian):
    ///   4 bytes magic "SMIF"
    ///   int32 schema version
    ///   int32 entry count
    ///   repeated entries, sorted by canonical GUID:
    ///     int32 + UTF-8 GUID
    ///     int32 + UTF-8 display name
    ///     32 raw SHA-256 bytes
    ///
    /// Location is intentionally absent. No BinaryReader.ReadString call is
    /// used, so every allocation is bounded before it occurs.
    /// </summary>
    public static class IntegrityManifestCodec
    {
        private static readonly byte[] Magic =
        {
            (byte)'S',
            (byte)'M',
            (byte)'I',
            (byte)'F'
        };

        public const int WireSchemaVersion = 2;

        public static IntegrityManifestEncodeResult TryEncode(
            IntegrityManifest? manifest)
        {
            return TryEncode(manifest, IntegrityLimits.Default);
        }

        public static IntegrityManifestEncodeResult TryEncode(
            IntegrityManifest? manifest,
            IntegrityLimits limits)
        {
            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            if (manifest == null)
            {
                return EncodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.ManifestUnavailable,
                        "A manifest is required."));
            }

            if (manifest.Entries.Count > limits.MaxPluginCount)
            {
                return EncodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.ManifestTooManyEntries,
                        "Manifest entry count " +
                        manifest.Entries.Count +
                        " exceeds the configured limit of " +
                        limits.MaxPluginCount +
                        "."));
            }

            try
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    stream.Write(Magic, 0, Magic.Length);
                    WriteInt32(stream, WireSchemaVersion);
                    WriteInt32(stream, manifest.Entries.Count);

                    string? previousGuid = null;
                    foreach (IntegrityManifestEntry entry in manifest.Entries)
                    {
                        IntegrityDiagnostic diagnostic;
                        string pluginGuid;
                        string name;
                        string sha256;

                        if (entry == null)
                        {
                            return EncodeFailed(
                                IntegrityCanonical.Error(
                                    IntegrityDiagnosticCodes.WireMalformed,
                                    "Manifest contains a null entry."));
                        }

                        if (!IntegrityCanonical.TryNormalizeGuid(
                                entry.PluginGuid,
                                limits,
                                IntegrityDiagnosticCodes.ManifestInvalidGuid,
                                out pluginGuid,
                                out diagnostic))
                        {
                            return EncodeFailed(diagnostic);
                        }

                        if (!IntegrityCanonical.TryNormalizeDisplayString(
                                entry.Name,
                                limits.MaxNameUtf8Bytes,
                                "Plugin name",
                                IntegrityDiagnosticCodes.ManifestInvalidName,
                                pluginGuid,
                                out name,
                                out diagnostic))
                        {
                            return EncodeFailed(diagnostic);
                        }

                        if (!IntegrityCanonical.TryNormalizeSha256(
                                entry.FileSha256,
                                IntegrityDiagnosticCodes.ManifestInvalidHash,
                                pluginGuid,
                                out sha256,
                                out diagnostic))
                        {
                            return EncodeFailed(diagnostic);
                        }

                        if (previousGuid != null &&
                            StringComparer.Ordinal.Compare(previousGuid, pluginGuid) >= 0)
                        {
                            return EncodeFailed(
                                IntegrityCanonical.Error(
                                    StringComparer.Ordinal.Equals(previousGuid, pluginGuid)
                                        ? IntegrityDiagnosticCodes.ManifestDuplicateGuid
                                        : IntegrityDiagnosticCodes.WireNonCanonical,
                                    "Manifest entries are not in strictly increasing GUID order.",
                                    pluginGuid));
                        }

                        previousGuid = pluginGuid;
                        WriteString(stream, pluginGuid);
                        WriteString(stream, name);
                        byte[] hashBytes = IntegrityCanonical.HexToBytes(sha256);
                        stream.Write(hashBytes, 0, hashBytes.Length);

                        if (stream.Length > limits.MaxPayloadBytes)
                        {
                            return EncodeFailed(
                                IntegrityCanonical.Error(
                                    IntegrityDiagnosticCodes.WirePayloadTooLarge,
                                    "Encoded manifest exceeds the configured limit of " +
                                    limits.MaxPayloadBytes +
                                    " bytes."));
                        }
                    }

                    return new IntegrityManifestEncodeResult(
                        true,
                        stream.ToArray(),
                        Array.Empty<IntegrityDiagnostic>());
                }
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                return EncodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.WireMalformed,
                        "Could not encode manifest: " +
                        exception.GetType().Name +
                        ": " +
                        exception.Message));
            }
        }

        public static IntegrityManifestDecodeResult TryDecode(byte[]? payload)
        {
            return TryDecode(payload, IntegrityLimits.Default);
        }

        public static IntegrityManifestDecodeResult TryDecode(
            byte[]? payload,
            IntegrityLimits limits)
        {
            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            if (payload == null)
            {
                return DecodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.WirePayloadMissing,
                        "Manifest payload is missing."));
            }

            if (payload.Length > limits.MaxPayloadBytes)
            {
                return DecodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.WirePayloadTooLarge,
                        "Manifest payload is " +
                        payload.Length +
                        " bytes, exceeding the configured limit of " +
                        limits.MaxPayloadBytes +
                        " bytes."));
            }

            if (payload.Length < Magic.Length + 8)
            {
                return DecodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.WireMalformed,
                        "Manifest payload is shorter than the protocol header."));
            }

            WireReader reader = new WireReader(payload);
            byte[] receivedMagic;
            if (!reader.TryReadBytes(Magic.Length, out receivedMagic) ||
                !EqualBytes(Magic, receivedMagic))
            {
                return DecodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.WireInvalidMagic,
                        "Manifest payload has an invalid protocol marker."));
            }

            int schemaVersion;
            if (!reader.TryReadInt32(out schemaVersion))
            {
                return DecodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.WireMalformed,
                        "Manifest payload does not contain a schema version."));
            }

            if (schemaVersion != WireSchemaVersion)
            {
                return DecodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.WireUnsupportedSchema,
                        "Manifest schema " +
                        schemaVersion +
                        " is unsupported; expected " +
                        WireSchemaVersion +
                        "."));
            }

            int entryCount;
            if (!reader.TryReadInt32(out entryCount) ||
                entryCount < 0)
            {
                return DecodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.WireMalformed,
                        "Manifest contains an invalid entry count."));
            }

            if (entryCount > limits.MaxPluginCount)
            {
                return DecodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.ManifestTooManyEntries,
                        "Manifest entry count " +
                        entryCount +
                        " exceeds the configured limit of " +
                        limits.MaxPluginCount +
                        "."));
            }

            List<IntegrityManifestEntry> entries =
                new List<IntegrityManifestEntry>(entryCount);
            string? previousGuid = null;

            for (int index = 0; index < entryCount; index++)
            {
                string rawGuid;
                string rawName;
                string readError;
                if (!reader.TryReadString(
                        limits.MaxGuidUtf8Bytes,
                        out rawGuid,
                        out readError) ||
                    !reader.TryReadString(
                        limits.MaxNameUtf8Bytes,
                        out rawName,
                        out readError))
                {
                    return DecodeFailed(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.WireMalformed,
                            "Manifest entry " + index + " is malformed: " + readError));
                }

                byte[] rawHash;
                if (!reader.TryReadBytes(32, out rawHash))
                {
                    return DecodeFailed(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.WireMalformed,
                            "Manifest entry " +
                            index +
                            " has a truncated SHA-256 value."));
                }

                IntegrityDiagnostic diagnostic;
                string pluginGuid;
                string name;
                if (!IntegrityCanonical.TryNormalizeGuid(
                        rawGuid,
                        limits,
                        IntegrityDiagnosticCodes.ManifestInvalidGuid,
                        out pluginGuid,
                        out diagnostic))
                {
                    return DecodeFailed(diagnostic);
                }

                if (!IntegrityCanonical.TryNormalizeDisplayString(
                        rawName,
                        limits.MaxNameUtf8Bytes,
                        "Plugin name",
                        IntegrityDiagnosticCodes.ManifestInvalidName,
                        pluginGuid,
                        out name,
                        out diagnostic))
                {
                    return DecodeFailed(diagnostic);
                }

                if (!StringComparer.Ordinal.Equals(rawGuid, pluginGuid) ||
                    !StringComparer.Ordinal.Equals(rawName, name))
                {
                    return DecodeFailed(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.WireNonCanonical,
                            "Manifest entry strings are not in canonical form.",
                            pluginGuid));
                }

                if (previousGuid != null)
                {
                    int order = StringComparer.Ordinal.Compare(
                        previousGuid,
                        pluginGuid);
                    if (order == 0)
                    {
                        return DecodeFailed(
                            IntegrityCanonical.Error(
                                IntegrityDiagnosticCodes.ManifestDuplicateGuid,
                                "Manifest contains duplicate GUID '" +
                                pluginGuid +
                                "'.",
                                pluginGuid));
                    }

                    if (order > 0)
                    {
                        return DecodeFailed(
                            IntegrityCanonical.Error(
                                IntegrityDiagnosticCodes.WireNonCanonical,
                                "Manifest entries are not sorted by canonical GUID.",
                                pluginGuid));
                    }
                }

                previousGuid = pluginGuid;
                entries.Add(
                    new IntegrityManifestEntry(
                        pluginGuid,
                        name,
                        IntegrityCanonical.ToLowerHex(rawHash),
                        limits));
            }

            if (reader.Remaining != 0)
            {
                return DecodeFailed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.WireTrailingData,
                        "Manifest payload contains " +
                        reader.Remaining +
                        " unexpected trailing bytes."));
            }

            return new IntegrityManifestDecodeResult(
                true,
                new IntegrityManifest(entries),
                Array.Empty<IntegrityDiagnostic>());
        }

        private static void WriteString(Stream stream, string value)
        {
            byte[] encoded = IntegrityCanonical.Utf8.GetBytes(value);
            WriteInt32(stream, encoded.Length);
            stream.Write(encoded, 0, encoded.Length);
        }

        private static void WriteInt32(Stream stream, int value)
        {
            unchecked
            {
                stream.WriteByte((byte)value);
                stream.WriteByte((byte)(value >> 8));
                stream.WriteByte((byte)(value >> 16));
                stream.WriteByte((byte)(value >> 24));
            }
        }

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static IntegrityManifestEncodeResult EncodeFailed(
            IntegrityDiagnostic diagnostic)
        {
            return new IntegrityManifestEncodeResult(
                false,
                null,
                new[] { diagnostic });
        }

        private static IntegrityManifestDecodeResult DecodeFailed(
            IntegrityDiagnostic diagnostic)
        {
            return new IntegrityManifestDecodeResult(
                false,
                null,
                new[] { diagnostic });
        }

        private sealed class WireReader
        {
            private readonly byte[] _payload;
            private int _offset;

            internal WireReader(byte[] payload)
            {
                _payload = payload;
            }

            internal int Remaining => _payload.Length - _offset;

            internal bool TryReadInt32(out int value)
            {
                value = 0;
                if (Remaining < 4)
                {
                    return false;
                }

                unchecked
                {
                    value =
                        _payload[_offset] |
                        _payload[_offset + 1] << 8 |
                        _payload[_offset + 2] << 16 |
                        _payload[_offset + 3] << 24;
                }

                _offset += 4;
                return true;
            }

            internal bool TryReadBytes(int length, out byte[] bytes)
            {
                bytes = Array.Empty<byte>();
                if (length < 0 || length > Remaining)
                {
                    return false;
                }

                bytes = new byte[length];
                Buffer.BlockCopy(_payload, _offset, bytes, 0, length);
                _offset += length;
                return true;
            }

            internal bool TryReadString(
                int maximumUtf8Bytes,
                out string value,
                out string error)
            {
                value = string.Empty;
                error = string.Empty;

                int byteLength;
                if (!TryReadInt32(out byteLength))
                {
                    error = "string length is missing";
                    return false;
                }

                if (byteLength < 0)
                {
                    error = "string length is negative";
                    return false;
                }

                if (byteLength > maximumUtf8Bytes)
                {
                    error = "string length exceeds its configured limit";
                    return false;
                }

                if (byteLength > Remaining)
                {
                    error = "string bytes are truncated";
                    return false;
                }

                try
                {
                    value = IntegrityCanonical.Utf8.GetString(
                        _payload,
                        _offset,
                        byteLength);
                }
                catch (DecoderFallbackException)
                {
                    error = "string contains invalid UTF-8";
                    return false;
                }

                _offset += byteLength;
                return true;
            }
        }
    }
}
