using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;

namespace ServerManager
{
    /// <summary>
    /// Builds the local manifest from BepInEx's already loaded PluginInfos.
    /// Assemblies are never loaded again; PluginInfo.Location is hashed directly.
    /// </summary>
    public static class PluginManifestScanner
    {
        // A cancelled connection must not leave competing disk scans behind.
        // This serializes asynchronous preparations, not server policy lookups.
        private static readonly SemaphoreSlim PreparationGate = new SemaphoreSlim(1, 1);

        private sealed class PluginRecord
        {
            internal PluginRecord(PluginInfo plugin)
            {
                Guid = plugin.Metadata.GUID;
                Name = plugin.Metadata.Name;
                Location = plugin.Location;
            }

            internal string Guid { get; }
            internal string Name { get; }
            internal string Location { get; }
        }

        /// <summary>
        /// One connection's disposable preparation. Only copied strings and file
        /// bytes reach the worker; it never touches Chainloader, Unity or RPCs.
        /// Disposal never waits for disk I/O and makes even a completed result
        /// unavailable. A later connection always starts a fresh file scan.
        /// </summary>
        internal sealed class Preparation : IDisposable
        {
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
            private readonly Stopwatch _elapsed = Stopwatch.StartNew();
            private readonly Task<IntegrityManifestBuildResult> _task;
            private readonly PluginRecord?[] _records = Array.Empty<PluginRecord?>();
            private bool _disposed;

            private Preparation(PluginRecord?[] records, IntegrityLimits limits)
            {
                _records = records;
                CancellationToken token = _cancellation.Token;
                _task = Task.Run(async () =>
                {
                    try
                    {
                        await PreparationGate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            return BuildSnapshot(records, limits, token);
                        }
                        finally
                        {
                            PreparationGate.Release();
                        }
                    }
                    finally
                    {
                        _elapsed.Stop();
                    }
                });
            }

            internal Preparation(IntegrityManifestBuildResult failure)
            {
                _elapsed.Stop();
                _task = Task.FromResult(failure);
            }

            internal long ElapsedMilliseconds => _elapsed.ElapsedMilliseconds;

            // Main-thread freshness check of the loaded plugin registry, not a
            // size/mtime shortcut for DLL contents. Files are still fully read
            // and hashed for each connection's preparation.
            internal bool MatchesCurrentPlugins()
            {
                if (_disposed) return false;
                try
                {
                    PluginRecord?[] current = CaptureRecords(Chainloader.PluginInfos.Values);
                    if (_records.Length != current.Length) return false;
                    return OrderRecords(_records).Zip(OrderRecords(current), (left, right) =>
                        left?.Guid == right?.Guid && left?.Name == right?.Name &&
                        left?.Location == right?.Location).All(matches => matches);
                }
                catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                {
                    return false;
                }
            }

            internal bool TryGetResult(out IntegrityManifestBuildResult result)
            {
                result = null!;
                if (_disposed || !_task.IsCompleted) return false;
                result = _task.GetAwaiter().GetResult();
                return true;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _cancellation.Cancel();
                // Observe abandoned faults and dispose the CTS only after the
                // worker stops using it. Never block the main thread on a task.
                _task.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    _cancellation.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            internal static Preparation Capture(IEnumerable<PluginInfo> pluginInfos, IntegrityLimits limits)
            {
                try
                {
                    return new Preparation(CaptureRecords(pluginInfos), limits);
                }
                catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                {
                    return new Preparation(SnapshotFailed(exception));
                }
            }
        }

        // Call these on the owning Unity thread, after Chainloader has finished.
        internal static Preparation BeginCurrent(IntegrityLimits limits)
        {
            if (limits == null) throw new ArgumentNullException(nameof(limits));
            try
            {
                return Begin(Chainloader.PluginInfos.Values, limits);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                return new Preparation(SnapshotFailed(exception));
            }
        }

        internal static Preparation Begin(IEnumerable<PluginInfo> pluginInfos, IntegrityLimits limits)
        {
            if (pluginInfos == null) throw new ArgumentNullException(nameof(pluginInfos));
            if (limits == null) throw new ArgumentNullException(nameof(limits));
            return Preparation.Capture(pluginInfos, limits);
        }

        private static PluginRecord?[] CaptureRecords(IEnumerable<PluginInfo> pluginInfos)
        {
            return pluginInfos.Select(plugin => plugin == null || plugin.Metadata == null
                ? null : new PluginRecord(plugin)).ToArray();
        }

        private static IOrderedEnumerable<PluginRecord?> OrderRecords(IEnumerable<PluginRecord?> records)
        {
            return records.OrderBy(plugin => plugin?.Guid ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(plugin => plugin?.Location ?? string.Empty, StringComparer.Ordinal);
        }

        private static IntegrityManifestBuildResult SnapshotFailed(Exception exception)
        {
            return Failed(IntegrityCanonical.Error(
                IntegrityDiagnosticCodes.ManifestUnavailable,
                "Could not snapshot BepInEx PluginInfos: " + exception.GetType().Name +
                ": " + exception.Message));
        }

        public static IntegrityManifestBuildResult BuildCurrent()
        {
            return BuildCurrent(IntegrityLimits.Default);
        }

        public static IntegrityManifestBuildResult BuildCurrent(IntegrityLimits limits)
        {
            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            PluginInfo[] pluginInfos;
            try
            {
                // Copy first so later work never observes a partially enumerated
                // Chainloader dictionary.
                pluginInfos = Chainloader.PluginInfos.Values.ToArray();
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                return Failed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.ManifestUnavailable,
                        "Could not snapshot BepInEx PluginInfos: " +
                        exception.GetType().Name +
                        ": " +
                        exception.Message));
            }

            return Build(pluginInfos, limits);
        }

        /// <summary>
        /// Public overload for deterministic tests and tooling. Runtime callers
        /// normally use BuildCurrent().
        /// </summary>
        public static IntegrityManifestBuildResult Build(
            IEnumerable<PluginInfo> pluginInfos,
            IntegrityLimits limits)
        {
            if (pluginInfos == null)
            {
                throw new ArgumentNullException(nameof(pluginInfos));
            }

            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            PluginRecord?[] records;
            try
            {
                records = CaptureRecords(pluginInfos);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                return Failed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.ManifestUnavailable,
                        "Could not enumerate BepInEx PluginInfos: " +
                        exception.GetType().Name +
                        ": " +
                        exception.Message));
            }

            return BuildSnapshot(records, limits, CancellationToken.None);
        }

        private static IntegrityManifestBuildResult BuildSnapshot(
            PluginRecord?[] records, IntegrityLimits limits, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<PluginRecord?> orderedPlugins = OrderRecords(records).ToList();

            if (orderedPlugins.Count > limits.MaxPluginCount)
            {
                return Failed(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.ManifestTooManyEntries,
                        "Loaded plugin count " +
                        orderedPlugins.Count +
                        " exceeds the configured limit of " +
                        limits.MaxPluginCount +
                        "."));
            }

            List<IntegrityDiagnostic> diagnostics = new List<IntegrityDiagnostic>();
            List<IntegrityManifestEntry> entries =
                new List<IntegrityManifestEntry>(orderedPlugins.Count);
            HashSet<string> seenGuids = new HashSet<string>(StringComparer.Ordinal);
            StringComparer pathComparer =
                Path.DirectorySeparatorChar == '\\'
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;
            Dictionary<string, string> hashByFullPath =
                new Dictionary<string, string>(pathComparer);

            foreach (PluginRecord? pluginInfo in orderedPlugins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pluginInfo == null)
                {
                    diagnostics.Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.ManifestUnavailable,
                            "BepInEx returned a plugin record without metadata."));
                    continue;
                }

                IntegrityDiagnostic diagnostic;
                string pluginGuid;
                if (!IntegrityCanonical.TryNormalizeGuid(
                        pluginInfo.Guid,
                        limits,
                        IntegrityDiagnosticCodes.ManifestInvalidGuid,
                        out pluginGuid,
                        out diagnostic))
                {
                    diagnostics.Add(diagnostic);
                    continue;
                }

                if (!seenGuids.Add(pluginGuid))
                {
                    diagnostics.Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.ManifestDuplicateGuid,
                            "More than one loaded plugin declares GUID '" +
                            pluginGuid +
                            "'.",
                            pluginGuid));
                    continue;
                }

                string name;
                if (!IntegrityCanonical.TryNormalizeDisplayString(
                        pluginInfo.Name,
                        limits.MaxNameUtf8Bytes,
                        "Plugin name",
                        IntegrityDiagnosticCodes.ManifestInvalidName,
                        pluginGuid,
                        out name,
                        out diagnostic))
                {
                    diagnostics.Add(diagnostic);
                    continue;
                }

                string location = pluginInfo.Location;
                if (string.IsNullOrWhiteSpace(location))
                {
                    diagnostics.Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.ManifestMissingLocation,
                            "BepInEx did not provide a DLL location for '" +
                            name +
                            "'. Dynamic or in-memory plugins cannot be attested.",
                            pluginGuid));
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(location);
                }
                catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                {
                    diagnostics.Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.ManifestMissingLocation,
                            "The DLL location for '" +
                            name +
                            "' is invalid: " +
                            exception.Message,
                            pluginGuid));
                    continue;
                }

                string sha256;
                if (!hashByFullPath.TryGetValue(fullPath, out sha256))
                {
                    if (!TryHashPluginFile(
                            fullPath,
                            name,
                            pluginGuid,
                            cancellationToken,
                            out sha256,
                            out diagnostic))
                    {
                        diagnostics.Add(diagnostic);
                        continue;
                    }

                    hashByFullPath.Add(fullPath, sha256);
                }

                entries.Add(
                    new IntegrityManifestEntry(
                        pluginGuid,
                        name,
                        sha256,
                        limits));
            }

            if (diagnostics.Count != 0)
            {
                return new IntegrityManifestBuildResult(
                    false,
                    null,
                    diagnostics);
            }

            return new IntegrityManifestBuildResult(
                true,
                new IntegrityManifest(entries),
                diagnostics);
        }

        private static bool TryHashPluginFile(
            string fullPath,
            string pluginName,
            string pluginGuid,
            CancellationToken cancellationToken,
            out string sha256,
            out IntegrityDiagnostic diagnostic)
        {
            sha256 = string.Empty;
            diagnostic = null!;

            try
            {
                using (FileStream stream = new FileStream(
                           fullPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           81920,
                           FileOptions.SequentialScan))
                {
                    using (SHA256 hasher = SHA256.Create())
                    {
                        byte[] buffer = new byte[81920];
                        long initialLength = stream.Length;
                        long totalRead = 0;
                        // Read only the initial extent so a continuously growing
                        // file cannot turn this preparation into unbounded work.
                        while (totalRead < initialLength)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int count = stream.Read(buffer, 0,
                                (int)Math.Min(buffer.Length, initialLength - totalRead));
                            if (count == 0) break;
                            totalRead += count;
                            hasher.TransformBlock(buffer, 0, count, buffer, 0);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        if (totalRead != initialLength || stream.Length != initialLength)
                            throw new InvalidDataException("The DLL length changed while hashing.");
                        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        sha256 = IntegrityCanonical.ToLowerHex(hasher.Hash!);
                    }
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                diagnostic = IntegrityCanonical.Error(
                    IntegrityDiagnosticCodes.ManifestFileNotFound,
                    "The DLL for '" + pluginName + "' no longer exists.",
                    pluginGuid);
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                diagnostic = IntegrityCanonical.Error(
                    IntegrityDiagnosticCodes.ManifestFileNotFound,
                    "The DLL directory for '" + pluginName + "' no longer exists.",
                    pluginGuid);
                return false;
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                diagnostic = IntegrityCanonical.Error(
                    IntegrityDiagnosticCodes.ManifestHashFailed,
                    "Could not SHA-256 the DLL for '" +
                    pluginName +
                    "': " +
                    exception.GetType().Name +
                    ": " +
                    exception.Message,
                    pluginGuid);
                return false;
            }
        }

        private static IntegrityManifestBuildResult Failed(
            IntegrityDiagnostic diagnostic)
        {
            return new IntegrityManifestBuildResult(
                false,
                null,
                new[] { diagnostic });
        }
    }
}
