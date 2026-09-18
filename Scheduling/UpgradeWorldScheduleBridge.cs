using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Bootstrap;
using HarmonyLib;
using ServerManager.Commands;
using ServerManager.Events;

namespace ServerManager;

internal enum UpgradeWorldScheduleState { Pending, Succeeded, Failed, Indeterminate }

internal readonly struct UpgradeWorldScheduleObservation
{
    internal UpgradeWorldScheduleState State { get; }
    internal string Code { get; }
    internal UpgradeWorldScheduleObservation(UpgradeWorldScheduleState state, string code)
    { State = state; Code = code; }
}

// Optional observation adapter: validate the required runtime contract, not a version number.
// Only a server-owned command dispatch can claim an operation. Observers never
// stop, advance or remove Upgrade World's work, including on shutdown/unload.
internal sealed class UpgradeWorldScheduleBridge : IDisposable
{
    private static UpgradeWorldScheduleBridge? _owner;
    private static bool _patchFaulted;
    private static readonly string[] CleanTypes = { "CleanDuplicates", "CleanLocations", "CleanObjects",
        "CleanChests", "CleanStands", "CleanDungeons", "CleanSpawns", "CleanHealth" };
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly Harmony _harmony = new("sighsorry.ServerManager.UpgradeWorldSchedule." + Guid.NewGuid().ToString("N"));
    private readonly Assembly _assembly;
    private readonly MethodInfo _getOperations, _isRoot;
    private readonly FieldInfo _coroutine, _failed, _savingDisabled, _user, _locationPlaced, _entityTotal;
    private readonly Type _entityOperation;
    private readonly Dictionary<string, Type> _operationTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _synchronousCompleted = new(StringComparer.Ordinal);
    private readonly List<TrackedOperation> _operations = new();
    private string[][] _expected = Array.Empty<string[]>();
    private string[] _synchronousExpected = Array.Empty<string>();
    private UpgradeWorldScheduleObservation _observation = new(UpgradeWorldScheduleState.Indeterminate, "uw_not_started");
    private bool _dispatching, _disposed;
    private int _moving;

    // Exact reviewed admission sequences, not an allow-all base-type check.
    // Composite commands may submit several different operations in one call.
    private static readonly Dictionary<string, string[]> Queued = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chests_reset"] = new[] { "ResetChests" },
        ["locations_add"] = new[] { "DistributeLocations", "SpawnLocations" },
        ["locations_remove"] = new[] { "RemoveLocations" },
        ["locations_reset"] = new[] { "RegenerateLocations" },
        ["objects_edit"] = new[] { "EditObjects" },
        ["objects_refresh"] = new[] { "RefreshObjects" },
        ["objects_remove"] = new[] { "RemoveObjects" },
        ["objects_swap"] = new[] { "SwapObjects" },
        ["temple_gen"] = new[] { "TempleVersion" },
        ["vegetation_add"] = new[] { "AddVegetation" },
        ["vegetation_remove"] = new[] { "RemoveVegetation" },
        ["vegetation_reset"] = new[] { "ResetVegetation" },
        ["world_reset"] = new[] { "RemoveLocations", "DistributeLocations", "ResetZones" },
        ["world_gen"] = new[] { "WorldVersion" },
        ["zones_generate"] = new[] { "Generate" },
        ["zones_reset"] = new[] { "ResetZones" },
        ["zones_restore"] = new[] { "RestoreZones" }
    };
    private static readonly Dictionary<string, string[]> Immediate = new(StringComparer.OrdinalIgnoreCase)
    {
        ["world_clean"] = CleanTypes,
        ["clean_duplicates"] = new[] { "CleanDuplicates" }, ["clean_locations"] = new[] { "CleanLocations" },
        ["clean_objects"] = new[] { "CleanObjects" }, ["clean_chests"] = new[] { "CleanChests" },
        ["clean_stands"] = new[] { "CleanStands" }, ["clean_dungeons"] = new[] { "CleanDungeons" },
        ["clean_spawns"] = new[] { "CleanSpawns" }, ["clean_health"] = new[] { "CleanHealth" },
        ["locations_fix"] = new[] { "FixLocations" }, ["locations_swap"] = new[] { "SwapLocations" },
        ["location_register"] = new[] { "RegisterLocation" },
        ["time_change"] = new[] { "ChangeTime" }, ["time_change_day"] = new[] { "ChangeTime" },
        ["time_set"] = new[] { "SetTime" }, ["time_set_day"] = new[] { "SetTime" }
    };

    private sealed class TrackedOperation
    {
        internal readonly object Instance;
        internal bool Initialized, Completed, IteratorCreated;
        internal TrackedOperation(object instance) => Instance = instance;
    }

    private UpgradeWorldScheduleBridge(Assembly assembly)
    {
        _assembly = assembly;
        Type executor = RequireType("Executor"), operation = RequireType("ExecutedOperation");
        _getOperations = RequireMethod(executor, "GetOperations", true, typeof(List<>).MakeGenericType(operation));
        _coroutine = RequireField(executor, "executionCoroutine", true, "UnityEngine.Coroutine");
        _failed = RequireField(operation, "Failed", false, typeof(int).FullName!);
        _savingDisabled = RequireField(RequireType("SavingCommands"), "SavingDisabled", true, typeof(bool).FullName!);
        _user = RequireField(RequireType("ServerExecution"), "User", true, typeof(ZRpc).FullName!);
        _locationPlaced = RequireField(typeof(ZoneSystem.LocationInstance), "m_placed", false, typeof(bool).FullName!);
        _isRoot = RequireMethod(RequireType("Settings"), "IsRoot", true, typeof(bool), typeof(string));
        _entityOperation = RequireType("ExecutedEntityOperation");
        _entityTotal = RequireField(_entityOperation, "TotalCount", false, typeof(int).FullName!);
        foreach (string name in Queued.Values.SelectMany(names => names).Concat(new[] { "Print" }).Distinct())
            _operationTypes.Add(name, RequireType(name));
        foreach (Type concrete in _operationTypes.Values)
            if (!operation.IsAssignableFrom(concrete)) throw new MissingMemberException();

        var patches = new List<(MethodBase Target, string? Prefix, string? Postfix, string? Finalizer)>();
        patches.Add((RequireMethod(executor, "AddOperation", true, typeof(void), operation, typeof(bool)), nameof(BeforeAdd), null, nameof(ObserveException)));
        patches.Add((RequireMethod(executor, "StopExecution", true, typeof(void)), nameof(BeforeStop), null, null));
        patches.Add((RequireMethod(operation, "Init", false, typeof(bool), typeof(bool)), null, nameof(AfterInit), nameof(ObserveOperationException)));
        patches.Add((RequireMethod(operation, "Execute", false, typeof(IEnumerator), typeof(Stopwatch)), null, nameof(AfterExecute), nameof(ObserveOperationException)));
        patches.Add((RequireMethod(RequireType("Helper"), "AddError", true, typeof(void), typeof(Terminal), typeof(string), typeof(bool)), nameof(BeforeError), null, null));
        patches.Add((RequireMethod(RequireType("Helper"), "Print", true, typeof(void), typeof(Terminal), typeof(ZRpc), typeof(string)), nameof(BeforePrintedMessage), null, null));
        // UW catches exceptions thrown while obtaining OnExecute and in OnStart.
        // Observe the actual virtual implementations before those catches; deep
        // iterator wrappers separately cover deferred bodies and OnEnd failures.
        var lifecycle = _operationTypes.Values.SelectMany(type => new[] {
            RequireMethod(type, "OnStart", false, typeof(void)),
            RequireMethod(type, "OnExecute", false, typeof(IEnumerator), typeof(Stopwatch)),
            RequireMethod(type, "OnEnd", false, typeof(void)) });
        foreach (MethodInfo method in lifecycle.GroupBy(method => method.MethodHandle).Select(group => group.First()))
            patches.Add((method, null, null, nameof(ObserveOperationException)));
        foreach (MethodInfo method in _operationTypes.Values.Select(type => RequireMethod(type, "OnInit", false, typeof(string)))
            .GroupBy(method => method.MethodHandle).Select(group => group.First()))
            patches.Add((method, nameof(BeforeOperationInit), null, nameof(ObserveOperationException)));
        foreach (string type in new[] { "RegenerateLocations", "SpawnLocations" })
            patches.Add((RequireMethod(_operationTypes[type], "ExecuteLocation", false, typeof(bool),
                typeof(Vector2i), typeof(ZoneSystem.LocationInstance)), nameof(BeforeLocation), nameof(AfterLocation), nameof(ObserveOperationException)));
        foreach (string clean in CleanTypes)
        {
            Type[] arguments = clean == "CleanDuplicates"
                ? new[] { typeof(Terminal), typeof(bool), typeof(bool) }
                : new[] { typeof(Terminal), typeof(ZDO[]), typeof(bool), typeof(bool) };
            ConstructorInfo constructor = RequireType(clean).GetConstructor(arguments) ?? throw new MissingMethodException();
            patches.Add((constructor, null, nameof(AfterImmediate), nameof(ObserveException)));
        }
        foreach (var contract in new[] {
            ("FixLocations", new[] { typeof(Terminal), RequireType("FiltererParameters") }),
            ("SwapLocations", new[] { typeof(Terminal), typeof(IEnumerable<string>), RequireType("DataParameters") }),
            ("RegisterLocation", new[] { typeof(Terminal), typeof(string), typeof(UnityEngine.Vector3) }),
            ("ChangeTime", new[] { typeof(Terminal), typeof(double) }),
            ("SetTime", new[] { typeof(Terminal), typeof(double) }) })
            patches.Add((RequireType(contract.Item1).GetConstructor(contract.Item2) ?? throw new MissingMethodException(),
                null, nameof(AfterImmediate), nameof(ObserveException)));
        // Fully validate before installing anything. Do not pin metadata tokens,
        // file hashes or MVIDs, which change without changing these contracts.
        _owner = this;
        try
        {
            foreach (var patch in patches)
                _harmony.Patch(patch.Target, prefix: Hook(patch.Prefix), postfix: Hook(patch.Postfix), finalizer: Hook(patch.Finalizer));
        }
        catch { Dispose(); throw; }
    }

    internal static bool IsMaintenanceCommand(string command) => UpgradeWorldScheduleCommands.IsMaintenance(command);

    internal static bool TryCreate(out UpgradeWorldScheduleBridge? bridge, out string code)
    {
        bridge = null;
        code = "uw_missing";
        if (_patchFaulted) { code = "uw_observer_cleanup_failed"; return false; }
        if (_owner != null) { code = "uw_observer_busy"; return false; }
        if (!Chainloader.PluginInfos.TryGetValue("upgrade_world", out var plugin) || plugin.Instance == null) return false;
        try { bridge = new UpgradeWorldScheduleBridge(plugin.Instance.GetType().Assembly); code = "uw_available"; return true; }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { code = _patchFaulted ? "uw_observer_cleanup_failed" : "uw_unsupported_contract"; return false; }
    }

    internal bool CanRun(out string code)
    {
        code = "uw_unavailable";
        if (!IsCurrent) return false;
        if (ZNet.instance == null || !ZNet.instance.IsServer()) { code = "uw_server_required"; return false; }
        try
        {
            if (_user.GetValue(null) != null) { code = "uw_foreign_user"; return false; }
            if ((bool)_savingDisabled.GetValue(null)!) { code = "uw_saving_disabled"; return false; }
            // Match UW's dedicated-console rule. Listen hosts go through UW's
            // ordinary local authority checks without inventing a root user.
            if (ZNet.instance.IsDedicated() && !(bool)_isRoot.Invoke(null, new object[] { "-1" })!)
            { code = "uw_root_denied"; return false; }
            code = "uw_available"; return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { code = "uw_observation_failed"; return false; }
    }

    internal bool TryGetIdle(out bool idle, out string code)
    {
        idle = false;
        if (!CanRun(out code)) return false;
        try
        {
            idle = Operations.Count == 0 && _coroutine.GetValue(null) == null;
            code = idle ? "uw_idle" : "uw_busy";
            return true;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { code = "uw_observation_failed"; return false; }
    }

    internal UpgradeWorldScheduleObservation Begin(string command, Func<ServerManagerCommandResult> dispatch)
    {
        if (!TryArguments(command, out string[] arguments) || !IsMaintenanceCommand(command))
            return new(UpgradeWorldScheduleState.Failed, "uw_unsupported_command");
        bool immediate = Immediate.TryGetValue(arguments[0], out string[] synchronous);
        string[][] expected = ExpectedOperations(command, arguments[0]);
        if (!immediate && expected.Length == 0)
            return new(UpgradeWorldScheduleState.Failed, "uw_unsupported_command");
        if (!immediate && !arguments.Skip(1).Contains("start", StringComparer.OrdinalIgnoreCase))
            return new(UpgradeWorldScheduleState.Failed, "uw_start_required");
        if (_observation.State == UpgradeWorldScheduleState.Pending)
            return new(UpgradeWorldScheduleState.Indeterminate, "uw_observer_busy");
        if (!TryGetIdle(out bool idle, out string code) || !idle)
            return new(UpgradeWorldScheduleState.Failed, code);
        // LPA's separately versioned placement API has no reviewed completion
        // result here. Do not silently certify a handoff to an unknown plugin.
        if (expected.Any(names => names.Contains("DistributeLocations")) &&
            Chainloader.PluginInfos.TryGetValue("nickpappas.locationplacementaccelerator", out var lpa) && lpa.Instance != null)
            return new(UpgradeWorldScheduleState.Failed, "uw_lpa_unverified");
        _operations.Clear(); _synchronousCompleted.Clear();
        _expected = expected; _synchronousExpected = synchronous ?? Array.Empty<string>();
        _observation = new(UpgradeWorldScheduleState.Pending, "uw_pending");
        _dispatching = true;
        try
        {
            ServerManagerCommandResult result = dispatch();
            if (!result.Success) Mark(UpgradeWorldScheduleState.Failed, "uw_dispatch_failed");
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { Mark(UpgradeWorldScheduleState.Failed, "uw_dispatch_exception"); }
        finally { _dispatching = false; }
        if (_observation.State != UpgradeWorldScheduleState.Pending) return _observation;
        if (immediate)
        {
            // Every expected synchronous constructor must return normally.
            // Error output is only a failure signal, never evidence of success.
            if (!_synchronousExpected.All(_synchronousCompleted.Contains))
                Mark(UpgradeWorldScheduleState.Indeterminate, "uw_synchronous_unconfirmed");
        }
        else if (!_expected.Any(sequence => SequenceMatches(sequence, exact: true)) || _operations.Any(item => !item.Initialized))
            Mark(UpgradeWorldScheduleState.Indeterminate, "uw_operation_not_admitted");
        else if (_operations.Any(item => !item.Completed) && _coroutine.GetValue(null) == null)
            Mark(UpgradeWorldScheduleState.Indeterminate, "uw_execution_not_started");
        return Poll();
    }

    internal UpgradeWorldScheduleObservation Poll()
    {
        if (!IsCurrent) return new(UpgradeWorldScheduleState.Indeterminate, "uw_observer_retired");
        if (_observation.State != UpgradeWorldScheduleState.Pending) return _observation;
        if (!CanRun(out string code)) { Mark(UpgradeWorldScheduleState.Indeterminate, code); return _observation; }
        try
        {
            if (Operations.Cast<object>().Any(item => Find(item) == null))
                Mark(UpgradeWorldScheduleState.Indeterminate, "uw_foreign_operation");
            else if (_operations.Any(item => (int)_failed.GetValue(item.Instance)! != 0))
                Mark(UpgradeWorldScheduleState.Failed, "uw_partial_failure");
            else if (Completed && Operations.Count == 0 && _coroutine.GetValue(null) == null)
                _observation = new(UpgradeWorldScheduleState.Succeeded, "uw_operation_completed");
            else if (_operations.Any(item => !item.Completed && !Operations.Contains(item.Instance)))
                Mark(UpgradeWorldScheduleState.Indeterminate, "uw_operation_disappeared");
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { Mark(UpgradeWorldScheduleState.Indeterminate, "uw_observation_failed"); }
        return _observation;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(_owner, this)) _owner = null;
        // Already-yielded wrappers still forward their original IEnumerator.
        // Retiring must not cancel another mod's world mutations.
        try { _harmony.UnpatchSelf(); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { _patchFaulted = true; }
    }

    private bool IsCurrent => !_disposed && ReferenceEquals(_owner, this) && _thread == Thread.CurrentThread.ManagedThreadId;
    private bool Completed => _synchronousExpected.Length != 0
        ? _synchronousExpected.All(_synchronousCompleted.Contains)
        : _operations.Count != 0 && _operations.All(item => item.Initialized && item.Completed);
    private bool Observing => IsCurrent && _observation.State == UpgradeWorldScheduleState.Pending;
    private IList Operations => (IList)(_getOperations.Invoke(null, Array.Empty<object>()) ?? throw new InvalidOperationException());
    private void Mark(UpgradeWorldScheduleState state, string code)
    { if (_observation.State == UpgradeWorldScheduleState.Pending) _observation = new(state, code); }
    private static UpgradeWorldScheduleBridge? Observer => _owner is { Observing: true } value ? value : null;

    private TrackedOperation? Find(object value) => _operations.FirstOrDefault(item => ReferenceEquals(item.Instance, value));
    private bool SequenceMatches(string[] expected, bool exact)
    {
        if (_operations.Count > expected.Length || (exact && _operations.Count != expected.Length)) return false;
        for (int i = 0; i < _operations.Count; i++)
            if (_operations[i].Instance.GetType() != _operationTypes[expected[i]]) return false;
        return true;
    }

    private static string[][] ExpectedOperations(string command, string verb)
    {
        if (Queued.TryGetValue(verb, out string[] sequence)) return new[] { sequence };
        if (verb != "upgrade") return Array.Empty<string[]>();
        string type = UpgradeWorldScheduleCommands.GetUpgradeType(command);
        return UpgradeSequences.TryGetValue(type, out string[][] sequences) ? sequences : Array.Empty<string[]>();
    }
    private static readonly Dictionary<string, string[][]> UpgradeSequences = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tarpits"] = new[] { new[] { "DistributeLocations", "SpawnLocations" } },
        ["mountain_caves"] = new[] { new[] { "DistributeLocations", "SpawnLocations" } },
        ["hildir"] = new[] { new[] { "DistributeLocations", "SpawnLocations" } },
        ["bogwitch"] = new[] { new[] { "DistributeLocations", "SpawnLocations" } },
        ["combatruins"] = new[] { new[] { "DistributeLocations", "SpawnLocations" } },
        ["mistlands"] = new[] { new[] { "ResetZones", "DistributeLocations" } },
        ["mistlands_worldgen"] = new[] { new[] { "WorldVersion", "RemoveLocations", "DistributeLocations", "ResetZones", "Print" } },
        ["hh_worldgen"] = new[] { new[] { "WorldVersion", "Print" } },
        ["legacy_worldgen"] = new[] { new[] { "WorldVersion" } },
        ["ashlands"] = new[] { new[] { "RemoveLocations", "ResetZones", "DistributeLocations", "TempleVersion" },
            new[] { "RemoveLocations", "ResetZones", "TempleVersion" } },
        ["deepnorth"] = new[] { new[] { "RemoveLocations", "ResetZones", "DistributeLocations" },
            new[] { "RemoveLocations", "ResetZones" } }
    };

    private static void BeforeAdd(object __0)
    {
        var owner = Observer;
        if (owner == null) return;
        if (!owner._dispatching || owner._operations.Count >= 16 || owner.Find(__0) != null)
        { owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_foreign_operation"); return; }
        owner._operations.Add(new TrackedOperation(__0));
        if (!owner._expected.Any(sequence => owner.SequenceMatches(sequence, exact: false)))
            owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_foreign_operation");
    }

    private static void AfterInit(object __instance, bool __result)
    {
        var owner = Observer;
        TrackedOperation? operation = owner?.Find(__instance);
        if (owner == null || operation == null) return;
        if (operation.Initialized) { owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_duplicate_initialization"); return; }
        operation.Initialized = true;
        if (!__result)
        {
            // Only the reviewed entity OnInit returns false for a legitimate
            // empty selection. A generic false Init is not a completion result.
            try { operation.Completed = owner._entityOperation.IsInstanceOfType(__instance) && (int)owner._entityTotal.GetValue(__instance)! == 0; }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { }
            if (!operation.Completed) owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_operation_not_admitted");
        }
    }

    private static void BeforeOperationInit(object __instance)
    {
        var owner = Observer;
        // In 1.80 GetInfo (uw_check) calls OnInit again, rebuilding selections
        // and counters without resetting execution progress. It is not a pure
        // inspection. Do not interfere with UW, but retire our completion proof.
        if (owner?.Find(__instance) is { Initialized: true })
            owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_foreign_inspection");
    }

    private static void AfterExecute(object __instance, ref IEnumerator __result)
    {
        var owner = Observer;
        TrackedOperation? operation = owner?.Find(__instance);
        if (owner != null && operation != null)
        {
            if (operation.IteratorCreated || operation.Completed || __result == null)
            { owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_duplicate_execution"); return; }
            operation.IteratorCreated = true;
            __result = new ObservedEnumerator(owner, operation, __result, true, 0);
        }
    }

    private static void BeforeStop()
    {
        var owner = Observer;
        if (owner == null) return;
        // Normal 1.80 completion calls StopExecution only after dequeueing the
        // completed iterator. Manual stop with any queued work is not success.
        try
        {
            if (!owner.Completed || owner.Operations.Count != 0)
                owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_stopped");
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_observation_failed"); }
    }

    private static void BeforeError()
    {
        var owner = Observer;
        if (owner != null && (owner._dispatching || owner._moving != 0))
            owner.Mark(UpgradeWorldScheduleState.Failed, "uw_reported_error");
    }

    private static void BeforePrintedMessage(string __2)
    {
        var owner = Observer;
        if (owner == null || (!owner._dispatching && owner._moving == 0)) return;
        string text = (__2 ?? "").TrimStart();
        // These are UW's reviewed non-throwing error paths (including location
        // generation exhaustion). Successful text is deliberately ignored.
        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Failed to place all ", StringComparison.Ordinal))
            owner.Mark(UpgradeWorldScheduleState.Failed, "uw_reported_error");
    }

    private static Exception? ObserveException(Exception? __exception)
    {
        var owner = Observer;
        if (__exception != null && owner != null && (owner._dispatching || owner._moving != 0))
            owner.Mark(UpgradeWorldScheduleState.Failed, "uw_execution_exception");
        return __exception;
    }

    private static Exception? ObserveOperationException(object __instance, Exception? __exception)
    {
        var owner = Observer;
        if (__exception != null && owner != null && owner.Find(__instance) != null)
            owner.Mark(UpgradeWorldScheduleState.Failed, "uw_execution_exception");
        return __exception;
    }

    private static void BeforeLocation(object __instance, object __1, out bool __state)
    {
        __state = false;
        var owner = Observer;
        if (owner == null || owner.Find(__instance) == null) return;
        try { __state = (bool)owner._locationPlaced.GetValue(__1)!; }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_location_unobservable"); }
    }

    private static void AfterLocation(object __instance, bool __result, bool __state)
    {
        var owner = Observer;
        // Reset skips unplaced locations; spawn skips already-placed locations.
        // The opposite false result means a missing location prefab was skipped.
        // Capture placement before UW changes it; Failed alone misses this case.
        bool shouldHaveRun = __instance.GetType().Name == "RegenerateLocations" ? __state : !__state;
        if (!__result && shouldHaveRun && owner != null && owner.Find(__instance) != null)
            owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_location_skipped");
    }

    private static void AfterImmediate(object __instance)
    {
        var owner = Observer;
        if (owner == null || !owner._dispatching) return;
        string name = __instance.GetType().Name;
        if (__instance.GetType().Assembly != owner._assembly || !owner._synchronousExpected.Contains(name) || !owner._synchronousCompleted.Add(name))
            owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_foreign_operation");
    }

    private sealed class ObservedEnumerator : IEnumerator, IDisposable
    {
        private readonly UpgradeWorldScheduleBridge _owner;
        private readonly TrackedOperation _operation;
        private readonly IEnumerator _inner;
        private readonly bool _root;
        private readonly int _depth;
        private bool _ended;
        private object? _current;
        internal ObservedEnumerator(UpgradeWorldScheduleBridge owner, TrackedOperation operation, IEnumerator inner, bool root, int depth)
        { _owner = owner; _operation = operation; _inner = inner; _root = root; _depth = depth; }
        public object? Current => _current;
        public bool MoveNext()
        {
            _owner._moving++;
            try
            {
                bool more = _inner.MoveNext();
                if (!more) { _ended = true; if (_root) _operation.Completed = true; _current = null; return false; }
                _current = _inner.Current;
                if (_current is IEnumerator child)
                {
                    if (_depth >= 32 || ReferenceEquals(child, _inner))
                        _owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_iterator_unobservable");
                    else _current = new ObservedEnumerator(_owner, _operation, child, false, _depth + 1);
                }
                return true;
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            { _owner.Mark(UpgradeWorldScheduleState.Failed, "uw_iterator_exception"); throw; }
            finally { _owner._moving--; }
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose()
        {
            if (!_ended) _owner.Mark(UpgradeWorldScheduleState.Indeterminate, "uw_iterator_disposed");
            try { (_inner as IDisposable)?.Dispose(); }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            { _owner.Mark(UpgradeWorldScheduleState.Failed, "uw_iterator_exception"); throw; }
        }
    }

    private static bool TryArguments(string command, out string[] arguments)
    {
        if (!ServerCommands.TryTokenize(command, out arguments, out _) || arguments.Length == 0) return false;
        arguments[0] = arguments[0].ToLowerInvariant();
        return true;
    }
    private Type RequireType(string name) => _assembly.GetType("UpgradeWorld." + name, true)!;
    private static HarmonyMethod? Hook(string? name) => name == null ? null : new HarmonyMethod(typeof(UpgradeWorldScheduleBridge), name);
    private static FieldInfo RequireField(Type type, string name, bool isStatic, string fieldType)
    {
        FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance)) ?? throw new MissingFieldException();
        if (field.IsStatic != isStatic || field.FieldType.FullName != fieldType) throw new MissingFieldException();
        return field;
    }
    private static MethodInfo RequireMethod(Type type, string name, bool isStatic, Type returns, params Type[] arguments)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, arguments, null) ?? throw new MissingMethodException();
        if (method.IsStatic != isStatic || method.ReturnType != returns || method.IsAbstract || method.ContainsGenericParameters) throw new MissingMethodException();
        return method;
    }
}
