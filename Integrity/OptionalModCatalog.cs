using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ServerManager
{
    internal enum OptionalModCatalogStatus
    {
        Available,
        Missing,
        Invalid,
        TooLarge
    }

    internal sealed class OptionalModCatalogEntry
    {
        internal OptionalModCatalogEntry(string pluginGuid, string name, IEnumerable<string> versions)
        {
            PluginGuid = pluginGuid;
            Name = name;
            Versions = Array.AsReadOnly(versions.ToArray());
        }

        // Identity for client-local installed-mod matching, never a display label.
        internal string PluginGuid { get; }
        internal string Name { get; }
        internal IReadOnlyList<string> Versions { get; }
    }

    internal sealed class OptionalModCatalogResult
    {
        internal OptionalModCatalogResult(OptionalModCatalogStatus status,
            IEnumerable<OptionalModCatalogEntry>? entries = null)
        {
            Status = status;
            Entries = Array.AsReadOnly((entries ?? Array.Empty<OptionalModCatalogEntry>()).ToArray());
        }

        internal OptionalModCatalogStatus Status { get; }
        internal IReadOnlyList<OptionalModCatalogEntry> Entries { get; }
    }

    // Advisory Steam rules, entirely separate from the admission protocol. No
    // file hash, path, password, or other server policy data is serialized. GUIDs
    // support exact client-local installed-mod matching, not admission decisions.
    // The digest detects truncation/mixed publications, not a trustworthy server.
    internal static class OptionalModCatalog
    {
        internal const string KeyPrefix = "sm_optional_";
        internal const string HeaderKey = KeyPrefix + "header";
        internal const string SchemaVersion = "2";
        internal const string UnavailableHeaderValue = SchemaVersion + "|missing|0|0|";
        internal const int MaximumRuleValueBytes = 96;
        internal const int MaximumChunkCount = 146;
        internal const int MaximumRulesBytes = 16 * 1024;
        internal const int MaximumEntryCount = 128;
        internal const int MaximumGuidBytes = IntegrityLimits.DefaultMaxGuidUtf8Bytes;
        internal const int MaximumNameBytes = 256;
        internal const int MaximumVersionBytes = 64;
        internal const int MaximumVersionsPerEntry = 32;
        private const int MaximumPayloadBytes = MaximumChunkCount * (MaximumRuleValueBytes / 4 * 3);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static IReadOnlyDictionary<string, string> Encode(IntegrityPolicySnapshot snapshot)
        {
            if (snapshot == null) return Unavailable(OptionalModCatalogStatus.Missing);
            try
            {
                List<IntegrityPolicyRule> rules = snapshot.Rules
                    .Where(rule => rule.Requirement == IntegrityRequirement.Optional &&
                        !IntegrityAssemblyIdentity.IsLibraryKey(rule.PluginGuid))
                    .ToList();
                if (rules.Count > MaximumEntryCount) throw new CatalogTooLargeException();
                // Bound every field before constructing sorting keys or payloads.
                // A reference DLL's display metadata is not an admission rule.
                HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (IntegrityPolicyRule rule in rules)
                {
                    ValidatePluginGuid(rule.PluginGuid);
                    if (!identities.Add(rule.PluginGuid)) throw new InvalidDataException();
                    ValidateText(rule.DisplayName, MaximumNameBytes);
                    if (rule.AllowedVersions.Count > MaximumVersionsPerEntry) throw new CatalogTooLargeException();
                    if (rule.AllowedVersions.Count == 0) throw new InvalidDataException();
                    foreach (string version in rule.AllowedVersions) ValidateText(version, MaximumVersionBytes);
                }
                rules = rules
                    .OrderBy(rule => rule.DisplayName, StringComparer.Ordinal)
                    .ThenBy(rule => string.Join("\0", rule.AllowedVersions), StringComparer.Ordinal)
                    .ThenBy(rule => rule.PluginGuid, StringComparer.Ordinal)
                    .ToList();
                List<byte> bytes = new List<byte>();
                WriteLength(bytes, rules.Count);
                foreach (IntegrityPolicyRule rule in rules)
                {
                    WriteText(bytes, rule.PluginGuid, MaximumGuidBytes);
                    WriteText(bytes, rule.DisplayName, MaximumNameBytes);
                    if (rule.AllowedVersions.Count > MaximumVersionsPerEntry) throw new CatalogTooLargeException();
                    if (rule.AllowedVersions.Count == 0) throw new InvalidDataException();
                    bytes.Add((byte)rule.AllowedVersions.Count);
                    foreach (string version in rule.AllowedVersions)
                        WriteText(bytes, version, MaximumVersionBytes);
                }

                byte[] payload = bytes.ToArray();
                string encoded = Convert.ToBase64String(payload);
                int chunks = (encoded.Length + MaximumRuleValueBytes - 1) / MaximumRuleValueBytes;
                Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [HeaderKey] = SchemaVersion + "|available|" + Number(rules.Count) + "|" + Number(chunks) + "|" + Digest(payload)
                };
                for (int index = 0; index < chunks; index++)
                {
                    int start = index * MaximumRuleValueBytes;
                    result.Add(ChunkKey(index), encoded.Substring(start,
                        Math.Min(MaximumRuleValueBytes, encoded.Length - start)));
                }
                if (result.Sum(pair => pair.Key.Length + pair.Value.Length) > MaximumRulesBytes)
                    throw new CatalogTooLargeException();
                return new ReadOnlyDictionary<string, string>(result);
            }
            catch (CatalogTooLargeException) { return Unavailable(OptionalModCatalogStatus.TooLarge); }
            catch (ArgumentException) { return Unavailable(OptionalModCatalogStatus.Invalid); }
            catch (InvalidDataException) { return Unavailable(OptionalModCatalogStatus.Invalid); }
        }

        internal static OptionalModCatalogResult Decode(IReadOnlyDictionary<string, string> rules)
        {
            if (rules == null) return Result(OptionalModCatalogStatus.Missing);
            try
            {
                // Inspect only our namespace. The server's other rules are not
                // ours to validate or clear. Empty old chunks permit shrinkage.
                int totalBytes = 0;
                int ownedCount = 0;
                Dictionary<string, string> owned = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> rule in rules)
                {
                    if (!rule.Key.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
                    if (rule.Value == null) return Result(OptionalModCatalogStatus.Invalid);
                    if (++ownedCount > MaximumChunkCount + 1 || rule.Key.Length > HeaderKey.Length ||
                        rule.Value.Length > MaximumRuleValueBytes)
                        return Result(OptionalModCatalogStatus.TooLarge);
                    if (!IsAscii(rule.Key) || !IsAscii(rule.Value)) return Result(OptionalModCatalogStatus.Invalid);
                    totalBytes += rule.Key.Length + rule.Value.Length;
                    if (totalBytes > MaximumRulesBytes) return Result(OptionalModCatalogStatus.TooLarge);
                    if (rule.Key != HeaderKey && !IsChunkKey(rule.Key)) return Result(OptionalModCatalogStatus.Invalid);
                    if (owned.ContainsKey(rule.Key)) return Result(OptionalModCatalogStatus.Invalid);
                    owned.Add(rule.Key, rule.Value);
                }
                // Do not inherit a caller's case-insensitive key comparer: only
                // keys whose exact spelling was bounded above can be consumed.
                if (!owned.TryGetValue(HeaderKey, out string header))
                    return Result(ownedCount == 0 ? OptionalModCatalogStatus.Missing : OptionalModCatalogStatus.Invalid);
                string[] fields = header.Split('|');
                if (fields.Length != 5 || fields[0] != SchemaVersion) return Result(OptionalModCatalogStatus.Invalid);
                if (fields[1] != "available")
                {
                    if (fields[2] != "0" || fields[3] != "0" || fields[4] != string.Empty)
                        return Result(OptionalModCatalogStatus.Invalid);
                    switch (fields[1])
                    {
                        case "missing": return Result(OptionalModCatalogStatus.Missing);
                        case "invalid": return Result(OptionalModCatalogStatus.Invalid);
                        case "too_large": return Result(OptionalModCatalogStatus.TooLarge);
                        default: return Result(OptionalModCatalogStatus.Invalid);
                    }
                }
                int entryCount = ReadCount(fields[2], MaximumEntryCount);
                int chunkCount = ReadCount(fields[3], MaximumChunkCount);
                if (chunkCount == 0 || fields[4].Length != 64 ||
                    fields[4].Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
                    return Result(OptionalModCatalogStatus.Invalid);
                StringBuilder encoded = new StringBuilder(chunkCount * MaximumRuleValueBytes);
                for (int index = 0; index < chunkCount; index++)
                {
                    if (!owned.TryGetValue(ChunkKey(index), out string chunk) || chunk.Length == 0 ||
                        (index < chunkCount - 1 && chunk.Length != MaximumRuleValueBytes))
                        return Result(OptionalModCatalogStatus.Invalid);
                    encoded.Append(chunk);
                }
                string encodedText = encoded.ToString();
                byte[] payload = Convert.FromBase64String(encodedText);
                if (payload.Length > MaximumPayloadBytes) throw new CatalogTooLargeException();
                if (Convert.ToBase64String(payload) != encodedText || Digest(payload) != fields[4])
                    return Result(OptionalModCatalogStatus.Invalid);
                int offset = 0;
                if (ReadLength(payload, ref offset) != entryCount) return Result(OptionalModCatalogStatus.Invalid);
                List<OptionalModCatalogEntry> entries = new List<OptionalModCatalogEntry>(entryCount);
                HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);
                string? previousName = null;
                string? previousVersions = null;
                string? previousGuid = null;
                for (int index = 0; index < entryCount; index++)
                {
                    string pluginGuid = ReadText(payload, ref offset, MaximumGuidBytes);
                    ValidatePluginGuid(pluginGuid);
                    if (!identities.Add(pluginGuid)) return Result(OptionalModCatalogStatus.Invalid);
                    string name = ReadText(payload, ref offset, MaximumNameBytes);
                    int versionCount = ReadByte(payload, ref offset);
                    if (versionCount > MaximumVersionsPerEntry) throw new CatalogTooLargeException();
                    if (versionCount == 0) return Result(OptionalModCatalogStatus.Invalid);
                    List<string> versions = new List<string>(versionCount);
                    for (int versionIndex = 0; versionIndex < versionCount; versionIndex++)
                    {
                        string version = ReadText(payload, ref offset, MaximumVersionBytes);
                        if (versions.Count != 0 && StringComparer.Ordinal.Compare(versions[versions.Count - 1], version) >= 0)
                            return Result(OptionalModCatalogStatus.Invalid);
                        versions.Add(version);
                    }
                    string versionOrder = string.Join("\0", versions);
                    int nameOrder = StringComparer.Ordinal.Compare(previousName, name);
                    int versionsOrder = StringComparer.Ordinal.Compare(previousVersions, versionOrder);
                    if (previousName != null && (nameOrder > 0 ||
                        (nameOrder == 0 && (versionsOrder > 0 ||
                            (versionsOrder == 0 && StringComparer.Ordinal.Compare(previousGuid, pluginGuid) >= 0)))))
                        return Result(OptionalModCatalogStatus.Invalid);
                    entries.Add(new OptionalModCatalogEntry(pluginGuid, name, versions));
                    previousName = name;
                    previousVersions = versionOrder;
                    previousGuid = pluginGuid;
                }
                return offset == payload.Length
                    ? new OptionalModCatalogResult(OptionalModCatalogStatus.Available, entries)
                    : Result(OptionalModCatalogStatus.Invalid);
            }
            catch (CatalogTooLargeException) { return Result(OptionalModCatalogStatus.TooLarge); }
            catch (ArgumentException) { return Result(OptionalModCatalogStatus.Invalid); }
            catch (FormatException) { return Result(OptionalModCatalogStatus.Invalid); }
            catch (InvalidDataException) { return Result(OptionalModCatalogStatus.Invalid); }
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string ChunkKey(int index) => KeyPrefix + index.ToString("D3", CultureInfo.InvariantCulture);

        private static bool IsChunkKey(string key)
        {
            if (key.Length != KeyPrefix.Length + 3) return false;
            string suffix = key.Substring(KeyPrefix.Length);
            return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
                index >= 0 && index < MaximumChunkCount && key == ChunkKey(index);
        }

        private static int ReadCount(string text, int maximum)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || Number(value) != text)
                throw new InvalidDataException();
            if (value > maximum) throw new CatalogTooLargeException();
            return value;
        }

        private static bool IsAscii(string text) => text.All(c => c >= 0x20 && c <= 0x7e);

        private static string Digest(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return IntegrityCanonical.ToLowerHex(sha.ComputeHash(bytes));
        }

        private static void ValidatePluginGuid(string value)
        {
            if (value == null) throw new InvalidDataException();
            if (value.Length > MaximumGuidBytes) throw new CatalogTooLargeException();
            if (!IntegrityCanonical.TryNormalizeGuid(value, IntegrityLimits.Default,
                    IntegrityDiagnosticCodes.ManifestInvalidGuid, out string canonical, out _) ||
                !string.Equals(value, canonical, StringComparison.Ordinal) ||
                IntegrityAssemblyIdentity.IsLibraryKey(canonical))
                throw new InvalidDataException();
        }

        private static void ValidateText(string value, int maximumBytes)
        {
            if (value == null) throw new InvalidDataException();
            if (value.Length > maximumBytes || StrictUtf8.GetByteCount(value) > maximumBytes)
                throw new CatalogTooLargeException();
            if (string.IsNullOrWhiteSpace(value) || value.Trim() != value) throw new InvalidDataException();
            for (int index = 0; index < value.Length; index++)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(value, index);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator ||
                    category == UnicodeCategory.Surrogate || value[index] == '<' || value[index] == '>')
                    throw new InvalidDataException();
                if (char.IsHighSurrogate(value[index])) index++;
            }
        }

        private static void WriteLength(List<byte> bytes, int length)
        {
            bytes.Add((byte)length);
            bytes.Add((byte)(length >> 8));
        }

        private static void WriteText(List<byte> bytes, string value, int maximumBytes)
        {
            ValidateText(value, maximumBytes);
            byte[] encoded = StrictUtf8.GetBytes(value);
            if (bytes.Count + encoded.Length + 2 > MaximumPayloadBytes) throw new CatalogTooLargeException();
            WriteLength(bytes, encoded.Length);
            bytes.AddRange(encoded);
        }

        private static int ReadByte(byte[] bytes, ref int offset)
        {
            if (offset >= bytes.Length) throw new InvalidDataException();
            return bytes[offset++];
        }

        private static int ReadLength(byte[] bytes, ref int offset) =>
            ReadByte(bytes, ref offset) | (ReadByte(bytes, ref offset) << 8);

        private static string ReadText(byte[] bytes, ref int offset, int maximumBytes)
        {
            int length = ReadLength(bytes, ref offset);
            if (length > maximumBytes) throw new CatalogTooLargeException();
            if (length > bytes.Length - offset) throw new InvalidDataException();
            string text = StrictUtf8.GetString(bytes, offset, length);
            offset += length;
            ValidateText(text, maximumBytes);
            return text;
        }

        private static OptionalModCatalogResult Result(OptionalModCatalogStatus status) => new OptionalModCatalogResult(status);

        private static IReadOnlyDictionary<string, string> Unavailable(OptionalModCatalogStatus status) =>
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HeaderKey] = SchemaVersion + "|" + (status == OptionalModCatalogStatus.TooLarge ? "too_large" :
                    status == OptionalModCatalogStatus.Missing ? "missing" : "invalid") + "|0|0|"
            });

        private sealed class CatalogTooLargeException : Exception { }
    }
}
