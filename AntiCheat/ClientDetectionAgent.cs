using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

namespace ServerManager;

/// <summary>
/// Coordinates optional client-side detection signals. The agent never applies
/// enforcement itself; callers bind dequeued signals to an authenticated
/// connection and let the server-owned policy choose an action.
/// </summary>
internal sealed class ClientDetectionAgent
{
    private const int MaximumQueuedAssemblies = 128;
    private const int MaximumAssemblyCandidatesPerTick = 16;
    private const int ReservedEventCandidatesPerTick = 8;
    private const int CarryWeightFailureWarningThreshold = 3;
    private static readonly long CarryWeightScanIntervalTicks =
        Stopwatch.Frequency;

    private readonly object _assemblyQueueGate = new();
    private readonly object _signalGate = new();
    private readonly Queue<Assembly> _loadedAssemblies = new();
    private readonly Queue<ClientDetectionSignal> _signals = new();
    private readonly Dictionary<Assembly, uint> _inspectedAssemblies = new();
    private readonly Dictionary<DetectionEvidence, uint> _stickyEvidence = new();

    private ProtocolChallengeOptions? _policy;
    private Task<ExternalProcessScanResult>? _processScanTask;
    private long _processScanGeneration;
    private uint _processScanPolicyGeneration;
    private uint _policyGeneration = 1;
    private long _generation;
    private long _nextProcessScanTimestamp;
    private long _nextCarryWeightScanTimestamp;
    private int _consecutiveCarryWeightFailures;
    private bool _runtimeWorldReady;
    private bool _carryWeightFailureLogged;

    private bool _acceptAssemblyLoadEvents;
    private bool _assemblyLoadSubscribed;
    private bool _assemblyQueueOverflowed;
    private Assembly[] _assemblyReconciliation = Array.Empty<Assembly>();
    private int _assemblyReconciliationIndex;
    private int _assemblyReconciliationPass;

    /// <summary>
    /// Starts a fresh detector generation. Configure is intended to be called
    /// when a new client connection adopts its server-supplied policy.
    /// </summary>
    internal void Configure(ProtocolChallengeOptions policy)
    {
        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        Reset();
        _policy = policy;
        _policyGeneration = 1;
        _nextProcessScanTimestamp = 0;
        _nextCarryWeightScanTimestamp = 0;
        _runtimeWorldReady = false;

        if (policy.DetectValheimTooler)
        {
            StartAssemblyDetector();
        }

        if (RequiresProcessScan(policy) &&
            !ExternalProcessDetector.IsSupportedPlatform)
        {
            Emit(DetectionEvidence.ProcessDetectorUnsupported);
        }
    }

    /// <summary>
    /// Ends the current detector generation. A process scan already executing
    /// in the background is allowed to finish, but its generation-bound result
    /// is discarded.
    /// </summary>
    internal void Reset()
    {
        _policy = null;
        unchecked
        {
            _generation++;
        }

        StopAssemblyDetector();
        _inspectedAssemblies.Clear();
        _assemblyReconciliation = Array.Empty<Assembly>();
        _assemblyReconciliationIndex = 0;
        _assemblyReconciliationPass = 0;
        _nextProcessScanTimestamp = long.MaxValue;
        _nextCarryWeightScanTimestamp = long.MaxValue;
        _consecutiveCarryWeightFailures = 0;
        _runtimeWorldReady = false;
        _carryWeightFailureLogged = false;

        lock (_signalGate)
        {
            _signals.Clear();
            _stickyEvidence.Clear();
        }
    }

    internal void Shutdown()
    {
        Reset();
    }

    /// <summary>Updates only live limits; detector history and queued evidence retain their generation.</summary>
    internal void UpdateGameplayLimits(uint generation, float carryWeight, float damage)
    {
        if (_policy == null || generation <= _policyGeneration)
            throw new InvalidOperationException("The detector policy generation is invalid.");
        ProtocolChallengeOptions updated = _policy.WithGameplayLimits(carryWeight, damage);
        _policy = updated;
        _policyGeneration = generation;
        _nextCarryWeightScanTimestamp = 0;
        _nextProcessScanTimestamp = 0;
        if (updated.DetectValheimTooler) BeginAssemblyReconciliation(initialPass: true);
    }

    /// <summary>
    /// Advances detector work on the Unity/main thread. Reflection inspection
    /// is deliberately capped; process enumeration runs only on a worker.
    /// </summary>
    internal void Tick(bool runtimeWorldReady)
    {
        ObserveCompletedProcessScan();

        ProtocolChallengeOptions? policy = _policy;
        if (policy == null)
        {
            return;
        }

        _runtimeWorldReady = runtimeWorldReady;

        if (policy.DetectValheimTooler)
        {
            TickAssemblyDetector();
        }

        if (RequiresProcessScan(policy))
        {
            TickProcessDetector(policy);
        }

        if (runtimeWorldReady && policy.EnforceCarryWeightLimit)
        {
            TickCarryWeightDetector(policy);
        }
        else
        {
            _nextCarryWeightScanTimestamp = 0;
        }
    }

    internal bool ShouldAllowLocalPlayerDamage(HitData? hit)
    {
        ProtocolChallengeOptions? policy = _policy;
        if (policy == null ||
            !_runtimeWorldReady ||
            !policy.EnforceMaximumDamageLimit ||
            hit == null)
        {
            return true;
        }

        Player? localPlayer = Player.m_localPlayer;
        if (localPlayer == null || hit.GetAttacker() != localPlayer)
        {
            return true;
        }

        bool validDamage = GameplayLimitValidation.IsValidDamage(hit);
        if (!GameplayLimitValidation.ShouldBlockDamage(
                hit,
                policy.MaximumDamage,
                policy.AllowAdminCheatCommands &&
                CheatCommandGuard.HasActiveAdminEntitlement()))
        {
            return true;
        }

        Emit(validDamage ? DetectionEvidence.MaximumDamageLimitExceeded :
            DetectionEvidence.InvalidGameplayValue);
        return false;
    }

    internal bool TryDequeue(out ClientDetectionSignal signal)
    {
        lock (_signalGate)
        {
            if (_signals.Count == 0)
            {
                signal = default;
                return false;
            }

            signal = _signals.Dequeue();
            return true;
        }
    }

    private void TickProcessDetector(ProtocolChallengeOptions policy)
    {
        if (!ExternalProcessDetector.IsSupportedPlatform ||
            _processScanTask != null ||
            Stopwatch.GetTimestamp() < _nextProcessScanTimestamp)
        {
            return;
        }

        ExternalProcessScanPolicy scanPolicy = new(
            policy.DetectCheatEngine,
            policy.DetectExternalTools,
            policy.DetectGenericProcessNames);
        _processScanGeneration = _generation;
        _processScanPolicyGeneration = _policyGeneration;
        _nextProcessScanTimestamp = long.MaxValue;
        try
        {
            _processScanTask = Task.Run(
                () => ExternalProcessDetector.Scan(scanPolicy));
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            _processScanTask = null;
            Emit(DetectionEvidence.ProcessDetectorUnavailable);
            ScheduleNextProcessScan(policy);
        }
    }

    private void TickCarryWeightDetector(ProtocolChallengeOptions policy)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextCarryWeightScanTimestamp)
        {
            return;
        }

        _nextCarryWeightScanTimestamp = checked(
            now + CarryWeightScanIntervalTicks);
        try
        {
            Player? player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }

            float effectiveCarryWeight = player.GetMaxCarryWeight();
            float baseCarryWeight = player.m_maxCarryWeight;
            bool exceeded = GameplayLimitValidation.ExceedsCarryWeight(
                    effectiveCarryWeight,
                    baseCarryWeight,
                    policy.MaximumCarryWeight);
            _consecutiveCarryWeightFailures = 0;
            _carryWeightFailureLogged = false;
            if (exceeded)
            {
                Emit(GameplayLimitValidation.IsValidCarryWeight(
                    effectiveCarryWeight, baseCarryWeight)
                    ? DetectionEvidence.CarryWeightLimitExceeded
                    : DetectionEvidence.InvalidGameplayValue);
            }
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            if (_consecutiveCarryWeightFailures <
                CarryWeightFailureWarningThreshold)
            {
                _consecutiveCarryWeightFailures++;
            }

            if (_consecutiveCarryWeightFailures >=
                    CarryWeightFailureWarningThreshold &&
                !_carryWeightFailureLogged)
            {
                _carryWeightFailureLogged = true;
                ServerManagerPlugin.Log.LogWarning(
                    "Carry-weight inspection failed three consecutive times. " +
                    "The client will keep retrying once per second without " +
                    "treating detector failure as cheat evidence. error=" +
                    DetectionLog.QuoteValue(
                        exception.GetType().Name,
                        96));
            }
        }
    }

    private void ObserveCompletedProcessScan()
    {
        Task<ExternalProcessScanResult>? task = _processScanTask;
        if (task == null || !task.IsCompleted)
        {
            return;
        }

        long completedGeneration = _processScanGeneration;
        uint completedPolicyGeneration = _processScanPolicyGeneration;
        _processScanTask = null;

        ExternalProcessScanResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            result = ExternalProcessScanResult.Unavailable;
        }

        ProtocolChallengeOptions? policy = _policy;
        if (policy == null || completedGeneration != _generation)
        {
            if (policy != null && RequiresProcessScan(policy))
            {
                _nextProcessScanTimestamp = 0;
            }

            return;
        }

        foreach (DetectionEvidence evidence in result.Evidence)
        {
            Emit(evidence, completedPolicyGeneration);
        }

        if (result.DetectorUnavailable)
        {
            Emit(DetectionEvidence.ProcessDetectorUnavailable, completedPolicyGeneration);
        }

        if (completedPolicyGeneration == _policyGeneration) ScheduleNextProcessScan(policy);
        else _nextProcessScanTimestamp = 0;
    }

    private void ScheduleNextProcessScan(ProtocolChallengeOptions policy)
    {
        long interval = checked(
            (long)policy.ProcessScanIntervalSeconds * Stopwatch.Frequency);
        _nextProcessScanTimestamp = checked(
            Stopwatch.GetTimestamp() + interval);
    }

    private void StartAssemblyDetector()
    {
        // Subscribe before taking the initial AppDomain snapshot. Anything
        // loaded in between is therefore represented by either the event queue
        // or the snapshot (normally both, and de-duplicated by reference).
        lock (_assemblyQueueGate)
        {
            _acceptAssemblyLoadEvents = true;
        }

        try
        {
            if (!_assemblyLoadSubscribed)
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                _assemblyLoadSubscribed = true;
            }
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            lock (_assemblyQueueGate)
            {
                _acceptAssemblyLoadEvents = false;
            }

            Emit(DetectionEvidence.AssemblyDetectorUnavailable);
        }

        BeginAssemblyReconciliation(initialPass: true);
    }

    private void StopAssemblyDetector()
    {
        lock (_assemblyQueueGate)
        {
            _acceptAssemblyLoadEvents = false;
            _loadedAssemblies.Clear();
            _assemblyQueueOverflowed = false;
        }

        if (_assemblyLoadSubscribed)
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                _assemblyLoadSubscribed = false;
            }
            catch (Exception exception)
                when (!IntegrityCanonical.IsFatal(exception))
            {
                // Reset must remain idempotent. No callback can publish into a
                // disabled generation because the acceptance flag is already
                // false. Retain the subscribed marker so a later Configure
                // does not add a duplicate handler if removal really failed.
            }
        }
    }

    private void OnAssemblyLoad(
        object? sender,
        AssemblyLoadEventArgs eventArgs)
    {
        Assembly? assembly = eventArgs?.LoadedAssembly;
        if (assembly == null)
        {
            return;
        }

        // This callback may run on an arbitrary loader thread. It performs no
        // reflection, Unity access, logging, or networking.
        lock (_assemblyQueueGate)
        {
            if (!_acceptAssemblyLoadEvents)
            {
                return;
            }

            if (_loadedAssemblies.Count >= MaximumQueuedAssemblies)
            {
                _assemblyQueueOverflowed = true;
                return;
            }

            _loadedAssemblies.Enqueue(assembly);
        }
    }

    private void TickAssemblyDetector()
    {
        int candidates = 0;

        // Reserve part of each tick for newly loaded assemblies so an initial
        // reconciliation cannot delay a live injection for many frames.
        while (candidates < ReservedEventCandidatesPerTick &&
               TryTakeLoadedAssembly(out Assembly? loadedAssembly))
        {
            candidates++;
            InspectAssemblyOnce(loadedAssembly);
        }

        while (candidates < MaximumAssemblyCandidatesPerTick &&
               TryTakeReconciliationAssembly(
                   out Assembly? reconciliationAssembly))
        {
            candidates++;
            InspectAssemblyOnce(reconciliationAssembly);
        }

        // If no reconciliation work remains, spend the rest of the bounded
        // budget draining live events.
        while (candidates < MaximumAssemblyCandidatesPerTick &&
               TryTakeLoadedAssembly(out Assembly? loadedAssembly))
        {
            candidates++;
            InspectAssemblyOnce(loadedAssembly);
        }

        AdvanceAssemblyReconciliation();
    }

    private bool TryTakeLoadedAssembly(out Assembly assembly)
    {
        lock (_assemblyQueueGate)
        {
            if (_loadedAssemblies.Count == 0)
            {
                assembly = null!;
                return false;
            }

            assembly = _loadedAssemblies.Dequeue();
            return true;
        }
    }

    private bool TryTakeReconciliationAssembly(out Assembly assembly)
    {
        if (_assemblyReconciliationIndex >=
            _assemblyReconciliation.Length)
        {
            assembly = null!;
            return false;
        }

        assembly =
            _assemblyReconciliation[_assemblyReconciliationIndex++];
        return true;
    }

    private void InspectAssemblyOnce(Assembly assembly)
    {
        if (_inspectedAssemblies.TryGetValue(assembly, out uint inspectedGeneration) &&
            inspectedGeneration == _policyGeneration)
        {
            return;
        }

        _inspectedAssemblies[assembly] = _policyGeneration;

        ValheimToolerInspection result =
            ValheimToolerAssemblyInspector.Inspect(assembly);
        if (result.Detected)
        {
            Emit(DetectionEvidence.ValheimToolerDetected);
        }

        if (result.InspectionIncomplete)
        {
            Emit(DetectionEvidence.AssemblyDetectorUnavailable);
        }
    }

    private void AdvanceAssemblyReconciliation()
    {
        if (_assemblyReconciliationIndex <
            _assemblyReconciliation.Length)
        {
            return;
        }

        if (_assemblyReconciliationPass == 1)
        {
            // Re-snapshot after the initial pass to reconcile assemblies
            // loaded around subscription/snapshot boundaries.
            BeginAssemblyReconciliation(initialPass: false);
            return;
        }

        _assemblyReconciliation = Array.Empty<Assembly>();
        _assemblyReconciliationIndex = 0;
        _assemblyReconciliationPass = 0;

        bool overflowed;
        lock (_assemblyQueueGate)
        {
            overflowed = _assemblyQueueOverflowed;
            _assemblyQueueOverflowed = false;
        }

        if (overflowed)
        {
            // A current AppDomain snapshot recovers assemblies dropped by the
            // bounded event queue.
            BeginAssemblyReconciliation(initialPass: false);
        }
    }

    private void BeginAssemblyReconciliation(bool initialPass)
    {
        try
        {
            _assemblyReconciliation =
                AppDomain.CurrentDomain.GetAssemblies();
            _assemblyReconciliationIndex = 0;
            _assemblyReconciliationPass = initialPass ? 1 : 2;
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            _assemblyReconciliation = Array.Empty<Assembly>();
            _assemblyReconciliationIndex = 0;
            _assemblyReconciliationPass = 0;
            Emit(DetectionEvidence.AssemblyDetectorUnavailable);
        }
    }

    private static bool RequiresProcessScan(ProtocolChallengeOptions policy)
    {
        return policy.DetectCheatEngine ||
               policy.DetectExternalTools ||
               policy.DetectGenericProcessNames;
    }

    private void Emit(DetectionEvidence evidence, uint policyGeneration = 0)
    {
        lock (_signalGate)
        {
            uint observedGeneration = policyGeneration == 0 ? _policyGeneration : policyGeneration;
            if (DetectionEvidenceCatalog.IsSticky(evidence) &&
                _stickyEvidence.TryGetValue(evidence, out uint lastGeneration) &&
                lastGeneration >= observedGeneration)
            {
                return;
            }

            if (DetectionEvidenceCatalog.IsSticky(evidence))
                _stickyEvidence[evidence] = observedGeneration;

            _signals.Enqueue(new ClientDetectionSignal(evidence,
                policyGeneration: observedGeneration));
        }
    }
}
