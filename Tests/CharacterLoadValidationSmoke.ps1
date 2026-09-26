param([string]$Configuration = 'Debug', [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    & (Get-Command pwsh -ErrorAction Stop).Source -NoProfile -File $PSCommandPath -Configuration $Configuration -GamePath $GamePath
    if ($LASTEXITCODE -ne 0) { throw 'Character load validation failed.' }
    exit 0
}
$root = Split-Path -Parent $PSScriptRoot
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx/core/Mono.Cecil.dll')) | Out-Null
$dll = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root "bin/$Configuration/ServerManager.dll"))
function Method([string]$type, [string]$name) { $dll.MainModule.GetType("ServerManager.$type").Methods | Where-Object Name -eq $name | Select-Object -First 1 }
function Call($method, [string]$name) {
    $call = $method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $name } | Select-Object -First 1
    if ($null -eq $call) { throw "Missing $name in $($method.FullName)." }
    return $call
}
function Assert([bool]$value, [string]$reason) { if (-not $value) { throw $reason } }
try {
    $prefix = Method 'ManagedCharacterPlayerLoadPatch' 'Prefix'
    $finalizer = Method 'ManagedCharacterPlayerLoadPatch' 'Finalizer'
    Assert ($null -eq $dll.MainModule.GetType('ServerManager.CharacterLoadValidation')) 'Content comparison must not remain in the deployed DLL.'
    Assert ((Call $prefix 'ShouldValidatePlayerLoad').Offset -lt (Call $prefix 'BeforeLoad').Offset) 'Scope the managed load before adapter initialization.'
    foreach ($method in @($prefix, $finalizer)) {
        Assert (@($method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -in @('GetItemPrefab', 'GetInventory', 'GetSkills', 'ReadPlayerLoadSections', 'Validate', 'RequireAvailablePrefabs') }).Count -eq 0) 'Load guards must not revalidate restored content or the prefab catalog.'
        $null = Call $method 'RejectUnsafePlayerLoad'
        $null = Call $method 'IsFatal'
    }
    $null = Call $finalizer 'AfterLoad'
    $null = Call $finalizer 'ShouldValidatePlayerLoad'
    foreach ($name in @('Capture', 'Throw')) {
        Assert (@($finalizer.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $name -and
            $_.Operand.DeclaringType.FullName -eq 'System.Runtime.ExceptionServices.ExceptionDispatchInfo'
        }).Count -eq 1) "The deployed finalizer must preserve the incoming exception with ExceptionDispatchInfo.$name."
    }
    $cleanup = Call $finalizer 'AfterPlayerLoad'
    Assert (@($finalizer.Body.ExceptionHandlers | Where-Object { $_.HandlerType.ToString() -eq 'Finally' -and $_.HandlerStart.Offset -le $cleanup.Offset -and ($null -eq $_.HandlerEnd -or $cleanup.Offset -lt $_.HandlerEnd.Offset) }).Count -eq 1) 'Load suppression must always unwind.'
    foreach ($type in @('UnsafeCharacterProfileSavePatch', 'UnsafeCharacterCapturePatch', 'ManagedCharacterPlayerSavePatch')) {
        $null = Call (Method $type 'Prefix') 'get_CharacterLoadSaveSuppressed'
    }
    $reject = Method 'ServerManagerRuntime' 'RejectUnsafePlayerLoad'
    $latch = $reject.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -eq '_unsafePlayerLoadGame' } | Select-Object -First 1
    Assert ($null -ne $latch -and $latch.Offset -lt (Call $reject 'Close').Offset -and $latch.Offset -lt (Call $reject 'FailClient').Offset) 'Latch saves before either disconnect path.'
    $null = Call (Method 'ServerManagerRuntime' 'ShouldValidatePlayerLoad') 'get_CharacterLoadSaveBlocked'
    $null = Call (Method 'ServerManagerRuntime' 'get_CharacterLoadSaveSuppressed') 'get_IsPlayerLoadInProgress'
    $reset = Method 'ServerManagerRuntime' 'ResetLocalHostLifecycle'
    Assert (@($reset.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -eq '_unsafePlayerLoadGame' }).Count -eq 0) 'Host teardown must not clear the unsafe old Game latch.'
    Write-Host 'PASS: Character load/save patch ordering and lifecycle metadata.'
} finally { $dll.Dispose() }

# Execute the complete production patch and fatal filter, source-linked unchanged.
# Only scope/rejection/poison and Player/Harmony boundaries are inert. This does
# not run Harmony, Unity, save/disconnect code, or the game's Mono runtime; the
# checks above separately inspect the actual final bundled DLL.
$patchSource = Get-Content -LiteralPath (Join-Path $root 'RuntimePatches.cs') -Raw
$patchMatch = [regex]::Matches($patchSource, '(?ms)^internal static class ManagedCharacterPlayerLoadPatch\r?\n\{.*?^\}')
Assert ($patchMatch.Count -eq 1) 'Expected exactly one source-linked player-load patch.'
$fatalSource = Get-Content -LiteralPath (Join-Path $root 'Integrity/IntegrityModels.cs') -Raw
$fatalMatch = [regex]::Matches($fatalSource, '(?ms)^        internal static bool IsFatal\(Exception exception\)\r?\n        \{.*?^        \}')
Assert ($fatalMatch.Count -eq 1) 'Expected exactly one source-linked fatal exception filter.'
$harness = @'
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace CharacterLoadValidationFixture
{
    internal sealed class Player { }
    internal sealed class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    internal static class Priority { internal const int First = 800, Last = 0; }
    internal static class IntegrityCanonical
    {
        // SOURCE_LINKED_FATAL_FILTER
    }
    internal static class ServerManagerRuntime
    {
        internal static bool Guarded, Entered, CharacterLoadSaveBlocked;
        internal static Exception? RejectFailure;
        internal static readonly List<Exception> Rejections = new List<Exception>();
        internal static readonly List<bool> Cleanups = new List<bool>();
        internal static readonly List<string> Events = new List<string>();
        internal static bool BeforePlayerLoad(Player player) => Entered;
        internal static bool ShouldValidatePlayerLoad(Player player) => Guarded;
        internal static void RejectUnsafePlayerLoad(Exception error)
        {
            Events.Add("reject"); Rejections.Add(error);
            if (RejectFailure != null) throw RejectFailure;
        }
        internal static void AfterPlayerLoad(bool entered)
        {
            Events.Add("cleanup"); Cleanups.Add(entered);
        }
    }
    internal static class CharacterPoisonPersistence
    {
        internal static Exception? BeforeFailure, AfterFailure;
        internal static int BeforeCalls;
        internal static readonly List<bool> Completions = new List<bool>();
        internal static void BeforeLoad(Player player)
        {
            BeforeCalls++;
            if (BeforeFailure != null) throw BeforeFailure;
        }
        internal static void AfterLoad(Player player, bool completed)
        {
            ServerManagerRuntime.Events.Add("poison"); Completions.Add(completed);
            if (AfterFailure != null) throw AfterFailure;
        }
    }
    // SOURCE_LINKED_LOAD_PATCH

    public static class CharacterLoadValidationSmoke
    {
        private static readonly Player Player = new Player();
        private static readonly MethodInfo Prefix = typeof(ManagedCharacterPlayerLoadPatch)
            .GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo Finalizer = typeof(ManagedCharacterPlayerLoadPatch)
            .GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static int assertions;

        private static void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        private static void Reset(bool guarded = true)
        {
            ServerManagerRuntime.Guarded = guarded;
            ServerManagerRuntime.Entered = guarded;
            ServerManagerRuntime.CharacterLoadSaveBlocked = false;
            ServerManagerRuntime.RejectFailure = null;
            ServerManagerRuntime.Rejections.Clear();
            ServerManagerRuntime.Cleanups.Clear();
            ServerManagerRuntime.Events.Clear();
            CharacterPoisonPersistence.BeforeFailure = CharacterPoisonPersistence.AfterFailure = null;
            CharacterPoisonPersistence.BeforeCalls = 0;
            CharacterPoisonPersistence.Completions.Clear();
        }
        private static object Begin(out bool runOriginal)
        {
            object?[] arguments = { Player, null };
            runOriginal = (bool)Prefix.Invoke(null, arguments)!;
            return arguments[1]!;
        }
        private static void Finish(object? state, bool runOriginal, Exception? incoming,
            Exception? expectedReturn = null, Exception? expectedEscape = null, bool entered = true)
        {
            Exception? returned = null, escaped = null;
            try { returned = (Exception?)Finalizer.Invoke(null, new object?[] { Player, state, runOriginal, incoming }); }
            catch (TargetInvocationException error) { escaped = error.InnerException; }
            Check(ReferenceEquals(returned, expectedReturn), "Finalizer changed returned exception identity.");
            Check(ReferenceEquals(escaped, expectedEscape), "Finalizer changed exception propagation.");
            Check(ServerManagerRuntime.Cleanups.Count == 1 && ServerManagerRuntime.Cleanups[0] == entered,
                "Every finalizer path must unwind once with the original Entered flag.");
            Check(ServerManagerRuntime.Events[ServerManagerRuntime.Events.Count - 1] == "cleanup",
                "Unwind must follow poison/rejection, including exceptional exits.");
        }
        private static void Rejected(Exception error)
        {
            Check(ServerManagerRuntime.Rejections.Count == 1 && ReferenceEquals(ServerManagerRuntime.Rejections[0], error),
                "Managed failure must reject once with the original exception object.");
        }
        private static void Poison(bool? completed)
        {
            Check(CharacterPoisonPersistence.Completions.Count == (completed.HasValue ? 1 : 0) &&
                (!completed.HasValue || CharacterPoisonPersistence.Completions[0] == completed.Value),
                "Poison completion must retain the original successful/failed/skipped policy.");
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EarlierPrefixFailure() => throw new InvalidOperationException("earlier prefix failure");
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LaterPostfixFailure() => throw new InvalidDataException("later postfix failure");
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception CaptureOrigin(bool earlierPrefix)
        {
            try { if (earlierPrefix) EarlierPrefixFailure(); else LaterPostfixFailure(); }
            catch (Exception error) { return error; }
            throw new InvalidOperationException("The throwing origin was not executed.");
        }
        public static void RunStackProbes(bool expectPreserved)
        {
            foreach (bool earlierPrefix in new[] { true, false })
            {
                Reset();
                object? state = earlierPrefix ? null : Begin(out _);
                string origin = earlierPrefix ? nameof(EarlierPrefixFailure) : nameof(LaterPostfixFailure);
                Exception incoming = CaptureOrigin(earlierPrefix);
                Check(incoming.StackTrace?.Contains(origin) == true, "The fixture must supply an actually thrown exception.");
                Finish(state, !earlierPrefix, incoming, entered: !earlierPrefix);
                Rejected(incoming); Poison(null);
                Check(incoming.ToString().Contains(origin) == expectPreserved,
                    "Rejected diagnostics lost the original " + origin + " stack frame.");
                Check(incoming.StackTrace?.Contains("ManagedCharacterPlayerLoadPatch.Finalizer") == true,
                    "The fixture must exercise the production finalizer's exception path.");
            }
        }
        public static int Run()
        {
            RunStackProbes(true);

            Reset();
            object state = Begin(out bool runOriginal);
            Check(runOriginal, "Healthy guarded prefix must run Player.Load.");
            Finish(state, runOriginal, null); Poison(true);
            Check(ServerManagerRuntime.Rejections.Count == 0, "Healthy loads must not reject.");

            Reset();
            state = Begin(out _);
            Finish(state, false, null); Poison(null);
            Check(ServerManagerRuntime.Rejections.Count == 1 && ServerManagerRuntime.Rejections[0] is InvalidDataException &&
                ServerManagerRuntime.Rejections[0].Message == "Another patch skipped the managed character load.",
                "Skipped managed loads must retain their existing rejection.");

            Reset();
            var prefixFailure = new InvalidDataException("poison prefix fixture");
            CharacterPoisonPersistence.BeforeFailure = prefixFailure;
            state = Begin(out runOriginal);
            Check(!runOriginal, "A failed guarded prefix must skip Player.Load.");
            Finish(state, runOriginal, CaptureOrigin(false)); Rejected(prefixFailure); Poison(null);

            Reset();
            ServerManagerRuntime.CharacterLoadSaveBlocked = true;
            state = Begin(out runOriginal);
            Check(!runOriginal && CharacterPoisonPersistence.BeforeCalls == 0, "Blocked load must not initialize the poison adapter.");
            Finish(state, runOriginal, null); Poison(null);
            Check(ServerManagerRuntime.Rejections.Count == 0, "Already blocked loads must not be rejected again.");

            foreach (Exception fatal in new Exception[] { new OutOfMemoryException("fixture"),
                new StackOverflowException("fixture"), new AccessViolationException("fixture"), new AppDomainUnloadedException("fixture") })
            {
                foreach (bool hasState in new[] { true, false })
                {
                    Reset();
                    object? fatalState = hasState ? Begin(out _) : null;
                    Finish(fatalState, false, fatal, expectedReturn: fatal, entered: hasState); Poison(null);
                    Check(ServerManagerRuntime.Rejections.Count == 0, "Fatal incoming errors must bypass managed rejection.");
                }
            }

            foreach (bool hasState in new[] { true, false })
            {
                Reset(false);
                object? unmanagedState = hasState ? Begin(out _) : null;
                Exception incoming = CaptureOrigin(false);
                Finish(unmanagedState, true, incoming, expectedReturn: incoming, entered: false); Poison(false);
                Check(ServerManagerRuntime.Rejections.Count == 0 && incoming.ToString().Contains(nameof(LaterPostfixFailure)),
                    "Unmanaged failures must pass through unchanged without rejection.");
            }
            foreach (bool completed in new[] { true, false })
            {
                Reset(false);
                state = Begin(out _);
                Finish(state, completed, null, entered: false); Poison(completed);
                Check(ServerManagerRuntime.Rejections.Count == 0, "Unmanaged success/skip must remain unmanaged.");
            }

            foreach (bool guarded in new[] { true, false })
            {
                foreach (bool fatal in new[] { false, true })
                {
                    Reset(guarded);
                    state = Begin(out _);
                    Exception poisonError = fatal ? new OutOfMemoryException("poison fixture") : new InvalidDataException("poison fixture");
                    CharacterPoisonPersistence.AfterFailure = poisonError;
                    Finish(state, true, null, expectedEscape: guarded && !fatal ? null : poisonError, entered: guarded);
                    Poison(true);
                    if (guarded && !fatal) Rejected(poisonError);
                    else Check(ServerManagerRuntime.Rejections.Count == 0, "Fatal/unmanaged poison errors must propagate.");
                }
            }

            Reset();
            state = Begin(out _);
            Exception rejectionFailure = new InvalidOperationException("rejection boundary fixture");
            Exception originalFailure = CaptureOrigin(false);
            ServerManagerRuntime.RejectFailure = rejectionFailure;
            Finish(state, true, originalFailure, expectedEscape: rejectionFailure); Rejected(originalFailure); Poison(null);
            return assertions;
        }
    }
}
'@
$linkedHarness = $harness.Replace('// SOURCE_LINKED_LOAD_PATCH', $patchMatch[0].Value).
    Replace('// SOURCE_LINKED_FATAL_FILTER', $fatalMatch[0].Value)
Add-Type -TypeDefinition $linkedHarness
$assertions = [CharacterLoadValidationFixture.CharacterLoadValidationSmoke]::Run()

# Negative control: compile the same source-linked patch with only the old
# throw restored. Both actually-thrown prefix/postfix origins must disappear,
# proving the regression assertion would catch the pre-fix implementation.
$preservingThrow = 'ExceptionDispatchInfo.Capture(__exception).Throw();'
Assert ($patchMatch[0].Value.Contains($preservingThrow)) 'Cannot locate the production stack-preserving dispatch for the negative control.'
$oldThrowHarness = $linkedHarness.Replace('namespace CharacterLoadValidationFixture', 'namespace CharacterLoadOldThrowFixture').
    Replace($preservingThrow, 'throw __exception;')
Add-Type -TypeDefinition $oldThrowHarness
[CharacterLoadOldThrowFixture.CharacterLoadValidationSmoke]::RunStackProbes($false)
Write-Host "PASS: $assertions source-linked load finalizer assertions and old-throw negative controls (isolated .NET runtime; not an in-game Harmony/Mono test)."
