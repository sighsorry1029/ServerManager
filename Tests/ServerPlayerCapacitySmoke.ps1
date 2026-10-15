param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run ServerPlayerCapacitySmoke.ps1 with Windows PowerShell (powershell.exe); bundled Harmony 2.9 requires its compatible Desktop CLR.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    ++$script:assertions
    if (-not $Condition) { throw $Message }
}
foreach ($path in @($pluginPath, (Join-Path $managedRoot 'assembly_valheim.dll'),
    (Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll'))) {
    Assert-True (Test-Path -LiteralPath $path) "Capacity smoke prerequisite missing: $path. Build the plugin first."
}
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
foreach ($name in @('MonoMod.Utils.dll', 'MonoMod.RuntimeDetour.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GamePath ('BepInEx\core\' + $name)))) | Out-Null
}
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll', 'UnityEngine.dll',
    'assembly_utils.dll', 'SoftReferenceableAssets.dll', 'com.rlabrecque.steamworks.net.dll',
    'Splatform.dll', 'assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll', 'YamlDotNet.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (Split-Path -Parent $pluginPath) $name))) | Out-Null
}

function Get-Method($Definition, [string]$TypeName, [string]$Name) {
    $type = $Definition.MainModule.Types | Where-Object FullName -eq $TypeName | Select-Object -First 1
    $method = $type.Methods | Where-Object Name -eq $Name | Select-Object -First 1
    Assert-True ($null -ne $method -and $method.HasBody) "Missing compiled method $TypeName.$Name."
    return $method
}
function Get-Calls($Method, [string]$Name) {
    return @($Method.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $Name
    })
}
function Get-Integer($Instruction) {
    switch ($Instruction.OpCode.Name) {
        'ldc.i4.m1' { return -1 }
        'ldc.i4.0' { return 0 }
        'ldc.i4.1' { return 1 }
        'ldc.i4.2' { return 2 }
        'ldc.i4.3' { return 3 }
        'ldc.i4.4' { return 4 }
        'ldc.i4.5' { return 5 }
        'ldc.i4.6' { return 6 }
        'ldc.i4.7' { return 7 }
        'ldc.i4.8' { return 8 }
        'ldc.i4.s' { return [int]$Instruction.Operand }
        'ldc.i4' { return [int]$Instruction.Operand }
        default { return $null }
    }
}
. (Join-Path $PSScriptRoot 'CecilControlFlow.ps1')
function Assert-GuardProtectsCall($Method, [string]$GuardName, [string]$EffectName) {
    $guard = @(Get-Calls $Method $GuardName) | Select-Object -First 1
    $effect = @(Get-Calls $Method $EffectName) | Select-Object -First 1
    Assert-True ($null -ne $guard -and $null -ne $effect) "Advertisement lost $GuardName / $EffectName."
    $branch = $guard.Next
    while ($null -ne $branch -and $branch.OpCode.FlowControl.ToString() -ne 'Cond_Branch') { $branch = $branch.Next }
    Assert-True ($null -ne $branch -and
        -not (Test-CecilReachable $Method.Body.Instructions[0] $effect $branch) -and
        ((Test-CecilReachable $branch.Operand $effect) -xor (Test-CecilReachable $branch.Next $effect))) `
        "$GuardName must guard $EffectName on every normal control-flow path."
}

function Add-HarmonyCallFixture($Definition, $CapacityType, [string]$MethodName,
    [string]$CounterName, [bool]$MayThrow) {
    $module = $Definition.MainModule
    $counter = [Mono.Cecil.FieldDefinition]::new($CounterName,
        [Mono.Cecil.FieldAttributes]'Public,Static', $module.TypeSystem.Int32)
    $CapacityType.Fields.Add($counter)
    $calls = @($CapacityType.Methods | Where-Object HasBody | ForEach-Object {
        $_.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq 'HarmonyLib.Harmony' -and $_.Operand.Name -eq $MethodName
        }
    })
    Assert-True ($calls.Count -gt 0) "The capacity fixture must intercept actual Harmony.$MethodName calls."
    $index = 0
    foreach ($call in $calls) {
        $original = $call.Operand
        $shim = [Mono.Cecil.MethodDefinition]::new("Test$MethodName$index",
            [Mono.Cecil.MethodAttributes]'Private,Static', $original.ReturnType)
        if ($original.HasThis) {
            $shim.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($original.DeclaringType))
        }
        foreach ($parameter in $original.Parameters) {
            $shim.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($parameter.ParameterType))
        }
        $CapacityType.Methods.Add($shim)
        $il = $shim.Body.GetILProcessor()
        $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldsfld, $counter)
        $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1)
        $il.Emit([Mono.Cecil.Cil.OpCodes]::Add)
        $il.Emit([Mono.Cecil.Cil.OpCodes]::Stsfld, $counter)
        $success = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Nop)
        if ($MayThrow) {
            $throwAt = $CapacityType.Fields | Where-Object Name -eq 'TestThrowAtPatch'
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldsfld, $counter)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldsfld, $throwAt)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Bne_Un, $success)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldstr, 'Test-only patch installation failure.')
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Newobj,
                $module.ImportReference([InvalidOperationException].GetConstructor([Type[]]@([string]))))
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Throw)
        }
        $il.Append($success)
        if ($original.ReturnType.FullName -ne 'System.Void') { $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldnull) }
        $il.Emit([Mono.Cecil.Cil.OpCodes]::Ret)
        $call.OpCode = [Mono.Cecil.Cil.OpCodes]::Call
        $call.Operand = $shim
        ++$index
    }
}

$gameDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managedRoot 'assembly_valheim.dll'))
$pluginDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
try {
    # Inspect actual installed vanilla metadata only; never start Unity/Steam.
    $peerInfo = Get-Method $gameDefinition 'ZNet' 'RPC_PeerInfo'
    $seams = @(Get-Calls $peerInfo 'GetNrOfPlayers' | Where-Object {
        (Get-Integer $_.Next) -eq 10 -and $_.Next.Next.OpCode.Name -in @('blt', 'blt.s')
    })
    Assert-True ($seams.Count -eq 1) 'Installed vanilla admission must contain exactly one reviewed player-cap seam.'
    $seam = $seams[0]
    $fullBody = @($peerInfo.Body.Instructions | Where-Object {
        $_.Offset -gt $seam.Next.Next.Offset -and $_.Offset -lt $seam.Next.Next.Operand.Offset
    })
    Assert-True (@($fullBody | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'Error' }).Count -eq 1 -and
        @($fullBody | Where-Object { (Get-Integer $_) -eq 9 }).Count -eq 1) `
        'The vanilla full branch must retain its existing Error(9) response.'
    $count = Get-Method $gameDefinition 'ZNet' 'GetNrOfPlayers'
    Assert-True (@($count.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'm_players'
    }).Count -eq 1 -and @(Get-Calls $count 'get_Count').Count -eq 1) 'Admission must use the vanilla player list count.'
    $players = Get-Method $gameDefinition 'ZNet' 'UpdatePlayerList'
    $graphics = @(Get-Calls $players 'get_graphicsDeviceType') | Select-Object -First 1
    $ready = @(Get-Calls $players 'IsReady') | Select-Object -First 1
    Assert-True ($null -ne $graphics -and (Get-Integer $graphics.Next) -eq 4 -and
        $graphics.Next.Next.OpCode.Name -in @('beq', 'beq.s') -and $null -ne $ready) `
        'Vanilla host accounting must exclude headless graphics and include ready peers.'
    $hostAdds = @(Get-Calls $players 'Add' | Where-Object {
        $_.Offset -gt $graphics.Offset -and $_.Offset -lt $graphics.Next.Next.Operand.Offset
    })
    Assert-True ($hostAdds.Count -eq 1 -and $hostAdds[0].Offset -lt $ready.Offset) `
        'The listen host must consume exactly one slot before remote ready peers are counted.'
    $sendList = Get-Method $gameDefinition 'ZNet' 'SendPlayerList'
    Assert-True (@(Get-Calls $sendList 'UpdatePlayerList').Count -eq 1) 'Sending the vanilla list must refresh the admission count.'
    $uid = @($peerInfo.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'm_uid'
    }) | Select-Object -First 1
    $refreshList = @(Get-Calls $peerInfo 'SendPlayerList') | Select-Object -First 1
    Assert-True ($null -ne $uid -and $null -ne $refreshList -and
        $seam.Offset -lt $uid.Offset -and $uid.Offset -lt $refreshList.Offset) `
        'An accepted peer must become ready and refresh the player list before the next admission.'

    $runtimeName = 'ServerManager.ServerManagerRuntime'
    $capacityName = 'ServerManager.ServerPlayerCapacity'
    $refresh = Get-Method $pluginDefinition $capacityName 'RefreshAdvertisement'
    Assert-GuardProtectsCall $refresh 'IsServer' 'SetMaxPlayerCount'
    Assert-GuardProtectsCall $refresh 'IsDedicated' 'SetMaxPlayerCount'
    Assert-GuardProtectsCall $refresh 'GetHSteamPipe' 'SetMaxPlayerCount'
    Assert-GuardProtectsCall $refresh 'IsServer' 'SetLobbyMemberLimit'
    Assert-GuardProtectsCall $refresh 'IsDedicated' 'SetLobbyMemberLimit'
    Assert-GuardProtectsCall $refresh 'get_Initialized' 'SetLobbyMemberLimit'
    Assert-True (@($refresh.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'm_onlineBackend'
    }).Count -ge 1) 'Advertisement must be restricted to the Steamworks backend.'
    $apply = Get-Method $pluginDefinition $runtimeName 'ApplyServerSettings'
    Assert-True (@(Get-Calls $apply 'RefreshAdvertisement').Count -eq 0 -and
        @(Get-Calls $apply 'Start').Count -eq 0 -and @(Get-Calls $apply 'NotifyReload').Count -eq 1) `
        'YAML live reload must not change the current session advertisement.'
    $limitMethod = Get-Method $pluginDefinition $capacityName 'get_EffectiveLimit'
    $limitFields = @($limitMethod.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'ldsfld' -and $_.Operand.FieldType.FullName -eq 'System.Int32'
    } | ForEach-Object Operand | Sort-Object FullName -Unique)
    Assert-True ($limitFields.Count -eq 1 -and @(Get-Calls $limitMethod 'get_MaxPlayers').Count -eq 0) `
        'Admission must read one frozen integer limit, not the reloadable YAML snapshot.'
    $frozenLimitFieldName = $limitFields[0].Name
    Assert-True (@($apply.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -eq $frozenLimitFieldName
    }).Count -eq 0) 'Live settings application must not overwrite the frozen admission limit.'
    foreach ($method in @($refresh, $apply)) {
        $forbidden = @($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -match 'Kick|Disconnect|RejectSession|SendServerRejection|Shutdown'
        })
        Assert-True ($forbidden.Count -eq 0) 'Changing capacity must never evict or reject already connected players.'
    }
    $steamPrefix = Get-Method $pluginDefinition $capacityName 'SteamAdvertisementPrefix'
    Assert-True (@(Get-Calls $steamPrefix 'GetLimit').Count -eq 1 -and
        @($steamPrefix.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stind.i4' }).Count -eq 1) `
        'Steam advertisement prefix must write the current cap into the original int argument.'
    $lobbyPostfix = Get-Method $pluginDefinition $capacityName 'LobbyCreatedPostfix'
    Assert-True (@(Get-Calls $lobbyPostfix 'RefreshAdvertisement').Count -eq 1) `
        'A newly created listen lobby must advertise the frozen session cap.'
    $capacityType = $pluginDefinition.MainModule.Types | Where-Object FullName -eq $capacityName
    Assert-True (@($capacityType.CustomAttributes | Where-Object {
        $_.AttributeType.FullName -like 'HarmonyLib.Harmony*'
    }).Count -eq 0 -and @($capacityType.Methods | ForEach-Object CustomAttributes | Where-Object {
        $_.AttributeType.FullName -like 'HarmonyLib.Harmony*'
    }).Count -eq 0) 'PatchAll must never discover optional capacity hooks when the override is OFF.'
    $owner = $capacityType.Fields | Where-Object Name -eq 'HarmonyOwner'
    Assert-True ($owner.Constant -ceq 'sighsorry.ServerManager.PlayerCapacity') `
        'Optional capacity patches must use a separate owner from mandatory authentication patches.'
    $resetSettings = Get-Method $pluginDefinition $runtimeName 'ResetServerSettings'
    Assert-True (@(Get-Calls $resetSettings 'Reset' | Where-Object {
        $_.Operand.DeclaringType.FullName -eq $capacityName
    }).Count -eq 1) 'Closing/resetting the server session must remove its optional capacity hooks.'
    $beforeOpen = Get-Method $pluginDefinition $runtimeName 'BeforeServerOpen'
    Assert-GuardProtectsCall $beforeOpen 'EnsureServerSettingsReady' 'Start'
    Assert-GuardProtectsCall $beforeOpen 'EnsureServerCharacterStorageReady' 'Start'
    $capacityStarts = @($pluginDefinition.MainModule.Types | ForEach-Object Methods | Where-Object {
        $_.HasBody -and @(Get-Calls $_ 'Start' | Where-Object {
            $_.Operand.DeclaringType.FullName -eq $capacityName
        }).Count -gt 0
    })
    Assert-True ($capacityStarts.Count -eq 1 -and $capacityStarts[0].Name -eq 'BeforeServerOpen') `
        'Only a real server-open path may choose the session capacity provider; clients and YAML reloads may not.'
    $unpatch = Get-Method $pluginDefinition 'ServerManager.ServerManagerPlugin' 'UnpatchOwnedPatches'
    Assert-True (@(Get-Calls $unpatch 'Reset' | Where-Object {
        $_.Operand.DeclaringType.FullName -eq $capacityName
    }).Count -eq 1) 'Plugin teardown must clean up the separate capacity owner even after runtime shutdown failure.'
    $runtimeType = $pluginDefinition.MainModule.Types | Where-Object FullName -eq $runtimeName
    foreach ($budget in @(
        @('MaximumSteamAuthenticationReservations', 512),
        @('MaximumQueuedSteamAuthenticationCallbacks', 1024),
        @('MaximumPendingAdminManifests', 64),
        @('MaximumQueuedWorldSaveWorkerResults', 64),
        @('MaximumCommandReportsPerWindow', 64),
        @('MaximumBufferedWorldBytes', 33554432)
    )) {
        $field = $runtimeType.Fields | Where-Object Name -eq $budget[0]
        Assert-True ($null -ne $field -and $field.Constant -eq $budget[1]) `
            "Authentication and unrelated protection budgets must remain intentional: $($budget[0])."
    }

    # Execute the real capacity lifecycle below, replacing ONLY native/game
    # Harmony patch installation/removal calls in this in-memory fixture. This
    # leaves OFF/external-provider/freeze/rollback decisions and the transpiler
    # unchanged, without modifying or running the installed game or plugin DLL.
    $capacityType.Fields.Add([Mono.Cecil.FieldDefinition]::new('TestThrowAtPatch',
        [Mono.Cecil.FieldAttributes]'Public,Static', $pluginDefinition.MainModule.TypeSystem.Int32))
    Add-HarmonyCallFixture $pluginDefinition $capacityType 'Patch' 'TestPatchCount' $true
    Add-HarmonyCallFixture $pluginDefinition $capacityType 'UnpatchSelf' 'TestUnpatchCount' $false

    # Disable only the production runtime's eager initializer in this in-memory
    # fixture. The actual transpiler, immutable settings parser and frozen getter
    # remain unchanged. No Harmony patch is installed and no game method runs.
    $initializer = Get-Method $pluginDefinition $runtimeName '.cctor'
    $initializer.Body.Instructions.Clear()
    $initializer.Body.ExceptionHandlers.Clear()
    $initializer.Body.Variables.Clear()
    $initializer.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
    $stream = [IO.MemoryStream]::new()
    try { $pluginDefinition.Write($stream); $plugin = [Reflection.Assembly]::Load($stream.ToArray()) }
    finally { $stream.Dispose() }
}
finally { $gameDefinition.Dispose(); $pluginDefinition.Dispose() }

$runtime = $plugin.GetType('ServerManager.ServerManagerRuntime', $true)
$capacity = $plugin.GetType('ServerManager.ServerPlayerCapacity', $true)
$transpile = $capacity.GetMethod('AdmissionTranspiler', $static)
Assert-True ($null -ne $transpile) 'The optional capacity class must contain its separate admission transpiler.'
$limitGetter = $capacity.GetMethod('GetLimit', $static)
$frozenLimit = $capacity.GetField($frozenLimitFieldName, $static)
$settings = $plugin.GetType('ServerManager.ServerSettings', $true)
$parse = $settings.GetMethod('Parse', $static)
$snapshot = $runtime.GetField('_currentServerSettings', $static)
$plugin.GetType('ServerManager.ServerManagerPlugin', $true).GetProperty('Log', $static).
    GetSetMethod($true).Invoke($null, [object[]]@([BepInEx.Logging.ManualLogSource]::new('Capacity test'))) | Out-Null
$start = $capacity.GetMethod('Start', $static)
$reset = $capacity.GetMethod('Reset', $static)
$notify = $capacity.GetMethod('NotifyReload', $static)
$externalOwner = $capacity.GetMethod('FindExternalOwner', $static)
$requested = $capacity.GetProperty('RequestedAtStart', $static)
$active = $capacity.GetProperty('IsOverrideActive', $static)
$patchCount = $capacity.GetField('TestPatchCount', $static)
$unpatchCount = $capacity.GetField('TestUnpatchCount', $static)
$throwAtPatch = $capacity.GetField('TestThrowAtPatch', $static)
function Reset-CapacityFixture {
    $reset.Invoke($null, @()) | Out-Null
    $patchCount.SetValue($null, 0)
    $unpatchCount.SetValue($null, 0)
    $throwAtPatch.SetValue($null, 0)
    Assert-True ($null -eq $requested.GetValue($null) -and -not $active.GetValue($null) -and
        $limitGetter.Invoke($null, @()) -eq 10) 'Reset must return to undecided, hook-free vanilla fallback.'
}
function Start-CapacityFixture([int]$Limit, [string[]]$Guids = @()) {
    $start.Invoke($null, [object[]]@($Limit, $Guids)) | Out-Null
}
Reset-CapacityFixture
Start-CapacityFixture 10
Assert-True ($requested.GetValue($null) -eq 10 -and -not $active.GetValue($null) -and
    $patchCount.GetValue($null) -eq 0) 'Default ten must install no capacity hooks at all.'
$notify.Invoke($null, [object[]]@(250)) | Out-Null
Start-CapacityFixture 250
Assert-True ($requested.GetValue($null) -eq 10 -and $limitGetter.Invoke($null, @()) -eq 10 -and
    $patchCount.GetValue($null) -eq 0) 'An OFF session must stay OFF after reload/repeated server-open calls.'
foreach ($guid in @('Azumatt.MaxPlayerCount', 'MidnightsFX.NetworkPerformanceSystem', 'org.bepinex.plugins.valheim_plus')) {
    Reset-CapacityFixture
    Assert-True ($externalOwner.Invoke($null, [object[]]@(,[string[]]@('unrelated.Mod', $guid))) -ceq $guid) `
        "Recognize the exact installed provider GUID: $guid."
    Start-CapacityFixture 250 @($guid)
    Assert-True ($requested.GetValue($null) -eq 250 -and $limitGetter.Invoke($null, @()) -eq 10 -and
        -not $active.GetValue($null) -and $patchCount.GetValue($null) -eq 0) `
        "An installed $guid must own capacity without ServerManager installing competing hooks."
    Start-CapacityFixture 100
    Assert-True ($patchCount.GetValue($null) -eq 0) 'The capacity provider must remain frozen for the server session.'
}
Assert-True ($null -eq $externalOwner.Invoke($null, [object[]]@(,[string[]]@('unrelated.Mod')))) `
    'Unrelated plugins must not disable the optional override.'
foreach ($cap in @(11, 24, 64, 100, 250)) {
    Reset-CapacityFixture
    Start-CapacityFixture $cap
    $installed = $patchCount.GetValue($null)
    Assert-True ($requested.GetValue($null) -eq $cap -and $limitGetter.Invoke($null, @()) -eq $cap -and
        $active.GetValue($null) -and $installed -ge 2 -and $installed -le 3) `
        "Starting capacity $cap must install only its admission and advertisement hooks."
    $notify.Invoke($null, [object[]]@(10)) | Out-Null
    Start-CapacityFixture 10
    Assert-True ($limitGetter.Invoke($null, @()) -eq $cap -and $patchCount.GetValue($null) -eq $installed) `
        'Reload or a repeated open must neither repatch nor replace the effective limit.'
    $reset.Invoke($null, @()) | Out-Null
    Assert-True ($unpatchCount.GetValue($null) -eq 1 -and -not $active.GetValue($null) -and
        $null -eq $requested.GetValue($null)) 'Closing an active session must remove its separate patch owner exactly once.'
}
Reset-CapacityFixture
$throwAtPatch.SetValue($null, 2)
Start-CapacityFixture 250
Assert-True ($patchCount.GetValue($null) -eq 2 -and $unpatchCount.GetValue($null) -eq 1 -and
    $limitGetter.Invoke($null, @()) -eq 10 -and -not $active.GetValue($null) -and
    $requested.GetValue($null) -eq 250) 'A partial capacity installation failure must roll back its hooks and freeze fallback.'
Start-CapacityFixture 250
Assert-True ($patchCount.GetValue($null) -eq 2) 'Failed capacity installation must not retry on every connection or frame.'
foreach ($invalid in @(9, 251)) {
    Reset-CapacityFixture
    $rejected = $false
    try { Start-CapacityFixture $invalid } catch { $rejected = $true }
    Assert-True ($rejected -and $patchCount.GetValue($null) -eq 0 -and
        $null -eq $requested.GetValue($null)) 'Out-of-range startup must reject before installing any hook.'
}
Reset-CapacityFixture
$vanillaCount = [ZNet].GetMethod('GetNrOfPlayers', $instance)
$labelMethod = [Reflection.Emit.DynamicMethod]::new('CapacityLabels', [void], [Type[]]@())
$labelGenerator = $labelMethod.GetILGenerator()
$markerLabel = $labelGenerator.DefineLabel()
$markerBlock = [HarmonyLib.ExceptionBlock]::new([HarmonyLib.ExceptionBlockType]::BeginExceptionBlock, $null)
function New-Seam([Reflection.Emit.OpCode]$Branch = [Reflection.Emit.OpCodes]::Blt_S) {
    $list = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
    $list.Add([HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Call, $vanillaCount))
    $limit = [HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Ldc_I4_S, [sbyte]10)
    $limit.labels.Add($markerLabel)
    $limit.blocks.Add($markerBlock)
    $list.Add($limit)
    $list.Add([HarmonyLib.CodeInstruction]::new($Branch, $markerLabel))
    $list.Add([HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Ldstr, 'Error'))
    $list.Add([HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Ldc_I4_S, [sbyte]9))
    $list.Add([HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Ldc_I4_S, [sbyte]10))
    return ,$list
}
function Invoke-Transpiler($Instructions) {
    return @($transpile.Invoke($null, [object[]]@(,$Instructions)))
}
function Assert-SeamRejected($Instructions, [string]$Message) {
    $rejected = $false
    try { $null = Invoke-Transpiler $Instructions }
    catch { $rejected = $true }
    Assert-True $rejected $Message
}

# Translate the installed Cecil instruction stream to a Harmony fixture, then
# execute the production transpiler, not the game method. GetOriginalInstructions
# cannot copy this game's default-interface metadata on Desktop CLR; bundled
# Harmony's own initializer is incompatible with newer pwsh/Core CLR. Resolve
# only the reviewed GetNrOfPlayers seam to MethodInfo; other operands retain
# their exact metadata objects. Snapshot values before the in-place replacement.
$originalPeerInfo = [ZNet].GetMethod('RPC_PeerInfo', $instance)
$metadata = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managedRoot 'assembly_valheim.dll'))
try {
    $originalInstructions = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
    $opcodeMap = @{}
    foreach ($field in [Reflection.Emit.OpCodes].GetFields($static)) {
        if ($field.FieldType -eq [Reflection.Emit.OpCode]) {
            $opcode = $field.GetValue($null)
            $opcodeMap[$opcode.Name] = $opcode
        }
    }
    $body = Get-Method $metadata 'ZNet' 'RPC_PeerInfo'
    foreach ($instruction in $body.Body.Instructions) {
        $operand = $instruction.Operand
        if ($operand -is [Mono.Cecil.MethodReference] -and
            $operand.DeclaringType.FullName -eq 'ZNet' -and $operand.Name -eq 'GetNrOfPlayers') {
            $operand = $vanillaCount
        }
        $originalInstructions.Add([HarmonyLib.CodeInstruction]::new($opcodeMap[$instruction.OpCode.Name], $operand))
    }
}
finally { $metadata.Dispose() }
$beforeOpcodes = @($originalInstructions | ForEach-Object opcode)
$beforeOperands = @($originalInstructions | ForEach-Object { ,$_.operand })
$realOutput = @(Invoke-Transpiler $originalInstructions)
Assert-True ($realOutput.Count -eq $beforeOpcodes.Count) 'The real vanilla method must retain its instruction count.'
$realChanges = @()
for ($index = 0; $index -lt $realOutput.Count; ++$index) {
    if ($realOutput[$index].opcode -ne $beforeOpcodes[$index] -or
        -not [object]::Equals($realOutput[$index].operand, $beforeOperands[$index])) { $realChanges += $index }
}
Assert-True ($realChanges.Count -eq 1 -and
    $realOutput[$realChanges[0]].opcode -eq [Reflection.Emit.OpCodes]::Call -and
    $realOutput[$realChanges[0]].operand -eq $limitGetter) `
    'The production transpiler must change exactly one instruction in actual vanilla PeerInfo, preserving every error/auth/password instruction.'

foreach ($branch in @([Reflection.Emit.OpCodes]::Blt_S, [Reflection.Emit.OpCodes]::Blt)) {
    $original = New-Seam $branch
    $limitInstruction = $original[1]
    $output = @(Invoke-Transpiler $original)
    Assert-True ($output.Count -eq $original.Count) 'Capacity patch must not add or remove vanilla instructions.'
    Assert-True ($output[1].opcode -eq [Reflection.Emit.OpCodes]::Call -and $output[1].operand -eq $limitGetter) `
        'Only the admission limit must become a call to the current runtime value.'
    Assert-True ([object]::ReferenceEquals($limitInstruction, $output[1]) -and
        $output[1].labels.Count -eq 1 -and $output[1].labels[0].Equals($markerLabel) -and
        $output[1].blocks.Count -eq 1 -and [object]::ReferenceEquals($output[1].blocks[0], $markerBlock)) `
        'The in-place replacement must preserve branch labels and exception blocks.'
    foreach ($index in @(0, 2, 3, 4, 5)) {
        Assert-True ([object]::ReferenceEquals($original[$index], $output[$index])) 'Unrelated vanilla instructions must remain intact.'
    }
    Assert-True ($output[2].opcode -eq $branch -and $output[3].operand -eq 'Error' -and
        [int]$output[4].operand -eq 9 -and [int]$output[5].operand -eq 10) `
        'The full-error branch and unrelated ten constants must stay untouched.'
}
Assert-SeamRejected ([Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()) 'A missing capacity seam must fail visibly.'
$duplicate = New-Seam
$duplicate.AddRange((New-Seam))
Assert-SeamRejected $duplicate 'Ambiguous duplicate capacity seams must fail visibly.'
$changed = New-Seam
$changed[1].operand = [sbyte]20
Assert-SeamRejected $changed 'An already changed capacity limit must not silently stack with another cap mod.'
$wrongBranch = New-Seam ([Reflection.Emit.OpCodes]::Bge_S)
Assert-SeamRejected $wrongBranch 'A changed admission comparison must fail visibly for review.'

# Emit only the tiny post-transpilation comparison, substituting an integer
# argument for GetNrOfPlayers. Execute the production getter at each admission.
# Frozen limit values stand in for starting each new server session below; no
# Steam server, network listener, or game peer is instantiated by this fixture.
$admitMethod = [Reflection.Emit.DynamicMethod]::new('CapacityAdmission', [int], [Type[]]@([int]), $capacity.Module, $true)
$il = $admitMethod.GetILGenerator()
$accepted = $il.DefineLabel()
$il.Emit([Reflection.Emit.OpCodes]::Ldarg_0)
$il.Emit([Reflection.Emit.OpCodes]::Call, $output[1].operand)
$il.Emit([Reflection.Emit.OpCodes]::Blt_S, $accepted)
$il.Emit([Reflection.Emit.OpCodes]::Ldc_I4_S, [sbyte]9)
$il.Emit([Reflection.Emit.OpCodes]::Ret)
$il.MarkLabel($accepted)
$il.Emit([Reflection.Emit.OpCodes]::Ldc_I4_0)
$il.Emit([Reflection.Emit.OpCodes]::Ret)
$admit = $admitMethod.CreateDelegate([Func[int,int]])
$snapshot.SetValue($null, $null)
Assert-True ($limitGetter.Invoke($null, @()) -eq 10 -and $admit.Invoke(9) -eq 0 -and $admit.Invoke(10) -eq 9) `
    'Before server start the getter must retain the vanilla ten-player fallback.'
foreach ($cap in @(10, 11, 24, 64, 100, 250)) {
    $value = $parse.Invoke($null, [object[]]@("serverSettings:`n  maxPlayers: $cap`n"))
    $snapshot.SetValue($null, $value)
    $frozenLimit.SetValue($null, $cap)
    Assert-True ($limitGetter.Invoke($null, @()) -eq $cap) 'Admission must read the frozen server-session limit.'
    Assert-True ($admit.Invoke($cap - 1) -eq 0 -and $admit.Invoke($cap) -eq 9 -and $admit.Invoke($cap + 1) -eq 9) `
        "Capacity $cap must accept the last available slot and use vanilla Error(9) at or over the cap."
    $snapshot.SetValue($null, $parse.Invoke($null, [object[]]@("serverSettings:`n  maxPlayers: 10`n")))
    Assert-True ($limitGetter.Invoke($null, @()) -eq $cap) 'Reloading YAML to OFF must not change an already running session.'
    $snapshot.SetValue($null, $parse.Invoke($null, [object[]]@("serverSettings:`n  maxPlayers: 250`n")))
    Assert-True ($limitGetter.Invoke($null, @()) -eq $cap) 'Reloading YAML to 250 must wait until a new server session.'
}
Write-Output "Server player capacity transpiler, frozen admission, vanilla accounting and advertisement guard smoke passed ($script:assertions assertions)."
