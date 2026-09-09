using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ServerManager
{
    /// <summary>
    /// Publishes an immutable strict policy built only from the required and
    /// optional reference-DLL folders. Reload is transactional: a complete scan
    /// must validate before it replaces the last-known-good snapshot.
    /// </summary>
    public sealed class IntegrityPolicyStore
    {
        private readonly object _reloadGate = new object();
        private readonly IntegrityLimits _limits;
        private readonly ReferencePluginPolicyScanner _referenceScanner;
        private readonly IntegrityManifestEntry? _selfEntry;
        private IntegrityPolicySnapshot? _current;
        private long _generation;

        public IntegrityPolicyStore(string sourceRoot)
            : this(sourceRoot, IntegrityLimits.Default)
        {
        }

        public IntegrityPolicyStore(
            string sourceRoot,
            IntegrityLimits limits)
            : this(sourceRoot, limits, null, null)
        {
        }

        internal IntegrityPolicyStore(
            string sourceRoot,
            IntegrityLimits limits,
            ReferencePluginPolicyScanner? referenceScanner,
            IntegrityManifestEntry? selfEntry)
        {
            if (string.IsNullOrWhiteSpace(sourceRoot))
            {
                throw new ArgumentException(
                    "A policy source root is required.",
                    nameof(sourceRoot));
            }

            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
            SourceRoot = Path.GetFullPath(sourceRoot);
            _referenceScanner = referenceScanner ??
                                new ReferencePluginPolicyScanner(SourceRoot, _limits);
            _selfEntry = selfEntry;
            _referenceScanner.EnsureDirectories();
        }

        public string SourceRoot { get; }

        /// <summary>
        /// Returns the current immutable snapshot or null before the first
        /// successful folder scan.
        /// </summary>
        public IntegrityPolicySnapshot? Current =>
            Volatile.Read(ref _current);

        public IntegrityPolicyReloadResult TryReload()
        {
            lock (_reloadGate)
            {
                return PublishReload(PrepareReload(CancellationToken.None));
            }
        }

        // A worker result owns no active-policy state. Only the owning service
        // may publish it after checking that its folder-change request is current.
        internal sealed class ReloadCandidate
        {
            internal readonly IReadOnlyList<IntegrityPolicyRule> Rules;
            internal readonly IReadOnlyList<IntegrityDiagnostic> Diagnostics;

            internal ReloadCandidate(IEnumerable<IntegrityPolicyRule> rules,
                IEnumerable<IntegrityDiagnostic> diagnostics)
            {
                Rules = Array.AsReadOnly(rules.ToArray());
                Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
            }
        }

        internal ReloadCandidate PrepareReload(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReferencePluginPolicyScanResult sourceScan = _referenceScanner.Scan(cancellationToken);
            List<IntegrityDiagnostic> diagnostics = sourceScan.Diagnostics.ToList();
            List<IntegrityPolicyRule> rules = diagnostics.Count == 0
                ? BuildEffectiveRules(sourceScan.Records, diagnostics)
                : new List<IntegrityPolicyRule>();
            cancellationToken.ThrowIfCancellationRequested();
            if (rules.Count(rule => IntegrityAssemblyIdentity.IsLibraryKey(rule.PluginGuid)) >
                IntegrityAssemblyIdentity.MaximumLibraryCount)
                diagnostics.Add(IntegrityCanonical.Error(IntegrityDiagnosticCodes.PolicyTooManyRules,
                    "The reference folders contain more than 128 distinct managed-library names."));
            return new ReloadCandidate(rules, diagnostics);
        }

        internal IntegrityPolicyReloadResult PublishReload(ReloadCandidate prepared)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            lock (_reloadGate)
            {
                if (prepared.Diagnostics.Count != 0)
                {
                    return FailedReload(Current, prepared.Diagnostics);
                }

                long generation = Interlocked.Increment(ref _generation);
                IntegrityPolicySnapshot candidate =
                    new IntegrityPolicySnapshot(
                        generation,
                        prepared.Rules);

                Interlocked.Exchange(ref _current, candidate);
                return new IntegrityPolicyReloadResult(
                    true,
                    false,
                    candidate,
                    prepared.Diagnostics);
            }
        }

        private List<IntegrityPolicyRule> BuildEffectiveRules(
            IEnumerable<ReferencePluginPolicyRecord> sourceRecords,
            ICollection<IntegrityDiagnostic> diagnostics)
        {
            Dictionary<string, IntegrityPolicyRule> effective =
                new Dictionary<string, IntegrityPolicyRule>(
                    StringComparer.Ordinal);
            string? selfGuid = _selfEntry?.PluginGuid;

            foreach (IGrouping<string, ReferencePluginPolicyRecord> group in
                     sourceRecords.GroupBy(
                         item => item.Entry.PluginGuid,
                         StringComparer.Ordinal))
            {
                IntegrityRequirement[] requirements = group
                    .Select(item => item.Requirement)
                    .Distinct()
                    .ToArray();
                if (requirements.Length != 1)
                {
                    diagnostics.Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.PolicySourceRoleConflict,
                            "Plugin GUID '" + group.Key +
                            "' appears in both required and optional reference folders.",
                            group.Key));
                    continue;
                }

                IntegrityRequirement requirement = requirements[0];
                if (selfGuid != null &&
                    StringComparer.Ordinal.Equals(group.Key, selfGuid))
                {
                    if (requirement != IntegrityRequirement.Required)
                    {
                        diagnostics.Add(
                            IntegrityCanonical.Error(
                                IntegrityDiagnosticCodes.PolicySourceRoleConflict,
                                "ServerManager reference DLLs may only be placed " +
                                "under ServerManager/required.",
                                group.Key));
                    }

                    continue;
                }

                string displayName = group
                    .Select(item => item.Entry.Name)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .First();
                string[] hashes = group
                    .Select(item => item.Entry.FileSha256)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                effective.Add(
                    group.Key,
                    new IntegrityPolicyRule(
                        group.Key,
                        displayName,
                        requirement,
                        hashes,
                        group.Select(item => item.Version)));
            }

            if (_selfEntry != null)
            {
                HashSet<string> selfHashes =
                    new HashSet<string>(StringComparer.Ordinal)
                    {
                        _selfEntry.FileSha256
                    };
                foreach (ReferencePluginPolicyRecord record in sourceRecords)
                {
                    if (record.Requirement == IntegrityRequirement.Required &&
                        StringComparer.Ordinal.Equals(
                            record.Entry.PluginGuid,
                            _selfEntry.PluginGuid))
                    {
                        selfHashes.Add(record.Entry.FileSha256);
                    }
                }

                effective[_selfEntry.PluginGuid] =
                    new IntegrityPolicyRule(
                        _selfEntry.PluginGuid,
                        _selfEntry.Name,
                        IntegrityRequirement.Required,
                        selfHashes,
                        sourceRecords.Where(record => record.Entry.PluginGuid == _selfEntry.PluginGuid)
                            .Select(record => record.Version));
            }

            if (effective.Count > _limits.MaxPluginCount)
            {
                diagnostics.Add(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.PolicyTooManyRules,
                        "Effective folder policy rule count " +
                        effective.Count +
                        " exceeds the configured limit of " +
                        _limits.MaxPluginCount +
                        "."));
            }

            return effective.Values
                .OrderBy(item => item.PluginGuid, StringComparer.Ordinal)
                .ToList();
        }

        private static IntegrityPolicyReloadResult FailedReload(
            IntegrityPolicySnapshot? previous,
            IEnumerable<IntegrityDiagnostic> diagnostics)
        {
            return new IntegrityPolicyReloadResult(
                false,
                previous != null,
                previous,
                diagnostics);
        }
    }
}
