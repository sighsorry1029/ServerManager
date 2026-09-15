using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using BepInEx;
using Mono.Cecil;

namespace ServerManager
{
    /// <summary>
    /// Reads administrator-provided plugin/library DLLs without loading them into the
    /// CLR. Hashing and deferred metadata reads share one read-only file handle.
    /// File sharing prevents writes/replacement where the platform enforces it;
    /// metadata checks also reject observable changes during the scan.
    /// </summary>
    internal sealed class ReferencePluginPolicyScanner
    {
        private const int MaximumDirectories = 4096;
        private const int MaximumDirectoryEntries = 16384;
        private const int MaximumTypesPerAssembly = 100000;
        private const int MaximumAttributesPerAssembly = 200000;
        private static readonly string BepInPluginAttributeName =
            typeof(BepInPlugin).FullName;
        private static readonly string BepInExAssemblyName =
            typeof(BepInPlugin).Assembly.GetName().Name;

        private readonly string _sourceRoot;
        private readonly IntegrityLimits _limits;

        internal ReferencePluginPolicyScanner(
            string sourceRoot,
            IntegrityLimits limits)
        {
            if (string.IsNullOrWhiteSpace(sourceRoot))
            {
                throw new ArgumentException(
                    "A reference plugin source root is required.",
                    nameof(sourceRoot));
            }

            _sourceRoot = Path.GetFullPath(sourceRoot);
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }

        internal string RequiredRoot =>
            Path.Combine(_sourceRoot, "required");

        internal string OptionalRoot =>
            Path.Combine(_sourceRoot, "optional");

        internal void EnsureDirectories()
        {
            Directory.CreateDirectory(RequiredRoot);
            Directory.CreateDirectory(OptionalRoot);
        }

        internal ReferencePluginPolicyScanResult Scan(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<IntegrityDiagnostic> diagnostics =
                new List<IntegrityDiagnostic>();
            List<string> skippedLibraries = new List<string>();
            List<ReferencePluginPolicyRecord> records =
                new List<ReferencePluginPolicyRecord>();

            List<ReferencePluginFile> files =
                new List<ReferencePluginFile>();
            EnumerateRoleFiles(
                RequiredRoot,
                IntegrityRequirement.Required,
                files,
                diagnostics,
                cancellationToken);
            if (files.Count <= _limits.MaxPluginCount &&
                diagnostics.Count == 0)
            {
                EnumerateRoleFiles(
                    OptionalRoot,
                    IntegrityRequirement.Optional,
                    files,
                    diagnostics,
                    cancellationToken);
            }

            if (files.Count > _limits.MaxPluginCount)
            {
                diagnostics.Add(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.PolicySourceTooManyFiles,
                        "Reference DLL count " +
                        files.Count +
                        " exceeds the configured limit of " +
                        _limits.MaxPluginCount +
                        "."));
            }

            if (diagnostics.Count != 0)
            {
                return new ReferencePluginPolicyScanResult(
                    records,
                    diagnostics);
            }

            files.Sort((left, right) =>
            {
                int roleOrder = left.Requirement.CompareTo(right.Requirement);
                return roleOrder != 0
                    ? roleOrder
                    : StringComparer.Ordinal.Compare(
                        left.FullPath,
                        right.FullPath);
            });

            foreach (ReferencePluginFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanFile(
                    file,
                    records,
                    diagnostics,
                    skippedLibraries,
                    cancellationToken);
                if (diagnostics.Count != 0)
                {
                    break;
                }

            }

            ValidateAggregate(records, diagnostics, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new ReferencePluginPolicyScanResult(
                records,
                diagnostics,
                skippedLibraries);
        }

        private void EnumerateRoleFiles(
            string roleRoot,
            IntegrityRequirement requirement,
            ICollection<ReferencePluginFile> files,
            ICollection<IntegrityDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(roleRoot))
            {
                diagnostics.Add(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.PolicySourceReadFailed,
                        "Reference plugin directory does not exist: " +
                        roleRoot));
                return;
            }

            try
            {
                DirectoryInfo root = new DirectoryInfo(roleRoot);
                if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.PolicySourceReparsePoint,
                            "Reference plugin directories cannot be reparse points: " +
                            roleRoot));
                    return;
                }

                Queue<DirectoryInfo> pending = new Queue<DirectoryInfo>();
                pending.Enqueue(root);
                int directoryCount = 0;
                int directoryEntryCount = 0;

                while (pending.Count != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DirectoryInfo directory = pending.Dequeue();
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(
                            IntegrityCanonical.Error(
                                IntegrityDiagnosticCodes.PolicySourceReparsePoint,
                                "Reference plugin directories cannot be reparse points: " +
                                directory.FullName));
                        continue;
                    }

                    directoryCount++;
                    if (directoryCount > MaximumDirectories)
                    {
                        diagnostics.Add(
                            IntegrityCanonical.Error(
                                IntegrityDiagnosticCodes.PolicySourceTooManyDirectories,
                                "Reference plugin tree contains more than " +
                                MaximumDirectories +
                                " directories."));
                        return;
                    }

                    foreach (FileSystemInfo entry in
                             directory.EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        directoryEntryCount++;
                        if (directoryEntryCount > MaximumDirectoryEntries)
                        {
                            diagnostics.Add(
                                IntegrityCanonical.Error(
                                    IntegrityDiagnosticCodes.PolicySourceTooManyEntries,
                                    "Reference plugin tree contains more than " +
                                    MaximumDirectoryEntries +
                                    " directory entries."));
                            return;
                        }

                        if ((entry.Attributes &
                             FileAttributes.ReparsePoint) != 0)
                        {
                            diagnostics.Add(
                                IntegrityCanonical.Error(
                                    IntegrityDiagnosticCodes.PolicySourceReparsePoint,
                                    "Reference plugin entries cannot be reparse points: " +
                                    entry.FullName));
                            continue;
                        }

                        DirectoryInfo? child = entry as DirectoryInfo;
                        if (child != null)
                        {
                            if (directoryCount + pending.Count >=
                                MaximumDirectories)
                            {
                                diagnostics.Add(
                                    IntegrityCanonical.Error(
                                        IntegrityDiagnosticCodes.PolicySourceTooManyDirectories,
                                        "Reference plugin tree contains more than " +
                                        MaximumDirectories +
                                        " directories."));
                                return;
                            }

                            pending.Enqueue(child);
                            continue;
                        }

                        FileInfo? file = entry as FileInfo;
                        if (file == null ||
                            !string.Equals(
                                file.Extension,
                                ".dll",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        files.Add(
                            new ReferencePluginFile(
                                Path.GetFullPath(file.FullName),
                                requirement));
                        if (files.Count > _limits.MaxPluginCount)
                        {
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                diagnostics.Add(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.PolicySourceReadFailed,
                        "Could not enumerate reference plugin directory '" +
                        roleRoot +
                        "': " +
                        exception.GetType().Name +
                        ": " +
                        exception.Message));
            }
        }

        private void ScanFile(
            ReferencePluginFile file,
            ICollection<ReferencePluginPolicyRecord> records,
            ICollection<IntegrityDiagnostic> diagnostics,
            ICollection<string> skippedLibraries,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo original = new FileInfo(file.FullPath);
                original.Refresh();
                if (!original.Exists)
                {
                    throw new FileNotFoundException("Reference DLL no longer exists.", file.FullPath);
                }

                if ((original.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.PolicySourceReparsePoint,
                            "Reference DLLs cannot be reparse points: " +
                            file.FullPath));
                    return;
                }

                long originalLength = original.Length;
                DateTime originalWriteTime = original.LastWriteTimeUtc;
                DateTime originalCreationTime = original.CreationTimeUtc;
                using (FileStream stream = new FileStream(
                           file.FullPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           81920,
                           FileOptions.SequentialScan))
                {
                    if (originalLength <= 0)
                    {
                        diagnostics.Add(
                            IntegrityCanonical.Error(
                                IntegrityDiagnosticCodes.PolicySourceInvalidAssembly,
                                "Reference DLL is empty: " + file.FullPath));
                        return;
                    }

                    EnsureUnchanged(
                        file.FullPath, stream, originalLength,
                        originalWriteTime, originalCreationTime);
                    string sha256 = HashFile(stream, originalLength, cancellationToken);
                    EnsureUnchanged(
                        file.FullPath, stream, originalLength,
                        originalWriteTime, originalCreationTime);
                    stream.Position = 0;

                    // Do not publish even a partial file result until metadata
                    // decoding and the final file-change check both succeed.
                    List<ReferencePluginPolicyRecord> fileRecords =
                        new List<ReferencePluginPolicyRecord>();
                    try
                    {
                        using (ModuleDefinition module = ModuleDefinition.ReadModule(
                                   stream,
                                   new ReaderParameters
                                   {
                                       InMemory = false,
                                       ReadSymbols = false,
                                       // Only BepInPlugin's string arguments are needed.
                                       // Deferred decoding avoids resolving unrelated
                                       // attributes and copying embedded asset resources.
                                       ReadingMode = ReadingMode.Deferred
                                   }))
                        {
                            List<TypeDefinition> types = GetBoundedTypes(
                                module,
                                file.FullPath,
                                cancellationToken);
                            int attributeCount = 0;
                            HashSet<string> fileGuids =
                                new HashSet<string>(StringComparer.Ordinal);

                            foreach (TypeDefinition type in types)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (!type.HasCustomAttributes)
                                {
                                    continue;
                                }

                                attributeCount += type.CustomAttributes.Count;
                                if (attributeCount > MaximumAttributesPerAssembly)
                                {
                                    throw new InvalidDataException(
                                        "Assembly contains more than " +
                                        MaximumAttributesPerAssembly +
                                        " custom attributes.");
                                }

                                foreach (CustomAttribute attribute in type.CustomAttributes)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    if (!IsBepInPluginAttribute(attribute))
                                    {
                                        continue;
                                    }

                                    IntegrityManifestEntry entry = DecodeBepInPluginAttribute(
                                        attribute, sha256, file.FullPath);
                                    if (!fileGuids.Add(entry.PluginGuid))
                                    {
                                        throw new InvalidDataException(
                                            "The DLL declares BepInPlugin GUID '" +
                                            entry.PluginGuid +
                                            "' more than once.");
                                    }

                                    if (records.Count + fileRecords.Count >= _limits.MaxPluginCount)
                                    {
                                        diagnostics.Add(
                                            IntegrityCanonical.Error(
                                                IntegrityDiagnosticCodes.PolicyTooManyRules,
                                                "Reference DLLs declare more than " +
                                                _limits.MaxPluginCount +
                                                " plugin entries."));
                                        return;
                                    }

                                    fileRecords.Add(new ReferencePluginPolicyRecord(
                                        file.Requirement, entry,
                                        attribute.ConstructorArguments[2].Value as string ?? string.Empty));
                                }
                            }

                            if (fileRecords.Count == 0)
                            {
                                if (module.Assembly == null)
                                    throw new InvalidDataException("Reference DLL has no managed assembly identity: " + file.FullPath);
                                skippedLibraries.Add(file.FullPath);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                    {
                        AddInvalidAssemblyDiagnostic(file.FullPath, exception, diagnostics);
                        return;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureUnchanged(
                        file.FullPath, stream, originalLength,
                        originalWriteTime, originalCreationTime);
                    foreach (ReferencePluginPolicyRecord record in fileRecords)
                    {
                        records.Add(record);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                diagnostics.Add(
                    IntegrityCanonical.Error(
                        IntegrityDiagnosticCodes.PolicySourceReadFailed,
                        "Could not read reference DLL '" + file.FullPath +
                        "': " + exception.GetType().Name + ": " + exception.Message));
            }
        }

        private static string HashFile(
            FileStream stream,
            long initialLength,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[81920];
            using (SHA256 hasher = SHA256.Create())
            {
                long remaining = initialLength;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0)
                    {
                        throw new IOException("Reference DLL changed or ended while being read.");
                    }

                    hasher.TransformBlock(buffer, 0, read, buffer, 0);
                    remaining -= read;
                }

                cancellationToken.ThrowIfCancellationRequested();
                hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return IntegrityCanonical.ToLowerHex(hasher.Hash!);
            }
        }

        private static void EnsureUnchanged(
            string fullPath,
            FileStream stream,
            long originalLength,
            DateTime originalWriteTime,
            DateTime originalCreationTime)
        {
            FileInfo current = new FileInfo(fullPath);
            current.Refresh();
            if (!current.Exists ||
                (current.Attributes & FileAttributes.ReparsePoint) != 0 ||
                current.Length != originalLength ||
                stream.Length != originalLength ||
                current.LastWriteTimeUtc != originalWriteTime ||
                current.CreationTimeUtc != originalCreationTime)
            {
                throw new IOException(
                    "Reference DLL changed while being read; retry reload: " + fullPath);
            }
        }

        private List<TypeDefinition> GetBoundedTypes(
            ModuleDefinition module,
            string fullPath,
            CancellationToken cancellationToken)
        {
            List<TypeDefinition> result = new List<TypeDefinition>();
            Stack<TypeDefinition> pending = new Stack<TypeDefinition>(
                module.Types.Reverse());

            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TypeDefinition type = pending.Pop();
                result.Add(type);
                if (result.Count > MaximumTypesPerAssembly)
                {
                    throw new InvalidDataException(
                        "Assembly contains more than " +
                        MaximumTypesPerAssembly +
                        " types: " +
                        fullPath);
                }

                if (!type.HasNestedTypes)
                {
                    continue;
                }

                for (int index = type.NestedTypes.Count - 1; index >= 0; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pending.Push(type.NestedTypes[index]);
                }
            }

            return result;
        }

        private IntegrityManifestEntry DecodeBepInPluginAttribute(
            CustomAttribute attribute,
            string sha256,
            string fullPath)
        {
            if (attribute.ConstructorArguments.Count != 3)
            {
                throw new InvalidDataException(
                    "BepInPlugin attribute must have exactly three constructor arguments: " +
                    fullPath);
            }

            string? guid = attribute.ConstructorArguments[0].Value as string;
            string? name = attribute.ConstructorArguments[1].Value as string;

            try
            {
                return new IntegrityManifestEntry(
                    guid ?? string.Empty,
                    name ?? string.Empty,
                    sha256,
                    _limits);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "BepInPlugin metadata is invalid in '" +
                    fullPath +
                    "': " +
                    exception.Message,
                    exception);
            }
        }

        private static bool IsBepInPluginAttribute(
            CustomAttribute attribute)
        {
            TypeReference attributeType = attribute.AttributeType;
            return string.Equals(
                       attributeType.FullName,
                       BepInPluginAttributeName,
                       StringComparison.Ordinal) &&
                   attributeType.Scope != null &&
                   string.Equals(
                       attributeType.Scope.Name,
                       BepInExAssemblyName,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateAggregate(
            IEnumerable<ReferencePluginPolicyRecord> records,
            ICollection<IntegrityDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (IGrouping<string, ReferencePluginPolicyRecord> group in
                     records.GroupBy(
                         item => item.Entry.PluginGuid,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntegrityRequirement[] requirements = group
                    .Select(item => item.Requirement)
                    .Distinct()
                    .ToArray();
                if (requirements.Length != 1)
                {
                    diagnostics.Add(
                        IntegrityCanonical.Error(
                            IntegrityDiagnosticCodes.PolicySourceRoleConflict,
                            "Plugin GUID '" +
                            group.Key +
                            "' appears in both required and optional reference folders.",
                            group.Key));
                    continue;
                }

                // GUID is the security identity. Display names may
                // legitimately change across rolling-upgrade reference DLLs.
            }
        }

        private static void AddInvalidAssemblyDiagnostic(
            string fullPath,
            Exception exception,
            ICollection<IntegrityDiagnostic> diagnostics)
        {
            diagnostics.Add(
                IntegrityCanonical.Error(
                    IntegrityDiagnosticCodes.PolicySourceInvalidAssembly,
                    "Could not read plugin or managed-assembly metadata from reference DLL '" +
                    fullPath +
                    "': " +
                    exception.GetType().Name +
                    ": " +
                    exception.Message));
        }

        private sealed class ReferencePluginFile
        {
            internal ReferencePluginFile(
                string fullPath,
                IntegrityRequirement requirement)
            {
                FullPath = fullPath;
                Requirement = requirement;
            }

            internal string FullPath { get; }

            internal IntegrityRequirement Requirement { get; }
        }
    }

    internal sealed class ReferencePluginPolicyRecord
    {
        internal ReferencePluginPolicyRecord(
            IntegrityRequirement requirement,
            IntegrityManifestEntry entry,
            string version = "")
        {
            Requirement = requirement;
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            Version = version;
        }

        internal IntegrityRequirement Requirement { get; }

        internal IntegrityManifestEntry Entry { get; }

        // Kept separately so reference metadata cannot change admission payloads.
        internal string Version { get; }

    }

    internal sealed class ReferencePluginPolicyScanResult
    {
        internal ReferencePluginPolicyScanResult(
            IEnumerable<ReferencePluginPolicyRecord> records,
            IEnumerable<IntegrityDiagnostic> diagnostics,
            IEnumerable<string>? skippedLibraries = null)
        {
            SkippedLibraries = Array.AsReadOnly((skippedLibraries ?? Array.Empty<string>()).ToArray());
            Records = records.ToArray();
            Diagnostics = IntegrityCollections.Freeze(diagnostics);
        }

        internal IReadOnlyList<string> SkippedLibraries { get; }

        internal IReadOnlyList<ReferencePluginPolicyRecord> Records { get; }

        internal IReadOnlyList<IntegrityDiagnostic> Diagnostics { get; }
    }
}
