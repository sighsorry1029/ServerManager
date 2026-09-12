param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"
$script:assertions = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    ++$script:assertions
}

function Get-Method {
    param($Assembly, [string]$TypeName, [string]$MethodName,
        [int]$ParameterCount = -1)
    $type = $Assembly.MainModule.GetType($TypeName)
    Assert-True ($null -ne $type) "Missing type $TypeName."
    $methods = @($type.Methods | Where-Object {
        $_.Name -eq $MethodName -and $_.HasBody -and
        ($ParameterCount -lt 0 -or $_.Parameters.Count -eq $ParameterCount)
    })
    Assert-True ($methods.Count -eq 1) `
        "Expected one method $TypeName.$MethodName/$ParameterCount."
    return $methods[0]
}

function Get-Calls {
    param($Method, [string]$TypeName, [string]$MethodName)
    return @($Method.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq $TypeName -and
        $_.Operand.Name -eq $MethodName
    })
}

function Get-Call {
    param($Method, [string]$TypeName, [string]$MethodName)
    $calls = @(Get-Calls $Method $TypeName $MethodName)
    Assert-True ($calls.Count -eq 1) `
        "Expected one $TypeName.$MethodName call in $($Method.FullName)."
    return $calls[0]
}

function Get-FieldInstructions {
    param($Method, [string]$TypeName, [string]$FieldName,
        [string]$OpCode = "")
    return @($Method.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.DeclaringType.FullName -eq $TypeName -and
        $_.Operand.Name -eq $FieldName -and
        ($OpCode.Length -eq 0 -or $_.OpCode.Name -eq $OpCode)
    })
}

function Get-Successors {
    param($Instruction)
    $flow = $Instruction.OpCode.FlowControl.ToString()
    if ($flow -eq "Branch") { return @($Instruction.Operand) }
    if ($flow -eq "Cond_Branch") {
        $next = @($Instruction.Operand)
        if ($null -ne $Instruction.Next) { $next += $Instruction.Next }
        return $next
    }
    if ($flow -in @("Return", "Throw")) { return @() }
    if ($null -ne $Instruction.Next) { return @($Instruction.Next) }
    return @()
}

function Test-Reachable {
    param($Start, $Target, $Excluded = $null)
    $pending = [Collections.Generic.Queue[object]]::new()
    $seen = @{}
    if ($null -eq $Start -or $null -eq $Target) { return $false }
    $pending.Enqueue($Start)
    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        if ($seen.ContainsKey($current.Offset) -or
            ($null -ne $Excluded -and $current.Offset -eq $Excluded.Offset)) {
            continue
        }
        if ($current.Offset -eq $Target.Offset) { return $true }
        $seen[$current.Offset] = $true
        foreach ($successor in @(Get-Successors $current)) {
            if ($null -ne $successor) { $pending.Enqueue($successor) }
        }
    }
    return $false
}

function Assert-FalseArgumentSkipsCall {
    param($Method, [int]$Argument, $ProtectedCall, $NextCall)
    $branches = @($Method.Body.Instructions | Where-Object {
        $_.OpCode.Name -in @("brfalse", "brfalse.s") -and
        $null -ne $_.Previous -and
        $_.Previous.OpCode.Name -eq "ldarg.$Argument" -and
        $_.Offset -lt $ProtectedCall.Offset -and
        $_.Operand.Offset -gt $ProtectedCall.Offset
    })
    Assert-True ($branches.Count -eq 1) `
        "$($Method.FullName) no longer explicitly skips saving for a false argument."
    Assert-True (-not (Test-Reachable $branches[0].Operand $ProtectedCall)) `
        "$($Method.FullName) can save through its false-argument branch."
    Assert-True (Test-Reachable $branches[0].Operand $NextCall) `
        "$($Method.FullName) no-save path no longer completes ordinary teardown."
}

function Invoke-SaveGuardIl {
    param($Method, [bool]$Failed, [bool]$Requested, [bool]$Active, [bool]$LoadSuppressed = $false)
    # Execute only this tiny boolean guard's IL with simulated state. Unknown
    # opcodes/calls fail the test; no plugin or Unity type is initialized.
    $stack = [Collections.Generic.Stack[int]]::new()
    $locals = @{}
    $instruction = $Method.Body.Instructions[0]
    for ($steps = 0; $steps -lt 100; ++$steps) {
        $op = $instruction.OpCode.Name
        $next = $instruction.Next
        switch -Regex ($op) {
            '^nop$' { break }
            '^ldsfld$' {
                $value = switch ($instruction.Operand.Name) {
                    '_localHostStartupFailed' { $Failed }
                    '_localHostRequested' { $Requested }
                    default { throw "Unexpected save-guard field $($instruction.Operand)." }
                }
                $stack.Push([int]$value)
                break
            }
            '^call$' {
                if ($instruction.Operand.DeclaringType.FullName -eq 'ServerManager.ServerManagerRuntime' -and
                    $instruction.Operand.Name -eq 'get_CharacterLoadSaveSuppressed') {
                    $stack.Push([int]$LoadSuppressed)
                    break
                }
                Assert-True ($instruction.Operand.DeclaringType.FullName -eq
                    'ServerManager.LocalHostCharacterRuntime' -and
                    $instruction.Operand.Name -eq 'get_IsActive') `
                    'The save guard gained an unaudited method call.'
                $stack.Push([int]$Active)
                break
            }
            '^ldc\.i4\.([01])$' { $stack.Push([int]$Matches[1]); break }
            '^ceq$' {
                $right = $stack.Pop()
                $stack.Push([int]($stack.Pop() -eq $right))
                break
            }
            '^brtrue(\.s)?$' {
                if ($stack.Pop() -ne 0) { $next = $instruction.Operand }
                break
            }
            '^brfalse(\.s)?$' {
                if ($stack.Pop() -eq 0) { $next = $instruction.Operand }
                break
            }
            '^br(\.s)?$' { $next = $instruction.Operand; break }
            '^stloc\.([0-3])$' { $locals[[int]$Matches[1]] = $stack.Pop(); break }
            '^ldloc\.([0-3])$' { $stack.Push($locals[[int]$Matches[1]]); break }
            '^stloc(\.s)?$' { $locals[$instruction.Operand.Index] = $stack.Pop(); break }
            '^ldloc(\.s)?$' { $stack.Push($locals[$instruction.Operand.Index]); break }
            '^ret$' { return [bool]$stack.Pop() }
            default { throw "Unaudited save-guard opcode $op." }
        }
        if ($null -eq $next) { throw 'Save-guard IL ended without returning.' }
        $instruction = $next
    }
    throw 'Save-guard IL exceeded its bounded execution budget.'
}

function Invoke-HostAccountCheckIl {
    param($Method, [uint64]$CurrentSteamId, [uint64]$ExpectedSteamId)
    # Execute the account-check tail of the real RequirePlayer IL. The earlier
    # player/session gates are checked separately below; no Steam or Unity call runs.
    $stack = [Collections.Generic.Stack[object]]::new()
    $locals = @{}
    $instruction = Get-Call $Method 'Steamworks.SteamUser' 'GetSteamID'
    for ($steps = 0; $steps -lt 80; ++$steps) {
        $op = $instruction.OpCode.Name
        $next = $instruction.Next
        switch -Regex ($op) {
            '^nop$' { break }
            '^call$' {
                Assert-True ($instruction.Operand.DeclaringType.FullName -eq
                    'Steamworks.SteamUser' -and $instruction.Operand.Name -eq 'GetSteamID') `
                    'The per-frame account check gained an unaudited call.'
                $stack.Push($CurrentSteamId)
                break
            }
            '^ldarg\.0$' { $stack.Push($ExpectedSteamId); break }
            '^ldfld$' {
                Assert-True ($instruction.Operand.FullName -in @(
                    'System.UInt64 Steamworks.CSteamID::m_SteamID',
                    'System.UInt64 ServerManager.LocalHostCharacterRuntime/HostState::SteamId')) `
                    'The per-frame account check read an unaudited field.'
                # Each simulated receiver already carries its corresponding ID.
                break
            }
            '^stloc\.([0-3])$' { $locals[[int]$Matches[1]] = $stack.Pop(); break }
            '^ldloc\.([0-3])$' { $stack.Push($locals[[int]$Matches[1]]); break }
            '^stloc(\.s)?$' { $locals[$instruction.Operand.Index] = $stack.Pop(); break }
            '^ldloca?(\.s)?$' { $stack.Push($locals[$instruction.Operand.Index]); break }
            '^ldc\.i4\.([01])$' { $stack.Push([int]$Matches[1]); break }
            '^ceq$' {
                $right = $stack.Pop()
                $stack.Push([int]($stack.Pop() -eq $right))
                break
            }
            '^bne\.un(\.s)?$' {
                $right = $stack.Pop()
                if ($stack.Pop() -ne $right) { $next = $instruction.Operand }
                break
            }
            '^beq(\.s)?$' {
                $right = $stack.Pop()
                if ($stack.Pop() -eq $right) { $next = $instruction.Operand }
                break
            }
            '^brtrue(\.s)?$' { if ($stack.Pop()) { $next = $instruction.Operand }; break }
            '^brfalse(\.s)?$' { if (-not $stack.Pop()) { $next = $instruction.Operand }; break }
            '^br(\.s)?$' { $next = $instruction.Operand; break }
            '^ldstr$' { $stack.Push($instruction.Operand); break }
            '^newobj$' {
                Assert-True ($instruction.Operand.DeclaringType.FullName -eq
                    'ServerManager.CharacterProtocolException') 'Account mismatch changed its rejection kind.'
                break
            }
            '^throw$' { return $false }
            '^ret$' { return $true }
            default { throw "Unaudited host-account opcode $op." }
        }
        if ($null -eq $next) { throw 'Host-account IL ended without a decision.' }
        $instruction = $next
    }
    throw 'Host-account IL exceeded its bounded execution budget.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$gameAssemblyPath = Join-Path $GamePath "valheim_Data\Managed\assembly_valheim.dll"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"
foreach ($path in @($pluginPath, $gameAssemblyPath, $cecilPath)) {
    Assert-True (Test-Path -LiteralPath $path) "Required assembly was not found: $path"
}

# Cecil reads metadata only: this test never starts Valheim, constructs Unity
# objects, binds a listener, loads a real profile, or writes a save-data path.
[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)
$plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
try {
    # Awake selects/loads the source profile. No player exists yet. Start reads
    # firstSpawn, so an authoritative profile must be installed before it runs.
    $awake = Get-Method $game "Game" "Awake" 0
    $profileLoads = @(Get-Calls $awake "PlayerProfile" "Load")
    Assert-True ($profileLoads.Count -eq 2) `
        "Game.Awake's selected/dev profile-load paths changed; review preparation timing."
    Assert-True (@(Get-Calls $awake "PlayerProfile" "LoadPlayerData").Count -eq 0) `
        "Game.Awake now loads player data before host preparation."
    $start = Get-Method $game "Game" "Start" 0
    Assert-True (@(Get-FieldInstructions $start "PlayerProfile" "m_firstSpawn" "ldfld").Count -eq 1) `
        "Game.Start's first-spawn profile use changed."
    Assert-True (@(Get-Calls $start "Game" "SpawnPlayer").Count -eq 0 -and
        @(Get-Calls $start "PlayerProfile" "LoadPlayerData").Count -eq 0) `
        "Game.Start now spawns or loads the player directly."

    $fixedUpdate = Get-Method $game "Game" "FixedUpdate" 0
    $requestRespawn = Get-Call $fixedUpdate "Game" "RequestRespawn"
    $updateRespawnCall = Get-Call $fixedUpdate "Game" "UpdateRespawn"
    Assert-True ($requestRespawn.Offset -lt $updateRespawnCall.Offset) `
        "Game.FixedUpdate's initial spawn ordering changed."
    $updateRespawn = Get-Method $game "Game" "UpdateRespawn" 1
    $findSpawn = Get-Call $updateRespawn "Game" "FindSpawnPoint"
    $homePoint = Get-Call $updateRespawn "PlayerProfile" "SetHomePoint"
    $spawn = Get-Call $updateRespawn "Game" "SpawnPlayer"
    Assert-True ($findSpawn.Offset -lt $homePoint.Offset -and
        $homePoint.Offset -lt $spawn.Offset) `
        "Host readiness must be reviewed: spawn-position/profile mutation order changed."

    $spawnPlayer = Get-Method $game "Game" "SpawnPlayer" 2
    $instantiate = Get-Call $spawnPlayer "UnityEngine.Object" "Instantiate"
    $setLocal = Get-Call $spawnPlayer "Player" "SetLocalPlayer"
    $loadData = Get-Call $spawnPlayer "PlayerProfile" "LoadPlayerData"
    $onSpawned = Get-Call $spawnPlayer "Player" "OnSpawned"
    Assert-True ($instantiate.Offset -lt $setLocal.Offset -and
        $setLocal.Offset -lt $loadData.Offset -and
        $loadData.Offset -lt $onSpawned.Offset) `
        "Player prefab/local-player/profile-load ordering changed."
    $loadPlayer = Get-Method $game "PlayerProfile" "LoadPlayerData" 1
    $null = Get-Call $loadPlayer "Player" "Load"

    # A previously generated world opens the listener synchronously inside
    # ZNet.Start: Game.Start must not be assumed to have run first.
    $networkStart = Get-Method $game "ZNet" "Start" 0
    $null = Get-Call $networkStart "ZNet" "ServerLoadWorld"
    $serverLoad = Get-Method $game "ZNet" "ServerLoadWorld" 0
    $worldLoad = Get-Call $serverLoad "ZNet" "LoadWorld"
    $generate = Get-Call $serverLoad "ZoneSystem" "GenerateLocationsIfNeeded"
    $subscribe = Get-Call $serverLoad "ZoneSystem" "add_GenerateLocationsCompleted"
    Assert-True ($worldLoad.Offset -lt $generate.Offset -and
        $generate.Offset -lt $subscribe.Offset) `
        "ZNet server-load/generation/listener callback ordering changed."
    $subscribeBody = Get-Method $game "ZoneSystem" "add_GenerateLocationsCompleted" 1
    $immediate = Get-Call $subscribeBody "System.Action" "Invoke"
    $combine = Get-Call $subscribeBody "System.Delegate" "Combine"
    $generated = @(Get-FieldInstructions $subscribeBody "ZoneSystem" "m_locationsGenerated" "ldfld")
    Assert-True ($generated.Count -eq 1 -and
        $generated[0].Offset -lt $immediate.Offset -and
        $immediate.Offset -lt $combine.Offset) `
        "Generation completion no longer has the audited synchronous callback path."
    $generationFinished = Get-Method $game "ZNet" "OnGenerationFinished" 0
    $open = Get-Call $generationFinished "ZNet" "OpenServer"
    $openFlag = @(Get-FieldInstructions $generationFinished "ZNet" "m_openServer" "ldsfld")
    Assert-True ($openFlag.Count -eq 1 -and $openFlag[0].Offset -lt $open.Offset) `
        "Listener opening is no longer gated by the selected open-server flag."
    $setServer = Get-Method $game "ZNet" "SetServer" 6
    $openAssignment = @(Get-FieldInstructions $setServer "ZNet" "m_openServer" "stsfld")
    Assert-True ($setServer.Parameters[1].Name -eq "openServer" -and
        $openAssignment.Count -eq 1 -and
        $openAssignment[0].Previous.OpCode.Name -eq "ldarg.1") `
        "Hosted/standalone selection no longer maps to SetServer's openServer argument."

    # Logout(false, true) is NOT a no-save primitive in this game: its disk-space
    # check overwrites 'save'. The explicit ContinueLogout(false,true,true) path
    # must remain available to return a failed host to the lobby without saving.
    $logout = Get-Method $game "Game" "Logout" 2
    $diskSpace = Get-Call $logout "ZNet" "EnoughDiskSpaceAvailable"
    $continueCall = Get-Call $logout "Game" "ContinueLogout"
    Assert-True ($diskSpace.Next.OpCode.Name -eq "stfld" -and
        $diskSpace.Next.Operand.Name -eq "save" -and
        $diskSpace.Offset -lt $continueCall.Offset) `
        "Game.Logout save-argument rewriting changed; review the no-save failure primitive."
    $continueLogout = Get-Method $game "Game" "ContinueLogout" 3
    Assert-True (@($continueLogout.Parameters | Where-Object {
        $_.ParameterType.FullName -ne "System.Boolean"
    }).Count -eq 0) "ContinueLogout's audited boolean signature changed."
    $shutdownCall = Get-Call $continueLogout "Game" "Shutdown"
    Assert-True ($shutdownCall.Previous.OpCode.Name -eq "ldarg.1") `
        "ContinueLogout no longer forwards the explicit save argument to Shutdown."
    $sceneLoad = Get-Call $continueLogout `
        "SystemResourceManager" "FastLoadScene"
    Assert-True ($shutdownCall.Offset -lt $sceneLoad.Offset) `
        "ContinueLogout no longer finishes shutdown before returning to the lobby."

    $shutdown = Get-Method $game "Game" "Shutdown" 1
    $saveProfile = Get-Call $shutdown "Game" "SavePlayerProfile"
    $sceneShutdown = Get-Call $shutdown "ZNetScene" "Shutdown"
    $networkShutdownCall = Get-Call $shutdown "ZNet" "Shutdown"
    Assert-True ($saveProfile.Offset -lt $sceneShutdown.Offset -and
        $sceneShutdown.Offset -lt $networkShutdownCall.Offset) `
        "Game final-save/player-destroy/network-shutdown ordering changed."
    Assert-FalseArgumentSkipsCall $shutdown 1 $saveProfile $sceneShutdown
    $networkShutdown = Get-Method $game "ZNet" "Shutdown" 1
    $worldSave = Get-Call $networkShutdown "ZNet" "Save"
    $stopAll = Get-Call $networkShutdown "ZNet" "StopAll"
    Assert-FalseArgumentSkipsCall $networkShutdown 1 $worldSave $stopAll
    $stopAllBody = Get-Method $game "ZNet" "StopAll" 1
    Assert-True (@(Get-Calls $stopAllBody "ZNet" "Save").Count -eq 0 -and
        @(Get-Calls $stopAllBody "Game" "SavePlayerProfile").Count -eq 0) `
        "ZNet.StopAll now starts a save even on no-save shutdown."

    $profileSave = Get-Method $game "Game" "SavePlayerProfile" 2
    $capturePlayer = Get-Call $profileSave "PlayerProfile" "SavePlayerData"
    $saveSlot = Get-Call $profileSave "PlayerProfile" "Save"
    Assert-True ($capturePlayer.Offset -lt $saveSlot.Offset) `
        "Game.SavePlayerProfile no longer captures the player before writing the slot."
    $saveLocal = Get-Method $game "PlayerProfile" "Save" 0
    $diskWrite = Get-Call $saveLocal "PlayerProfile" "SavePlayerToDisk"
    $filenameReads = @(Get-FieldInstructions $saveLocal "PlayerProfile" "m_filename" "ldfld")
    Assert-True ($filenameReads.Count -eq 1 -and
        $filenameReads[0].Next.OpCode.Name -in @("brtrue", "brtrue.s") -and
        -not (Test-Reachable $filenameReads[0].Next.Next $diskWrite) -and
        (Test-Reachable $filenameReads[0].Next.Operand $diskWrite)) `
        "A filename-bound profile must reach vanilla disk saving; only a null filename may bypass it."
    foreach ($spec in @(@("ZNet", "m_openServer", $true),
        @("Game", "m_playerProfile", $false))) {
        $field = $game.MainModule.GetType($spec[0]).Fields |
            Where-Object Name -eq $spec[1]
        Assert-True ($null -ne $field -and $field.IsPrivate -and
            $field.IsStatic -eq $spec[2]) `
            "Audited private access changed for $($spec[0]).$($spec[1])."
    }

    # The remaining checks exercise the built plugin's guards and host lease
    # lifecycle without executing Unity/game code.
    $privateAccess = Get-Method $plugin "ServerManager.ValheimPrivateAccess" "GetOpenServer" 0
    $null = Get-Call $privateAccess "System.Reflection.FieldInfo" "GetValue"
    Assert-True (@(Get-FieldInstructions $privateAccess "ZNet" "m_openServer").Count -eq 0) `
        "Hosted-mode detection emitted an illegal direct reference to a private game field."

    $runtimeName = "ServerManager.ServerManagerRuntime"
    $hostName = "ServerManager.LocalHostCharacterRuntime"
    $pluginSettings = $plugin.MainModule.GetType("ServerManager.ServerManagerPlugin")
    Assert-True (@($pluginSettings.Properties |
        Where-Object Name -eq "EnableServerCharacters").Count -eq 0) `
        "Managed host characters still have a cfg opt-out."
    foreach ($fixedCoreMethodName in @(
            "BeforeNetworkStart", "CaptureLocalHostIntent", "BeforeLocalHostGameplay")) {
        $fixedCoreMethod = Get-Method $plugin $runtimeName $fixedCoreMethodName 1
        Assert-True (@(Get-Calls $fixedCoreMethod $pluginSettings.FullName `
            "get_EnableServerCharacters").Count -eq 0) `
            "$fixedCoreMethodName still branches on the removed character-enable cfg."
    }
    $startPrefix = Get-Method $plugin "ServerManager.LocalHostCharacterStartPatch" "Prefix" 1
    Assert-True ($startPrefix.ReturnType.FullName -eq "System.Void") `
        "The host Game.Start hook must prepare first without skipping vanilla setup."
    $null = Get-Call $startPrefix $runtimeName "BeforeLocalHostGameplay"
    $spawnPrefix = Get-Method $plugin "ServerManager.LocalHostCharacterSpawnGatePatch" "Prefix" 1
    Assert-True ($spawnPrefix.ReturnType.FullName -eq "System.Boolean") `
        "Initial FixedUpdate must be able to block respawn before player creation."
    $null = Get-Call $spawnPrefix $runtimeName "BeforeLocalHostGameplay"
    $shutdownPrefix = Get-Method $plugin "ServerManager.LocalHostCharacterShutdownPatch" "Prefix" 1
    Assert-True ($shutdownPrefix.Parameters[0].ParameterType.FullName -eq "System.Boolean&" -and
        $shutdownPrefix.Parameters[0].Name -eq "__0") `
        "Shutdown must bind its boolean by index; vanilla calls it saveWorld, not save."
    $null = Get-Call $shutdownPrefix $runtimeName "BeforeGameShutdown"

    $networkPreparation = Get-Method $plugin $runtimeName "BeforeNetworkStart" 1
    $latch = Get-Call $networkPreparation $runtimeName "CaptureLocalHostIntent"
    $rootPreparation = Get-Call $networkPreparation "ServerManager.ServerDataRoot" "BindAndPrepare"
    $closeListeners = @(Get-Calls $networkPreparation "ServerManager.ValheimPrivateAccess" "SetOpenServer")
    Assert-True ($latch.Offset -lt $rootPreparation.Offset -and
        @($closeListeners | Where-Object { $_.Offset -lt $latch.Offset }).Count -eq 0) `
        "Hosted intent must be latched before preparation can clear the open-server flag."
    $captureIntent = Get-Method $plugin $runtimeName "CaptureLocalHostIntent" 1
    $serverCheck = Get-Call $captureIntent "ZNet" "IsServer"
    $dedicatedCheck = Get-Call $captureIntent "ZNet" "IsDedicated"
    $readOpen = Get-Call $captureIntent "ServerManager.ValheimPrivateAccess" "GetOpenServer"
    Assert-True ($serverCheck.Offset -lt $readOpen.Offset -and
        $dedicatedCheck.Offset -lt $readOpen.Offset) `
        "Host detection must exclude dedicated/remote modes before reading the selected host flag."
    $intentStore = @(Get-FieldInstructions $captureIntent $runtimeName "_localHostRequested" "stsfld")
    Assert-True ($intentStore.Count -eq 1 -and $readOpen.Offset -lt $intentStore[0].Offset) `
        "Ordinary IsServer singleplayer must not imply managed local-host intent."

    $beforeGameplay = Get-Method $plugin $runtimeName "BeforeLocalHostGameplay" 1
    $prepareData = Get-Call $beforeGameplay $runtimeName "BeforeNetworkStart"
    $storageReady = Get-Call $beforeGameplay $runtimeName "EnsureServerCharacterStorageReady"
    $prepareHost = Get-Call $beforeGameplay $hostName "Prepare"
    Assert-True ($prepareData.Offset -lt $storageReady.Offset -and
        $storageReady.Offset -lt $prepareHost.Offset) `
        "Host profile preparation must follow data-root setup and native storage validation."
    $intentRead = @(Get-FieldInstructions $beforeGameplay $runtimeName "_localHostRequested" "ldsfld")
    Assert-True ($intentRead.Count -eq 1 -and $intentRead[0].Offset -lt $prepareHost.Offset) `
        "Standalone singleplayer must be guarded before managed-profile preparation."
    $beforeOpen = Get-Method $plugin $runtimeName "BeforeServerOpen" 1
    $null = Get-Call $beforeOpen $runtimeName "BeforeLocalHostGameplay"

    $saveGuard = Get-Method $plugin $runtimeName "AllowLocalHostSave" 0
    foreach ($failed in @($false, $true)) {
        foreach ($requested in @($false, $true)) {
            foreach ($active in @($false, $true)) {
                $actual = Invoke-SaveGuardIl $saveGuard $failed $requested $active
                Assert-True ($actual -eq (-not $failed -and (-not $requested -or $active))) `
                    "Save guard violated failed=$failed, hosted=$requested, active=$active."
                Assert-True (-not (Invoke-SaveGuardIl $saveGuard $failed $requested $active $true)) `
                    "A suppressed character load must block saving for every host state."
            }
        }
    }
    foreach ($spec in @(@("ServerManager.ManagedCharacterSavePatch", "Prefix", 2),
        @("ServerManager.ServerEventWorldSavePatch", "Prefix", 0),
        @($runtimeName, "BeforeGameShutdown", 1))) {
        $guarded = Get-Method $plugin $spec[0] $spec[1] $spec[2]
        $null = Get-Call $guarded $runtimeName "AllowLocalHostSave"
    }

    $failureExit = Get-Method $plugin $runtimeName "ProcessLocalHostStartupFailure" 0
    Assert-True (@(Get-Calls $failureExit "Game" "Logout").Count -eq 0) `
        "A rejected host must never use Logout(false,true), which can save anyway."
    $continuationName = @($failureExit.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq "ldstr" -and $_.Operand -ceq "ContinueLogout"
    })
    Assert-True ($continuationName.Count -eq 1) `
        "A refused host no longer selects the audited no-save continuation."
    $boxedArguments = @($failureExit.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq "box" -and
        $_.Operand.FullName -eq "System.Boolean"
    })
    Assert-True ($boxedArguments.Count -eq 3 -and
        $boxedArguments[0].Previous.OpCode.Name -eq "ldc.i4.0" -and
        $boxedArguments[1].Previous.OpCode.Name -eq "ldc.i4.1" -and
        $boxedArguments[2].Previous.OpCode.Name -eq "ldc.i4.1") `
        "Failure exit must call ContinueLogout with exactly (false, true, true)."

    $checkpoint = Get-Method $plugin $runtimeName "BeforeWorldSnapshotPreparedCore" 0
    $captureHost = Get-Call $checkpoint $hostName "CaptureForWorldCheckpoint"
    $freeze = Get-Call $checkpoint "ServerManager.CharacterSnapshotService" "BeginCheckpoint"
    Assert-True ($captureHost.Offset -lt $freeze.Offset) `
        "Host character capture must occur before the shared world checkpoint cutoff."
    $runtimeShutdown = Get-Method $plugin $runtimeName "Shutdown" 0
    $drain = Get-Call $runtimeShutdown $runtimeName "DrainPendingCharacterCheckpointBeforeShutdown"
    $closeHost = Get-Call $runtimeShutdown $hostName "Close"
    $disposeService = Get-Call $runtimeShutdown "ServerManager.CharacterSnapshotService" "Dispose"
    Assert-True ($drain.Offset -lt $closeHost.Offset -and
        $closeHost.Offset -lt $disposeService.Offset) `
        "Runtime teardown must drain checkpoints, close the host lease, then dispose storage."
    $networkStopped = Get-Method $plugin $runtimeName "AfterNetworkShutdown" 3
    $networkCloseHost = Get-Call $networkStopped $hostName "Close"
    $networkDrains = @(Get-Calls $networkStopped $runtimeName "DrainPendingCharacterCheckpointBeforeShutdown")
    $disposeLabel = @($networkStopped.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq "ldstr" -and $_.Operand -ceq "character service disposal"
    })
    Assert-True ($networkDrains.Count -eq 2 -and $disposeLabel.Count -eq 1 -and
        $networkDrains[1].Offset -lt $networkCloseHost.Offset -and
        $networkCloseHost.Offset -lt $disposeLabel[0].Offset) `
        "Successful network teardown must drain before host close and dispose storage afterward."

    $hostPrepare = Get-Method $plugin $hostName "Prepare" 3
    $openLease = Get-Call $hostPrepare "ServerManager.CharacterSnapshotService" "OpenOrCreateLocalHostSession"
    $openBackupLease = Get-Call $hostPrepare "ServerManager.CharacterSnapshotService" "OpenBackupLocalHostSession"
    $captureSelected = Get-Call $hostPrepare "ServerManager.ValheimPlayerProfileCodec" "SerializeProfileToBytes"
    $settingsRead = Get-Call $hostPrepare $runtimeName "get_CurrentServerSettings"
    $loadSettingRead = Get-Call $hostPrepare "ServerManager.ServerSettings" "get_LoadServerCharacterOnJoin"
    $decodeManaged = Get-Call $hostPrepare "ServerManager.ValheimPlayerProfileCodec" "DeserializeProfileFromBytes"
    $usedGuard = Get-Call $hostPrepare "ServerManager.LocalCharacterFirstJoinGuard" "ShouldRejectUsedLocalFirstJoin"
    $profileSwaps = @(Get-Calls $hostPrepare "ServerManager.ValheimPrivateAccess" "SetGamePlayerProfile")
    $prepareRestores = @(Get-Calls $hostPrepare $hostName "Restore")
    $finalizeInitial = Get-Call $hostPrepare "ServerManager.CharacterSnapshotService" "FinalizePendingLocalHostSnapshot"
    $closeFailedLease = Get-Call $hostPrepare "ServerManager.CharacterSnapshotService" "CloseLocalHostSession"
    Assert-True ($profileSwaps.Count -eq 1 -and $prepareRestores.Count -eq 2 -and
        $openLease.Offset -lt $decodeManaged.Offset -and
        $decodeManaged.Offset -lt $usedGuard.Offset -and
        $usedGuard.Offset -lt $profileSwaps[0].Offset -and
        $profileSwaps[0].Offset -lt $finalizeInitial.Offset -and
        $finalizeInitial.Offset -lt $prepareRestores[1].Offset -and
        $prepareRestores[1].Offset -lt $closeFailedLease.Offset) `
        "Host preparation must open, validate/guard, swap, finalize; failure restores before lease close."
    Assert-True ($settingsRead.Offset -lt $loadSettingRead.Offset -and
        $loadSettingRead.Offset -lt $captureSelected.Offset -and
        $captureSelected.Offset -lt $openBackupLease.Offset -and
        $openBackupLease.Offset -lt $decodeManaged.Offset) `
        "Backup host admission must capture the selected profile under one settings snapshot before binding the managed profile."
    Assert-True ($loadSettingRead.Next.OpCode.Name -eq 'ldc.i4.0' -and
        $loadSettingRead.Next.Next.OpCode.Name -eq 'ceq') `
        "Host capture mode must negate loadServerCharacterOnJoin: false captures local, true loads the server character."
    $remoteConnect = Get-Method $plugin $runtimeName 'AfterNewConnection' 2
    $remoteLoadSetting = Get-Call $remoteConnect 'ServerManager.ServerSettings' 'get_LoadServerCharacterOnJoin'
    $remoteBackupSetter = Get-Call $remoteConnect "$runtimeName/ServerDetectionState" 'set_BackupOnly'
    Assert-True ($remoteLoadSetting.Next.OpCode.Name -eq 'ldc.i4.0' -and
        $remoteLoadSetting.Next.Next.OpCode.Name -eq 'ceq' -and
        $remoteLoadSetting.Next.Next.Next.Offset -eq $remoteBackupSetter.Offset) `
        'Remote admission must capture the same inverse setting as the host: false collects local, true loads server.'
    $backupGuardReads = @(Get-Calls $hostPrepare "ServerManager.CharacterSession" "get_BackupOnly" |
        Where-Object { $_.Offset -lt $usedGuard.Offset })
    Assert-True ($backupGuardReads.Count -eq 1) `
        "The local first-join guard must use the host session's captured backup-only mode."
    $backupGuardSkips = @($hostPrepare.Body.Instructions | Where-Object {
        $_.Offset -gt $backupGuardReads[0].Offset -and $_.Offset -lt $usedGuard.Offset -and
        $_.OpCode.FlowControl -eq [Mono.Cecil.Cil.FlowControl]::Cond_Branch -and
        $_.Operand -is [Mono.Cecil.Cil.Instruction] -and $_.Operand.Offset -gt $usedGuard.Offset
    })
    Assert-True ($backupGuardSkips.Count -eq 1) `
        "Backup host sessions must bypass the used-local first-join guard before it executes."
    $selectedFilename = Get-Call $hostPrepare 'PlayerProfile' 'GetFilename'
    $selectedFileSource = @(Get-FieldInstructions $hostPrepare 'PlayerProfile' 'm_fileSource' 'ldfld')
    Assert-True ($selectedFileSource.Count -eq 1 -and
        $selectedFilename.Offset -lt $selectedFileSource[0].Offset -and
        $selectedFileSource[0].Next.Offset -eq $decodeManaged.Offset) `
        'Host gameplay must retain the selected filename and Local/Cloud source so vanilla and mod saves work without server approval.'
    Assert-True (@(Get-Calls $hostPrepare "PlayerProfile" "LoadPlayerData").Count -eq 0 -and
        @(Get-Calls $hostPrepare "Player" "Load").Count -eq 0 -and
        @(Get-Calls $hostPrepare "Game" "SpawnPlayer").Count -eq 0) `
        "Host preparation must not load/spawn an alternate player as a side effect."

    $initialAccount = Get-Call $hostPrepare $hostName 'GetLocalAccountId'
    Assert-True ($initialAccount.Offset -lt $openLease.Offset) `
        'The initial Steam account must be canonical before opening a host session.'
    $accountResolver = Get-Method $plugin $hostName 'GetLocalAccountId' 0
    $null = Get-Call $accountResolver 'ServerManager.CharacterPeerIdentityResolver' 'CreateCanonicalAccountId'
    $hostConstructor = Get-Method $plugin "$hostName/HostState" '.ctor' 8
    $parseAccount = Get-Call $hostConstructor 'ServerManager.CharacterSteamIdentity' 'TryParseCanonicalAccountId'
    $storedAccount = @(Get-FieldInstructions $hostConstructor "$hostName/HostState" 'SteamId' 'stfld')
    $steamIdField = $plugin.MainModule.GetType("$hostName/HostState").Fields |
        Where-Object Name -eq 'SteamId'
    Assert-True ($storedAccount.Count -eq 1 -and $parseAccount.Offset -lt $storedAccount[0].Offset -and
        $steamIdField.IsInitOnly -and $steamIdField.FieldType.FullName -eq 'System.UInt64') `
        'Host state must retain one immutable, validated numeric Steam identity.'
    $requirePlayer = Get-Method $plugin $hostName 'RequirePlayer' 1
    $currentAccount = Get-Call $requirePlayer 'Steamworks.SteamUser' 'GetSteamID'
    foreach ($check in @(@($hostName, 'EnsureSession'), @('Player', 'GetPlayerID'), @('Player', 'GetPlayerName'))) {
        Assert-True ((Get-Call $requirePlayer $check[0] $check[1]).Offset -lt $currentAccount.Offset) `
            "The current host account check lost its earlier $($check[1]) gate."
    }
    Assert-True (@(Get-Calls $requirePlayer $hostName 'GetLocalAccountId').Count -eq 0 -and
        @(Get-FieldInstructions $requirePlayer "$hostName/HostState" 'SteamId' 'ldfld').Count -eq 1) `
        'The per-frame host identity check must compare the current numeric Steam ID without rebuilding the account string.'
    $expectedSteamId = [uint64]76561198000000001
    foreach ($currentSteamId in @($expectedSteamId, [uint64]76561198000000002,
        [uint64]0, [uint64]::MaxValue, $expectedSteamId)) {
        Assert-True ((Invoke-HostAccountCheckIl $requirePlayer $currentSteamId $expectedSteamId) -eq
            ($currentSteamId -eq $expectedSteamId)) `
            "The host account check changed for current Steam ID $currentSteamId."
    }

    $hostClose = Get-Method $plugin $hostName 'Close' 0
    $null = Get-Call $hostClose 'ServerManager.CharacterSnapshotService' 'CloseLocalHostSession'
    $closeRestorations = @($hostClose.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq "$hostName/Restoration" -and $_.Operand.Name -eq '.ctor'
    })
    Assert-True ($closeRestorations.Count -eq 0 -and
        @(Get-Calls $hostClose $hostName 'CaptureFull').Count -eq 0 -and
        @(Get-Calls $hostClose $hostName 'Submit').Count -eq 0 -and
        @(Get-Calls $hostClose 'Game' 'SavePlayerProfile').Count -eq 0 -and
        @(Get-Calls $hostClose 'ZNet' 'Save').Count -eq 0 -and
        @(Get-Calls $hostClose 'PlayerProfile' 'Save').Count -eq 0 -and
        @(Get-Calls $hostClose 'ServerManager.ValheimPrivateAccess' 'SetGamePlayerProfile').Count -eq 0) `
        'Host close must release the server lease without recapturing or restoring an older local profile.'
    $obsoleteHostState = @($plugin.MainModule.GetType("$hostName/HostState").Fields | Where-Object {
        $_.Name -in @('OriginalProfile', 'RestorableProfile', 'MirroredRevision')
    })
    Assert-True ($obsoleteHostState.Count -eq 0) 'Host state still retains an obsolete accepted local mirror for normal-session restoration.'
    $fullCapture = Get-Method $plugin $hostName "CaptureFull" 3
    $captureData = Get-Call $fullCapture "PlayerProfile" "SavePlayerData"
    $captureMap = Get-Call $fullCapture "Minimap" "SaveMapData"
    $capturePosition = Get-Call $fullCapture "PlayerProfile" "SaveLogoutPoint"
    $serializeCapture = Get-Call $fullCapture "ServerManager.ValheimPlayerProfileCodec" "SerializeProfileToBytes"
    $submitCapture = Get-Call $fullCapture $hostName "Submit"
    Assert-True ($captureData.Offset -lt $captureMap.Offset -and
        $captureMap.Offset -lt $capturePosition.Offset -and
        $capturePosition.Offset -lt $serializeCapture.Offset -and
        $serializeCapture.Offset -lt $submitCapture.Offset) `
        "Fresh host full capture must include player, map, and guarded logout position before admission."
    $restoreHost = Get-Method $plugin $hostName "Restore" 0
    $restoreSwap = Get-Call $restoreHost "ServerManager.ValheimPrivateAccess" "SetGamePlayerProfile"
    $livePlayerReads = @(Get-FieldInstructions $restoreHost "Player" "m_localPlayer" "ldsfld")
    Assert-True ($livePlayerReads.Count -eq 1 -and
        $livePlayerReads[0].Offset -lt $restoreSwap.Offset) `
        "Profile restoration must inspect and avoid a still-live local player."

    Write-Host "PASS: Local-host character lifecycle smoke ($script:assertions assertions; metadata only)."
}
finally {
    $plugin.Dispose()
    $game.Dispose()
}
