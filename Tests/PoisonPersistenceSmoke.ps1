param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $poisonPowerShell = (Get-Command pwsh -ErrorAction Stop).Source
    & $poisonPowerShell -NoProfile -File $PSCommandPath -Configuration $Configuration -GamePath $GamePath
    if ($LASTEXITCODE -ne 0) { throw 'The source-linked poison persistence fixture failed.' }
    exit 0
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$poisonSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Character/CharacterPoisonPersistence.cs') -Raw
# Source-link the complete implementation with inert ownership/logging and
# engine boundaries. Neither a local save nor an RPC can execute in the fixture.
$poisonNamespace = 'namespace ServerManager;'
$poisonStart = $poisonSource.IndexOf($poisonNamespace, [StringComparison]::Ordinal)
if ($poisonStart -lt 0) { throw 'Cannot locate the production poison namespace boundary.' }
$poisonImplementation = $poisonSource.Substring($poisonStart + $poisonNamespace.Length)
$poisonHarness = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PoisonPersistenceSmoke.cs') -Raw
$poisonMarker = '// SOURCE_LINKED_POISON_IMPLEMENTATION'
if (($poisonHarness.Split(@($poisonMarker), [StringSplitOptions]::None)).Length -ne 2) {
    throw 'The poison fixture must have exactly one production implementation insertion point.'
}
Add-Type -TypeDefinition $poisonHarness.Replace($poisonMarker, $poisonImplementation)
[PoisonPersistenceSmoke]::Run()

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Find-Method($type, [string]$name) {
    $method = @($type.Methods | Where-Object Name -eq $name | Select-Object -First 1)
    Assert-True ($method.Count -eq 1) "Missing poison integration method: $name"
    return $method[0]
}
function Find-Call($method, [string]$name, [string]$owner = '') {
    return $method.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $name -and
        ($owner -eq '' -or $_.Operand.DeclaringType.FullName -eq $owner)
    } | Select-Object -First 1
}
function Assert-Calls($method, [string]$name, [string]$owner = '') {
    Assert-True ($null -ne (Find-Call $method $name $owner)) "$($method.Name) no longer calls $owner.$name."
}

# Read the actual build's IL; no Player/Game/Harmony methods execute here.
$pluginPath = Join-Path $projectRoot "bin/$Configuration/ServerManager.dll"
Assert-True (Test-Path -LiteralPath $pluginPath) 'Build ServerManager before running the poison integration guards.'
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx/core/Mono.Cecil.dll')) | Out-Null
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
try {
    $runtime = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerManagerRuntime'
    $hostRuntime = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.LocalHostCharacterRuntime'
    $poison = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.CharacterPoisonPersistence'
    $state = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.CharacterPoisonState'
    Assert-True ($null -ne $runtime -and $null -ne $hostRuntime -and $null -ne $poison -and $null -ne $state) 'Missing poison implementation or character adapters.'
    $key = @($poison.Fields | Where-Object Name -eq 'CustomDataKey')
    Assert-True ($key.Count -eq 1 -and $key[0].HasConstant -and $key[0].Constant -ceq 'ServerManager.Poison') 'Poison persistence must use one independently implemented own customData key.'
    foreach ($name in @('Capture', 'BeforeLoad', 'AfterLoad')) {
        Assert-Calls (Find-Method $poison $name) 'CanPersistCharacterPoison' 'ServerManager.ServerManagerRuntime'
    }
    $savePatch = $definition.MainModule.Types | Where-Object Name -eq 'ManagedCharacterPlayerSavePatch'
    $loadPatch = $definition.MainModule.Types | Where-Object Name -eq 'ManagedCharacterPlayerLoadPatch'
    Assert-True ($null -ne $savePatch -and $null -ne $loadPatch) 'Missing scoped vanilla Save/Load hooks.'
    foreach ($patch in @($savePatch, $loadPatch)) {
        $patchAttribute = @($patch.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' })
        Assert-True ($patchAttribute.Count -eq 1 -and $patchAttribute[0].ConstructorArguments[0].Value.FullName -eq 'Player' -and
            $patchAttribute[0].ConstructorArguments[1].Value -in @('Save', 'Load')) 'Poison hook no longer targets vanilla Player.Save/Load.'
    }
    $savePrefix = Find-Method $savePatch 'Prefix'
    Assert-Calls $savePrefix 'Capture' 'ServerManager.CharacterPoisonPersistence'
    $savePriority = @($savePrefix.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPriority' })
    Assert-True ($savePriority.Count -eq 1 -and $savePriority[0].ConstructorArguments[0].Value -eq 0) 'Poison capture should run late before Player.Save writes customData.'
    $loadPrefix = Find-Method $loadPatch 'Prefix'
    Assert-Calls $loadPrefix 'BeforePlayerLoad' 'ServerManager.ServerManagerRuntime'
    Assert-Calls $loadPrefix 'BeforeLoad' 'ServerManager.CharacterPoisonPersistence'
    Assert-True ((Find-Call $loadPrefix 'BeforePlayerLoad').Offset -lt (Find-Call $loadPrefix 'BeforeLoad').Offset) 'Load suppression must be entered before removing a stale record.'
    $loadFinalizer = Find-Method $loadPatch 'Finalizer'
    Assert-True (@($loadFinalizer.Parameters | Where-Object { $_.Name -eq '__runOriginal' -and $_.ParameterType.FullName -eq 'System.Boolean' }).Count -eq 1 -and
        @($loadFinalizer.Parameters | Where-Object { $_.Name -eq '__exception' -and $_.ParameterType.FullName -eq 'System.Exception' }).Count -eq 1) 'Load completion lost original-run or original-exception visibility.'
    Assert-Calls $loadFinalizer 'AfterLoad' 'ServerManager.CharacterPoisonPersistence'
    $loadCleanup = Find-Call $loadFinalizer 'AfterPlayerLoad' 'ServerManager.ServerManagerRuntime'
    $cleanupFinally = @($loadFinalizer.Body.ExceptionHandlers | Where-Object {
        $_.HandlerType.ToString() -eq 'Finally' -and $loadCleanup.Offset -ge $_.HandlerStart.Offset -and
        ($null -eq $_.HandlerEnd -or $loadCleanup.Offset -lt $_.HandlerEnd.Offset)
    })
    Assert-True ($cleanupFinally.Count -eq 1) 'Poison restoration failure can leave the existing player-load suppression stuck.'

    $remoteScope = Find-Method $runtime 'CanPersistCharacterPoison'
    Assert-Calls $remoteScope 'CanPersistCharacterPoison' 'ServerManager.LocalHostCharacterRuntime'
    foreach ($name in @('IsServer', 'get_Network', 'get_Failed', 'get_ServerCharacterActive', 'get_ReadyAcknowledgementSent',
        'get_CharacterState', 'get_ManagedProfile', 'get_BackupOnly', 'GetGamePlayerProfile', 'GetPlayerID', 'GetPlayerName')) {
        Assert-Calls $remoteScope $name
    }
    $hostScope = Find-Method $hostRuntime 'CanPersistCharacterPoison'
    foreach ($name in @('IsCurrentWorld', 'get_BackupOnly', 'GetGamePlayerProfile', 'GetPlayerID', 'get_PlayerId', 'GetPlayerName', 'get_CharacterName')) {
        Assert-Calls $hostScope $name
    }
    $capture = Find-Method $poison 'Capture'
    Assert-Calls $capture 'get_IsPlayerLoadInProgress' 'ServerManager.ServerManagerRuntime'
    Assert-Calls $capture 'GetRemaningTime' 'StatusEffect'
    Assert-Calls $capture 'IsDead'
    Assert-Calls $capture 'Encode' 'ServerManager.CharacterPoisonState'
    $afterLoad = Find-Method $poison 'AfterLoad'
    Assert-Calls $afterLoad 'TryDecode' 'ServerManager.CharacterPoisonState'
    Assert-Calls $afterLoad 'GetStatusEffect' 'SEMan'
    Assert-Calls $afterLoad 'AddStatusEffect' 'SEMan'
    Assert-Calls $afterLoad 'RemoveStatusEffect' 'SEMan'
    Assert-True (@($capture.Body.ExceptionHandlers | Where-Object { $_.HandlerType.ToString() -eq 'Filter' }).Count -ge 1 -and
        @($afterLoad.Body.ExceptionHandlers | Where-Object { $_.HandlerType.ToString() -eq 'Filter' }).Count -ge 1) 'Optional poison adapter lost its nonfatal exception boundary.'
    $profileCodec = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ValheimPlayerProfileCodec'
    Assert-Calls (Find-Method $profileCodec 'CaptureProfileToBytes') 'SavePlayerData' 'PlayerProfile'
    Assert-Calls (Find-Method $runtime 'CaptureDeferredClientExitSnapshot') 'SavePlayerData' 'PlayerProfile'
    Assert-Calls (Find-Method $hostRuntime 'CaptureFull') 'SavePlayerData' 'PlayerProfile'
    Assert-True ($null -eq (Find-Call (Find-Method $profileCodec 'ReplaceInventorySnapshot') 'Capture' 'ServerManager.CharacterPoisonPersistence')) 'Inventory-only hot path must not pretend to capture live poison.'
    foreach ($method in $state.Methods | Where-Object HasBody) {
        foreach ($instruction in $method.Body.Instructions) {
            if ($instruction.Operand -isnot [Mono.Cecil.MethodReference]) { continue }
            $owner = $instruction.Operand.DeclaringType.FullName
            Assert-True ($owner -notmatch '^(UnityEngine\.|Player$|Game$|ZNet|ZRpc|System\.(IO\.|Net\.|DateTime|Diagnostics\.Stopwatch))') 'Pure poison state now depends on Unity, network, files, or offline elapsed time.'
        }
    }
    foreach ($method in $poison.Methods | Where-Object HasBody) {
        foreach ($instruction in $method.Body.Instructions) {
            if ($instruction.Operand -isnot [Mono.Cecil.FieldReference]) { continue }
            Assert-True (-not (($instruction.Operand.DeclaringType.FullName -eq 'SE_Poison' -and
                $instruction.Operand.Name -in @('m_timer', 'm_damageLeft', 'm_damagePerHit')) -or
                ($instruction.Operand.DeclaringType.FullName -eq 'StatusEffect' -and $instruction.Operand.Name -eq 'm_time'))) 'Poison adapter emits direct references to inaccessible vanilla fields.'
        }
    }
    $runtimeSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Networking/ServerManagerRuntime.cs') -Raw
    $hostSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Networking/LocalHostCharacterRuntime.cs') -Raw
    $patchSource = Get-Content -LiteralPath (Join-Path $projectRoot 'RuntimePatches.cs') -Raw
    Assert-True ($runtimeSource.Contains('(!restoring || !session.BackupOnly)') -and
        $hostSource.Contains('(!restoring || !state.Session.BackupOnly)')) 'Backup-only must allow capture but disallow forced poison restoration for remote and local-host sessions.'
    Assert-True ($runtimeSource.Contains('ReferenceEquals(session.Network, network)') -and
        $runtimeSource.Contains('ReferenceEquals(ValheimPrivateAccess.GetGamePlayerProfile(Game.instance), session.ManagedProfile)') -and
        $hostSource.Contains('ReferenceEquals(ValheimPrivateAccess.GetGamePlayerProfile(state.Game), state.ManagedProfile)')) 'Poison ownership can cross a replaced network or profile instance.'
    Assert-True ($patchSource.Contains('CharacterPoisonPersistence.AfterLoad(__instance, __runOriginal && __exception == null);')) 'Skipped or failed original Player.Load can be treated as a successful poison restore.'
    Assert-True (-not $poisonSource.Contains('ServerCharacters Poison')) 'Poison persistence introduced unrequested legacy ServerCharacters data migration.'
    Write-Host 'Poison persistence runtime integration guards passed.'
}
finally { $definition.Dispose() }

# Verify assumptions against the installed game's metadata rather than a
# publicized build reference. This is read-only; no game assembly is executed.
$vanilla = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GamePath 'valheim_Data/Managed/assembly_valheim.dll'))
try {
    $playerType = $vanilla.MainModule.Types | Where-Object FullName -eq 'Player'
    foreach ($name in @('Save', 'Load')) {
        $method = Find-Method $playerType $name
        Assert-True (@($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.DeclaringType.FullName -eq 'Player' -and $_.Operand.Name -eq 'm_customData'
        }).Count -gt 0) "Installed Player.$name no longer serializes customData."
    }
    $poisonType = $vanilla.MainModule.Types | Where-Object FullName -eq 'SE_Poison'
    foreach ($name in @('m_timer', 'm_damageLeft', 'm_damagePerHit')) {
        $field = @($poisonType.Fields | Where-Object Name -eq $name)
        Assert-True ($field.Count -eq 1 -and $field[0].FieldType.FullName -eq 'System.Single') "Installed poison field changed: $name."
    }
    $effectType = $vanilla.MainModule.Types | Where-Object FullName -eq 'StatusEffect'
    Assert-True (@($effectType.Fields | Where-Object { $_.Name -eq 'm_time' -and $_.FieldType.FullName -eq 'System.Single' }).Count -eq 1) 'Installed elapsed status timer changed.'
    Assert-True ((Find-Method $effectType 'GetRemaningTime').ReturnType.FullName -eq 'System.Single') 'Installed remaining status duration API changed.'
    $effectsType = $vanilla.MainModule.Types | Where-Object FullName -eq 'SEMan'
    Assert-True (@($effectsType.Methods | Where-Object {
        $_.Name -eq 'AddStatusEffect' -and $_.ReturnType.FullName -eq 'StatusEffect' -and $_.Parameters.Count -eq 5 -and
        $_.Parameters[0].ParameterType.FullName -eq 'System.Int32' -and $_.Parameters[1].ParameterType.FullName -eq 'System.Boolean'
    }).Count -eq 1) 'Installed poison creation overload changed.'
    Write-Host 'Installed vanilla poison/customData API assumptions passed (metadata only).'
}
finally { $vanilla.Dispose() }
