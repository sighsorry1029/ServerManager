using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using ServerManager;
using ServerManager.Events;

internal static class UpgradeWorldScheduleSmoke
{
    private static int _checks;
    private static readonly ServerManagerCommandResult Success = new(true, "rcon_dispatched");
    private static int Main()
    {
        try
        {
            Check(!UpgradeWorldScheduleBridge.TryCreate(out _, out string absent) && absent == "uw_missing", "Absent dependency");
            BepInEx.Bootstrap.Chainloader.PluginInfos["upgrade_world"] = new();
            var plugin = BepInEx.Bootstrap.Chainloader.PluginInfos["upgrade_world"];
            plugin.Metadata.Version = new Version(1, 82);
            Check(UpgradeWorldScheduleBridge.TryCreate(out var bridge, out _), "Compatible contract accepted regardless of plugin version");
            using (bridge!)
            {
                Check(HarmonyLib.Harmony.Patched.Count >= 15, "All observation targets registered");
                Check(!UpgradeWorldScheduleBridge.TryCreate(out _, out _), "Single observer ownership");
                Check(bridge!.CanRun(out _), "Dedicated default root allowed");
                UpgradeWorld.Settings.Root = false;
                Check(!bridge.CanRun(out string denied) && denied == "uw_root_denied", "No root impersonation");
                ZNet.instance!.Dedicated = false;
                Check(bridge.CanRun(out _), "Listen host keeps UW local rule");
                ZNet.instance.Dedicated = true; UpgradeWorld.Settings.Root = true;
                UpgradeWorld.SavingCommands.SavingDisabled = true;
                Check(!bridge.CanRun(out _), "Disabled saving blocks maintenance");
                UpgradeWorld.SavingCommands.SavingDisabled = false;
                UpgradeWorld.ServerExecution.User = new ZRpc();
                Check(!bridge.CanRun(out _), "No borrowed RPC caller");
                UpgradeWorld.ServerExecution.User = null;
                Check(bridge.Begin("zones_reset", () => throw new Exception()).Code == "uw_start_required", "Explicit start required");
                Check(bridge.Begin("upgrade onions start", () => throw new Exception()).Code == "uw_unsupported_command", "Broken upstream command never dispatched");
                Normal(bridge); FailedCounter(bridge); NestedException(bridge); SwallowedStart(bridge);
                ManualStop(bridge); MissingLocation(bridge); ForeignOperation(bridge); NotAdmitted(bridge); Clean(bridge);
                Composite(bridge); AllReviewedShapes(bridge); EntityNoOp(bridge); Immediate(bridge); GeneralizedFailures(bridge); ForeignInspection(bridge);
                Retire(bridge);
            }
            Check(HarmonyLib.Harmony.Patched.Count == 0, "Observers removed on dispose");
            Check(UpgradeWorld.Executor.StopCalls == 0, "Adapter never stops UW work");
            Console.WriteLine("PASS: Upgrade World bridge " + _checks + " assertions (source-linked iterator/lifecycle tests; no world mutation).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static void Check(bool condition, string reason) { _checks++; if (!condition) throw new Exception(reason); }
    private static object? Hook(string name, params object?[] arguments) =>
        typeof(UpgradeWorldScheduleBridge).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);
    private static IEnumerator Track(UpgradeWorld.ExecutedOperation operation, IEnumerator body)
    {
        Hook("BeforeAdd", operation); Hook("AfterInit", operation, true);
        UpgradeWorld.Executor.Work.Add(operation); UpgradeWorld.Executor.Busy = new UnityEngine.Coroutine();
        object?[] arguments = { operation, body };
        Hook("AfterExecute", arguments);
        return (IEnumerator)arguments[1]!;
    }
    private static IEnumerable<object?> Nested()
    { yield return One().GetEnumerator(); yield return null; }
    private static IEnumerable<object?> One() { yield return null; }
    private static IEnumerable<object?> Throwing()
    { yield return One().GetEnumerator(); throw new InvalidOperationException("test"); }
    private static void Drain(IEnumerator iterator)
    { while (iterator.MoveNext()) if (iterator.Current is IEnumerator child) Drain(child); }
    private static void Empty() { UpgradeWorld.Executor.Work.Clear(); UpgradeWorld.Executor.Busy = null; }
    private static void Normal(UpgradeWorldScheduleBridge bridge)
    {
        IEnumerator? iterator = null;
        var state = bridge.Begin("zones_reset start", () => { iterator = Track(new UpgradeWorld.ResetZones(), Nested().GetEnumerator()); return Success; });
        Check(state.State == UpgradeWorldScheduleState.Pending, "Submitted is not completed");
        Drain(iterator!);
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Pending, "Iterator end is not executor drain");
        UpgradeWorld.Executor.Work.Clear(); Hook("BeforeStop"); UpgradeWorld.Executor.Busy = null;
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Succeeded, "Nested full completion and executor drain");
    }
    private static void FailedCounter(UpgradeWorldScheduleBridge bridge)
    {
        IEnumerator? iterator = null; var operation = new UpgradeWorld.ResetVegetation();
        bridge.Begin("vegetation_reset Copper start", () => { iterator = Track(operation, One().GetEnumerator()); return Success; });
        Drain(iterator!); operation.SetFailed(1); Empty();
        Check(bridge.Poll().Code == "uw_partial_failure", "Failed zones never reported successful");
    }
    private static void NestedException(UpgradeWorldScheduleBridge bridge)
    {
        IEnumerator? iterator = null;
        bridge.Begin("zones_reset start", () => { iterator = Track(new UpgradeWorld.ResetZones(), Throwing().GetEnumerator()); return Success; });
        try { Drain(iterator!); throw new Exception("Exception swallowed by observer"); }
        catch (InvalidOperationException) { _checks++; }
        Empty(); Check(bridge.Poll().Code == "uw_iterator_exception", "Iterator exception recorded");
    }
    private static void SwallowedStart(UpgradeWorldScheduleBridge bridge)
    {
        var operation = new UpgradeWorld.ResetZones();
        bridge.Begin("zones_reset start", () =>
        {
            Track(operation, One().GetEnumerator());
            var error = new InvalidOperationException("caught upstream");
            Check(ReferenceEquals(Hook("ObserveOperationException", operation, error), error), "Observer preserves exception");
            return Success;
        });
        Empty(); Check(bridge.Poll().Code == "uw_execution_exception", "Swallowed OnStart exception recorded");
    }
    private static void ManualStop(UpgradeWorldScheduleBridge bridge)
    {
        bridge.Begin("zones_reset start", () => { Track(new UpgradeWorld.ResetZones(), One().GetEnumerator()); return Success; });
        Hook("BeforeStop"); Empty();
        Check(bridge.Poll().Code == "uw_stopped", "Manual stop not normal queueempty");
    }
    private static void MissingLocation(UpgradeWorldScheduleBridge bridge)
    {
        var operation = new UpgradeWorld.RegenerateLocations();
        bridge.Begin("locations_reset start", () => { Track(operation, One().GetEnumerator()); return Success; });
        object?[] arguments = { operation, new ZoneSystem.LocationInstance { m_placed = true }, false };
        Hook("BeforeLocation", arguments);
        Check((bool)arguments[2]!, "Placed location captured before upstream mutation");
        Hook("AfterLocation", operation, false, arguments[2]); Empty();
        Check(bridge.Poll().Code == "uw_location_skipped", "Skipped/missing location not success");

        IEnumerator? iterator = null;
        bridge.Begin("locations_reset start", () => { iterator = Track(operation, One().GetEnumerator()); return Success; });
        arguments = new object?[] { operation, new ZoneSystem.LocationInstance { m_placed = false }, true };
        Hook("BeforeLocation", arguments); Hook("AfterLocation", operation, false, arguments[2]);
        Drain(iterator!); Empty();
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Succeeded, "Known already-unplaced no-op does not turn a completed batch into a false alarm");
    }
    private static void ForeignOperation(UpgradeWorldScheduleBridge bridge)
    {
        bridge.Begin("zones_reset start", () => { Track(new UpgradeWorld.ResetZones(), One().GetEnumerator()); return Success; });
        Hook("BeforeAdd", new UpgradeWorld.ResetZones());
        Check(bridge.Poll().Code == "uw_foreign_operation", "Foreign submission retires proof");
        Empty();
        bridge.Begin("zones_reset start", () => { Track(new UpgradeWorld.ResetZones(), One().GetEnumerator()); return Success; });
        UpgradeWorld.Executor.Work.Add(new UpgradeWorld.ResetZones());
        Check(bridge.Poll().Code == "uw_foreign_operation", "Direct foreign queue insertion retires proof"); Empty();
    }
    private static void NotAdmitted(UpgradeWorldScheduleBridge bridge)
    {
        Check(bridge.Begin("zones_reset start", () => Success).Code == "uw_operation_not_admitted", "Text/dispatch success without admission not success");
        bridge.Begin("zones_reset start", () => { Hook("BeforeAdd", new UpgradeWorld.ResetZones()); return Success; });
        Check(bridge.Poll().Code == "uw_operation_not_admitted", "Init false not confirmed empty work");
        bridge.Begin("zones_reset start", () => { Hook("BeforeError"); return Success; });
        Check(bridge.Poll().Code == "uw_reported_error", "Swallowed console validation error observed");
    }
    private static void Clean(UpgradeWorldScheduleBridge bridge)
    {
        Check(bridge.Begin("world_clean", () => Success).Code == "uw_synchronous_unconfirmed", "World cleaned text alone insufficient");
        Check(bridge.Begin("world_clean", () =>
        {
            foreach (var name in new[] { "CleanDuplicates", "CleanLocations", "CleanObjects", "CleanChests", "CleanStands", "CleanDungeons", "CleanSpawns", "CleanHealth" })
                Hook("AfterImmediate", System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UpgradeWorld.CleanDuplicates).Assembly.GetType("UpgradeWorld." + name)!));
            return Success;
        }).State == UpgradeWorldScheduleState.Succeeded, "All synchronous cleanup components confirmed");
    }
    private static void Composite(UpgradeWorldScheduleBridge bridge)
    {
        var iterators = new List<IEnumerator>();
        var state = bridge.Begin("upgrade mistlands_worldgen start", () =>
        {
            foreach (var operation in new UpgradeWorld.ExecutedOperation[] { new UpgradeWorld.WorldVersion(), new UpgradeWorld.RemoveLocations(),
                new UpgradeWorld.DistributeLocations(), new UpgradeWorld.ResetZones(), new UpgradeWorld.Print() })
                iterators.Add(Track(operation, Nested().GetEnumerator()));
            return Success;
        });
        Check(state.State == UpgradeWorldScheduleState.Pending, "Composite owns all five admissions");
        for (int i = 0; i < iterators.Count; i++)
        {
            Drain(iterators[i]); UpgradeWorld.Executor.Work.RemoveAt(0);
            Check(bridge.Poll().State == UpgradeWorldScheduleState.Pending, "Composite waits every owned iterator and executor shutdown");
        }
        Hook("BeforeStop"); UpgradeWorld.Executor.Busy = null;
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Succeeded, "Composite completed exactly once");

        bridge.Begin("world_reset start", () => { Track(new UpgradeWorld.RemoveLocations(), One().GetEnumerator()); return Success; });
        Check(bridge.Poll().Code == "uw_operation_not_admitted", "Partial composite dispatch cannot certify success"); Empty();
        bridge.Begin("locations_add start", () =>
        {
            Track(new UpgradeWorld.DistributeLocations(), One().GetEnumerator());
            Track(new UpgradeWorld.ResetZones(), One().GetEnumerator()); return Success;
        });
        Check(bridge.Poll().Code == "uw_foreign_operation", "Known type in wrong command still foreign"); Empty();
        BepInEx.Bootstrap.Chainloader.PluginInfos["nickpappas.locationplacementaccelerator"] = new();
        Check(bridge.Begin("locations_add start", () => throw new Exception()).Code == "uw_lpa_unverified", "Unknown optional placement contract refused before mutation");
        BepInEx.Bootstrap.Chainloader.PluginInfos.Remove("nickpappas.locationplacementaccelerator");
    }
    private static void EntityNoOp(UpgradeWorldScheduleBridge bridge)
    {
        var empty = new UpgradeWorld.RemoveObjects();
        Check(bridge.Begin("objects_remove Stone start", () => { Hook("BeforeAdd", empty); Hook("AfterInit", empty, false); return Success; }).State ==
            UpgradeWorldScheduleState.Succeeded, "Entity Init false with verified zero selection is a successful no-op");
        var nonempty = new UpgradeWorld.RemoveObjects(); nonempty.Total = 1;
        Check(bridge.Begin("objects_remove Stone start", () => { Hook("BeforeAdd", nonempty); Hook("AfterInit", nonempty, false); return Success; }).Code ==
            "uw_operation_not_admitted", "Unexplained rejection never treated as no-op");
    }
    private static void AllReviewedShapes(UpgradeWorldScheduleBridge bridge)
    {
        var queued = (Dictionary<string, string[]>)typeof(UpgradeWorldScheduleBridge).GetField("Queued", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var recipes = (Dictionary<string, string[][]>)typeof(UpgradeWorldScheduleBridge).GetField("UpgradeSequences", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var shapes = queued.Select(pair => (pair.Key + (pair.Key == "temple_gen" || pair.Key == "world_gen" ? " mistlands start" : " start"), pair.Value))
            .Concat(recipes.SelectMany(pair => pair.Value.Select(sequence => ("upgrade " + pair.Key + " start", sequence))));
        foreach (var shape in shapes)
        {
            var iterators = new List<IEnumerator>();
            Check(bridge.Begin(shape.Item1, () =>
            {
                foreach (string name in shape.Item2)
                {
                    var operation = (UpgradeWorld.ExecutedOperation)Activator.CreateInstance(typeof(UpgradeWorld.UpgradeWorld).Assembly.GetType("UpgradeWorld." + name)!)!;
                    iterators.Add(Track(operation, One().GetEnumerator()));
                }
                return Success;
            }).State == UpgradeWorldScheduleState.Pending, "Reviewed exact queue shape admitted: " + shape.Item1);
            foreach (var iterator in iterators) { Drain(iterator); UpgradeWorld.Executor.Work.RemoveAt(0); }
            Hook("BeforeStop"); UpgradeWorld.Executor.Busy = null;
            Check(bridge.Poll().State == UpgradeWorldScheduleState.Succeeded, "Reviewed queue shape completed: " + shape.Item1);
        }
        var synchronous = (Dictionary<string, string[]>)typeof(UpgradeWorldScheduleBridge).GetField("Immediate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var pair in synchronous)
            Check(bridge.Begin(pair.Key, () =>
            {
                foreach (string name in pair.Value)
                    Hook("AfterImmediate", System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UpgradeWorld.UpgradeWorld).Assembly.GetType("UpgradeWorld." + name)!));
                return Success;
            }).State == UpgradeWorldScheduleState.Succeeded, "Reviewed synchronous shape completed: " + pair.Key);
    }
    private static void Immediate(UpgradeWorldScheduleBridge bridge)
    {
        foreach (var pair in new[] { ("clean_objects", "CleanObjects"), ("locations_fix", "FixLocations"),
            ("locations_swap Crypt1 Crypt2", "SwapLocations"), ("location_register Crypt1 pos=0,0,0", "RegisterLocation"),
            ("time_change 100", "ChangeTime"), ("time_set 100", "SetTime") })
        {
            Check(bridge.Begin(pair.Item1, () =>
            {
                Hook("AfterImmediate", System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UpgradeWorld.UpgradeWorld).Assembly.GetType("UpgradeWorld." + pair.Item2)!));
                return Success;
            }).State == UpgradeWorldScheduleState.Succeeded, "Reviewed synchronous constructor: " + pair.Item1);
        }
        Check(bridge.Begin("locations_fix", () => { Hook("BeforePrintedMessage", "Everything complete"); return Success; }).Code ==
            "uw_synchronous_unconfirmed", "Success output without constructor is not proof");
        Check(bridge.Begin("time_set -100", () =>
        {
            Hook("BeforePrintedMessage", "Error: New time would be negative.");
            Hook("AfterImmediate", new UpgradeWorld.SetTime(new Terminal(), -100)); return Success;
        }).Code == "uw_reported_error", "Nonthrowing immediate error overrides constructor completion");
    }
    private static void GeneralizedFailures(UpgradeWorldScheduleBridge bridge)
    {
        var spawn = new UpgradeWorld.SpawnLocations();
        bridge.Begin("locations_add start", () =>
        {
            Track(new UpgradeWorld.DistributeLocations(), One().GetEnumerator()); Track(spawn, One().GetEnumerator()); return Success;
        });
        object?[] args = { spawn, new ZoneSystem.LocationInstance { m_placed = false }, true };
        Hook("BeforeLocation", args); Hook("AfterLocation", spawn, false, args[2]);
        Check(bridge.Poll().Code == "uw_location_skipped", "Missing spawn prefab fails rather than completed queue"); Empty();
        bridge.Begin("objects_edit Stone start", () =>
        {
            var operation = new UpgradeWorld.EditObjects(); Track(operation, One().GetEnumerator());
            Hook("ObserveOperationException", operation, new InvalidOperationException("OnExecute factory caught upstream")); return Success;
        });
        Check(bridge.Poll().Code == "uw_execution_exception", "Concrete OnExecute factory failure observed"); Empty();
        bridge.Begin("upgrade bogwitch start", () =>
        {
            Track(new UpgradeWorld.DistributeLocations(), One().GetEnumerator());
            Hook("BeforePrintedMessage", "Failed to place all BogWitch_Camp, placed 0 out of 1."); return Success;
        });
        Check(bridge.Poll().Code == "uw_reported_error", "Known location placement exhaustion is a failure signal"); Empty();
        bridge.Begin("zones_reset start", () =>
        {
            var operation = new UpgradeWorld.ResetZones(); Track(operation, One().GetEnumerator());
            object?[] args2 = { operation, One().GetEnumerator() }; Hook("AfterExecute", args2); return Success;
        });
        Check(bridge.Poll().Code == "uw_duplicate_execution", "Repeated iterator creation cannot certify one occurrence"); Empty();
    }
    private static void ForeignInspection(UpgradeWorldScheduleBridge bridge)
    {
        var operation = new UpgradeWorld.RemoveObjects();
        IEnumerator? iterator = null;
        Check(bridge.Begin("objects_remove Stone start", () =>
        {
            Hook("BeforeAdd", operation);
            Hook("BeforeOperationInit", operation);
            Check(((UpgradeWorldScheduleObservation)typeof(UpgradeWorldScheduleBridge).GetField("_observation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!).State ==
                UpgradeWorldScheduleState.Pending, "Initial OnInit during admission is allowed");
            Hook("AfterInit", operation, true);
            UpgradeWorld.Executor.Work.Add(operation); UpgradeWorld.Executor.Busy = new UnityEngine.Coroutine();
            object?[] arguments = { operation, One().GetEnumerator() }; Hook("AfterExecute", arguments); iterator = (IEnumerator)arguments[1]!;
            return Success;
        }).State == UpgradeWorldScheduleState.Pending, "Normal one-time initialization completes admission");
        Hook("BeforeOperationInit", operation);
        Check(UpgradeWorld.Executor.Work.Count == 1 && UpgradeWorld.Executor.Busy != null, "Manual inspection never cancels UW work");
        Drain(iterator!); Empty();
        Check(bridge.Poll().Code == "uw_foreign_inspection", "GetInfo reinitialization cannot later become successful completion");
    }
    private static void Retire(UpgradeWorldScheduleBridge bridge)
    {
        IEnumerator? iterator = null;
        bridge.Begin("zones_reset start", () => { iterator = Track(new UpgradeWorld.ResetZones(), Nested().GetEnumerator()); return Success; });
        bridge.Dispose(); Drain(iterator!);
        Check(UpgradeWorld.Executor.Work.Count == 1 && UpgradeWorld.Executor.Busy != null, "Dispose does not remove foreign world work");
        Check(bridge.Poll().Code == "uw_observer_retired", "Retired observer cannot approve work"); Empty();
    }
}

namespace HarmonyLib
{
    public sealed class HarmonyMethod { public readonly MethodInfo Method; public HarmonyMethod(Type type, string name) => Method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!; }
    public sealed class Harmony
    {
        public static readonly List<MethodBase> Patched = new();
        public Harmony(string id) { }
        public void Patch(MethodBase original, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null, HarmonyMethod? transpiler = null, HarmonyMethod? finalizer = null) => Patched.Add(original);
        public void UnpatchSelf() => Patched.Clear();
    }
}
namespace BepInEx.Bootstrap
{
    public static class Chainloader { public static readonly Dictionary<string, Plugin> PluginInfos = new(); }
    public sealed class Plugin { public object Instance = new UpgradeWorld.UpgradeWorld(); public Metadata Metadata = new(); }
    public sealed class Metadata { public Version Version = new(1, 80); }
}
namespace ServerManager
{
    internal static class IntegrityCanonical { internal static bool IsFatal(Exception error) => error is OutOfMemoryException; }
    internal static class UpgradeWorldScheduleCommands
    {
        // Real catalog/parser has its own exhaustive source-linked suite.
        internal static bool IsMaintenance(string line) => !line.StartsWith("upgrade onions") && !line.StartsWith("location_unregister");
        internal static string GetUpgradeType(string line) => line.Split(' ').Skip(1).FirstOrDefault(value => value != "start") ?? "";
    }
}
namespace ServerManager.Commands
{
    internal static class ServerCommands
    {
        internal static bool TryTokenize(string value, out string[] arguments, out string error)
        { arguments = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); error = ""; return arguments.Length != 0; }
    }
}
namespace ServerManager.Events
{
    internal sealed class ServerManagerCommandResult
    { internal bool Success; internal string Code; internal ServerManagerCommandResult(bool success, string code) { Success = success; Code = code; } }
}
public class Terminal { }
public class ZRpc { }
public class ZDO { }
public struct Vector2i { }
public class ZoneSystem { public struct LocationInstance { public bool m_placed; } }
public class ZNet { public static ZNet? instance = new(); public bool Dedicated = true; public bool IsServer() => true; public bool IsDedicated() => Dedicated; }
namespace UnityEngine { public class Coroutine { } public struct Vector3 { } }
namespace UpgradeWorld
{
    public sealed class UpgradeWorld { }
    public static class Settings { public static bool Root = true; public static bool IsRoot(string id) => Root; }
    public static class SavingCommands { public static bool SavingDisabled; }
    public static class ServerExecution { public static ZRpc? User; }
    public static class Helper { public static void AddError(Terminal context, string message, bool priority) { } public static void Print(Terminal context, ZRpc? user, string value) { } }
    public static class Executor
    {
        public static readonly List<ExecutedOperation> Work = new();
        private static UnityEngine.Coroutine? executionCoroutine;
        public static UnityEngine.Coroutine? Busy { get => executionCoroutine; set => executionCoroutine = value; }
        public static int StopCalls;
        public static List<ExecutedOperation> GetOperations() => Work;
        public static void AddOperation(ExecutedOperation operation, bool autoStart) { }
        public static void StopExecution() { StopCalls++; }
    }
    public abstract class ExecutedOperation
    {
        protected int Failed;
        public void SetFailed(int value) => Failed = value;
        public bool Init(bool autoStart) => true;
        public IEnumerator Execute(Stopwatch sw) { yield return null; }
        protected virtual void OnStart() { }
        protected virtual string OnInit() => "Ready";
        protected virtual IEnumerator OnExecute(Stopwatch sw) { yield return null; }
        protected virtual void OnEnd() { }
    }
    public class ExecutedEntityOperation : ExecutedOperation { protected int TotalCount; public int Total { set => TotalCount = value; } }
    public sealed class ResetChests : ExecutedEntityOperation { }
    public sealed class EditObjects : ExecutedEntityOperation { }
    public sealed class RefreshObjects : ExecutedEntityOperation { }
    public sealed class RemoveObjects : ExecutedEntityOperation { }
    public sealed class SwapObjects : ExecutedEntityOperation { }
    public sealed class ResetZones : ExecutedOperation { }
    public sealed class ResetVegetation : ExecutedOperation { }
    public sealed class AddVegetation : ExecutedOperation { }
    public sealed class RemoveVegetation : ExecutedOperation { }
    public sealed class RemoveLocations : ExecutedOperation { }
    public sealed class DistributeLocations : ExecutedOperation { }
    public sealed class TempleVersion : ExecutedOperation { }
    public sealed class WorldVersion : ExecutedOperation { }
    public sealed class Print : ExecutedOperation { }
    public sealed class Generate : ExecutedOperation { }
    public sealed class RestoreZones : ExecutedOperation { }
    public class RegenerateLocations : ExecutedOperation
    { protected bool ExecuteLocation(Vector2i zone, ZoneSystem.LocationInstance location) => true; }
    public class SpawnLocations : ExecutedOperation
    { protected bool ExecuteLocation(Vector2i zone, ZoneSystem.LocationInstance location) => true; }
    public class FiltererParameters { }
    public class DataParameters { }
    public sealed class FixLocations { public FixLocations(Terminal context, FiltererParameters args) { } }
    public sealed class SwapLocations { public SwapLocations(Terminal context, IEnumerable<string> ids, DataParameters args) { } }
    public sealed class RegisterLocation { public RegisterLocation(Terminal context, string id, UnityEngine.Vector3 position) { } }
    public sealed class ChangeTime { public ChangeTime(Terminal context, double time) { } }
    public sealed class SetTime { public SetTime(Terminal context, double time) { } }
    public sealed class CleanDuplicates { public CleanDuplicates(Terminal context, bool pin, bool print) { } }
    public sealed class CleanLocations { public CleanLocations(Terminal context, ZDO[] data, bool pin, bool print) { } }
    public sealed class CleanObjects { public CleanObjects(Terminal context, ZDO[] data, bool pin, bool print) { } }
    public sealed class CleanChests { public CleanChests(Terminal context, ZDO[] data, bool pin, bool print) { } }
    public sealed class CleanStands { public CleanStands(Terminal context, ZDO[] data, bool pin, bool print) { } }
    public sealed class CleanDungeons { public CleanDungeons(Terminal context, ZDO[] data, bool pin, bool print) { } }
    public sealed class CleanSpawns { public CleanSpawns(Terminal context, ZDO[] data, bool pin, bool print) { } }
    public sealed class CleanHealth { public CleanHealth(Terminal context, ZDO[] data, bool pin, bool print) { } }
}
