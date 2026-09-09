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
    AssemblyDetectorUnavailable = 902
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
               evidence == DetectionEvidence.AssemblyDetectorUnavailable;
    }

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
               evidence == DetectionEvidence.SharpMonoInjectorProcess;
    }

    internal static bool IsValheimTooler(DetectionEvidence evidence)
    {
        return evidence == DetectionEvidence.ValheimToolerDetected;
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
