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
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
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
    $refresh = Get-Method $pluginDefinition $runtimeName 'RefreshServerPlayerLimitAdvertisement'
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
    $applyRefresh = @(Get-Calls $apply 'RefreshServerPlayerLimitAdvertisement')
    $maxReads = @(Get-Calls $apply 'get_MaxPlayers')
    Assert-True ($applyRefresh.Count -eq 1 -and $maxReads.Count -ge 2) 'Apply must compare the old and new caps and refresh once.'
    $changeBranches = @($apply.Body.Instructions | Where-Object {
        $_.Offset -gt $maxReads[1].Offset -and $_.Offset -lt $applyRefresh[0].Offset -and
        $_.OpCode.FlowControl.ToString() -eq 'Cond_Branch' -and
        ((Test-CecilReachable $_.Operand $applyRefresh[0]) -xor (Test-CecilReachable $_.Next $applyRefresh[0]))
    })
    Assert-True ($changeBranches.Count -ge 1) 'An unchanged cap must skip the live advertisement refresh.'
    foreach ($method in @($refresh, $apply)) {
        $forbidden = @($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -match 'Kick|Disconnect|RejectSession|SendServerRejection|Shutdown'
        })
        Assert-True ($forbidden.Count -eq 0) 'Changing capacity must never evict or reject already connected players.'
    }
    $steamPrefix = Get-Method $pluginDefinition 'ServerManager.SteamServerPlayerLimitPatch' 'Prefix'
    Assert-True (@(Get-Calls $steamPrefix 'GetServerPlayerLimit').Count -eq 1 -and
        @($steamPrefix.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stind.i4' }).Count -eq 1) `
        'Steam advertisement prefix must write the current cap into the original int argument.'
    $lobbyPostfix = Get-Method $pluginDefinition 'ServerManager.SteamLobbyPlayerLimitPatch' 'Postfix'
    Assert-True (@(Get-Calls $lobbyPostfix 'RefreshServerPlayerLimitAdvertisement').Count -eq 1) `
        'A newly created listen lobby must advertise the latest cap, including reloads while creation was pending.'

    # Disable only the production runtime's eager initializer in this in-memory
    # fixture. The actual transpiler, immutable settings parser and live getter
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
$patch = $plugin.GetType('ServerManager.ServerPeerInfoPatch', $true)
$transpile = $patch.GetMethod('Transpiler', $static)
Assert-True ($null -ne $transpile) 'The existing PeerInfo patch must contain the capacity transpiler.'
$limitGetter = $runtime.GetMethod('GetServerPlayerLimit', $static)
$settings = $plugin.GetType('ServerManager.ServerSettings', $true)
$parse = $settings.GetMethod('Parse', $static)
$snapshot = $runtime.GetField('_currentServerSettings', $static)
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
# argument for GetNrOfPlayers. Execute the production getter at each admission,
# so this catches accidentally baking the initial YAML value into patched IL.
$admitMethod = [Reflection.Emit.DynamicMethod]::new('CapacityAdmission', [int], [Type[]]@([int]), $runtime.Module, $true)
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
Assert-True ($limitGetter.Invoke($null, @()) -eq 24 -and $admit.Invoke(23) -eq 0 -and $admit.Invoke(24) -eq 9) `
    'Before YAML load the safe default must be 24, including the exact full boundary.'
foreach ($cap in @(1, 24, 64, 8, 32)) {
    $value = $parse.Invoke($null, [object[]]@("serverSettings:`n  maxPlayers: $cap`n"))
    $snapshot.SetValue($null, $value)
    Assert-True ($limitGetter.Invoke($null, @()) -eq $cap) 'Every new admission must read the latest immutable settings snapshot.'
    Assert-True ($admit.Invoke($cap - 1) -eq 0 -and $admit.Invoke($cap) -eq 9 -and $admit.Invoke($cap + 1) -eq 9) `
        "Capacity $cap must accept the last available slot and use vanilla Error(9) at or over the cap."
}
$snapshot.SetValue($null, $parse.Invoke($null, [object[]]@("serverSettings:`n  maxPlayers: 1`n")))
Assert-True ($admit.Invoke(0) -eq 0 -and $admit.Invoke(1) -eq 9) `
    'At cap one an empty dedicated server admits one player, while a counted listen host already fills the server.'
$snapshot.SetValue($null, $parse.Invoke($null, [object[]]@("serverSettings:`n  maxPlayers: 8`n")))
Assert-True ($admit.Invoke(24) -eq 9) 'Lowering below occupancy must block only subsequent admissions.'
$snapshot.SetValue($null, $parse.Invoke($null, [object[]]@("serverSettings:`n  maxPlayers: 32`n")))
Assert-True ($admit.Invoke(24) -eq 0) 'Raising the cap must reopen slots without retranspiling.'
Write-Output "Server player capacity transpiler, live admission, vanilla accounting and advertisement guard smoke passed ($script:assertions assertions)."
