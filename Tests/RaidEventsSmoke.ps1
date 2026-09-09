param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$script:checks = 0
function Assert-True([bool]$Condition, [string]$Message) {
    ++$script:checks
    if (-not $Condition) { throw $Message }
}
Assert-True (Test-Path -LiteralPath $pluginPath) 'Build ServerManager before the raid smoke test.'
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll', 'UnityEngine.dll', 'assembly_utils.dll',
    'SoftReferenceableAssets.dll', 'com.rlabrecque.steamworks.net.dll', 'Splatform.dll', 'assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (Split-Path -Parent $pluginPath) $name))) | Out-Null
}
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
public sealed class RaidEventRecord {
    public string Kind;
    public string Reliability;
    public Dictionary<string, string> Fields;
}
public static class RaidEventProbe {
    public static readonly List<RaidEventRecord> Events = new List<RaidEventRecord>();
    public static bool SameObject(object left, object right) { return Object.ReferenceEquals(left, right); }
    public static void Record(string kind, string reliability, Dictionary<string, string> fields) {
        Events.Add(new RaidEventRecord { Kind = kind, Reliability = reliability,
            Fields = new Dictionary<string, string>(fields) });
    }
}
'@

# Only in-memory copies are instrumented. Raid polling, transition detection,
# cached coordinates, validation, formatting and PublishRaid remain production.
# Replace Unity's native object-null comparison and the final external sink.
# Keep production SafeText, but isolate its unrelated combat-field initializer:
# Windows PowerShell's CLR4 cannot reflect Character's newer interface metadata.
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
$eventIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.Events.ServerEventRuntime'
try {
    $observationIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.Events.ClientEventObservation'
    $observationInitializer = $observationIL.Methods | Where-Object Name -eq '.cctor'
    $characterTokens = @($observationInitializer.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldtoken -and $_.Operand.FullName -eq 'Character'
    })
    Assert-True ($characterTokens.Count -eq 1 -and @($observationInitializer.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stsfld -and $_.Operand.Name -eq 'LastHitField'
    }).Count -eq 1) 'Fixture must isolate only the unused combat-field reflection initializer.'
    # Object has no m_lastHit: the unused field becomes null without loading Character.
    $characterTokens[0].Operand = $definition.MainModule.TypeSystem.Object

    foreach ($methodName in @('Initialize', 'OnServerStarted', 'OnServerShutdown', 'Shutdown')) {
        $method = $eventIL.Methods | Where-Object Name -eq $methodName
        $calls = @($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'ResetRaidState'
        })
        Assert-True ($calls.Count -gt 0) "Raid cache must reset in $methodName."
    }
    $pollIL = $eventIL.Methods | Where-Object Name -eq 'PollRaid'
    Assert-True (@($pollIL.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'GetCurrentRandomEvent'
    }).Count -eq 1) 'Polling reads the current random event once.'
    Assert-True (@($pollIL.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'm_pos'
    }).Count -eq 1) 'Raid center uses the actual public field, without reflection or player-position substitution.'
    $nullComparisons = 0
    foreach ($instruction in $pollIL.Body.Instructions) {
        if ($instruction.Operand -is [Mono.Cecil.MethodReference] -and
            $instruction.Operand.DeclaringType.FullName -eq 'UnityEngine.Object' -and
            $instruction.Operand.Name -eq 'op_Equality') {
            $instruction.Operand = $definition.MainModule.ImportReference([RaidEventProbe].GetMethod('SameObject'))
            ++$nullComparisons
        }
    }
    Assert-True ($nullComparisons -ge 2) 'Fixture must isolate the native Unity null comparisons.'
    $publishIL = $eventIL.Methods | Where-Object Name -eq 'Publish'
    $publishIL.Body.Instructions.Clear(); $publishIL.Body.ExceptionHandlers.Clear(); $publishIL.Body.Variables.Clear()
    foreach ($index in @(0, 1, 4)) {
        $publishIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg, $publishIL.Parameters[$index]))
    }
    $publishIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call,
        $definition.MainModule.ImportReference([RaidEventProbe].GetMethod('Record'))))
    $publishIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldnull))
    $publishIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
    $stream = [IO.MemoryStream]::new()
    try { $definition.Write($stream); $plugin = [Reflection.Assembly]::Load($stream.ToArray()) }
    finally { $stream.Dispose() }
} finally { $definition.Dispose() }

$runtime = $plugin.GetType('ServerManager.Events.ServerEventRuntime', $true)
$poll = $runtime.GetMethod('PollRaid', $static)
$reset = $runtime.GetMethod('ResetRaidState', $static)
$format = $runtime.GetMethod('TryFormatRaidCoordinates', $static)
$network = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([ZNet])
$system = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([RandEventSystem])
$networkInstance = [ZNet].GetField('m_instance', $static)
$serverFlag = [ZNet].GetField('m_isServer', $static)
$systemInstance = [RandEventSystem].GetField('m_instance', $static)
$randomField = [RandEventSystem].GetField('m_randomEvent', $instance)
$activeField = [RandEventSystem].GetField('m_activeEvent', $instance)
$oldNetwork = $networkInstance.GetValue($null)
$oldServer = $serverFlag.GetValue($null)
$oldSystem = $systemInstance.GetValue($null)
$oldCulture = [Globalization.CultureInfo]::CurrentCulture

function Set-State([string]$Name, [object]$Value) { $runtime.GetField($Name, $static).SetValue($null, $Value) }
function New-Raid([string]$Name, [float]$X = 0, [float]$Y = 0, [float]$Z = 0) {
    $raid = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([RandomEvent])
    $raid.m_name = $Name
    $raid.m_pos = [UnityEngine.Vector3]::new($X, $Y, $Z)
    return $raid
}
function Set-Raids([object]$Random, [object]$Fallback = $null) {
    $randomField.SetValue($system, $Random); $activeField.SetValue($system, $Fallback)
}
function Poll-Raid([switch]$RespectInterval) {
    if (-not $RespectInterval) { Set-State '_nextRaidPollTimestamp' ([long]0) }
    $poll.Invoke($null, $null) | Out-Null
}
function Reset-Fixture {
    $reset.Invoke($null, $null) | Out-Null
    Set-State '_initialized' $true; Set-State '_serverStarted' $true
    Set-State '_worldReady' $true; Set-State '_shutdownStarted' $false
    $networkInstance.SetValue($null, $network); $serverFlag.SetValue($null, $true)
    $systemInstance.SetValue($null, $system)
    Set-Raids $null
    [RaidEventProbe]::Events.Clear()
}
function Assert-Event([int]$Index, [string]$Kind, [string]$Name, [string]$Coordinates) {
    $event = [RaidEventProbe]::Events[$Index]
    Assert-True ($event.Kind -eq $Kind -and $event.Reliability -eq 'authoritative') 'Raid event kind/reliability must remain authoritative.'
    Assert-True ($event.Fields['raid'] -eq $Name) 'Raid event must retain the captured name.'
    $verb = if ($Kind -eq 'raid.started') { 'started' } else { 'ended' }
    if ($Coordinates) {
        Assert-True ($event.Fields['raid_coordinates'] -ceq $Coordinates) 'Raid event must contain the exact invariant cached center.'
        Assert-True ($event.Fields['message'] -ceq "Raid $Name $verb at [$Coordinates].") 'Known raid center must appear in the local log message.'
    } else {
        Assert-True (-not $event.Fields.ContainsKey('raid_coordinates')) 'Unknown raid center must omit the coordinate field.'
        Assert-True ($event.Fields['message'] -ceq "Raid $Name $verb.") 'Unknown raid center must preserve the previous message format.'
    }
}

try {
    Reset-Fixture
    Assert-True ($runtime.GetField('RaidPollTicks', $static).GetValue($null) -eq [Diagnostics.Stopwatch]::Frequency) 'Raid polling remains exactly one second.'
    Poll-Raid
    Assert-True ([RaidEventProbe]::Events.Count -eq 0) 'An initially empty raid state emits no event.'
    $first = New-Raid 'army_eikthyr' -370.6 39.6 1590.6
    Set-Raids $first
    Poll-Raid -RespectInterval
    Assert-True ([RaidEventProbe]::Events.Count -eq 0) 'Unexpired one-second interval suppresses another poll.'
    Poll-Raid
    Assert-Event 0 'raid.started' 'army_eikthyr' '-371, 40, 1591'
    $first.m_pos = [UnityEngine.Vector3]::new(0, 0, 0)
    Poll-Raid
    Assert-True ([RaidEventProbe]::Events.Count -eq 1) 'An ongoing instance does not repeat or overwrite its captured center.'
    Set-Raids $null
    Poll-Raid
    Assert-Event 1 'raid.ended' 'army_eikthyr' '-371, 40, 1591'
    Poll-Raid
    Assert-True ([RaidEventProbe]::Events.Count -eq 2) 'An ended raid does not emit a second end.'

    Reset-Fixture
    $first = New-Raid 'wolves' 1 2 3
    Set-Raids $first; Poll-Raid
    $second = New-Raid 'wolves' 10 20 30
    Set-Raids $second; Poll-Raid
    Assert-True ([RaidEventProbe]::Events.Count -eq 3) 'A new same-name instance ends the old raid and starts another.'
    Assert-Event 1 'raid.ended' 'wolves' '1, 2, 3'
    Assert-Event 2 'raid.started' 'wolves' '10, 20, 30'
    $second.m_name = 'trolls'; $second.m_pos = [UnityEngine.Vector3]::new(40, 50, 60)
    Poll-Raid
    Assert-Event 3 'raid.ended' 'wolves' '10, 20, 30'
    Assert-Event 4 'raid.started' 'trolls' '40, 50, 60'

    foreach ($coordinate in @(0, 999)) {
        Reset-Fixture
        Set-Raids $null (New-Raid 'forced_unknown' $coordinate $coordinate $coordinate)
        Poll-Raid; Assert-Event 0 'raid.started' 'forced_unknown' ''
        Set-Raids $null; Poll-Raid; Assert-Event 1 'raid.ended' 'forced_unknown' ''
    }
    Reset-Fixture
    Set-Raids (New-Raid 'true_origin') (New-Raid 'unrelated_fallback' 9 9 9)
    Poll-Raid; Assert-Event 0 'raid.started' 'true_origin' '0, 0, 0'

    foreach ($cultureName in @('fr-FR', 'ar-SA', 'en-US')) {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($cultureName)
        Reset-Fixture; Set-Raids (New-Raid 'rounding' -1.5 0.5 2.5)
        Poll-Raid; Assert-Event 0 'raid.started' 'rounding' '-2, 1, 3'
    }
    [Globalization.CultureInfo]::CurrentCulture = $oldCulture
    foreach ($bad in @([float]::NaN, [float]::PositiveInfinity, [float]::NegativeInfinity, [float]::MaxValue,
        [float]::MinValue, [float]2147483648, [float]-2147483904)) {
        foreach ($axis in @(0, 1, 2)) {
            $values = [float[]]@(1, 2, 3); $values[$axis] = $bad
            $arguments = [object[]]@([UnityEngine.Vector3]::new($values[0], $values[1], $values[2]), $null)
            Assert-True (-not [bool]$format.Invoke($null, $arguments) -and $null -eq $arguments[1]) 'Nonfinite/out-of-int32 center must be wholly unknown.'
        }
    }
    Reset-Fixture; Set-Raids (New-Raid 'int_boundaries' 2147483520 -2147483648 0)
    Poll-Raid; Assert-Event 0 'raid.started' 'int_boundaries' '2147483520, -2147483648, 0'
    Reset-Fixture
    $badRaid = New-Raid 'invalid_center' ([float]::NaN) 0 0
    Set-Raids $badRaid; Poll-Raid; Assert-Event 0 'raid.started' 'invalid_center' ''
    $badRaid.m_pos = [UnityEngine.Vector3]::new(8, 9, 10)
    Poll-Raid; Set-Raids $null; Poll-Raid; Assert-Event 1 'raid.ended' 'invalid_center' ''

    Reset-Fixture; Set-Raids (New-Raid 'old_world' 1 2 3); Poll-Raid
    $reset.Invoke($null, $null) | Out-Null
    foreach ($name in @('_activeRaid', '_activeRaidInstance', '_activeRaidCoordinates')) {
        Assert-True ($null -eq $runtime.GetField($name, $static).GetValue($null)) "Lifecycle reset must clear $name."
    }
    Set-Raids (New-Raid 'new_world' 4 5 6); Poll-Raid
    Assert-True ([RaidEventProbe]::Events.Count -eq 2) 'A reset never emits an old-world raid end into a new world.'
    Assert-Event 1 'raid.started' 'new_world' '4, 5, 6'
    foreach ($guard in @('_serverStarted', '_worldReady', '_shutdownStarted')) {
        Reset-Fixture; Set-Raids (New-Raid 'guarded' 1 2 3)
        Set-State $guard ($guard -eq '_shutdownStarted'); Poll-Raid
        Assert-True ([RaidEventProbe]::Events.Count -eq 0) "Poll remains guarded by $guard."
    }
    Reset-Fixture; Set-Raids (New-Raid 'client' 1 2 3)
    $serverFlag.SetValue($null, $false); Poll-Raid
    Assert-True ([RaidEventProbe]::Events.Count -eq 0) 'Clients never publish raid events.'
    Reset-Fixture; $systemInstance.SetValue($null, $null); Poll-Raid
    Assert-True ([RaidEventProbe]::Events.Count -eq 0) 'Missing event system is a safe empty state.'
    Write-Output "PASS: raid event centers ($script:checks assertions; production polling/formatting; no game/network/disk writes)."
} finally {
    [Globalization.CultureInfo]::CurrentCulture = $oldCulture
    $networkInstance.SetValue($null, $oldNetwork); $serverFlag.SetValue($null, $oldServer)
    $systemInstance.SetValue($null, $oldSystem)
}
