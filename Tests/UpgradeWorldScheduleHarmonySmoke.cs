// Real Harmony on managed-only fixtures. No Unity/plugin assembly is loaded or executed.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

internal static class UpgradeWorldScheduleHarmonySmoke
{
    private const string Owner = "ServerManager.Tests.UpgradeWorld.Observer";
    private const string ForeignOwner = "ServerManager.Tests.UpgradeWorld.Foreign";
    private static int _checks, _completed, _failed, _skipped, _calls;
    private static bool _skip, _throwPrefix, _nestedScopePreserved;

    private static int Main()
    {
        try { Run(); Console.WriteLine("PASS: real Harmony lifecycle injection " + _checks + " assertions (managed-only fixtures; no Unity/plugin execution)."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Console.WriteLine("Harmony under test: " + typeof(Harmony).Assembly.FullName);
        var observer = new Harmony(Owner);
        var foreign = new Harmony(ForeignOwner);
        MethodInfo baseEnd = typeof(BaseOperation).GetMethod(nameof(BaseOperation.End))!;
        MethodInfo end = typeof(Operation).GetMethod(nameof(Operation.End))!;
        MethodInfo throwing = typeof(Operation).GetMethod(nameof(Operation.ThrowEnd))!;
        MethodInfo catching = typeof(Operation).GetMethod(nameof(Operation.CatchNestedEndError))!;
        var before = new HarmonyMethod(typeof(UpgradeWorldScheduleHarmonySmoke), nameof(Before));
        var after = new HarmonyMethod(typeof(UpgradeWorldScheduleHarmonySmoke), nameof(After));
        var returned = new HarmonyMethod(typeof(UpgradeWorldScheduleHarmonySmoke), nameof(Returned));
        try
        {
            observer.Patch(baseEnd, prefix: before, postfix: returned, finalizer: after);
            observer.Patch(end, prefix: before, postfix: returned, finalizer: after);
            observer.Patch(throwing, prefix: before, postfix: returned, finalizer: after);
            observer.Patch(catching, prefix: before, postfix: returned, finalizer: after);
            foreign.Patch(end, prefix: new HarmonyMethod(typeof(UpgradeWorldScheduleHarmonySmoke), nameof(Skip)) { priority = Priority.First });
            foreign.Patch(baseEnd, prefix: new HarmonyMethod(typeof(UpgradeWorldScheduleHarmonySmoke), nameof(ThrowPrefix)) { priority = Priority.First });

            var operation = new Operation();
            operation.End();
            Check(_completed == 1 && _calls == 2 && operation.EndingDepth == 0 && _failed == 0 && _skipped == 0,
                "Derived End -> base End is one completed root lifecycle; state depth restored");

            _skip = true;
            operation.End();
            Check(_completed == 1 && _calls == 2 && operation.EndingDepth == 0 && _skipped == 1,
                "__runOriginal=false reaches finalizer and cannot certify a skipped End");
            _skip = false;

            Exception? caught = null;
            try { operation.ThrowEnd(); } catch (Exception error) { caught = error; }
            Check(ReferenceEquals(caught, Operation.Expected), "Finalizer preserves exact exception");
            Check(_failed == 1 && _completed == 1 && operation.EndingDepth == 0, "Throwing End is not complete and restores scope");

            _throwPrefix = true;
            operation.CatchNestedEndError();
            Check(_nestedScopePreserved && operation.EndingDepth == 0,
                "Earlier foreign prefix exception's default state never resets outer End scope");
            Check(_failed == 2, "Failure evidence survives a nested prefix exception caught by the original caller");
            _throwPrefix = false;

            observer.UnpatchSelf();
            Check(!Harmony.GetPatchInfo(end).Owners.Contains(Owner) && Harmony.GetPatchInfo(end).Owners.Contains(ForeignOwner),
                "Observer cleanup leaves another owner's patch intact");
            _skip = true;
            new Operation().End();
            Check(_calls == 2 && _skipped == 1, "Foreign behavior remains active after observer cleanup");
        }
        finally { observer.UnpatchSelf(); foreign.UnpatchSelf(); }
    }

    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }
    // Mirror the bridge's exact injected signatures and 1-based entered marker.
    // Finalizer __runOriginal is intentionally NOT used: HarmonyX 2.9 rejects it.
    private static void Before(object __instance, out int __state)
    {
        __state = 0;
        if (__instance is BaseOperation operation) __state = ++operation.EndingDepth;
    }
    private static void Returned(object __instance, int __state, bool __runOriginal)
    {
        if (__instance is not BaseOperation || __state <= 0) return;
        if (!__runOriginal) _skipped++;
        else if (__state == 1) _completed++;
    }
    private static Exception? After(object __instance, Exception? __exception, int __state)
    {
        if (__instance is BaseOperation operation)
        {
            if (__state > 0) operation.EndingDepth = __state - 1;
            if (__exception != null) _failed++;
        }
        return __exception;
    }
    private static bool Skip() => !_skip;
    private static void ThrowPrefix() { if (_throwPrefix) throw Operation.Expected; }
    public class BaseOperation
    {
        internal int EndingDepth;
        [MethodImpl(MethodImplOptions.NoInlining)] public virtual void End() { _calls++; }
    }
    public sealed class Operation : BaseOperation
    {
        internal static readonly InvalidOperationException Expected = new("Observed exception");
        [MethodImpl(MethodImplOptions.NoInlining)] public override void End() { base.End(); _calls++; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void ThrowEnd() { throw Expected; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void CatchNestedEndError()
        { try { base.End(); } catch (InvalidOperationException) { _nestedScopePreserved = EndingDepth == 1; } }
    }
}
