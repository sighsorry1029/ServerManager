param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [switch]$SourceOnly
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $arguments = @('-NoProfile', '-File', $PSCommandPath, '-Configuration', $Configuration, '-GamePath', $GamePath)
    if ($SourceOnly) { $arguments += '-SourceOnly' }
    & (Get-Command pwsh -ErrorAction Stop).Source @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Character equipment recovery validation failed.' }
    exit 0
}
$root = Split-Path -Parent $PSScriptRoot
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }

# Inspect the final bundled plugin and the unmodified installed game, without
# loading Unity/game code or changing either DLL. SourceOnly is for development
# before the parent build; it intentionally does not claim binary verification.
if (-not $SourceOnly) {
    [Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx/core/Mono.Cecil.dll')) | Out-Null
    $plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root "bin/$Configuration/ServerManager.dll"))
    $game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GamePath 'valheim_Data/Managed/assembly_valheim.dll'))
    try {
        $patch = $plugin.MainModule.GetType('ServerManager.ManagedCharacterEquipmentRestorePatch')
        Assert ($null -ne $patch) 'The final bundled plugin lacks the equipment restoration patch.'
        $target = @($patch.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' })
        Assert ($target.Count -eq 1 -and $target[0].ConstructorArguments.Count -eq 3 -and
            $target[0].ConstructorArguments[0].Value.FullName -eq 'Player' -and
            $target[0].ConstructorArguments[1].Value -eq 'Load' -and
            $target[0].ConstructorArguments[2].Value.Count -eq 1 -and
            $target[0].ConstructorArguments[2].Value[0].Value.FullName -eq 'ZPackage') 'Recovery must target only Player.Load(ZPackage), not every equipment call.'
        $wrapper = $patch.Methods | Where-Object Name -eq 'RestoreEquipment' | Select-Object -First 1
        $transpiler = $patch.Methods | Where-Object Name -eq 'Transpiler' | Select-Object -First 1
        Assert ($null -ne $wrapper -and $wrapper.IsPrivate -and $wrapper.IsStatic) 'The wrapper must remain a private static call-site helper.'
        Assert ($null -ne $transpiler) 'The final bundled plugin lacks the equipment call-site transpiler.'
        Assert (@($patch.Fields | Where-Object { $_.IsStatic -and $_.IsInitOnly -and $_.FieldType.FullName -eq 'System.Reflection.MethodInfo' }).Count -ge 1) 'Cache the original private equipment method lookup.'
        $calls = @($wrapper.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
        foreach ($name in @('Invoke', 'get_IsPlayerLoadInProgress', 'ShouldValidatePlayerLoad', 'get_CharacterLoadSaveBlocked', 'Capture', 'Throw')) {
            Assert (@($calls | Where-Object { $_.Operand.Name -eq $name }).Count -gt 0) "Wrapper is missing required $name behavior."
        }
        foreach ($method in @($patch.Methods | Where-Object HasBody)) {
            foreach ($instruction in $method.Body.Instructions) {
                if ($instruction.Operand -is [Mono.Cecil.MethodReference]) {
                    $member = $instruction.Operand
                    Assert (-not ($member.DeclaringType.FullName -eq 'Player' -and $member.Name -in @('EquipInventoryItems', 'UpdateAvailablePiecesList'))) 'No compiled direct call may bypass private game-member access.'
                    Assert ($member.Name -notin @('RejectUnsafePlayerLoad', 'Clear', 'RemoveItem', 'RemoveAll', 'SetCustomData')) 'The equipment helper must not reject, delete, reset, or rewrite loaded data.'
                }
            }
        }
        $guard = $plugin.MainModule.GetType('ServerManager.ManagedCharacterPlayerLoadPatch')
        $finalizer = $guard.Methods | Where-Object Name -eq 'Finalizer' | Select-Object -First 1
        Assert (@($finalizer.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RejectUnsafePlayerLoad' }).Count -eq 1) 'The strict general-load guard must still reject failures outside equipment restoration.'

        $player = $game.MainModule.GetType('Player')
        $equipment = $player.Methods | Where-Object { $_.Name -eq 'EquipInventoryItems' -and $_.Parameters.Count -eq 0 } | Select-Object -First 1
        $loading = $player.Fields | Where-Object Name -eq 'm_isLoading' | Select-Object -First 1
        Assert ($equipment.IsPrivate -and -not $equipment.IsStatic -and $equipment.ReturnType.FullName -eq 'System.Void') 'Use the original private instance EquipInventoryItems metadata, not a publicized assembly.'
        Assert ($loading.IsPrivate -and $loading.FieldType.FullName -eq 'System.Boolean') 'Use the original private m_isLoading metadata.'
        $load = $player.Methods | Where-Object { $_.Name -eq 'Load' -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'ZPackage' } | Select-Object -First 1
        $realCode = @($load.Body.Instructions | Where-Object { $_.OpCode.Name -ne 'nop' })
        $tail = @($realCode | Select-Object -Last 10)
        Assert (($tail.OpCode.Name -join ',') -eq 'ldarg.0,ldc.i4.0,stfld,ldarg.0,call,ldarg.0,call,ldarg.0,call,ret') 'The original game normal-return tail changed; review before accepting recovery.'
        Assert ($tail[2].Operand.FullName -eq 'System.Boolean Player::m_isLoading') 'The tail must clear Player.m_isLoading first.'
        foreach ($entry in @(@(4, 'UpdateAvailablePiecesList'), @(6, 'EquipInventoryItems'), @(8, 'UpdateEvents'))) {
            Assert ($tail[$entry[0]].Operand.FullName -eq "System.Void Player::$($entry[1])()") 'The original game tail call order changed.'
        }
        Assert (@($realCode | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.FullName -eq $equipment.FullName }).Count -eq 1) 'The original game must have one equipment call in Player.Load.'
        Write-Host 'PASS: Bundled helper metadata and original game private-member/normal-return-tail checks.'
    } finally {
        $plugin.Dispose()
        $game.Dispose()
    }
}

# Source-link both complete production patches and the production fatal filter.
# Only Harmony instruction storage, Player, runtime/session state, logging and
# poison adapter boundaries are fixtures. The transformed tail is emitted and
# executed as IL. This is an isolated .NET test, not in-game Harmony/Mono proof.
$patchSource = Get-Content -LiteralPath (Join-Path $root 'RuntimePatches.cs') -Raw
$equipmentMatch = [regex]::Matches($patchSource, '(?ms)^internal static class ManagedCharacterEquipmentRestorePatch\r?\n\{.*?^\}')
$guardMatch = [regex]::Matches($patchSource, '(?ms)^internal static class ManagedCharacterPlayerLoadPatch\r?\n\{.*?^\}')
Assert ($equipmentMatch.Count -eq 1) 'Expected one source-linked equipment patch.'
Assert ($guardMatch.Count -eq 1) 'Expected one source-linked strict load guard.'
$fatalSource = Get-Content -LiteralPath (Join-Path $root 'Integrity/IntegrityModels.cs') -Raw
$fatalMatch = [regex]::Matches($fatalSource, '(?ms)^        internal static bool IsFatal\(Exception exception\)\r?\n        \{.*?^        \}')
Assert ($fatalMatch.Count -eq 1) 'Expected one production fatal-exception filter.'
$harness = @'
#nullable enable
#pragma warning disable CS0649, CS0162
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace CharacterEquipmentRecoveryFixture
{
    internal sealed class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    internal static class Priority { internal const int First = 800, Last = 0; }
    internal enum ExceptionBlockType { BeginExceptionBlock, BeginCatchBlock, EndExceptionBlock }
    internal sealed class ExceptionBlock
    {
        internal ExceptionBlockType blockType;
        internal Type? catchType;
        internal ExceptionBlock(ExceptionBlockType type, Type? caught = null) { blockType = type; catchType = caught; }
    }
    internal sealed class CodeInstruction
    {
        internal OpCode opcode;
        internal object? operand;
        internal List<Label> labels = new List<Label>();
        internal List<ExceptionBlock> blocks = new List<ExceptionBlock>();
        internal CodeInstruction(OpCode code, object? argument = null) { opcode = code; operand = argument; }
        internal CodeInstruction(CodeInstruction original)
        {
            opcode = original.opcode; operand = original.operand;
            labels.AddRange(original.labels); blocks.AddRange(original.blocks);
        }
        internal bool Calls(MethodInfo method) => (opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && Equals(operand, method);
    }
    internal static class AccessTools
    {
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static MethodInfo? Method(Type type, string name, Type[]? parameters = null) =>
            parameters == null ? type.GetMethod(name, All) : type.GetMethod(name, All, null, parameters, null);
        internal static FieldInfo? Field(Type type, string name) => type.GetField(name, All);
        internal static MethodInfo? DeclaredMethod(Type type, string name, Type[]? parameters = null) =>
            parameters == null ? type.GetMethod(name, All | BindingFlags.DeclaredOnly) :
                type.GetMethod(name, All | BindingFlags.DeclaredOnly, null, parameters, null);
        internal static FieldInfo? DeclaredField(Type type, string name) => type.GetField(name, All | BindingFlags.DeclaredOnly);
    }
    internal sealed class ZPackage { }
    internal sealed class Player
    {
        private bool m_isLoading = true;
        internal bool IsLoading => m_isLoading;
        internal Exception? EquipmentFailure, CoreFailure, AvailableFailure, EventsFailure;
        internal Action? DuringEquipment;
        internal int CoreCalls, EquipmentCalls, AvailableCalls, EventCalls;
        internal readonly List<string> Flow = new List<string>();
        internal readonly List<Item> Inventory = new List<Item> { new Item("sword"), new Item("shield") };
        internal readonly Dictionary<string, string> CustomData = new Dictionary<string, string> { ["other-mod:opaque"] = "preserve exactly" };
        public void Load(ZPackage package) { }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CoreLoad()
        {
            CoreCalls++; Flow.Add("core");
            if (CoreFailure != null) throw CoreFailure;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EquipInventoryItems()
        {
            EquipmentCalls++; Flow.Add("equipment");
            // Emulate a normal mod-owned partial restoration, not a rollback.
            Inventory[0].Equipped = true;
            Inventory[0].CustomData["equipment-mod:visited"] = "yes";
            DuringEquipment?.Invoke();
            if (EquipmentFailure != null) throw EquipmentFailure;
            Inventory[1].Equipped = true;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UpdateAvailablePiecesList()
        {
            AvailableCalls++; Flow.Add("available");
            if (AvailableFailure != null) throw AvailableFailure;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateEvents()
        {
            EventCalls++; Flow.Add("events");
            if (EventsFailure != null) throw EventsFailure;
        }
    }
    internal sealed class Item
    {
        internal readonly string Name;
        internal bool Equipped;
        internal readonly Dictionary<string, string> CustomData = new Dictionary<string, string> { ["item-mod:opaque"] = "unchanged" };
        internal Item(string name) { Name = name; }
    }
    internal static class IntegrityCanonical
    {
        // SOURCE_LINKED_FATAL_FILTER
    }
    internal sealed class FixtureLog
    {
        internal readonly List<string> Warnings = new List<string>();
        internal Exception? Failure;
        internal void LogWarning(object message)
        {
            if (Failure != null) throw Failure;
            Warnings.Add(message.ToString()!);
        }
    }
    internal static class ServerManagerPlugin { internal static readonly FixtureLog Log = new FixtureLog(); }
    internal static class ServerManagerRuntime
    {
        internal static bool Guarded = true;
        internal static bool IsPlayerLoadInProgress { get; set; }
        internal static bool CharacterLoadSaveBlocked { get; set; }
        internal static readonly List<Exception> Rejections = new List<Exception>();
        internal static int Cleanups;
        internal static bool ShouldValidatePlayerLoad(Player player) => Guarded;
        internal static bool BeforePlayerLoad(Player player)
        {
            if (!Guarded) return false;
            IsPlayerLoadInProgress = true; return true;
        }
        internal static void RejectUnsafePlayerLoad(Exception error)
        {
            CharacterLoadSaveBlocked = true; Rejections.Add(error);
        }
        internal static void AfterPlayerLoad(bool entered)
        {
            Cleanups++; if (entered) IsPlayerLoadInProgress = false;
        }
    }
    internal static class CharacterPoisonPersistence
    {
        internal static readonly List<bool> Completions = new List<bool>();
        internal static int BeforeCalls;
        internal static void BeforeLoad(Player player) { BeforeCalls++; }
        internal static void AfterLoad(Player player, bool completed) { Completions.Add(completed); }
    }
    // SOURCE_LINKED_EQUIPMENT_PATCH
    // SOURCE_LINKED_LOAD_GUARD

    public static class CharacterEquipmentRecoverySmoke
    {
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly MethodInfo Wrapper = typeof(ManagedCharacterEquipmentRestorePatch).GetMethod("RestoreEquipment", StaticPrivate)!;
        private static readonly MethodInfo Transpiler = typeof(ManagedCharacterEquipmentRestorePatch).GetMethod("Transpiler", StaticPrivate)!;
        private static readonly MethodInfo Prefix = typeof(ManagedCharacterPlayerLoadPatch).GetMethod("Prefix", StaticPrivate)!;
        private static readonly MethodInfo Finalizer = typeof(ManagedCharacterPlayerLoadPatch).GetMethod("Finalizer", StaticPrivate)!;
        private static readonly MethodInfo Equip = AccessTools.Method(typeof(Player), "EquipInventoryItems")!;
        private static int assertions;
        private static void Check(bool value, string reason)
        {
            assertions++; if (!value) throw new InvalidOperationException(reason);
        }
        private static Player Reset()
        {
            ServerManagerRuntime.Guarded = true;
            ServerManagerRuntime.IsPlayerLoadInProgress = false;
            ServerManagerRuntime.CharacterLoadSaveBlocked = false;
            ServerManagerRuntime.Rejections.Clear(); ServerManagerRuntime.Cleanups = 0;
            CharacterPoisonPersistence.Completions.Clear(); CharacterPoisonPersistence.BeforeCalls = 0;
            ServerManagerPlugin.Log.Warnings.Clear(); ServerManagerPlugin.Log.Failure = null;
            return new Player();
        }
        private static List<CodeInstruction> Tail(bool nops = false)
        {
            var code = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Player), "CoreLoad")),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Stfld, AccessTools.Field(typeof(Player), "m_isLoading")),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList")),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, Equip),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Player), "UpdateEvents")),
                new CodeInstruction(OpCodes.Ret)
            };
            if (nops) {
                for (int i = code.Count - 1; i >= 0; i--) code.Insert(i, new CodeInstruction(OpCodes.Nop));
                code.Add(new CodeInstruction(OpCodes.Nop));
            }
            return code;
        }
        private static List<CodeInstruction> Transform(List<CodeInstruction> code)
        {
            var generator = new DynamicMethod("transpilerLabels", typeof(void), Type.EmptyTypes).GetILGenerator();
            object?[] args = Transpiler.GetParameters().Select(parameter =>
                typeof(IEnumerable<CodeInstruction>).IsAssignableFrom(parameter.ParameterType) ? (object)code :
                parameter.ParameterType == typeof(ILGenerator) ? generator :
                typeof(MethodBase).IsAssignableFrom(parameter.ParameterType) ? AccessTools.Method(typeof(Player), "Load") :
                throw new InvalidOperationException("Unsupported transpiler parameter: " + parameter.Name)).ToArray();
            return ((IEnumerable<CodeInstruction>)Transpiler.Invoke(null, args)!).ToList();
        }
        private static Action<Player> Emit(List<CodeInstruction> code)
        {
            var method = new DynamicMethod("restoredPlayerLoadTail", typeof(void), new[] { typeof(Player) }, typeof(Player), true);
            ILGenerator il = method.GetILGenerator();
            foreach (CodeInstruction instruction in code) {
                if (instruction.operand is MethodInfo member) il.Emit(instruction.opcode, member);
                else if (instruction.operand is FieldInfo field) il.Emit(instruction.opcode, field);
                else if (instruction.operand == null) il.Emit(instruction.opcode);
                else throw new InvalidOperationException("Unsupported emitted operand.");
            }
            return (Action<Player>)method.CreateDelegate(typeof(Action<Player>));
        }
        private static Exception? InvokeWrapper(Player player)
        {
            try { Wrapper.Invoke(null, new object[] { player }); return null; }
            catch (TargetInvocationException error) { return error.InnerException; }
        }
        private static Exception? RunLoad(Player player, Action<Player> body, bool skipOriginal = false)
        {
            object?[] prefixArguments = { player, null };
            bool run = (bool)Prefix.Invoke(null, prefixArguments)! && !skipOriginal;
            Exception? incoming = null;
            if (run) { try { body(player); } catch (Exception error) { incoming = error; } }
            try { return (Exception?)Finalizer.Invoke(null, new object?[] { player, prefixArguments[1], run, incoming }); }
            catch (TargetInvocationException error) { return error.InnerException; }
        }
        private static void CheckPreserved(Player player, Item first, Item second, bool attempted)
        {
            Check(player.Inventory.Count == 2 && ReferenceEquals(player.Inventory[0], first) && ReferenceEquals(player.Inventory[1], second),
                "Equipment recovery must not delete, replace, or reorder loaded items.");
            Check(player.CustomData.Count == 1 && player.CustomData["other-mod:opaque"] == "preserve exactly",
                "Equipment recovery must not rewrite the character custom-data dictionary.");
            Check(first.CustomData["item-mod:opaque"] == "unchanged" && second.CustomData.Count == 1 && second.CustomData["item-mod:opaque"] == "unchanged",
                "Equipment recovery must retain unknown item custom data.");
            Check(!attempted || first.Equipped && first.CustomData["equipment-mod:visited"] == "yes",
                "Recovery must not roll back a mod's partial equipment restoration.");
        }
        private static void CheckUnchanged(List<CodeInstruction> code, string scenario)
        {
            CodeInstruction[] before = code.Select(instruction => new CodeInstruction(instruction)).ToArray();
            int warnings = ServerManagerPlugin.Log.Warnings.Count;
            List<CodeInstruction> result = Transform(code);
            Check(result.Count == before.Length, scenario + ": unsafe fallback changed instruction count.");
            for (int i = 0; i < before.Length; i++) {
                Check(result[i].opcode == before[i].opcode && Equals(result[i].operand, before[i].operand) &&
                    result[i].labels.SequenceEqual(before[i].labels) && result[i].blocks.SequenceEqual(before[i].blocks),
                    scenario + ": unsafe fallback changed an original instruction or metadata.");
            }
            Check(ServerManagerPlugin.Log.Warnings.Count == warnings + 1, scenario + ": unknown layout must produce one warning.");
        }
        private static void RunTranspilerProbes()
        {
            Reset();
            foreach (bool nops in new[] { false, true }) {
                List<CodeInstruction> input = Tail(nops);
                int equipmentIndex = input.FindIndex(instruction => Equals(instruction.operand, Equip));
                var generator = new DynamicMethod("labels", typeof(void), Type.EmptyTypes).GetILGenerator();
                input[equipmentIndex].labels.Add(generator.DefineLabel());
                input[equipmentIndex].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
                input[equipmentIndex].blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
                CodeInstruction[] before = input.Select(instruction => new CodeInstruction(instruction)).ToArray();
                List<CodeInstruction> result = Transform(input);
                Check(result.Count == before.Length, "Only the one equipment call may change.");
                for (int i = 0; i < before.Length; i++) {
                    Check(result[i].labels.SequenceEqual(before[i].labels) && result[i].blocks.SequenceEqual(before[i].blocks),
                        "Call replacement must preserve labels and exception-block metadata.");
                    Check(result[i].opcode == before[i].opcode && Equals(result[i].operand, i == equipmentIndex ? Wrapper : before[i].operand),
                        "The transpiler changed a core/other tail instruction or missed the equipment call.");
                }
            }
            Check(ServerManagerPlugin.Log.Warnings.Count == 0, "Recognized normal-return tails should not warn.");
            CheckUnchanged(new List<CodeInstruction> { new CodeInstruction(OpCodes.Ret) }, "missing equipment call");
            var changed = Tail(); changed[3].opcode = OpCodes.Ldc_I4_1;
            CheckUnchanged(changed, "loading flag is not cleared");
            changed = Tail(); changed[10].operand = AccessTools.Method(typeof(Player), "CoreLoad");
            CheckUnchanged(changed, "different final continuation");
            changed = Tail(); changed.Insert(9, new CodeInstruction(OpCodes.Ldnull));
            CheckUnchanged(changed, "extra instruction in restore tail");
            changed = Tail(); changed.AddRange(Tail());
            CheckUnchanged(changed, "duplicate normal-return tail");
            changed = Tail(); changed[1].operand = Equip;
            CheckUnchanged(changed, "second equipment call outside tail");
            changed = Tail(); changed[8].opcode = OpCodes.Ldftn;
            CheckUnchanged(changed, "non-call equipment operand");
        }
        private static void RunWrapperProbes()
        {
            Player player = Reset();
            ServerManagerRuntime.IsPlayerLoadInProgress = true;
            Check(InvokeWrapper(player) == null && player.EquipmentCalls == 1, "A healthy equipment pass must execute exactly once.");
            Check(ServerManagerPlugin.Log.Warnings.Count == 0, "A healthy equipment pass should not warn.");
            foreach (int mode in new[] { 0, 1, 2, 3 }) {
                player = Reset();
                ServerManagerRuntime.IsPlayerLoadInProgress = mode != 2;
                ServerManagerRuntime.Guarded = mode != 1;
                ServerManagerRuntime.CharacterLoadSaveBlocked = mode == 3;
                Exception original = new InvalidOperationException("equipment mod fixture");
                player.EquipmentFailure = original;
                Item first = player.Inventory[0], second = player.Inventory[1];
                Exception? propagated = InvokeWrapper(player);
                Check(mode == 0 ? propagated == null : ReferenceEquals(propagated, original),
                    "Only a nonfatal managed, active, unblocked load may recover equipment failure.");
                Check(player.EquipmentCalls == 1, "Failed equipment must not be retried.");
                Check(original.StackTrace?.Contains("Player.EquipInventoryItems") == true, "The original equipment throw stack must survive reflection dispatch.");
                Check(ServerManagerPlugin.Log.Warnings.Count == (mode == 0 ? 1 : 0), "Only an actually recovered failure should warn.");
                Check(ServerManagerRuntime.Rejections.Count == 0, "The wrapper itself must not reject/disconnect.");
                CheckPreserved(player, first, second, true);
            }
            player = Reset();
            Exception nestedNonfatal = new InvalidOperationException("nested nonfatal equipment");
            player.EquipmentFailure = new TargetInvocationException(new TargetInvocationException(nestedNonfatal));
            Check(ReferenceEquals(InvokeWrapper(player), nestedNonfatal), "Nonfatal reflection wrappers must also unwrap to the original error outside managed loading.");
            foreach (int transition in new[] { 0, 1, 2 }) {
                player = Reset(); ServerManagerRuntime.IsPlayerLoadInProgress = true;
                Exception lateFailure = new InvalidOperationException("equipment changed session state");
                player.EquipmentFailure = lateFailure;
                player.DuringEquipment = () => {
                    if (transition == 0) ServerManagerRuntime.IsPlayerLoadInProgress = false;
                    else if (transition == 1) ServerManagerRuntime.Guarded = false;
                    else ServerManagerRuntime.CharacterLoadSaveBlocked = true;
                };
                Check(ReferenceEquals(InvokeWrapper(player), lateFailure) && ServerManagerPlugin.Log.Warnings.Count == 0,
                    "Recovery eligibility must be checked when equipment fails, after any state change by another mod.");
            }
            Reset(); ServerManagerRuntime.IsPlayerLoadInProgress = true;
            Check(InvokeWrapper(null!) is TargetException && ServerManagerPlugin.Log.Warnings.Count == 0,
                "Reflection target/setup errors must not be swallowed as recoverable equipment invocation failures.");
            foreach (Exception fatal in new Exception[] { new OutOfMemoryException("fatal fixture"), new StackOverflowException("fatal fixture"),
                new AccessViolationException("fatal fixture"), new AppDomainUnloadedException("fatal fixture") }) {
                foreach (bool nested in new[] { false, true }) {
                    player = Reset(); ServerManagerRuntime.IsPlayerLoadInProgress = true;
                    player.EquipmentFailure = nested ? new TargetInvocationException(new TargetInvocationException(fatal)) : fatal;
                    Exception? propagated = InvokeWrapper(player);
                    Check(propagated != null, "Fatal equipment errors, including nested reflection wrappers, must propagate.");
                    Exception innermost = propagated!;
                    while (innermost is TargetInvocationException invocation && invocation.InnerException != null) innermost = invocation.InnerException;
                    Check(ReferenceEquals(innermost, fatal), "Fatal propagation must preserve the original exception identity.");
                    Check(ServerManagerPlugin.Log.Warnings.Count == 0 && player.EquipmentCalls == 1, "Fatal errors must not recover, warn as recovered, or retry.");
                }
            }
        }
        private static void RunGuardedFlowProbes()
        {
            // Structural probes cover nops even after ret; emitted CLR methods
            // must not fall off their body through such unreachable padding.
            Action<Player> body = Emit(Transform(Tail()));
            foreach (bool failEquipment in new[] { false, true }) {
                Player player = Reset();
                if (failEquipment) player.EquipmentFailure = new InvalidOperationException("recoverable equipment");
                Item first = player.Inventory[0], second = player.Inventory[1];
                Check(RunLoad(player, body) == null, "A healthy/recovered managed load should complete.");
                Check(string.Join(",", player.Flow) == "core,available,equipment,events", "Recovery must continue the original Player.Load tail exactly once; observed: " + string.Join(",", player.Flow) +
                    "; rejected: " + string.Join(";", ServerManagerRuntime.Rejections));
                Check(!player.IsLoading && !ServerManagerRuntime.IsPlayerLoadInProgress && ServerManagerRuntime.Cleanups == 1,
                    "The normal tail must clear game loading state and the existing finalizer must unwind load depth.");
                Check(!ServerManagerRuntime.CharacterLoadSaveBlocked && ServerManagerRuntime.Rejections.Count == 0,
                    "Recovered equipment must not set the no-save latch or reject the session.");
                Check(CharacterPoisonPersistence.Completions.SequenceEqual(new[] { true }), "Successful poison completion must remain after the recovered tail.");
                CheckPreserved(player, first, second, true);
                if (failEquipment) Check(!second.Equipped, "Recovery must not reset flags or fake completion of later equipment items.");
            }
            foreach (int failurePoint in new[] { 0, 1, 2 }) {
                Player player = Reset();
                Exception failure = new InvalidDataException("non-equipment restoration fixture");
                if (failurePoint == 0) player.CoreFailure = failure;
                else if (failurePoint == 1) player.AvailableFailure = failure;
                else player.EventsFailure = failure;
                Check(RunLoad(player, body) == null, "The existing strict guard owns nonfatal non-equipment failures.");
                Check(ServerManagerRuntime.CharacterLoadSaveBlocked && ServerManagerRuntime.Rejections.Count == 1 &&
                    ReferenceEquals(ServerManagerRuntime.Rejections[0], failure), "Core/adjacent continuation failure must retain strict no-save rejection.");
                Check(CharacterPoisonPersistence.Completions.Count == 0 && ServerManagerRuntime.Cleanups == 1 && !ServerManagerRuntime.IsPlayerLoadInProgress,
                    "Failed general loads must skip successful poison completion and still unwind.");
                Check(ServerManagerPlugin.Log.Warnings.Count == 0, "A non-equipment error must not be described as recovered equipment.");
            }
            Player skipped = Reset();
            Check(RunLoad(skipped, body, true) == null && skipped.CoreCalls == 0 && ServerManagerRuntime.CharacterLoadSaveBlocked &&
                ServerManagerRuntime.Rejections.Count == 1, "Another prefix skipping Player.Load must still trigger strict rejection.");
            Player blocked = Reset(); ServerManagerRuntime.CharacterLoadSaveBlocked = true;
            Check(RunLoad(blocked, body) == null && blocked.EquipmentCalls == 0 && blocked.CoreCalls == 0 && ServerManagerRuntime.Cleanups == 1,
                "A preexisting no-save latch must still skip the complete load, including equipment.");
            Player unmanaged = Reset(); ServerManagerRuntime.Guarded = false;
            Exception unmanagedError = new InvalidOperationException("unmanaged equipment"); unmanaged.EquipmentFailure = unmanagedError;
            Check(ReferenceEquals(RunLoad(unmanaged, body), unmanagedError) && unmanaged.EventCalls == 0 &&
                ServerManagerRuntime.Rejections.Count == 0 && !ServerManagerRuntime.CharacterLoadSaveBlocked,
                "An unmanaged equipment error must propagate without strict managed rejection or false continuation.");
            foreach (bool nested in new[] { false, true }) {
                Player fatalPlayer = Reset();
                Exception fatal = new OutOfMemoryException("fatal guarded flow");
                fatalPlayer.EquipmentFailure = nested ? new TargetInvocationException(new TargetInvocationException(fatal)) : fatal;
                Exception? propagated = RunLoad(fatalPlayer, body);
                Check(ReferenceEquals(propagated, fatal), "The equipment wrapper must expose a nested fatal error to the existing fatal-aware load guard.");
                Check(ServerManagerRuntime.Rejections.Count == 0 && !ServerManagerRuntime.CharacterLoadSaveBlocked &&
                    fatalPlayer.EventCalls == 0 && ServerManagerRuntime.Cleanups == 1 && !ServerManagerRuntime.IsPlayerLoadInProgress,
                    "Fatal equipment errors must bypass recovery/rejection and still unwind via the strict finalizer.");
            }
            foreach (bool fatalLogging in new[] { false, true }) {
                Player logPlayer = Reset();
                logPlayer.EquipmentFailure = new InvalidOperationException("equipment before logging error");
                Exception logFailure = fatalLogging ? new OutOfMemoryException("fatal logger") : new InvalidOperationException("logger");
                ServerManagerPlugin.Log.Failure = logFailure;
                Exception? propagated = RunLoad(logPlayer, body);
                Check(fatalLogging ? ReferenceEquals(propagated, logFailure) && logPlayer.EventCalls == 0 : propagated == null && logPlayer.EventCalls == 1,
                    "A broken diagnostic sink must not reject recovered data; fatal logging failures still propagate.");
                Check(ServerManagerRuntime.Rejections.Count == 0 && !ServerManagerRuntime.CharacterLoadSaveBlocked &&
                    ServerManagerRuntime.Cleanups == 1 && !ServerManagerRuntime.IsPlayerLoadInProgress,
                    "Diagnostic failure must retain fatal policy and always unwind the existing load guard.");
            }
        }
        public static int Run()
        {
            RunTranspilerProbes(); RunWrapperProbes(); RunGuardedFlowProbes();
            return assertions;
        }
    }
}
'@
$linkedHarness = $harness.Replace('// SOURCE_LINKED_EQUIPMENT_PATCH', $equipmentMatch[0].Value).
    Replace('// SOURCE_LINKED_LOAD_GUARD', $guardMatch[0].Value).
    Replace('// SOURCE_LINKED_FATAL_FILTER', $fatalMatch[0].Value)
Add-Type -TypeDefinition $linkedHarness
$assertions = [CharacterEquipmentRecoveryFixture.CharacterEquipmentRecoverySmoke]::Run()
Write-Host "PASS: $assertions source-linked equipment recovery, transpiler fallback, data preservation and strict-load-guard assertions (isolated .NET; not in-game Harmony/Mono)."

# Prove the tests discriminate against the two critical regressions: reverting
# to an unprotected equipment call and discarding the equipment throw origin.
$oldCallHarness = $linkedHarness.Replace('namespace CharacterEquipmentRecoveryFixture', 'namespace CharacterEquipmentOldCallFixture').
    Replace('call.operand = wrapper;', 'call.operand = EquipInventoryItems;')
Assert ($oldCallHarness -ne $linkedHarness -and $equipmentMatch[0].Value.Contains('call.operand = wrapper;')) 'Cannot construct the unprotected-call negative control.'
Add-Type -TypeDefinition $oldCallHarness
$negativeError = ''
try { [CharacterEquipmentOldCallFixture.CharacterEquipmentRecoverySmoke]::Run() | Out-Null } catch { $negativeError = $_.ToString() }
Assert ($negativeError.Contains('transpiler changed a core/other tail instruction or missed the equipment call')) 'The unprotected-call negative control did not fail at the expected assertion.'

$oldThrowHarness = $linkedHarness.Replace('namespace CharacterEquipmentRecoveryFixture', 'namespace CharacterEquipmentOldThrowFixture').
    Replace('ExceptionDispatchInfo.Capture(error).Throw();', 'throw error;')
Assert ($equipmentMatch[0].Value.Contains('ExceptionDispatchInfo.Capture(error).Throw();')) 'Cannot construct the lost-origin negative control.'
Add-Type -TypeDefinition $oldThrowHarness
$negativeError = ''
try { [CharacterEquipmentOldThrowFixture.CharacterEquipmentRecoverySmoke]::Run() | Out-Null } catch { $negativeError = $_.ToString() }
Assert ($negativeError.Contains('original equipment throw stack must survive reflection dispatch')) 'The lost-origin negative control did not fail at the expected assertion.'
Write-Host 'PASS: Unprotected-call and lost-exception-origin negative controls failed at the expected regression assertions.'
