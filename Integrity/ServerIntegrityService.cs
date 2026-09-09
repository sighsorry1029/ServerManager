using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace ServerManager;

internal sealed class ServerIntegrityService : IManifestValidator, IDisposable
{
    private const int MaximumAutomaticReloadRetries = 3;
    // The general protocol default accepts 512 UTF-8 bytes. Keep a little room
    // below that limit even though ServerManager's live runtime currently allows
    // a larger rejection packet.
    private const int MaximumClientRejectionUtf8Bytes = 480;
    private const int MaximumClientPluginNameUtf8Bytes = 48;
    private const int MaximumClientNamesPerAction = 2;
    internal const NotifyFilters PolicyWatcherNotifyFilters =
        NotifyFilters.FileName |
        NotifyFilters.DirectoryName |
        NotifyFilters.LastWrite |
        NotifyFilters.Size |
        NotifyFilters.CreationTime;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private static readonly StringComparison PathComparison =
        Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    private static readonly long ReloadQuietPeriodTicks =
        Math.Max(1L, Stopwatch.Frequency / 2L);
    private readonly IntegrityLimits _limits;
    private readonly IntegrityPolicyStore _policyStore;
    private readonly ManualLogSource _log;
    private readonly string _dataRoot;
    private FileSystemWatcher? _requiredWatcher;
    private FileSystemWatcher? _optionalWatcher;
    private FileSystemWatcher? _rootWatcher;
    private readonly string _requiredSourceRoot;
    private readonly string _optionalSourceRoot;
    private readonly object _reloadGate = new();
    private long _reloadDueTimestamp;
    private int _reloadRetryCount;
    private bool _reloadPending;
    private Task<IntegrityPolicyStore.ReloadCandidate>? _reloadTask;
    private CancellationTokenSource? _reloadCancellation;
    private long _reloadRequestVersion;
    private long _runningReloadVersion;
    private volatile bool _disposed;

    internal ServerIntegrityService(
        string dataRoot,
        IntegrityLimits limits,
        ManualLogSource log)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException(
                "A ServerManager data root is required.",
                nameof(dataRoot));
        }

        string fullRoot = Path.GetFullPath(dataRoot);
        _dataRoot = fullRoot;
        Directory.CreateDirectory(fullRoot);
        ReferencePluginPolicyScanner referenceScanner =
            new(fullRoot, _limits);
        _requiredSourceRoot = referenceScanner.RequiredRoot;
        _optionalSourceRoot = referenceScanner.OptionalRoot;
        IntegrityManifestEntry ownEntry = BuildOwnManifestEntry();
        _policyStore = new IntegrityPolicyStore(
            fullRoot,
            _limits,
            referenceScanner,
            ownEntry);
        // Keep high-volume character/log writes outside the native watcher
        // scope. The lightweight root sentinel only observes replacement of
        // required/optional themselves so their recursive watchers can be
        // rearmed after an administrator swaps a whole folder.
        try
        {
            _rootWatcher = CreatePolicyRootWatcher(fullRoot);
            _requiredWatcher = CreatePolicySourceWatcher(_requiredSourceRoot);
            _optionalWatcher = CreatePolicySourceWatcher(_optionalSourceRoot);
        }
        catch
        {
            DisposePolicyWatcher(_requiredWatcher, rootWatcher: false);
            DisposePolicyWatcher(_optionalWatcher, rootWatcher: false);
            DisposePolicyWatcher(_rootWatcher, rootWatcher: true);
            _requiredWatcher = null;
            _optionalWatcher = null;
            _rootWatcher = null;
            throw;
        }

        // Watch before the first scan. Until the worker publishes a valid policy,
        // admission remains closed; no empty/default allow policy is substituted.
        Reload();
        _log.LogInfo("Preparing the initial mod folder policy in the background; connections remain refused until it is ready.");
    }

    private FileSystemWatcher CreatePolicySourceWatcher(string sourceRoot)
    {
        FileSystemWatcher watcher = new(sourceRoot)
        {
            Filter = "*",
            IncludeSubdirectories = true,
            NotifyFilter = PolicyWatcherNotifyFilters,
            SynchronizingObject = ThreadingHelper.SynchronizingObject
        };

        try
        {
            watcher.Changed += OnPolicyChanged;
            watcher.Created += OnPolicyChanged;
            watcher.Deleted += OnPolicyChanged;
            watcher.Renamed += OnPolicyRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            DisposePolicyWatcher(watcher, rootWatcher: false);
            throw;
        }
    }

    private FileSystemWatcher CreatePolicyRootWatcher(string dataRoot)
    {
        FileSystemWatcher watcher = new(dataRoot)
        {
            Filter = "*",
            IncludeSubdirectories = false,
            NotifyFilter =
                PolicyWatcherNotifyFilters & NotifyFilters.DirectoryName,
            SynchronizingObject = ThreadingHelper.SynchronizingObject
        };

        try
        {
            watcher.Created += OnPolicyRootChanged;
            watcher.Deleted += OnPolicyRootChanged;
            watcher.Renamed += OnPolicyRootRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            DisposePolicyWatcher(watcher, rootWatcher: true);
            throw;
        }
    }

    public ManifestValidationDecision Validate(
        ServerPeerIdentity peerIdentity,
        byte[] manifestPayload)
    {
        return ValidateForAdmission(peerIdentity, manifestPayload, false, out _);
    }

    // The public listing and admission consume this same last-known-good object.
    // Failed/retrying reload candidates are never exposed as an empty catalog.
    internal IntegrityPolicySnapshot? CurrentPolicy => _policyStore.Current;

    internal IntegrityPolicySnapshot? CaptureLibraryPolicy()
    {
        IntegrityPolicySnapshot? current = _policyStore.Current;
        return current == null ? null : new IntegrityPolicySnapshot(current.Generation,
            current.Rules.Where(rule => IntegrityAssemblyIdentity.IsLibraryKey(rule.PluginGuid)));
    }

    internal ManifestValidationDecision ValidateLibraryUpdate(ServerPeerIdentity identity,
        IntegrityPolicySnapshot policy, byte[] payload, bool authenticatedAdmin,
        out IReadOnlyList<IntegrityDiagnostic> exemptions)
    {
        exemptions = Array.Empty<IntegrityDiagnostic>();
        var limits = new IntegrityLimits(maxPayloadBytes: _limits.MaxPayloadBytes,
            maxPluginCount: IntegrityAssemblyIdentity.MaximumLibraryCount);
        IntegrityManifestDecodeResult decoded = IntegrityManifestCodec.TryDecode(payload, limits);
        if (decoded.Success && decoded.Manifest!.Entries.Any(entry =>
                !IntegrityAssemblyIdentity.IsLibraryKey(entry.PluginGuid) ||
                !policy.TryGetRule(entry.PluginGuid, out _)))
            return ManifestValidationDecision.Reject("The library update contains an unrequested identity.");
        IntegrityValidationResult validation = IntegrityValidator.Validate(policy, decoded, authenticatedAdmin);
        if (validation.Allowed) exemptions = validation.ExemptedDiagnostics;
        return CreateValidationDecision(identity, policy, decoded.Manifest, validation);
    }

    // A preliminary admin lookup may defer only optional/unlisted mismatches.
    // This is permission to begin vanilla authentication, NOT an admin grant.
    internal ManifestValidationDecision ValidateForAdmission(
        ServerPeerIdentity peerIdentity,
        byte[] manifestPayload,
        bool adminCandidate,
        out IntegrityManifest? pendingAdminManifest)
    {
        pendingAdminManifest = null;
        if (_disposed)
        {
            return ManifestValidationDecision.Reject(
                "The server integrity service is shutting down.",
                ProtocolRejectCode.InternalError);
        }

        IntegrityPolicySnapshot? snapshot = _policyStore.Current;
        if (snapshot == null)
        {
            return ManifestValidationDecision.Reject(
                _reloadPending || _reloadTask != null
                    ? "The server is preparing its mod folder policy. Please try connecting again shortly."
                    : "The server has no valid mod folder policy.",
                ProtocolRejectCode.ManifestValidatorFailed);
        }

        IntegrityManifestDecodeResult decoded =
            IntegrityManifestCodec.TryDecode(manifestPayload, _limits);
        IntegrityValidationResult validation =
            IntegrityValidator.Validate(snapshot, decoded);

        if (!validation.Allowed && adminCandidate && decoded.Success &&
            IntegrityValidator.Validate(snapshot, decoded.Manifest, true).Allowed)
        {
            pendingAdminManifest = decoded.Manifest;
            return ManifestValidationDecision.Accept();
        }

        return CreateValidationDecision(peerIdentity, snapshot, decoded.Manifest, validation);
    }

    // The runtime calls this only after final Steam authentication, before
    // opening a character or releasing any world data. Re-evaluate the current
    // policy so a folder reload cannot leave a stale required-mod exemption.
    internal ManifestValidationDecision ConfirmAdminManifest(
        ServerPeerIdentity peerIdentity,
        IntegrityManifest manifest,
        bool authenticatedAdmin,
        out IReadOnlyList<IntegrityDiagnostic> exemptions)
    {
        exemptions = Array.Empty<IntegrityDiagnostic>();
        IntegrityPolicySnapshot? snapshot = _policyStore.Current;
        if (_disposed || snapshot == null)
        {
            return ManifestValidationDecision.Reject(
                "The server has no valid mod folder policy.",
                ProtocolRejectCode.ManifestValidatorFailed);
        }

        IntegrityValidationResult validation =
            IntegrityValidator.Validate(snapshot, manifest, authenticatedAdmin);
        if (validation.Allowed)
        {
            exemptions = validation.ExemptedDiagnostics;
        }

        return CreateValidationDecision(peerIdentity, snapshot, manifest, validation);
    }

    private ManifestValidationDecision CreateValidationDecision(
        ServerPeerIdentity peerIdentity,
        IntegrityPolicySnapshot snapshot,
        IntegrityManifest? decodedManifest,
        IntegrityValidationResult validation)
    {

        List<IntegrityDiagnostic> diagnostics =
            validation.Diagnostics.ToList();

        if (validation.Allowed && diagnostics.Count == 0)
        {
            _log.LogInfo(
                $"Accepted plugin manifest for {SafePeerLabel(peerIdentity)} " +
                $"using policy generation {snapshot.Generation}.");
            return ManifestValidationDecision.Accept();
        }

        foreach (IntegrityDiagnostic diagnostic in diagnostics.Take(20))
        {
            _log.LogWarning(
                $"Rejected manifest from {SafePeerLabel(peerIdentity)}: {diagnostic}");
        }

        string clientMessage = BuildClientRejection(snapshot, decodedManifest, diagnostics, out string[] actions);
        return ManifestValidationDecision.Reject(clientMessage)
            .WithPlayerMessage("sm_mod_mismatch", actions)
            .WithConnectionAudit("mod_policy",
                diagnostics.FirstOrDefault()?.Code ?? "manifest_rejected",
                BuildRejectionAuditDetail(snapshot, decodedManifest, diagnostics),
                BuildRejectionPluginSummary(snapshot, decodedManifest, diagnostics));
    }

    // These summaries are constructed from the validated DTOs and fixed labels,
    // never diagnostic.Message (which may contain arbitrary decode text/paths).
    // Hashes are local-audit-only; the independent plugin summary has no hashes.
    internal static string BuildRejectionAuditDetail(IntegrityPolicySnapshot snapshot,
        IntegrityManifest? manifest, IEnumerable<IntegrityDiagnostic> diagnostics) =>
        BuildRejectionSummary(snapshot, manifest, diagnostics, includeHashes: true, maximumLength: 4096);

    internal static string BuildRejectionPluginSummary(IntegrityPolicySnapshot snapshot,
        IntegrityManifest? manifest, IEnumerable<IntegrityDiagnostic> diagnostics) =>
        BuildRejectionSummary(snapshot, manifest, diagnostics, includeHashes: false, maximumLength: 1024);

    private static string BuildRejectionSummary(IntegrityPolicySnapshot snapshot,
        IntegrityManifest? manifest, IEnumerable<IntegrityDiagnostic> diagnostics,
        bool includeHashes, int maximumLength)
    {
        List<IntegrityDiagnostic> entries = diagnostics.Where(item => item != null).ToList();
        Dictionary<string, IntegrityManifestEntry> reported = manifest?.Entries.ToDictionary(
            item => item.PluginGuid, StringComparer.Ordinal) ?? new Dictionary<string, IntegrityManifestEntry>(StringComparer.Ordinal);
        StringBuilder text = new();
        int shown = 0;
        foreach (IntegrityDiagnostic diagnostic in entries.Take(5))
        {
            string guid = diagnostic.PluginGuid ?? "";
            snapshot.TryGetRule(guid, out IntegrityPolicyRule rule);
            reported.TryGetValue(guid, out IntegrityManifestEntry entry);
            string classification = diagnostic.Code == IntegrityDiagnosticCodes.RequiredPluginMissing ? "missing" :
                diagnostic.Code == IntegrityDiagnosticCodes.UnlistedPluginPresent ? "unlisted" :
                diagnostic.Code == IntegrityDiagnosticCodes.HashNotAllowed ? "hash_not_allowed" :
                IsSafeDiagnosticCode(diagnostic.Code) ? diagnostic.Code : "manifest_invalid";
            string line = "name=" + MakeClientSafePluginLabel(rule?.DisplayName ?? entry?.Name) +
                "; guid=" + (rule?.PluginGuid ?? entry?.PluginGuid ?? "unavailable") + "; mismatch=" + classification;
            if (includeHashes)
            {
                line += "; expected_sha256=" + (rule == null ? "not_listed" : string.Join(",", rule.AllowedSha256.Take(2))) +
                    "; expected_sha256_omitted=" + Math.Max(0, (rule?.AllowedSha256.Count ?? 0) - 2).ToString(CultureInfo.InvariantCulture) +
                    "; reported_sha256=" + (entry?.FileSha256 ?? "not_reported");
            }
            // Reserve a fixed suffix budget so omitted counts are never lost.
            if (text.Length + line.Length + 1 > maximumLength - 80) break;
            if (shown != 0) text.Append('\n');
            text.Append(line);
            ++shown;
        }
        if (shown != 0) text.Append('\n');
        text.Append("mismatches_shown=").Append(shown).Append("; mismatches_omitted=").Append(entries.Count - shown);
        return text.ToString();
    }

    /// <summary>
    /// Builds a bounded, client-actionable explanation from stable diagnostic
    /// codes. Diagnostic messages are deliberately not copied because they can
    /// contain hashes or other server/log-only details.
    /// </summary>
    internal static string BuildClientRejectionMessage(
        IntegrityPolicySnapshot snapshot,
        IntegrityManifest? manifest,
        IEnumerable<IntegrityDiagnostic> diagnostics)
        => BuildClientRejection(snapshot, manifest, diagnostics, out _);

    private static string BuildClientRejection(
        IntegrityPolicySnapshot snapshot,
        IntegrityManifest? manifest,
        IEnumerable<IntegrityDiagnostic> diagnostics,
        out string[] actions)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        List<IntegrityDiagnostic> diagnosticList = diagnostics?
            .Where(item => item != null)
            .ToList() ?? new List<IntegrityDiagnostic>();
        Dictionary<string, IntegrityManifestEntry> manifestEntries =
            manifest?.Entries.ToDictionary(
                item => item.PluginGuid,
                item => item,
                StringComparer.Ordinal) ??
            new Dictionary<string, IntegrityManifestEntry>(StringComparer.Ordinal);

        List<string> install = new();
        List<string> remove = new();
        List<string> update = new();
        HashSet<string> installGuids = new(StringComparer.Ordinal);
        HashSet<string> removeGuids = new(StringComparer.Ordinal);
        HashSet<string> updateGuids = new(StringComparer.Ordinal);

        foreach (IntegrityDiagnostic diagnostic in diagnosticList)
        {
            switch (diagnostic.Code)
            {
                case IntegrityDiagnosticCodes.RequiredPluginMissing:
                    AddClientPluginLabel(
                        install,
                        installGuids,
                        diagnostic.PluginGuid,
                        snapshot,
                        manifestEntries,
                        preferPolicyName: true);
                    break;

                case IntegrityDiagnosticCodes.UnlistedPluginPresent:
                    AddClientPluginLabel(
                        remove,
                        removeGuids,
                        diagnostic.PluginGuid,
                        snapshot,
                        manifestEntries,
                        preferPolicyName: false);
                    break;

                case IntegrityDiagnosticCodes.HashNotAllowed:
                    AddClientPluginLabel(
                        update,
                        updateGuids,
                        diagnostic.PluginGuid,
                        snapshot,
                        manifestEntries,
                        preferPolicyName: true);
                    break;
            }
        }

        StringBuilder message = new("Server mod check failed.");
        actions = new[] { ClientActionNames(install), ClientActionNames(remove), ClientActionNames(update) };
        AppendClientAction(message, "Install required", install);
        AppendClientAction(message, "Remove not allowed", remove);
        AppendClientAction(message, "Update or reinstall", update);

        if (install.Count != 0 || remove.Count != 0 || update.Count != 0)
        {
            message.Append("\nRestart Valheim after changing mods, then try again.");
        }
        else
        {
            string safeCodes = string.Join(
                ", ",
                diagnosticList
                    .Select(item => item.Code)
                    .Where(IsSafeDiagnosticCode)
                    .Distinct(StringComparer.Ordinal)
                    .Take(4));
            if (string.IsNullOrEmpty(safeCodes))
            {
                safeCodes = IntegrityDiagnosticCodes.ManifestUnavailable;
            }

            message.Append(
                "\nThe loaded-mod report could not be verified. Restart Valheim and " +
                "update or reinstall ServerManager. If this continues, contact the " +
                "server administrator.\nReference: ");
            message.Append(safeCodes);
            message.Append('.');
        }

        return TruncateUtf8(message.ToString(), MaximumClientRejectionUtf8Bytes);
    }

    private static void AddClientPluginLabel(
        ICollection<string> destination,
        ISet<string> seenGuids,
        string pluginGuid,
        IntegrityPolicySnapshot snapshot,
        IReadOnlyDictionary<string, IntegrityManifestEntry> manifestEntries,
        bool preferPolicyName)
    {
        string canonicalGuid = pluginGuid ?? string.Empty;
        if (!seenGuids.Add(canonicalGuid))
        {
            return;
        }

        IntegrityPolicyRule policyRule;
        IntegrityManifestEntry manifestEntry;
        string? label = null;
        if (preferPolicyName &&
            snapshot.TryGetRule(canonicalGuid, out policyRule))
        {
            label = policyRule.DisplayName;
        }
        else if (manifestEntries.TryGetValue(canonicalGuid, out manifestEntry))
        {
            label = manifestEntry.Name;
        }
        else if (snapshot.TryGetRule(canonicalGuid, out policyRule))
        {
            label = policyRule.DisplayName;
        }

        destination.Add(MakeClientSafePluginLabel(label));
    }

    private static string MakeClientSafePluginLabel(string? value)
    {
        string source = string.IsNullOrWhiteSpace(value)
            ? "unknown plugin"
            : value!;
        StringBuilder safe = new(source.Length);
        bool previousWasSpace = false;
        foreach (char character in source)
        {
            UnicodeCategory category = char.GetUnicodeCategory(character);
            bool allowedLetterOrNumber =
                char.IsLetterOrDigit(character) ||
                category == UnicodeCategory.NonSpacingMark ||
                category == UnicodeCategory.SpacingCombiningMark ||
                category == UnicodeCategory.EnclosingMark;
            bool allowedPunctuation =
                character == '.' || character == '_' || character == '-' ||
                character == '+' || character == '\'' || character == '(' ||
                character == ')' || character == '[' || character == ']' ||
                character == '#';

            if (allowedLetterOrNumber || allowedPunctuation)
            {
                safe.Append(character);
                previousWasSpace = false;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace && safe.Length != 0)
                {
                    safe.Append(' ');
                    previousWasSpace = true;
                }
            }
            else
            {
                safe.Append('_');
                previousWasSpace = false;
            }
        }

        string result = safe.ToString().Trim();
        if (string.IsNullOrEmpty(result))
        {
            result = "unknown plugin";
        }

        return TruncateUtf8(result, MaximumClientPluginNameUtf8Bytes);
    }

    private static void AppendClientAction(
        StringBuilder message,
        string heading,
        IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return;
        }

        int shown = Math.Min(names.Count, MaximumClientNamesPerAction);
        message.Append('\n');
        message.Append(heading);
        message.Append(": ");
        message.Append(string.Join(", ", names.Take(shown)));
        if (names.Count > shown)
        {
            message.Append(" (+");
            message.Append(names.Count - shown);
            message.Append(" more)");
        }

        message.Append('.');
    }

    private static string ClientActionNames(IReadOnlyList<string> names)
    {
        int shown = Math.Min(names.Count, MaximumClientNamesPerAction);
        string text = string.Join(", ", names.Take(shown));
        // Only names/counts travel as arguments. The client owns translated headings.
        if (names.Count > shown) text += " (+" + (names.Count - shown).ToString(CultureInfo.InvariantCulture) + ")";
        return TruncateUtf8(text, 256);
    }

    private static bool IsSafeDiagnosticCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        string safeCode = code!;
        if (safeCode.Length > 96)
        {
            return false;
        }

        foreach (char character in safeCode)
        {
            if (!(character >= 'a' && character <= 'z') &&
                !(character >= '0' && character <= '9') &&
                character != '.' && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (Utf8NoBom.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        const string suffix = "...";
        int budget = maximumBytes - Utf8NoBom.GetByteCount(suffix);
        char[] characters = value.ToCharArray();
        int length = 0;
        int bytes = 0;
        while (length < characters.Length)
        {
            // Every dynamic label has already had surrogate/control characters
            // replaced, so a one-char UTF-8 count cannot split a scalar value.
            int characterBytes = Utf8NoBom.GetByteCount(characters, length, 1);
            if (bytes + characterBytes > budget)
            {
                break;
            }

            bytes += characterBytes;
            length++;
        }

        return value.Substring(0, length) + suffix;
    }

    internal void Reload()
    {
        lock (_reloadGate)
        {
            if (_disposed)
            {
                return;
            }

            ScheduleReloadLocked(
                Stopwatch.GetTimestamp(),
                resetRetryCount: true);
        }
    }

    /// <summary>
    /// Starts one worker or publishes its completed result on the Unity thread.
    /// File watcher callbacks only invalidate/schedule; no DLL IO occurs here.
    /// </summary>
    internal void Tick()
    {
        lock (_reloadGate)
        {
            if (_disposed)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (_reloadTask != null)
            {
                if (!_reloadTask.IsCompleted) return;
                Task<IntegrityPolicyStore.ReloadCandidate> completed = _reloadTask;
                _reloadTask = null;
                _reloadCancellation!.Dispose();
                _reloadCancellation = null;
                bool current = _runningReloadVersion == _reloadRequestVersion;
                try
                {
                    IntegrityPolicyStore.ReloadCandidate candidate = completed.GetAwaiter().GetResult();
                    if (current)
                    {
                        bool initialLoad = _policyStore.Current == null;
                        IntegrityPolicyReloadResult result = _policyStore.PublishReload(candidate);
                        LogReload(result, initialLoad);
                        if (result.Success) _reloadRetryCount = 0;
                        else ScheduleFailedReloadRetryLocked(now);
                    }
                }
                catch (OperationCanceledException) when (!current) { }
                catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                {
                    // Observe even an obsolete worker's failure, but do not let
                    // it consume the newer request's retry budget or log outcome.
                    if (current)
                    {
                        _log.LogError("Unexpected mod folder policy reload failure: " +
                            exception.GetType().Name + ": " + exception.Message);
                        ScheduleFailedReloadRetryLocked(now);
                    }
                }
            }
            if (!_reloadPending || now < _reloadDueTimestamp) return;
            _reloadPending = false;

            if (!TryEnsurePolicyWatchersLocked())
            {
                ScheduleFailedReloadRetryLocked(now);
                return;
            }

            _reloadCancellation = new CancellationTokenSource();
            CancellationToken token = _reloadCancellation.Token;
            _runningReloadVersion = _reloadRequestVersion;
            _reloadTask = Task.Run(() => _policyStore.PrepareReload(token));
        }
    }

    private IntegrityManifestEntry BuildOwnManifestEntry()
    {
        if (!Chainloader.PluginInfos.TryGetValue(
                ServerManagerPlugin.ModGuid,
                out PluginInfo ownPlugin))
        {
            throw new InvalidOperationException(
                "The running ServerManager plugin was not present in Chainloader.PluginInfos.");
        }

        IntegrityManifestBuildResult build =
            PluginManifestScanner.Build(new[] { ownPlugin }, _limits);
        IntegrityManifest? manifest = build.Manifest;
        if (!build.Success || manifest == null)
        {
            throw new InvalidOperationException(
                "Could not build the local ServerManager manifest entry: " +
                string.Join("; ", build.Diagnostics.Select(item => item.Code)));
        }

        IntegrityManifestEntry? ownEntry = manifest.Entries.SingleOrDefault();
        if (ownEntry == null)
        {
            throw new InvalidOperationException(
                "The running ServerManager plugin was not present in Chainloader.PluginInfos.");
        }

        return ownEntry;
    }

    private void LogReload(IntegrityPolicyReloadResult result, bool initialLoad)
    {
        foreach (IntegrityDiagnostic diagnostic in result.Diagnostics)
        {
            _log.LogError("Mod folder policy: " + diagnostic);
        }

        if (result.Success && result.ActiveSnapshot != null)
        {
            _log.LogInfo(
                $"{(initialLoad ? "Loaded" : "Reloaded")} mod folder policy generation " +
                $"{result.ActiveSnapshot.Generation} with " +
                $"{result.ActiveSnapshot.Rules.Count} rules.");
        }
        else if (result.KeptPreviousSnapshot)
        {
            _log.LogWarning(
                "Invalid required/optional folder update ignored; the " +
                "last-known-good policy remains active.");
        }
        else
        {
            _log.LogWarning("No valid required/optional folder policy is active; connections remain refused.");
        }
    }

    private void OnPolicyChanged(object sender, FileSystemEventArgs eventArgs)
    {
        if (IsRelevantPolicyPath(eventArgs.FullPath))
        {
            Reload();
        }
    }

    private void OnPolicyRenamed(object sender, RenamedEventArgs eventArgs)
    {
        if (IsRelevantPolicyPath(eventArgs.FullPath) ||
            IsRelevantPolicyPath(eventArgs.OldFullPath))
        {
            Reload();
        }
    }

    private void OnPolicyRootChanged(
        object sender,
        FileSystemEventArgs eventArgs)
    {
        HandlePolicyRootChange(eventArgs.FullPath, null);
    }

    private void OnPolicyRootRenamed(
        object sender,
        RenamedEventArgs eventArgs)
    {
        HandlePolicyRootChange(eventArgs.FullPath, eventArgs.OldFullPath);
    }

    private void HandlePolicyRootChange(string? path, string? oldPath)
    {
        lock (_reloadGate)
        {
            if (_disposed)
            {
                return;
            }

            bool requiredChanged =
                IsSamePath(path, _requiredSourceRoot) ||
                IsSamePath(oldPath, _requiredSourceRoot);
            bool optionalChanged =
                IsSamePath(path, _optionalSourceRoot) ||
                IsSamePath(oldPath, _optionalSourceRoot);
            if (!requiredChanged && !optionalChanged)
            {
                return;
            }

            if (requiredChanged)
            {
                DisposePolicyWatcher(_requiredWatcher, rootWatcher: false);
                _requiredWatcher = null;
            }

            if (optionalChanged)
            {
                DisposePolicyWatcher(_optionalWatcher, rootWatcher: false);
                _optionalWatcher = null;
            }

            ScheduleReloadLocked(
                Stopwatch.GetTimestamp(),
                resetRetryCount: true);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        lock (_reloadGate)
        {
            if (_disposed)
            {
                return;
            }

            if (ReferenceEquals(sender, _requiredWatcher))
            {
                DisposePolicyWatcher(_requiredWatcher, rootWatcher: false);
                _requiredWatcher = null;
            }
            else if (ReferenceEquals(sender, _optionalWatcher))
            {
                DisposePolicyWatcher(_optionalWatcher, rootWatcher: false);
                _optionalWatcher = null;
            }
            else if (ReferenceEquals(sender, _rootWatcher))
            {
                // A lost root-directory notification could have hidden a
                // required/optional folder replacement. Invalidate both
                // recursive handles so Tick rebinds all three watchers to the
                // currently named directories before publishing a new scan.
                DisposePolicyWatcher(_requiredWatcher, rootWatcher: false);
                DisposePolicyWatcher(_optionalWatcher, rootWatcher: false);
                DisposePolicyWatcher(_rootWatcher, rootWatcher: true);
                _requiredWatcher = null;
                _optionalWatcher = null;
                _rootWatcher = null;
            }

            Exception exception = eventArgs.GetException();
            _log.LogError(
                "Mod folder watcher reported an error; a bounded reload retry " +
                "has been scheduled. " +
                exception.GetType().Name +
                ": " +
                exception.Message);
            ScheduleReloadLocked(
                Stopwatch.GetTimestamp(),
                resetRetryCount: true);
        }
    }

    private bool TryEnsurePolicyWatchersLocked()
    {
        bool success = true;
        if (_requiredWatcher == null &&
            Directory.Exists(_requiredSourceRoot))
        {
            try
            {
                _requiredWatcher =
                    CreatePolicySourceWatcher(_requiredSourceRoot);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                success = false;
                _log.LogError(
                    "Could not rearm the required mod-folder watcher: " +
                    exception.Message);
            }
        }

        if (_optionalWatcher == null &&
            Directory.Exists(_optionalSourceRoot))
        {
            try
            {
                _optionalWatcher =
                    CreatePolicySourceWatcher(_optionalSourceRoot);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                success = false;
                _log.LogError(
                    "Could not rearm the optional mod-folder watcher: " +
                    exception.Message);
            }
        }

        if (_rootWatcher == null && Directory.Exists(_dataRoot))
        {
            try
            {
                _rootWatcher = CreatePolicyRootWatcher(_dataRoot);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                success = false;
                _log.LogError(
                    "Could not rearm the mod-folder root watcher: " +
                    exception.Message);
            }
        }

        return success;
    }

    private void ScheduleReloadLocked(
        long now,
        bool resetRetryCount)
    {
        if (_disposed)
        {
            return;
        }

        if (resetRetryCount)
        {
            _reloadRetryCount = 0;
        }

        ++_reloadRequestVersion;
        _reloadCancellation?.Cancel();
        _reloadPending = true;
        _reloadDueTimestamp = now + ReloadQuietPeriodTicks;
    }

    private void ScheduleFailedReloadRetryLocked(long now)
    {
        if (_disposed)
        {
            return;
        }

        if (_reloadRetryCount >= MaximumAutomaticReloadRetries)
        {
            _reloadRetryCount = 0;
            _log.LogWarning(
                "Automatic mod folder policy reload retries were exhausted. " +
                (_policyStore.Current != null
                    ? "The last-known-good policy remains active; "
                    : "No valid policy is active and connections remain refused; ") +
                "the next file change will start a new reload attempt.");
            return;
        }

        _reloadRetryCount++;
        ScheduleReloadLocked(now, resetRetryCount: false);
        _log.LogWarning(
            "Scheduled mod folder policy reload retry " +
            _reloadRetryCount +
            "/" +
            MaximumAutomaticReloadRetries +
            ".");
    }

    private static string SafePeerLabel(ServerPeerIdentity peerIdentity)
    {
        if (peerIdentity == null)
        {
            return "unknown peer";
        }

        return string.IsNullOrEmpty(peerIdentity.Endpoint)
            ? "peer"
            : peerIdentity.Endpoint;
    }

    private bool IsRelevantPolicyPath(string? path)
    {
        return IsRelevantPolicyPath(
            path,
            _requiredSourceRoot,
            _optionalSourceRoot);
    }

    internal static bool IsRelevantPolicyPath(
        string? path,
        string requiredSourceRoot,
        string optionalSourceRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }

        return IsSameOrDescendant(fullPath, requiredSourceRoot) ||
               IsSameOrDescendant(fullPath, optionalSourceRoot);
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (string.Equals(path, root, PathComparison))
        {
            return true;
        }

        string sourcePrefix = root.EndsWith(
            Path.DirectorySeparatorChar.ToString(),
            StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(sourcePrefix, PathComparison);
    }

    private static bool IsSamePath(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(path),
                root,
                PathComparison);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    private void DisposePolicyWatcher(
        FileSystemWatcher? watcher,
        bool rootWatcher)
    {
        if (watcher == null)
        {
            return;
        }

        if (rootWatcher)
        {
            watcher.Created -= OnPolicyRootChanged;
            watcher.Deleted -= OnPolicyRootChanged;
            watcher.Renamed -= OnPolicyRootRenamed;
        }
        else
        {
            watcher.Changed -= OnPolicyChanged;
            watcher.Created -= OnPolicyChanged;
            watcher.Deleted -= OnPolicyChanged;
            watcher.Renamed -= OnPolicyRenamed;
        }

        watcher.Error -= OnWatcherError;

        try
        {
            watcher.EnableRaisingEvents = false;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            _log.LogWarning(
                "Could not stop a mod folder watcher cleanly: " +
                exception.Message);
        }

        try
        {
            watcher.Dispose();
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            _log.LogWarning(
                "Could not dispose a mod folder watcher cleanly: " +
                exception.Message);
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? requiredWatcher;
        FileSystemWatcher? optionalWatcher;
        FileSystemWatcher? rootWatcher;
        lock (_reloadGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _reloadPending = false;
            _reloadCancellation?.Cancel();
            if (_reloadTask != null)
            {
                CancellationTokenSource? cancellation = _reloadCancellation;
                _ = _reloadTask.ContinueWith(completed =>
                {
                    if (completed.IsFaulted) _ = completed.Exception;
                    cancellation?.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else _reloadCancellation?.Dispose();
            _reloadTask = null;
            _reloadCancellation = null;
            requiredWatcher = _requiredWatcher;
            optionalWatcher = _optionalWatcher;
            rootWatcher = _rootWatcher;
            _requiredWatcher = null;
            _optionalWatcher = null;
            _rootWatcher = null;
        }

        DisposePolicyWatcher(requiredWatcher, rootWatcher: false);
        DisposePolicyWatcher(optionalWatcher, rootWatcher: false);
        DisposePolicyWatcher(rootWatcher, rootWatcher: true);
    }
}
