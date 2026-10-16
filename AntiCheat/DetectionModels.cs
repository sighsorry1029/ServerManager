using System;
using System.Collections.Generic;

namespace ServerManager;

public enum DetectionAction
{
    Off = 0,
    Log = 1,
    Kick = 2,
    Ban = 3
}

internal enum DetectionEvidence : ushort
{
    None = 0,

    CheatEngineProcess = 100,
    CheatEngineInjectedModule = 101,
    ArtMoneyProcess = 110,
    WeModProcess = 111,
    SharpMonoInjectorProcess = 112,

    GenericCheatProcess = 120,
    GenericInjectProcess = 121,
    GenericTrainerProcess = 122,

    ValheimToolerDetected = 200,
    ValheavenAssemblyDetected = 201,
    ValheavenNativeLoaderDetected = 202,

    CheatCommand = 300,

    CarryWeightLimitExceeded = 400,
    MaximumDamageLimitExceeded = 401,
    // Produced only from server-side routed-RPC inspection. The detection
    // wire codec deliberately rejects this value in client reports.
    MalformedGameplayTraffic = 402,
    // Measured from a received full profile, never accepted as client detector
    // claims. Numeric caps do not invalidate that profile or its revision.
    MaximumHealthLimitExceeded = 403,
    MaximumStaminaLimitExceeded = 404,
    MaximumEitrLimitExceeded = 405,
    // A client observed a non-finite/invalid gameplay number, not a finite
    // stat-cap excess. It must never inherit a numeric administrator exception.
    InvalidGameplayValue = 406,

    ProcessDetectorUnsupported = 900,
    ProcessDetectorUnavailable = 901,
    AssemblyDetectorUnavailable = 902,
    AssemblyInspectionIncomplete = 903
}

internal readonly struct ClientDetectionSignal
{
    internal ClientDetectionSignal(
        DetectionEvidence evidence,
        string detail = "",
        uint policyGeneration = 1)
    {
        if (evidence == DetectionEvidence.None)
        {
            throw new ArgumentOutOfRangeException(nameof(evidence));
        }

        Evidence = evidence;
        Detail = detail ?? string.Empty;
        PolicyGeneration = policyGeneration;
    }

    internal DetectionEvidence Evidence { get; }

    internal string Detail { get; }

    internal uint PolicyGeneration { get; }
}

internal static class DetectionEvidenceCatalog
{
    internal static bool IsDefined(DetectionEvidence evidence)
    {
        return Enum.IsDefined(typeof(DetectionEvidence), evidence) &&
               evidence != DetectionEvidence.None;
    }

    internal static bool IsClientReportable(DetectionEvidence evidence)
    {
        return IsDefined(evidence) &&
               evidence != DetectionEvidence.MalformedGameplayTraffic &&
               !IsSnapshotStatLimit(evidence);
    }

    internal static bool IsSticky(DetectionEvidence evidence)
    {
        return evidence != DetectionEvidence.CheatCommand;
    }

    internal static bool IsDiagnostic(DetectionEvidence evidence)
    {
        return evidence == DetectionEvidence.ProcessDetectorUnsupported ||
               evidence == DetectionEvidence.ProcessDetectorUnavailable ||
               IsAssemblyDiagnostic(evidence);
    }

    internal static bool IsAssemblyDiagnostic(DetectionEvidence evidence) =>
        evidence == DetectionEvidence.AssemblyDetectorUnavailable ||
        evidence == DetectionEvidence.AssemblyInspectionIncomplete;

    internal static bool IsGenericProcessName(DetectionEvidence evidence)
    {
        return evidence == DetectionEvidence.GenericCheatProcess ||
               evidence == DetectionEvidence.GenericInjectProcess ||
               evidence == DetectionEvidence.GenericTrainerProcess;
    }

    internal static bool IsCheatEngine(DetectionEvidence evidence)
    {
        return evidence == DetectionEvidence.CheatEngineProcess ||
               evidence == DetectionEvidence.CheatEngineInjectedModule;
    }

    internal static bool IsExternalTool(DetectionEvidence evidence)
    {
        return evidence == DetectionEvidence.ArtMoneyProcess ||
               evidence == DetectionEvidence.WeModProcess ||
               evidence == DetectionEvidence.SharpMonoInjectorProcess ||
               evidence == DetectionEvidence.ValheavenNativeLoaderDetected;
    }

    internal static bool IsInjectedCheatAssembly(DetectionEvidence evidence)
    {
        return evidence == DetectionEvidence.ValheimToolerDetected ||
               evidence == DetectionEvidence.ValheavenAssemblyDetected;
    }

    internal static bool IsGameplayLimit(DetectionEvidence evidence)
    {
        return evidence == DetectionEvidence.CarryWeightLimitExceeded ||
               evidence == DetectionEvidence.MaximumDamageLimitExceeded ||
               IsSnapshotStatLimit(evidence);
    }

    internal static bool IsSnapshotStatLimit(DetectionEvidence evidence)
    {
        return evidence == DetectionEvidence.MaximumHealthLimitExceeded ||
               evidence == DetectionEvidence.MaximumStaminaLimitExceeded ||
               evidence == DetectionEvidence.MaximumEitrLimitExceeded;
    }
}

// A small diagnostic contract, not an arbitrary exception/log text channel.
// Only assembly simple names and exception type names belong here, never paths,
// exception messages, stack traces, or assembly-qualified identities.
internal static class AssemblyDiagnosticDetail
{
    internal const int MaximumLength = 192;
    private const int MaximumIdentifierLength = 64;
    private const string Unknown = "unknown";

    internal static string Format(string stage, string assemblyName, string exceptionType)
    {
        bool infrastructure = stage == "subscribe" || stage == "enumerate";
        if (!infrastructure && stage != "name" && stage != "types" && stage != "namespace")
            throw new ArgumentException("Unknown assembly diagnostic stage.", nameof(stage));
        return "stage=" + stage + ";assembly=" +
            (infrastructure ? Unknown : SanitizeIdentifier(assemblyName)) +
            ";exception=" + SanitizeIdentifier(exceptionType);
    }

    internal static bool IsValid(DetectionEvidence evidence, string detail) =>
        TryParse(evidence, detail, out _, out _, out _);

    internal static bool TryParse(DetectionEvidence evidence, string detail,
        out string stage, out string assemblyName, out string exceptionType)
    {
        stage = assemblyName = exceptionType = string.Empty;
        if (!DetectionEvidenceCatalog.IsAssemblyDiagnostic(evidence) ||
            string.IsNullOrEmpty(detail) || detail.Length > MaximumLength)
            return false;
        string[] parts = detail.Split(';');
        if (parts.Length != 3 || !parts[0].StartsWith("stage=", StringComparison.Ordinal) ||
            !parts[1].StartsWith("assembly=", StringComparison.Ordinal) ||
            !parts[2].StartsWith("exception=", StringComparison.Ordinal))
            return false;

        string parsedStage = parts[0].Substring(6);
        string parsedAssembly = parts[1].Substring(9);
        string parsedException = parts[2].Substring(10);
        bool infrastructure = evidence == DetectionEvidence.AssemblyDetectorUnavailable;
        if ((infrastructure
                ? parsedStage != "subscribe" && parsedStage != "enumerate"
                : parsedStage != "name" && parsedStage != "types" && parsedStage != "namespace") ||
            !IsIdentifier(parsedAssembly) || !IsIdentifier(parsedException) ||
            (infrastructure && parsedAssembly != Unknown))
            return false;

        stage = parsedStage;
        assemblyName = parsedAssembly;
        exceptionType = parsedException;
        return true;
    }

    private static string SanitizeIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value)) return Unknown;
        int length = Math.Min(value.Length, MaximumIdentifierLength);
        char[] result = new char[length];
        for (int index = 0; index < length; ++index)
            result[index] = IsIdentifierCharacter(value[index]) ? value[index] : '_';
        return new string(result);
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || value.Length > MaximumIdentifierLength) return false;
        foreach (char character in value)
            if (!IsIdentifierCharacter(character)) return false;
        return true;
    }

    private static bool IsIdentifierCharacter(char value) =>
        (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
        (value >= '0' && value <= '9') || value == '.' || value == '_' || value == '-';
}

internal static class DetectionRateLimiter
{
    internal static bool TryAdmit(
        Queue<long> timestamps,
        long now,
        long windowTicks,
        int maximumCount)
    {
        if (timestamps == null)
        {
            throw new ArgumentNullException(nameof(timestamps));
        }

        if (windowTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowTicks));
        }

        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        long cutoff = now - windowTicks;
        while (timestamps.Count > 0 &&
               timestamps.Peek() < cutoff)
        {
            timestamps.Dequeue();
        }

        if (timestamps.Count >= maximumCount)
        {
            return false;
        }

        timestamps.Enqueue(now);
        return true;
    }
}
