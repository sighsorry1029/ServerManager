using System;
using System.Collections.Generic;
using System.Diagnostics;

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
/// enabled, an exact-name scan of native modules loaded into this process. It
/// never reads command lines, window titles, other-process module lists, or
/// mutates a process. The exact name "smi" alone is accepted only after reading
/// the main executable's version resource.
/// </summary>
internal static class ExternalProcessDetector
{
    private const string SharpMonoInjectorProductName =
        "SharpMonoInjector.Console";

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

        if (policy.DetectCheatEngine &&
            !TryScanCurrentProcessNativeModules(evidence))
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
        HashSet<DetectionEvidence> evidence)
    {
        try
        {
            using Process currentProcess = Process.GetCurrentProcess();
            ProcessModuleCollection modules = currentProcess.Modules;
            foreach (ProcessModule module in modules)
            {
                try
                {
                    if (IsCheatEngineNativeModuleName(module.ModuleName))
                    {
                        evidence.Add(
                            DetectionEvidence.CheatEngineInjectedModule);
                        break;
                    }
                }
                catch (Exception exception)
                    when (!IntegrityCanonical.IsFatal(exception))
                {
                    // A single unreadable module does not invalidate the
                    // current-process module snapshot.
                }
            }

            return true;
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
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
