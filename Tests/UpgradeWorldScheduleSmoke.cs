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
#if UW_BAD_CORE
            Check(!UpgradeWorldScheduleBridge.TryCreate(out _, out string unsupported) && unsupported == "uw_unsupported_contract", "Old Init(bool) contract rejected by signature, not version");
            Check(HarmonyLib.Harmony.Patched.Count == 0, "Invalid shared contract installs no hooks");
            Check(ServerManagerPlugin.Log.Warnings.Any(message => message.Contains("Init") && message.Contains("expected") && message.Contains("actual") && message.Contains("MVID=")), "Concrete diagnostic identifies expected/actual member and DLL");
            int warnings = ServerManagerPlugin.Log.Warnings.Count;
            Check(!UpgradeWorldScheduleBridge.TryCreate(out _, out _), "Repeated invalid contract still rejected");
            Check(ServerManagerPlugin.Log.Warnings.Count == warnings, "Repeated contract failure logged once");
            Console.WriteLine("PASS: unsupported shared contract " + _checks + " assertions.");
            return 0;
#elif UW_BAD_CLEAN
            Check(UpgradeWorldScheduleBridge.TryCreate(out var capabilityBridge, out _), "Unrelated bad Clean signature leaves shared tracking available");
            using (capabilityBridge!)
            {
                Check(capabilityBridge!.SupportsCommand("zones_generate start", out _), "Generate capability remains supported");
                Check(capabilityBridge.SupportsCommand("clean_objects", out _), "Other clean capability remains supported");
                Check(!capabilityBridge.SupportsCommand("world_clean", out string unavailable) && unavailable == "uw_unsupported_contract", "World-clean capability reports exact unsupported contract");
                Check(!capabilityBridge.SupportsCommand("clean_duplicates", out _), "Bad command cannot dispatch through tracked path");
                Check(capabilityBridge.Begin("clean_duplicates", () => throw new Exception("Must not dispatch")).Code == "uw_unsupported_contract", "Unsupported capability refuses before mutation");
                Check(ServerManagerPlugin.Log.Warnings.Any(message => message.Contains("CleanDuplicates") && message.Contains("expected") && message.Contains("actual")), "Capability diagnostic names bad constructor");
                Normal(capabilityBridge);
            }
            Check(HarmonyLib.Harmony.Patched.Count == 0, "Partial capability observer cleans up own hooks");
            Console.WriteLine("PASS: isolated command capability " + _checks + " assertions.");
            return 0;
#else
            Check(UpgradeWorldScheduleBridge.TryCreate(out var bridge, out _), "Compatible contract accepted regardless of plugin version");
            using (bridge!)
            {
                Check(HarmonyLib.Harmony.Patched.Count >= 15, "All observation targets registered");
                Check(!UpgradeWorldScheduleBridge.TryCreate(out _, out _), "Single observer ownership");
                Check(bridge!.CanRun(out _), "Dedicated default root allowed");
                UpgradeWorld.Settings.Root = false;
                Check(!bridge.CanRun(out string denied) && denied == "uw_root_denied", "No root impersonation");
                Check(bridge.TryGetIdle(out bool cleanupIdle, out _) && cleanupIdle, "Idle cleanup does not require root execution permission");
                Check(bridge.Begin("zones_generate start", () => throw new Exception()).Code == "uw_root_denied", "Read-only idle proof never authorizes dispatch");
                ZNet.instance!.Dedicated = false;
                Check(bridge.CanRun(out _), "Listen host keeps UW local rule");
                ZNet.instance.Dedicated = true; UpgradeWorld.Settings.Root = true;
                UpgradeWorld.SavingCommands.SavingDisabled = true;
                Check(!bridge.CanRun(out _), "Disabled saving blocks maintenance");
                Check(bridge.TryGetIdle(out cleanupIdle, out _) && cleanupIdle, "Save-disabled mode cannot strand an idle exclusion lease");
                Check(bridge.Begin("zones_generate start", () => throw new Exception()).Code == "uw_saving_disabled", "Save-disabled still rejects new commands");
                UpgradeWorld.SavingCommands.SavingDisabled = false;
                UpgradeWorld.ServerExecution.User = new ZRpc();
                Check(!bridge.CanRun(out _), "No borrowed RPC caller");
                Check(bridge.TryGetIdle(out cleanupIdle, out _) && cleanupIdle, "Cleanup reads native executor without borrowing RPC authority");
                UpgradeWorld.ServerExecution.User = null;
                Check(bridge.Begin("zones_reset", () => throw new Exception()).Code == "uw_start_required", "Explicit start required");
                Check(bridge.Begin("upgrade onions start", () => throw new Exception()).Code == "uw_unsupported_command", "Broken upstream command never dispatched");
                Normal(bridge); FailedCounter(bridge); NestedException(bridge); SwallowedStart(bridge);
                ManualStop(bridge); MissingLocation(bridge); ForeignOperation(bridge); NotAdmitted(bridge); Clean(bridge);
                Composite(bridge); AllReviewedShapes(bridge); EntityNoOp(bridge); Immediate(bridge); GeneralizedFailures(bridge); ForeignInspection(bridge);
                CurrentLifecycleFailures(bridge); PartialCompletion(bridge); EndProof(bridge); TimeoutWaits(bridge); HookIntegrity(bridge);
                Retire(bridge);
            }
            Check(HarmonyLib.Harmony.Patched.Count == 0, "Observers removed on dispose");
            Check(UpgradeWorld.Executor.StopCalls == 0, "Adapter never stops UW work");
            HarmonyLib.Harmony.FailAfter = 3;
            Check(!UpgradeWorldScheduleBridge.TryCreate(out _, out string failedPatch) && failedPatch == "uw_unsupported_contract", "Partial hook installation failure is unsupported");
            Check(HarmonyLib.Harmony.Patched.Count == 0 && !UpgradeWorldScheduleBridge.ObserverCleanupFailed, "Partial installation removes only this observer's hooks");
            HarmonyLib.Harmony.FailAfter = -1;
            Check(UpgradeWorldScheduleBridge.TryCreate(out var recovered, out _), "Cleanly failed installation does not strand observer ownership");
            recovered!.Dispose();
            Console.WriteLine("PASS: Upgrade World bridge " + _checks + " assertions (source-linked iterator/lifecycle tests; no world mutation).");
            return 0;
#endif
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
        object?[] arguments = { operation, CompleteLifecycle(operation, body) };
        Hook("AfterExecute", arguments);
        return (IEnumerator)arguments[1]!;
    }
    private static IEnumerator CompleteLifecycle(UpgradeWorld.ExecutedOperation operation, IEnumerator body)
    {
        yield return body;
        object?[] state = { operation, 0 };
        Hook("BeforeOperationEnd", state);
        Hook("AfterOperationEnd", operation, state[1], true);
        Hook("FinalizeOperationEnd", operation, null, state[1]);
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
        Check(bridge.Begin("world_clean", () => Success).Code == "uw_start_required", "Current queued cleanup requires explicit start");
        Check(bridge.Begin("world_clean start", () => Success).Code == "uw_operation_not_admitted", "World cleaned text alone insufficient");
        IEnumerator? iterator = null;
        var names = new[] { "CleanDuplicates", "CleanLocations", "CleanObjects", "CleanChests", "CleanStands", "CleanDungeons", "CleanSpawns", "CleanHealth" };
        Check(bridge.Begin("world_clean start", () =>
        {
            iterator = Track(new UpgradeWorld.QueuedOperation(), ConstructorBody(names).GetEnumerator());
            return Success;
        }).State == UpgradeWorldScheduleState.Pending, "Cleanup constructor callbacks may run after dispatch");
        Drain(iterator!); Empty();
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Succeeded, "Queued cleanup and every constructor completed");
        bridge.Begin("world_clean start", () => { iterator = Track(new UpgradeWorld.QueuedOperation(), ConstructorBody(names.Take(7)).GetEnumerator()); return Success; });
        Drain(iterator!); Empty();
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Indeterminate, "Queued wrapper alone cannot certify missing cleanup component");
    }
    private static IEnumerable<object?> ConstructorBody(IEnumerable<string> names)
    {
        yield return null;
        foreach (string name in names)
            Hook("AfterImmediate", System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UpgradeWorld.UpgradeWorld).Assembly.GetType("UpgradeWorld." + name)!));
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
        foreach (string value in new[] { "false", "0", "no", "off" })
        {
            IEnumerator? iterator = null;
            bridge.Begin("locations_add explored=" + value + " start", () => { iterator = Track(new UpgradeWorld.DistributeLocations(), One().GetEnumerator()); return Success; });
            Drain(iterator!); Empty();
            Check(bridge.Poll().State == UpgradeWorldScheduleState.Succeeded, "Unexplored-only add has no SpawnLocations: " + value);
        }
        bridge.Begin("locations_add explored=false explored=true start", () => { Track(new UpgradeWorld.DistributeLocations(), One().GetEnumerator()); return Success; });
        Check(bridge.Poll().Code == "uw_operation_not_admitted", "Last explored assignment restores required spawn stage"); Empty();
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
        var synchronous = (Dictionary<string, string[]>)typeof(UpgradeWorldScheduleBridge).GetField("Constructors", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
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
                    IEnumerator body = synchronous.TryGetValue(shape.Item1.Split(' ')[0], out string[] constructors)
                        ? ConstructorBody(constructors).GetEnumerator() : One().GetEnumerator();
                    iterators.Add(Track(operation, body));
                }
                return Success;
            }).State == UpgradeWorldScheduleState.Pending, "Reviewed exact queue shape admitted: " + shape.Item1);
            foreach (var iterator in iterators) { Drain(iterator); UpgradeWorld.Executor.Work.RemoveAt(0); }
            Hook("BeforeStop"); UpgradeWorld.Executor.Busy = null;
            Check(bridge.Poll().State == UpgradeWorldScheduleState.Succeeded, "Reviewed queue shape completed: " + shape.Item1);
        }
        foreach (var pair in synchronous.Where(pair => !queued.ContainsKey(pair.Key)))
            Check(bridge.Begin(pair.Key, () =>
            {
                foreach (string name in pair.Value)
                    Hook("AfterImmediate", System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UpgradeWorld.UpgradeWorld).Assembly.GetType("UpgradeWorld." + name)!));
                return Success;
            }).State == UpgradeWorldScheduleState.Succeeded, "Reviewed synchronous shape completed: " + pair.Key);
    }
    private static void Immediate(UpgradeWorldScheduleBridge bridge)
    {
        foreach (var pair in new[] { ("locations_fix", "FixLocations"),
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
    private static void CurrentLifecycleFailures(UpgradeWorldScheduleBridge bridge)
    {
        var operation = new UpgradeWorld.Generate(); IEnumerator? iterator = null;
        bridge.Begin("zones_generate start", () => { iterator = Track(operation, One().GetEnumerator()); return Success; });
        operation.ExecutionFailed = true;
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Pending, "Internally caught exception does not imply UW idle");
        Drain(iterator!); Empty();
        var result = bridge.Poll();
        Check(result.State == UpgradeWorldScheduleState.Failed && result.Code == "uw_execution_exception",
            "ExecutionFailed defeats normal root exhaustion and empty queue");

        operation = new UpgradeWorld.Generate();
        bridge.Begin("zones_generate start", () =>
        {
            iterator = Track(operation, One().GetEnumerator());
            Hook("BeforePrintedMessage", "Operation failed: nested iterator error. Changes already made are not rolled back.");
            return Success;
        });
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Pending, "Reported execution fault keeps observation until idle");
        Drain(iterator!); Empty();
        Check(bridge.Poll().Code == "uw_execution_exception", "Reviewed exception report is not generic success output");
    }
    private static void PartialCompletion(UpgradeWorldScheduleBridge bridge)
    {
        var operation = new UpgradeWorld.Generate(); IEnumerator? iterator = null;
        bridge.Begin("zones_generate start", () => { iterator = Track(operation, Nested().GetEnumerator()); return Success; });
        operation.SetFailed(2);
        var pending = bridge.Poll();
        Check(pending.State == UpgradeWorldScheduleState.Pending && pending.Code == "uw_partial_failure",
            "Skipped zones remain pending while later zones execute");
        Drain(iterator!);
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Pending, "Partial result still waits for executor drain");
        UpgradeWorld.Executor.Work.Clear(); Hook("BeforeStop"); UpgradeWorld.Executor.Busy = null;
        var result = bridge.Poll();
        Check(result.State == UpgradeWorldScheduleState.Failed && result.Code == "uw_partial_failure",
            "Fully ended, idle partial failures remain failed; the observer does not request a save");

        foreach (string lateFault in new[] { "exception", "cancel", "foreign", "timeout" })
        {
            operation = new UpgradeWorld.Generate();
            bridge.Begin("zones_generate start", () => { iterator = Track(operation, One().GetEnumerator()); return Success; });
            operation.SetFailed(1); bridge.Poll();
            if (lateFault == "exception") operation.ExecutionFailed = true;
            else if (lateFault == "cancel") Hook("BeforeStop");
            else if (lateFault == "foreign") Hook("BeforeAdd", new UpgradeWorld.Generate());
            else bridge.MarkTimedOut();
            Check(bridge.Poll().State == UpgradeWorldScheduleState.Pending, "Late " + lateFault + " keeps ownership until idle");
            Drain(iterator!); Empty(); result = bridge.Poll();
            Check(result.State != UpgradeWorldScheduleState.Succeeded && result.Code != "uw_partial_failure",
                "Late " + lateFault + " takes diagnostic precedence over skipped targets");
        }
    }
    private static void EndProof(UpgradeWorldScheduleBridge bridge)
    {
        var operation = new UpgradeWorld.Generate(); IEnumerator? iterator = null;
        bridge.Begin("zones_generate start", () =>
        {
            Hook("BeforeAdd", operation); Hook("AfterInit", operation, true);
            UpgradeWorld.Executor.Work.Add(operation); UpgradeWorld.Executor.Busy = new UnityEngine.Coroutine();
            object?[] args = { operation, One().GetEnumerator() }; Hook("AfterExecute", args); iterator = (IEnumerator)args[1]!;
            return Success;
        });
        Drain(iterator!); Empty();
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Indeterminate, "Root exhaustion without successful OnEnd is not completion");
        operation = new UpgradeWorld.Generate();
        bridge.Begin("zones_generate start", () => { iterator = Track(operation, One().GetEnumerator()); return Success; });
        object?[] ending = { operation, 0 }; Hook("BeforeOperationEnd", ending);
        Hook("AfterOperationEnd", operation, ending[1], false);
        Hook("FinalizeOperationEnd", operation, null, ending[1]);
        Drain(iterator!); Empty();
        Check(bridge.Poll().Code == "uw_completion_skipped", "Other patch skipping OnEnd cannot certify completion");

        operation = new UpgradeWorld.Generate();
        bridge.Begin("zones_generate start", () =>
        {
            Hook("BeforeAdd", operation); Hook("AfterInit", operation, true);
            object?[] outer = { operation, 0 }; object?[] inner = { operation, 0 };
            Hook("BeforeOperationEnd", outer); Hook("BeforeOperationEnd", inner);
            Hook("AfterOperationEnd", operation, inner[1], true);
            Hook("FinalizeOperationEnd", operation, null, inner[1]);
            Hook("AfterOperationEnd", operation, outer[1], true);
            Hook("FinalizeOperationEnd", operation, null, outer[1]);
            UpgradeWorld.Executor.Work.Add(operation); UpgradeWorld.Executor.Busy = new UnityEngine.Coroutine();
            object?[] args = { operation, One().GetEnumerator() }; Hook("AfterExecute", args); iterator = (IEnumerator)args[1]!;
            return Success;
        });
        Drain(iterator!); Empty();
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Succeeded, "Derived-to-base OnEnd nesting is one completion");
    }
    private static void TimeoutWaits(UpgradeWorldScheduleBridge bridge)
    {
        IEnumerator? iterator = null;
        bridge.Begin("zones_generate start", () => { iterator = Track(new UpgradeWorld.Generate(), One().GetEnumerator()); return Success; });
        bridge.MarkTimedOut();
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Pending && UpgradeWorld.Executor.StopCalls == 0, "Timeout does not stop work or release ownership");
        Drain(iterator!); Empty();
        Check(bridge.Poll().State == UpgradeWorldScheduleState.Indeterminate && bridge.Poll().Code == "uw_unconfirmed", "Late completion remains review-required after timeout");
    }
    private static void HookIntegrity(UpgradeWorldScheduleBridge bridge)
    {
        HarmonyLib.Harmony.TemporarilyMissingHooks = true;
        Check(!bridge.SupportsCommand("zones_generate start", out string code) && code == "uw_observer_hooks_changed", "Removed observation hooks detected before dispatch");
        Check(bridge.Begin("zones_generate start", () => throw new Exception("Must not dispatch")).Code == "uw_observer_hooks_changed", "Begin rechecks own hooks before mutation");
        HarmonyLib.Harmony.TemporarilyMissingHooks = false;
        Normal(bridge);
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
    public sealed class Patch { public string owner = ""; public MethodInfo PatchMethod = null!; }
    public sealed class Patches
    {
        public readonly List<Patch> Prefixes = new(), Postfixes = new(), Finalizers = new();
    }
    public sealed class Harmony
    {
        public static readonly List<MethodBase> Patched = new();
        private static readonly Dictionary<MethodBase, Patches> Details = new();
        public static int FailAfter = -1;
        public static bool TemporarilyMissingHooks;
        public string Id { get; }
        public Harmony(string id) { Id = id; }
        public static Patches? GetPatchInfo(MethodBase target) => TemporarilyMissingHooks ? null : Details.TryGetValue(target, out var patches) ? patches : null;
        public void Patch(MethodBase original, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null, HarmonyMethod? transpiler = null, HarmonyMethod? finalizer = null)
        {
            if (FailAfter >= 0 && Patched.Count >= FailAfter) throw new InvalidOperationException("Injected patch failure");
            Patched.Add(original);
            if (!Details.TryGetValue(original, out var details)) Details[original] = details = new();
            if (prefix != null) details.Prefixes.Add(new Patch { owner = Id, PatchMethod = prefix.Method });
            if (postfix != null) details.Postfixes.Add(new Patch { owner = Id, PatchMethod = postfix.Method });
            if (finalizer != null) details.Finalizers.Add(new Patch { owner = Id, PatchMethod = finalizer.Method });
        }
        public void UnpatchSelf() { Patched.Clear(); Details.Clear(); }
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
    internal static class ServerManagerPlugin
    {
        internal const string ModVersion = "test";
        internal static readonly TestLog Log = new();
    }
    internal sealed class TestLog
    {
        internal readonly List<string> Warnings = new();
        internal void LogWarning(object message) => Warnings.Add(message.ToString()!);
    }
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
public struct Vector2s { }
public class Minimap { public enum PinType { Icon0 } }
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
        public bool ExecutionFailed { get; set; }
#if UW_BAD_CORE
        public bool Init(bool start) => true;
#else
        public bool Init() => true;
#endif
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
    public sealed class QueuedOperation : ExecutedOperation { }
    public sealed class Generate : ExecutedOperation { }
    public sealed class RestoreZones : ExecutedOperation { }
    public class RegenerateLocations : ExecutedOperation
    { protected bool ExecuteLocation(Vector2s zone, ZoneSystem.LocationInstance location) => true; }
    public class SpawnLocations : ExecutedOperation
    { protected bool ExecuteLocation(Vector2s zone, ZoneSystem.LocationInstance location) => true; }
    public class FiltererParameters { }
    public class DataParameters { }
    public sealed class FixLocations { public FixLocations(Terminal context, FiltererParameters args) { } }
    public sealed class SwapLocations { public SwapLocations(Terminal context, IEnumerable<string> ids, DataParameters args) { } }
    public sealed class RegisterLocation { public RegisterLocation(Terminal context, string id, UnityEngine.Vector3 position) { } }
    public sealed class ChangeTime { public ChangeTime(Terminal context, double time) { } }
    public sealed class SetTime { public SetTime(Terminal context, double time) { } }
#if UW_BAD_CLEAN
    public sealed class CleanDuplicates { public CleanDuplicates(Terminal context, bool pin, bool print) { } }
#else
    public sealed class CleanDuplicates { public CleanDuplicates(Terminal context, Minimap.PinType? pin, bool print, FiltererParameters args) { } }
#endif
    public sealed class CleanLocations { public CleanLocations(Terminal context, ZDO[] data, Minimap.PinType? pin, bool print) { } }
    public sealed class CleanObjects { public CleanObjects(Terminal context, ZDO[] data, Minimap.PinType? pin, bool print) { } }
    public sealed class CleanChests { public CleanChests(Terminal context, ZDO[] data, Minimap.PinType? pin, bool print) { } }
    public sealed class CleanStands { public CleanStands(Terminal context, ZDO[] data, Minimap.PinType? pin, bool print) { } }
    public sealed class CleanDungeons { public CleanDungeons(Terminal context, ZDO[] data, Minimap.PinType? pin, bool print) { } }
    public sealed class CleanSpawns { public CleanSpawns(Terminal context, ZDO[] data, Minimap.PinType? pin, bool print) { } }
    public sealed class CleanHealth { public CleanHealth(Terminal context, ZDO[] data, Minimap.PinType? pin, bool print) { } }
}
