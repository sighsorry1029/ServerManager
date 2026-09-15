using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ServerManager
{
    /// <summary>
    /// Describes how a server policy treats a plugin.
    /// </summary>
    public enum IntegrityRequirement
    {
        Required,
        Optional
    }

    /// <summary>
    /// Stable, machine-readable diagnostic codes. Code that consumes validation
    /// results should branch on these values rather than parsing human messages.
    /// </summary>
    public static class IntegrityDiagnosticCodes
    {
        public const string ManifestUnavailable = "manifest.unavailable";
        public const string ManifestTooManyEntries = "manifest.too_many_entries";
        public const string ManifestInvalidGuid = "manifest.invalid_guid";
        public const string ManifestInvalidName = "manifest.invalid_name";
        public const string ManifestInvalidHash = "manifest.invalid_sha256";
        public const string ManifestDuplicateGuid = "manifest.duplicate_guid";
        public const string ManifestMissingLocation = "manifest.missing_location";
        public const string ManifestFileNotFound = "manifest.file_not_found";
        public const string ManifestHashFailed = "manifest.hash_failed";

        public const string WirePayloadMissing = "wire.payload_missing";
        public const string WirePayloadTooLarge = "wire.payload_too_large";
        public const string WireInvalidMagic = "wire.invalid_magic";
        public const string WireUnsupportedSchema = "wire.unsupported_schema";
        public const string WireMalformed = "wire.malformed";
        public const string WireNonCanonical = "wire.non_canonical";
        public const string WireTrailingData = "wire.trailing_data";

        public const string PolicyUnavailable = "policy.unavailable";
        public const string PolicyTooManyRules = "policy.too_many_rules";
        public const string PolicySourceReadFailed = "policy.source.read_failed";
        public const string PolicySourceInvalidAssembly = "policy.source.invalid_assembly";
        public const string PolicySourceMissingPluginMetadata =
            "policy.source.missing_bepinplugin";
        public const string PolicySourceTooManyFiles = "policy.source.too_many_files";
        public const string PolicySourceTooManyEntries =
            "policy.source.too_many_entries";
        public const string PolicySourceTooManyDirectories =
            "policy.source.too_many_directories";
        public const string PolicySourceReparsePoint = "policy.source.reparse_point";
        public const string PolicySourceRoleConflict = "policy.source.role_conflict";

        public const string RequiredPluginMissing = "validation.required_plugin_missing";
        public const string UnlistedPluginPresent = "validation.unlisted_plugin_present";
        public const string HashNotAllowed = "validation.hash_not_allowed";
    }

    /// <summary>
    /// A stable machine code paired with a message suitable for logs or a
    /// connection rejection UI.
    /// </summary>
    public sealed class IntegrityDiagnostic
    {
        public IntegrityDiagnostic(
            string code,
            string message,
            string pluginGuid = "")
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("A diagnostic code is required.", nameof(code));
            }

            Code = code;
            Message = message ?? string.Empty;
            PluginGuid = pluginGuid ?? string.Empty;
        }

        public string Code { get; }

        public string Message { get; }

        public string PluginGuid { get; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(PluginGuid)
                ? Code + ": " + Message
                : Code + " [" + PluginGuid + "]: " + Message;
        }
    }

    /// <summary>
    /// Hard input limits shared by manifest creation, reference-folder scanning,
    /// and wire decoding. Instances are immutable and validate their own
    /// configuration.
    /// </summary>
    public sealed class IntegrityLimits
    {
        public const int DefaultMaxPayloadBytes = 256 * 1024;
        public const int DefaultMaxPluginCount = 1024;
        public const int DefaultMaxGuidUtf8Bytes = 256;
        public const int DefaultMaxNameUtf8Bytes = 512;

        private const int AbsoluteMaxPayloadBytes = 4 * 1024 * 1024;
        private const int AbsoluteMaxPluginCount = 4096;
        private const int AbsoluteMaxStringUtf8Bytes = 4096;

        public IntegrityLimits(
            int maxPayloadBytes = DefaultMaxPayloadBytes,
            int maxPluginCount = DefaultMaxPluginCount,
            int maxGuidUtf8Bytes = DefaultMaxGuidUtf8Bytes,
            int maxNameUtf8Bytes = DefaultMaxNameUtf8Bytes)
        {
            RequireRange(
                maxPayloadBytes,
                64,
                AbsoluteMaxPayloadBytes,
                nameof(maxPayloadBytes));
            RequireRange(
                maxPluginCount,
                1,
                AbsoluteMaxPluginCount,
                nameof(maxPluginCount));
            RequireRange(
                maxGuidUtf8Bytes,
                1,
                AbsoluteMaxStringUtf8Bytes,
                nameof(maxGuidUtf8Bytes));
            RequireRange(
                maxNameUtf8Bytes,
                1,
                AbsoluteMaxStringUtf8Bytes,
                nameof(maxNameUtf8Bytes));

            MaxPayloadBytes = maxPayloadBytes;
            MaxPluginCount = maxPluginCount;
            MaxGuidUtf8Bytes = maxGuidUtf8Bytes;
            MaxNameUtf8Bytes = maxNameUtf8Bytes;
        }

        public static IntegrityLimits Default { get; } = new IntegrityLimits();

        public int MaxPayloadBytes { get; }

        public int MaxPluginCount { get; }

        public int MaxGuidUtf8Bytes { get; }

        public int MaxNameUtf8Bytes { get; }

        private static void RequireRange(int value, int minimum, int maximum, string parameterName)
        {
            if (value < minimum || value > maximum)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    /// <summary>
    /// One canonical BepInEx plugin record. The local file path deliberately is
    /// not part of this DTO and therefore cannot leak over the network.
    /// </summary>
    public sealed class IntegrityManifestEntry
    {
        public IntegrityManifestEntry(
            string pluginGuid,
            string name,
            string fileSha256)
            : this(
                pluginGuid,
                name,
                fileSha256,
                IntegrityLimits.Default)
        {
        }

        internal IntegrityManifestEntry(
            string pluginGuid,
            string name,
            string fileSha256,
            IntegrityLimits limits)
        {
            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            IntegrityDiagnostic diagnostic;
            string canonicalGuid;
            string canonicalName;
            string canonicalHash;

            if (!IntegrityCanonical.TryNormalizeGuid(
                    pluginGuid,
                    limits,
                    IntegrityDiagnosticCodes.ManifestInvalidGuid,
                    out canonicalGuid,
                    out diagnostic))
            {
                throw new ArgumentException(diagnostic.Message, nameof(pluginGuid));
            }

            if (!IntegrityCanonical.TryNormalizeDisplayString(
                    name,
                    limits.MaxNameUtf8Bytes,
                    "Plugin name",
                    IntegrityDiagnosticCodes.ManifestInvalidName,
                    canonicalGuid,
                    out canonicalName,
                    out diagnostic))
            {
                throw new ArgumentException(diagnostic.Message, nameof(name));
            }

            if (!IntegrityCanonical.TryNormalizeSha256(
                    fileSha256,
                    IntegrityDiagnosticCodes.ManifestInvalidHash,
                    canonicalGuid,
                    out canonicalHash,
                    out diagnostic))
            {
                throw new ArgumentException(diagnostic.Message, nameof(fileSha256));
            }

            PluginGuid = canonicalGuid;
            Name = canonicalName;
            FileSha256 = canonicalHash;
        }

        public string PluginGuid { get; }

        public string Name { get; }

        public string FileSha256 { get; }
    }

    /// <summary>
    /// Canonically ordered manifest. Entries are sorted by normalized BepInEx
    /// GUID, making wire serialization deterministic.
    /// </summary>
    public sealed class IntegrityManifest
    {
        private readonly ReadOnlyCollection<IntegrityManifestEntry> _entries;

        public IntegrityManifest(IEnumerable<IntegrityManifestEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            List<IntegrityManifestEntry> copy = entries.ToList();
            if (copy.Any(entry => entry == null))
            {
                throw new ArgumentException("Manifest entries cannot contain null.", nameof(entries));
            }

            copy.Sort((left, right) =>
                StringComparer.Ordinal.Compare(left.PluginGuid, right.PluginGuid));

            for (int index = 1; index < copy.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(
                        copy[index - 1].PluginGuid,
                        copy[index].PluginGuid))
                {
                    throw new ArgumentException(
                        "The manifest contains duplicate plugin GUID '" +
                        copy[index].PluginGuid +
                        "'.",
                        nameof(entries));
                }
            }

            _entries = new ReadOnlyCollection<IntegrityManifestEntry>(copy);
        }

        public IReadOnlyList<IntegrityManifestEntry> Entries => _entries;
    }

    public sealed class IntegrityManifestBuildResult
    {
        internal IntegrityManifestBuildResult(
            bool success,
            IntegrityManifest? manifest,
            IEnumerable<IntegrityDiagnostic> diagnostics)
        {
            Success = success;
            Manifest = manifest;
            Diagnostics = IntegrityCollections.Freeze(diagnostics);
        }

        public bool Success { get; }

        /// <summary>
        /// Null when Success is false. An incomplete manifest is never returned
        /// as a successful result.
        /// </summary>
        public IntegrityManifest? Manifest { get; }

        public IReadOnlyList<IntegrityDiagnostic> Diagnostics { get; }
    }

    public sealed class IntegrityManifestEncodeResult
    {
        internal IntegrityManifestEncodeResult(
            bool success,
            byte[]? payload,
            IEnumerable<IntegrityDiagnostic> diagnostics)
        {
            Success = success;
            Payload = payload;
            Diagnostics = IntegrityCollections.Freeze(diagnostics);
        }

        public bool Success { get; }

        /// <summary>
        /// Null when Success is false.
        /// </summary>
        public byte[]? Payload { get; }

        public IReadOnlyList<IntegrityDiagnostic> Diagnostics { get; }
    }

    public sealed class IntegrityManifestDecodeResult
    {
        internal IntegrityManifestDecodeResult(
            bool success,
            IntegrityManifest? manifest,
            IEnumerable<IntegrityDiagnostic> diagnostics)
        {
            Success = success;
            Manifest = manifest;
            Diagnostics = IntegrityCollections.Freeze(diagnostics);
        }

        public bool Success { get; }

        /// <summary>
        /// Null when Success is false.
        /// </summary>
        public IntegrityManifest? Manifest { get; }

        public IReadOnlyList<IntegrityDiagnostic> Diagnostics { get; }
    }

    public sealed class IntegrityPolicyRule
    {
        private readonly ReadOnlyCollection<string> _allowedSha256;
        private readonly ReadOnlyCollection<string> _allowedVersions;
        private readonly HashSet<string> _allowedSha256Lookup;

        internal IntegrityPolicyRule(
            string pluginGuid,
            string displayName,
            IntegrityRequirement requirement,
            IEnumerable<string> allowedSha256)
            : this(pluginGuid, displayName, requirement, allowedSha256, null)
        {
        }

        internal IntegrityPolicyRule(
            string pluginGuid,
            string displayName,
            IntegrityRequirement requirement,
            IEnumerable<string> allowedSha256,
            IEnumerable<string>? allowedVersions)
        {
            PluginGuid = pluginGuid;
            DisplayName = displayName;
            Requirement = requirement;

            List<string> hashes = allowedSha256
                .OrderBy(hash => hash, StringComparer.Ordinal)
                .ToList();
            _allowedSha256 = new ReadOnlyCollection<string>(hashes);
            _allowedSha256Lookup = new HashSet<string>(hashes, StringComparer.Ordinal);
            _allowedVersions = new ReadOnlyCollection<string>((allowedVersions ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(version => version, StringComparer.Ordinal)
                .ToList());
        }

        public string PluginGuid { get; }

        /// <summary>
        /// Diagnostic-only label. It is never used as plugin identity.
        /// </summary>
        public string DisplayName { get; }

        public IntegrityRequirement Requirement { get; }

        public IReadOnlyList<string> AllowedSha256 => _allowedSha256;

        // Server-local preview metadata only. Versions are never admission evidence
        // and are deliberately absent from the integrity manifest and wire codec.
        internal IReadOnlyList<string> AllowedVersions => _allowedVersions;

        internal bool AllowsHash(string canonicalSha256)
        {
            return _allowedSha256Lookup.Contains(canonicalSha256);
        }
    }

    /// <summary>
    /// An immutable policy view. The store swaps complete instances atomically,
    /// so readers never observe a partially reloaded policy.
    /// </summary>
    public sealed class IntegrityPolicySnapshot
    {
        private readonly ReadOnlyCollection<IntegrityPolicyRule> _rules;
        private readonly ReadOnlyDictionary<string, IntegrityPolicyRule> _rulesByGuid;

        internal IntegrityPolicySnapshot(
            long generation,
            IEnumerable<IntegrityPolicyRule> rules)
        {
            Generation = generation;

            List<IntegrityPolicyRule> orderedRules = rules
                .OrderBy(rule => rule.PluginGuid, StringComparer.Ordinal)
                .ToList();
            _rules = new ReadOnlyCollection<IntegrityPolicyRule>(orderedRules);
            _rulesByGuid = new ReadOnlyDictionary<string, IntegrityPolicyRule>(
                orderedRules.ToDictionary(
                    rule => rule.PluginGuid,
                    rule => rule,
                    StringComparer.Ordinal));
        }

        public long Generation { get; }

        public IReadOnlyList<IntegrityPolicyRule> Rules => _rules;

        public bool TryGetRule(string? pluginGuid, out IntegrityPolicyRule rule)
        {
            string canonicalGuid;
            IntegrityDiagnostic ignored;
            if (!IntegrityCanonical.TryNormalizeGuid(
                    pluginGuid,
                    IntegrityLimits.Default,
                    IntegrityDiagnosticCodes.ManifestInvalidGuid,
                    out canonicalGuid,
                    out ignored))
            {
                rule = null!;
                return false;
            }

            return _rulesByGuid.TryGetValue(canonicalGuid, out rule);
        }

        internal bool TryGetCanonicalRule(string canonicalGuid, out IntegrityPolicyRule rule)
        {
            return _rulesByGuid.TryGetValue(canonicalGuid, out rule);
        }
    }

    public sealed class IntegrityPolicyReloadResult
    {
        internal IntegrityPolicyReloadResult(
            bool success,
            bool keptPreviousSnapshot,
            IntegrityPolicySnapshot? activeSnapshot,
            IEnumerable<IntegrityDiagnostic> diagnostics)
        {
            Success = success;
            KeptPreviousSnapshot = keptPreviousSnapshot;
            ActiveSnapshot = activeSnapshot;
            Diagnostics = IntegrityCollections.Freeze(diagnostics);
        }

        public bool Success { get; }

        public bool KeptPreviousSnapshot { get; }

        /// <summary>
        /// The snapshot currently active after the reload attempt. This can be
        /// null only when the first load failed.
        /// </summary>
        public IntegrityPolicySnapshot? ActiveSnapshot { get; }

        public IReadOnlyList<IntegrityDiagnostic> Diagnostics { get; }
    }

    public sealed class IntegrityValidationResult
    {
        internal IntegrityValidationResult(
            bool allowed,
            IEnumerable<IntegrityDiagnostic> diagnostics)
            : this(allowed, diagnostics, Array.Empty<IntegrityDiagnostic>())
        {
        }

        internal IntegrityValidationResult(
            bool allowed,
            IEnumerable<IntegrityDiagnostic> diagnostics,
            IEnumerable<IntegrityDiagnostic> exemptedDiagnostics)
        {
            Allowed = allowed;
            Diagnostics = IntegrityCollections.Freeze(diagnostics);
            ExemptedDiagnostics = IntegrityCollections.Freeze(exemptedDiagnostics);
        }

        public bool Allowed { get; }

        /// <summary>
        /// Rejection reasons only. Administrator exceptions are kept separately
        /// so a rejection never instructs an administrator to fix exempt mods.
        /// </summary>
        public IReadOnlyList<IntegrityDiagnostic> Diagnostics { get; }

        /// <summary>
        /// Original discrepancy diagnostics waived by the explicit administrator
        /// policy option. Intended for server-local audit, not client rejection.
        /// Required rules and malformed manifests can never appear here.
        /// </summary>
        public IReadOnlyList<IntegrityDiagnostic> ExemptedDiagnostics { get; }
    }

    internal static class IntegrityCollections
    {
        internal static IReadOnlyList<IntegrityDiagnostic> Freeze(
            IEnumerable<IntegrityDiagnostic>? diagnostics)
        {
            return new ReadOnlyCollection<IntegrityDiagnostic>(
                diagnostics == null
                    ? new List<IntegrityDiagnostic>()
                    : diagnostics.ToList());
        }
    }

    internal static class IntegrityCanonical
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            false,
            true);

        internal static Encoding Utf8 => StrictUtf8;

        internal static bool TryNormalizeGuid(
            string? value,
            IntegrityLimits limits,
            string diagnosticCode,
            out string canonical,
            out IntegrityDiagnostic diagnostic)
        {
            canonical = string.Empty;
            diagnostic = null!;

            if (string.IsNullOrWhiteSpace(value))
            {
                diagnostic = Error(
                    diagnosticCode,
                    "Plugin GUID is empty.",
                    string.Empty);
                return false;
            }

            string trimmed = value!.Trim();
            for (int index = 0; index < trimmed.Length; index++)
            {
                char character = trimmed[index];
                bool allowed =
                    character >= 'a' && character <= 'z' ||
                    character >= 'A' && character <= 'Z' ||
                    character >= '0' && character <= '9' ||
                    character == '.' ||
                    character == '_' ||
                    character == '-';
                if (!allowed)
                {
                    diagnostic = Error(
                        diagnosticCode,
                        "Plugin GUID contains an unsupported character.",
                        string.Empty);
                    return false;
                }
            }

            if (trimmed.Length > limits.MaxGuidUtf8Bytes)
            {
                diagnostic = Error(
                    diagnosticCode,
                    "Plugin GUID exceeds the configured byte limit.",
                    string.Empty);
                return false;
            }

            canonical = trimmed.ToLowerInvariant();
            return true;
        }

        internal static bool TryNormalizeDisplayString(
            string? value,
            int maximumUtf8Bytes,
            string fieldName,
            string diagnosticCode,
            string pluginGuid,
            out string canonical,
            out IntegrityDiagnostic diagnostic)
        {
            canonical = string.Empty;
            diagnostic = null!;

            if (string.IsNullOrWhiteSpace(value))
            {
                diagnostic = Error(
                    diagnosticCode,
                    fieldName + " is empty.",
                    pluginGuid);
                return false;
            }

            string normalized;
            try
            {
                normalized = value!.Trim().Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                diagnostic = Error(
                    diagnosticCode,
                    fieldName + " contains invalid Unicode.",
                    pluginGuid);
                return false;
            }

            for (int index = 0; index < normalized.Length; index++)
            {
                char character = normalized[index];
                UnicodeCategory category = char.GetUnicodeCategory(character);
                if (char.IsControl(character) ||
                    char.IsSurrogate(character) ||
                    category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator ||
                    category == UnicodeCategory.ParagraphSeparator)
                {
                    diagnostic = Error(
                        diagnosticCode,
                        fieldName + " contains an unsafe formatting character.",
                        pluginGuid);
                    return false;
                }
            }

            int encodedLength;
            try
            {
                encodedLength = StrictUtf8.GetByteCount(normalized);
            }
            catch (EncoderFallbackException)
            {
                diagnostic = Error(
                    diagnosticCode,
                    fieldName + " contains invalid Unicode.",
                    pluginGuid);
                return false;
            }

            if (encodedLength > maximumUtf8Bytes)
            {
                diagnostic = Error(
                    diagnosticCode,
                    fieldName + " exceeds the configured byte limit.",
                    pluginGuid);
                return false;
            }

            canonical = normalized;
            return true;
        }

        internal static bool TryNormalizeOptionalDisplayString(
            string? value,
            int maximumUtf8Bytes,
            string fallback,
            string diagnosticCode,
            string pluginGuid,
            out string canonical,
            out IntegrityDiagnostic diagnostic)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                canonical = fallback;
                diagnostic = null!;
                return true;
            }

            return TryNormalizeDisplayString(
                value,
                maximumUtf8Bytes,
                "Policy plugin name",
                diagnosticCode,
                pluginGuid,
                out canonical,
                out diagnostic);
        }

        internal static bool TryNormalizeSha256(
            string? value,
            string diagnosticCode,
            string pluginGuid,
            out string canonical,
            out IntegrityDiagnostic diagnostic)
        {
            canonical = string.Empty;
            diagnostic = null!;

            if (value == null || value.Length != 64)
            {
                diagnostic = Error(
                    diagnosticCode,
                    "SHA-256 must contain exactly 64 hexadecimal characters.",
                    pluginGuid);
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool isHex =
                    character >= '0' && character <= '9' ||
                    character >= 'a' && character <= 'f' ||
                    character >= 'A' && character <= 'F';
                if (!isHex)
                {
                    diagnostic = Error(
                        diagnosticCode,
                        "SHA-256 contains a non-hexadecimal character.",
                        pluginGuid);
                    return false;
                }
            }

            canonical = value.ToLowerInvariant();
            return true;
        }

        internal static string ToLowerHex(byte[] bytes)
        {
            char[] characters = new char[bytes.Length * 2];
            const string alphabet = "0123456789abcdef";
            for (int index = 0; index < bytes.Length; index++)
            {
                characters[index * 2] = alphabet[bytes[index] >> 4];
                characters[index * 2 + 1] = alphabet[bytes[index] & 0x0f];
            }

            return new string(characters);
        }

        internal static byte[] HexToBytes(string canonicalHex)
        {
            byte[] bytes = new byte[canonicalHex.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)(
                    HexValue(canonicalHex[index * 2]) * 16 +
                    HexValue(canonicalHex[index * 2 + 1]));
            }

            return bytes;
        }

        internal static bool IsFatal(Exception exception)
        {
            return exception is OutOfMemoryException ||
                   exception is StackOverflowException ||
                   exception is AccessViolationException ||
                   exception is AppDomainUnloadedException;
        }

        internal static IntegrityDiagnostic Error(
            string code,
            string message,
            string pluginGuid = "")
        {
            return new IntegrityDiagnostic(
                code,
                message,
                pluginGuid);
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9')
            {
                return value - '0';
            }

            return value - 'a' + 10;
        }
    }
}
