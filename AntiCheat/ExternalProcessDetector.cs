using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ServerManager;

internal readonly struct ExternalProcessScanPolicy
{
    internal ExternalProcessScanPolicy(
        bool detectCheatEngine,
        bool detectExternalTools,
        bool detectGenericProcessNames)
    {
        DetectCheatEngine = detectCheatEngine;
        DetectExternalTools = detectExternalTools;
        DetectGenericProcessNames = detectGenericProcessNames;
    }

    internal bool DetectCheatEngine { get; }

    internal bool DetectExternalTools { get; }

    internal bool DetectGenericProcessNames { get; }
}

internal sealed class ExternalProcessScanResult
{
    internal static readonly ExternalProcessScanResult Unavailable =
        new(Array.Empty<DetectionEvidence>(), true);

    internal ExternalProcessScanResult(
        IReadOnlyList<DetectionEvidence> evidence,
        bool detectorUnavailable)
    {
        Evidence = evidence ??
                   throw new ArgumentNullException(nameof(evidence));
        DetectorUnavailable = detectorUnavailable;
    }

    internal IReadOnlyList<DetectionEvidence> Evidence { get; }

    internal bool DetectorUnavailable { get; }
}

/// <summary>
/// Performs a narrow process-name scan and, when Cheat Engine detection is
/// enabled, an exact-name scan of native modules loaded into this process.
/// External-tool detection also hashes a bounded set of loaded proxy DLLs for
/// exact known loader signatures; a proxy name alone is never evidence. It
/// never reads command lines, window titles, other-process module lists, or
/// mutates a process. The exact name "smi" alone is accepted only after reading
/// the main executable's version resource.
/// </summary>
internal static class ExternalProcessDetector
{
    private const string SharpMonoInjectorProductName =
        "SharpMonoInjector.Console";

    private const int MaximumNativeHashCacheEntries = 128;
    private const long MaximumNativeModuleBytes = 8 * 1024 * 1024;
    private const long MaximumNativeScanBytes = 16 * 1024 * 1024;
    private const int MaximumNativeScanFiles = 4;
    private const int MaximumNativeScanMilliseconds = 200;

    // Candidate names select files to inspect, not files to condemn. In
    // particular, ordinary Windows DLLs and BepInEx's Doorstop are not cheats.
    private static readonly HashSet<string> NativeLoaderCandidateNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "version.dll", "winmm.dll", "dinput8.dll", "xinput1_3.dll",
            "xinput1_4.dll", "xinput9_1_0.dll", "dsound.dll", "wininet.dll",
            "msacm32.dll", "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll",
            "d3d12.dll", "ddraw.dll", "opengl32.dll", "winhttp.dll"
        };

    // Signature data observed in the supplied ValheimEnforcer 0.32.1
    // CheatToolCatalog. The cheat binaries themselves were not downloaded or
    // executed. A different/repacked loader is not covered by these hashes.
    private static readonly HashSet<string> ValheavenNativeLoaderHashes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "b381ba752c760398866dc8933816f07b9b0c64651972d413a716ad1d255c7985",
            "229aa381485ea15099775581673cd6388ce7dbeadb6a1bde8858df4021e418e3",
            "726066d7780ae8bd9f9e0e4e3604584a47df4bf85dedfc61b2495716bff42d85"
        };

    private static readonly object NativeHashCacheLock = new();
    private static readonly Dictionary<string, NativeHashCacheEntry> NativeHashCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> NativeHashCacheOrder = new();
    private static int nativeScanStart;

    private readonly struct NativeHashCacheEntry
    {
        internal NativeHashCacheEntry(long length, long writeTicks, string hash)
        {
            Length = length;
            WriteTicks = writeTicks;
            Hash = hash;
        }

        internal long Length { get; }
        internal long WriteTicks { get; }
        internal string Hash { get; }
    }

    private sealed class NativeHashScanBudget
    {
        internal readonly Stopwatch Timer = Stopwatch.StartNew();
        internal int Files;
        internal long Bytes;

        // Cooperative checks between file operations: an individual OS file
        // read cannot be interrupted here. This runs only on the scan worker.
        internal bool HasTime => Timer.ElapsedMilliseconds < MaximumNativeScanMilliseconds;
    }

    private static readonly Dictionary<string, DetectionEvidence>
        CheatEngineNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["cheatengine"] = DetectionEvidence.CheatEngineProcess,
                ["cheatengine-x86_64"] =
                    DetectionEvidence.CheatEngineProcess,
                ["cheatengine-i386"] =
                    DetectionEvidence.CheatEngineProcess,
                ["cheatengine-x86_64-sse4-avx2"] =
                    DetectionEvidence.CheatEngineProcess
            };

    private static readonly HashSet<string> CheatEngineNativeModuleNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "speedhack-i386.dll",
            "speedhack-x86_64.dll",
            "vehdebug-i386.dll",
            "vehdebug-x86_64.dll",
            "MonoDataCollector32.dll",
            "MonoDataCollector64.dll"
        };

    private static readonly Dictionary<string, DetectionEvidence>
        ExternalToolNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["artmoney"] = DetectionEvidence.ArtMoneyProcess,
                ["wemod"] = DetectionEvidence.WeModProcess,
                ["sharpmonoinjector"] =
                    DetectionEvidence.SharpMonoInjectorProcess,
                ["smi_gui"] =
                    DetectionEvidence.SharpMonoInjectorProcess
            };

    private static readonly Dictionary<string, DetectionEvidence>
        GenericProcessNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["cheat"] = DetectionEvidence.GenericCheatProcess,
                ["inject"] = DetectionEvidence.GenericInjectProcess,
                ["trainer"] = DetectionEvidence.GenericTrainerProcess
            };

    internal static bool IsSupportedPlatform =>
        Environment.OSVersion.Platform == PlatformID.Win32NT;

    internal static ExternalProcessScanResult Scan(
        ExternalProcessScanPolicy policy)
    {
        if (!IsSupportedPlatform)
        {
            return ExternalProcessScanResult.Unavailable;
        }

        HashSet<DetectionEvidence> evidence = new();
        bool detectorUnavailable = false;

        if ((policy.DetectCheatEngine || policy.DetectExternalTools) &&
            !TryScanCurrentProcessNativeModules(policy, evidence))
        {
            detectorUnavailable = true;
        }

        if (!TryScanProcessNames(policy, evidence))
        {
            detectorUnavailable = true;
        }

        List<DetectionEvidence> orderedEvidence = new(evidence);
        orderedEvidence.Sort();
        return new ExternalProcessScanResult(
            orderedEvidence,
            detectorUnavailable);
    }

    internal static bool IsCheatEngineNativeModuleName(string? moduleName)
    {
        return !string.IsNullOrEmpty(moduleName) &&
               CheatEngineNativeModuleNames.Contains(moduleName!);
    }

    internal static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        string normalized = processName!;
        if (normalized.EndsWith(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(
                0,
                normalized.Length - 4);
        }

        return normalized;
    }

    private static bool TryScanCurrentProcessNativeModules(
        ExternalProcessScanPolicy policy,
        HashSet<DetectionEvidence> evidence)
    {
        try
        {
            using Process currentProcess = Process.GetCurrentProcess();
            ProcessModuleCollection modules = currentProcess.Modules;
            List<string> loaderCandidates = new();
            bool available = true;
            foreach (ProcessModule module in modules)
            {
                try
                {
                    if (policy.DetectCheatEngine &&
                        IsCheatEngineNativeModuleName(module.ModuleName))
                    {
                        evidence.Add(
                            DetectionEvidence.CheatEngineInjectedModule);
                    }

                    if (policy.DetectExternalTools &&
                        NativeLoaderCandidateNames.Contains(module.ModuleName))
                    {
                        if (loaderCandidates.Count < MaximumNativeHashCacheEntries)
                        {
                            loaderCandidates.Add(module.FileName);
                        }
                        else
                        {
                            available = false;
                        }
                    }
                }
                catch (Exception exception)
                    when (!IntegrityCanonical.IsFatal(exception))
                {
                    // An unreadable module is diagnostic, never evidence.
                    available = false;
                }
            }

            return TryScanNativeLoaderFiles(loaderCandidates, evidence) && available;
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    private static bool TryScanNativeLoaderFiles(
        IReadOnlyList<string> paths,
        HashSet<DetectionEvidence> evidence)
    {
        if (paths.Count == 0)
        {
            return true;
        }

        int start;
        lock (NativeHashCacheLock)
        {
            // Rotate across snapshots so unreadable files cannot indefinitely
            // consume the budget before newly loaded candidates are inspected.
            start = nativeScanStart % paths.Count;
            nativeScanStart = (start + 1) % paths.Count;
        }

        NativeHashScanBudget budget = new();
        bool available = true;
        for (int index = 0; index < paths.Count; ++index)
        {
            if (!TryGetNativeModuleHash(paths[(start + index) % paths.Count], budget,
                    out string hash, out bool deferred))
            {
                // Work intentionally left for the next scan is not an OS or
                // detector failure. Inaccessible/unsupported files still are.
                available &= deferred;
                continue;
            }

            if (ValheavenNativeLoaderHashes.Contains(hash))
            {
                evidence.Add(DetectionEvidence.ValheavenNativeLoaderDetected);
            }
        }

        return available;
    }

    private static bool TryGetNativeModuleHash(
        string path,
        NativeHashScanBudget budget,
        out string hash,
        out bool deferred)
    {
        hash = string.Empty;
        deferred = false;
        try
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                !NativeLoaderCandidateNames.Contains(Path.GetFileName(fullPath)))
            {
                return false;
            }

            FileInfo file = new(fullPath);
            long length = file.Length;
            long writeTicks = file.LastWriteTimeUtc.Ticks;
            if (length <= 0 || length > MaximumNativeModuleBytes)
            {
                return false;
            }

            lock (NativeHashCacheLock)
            {
                if (NativeHashCache.TryGetValue(fullPath, out NativeHashCacheEntry cached) &&
                    cached.Length == length && cached.WriteTicks == writeTicks)
                {
                    hash = cached.Hash;
                    return true;
                }
            }

            if (!budget.HasTime || budget.Files >= MaximumNativeScanFiles ||
                length > MaximumNativeScanBytes - budget.Bytes)
            {
                deferred = true;
                return false;
            }

            ++budget.Files;
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 32768, FileOptions.SequentialScan);
            if (stream.Length != length)
            {
                return false;
            }

            using SHA256 sha = SHA256.Create();
            byte[] buffer = new byte[32768];
            long readBytes = 0;
            while (readBytes < length)
            {
                if (!budget.HasTime)
                {
                    deferred = true;
                    return false;
                }

                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - readBytes));
                if (read == 0)
                {
                    return false;
                }

                budget.Bytes += read;
                readBytes += read;
                sha.TransformBlock(buffer, 0, read, buffer, 0);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            file.Refresh();
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != writeTicks ||
                stream.Length != length)
            {
                return false;
            }

            if (!budget.HasTime)
            {
                deferred = true;
                return false;
            }

            hash = BitConverter.ToString(sha.Hash!).Replace("-", string.Empty).ToLowerInvariant();
            lock (NativeHashCacheLock)
            {
                if (!NativeHashCache.ContainsKey(fullPath))
                {
                    if (NativeHashCache.Count >= MaximumNativeHashCacheEntries)
                    {
                        NativeHashCache.Remove(NativeHashCacheOrder.Dequeue());
                    }

                    NativeHashCacheOrder.Enqueue(fullPath);
                }

                NativeHashCache[fullPath] = new NativeHashCacheEntry(length, writeTicks, hash);
            }

            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            // Read/metadata races and permissions must not become accusations.
            return false;
        }
    }

    private static bool TryScanProcessNames(
        ExternalProcessScanPolicy policy,
        HashSet<DetectionEvidence> evidence)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }

        int readableProcessNames = 0;
        foreach (Process process in processes)
        {
            if (process == null)
            {
                continue;
            }

            try
            {
                string processName = NormalizeProcessName(
                    process.ProcessName);
                if (processName.Length == 0)
                {
                    continue;
                }

                readableProcessNames++;
                if (policy.DetectCheatEngine &&
                    CheatEngineNames.TryGetValue(
                        processName,
                        out DetectionEvidence cheatEngineEvidence))
                {
                    evidence.Add(cheatEngineEvidence);
                }

                if (policy.DetectExternalTools)
                {
                    if (ExternalToolNames.TryGetValue(
                            processName,
                            out DetectionEvidence externalToolEvidence))
                    {
                        evidence.Add(externalToolEvidence);
                    }
                    else if (string.Equals(
                                 processName,
                                 "smi",
                                 StringComparison.OrdinalIgnoreCase) &&
                             HasSharpMonoInjectorMetadata(process))
                    {
                        evidence.Add(
                            DetectionEvidence.SharpMonoInjectorProcess);
                    }
                }

                if (policy.DetectGenericProcessNames &&
                    GenericProcessNames.TryGetValue(
                        processName,
                        out DetectionEvidence genericEvidence))
                {
                    evidence.Add(genericEvidence);
                }
            }
            catch (Exception exception)
                when (!IntegrityCanonical.IsFatal(exception))
            {
                // Access-denied and process-exited races are expected during
                // system-wide enumeration. A single unreadable process does
                // not make the detector unavailable.
            }
            finally
            {
                process.Dispose();
            }
        }

        return processes.Length == 0 || readableProcessNames > 0;
    }

    private static bool HasSharpMonoInjectorMetadata(Process process)
    {
        try
        {
            ProcessModule? mainModule = process.MainModule;
            FileVersionInfo? versionInfo = mainModule?.FileVersionInfo;
            if (versionInfo == null)
            {
                return false;
            }

            return IsSharpMonoInjectorProductValue(
                       versionInfo.ProductName) ||
                   IsSharpMonoInjectorProductValue(
                       versionInfo.FileDescription);
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    private static bool IsSharpMonoInjectorProductValue(string? value)
    {
        return string.Equals(
            value,
            SharpMonoInjectorProductName,
            StringComparison.OrdinalIgnoreCase);
    }
}
