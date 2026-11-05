using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Mono.Cecil;

namespace ServerManager
{
    /// <summary>
    /// Describes installed BepInEx 5 preloader candidates without loading an
    /// assembly or invoking TargetDLLs. This attests files, not patch execution.
    /// </summary>
    internal static class PatcherMetadata
    {
        private const int MaximumDirectories = 4096;
        private const int MaximumDirectoryEntries = 16384;
        private const int MaximumTypes = 100000;
        private const int MaximumMethods = 200000;

        internal static IReadOnlyList<PatcherMetadataRecord> Read(
            ModuleDefinition module, string sha256, IntegrityLimits limits,
            CancellationToken cancellationToken)
        {
            if (module.Assembly == null)
                throw new InvalidDataException("DLL has no managed assembly identity.");
            if (module.Types.Count > MaximumTypes)
                throw new InvalidDataException("Assembly contains too many top-level types.");

            List<TypeDefinition> patchers = new List<TypeDefinition>();
            int methodCount = 0;
            // BepInEx 5 TypeLoader.FindPluginTypes selects MainModule.Types,
            // not nested types. Mirror AssemblyPatcher.ToPatcherPlugin's
            // metadata contract, including its exact-case Patch method name.
            foreach (TypeDefinition type in module.Types)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (type.IsInterface || (type.IsAbstract && !type.IsSealed)) continue;
                methodCount += type.Methods.Count;
                if (methodCount > MaximumMethods)
                    throw new InvalidDataException("Assembly contains too many methods.");
                MethodDefinition? targetGetter = type.Methods.FirstOrDefault(method =>
                    method.Name.Equals("get_TargetDLLs", StringComparison.InvariantCultureIgnoreCase) &&
                    method.IsPublic && method.IsStatic);
                if (targetGetter == null || targetGetter.ReturnType.FullName !=
                    "System.Collections.Generic.IEnumerable`1<System.String>") continue;
                if (!type.Methods.Any(method => method.Name == "Patch" &&
                    method.IsPublic && method.IsStatic && method.ReturnType.FullName == "System.Void" &&
                    method.Parameters.Count == 1 &&
                    (method.Parameters[0].ParameterType.FullName == "Mono.Cecil.AssemblyDefinition" ||
                     method.Parameters[0].ParameterType.FullName == "Mono.Cecil.AssemblyDefinition&"))) continue;
                if (patchers.Count >= limits.MaxPluginCount)
                    throw new InvalidDataException("Assembly declares too many patcher entries.");
                patchers.Add(type);
            }

            string assemblyName = module.Assembly.Name.Name;
            string version = module.Assembly.Name.Version?.ToString() ?? string.Empty;
            List<PatcherMetadataRecord> records = new List<PatcherMetadataRecord>();
            foreach (TypeDefinition type in patchers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(assemblyName) || assemblyName.IndexOf('\n') >= 0 ||
                    string.IsNullOrWhiteSpace(type.FullName) || type.FullName.IndexOf('\n') >= 0)
                    throw new InvalidDataException("Patcher assembly/type identity is invalid.");
                string id;
                using (SHA256 hasher = SHA256.Create())
                    id = IntegrityCanonical.ToLowerHex(hasher.ComputeHash(
                        Encoding.UTF8.GetBytes(assemblyName + "\n" + type.FullName)));
                string name = patchers.Count == 1 ? assemblyName : assemblyName + " / " + type.FullName;
                records.Add(new PatcherMetadataRecord(
                    new IntegrityManifestEntry(id, name, sha256, limits, IntegrityModuleKind.Patcher), version));
            }
            return records;
        }

        internal static InstalledPatcherScanResult ScanDirectory(
            string patcherRoot, IntegrityLimits limits, CancellationToken cancellationToken)
        {
            if (limits == null) throw new ArgumentNullException(nameof(limits));
            List<PatcherMetadataRecord> records = new List<PatcherMetadataRecord>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<string> files = EnumerateFiles(patcherRoot, limits, cancellationToken);
                HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (string path in files)
                {
                    foreach (PatcherMetadataRecord record in ReadFile(path, limits, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!identities.Add(record.Entry.IdentityKey))
                            throw new InvalidDataException("More than one installed patcher declares identity '" +
                                record.Entry.PluginGuid + "'.");
                        if (records.Count >= limits.MaxPluginCount)
                            throw new InvalidDataException("Installed patchers exceed the configured module count limit.");
                        records.Add(record);
                    }
                }
                return new InstalledPatcherScanResult(true, records, Array.Empty<IntegrityDiagnostic>());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                // Incomplete discovery must not hide optional entries or become
                // a successful admission manifest with missing patchers.
                return new InstalledPatcherScanResult(false, Array.Empty<PatcherMetadataRecord>(), new[]
                {
                    IntegrityCanonical.Error(IntegrityDiagnosticCodes.ManifestUnavailable,
                        "Could not inspect installed preloader patchers: " + exception.GetType().Name +
                        ": " + exception.Message)
                });
            }
        }

        private static List<string> EnumerateFiles(
            string rootPath, IntegrityLimits limits, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new InvalidDataException("BepInEx did not provide a patcher directory.");
            string fullRoot = Path.GetFullPath(rootPath);
            List<string> files = new List<string>();
            // A missing patchers folder is the normal no-patchers installation.
            // Do not use Directory.Exists: it also hides access/IO failures.
            try
            {
                FileAttributes rootAttributes = File.GetAttributes(fullRoot);
                if ((rootAttributes & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("Patcher directory is not a directory: " + fullRoot);
            }
            catch (DirectoryNotFoundException) { return files; }
            catch (FileNotFoundException) { return files; }

            Queue<DirectoryInfo> pending = new Queue<DirectoryInfo>();
            pending.Enqueue(new DirectoryInfo(fullRoot));
            int directoryCount = 0;
            int entryCount = 0;
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectoryInfo directory = pending.Dequeue();
                directory.Refresh();
                RejectReparsePoint(directory);
                if (++directoryCount > MaximumDirectories)
                    throw new InvalidDataException("Patcher tree contains too many directories.");
                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++entryCount > MaximumDirectoryEntries)
                        throw new InvalidDataException("Patcher tree contains too many directory entries.");
                    RejectReparsePoint(entry);
                    if (entry is DirectoryInfo child)
                    {
                        if (directoryCount + pending.Count >= MaximumDirectories)
                            throw new InvalidDataException("Patcher tree contains too many directories.");
                        pending.Enqueue(child);
                    }
                    else if (entry is FileInfo file &&
                             file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        if (files.Count >= limits.MaxPluginCount)
                            throw new InvalidDataException("Patcher tree contains too many DLL files.");
                        files.Add(Path.GetFullPath(file.FullName));
                    }
                }
            }
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        private static IReadOnlyList<PatcherMetadataRecord> ReadFile(
            string path, IntegrityLimits limits, CancellationToken cancellationToken)
        {
            FileInfo original = new FileInfo(path);
            original.Refresh();
            if (!original.Exists) throw new FileNotFoundException("Patcher DLL no longer exists.", path);
            RejectReparsePoint(original);
            long length = original.Length;
            DateTime modified = original.LastWriteTimeUtc;
            DateTime created = original.CreationTimeUtc;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 81920, FileOptions.SequentialScan))
            {
                EnsureUnchanged(path, stream, length, modified, created);
                string sha256 = Hash(stream, length, cancellationToken);
                EnsureUnchanged(path, stream, length, modified, created);
                stream.Position = 0;
                IReadOnlyList<PatcherMetadataRecord> result;
                try
                {
                    using (ModuleDefinition module = ModuleDefinition.ReadModule(stream, new ReaderParameters
                           { InMemory = false, ReadSymbols = false, ReadingMode = ReadingMode.Deferred }))
                        result = Read(module, sha256, limits, cancellationToken);
                }
                catch (BadImageFormatException)
                {
                    // Native helper DLLs are not preloader candidates. Only
                    // positively identified native PE DLLs may be ignored;
                    // malformed managed images and arbitrary bytes still fail.
                    if (!IsNativePeDll(stream, cancellationToken)) throw;
                    result = Array.Empty<PatcherMetadataRecord>();
                }
                EnsureUnchanged(path, stream, length, modified, created);
                return result;
            }
        }

        private static bool IsNativePeDll(FileStream stream, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length < 64) return false;
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                stream.Position = 0;
                if (reader.ReadUInt16() != 0x5a4d) return false;
                stream.Position = 0x3c;
                uint headerOffset = reader.ReadUInt32();
                if (headerOffset < 64 || (long)headerOffset + 24 > stream.Length) return false;
                stream.Position = headerOffset;
                if (reader.ReadUInt32() != 0x00004550) return false;
                ushort machine = reader.ReadUInt16();
                if (machine != 0x014c && machine != 0x8664 && machine != 0x01c0 &&
                    machine != 0x01c4 && machine != 0xaa64) return false;
                ushort sectionCount = reader.ReadUInt16();
                if (sectionCount == 0 || sectionCount > 96) return false;
                stream.Position = headerOffset + 20;
                ushort optionalSize = reader.ReadUInt16();
                ushort characteristics = reader.ReadUInt16();
                if ((characteristics & 0x2000) == 0) return false;
                long optionalStart = (long)headerOffset + 24;
                long sectionsStart = optionalStart + optionalSize;
                if (sectionsStart + sectionCount * 40 > stream.Length || optionalSize < 96) return false;
                ushort magic = reader.ReadUInt16();
                int directoryOffset = magic == 0x10b ? 96 : magic == 0x20b ? 112 : 0;
                if (directoryOffset == 0 || optionalSize < directoryOffset) return false;
                stream.Position = optionalStart + directoryOffset - 4;
                uint directoryCount = reader.ReadUInt32();
                if (directoryCount > 16 || directoryOffset + directoryCount * 8 > optionalSize) return false;
                // IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR is the CLR header. A
                // nonzero RVA OR size must be treated as managed, even if Cecil
                // could not decode it; never downgrade corrupt managed images.
                if (directoryCount > 14)
                {
                    stream.Position = optionalStart + directoryOffset + 14 * 8;
                    uint clrRva = reader.ReadUInt32();
                    uint clrSize = reader.ReadUInt32();
                    if (clrRva != 0 || clrSize != 0) return false;
                }
                for (int index = 0; index < sectionCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stream.Position = sectionsStart + index * 40 + 16;
                    uint rawSize = reader.ReadUInt32();
                    uint rawOffset = reader.ReadUInt32();
                    if (rawSize != 0 && (rawOffset < sectionsStart + sectionCount * 40 ||
                        (long)rawOffset + rawSize > stream.Length)) return false;
                }
                return true;
            }
        }

        internal static string Hash(FileStream stream, long length, CancellationToken cancellationToken)
        {
            using (SHA256 hasher = SHA256.Create())
            {
                byte[] buffer = new byte[81920];
                long remaining = length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw new IOException("DLL ended while being read.");
                    hasher.TransformBlock(buffer, 0, read, buffer, 0);
                    remaining -= read;
                }
                cancellationToken.ThrowIfCancellationRequested();
                hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return IntegrityCanonical.ToLowerHex(hasher.Hash!);
            }
        }

        internal static void EnsureUnchanged(string path, FileStream stream, long length,
            DateTime modified, DateTime created)
        {
            FileInfo current = new FileInfo(path);
            current.Refresh();
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0 ||
                current.Length != length || stream.Length != length ||
                current.LastWriteTimeUtc != modified || current.CreationTimeUtc != created)
                throw new IOException("DLL changed while being read; retry the scan: " + path);
        }

        private static void RejectReparsePoint(FileSystemInfo entry)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Patcher entries cannot be reparse points: " + entry.FullName);
        }
    }

    internal sealed class PatcherMetadataRecord
    {
        internal PatcherMetadataRecord(IntegrityManifestEntry entry, string version)
        {
            Entry = entry;
            Version = version;
        }
        internal IntegrityManifestEntry Entry { get; }
        internal string Version { get; }
    }

    internal sealed class InstalledPatcherScanResult
    {
        internal InstalledPatcherScanResult(bool success, IEnumerable<PatcherMetadataRecord> records,
            IEnumerable<IntegrityDiagnostic> diagnostics)
        {
            Success = success;
            Records = Array.AsReadOnly(records.ToArray());
            Diagnostics = IntegrityCollections.Freeze(diagnostics);
        }
        internal bool Success { get; }
        internal IReadOnlyList<PatcherMetadataRecord> Records { get; }
        internal IReadOnlyList<IntegrityDiagnostic> Diagnostics { get; }
    }
}
