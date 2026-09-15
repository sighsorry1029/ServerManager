using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerManager
{
    /// <summary>
    /// Pure policy evaluator for BepInEx plugin GUIDs.
    /// Display names are retained for diagnostics but never grant access; an
    /// allowed file SHA-256 is always required for present required rules.
    /// Optional and unlisted plugins are strict by default, with an explicit
    /// exception option for server-authenticated administrators.
    /// </summary>
    public static class IntegrityValidator
    {
        public static IntegrityValidationResult Validate(
            IntegrityPolicySnapshot? policy,
            IntegrityManifest? manifest)
        {
            return Validate(policy, manifest, false);
        }

        /// <summary>
        /// Allows only optional-hash and unlisted-plugin discrepancies to be
        /// exempted. The caller must authenticate administrator status on the
        /// server; this pure evaluator does not establish identity or access.
        /// Required presence and hashes remain mandatory for every caller.
        /// </summary>
        public static IntegrityValidationResult Validate(
            IntegrityPolicySnapshot? policy,
            IntegrityManifest? manifest,
            bool allowAdminExceptions)
        {
            if (policy == null)
            {
                return new IntegrityValidationResult(
                    false,
                    new[]
                    {
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.PolicyUnavailable,
                            "No validated server integrity policy is active.")
                    });
            }

            if (manifest == null)
            {
                return new IntegrityValidationResult(
                    false,
                    new[]
                    {
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.ManifestUnavailable,
                            "Client manifest is unavailable.")
                    });
            }

            Dictionary<string, IntegrityManifestEntry> entriesByGuid =
                manifest.Entries.ToDictionary(
                    entry => entry.PluginGuid,
                    entry => entry,
                    StringComparer.Ordinal);
            List<IntegrityDiagnostic> diagnostics =
                new List<IntegrityDiagnostic>();
            List<IntegrityDiagnostic> exemptedDiagnostics =
                new List<IntegrityDiagnostic>();

            foreach (IntegrityPolicyRule rule in policy.Rules)
            {
                if (rule.Requirement == IntegrityRequirement.Required &&
                    !entriesByGuid.ContainsKey(rule.PluginGuid))
                {
                    diagnostics.Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.RequiredPluginMissing,
                            "Required plugin '" +
                            rule.DisplayName +
                            "' (" +
                            rule.PluginGuid +
                            ") is not available.",
                            rule.PluginGuid));
                }
            }

            foreach (IntegrityManifestEntry entry in manifest.Entries)
            {
                IntegrityPolicyRule rule;
                if (!policy.TryGetCanonicalRule(entry.PluginGuid, out rule))
                {
                    (allowAdminExceptions ? exemptedDiagnostics : diagnostics).Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.UnlistedPluginPresent,
                            "Plugin '" +
                            entry.Name +
                            "' is not listed by the server policy.",
                            entry.PluginGuid));

                    continue;
                }

                if (!rule.AllowsHash(entry.FileSha256))
                {
                    bool exemptOptionalHash = allowAdminExceptions &&
                        rule.Requirement == IntegrityRequirement.Optional;
                    (exemptOptionalHash ? exemptedDiagnostics : diagnostics).Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.HashNotAllowed,
                            "Plugin '" + rule.DisplayName +
                            "' has SHA-256 " +
                            entry.FileSha256 +
                            ", which is not allowed by the server policy.",
                            entry.PluginGuid));
                }
            }

            return new IntegrityValidationResult(
                diagnostics.Count == 0,
                diagnostics,
                exemptedDiagnostics);
        }

        /// <summary>
        /// Convenience overload that preserves bounded decode diagnostics as the
        /// connection validation result.
        /// </summary>
        public static IntegrityValidationResult Validate(
            IntegrityPolicySnapshot? policy,
            IntegrityManifestDecodeResult? decodedManifest)
        {
            return Validate(policy, decodedManifest, false);
        }

        /// <summary>
        /// Applies administrator exceptions only after a successful bounded
        /// decode. Wire errors and manifest limits are never exempted.
        /// </summary>
        public static IntegrityValidationResult Validate(
            IntegrityPolicySnapshot? policy,
            IntegrityManifestDecodeResult? decodedManifest,
            bool allowAdminExceptions)
        {
            if (decodedManifest == null)
            {
                return Validate(policy, (IntegrityManifest?)null, allowAdminExceptions);
            }

            if (!decodedManifest.Success)
            {
                return new IntegrityValidationResult(
                    false,
                    decodedManifest.Diagnostics);
            }

            return Validate(policy, decodedManifest.Manifest, allowAdminExceptions);
        }
    }
}
