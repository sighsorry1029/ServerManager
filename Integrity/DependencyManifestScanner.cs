using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using Mono.Cecil;

namespace ServerManager;

// Only server-selected managed dependencies are inspected. A loaded assembly's
// actual backing file wins over unused copies in the mod directories.
internal static class DependencyManifestScanner
{
    private const int MaximumDirectories = 4096;
    private const int MaximumDirectoryEntries = 16384;
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private static readonly StringComparer PathComparer = Path.DirectorySeparatorChar == '\\'
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class LoadedRecord
    {
        internal LoadedRecord(string key, string location, Guid mvid)
        {
            Key = key;
            Location = location;
            Mvid = mvid;
        }

        internal readonly string Key;
        internal readonly string Location;
        internal readonly Guid Mvid;
    }

    internal sealed class Preparation : IDisposable
    {
        private readonly string[] _keys;
        private readonly LoadedRecord[] _loaded;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly Task<IntegrityManifestBuildResult> _task;
        private bool _disposed;

        private Preparation(string[] keys, LoadedRecord[] loaded, string[] roots, IntegrityLimits limits)
        {
            _keys = keys;
            _loaded = loaded;
            CancellationToken cancellation = _cancellation.Token;
            _task = Task.Run(async () =>
            {
                try
                {
                    await ScanGate.WaitAsync(cancellation).ConfigureAwait(false);
                    try { return Build(keys, loaded, roots, limits, cancellation); }
                    finally { ScanGate.Release(); }
                }
                finally { _elapsed.Stop(); }
            });
        }

        private Preparation(string[] keys, Exception error)
        {
            _keys = keys;
            _loaded = Array.Empty<LoadedRecord>();
            _elapsed.Stop();
            _task = Task.FromResult(Failed(error));
        }

        internal long ElapsedMilliseconds => _elapsed.ElapsedMilliseconds;
        internal bool HasChanges => !MatchesCurrentAssemblies();

        // Call on the owning thread, at a bounded polling interval. No file IO,
        // hashing, type enumeration, or Unity objects participate in this check.
        internal bool MatchesCurrentAssemblies() => MatchesCurrentAssemblies(AppDomain.CurrentDomain.GetAssemblies());

        internal bool MatchesCurrentAssemblies(IEnumerable<Assembly> assemblies)
        {
            if (_disposed) return false;
            try
            {
                LoadedRecord[] current = CaptureLoaded(_keys, assemblies);
                return _loaded.Length == current.Length && _loaded.Zip(current, (left, right) =>
                    left.Key == right.Key && PathComparer.Equals(left.Location, right.Location) &&
                    left.Mvid == right.Mvid).All(equal => equal);
            }
            catch (Exception error) when (!IntegrityCanonical.IsFatal(error)) { return false; }
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
            _ = _task.ContinueWith(completed =>
            {
                if (completed.IsFaulted) _ = completed.Exception;
                _cancellation.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        internal static Preparation Capture(string[] keys, IntegrityLimits limits,
            IEnumerable<Assembly> assemblies, IEnumerable<string> roots)
        {
            string[] selected = keys.ToArray();
            try
            {
                ValidateKeys(selected, limits);
                Array.Sort(selected, StringComparer.Ordinal);
                return new Preparation(selected, CaptureLoaded(selected, assemblies),
                    roots.Select(Path.GetFullPath).Distinct(PathComparer).ToArray(), limits);
            }
            catch (Exception error) when (!IntegrityCanonical.IsFatal(error))
            {
                return new Preparation(selected, error);
            }
        }
    }

    internal static Preparation Begin(string[] keys, IntegrityLimits limits) =>
        Begin(keys, limits, AppDomain.CurrentDomain.GetAssemblies(), new[] { Paths.PluginPath, Paths.BepInExRootPath + Path.DirectorySeparatorChar + "core" });

    // Deterministic test/tooling entrypoint; actual assemblies are captured here,
    // never retained or consulted by the worker.
    internal static Preparation Begin(string[] keys, IntegrityLimits limits,
        IEnumerable<Assembly> assemblies, IEnumerable<string> roots)
    {
        if (keys == null) throw new ArgumentNullException(nameof(keys));
        if (limits == null) throw new ArgumentNullException(nameof(limits));
        if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));
        if (roots == null) throw new ArgumentNullException(nameof(roots));
        return Preparation.Capture(keys, limits, assemblies, roots);
    }

    private static void ValidateKeys(string[] keys, IntegrityLimits limits)
    {
        if (keys.Length > Math.Min(limits.MaxPluginCount, IntegrityAssemblyIdentity.MaximumLibraryCount))
            throw new InvalidDataException("Too many managed dependency identities.");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            if (key == null || !IntegrityAssemblyIdentity.IsLibraryKey(key) ||
                IntegrityAssemblyIdentity.GetKey(key.Substring("assembly:".Length)) != key || !seen.Add(key))
                throw new InvalidDataException("A managed dependency identity is invalid or duplicated.");
        }
    }

    private static LoadedRecord[] CaptureLoaded(string[] keys, IEnumerable<Assembly> assemblies)
    {
        if (keys.Length == 0) return Array.Empty<LoadedRecord>();
        HashSet<string> selected = new(keys, StringComparer.Ordinal);
        List<LoadedRecord> records = new();
        foreach (Assembly assembly in assemblies)
        {
            string? name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name)) continue;
            string key;
            try { key = IntegrityAssemblyIdentity.GetKey(name!); }
            catch (ArgumentException) { continue; }
            if (!selected.Contains(key)) continue;
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                throw new InvalidDataException("Selected dependency '" + key + "' is loaded without an attestable file.");
            records.Add(new LoadedRecord(key, Path.GetFullPath(assembly.Location), assembly.ManifestModule.ModuleVersionId));
        }
        return records.OrderBy(record => record.Key, StringComparer.Ordinal)
            .ThenBy(record => record.Location, PathComparer).ThenBy(record => record.Mvid).ToArray();
    }

    private static IntegrityManifestBuildResult Build(string[] keys, LoadedRecord[] loaded, string[] roots,
        IntegrityLimits limits, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            Dictionary<string, IntegrityManifestEntry> entries = new(StringComparer.Ordinal);
            HashSet<string> loadedKeys = new(loaded.Select(record => record.Key), StringComparer.Ordinal);
            foreach (LoadedRecord record in loaded)
            {
                cancellation.ThrowIfCancellationRequested();
                IntegrityManifestEntry entry = ReadSelectedFile(record.Location,
                    new HashSet<string>(new[] { record.Key }, StringComparer.Ordinal), record.Mvid, limits, cancellation)
                    ?? throw new InvalidDataException("Loaded dependency identity no longer matches its backing file: " + record.Key);
                AddConsistent(entries, entry);
            }

            HashSet<string> pending = new(keys.Where(key => !loadedKeys.Contains(key)), StringComparer.Ordinal);
            if (pending.Count != 0)
            {
                foreach (string path in EnumerateCandidates(roots, cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();
                    IntegrityManifestEntry? entry;
                    try { entry = ReadSelectedFile(path, pending, null, limits, cancellation); }
                    catch (BadImageFormatException)
                    {
                        // Native DLLs are unrelated unless they occupy a selected
                        // assembly's conventional filename, which cannot attest it.
                        if (MatchesCandidateName(path, pending)) throw;
                        continue;
                    }
                    catch (Exception error) when (!IntegrityCanonical.IsFatal(error) &&
                        !(error is OperationCanceledException) && !MatchesCandidateName(path, pending))
                    {
                        // A selected managed file's failures are wrapped below,
                        // so unreadable/non-managed unrelated binaries are ignored.
                        if (error is SelectedFileException) throw;
                        continue;
                    }
                    if (entry != null) AddConsistent(entries, entry);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            return new IntegrityManifestBuildResult(true, new IntegrityManifest(entries.Values), Array.Empty<IntegrityDiagnostic>());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (!IntegrityCanonical.IsFatal(error)) { return Failed(error); }
    }

    private static bool MatchesCandidateName(string path, HashSet<string> selected)
    {
        try { return selected.Contains(IntegrityAssemblyIdentity.GetKey(Path.GetFileNameWithoutExtension(path))); }
        catch (ArgumentException) { return false; }
    }

    private sealed class SelectedFileException : IOException
    {
        internal SelectedFileException(string path, Exception error)
            : base("Selected dependency could not be attested: " + path, error) { }
    }

    private static IntegrityManifestEntry? ReadSelectedFile(string path, HashSet<string> selected, Guid? runtimeMvid,
        IntegrityLimits limits, CancellationToken cancellation)
    {
        bool matched = runtimeMvid.HasValue;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            FileInfo before = new(path);
            before.Refresh();
            if ((before.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Selected dependency cannot be a reparse point.");
            long length = before.Length;
            DateTime write = before.LastWriteTimeUtc;
            DateTime created = before.CreationTimeUtc;
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            using ModuleDefinition module = ModuleDefinition.ReadModule(stream,
                new ReaderParameters { InMemory = false, ReadSymbols = false, ReadingMode = ReadingMode.Deferred });
            if (module.Assembly == null) throw new BadImageFormatException("Managed dependency has no assembly identity.");
            string name = module.Assembly.Name.Name;
            string key = IntegrityAssemblyIdentity.GetKey(name);
            if (!selected.Contains(key)) return null;
            matched = true;
            if (runtimeMvid.HasValue && module.Mvid != runtimeMvid.Value)
                throw new IOException("Loaded dependency does not match its backing file's module identity.");
            EnsureUnchanged(path, stream, length, write, created);
            stream.Position = 0;
            byte[] buffer = new byte[81920];
            using SHA256 hasher = SHA256.Create();
            long remaining = length;
            while (remaining > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) throw new IOException("Dependency ended while hashing.");
                hasher.TransformBlock(buffer, 0, count, buffer, 0);
                remaining -= count;
            }
            cancellation.ThrowIfCancellationRequested();
            EnsureUnchanged(path, stream, length, write, created);
            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return new IntegrityManifestEntry(key, name, IntegrityCanonical.ToLowerHex(hasher.Hash!), limits);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (matched && !IntegrityCanonical.IsFatal(error))
        {
            throw new SelectedFileException(path, error);
        }
    }

    private static void EnsureUnchanged(string path, FileStream stream, long length, DateTime write, DateTime created)
    {
        FileInfo after = new(path);
        after.Refresh();
        if (!after.Exists || (after.Attributes & FileAttributes.ReparsePoint) != 0 ||
            after.Length != length || stream.Length != length || after.LastWriteTimeUtc != write || after.CreationTimeUtc != created)
            throw new IOException("Dependency file changed during attestation.");
    }

    private static void AddConsistent(Dictionary<string, IntegrityManifestEntry> entries, IntegrityManifestEntry entry)
    {
        if (entries.TryGetValue(entry.PluginGuid, out IntegrityManifestEntry previous) && previous.FileSha256 != entry.FileSha256)
            throw new InvalidDataException("Conflicting files share managed dependency identity '" + entry.PluginGuid + "'.");
        entries[entry.PluginGuid] = entry;
    }

    private static IEnumerable<string> EnumerateCandidates(string[] roots, CancellationToken cancellation)
    {
        Queue<string> pending = new(roots);
        HashSet<string> visited = new(PathComparer);
        int entries = 0;
        while (pending.Count != 0)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = pending.Dequeue();
            if (!visited.Add(path)) continue;
            if (visited.Count > MaximumDirectories) throw new IOException("Dependency search has too many directories.");
            DirectoryInfo directory = new(path);
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos())
            {
                cancellation.ThrowIfCancellationRequested();
                if (++entries > MaximumDirectoryEntries) throw new IOException("Dependency search has too many entries.");
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (item is DirectoryInfo) pending.Enqueue(item.FullName);
                else if (string.Equals(item.Extension, ".dll", StringComparison.OrdinalIgnoreCase)) yield return item.FullName;
            }
        }
    }

    private static IntegrityManifestBuildResult Failed(Exception error) => new(false, null,
        new[] { IntegrityCanonical.Error(IntegrityDiagnosticCodes.ManifestHashFailed,
            "Managed dependency scan failed: " + error.GetType().Name + ": " + error.Message) });
}
