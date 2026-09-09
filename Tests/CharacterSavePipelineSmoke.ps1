param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-ByteArrayEqual {
    param(
        [byte[]]$Left,
        [byte[]]$Right
    )

    if ($null -eq $Left -or $null -eq $Right -or
        $Left.Length -ne $Right.Length) {
        return $false
    }

    $difference = 0
    for ($index = 0; $index -lt $Left.Length; ++$index) {
        $difference = $difference -bor ($Left[$index] -bxor $Right[$index])
    }

    return $difference -eq 0
}

function Get-InstanceMethod {
    param(
        [Type]$Type,
        [string]$Name,
        [int]$ParameterCount
    )

    $flags = [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::Public -bor
        [Reflection.BindingFlags]::NonPublic
    return $Type.GetMethods($flags) |
        Where-Object {
            $_.Name -eq $Name -and
            $_.GetParameters().Count -eq $ParameterCount
        } |
        Select-Object -First 1
}

function Get-InstanceProperty {
    param(
        [object]$Instance,
        [string]$Name
    )

    $flags = [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::Public -bor
        [Reflection.BindingFlags]::NonPublic
    $property = $Instance.GetType().GetProperty($Name, $flags)
    Assert-True ($null -ne $property) "Missing property $Name."
    return $property.GetValue($Instance)
}

function Set-InstanceField {
    param(
        [object]$Instance,
        [string]$Name,
        [object]$Value
    )

    $flags = [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic
    $field = $Instance.GetType().GetField($Name, $flags)
    Assert-True ($null -ne $field) "Missing field $Name."
    $field.SetValue($Instance, $Value)
}

function Get-CecilSuccessors {
    param($Instruction)

    if ($null -eq $Instruction) {
        return @()
    }

    $flowControl = $Instruction.OpCode.FlowControl.ToString()
    if ($flowControl -eq "Branch") {
        return @($Instruction.Operand)
    }

    if ($flowControl -eq "Cond_Branch") {
        $successors = @()
        if ($Instruction.Operand -is [Collections.IEnumerable] -and
            $Instruction.Operand -isnot [string]) {
            $successors += @($Instruction.Operand)
        }
        else {
            $successors += $Instruction.Operand
        }

        if ($null -ne $Instruction.Next) {
            $successors += $Instruction.Next
        }

        return $successors
    }

    if ($flowControl -eq "Return" -or $flowControl -eq "Throw") {
        return @()
    }

    if ($null -eq $Instruction.Next) {
        return @()
    }

    return @($Instruction.Next)
}

function Test-CecilReachable {
    param(
        $Start,
        $Target
    )

    if ($null -eq $Start -or $null -eq $Target) {
        return $false
    }

    $pending = [Collections.Generic.Queue[object]]::new()
    $visited = @{}
    $pending.Enqueue($Start)
    while ($pending.Count -ne 0) {
        $current = $pending.Dequeue()
        if ($visited.ContainsKey($current.Offset)) {
            continue
        }

        if ($current.Offset -eq $Target.Offset) {
            return $true
        }

        $visited[$current.Offset] = $true
        foreach ($successor in @(Get-CecilSuccessors $current)) {
            if ($null -ne $successor -and
                -not $visited.ContainsKey($successor.Offset)) {
                $pending.Enqueue($successor)
            }
        }
    }

    return $false
}

function Get-PluginMethodDefinition {
    param(
        [string]$TypeName,
        [string]$MethodName,
        [int]$ParameterCount = -1
    )

    $typeDefinition = $script:pluginDefinition.MainModule.Types |
        Where-Object FullName -eq $TypeName |
        Select-Object -First 1
    Assert-True ($null -ne $typeDefinition) `
        "The plugin type $TypeName was not found."
    $method = $typeDefinition.Methods |
        Where-Object { $_.Name -eq $MethodName -and
            ($ParameterCount -lt 0 -or $_.Parameters.Count -eq $ParameterCount) } |
        Select-Object -First 1
    Assert-True ($null -ne $method -and $method.HasBody) `
        "The plugin method $TypeName.$MethodName was not found."
    return $method
}

function Get-CecilCall {
    param(
        $Method,
        [string]$DeclaringType,
        [string]$MethodName
    )

    return $Method.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq $DeclaringType -and
            $_.Operand.Name -eq $MethodName
        } |
        Select-Object -First 1
}

function Get-CecilStringInstruction {
    param(
        $Method,
        [string]$Text
    )

    return $Method.Body.Instructions |
        Where-Object {
            $_.Operand -is [string] -and
            $_.Operand.Contains($Text)
        } |
        Select-Object -First 1
}

function Test-CecilInstructionInRange {
    param(
        $Instruction,
        $Start,
        $End
    )

    if ($null -eq $Instruction -or $null -eq $Start) {
        return $false
    }

    return $Instruction.Offset -ge $Start.Offset -and
        ($null -eq $End -or $Instruction.Offset -lt $End.Offset)
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$gameAssemblyPath = Join-Path $GamePath `
    "valheim_Data\Managed\assembly_valheim.dll"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"
Assert-True (Test-Path -LiteralPath $pluginPath) `
    "Build ServerManager before running this smoke test."
Assert-True (Test-Path -LiteralPath $gameAssemblyPath) `
    "The installed Valheim assembly was not found."
Assert-True (Test-Path -LiteralPath $cecilPath) `
    "Mono.Cecil from BepInEx was not found."

[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
$gameDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $gameAssemblyPath)
$pluginDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $pluginPath)
$gameAssembly = [Reflection.Assembly]::LoadFrom($gameAssemblyPath)

$pluginSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Plugin.cs"))
$characterModelsSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Character\CharacterModels.cs"))
$snapshotServiceSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Character\CharacterSnapshotService.cs"))
$characterRepositorySource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Character\CharacterRepository.cs"))
$runtimeSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Networking\ServerManagerRuntime.cs"))
$coordinatorSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Networking\ConnectionSessionCoordinator.cs"))
$sendClientSaveSource = [Text.RegularExpressions.Regex]::Match(
    $runtimeSource,
    'private static void SendClientSave\([\s\S]*?' +
        'private static void BeginClientGameplayQuiescence').Value
$afterInventoryChangedSource = [Text.RegularExpressions.Regex]::Match(
    $runtimeSource,
    'internal static void AfterInventoryChanged\([\s\S]*?' +
        'internal static bool BeforeLocalPlayerDamage').Value
$afterGameSaveSource = [Text.RegularExpressions.Regex]::Match(
    $runtimeSource,
    'internal static void AfterGameSave\([\s\S]*?' +
        'internal static void AfterInventoryChanged').Value
Assert-True (
    $sendClientSaveSource.Contains("preferCompression: true") -and
    -not $sendClientSaveSource.Contains("preferCompression: false") -and
    $sendClientSaveSource.Contains(
        "CharacterEnvelopeKind.InventorySaveRequest") -and
    $sendClientSaveSource.Contains(
        "ClientCharacterSaveReason.InventoryDirty") -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        '_serverReassembler\s*=\s*new BoundedFragmentReassembler\(\s*' +
        '_serverInboundFragmentLimits,\s*allowCompressed:\s*true,')) `
    "Client character saves no longer select the inventory request kind, prefer bounded GZip, or the server inbound reassembler rejects compressed saves."
Assert-True (
    $runtimeSource.Contains(
        "InventoryFastSaveCoalesceTicks =") -and
    $runtimeSource.Contains(
        "Math.Max(1L, Stopwatch.Frequency / 4L)") -and
    $runtimeSource.Contains(
        "InventoryFastSaveMinimumIntervalTicks =") -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'if\s*\(session\.InventoryFastPathReady\s*&&\s*' +
        'session\.InventoryFastSaveDueTimestamp\s*!=\s*0.*?' +
        '!session\.SavePipeline\.HasPendingFullProfile.*?' +
        'CaptureInventoryToBytes\(.*?' +
        'OfferClientSave\(.*?ClientCharacterSaveReason\.InventoryDirty\).*?' +
        'session\.InventoryFastSaveDueTimestamp\s*=\s*0;',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "The 250-ms/one-second inventory fast-path coalescer no longer preserves dirty state behind a pending full profile."
Assert-True (
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'if\s*\(\s*!session\.InventoryFastPathReady\s*&&\s*' +
        'session\.FullProfileSafetySaveDueTimestamp\s*==\s*0\s*\).*?' +
        'AddStopwatchDuration\(\s*now,\s*' +
        'InventoryFastSaveCoalesceTicks\s*\).*?' +
        'session\.FullProfileSafetySaveDueTimestamp\s*=\s*due;',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "An empty first-join profile no longer promotes its first inventory change to a prompt full baseline."
Assert-True (
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'catch\s*\(CharacterProtocolException exception\)\s*' +
        '\{.*?session\.FullProfileSafetySaveDueTimestamp\s*=.*?' +
        'falling back to a full character snapshot',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "A bounded inventory capture failure no longer schedules an immediate full-profile fallback."
Assert-True (
    $runtimeSource.Contains(
        "FullProfileHeartbeatIntervalSeconds = 300") -and
    $runtimeSource.Contains(
        "FullProfileHeartbeatInitialSpreadSeconds = 30") -and
    $runtimeSource.Contains(
        "elapsed / FullProfileHeartbeatIntervalTicks + 1") -and
    $runtimeSource.Contains(
        "NextFullProfileHeartbeatTimestamp") -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'session\.ReadyAcknowledgementSent\s*=\s*true;.*?' +
        'CreateInitialFullProfileHeartbeatDeadline\(',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'bool fullProfileHeartbeatDue\s*=.*?' +
        'NextFullProfileHeartbeatTimestamp.*?' +
        'if\s*\(\(fullProfileFallbackDue\s*\|\|\s*' +
        'fullProfileHeartbeatDue\).*?' +
        'ClientCharacterSaveReason\.PeriodicFull.*?' +
        'if\s*\(fullProfileHeartbeatDue\).*?' +
        'AdvanceFullProfileHeartbeatDeadline\(',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    -not $runtimeSource.Contains(
        "InventoryFullSnapshotIntervalSeconds")) `
    "The fixed five-minute full-profile heartbeat is missing or coupled to player-log configuration."
Assert-True (
    -not $afterInventoryChangedSource.Contains(
        "NextFullProfileHeartbeatTimestamp") -and
    -not $afterGameSaveSource.Contains(
        "NextFullProfileHeartbeatTimestamp")) `
    "An inventory change or vanilla full save can postpone the independent full-profile heartbeat."
Assert-True (
    -not $characterModelsSource.Contains("CharacterFirstJoinPolicy") -and
    -not $pluginSource.Contains("FirstJoinCharacters") -and
    -not $pluginSource.Contains('"First Join Policy"') -and
    -not $snapshotServiceSource.Contains("CharacterFirstJoinPolicy") -and
    -not $snapshotServiceSource.Contains("case CharacterFirstJoinPolicy.Reject") -and
    -not $runtimeSource.Contains("FirstJoinCharacters")) `
    "The removed first-join policy or Reject branch returned to source."
Assert-True (
    $coordinatorSource.Contains(
        "internal bool ServerCharactersEnabled;") -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $coordinatorSource,
        'session\.ServerCharactersEnabled\s*=\s*' +
        'challengeOptions\.ServerCharactersEnabled\s*;') -and
    $runtimeSource.Contains(
        "authenticated.Session.ServerCharactersEnabled") -and
    $runtimeSource.Contains(
        "result.Session.ServerCharactersEnabled")) `
    "Server-character enablement is no longer pinned from the challenge into both authenticated callsites."
Assert-True (
    $snapshotServiceSource.Contains(
        "_repository.PrepareInitialSnapshot(") -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $snapshotServiceSource,
        '_profileCodec\.CreateInitialProfileBytes\(\s*' +
        'identity\.CharacterName,\s*Volatile\.Read\(ref _serverSettings\)\s*\)') -and
    $characterRepositorySource.Contains(
        "persistIfMissing: false") -and
    $characterRepositorySource.Contains(
        "FinalizePreparedInitialSnapshot(") -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $characterRepositorySource,
        'CharacterEnvelope\.CreateWithOrigin\(\s*' +
        'CharacterEnvelopeKind\.Snapshot,\s*1,\s*0,',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    -not $snapshotServiceSource.Contains(
        "No authoritative server character exists and first-join")) `
    "A missing authoritative snapshot no longer uses a staged clean profile."
Assert-True (
    [Text.RegularExpressions.Regex]::IsMatch(
        $snapshotServiceSource,
        'pendingInitialCommit\s*\?\s*' +
        'storedEnvelope\.BaseRevision\s*:\s*' +
        'storedEnvelope\.Revision')) `
    "The load response no longer distinguishes a fresh staged first join from an already registered clean revision-1 profile."

function Find-GameMethod {
    param(
        [string]$TypeName,
        [string]$MethodName,
        [string]$ReturnType,
        [string[]]$ParameterTypes
    )

    $typeDefinition = $gameDefinition.MainModule.Types |
        Where-Object FullName -eq $TypeName
    Assert-True ($null -ne $typeDefinition) `
        "The installed Valheim type $TypeName was not found."

    return $typeDefinition.Methods |
        Where-Object {
            if ($_.Name -ne $MethodName -or
                $_.ReturnType.FullName -ne $ReturnType -or
                $_.Parameters.Count -ne $ParameterTypes.Count) {
                return $false
            }

            for ($index = 0; $index -lt $ParameterTypes.Count; ++$index) {
                if ($_.Parameters[$index].ParameterType.FullName -ne
                    $ParameterTypes[$index]) {
                    return $false
                }
            }

            return $true
        } |
        Select-Object -First 1
}

$continueLogoutDefinition = Find-GameMethod `
    "Game" `
    "ContinueLogout" `
    "System.Void" `
    @("System.Boolean", "System.Boolean", "System.Boolean")
Assert-True ($null -ne $continueLogoutDefinition) `
    "Game.ContinueLogout(bool,bool,bool) changed."

$saveProfileDefinition = Find-GameMethod `
    "Game" `
    "SavePlayerProfile" `
    "System.Void" `
    @("System.Boolean")
Assert-True ($null -ne $saveProfileDefinition) `
    "Game.SavePlayerProfile(bool) changed."

$savePlayerDataDefinition = Find-GameMethod `
    "PlayerProfile" `
    "SavePlayerData" `
    "System.Void" `
    @("Player")
Assert-True ($null -ne $savePlayerDataDefinition) `
    "PlayerProfile.SavePlayerData(Player) changed."

$saveLogoutPointDefinition = Find-GameMethod `
    "PlayerProfile" `
    "SaveLogoutPoint" `
    "System.Void" `
    @()
Assert-True ($null -ne $saveLogoutPointDefinition) `
    "PlayerProfile.SaveLogoutPoint() changed."

$inventoryHideDefinition = Find-GameMethod `
    "InventoryGui" `
    "Hide" `
    "System.Void" `
    @()
Assert-True ($null -ne $inventoryHideDefinition) `
    "InventoryGui.Hide() changed."

$storeHideDefinition = Find-GameMethod `
    "StoreGui" `
    "Hide" `
    "System.Void" `
    @()
Assert-True ($null -ne $storeHideDefinition) `
    "StoreGui.Hide() changed."

$flushClientObjectsDefinition = Find-GameMethod `
    "ZDOMan" `
    "FlushClientObjects" `
    "System.Void" `
    @()
Assert-True ($null -ne $flushClientObjectsDefinition) `
    "ZDOMan.FlushClientObjects() changed."

$sendDestroyedDefinition = Find-GameMethod `
    "ZDOMan" `
    "SendDestroyed" `
    "System.Void" `
    @()
Assert-True ($null -ne $sendDestroyedDefinition) `
    "ZDOMan.SendDestroyed() changed."

$sendZdoDefinition = Find-GameMethod `
    "ZDOMan" `
    "SendZDOToPeers2" `
    "System.Void" `
    @("System.Single")
Assert-True ($null -ne $sendZdoDefinition) `
    "ZDOMan.SendZDOToPeers2(float) changed."

$syncTransformDefinition = Find-GameMethod `
    "ZSyncTransform" `
    "SyncNow" `
    "System.Void" `
    @()
Assert-True ($null -ne $syncTransformDefinition) `
    "ZSyncTransform.SyncNow() changed."

$inventoryChangedDefinition = Find-GameMethod `
    "Inventory" `
    "Changed" `
    "System.Void" `
    @()
Assert-True ($null -ne $inventoryChangedDefinition) `
    "Inventory.Changed() changed."

$playerLoadDefinition = Find-GameMethod `
    "Player" `
    "Load" `
    "System.Void" `
    @("ZPackage")
Assert-True ($null -ne $playerLoadDefinition) `
    "Player.Load(ZPackage) changed."

$checkDeathDefinition = Find-GameMethod `
    "Character" `
    "CheckDeath" `
    "System.Void" `
    @()
Assert-True ($null -ne $checkDeathDefinition) `
    "Character.CheckDeath() changed."

$firstJoinPolicyDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.CharacterFirstJoinPolicy" |
    Select-Object -First 1
$pluginDefinitionType = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ServerManagerPlugin" |
    Select-Object -First 1
Assert-True ($null -eq $firstJoinPolicyDefinition) `
    "The removed CharacterFirstJoinPolicy enum returned to the assembly."
Assert-True (
    $null -ne $pluginDefinitionType -and
    @($pluginDefinitionType.Fields |
        Where-Object Name -eq "FirstJoinCharacters").Count -eq 0) `
    "The removed First Join Policy config field returned to the assembly."

$connectionSnapshotDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ConnectionSessionSnapshot" |
    Select-Object -First 1
$serverCharactersEnabledProperty =
    $connectionSnapshotDefinition.Properties |
        Where-Object Name -eq "ServerCharactersEnabled" |
        Select-Object -First 1
$dispatchChallenge = Get-PluginMethodDefinition `
    "ServerManager.ConnectionSessionCoordinator" `
    "DispatchChallenge"
$readChallengeServerCharacters = Get-CecilCall `
    $dispatchChallenge `
    "ServerManager.ProtocolChallengeOptions" `
    "get_ServerCharactersEnabled"
$pinServerCharacters = $dispatchChallenge.Body.Instructions |
    Where-Object {
        $_.OpCode.Code.ToString() -eq "Stfld" -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ConnectionSessionCoordinator/Session" -and
        $_.Operand.Name -eq "ServerCharactersEnabled"
    } |
    Select-Object -First 1
$snapshotCoordinatorSession = Get-PluginMethodDefinition `
    "ServerManager.ConnectionSessionCoordinator" `
    "Snapshot"
$loadPinnedServerCharacters = $snapshotCoordinatorSession.Body.Instructions |
    Where-Object {
        $_.OpCode.Code.ToString() -eq "Ldfld" -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ConnectionSessionCoordinator/Session" -and
        $_.Operand.Name -eq "ServerCharactersEnabled"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $connectionSnapshotDefinition -and
    $null -ne $serverCharactersEnabledProperty -and
    $serverCharactersEnabledProperty.PropertyType.FullName -eq
        "System.Boolean" -and
    $null -ne $readChallengeServerCharacters -and
    $null -ne $pinServerCharacters -and
    $readChallengeServerCharacters.Offset -lt $pinServerCharacters.Offset -and
    $null -ne $loadPinnedServerCharacters) `
    "The challenge no longer pins server-character enablement into immutable session snapshots."

$openServerCharacterSession = Get-PluginMethodDefinition `
    "ServerManager.CharacterSnapshotService" `
    "OpenOrCreateServerSession"
$openServerCharacterSessionCore = Get-PluginMethodDefinition `
    "ServerManager.CharacterSnapshotService" `
    "OpenOrCreateSessionCore"
$openServerCharacterSessionCoreCall = Get-CecilCall `
    $openServerCharacterSession `
    "ServerManager.CharacterSnapshotService" `
    "OpenOrCreateSessionCore"
$loadOrCreateServerCharacter = Get-CecilCall `
    $openServerCharacterSessionCore `
    "ServerManager.CharacterRepository" `
    "PrepareInitialSnapshot"
$loadServerCharacterCalls = @(
    $openServerCharacterSessionCore.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.CharacterRepository" -and
            $_.Operand.Name -eq "Load"
        })
$openCharacterSessionConstructor = Get-CecilCall `
    $openServerCharacterSessionCore `
    "ServerManager.CharacterSession" `
    ".ctor"
$characterSessionDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.CharacterSession" |
    Select-Object -First 1
$openResultDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.CharacterSessionOpenResult" |
    Select-Object -First 1
$sessionPendingProperty = $characterSessionDefinition.Properties |
    Where-Object Name -eq "PendingInitialCommit" |
    Select-Object -First 1
$openResultPendingProperty = $openResultDefinition.Properties |
    Where-Object Name -eq "PendingInitialCommit" |
    Select-Object -First 1
Assert-True (
    $openServerCharacterSession.Parameters.Count -eq 1 -and
    $openServerCharacterSession.Parameters[0].ParameterType.FullName -eq
        "ZNetPeer" -and
    $null -ne $openServerCharacterSessionCoreCall -and
    $null -ne $loadOrCreateServerCharacter -and
    $loadServerCharacterCalls.Count -eq 0 -and
    $null -ne $openCharacterSessionConstructor -and
    @($openCharacterSessionConstructor.Operand.Parameters | Where-Object {
        $_.Name -eq "pendingInitialEnvelope" -and
        $_.ParameterType.FullName -eq "ServerManager.CharacterEnvelope"
    }).Count -eq 1 -and
    $null -ne $sessionPendingProperty -and
    $sessionPendingProperty.PropertyType.FullName -eq "System.Boolean" -and
    $null -ne $openResultPendingProperty -and
    $openResultPendingProperty.PropertyType.FullName -eq "System.Boolean") `
    "Missing snapshots no longer follow the staged clean revision-1 path."

$prepareInitialSnapshot = Get-PluginMethodDefinition `
    "ServerManager.CharacterRepository" `
    "PrepareInitialSnapshot"
$prepareInitialCore = Get-CecilCall `
    $prepareInitialSnapshot `
    "ServerManager.CharacterRepository" `
    "LoadOrPrepareInitialCore"
$prepareWriteCalls = @(
    $prepareInitialSnapshot.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.CharacterRepository" -and
            ($_.Operand.Name -eq "WriteInitialAtomically" -or
             $_.Operand.Name -eq "FinalizePreparedInitialSnapshot")
        })
$finalizeInitialSnapshot = Get-PluginMethodDefinition `
    "ServerManager.CharacterRepository" `
    "FinalizePreparedInitialSnapshot" 4
$finalizePrimaryCheck = Get-CecilCall `
    $finalizeInitialSnapshot `
    "System.IO.File" `
    "Exists"
$finalizeBackupChecks = @(
    $finalizeInitialSnapshot.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.CharacterRepository" -and
            $_.Operand.Name -eq "HasBackup"
        })
$finalizeQuotaCheck = Get-CecilCall `
    $finalizeInitialSnapshot `
    "ServerManager.CharacterRepository" `
    "CountProfilesForAccount"
$finalizeInitialWrite = Get-CecilCall `
    $finalizeInitialSnapshot `
    "ServerManager.CharacterRepository" `
    "WriteInitialAtomically"
Assert-True (
    $null -ne $prepareInitialCore -and
    $prepareWriteCalls.Count -eq 0 -and
    $null -ne $finalizePrimaryCheck -and
    $finalizeBackupChecks.Count -ge 2 -and
    $null -ne $finalizeQuotaCheck -and
    $null -ne $finalizeInitialWrite -and
    $finalizePrimaryCheck.Offset -lt $finalizeInitialWrite.Offset -and
    $finalizeBackupChecks[0].Offset -lt $finalizeInitialWrite.Offset -and
    $finalizeBackupChecks[-1].Offset -lt $finalizeInitialWrite.Offset -and
    $finalizeQuotaCheck.Offset -lt $finalizeInitialWrite.Offset) `
    "Prepared first joins can persist before ACK or skip final primary/backup/quota rechecks."

$finalizePendingInitial = Get-PluginMethodDefinition `
    "ServerManager.CharacterSnapshotService" `
    "FinalizePendingInitialSnapshot"
$finalizePendingInitialCore = Get-PluginMethodDefinition `
    "ServerManager.CharacterSnapshotService" `
    "FinalizePendingInitialSnapshotCore"
$finalizePendingInitialCoreCall = Get-CecilCall `
    $finalizePendingInitial `
    "ServerManager.CharacterSnapshotService" `
    "FinalizePendingInitialSnapshotCore"
$getPendingInitial = Get-CecilCall `
    $finalizePendingInitialCore `
    "ServerManager.CharacterSession" `
    "GetPendingInitialEnvelope"
$commitPendingInitial = Get-CecilCall `
    $finalizePendingInitialCore `
    "ServerManager.CharacterRepository" `
    "FinalizePreparedInitialSnapshot"
$completePendingInitial = Get-CecilCall `
    $finalizePendingInitialCore `
    "ServerManager.CharacterSession" `
    "CompleteInitialCommit"
Assert-True (
    $finalizePendingInitial.ReturnType.FullName -eq "System.Boolean" -and
    $null -ne $finalizePendingInitialCoreCall -and
    $null -ne $getPendingInitial -and
    $null -ne $commitPendingInitial -and
    $null -ne $completePendingInitial -and
    $getPendingInitial.Offset -lt $commitPendingInitial.Offset -and
    $commitPendingInitial.Offset -lt $completePendingInitial.Offset) `
    "Existing snapshots no longer no-op or pending snapshots clear before durable creation."

$handleServerPacket = Get-PluginMethodDefinition `
    "ServerManager.ServerManagerRuntime" `
    "HandleServerPacket"
$acceptServerReady = Get-CecilCall `
    $handleServerPacket `
    "ServerManager.ConnectionSessionCoordinator" `
    "AcceptReadyAck"
$readyPinnedServerCharacters = Get-CecilCall `
    $handleServerPacket `
    "ServerManager.ConnectionSessionSnapshot" `
    "get_ServerCharactersEnabled"
$readyLiveServerCharacters = @(
    $handleServerPacket.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerPlugin" -and
            $_.Operand.Name -eq "get_EnableServerCharacters"
        })
$finalizeServerFirstJoin = Get-CecilCall `
    $handleServerPacket `
    "ServerManager.CharacterSnapshotService" `
    "FinalizePendingInitialSnapshot"
$releaseServerWorld = Get-CecilCall `
    $handleServerPacket `
    "ServerManager.ServerManagerRuntime" `
    "ReleaseWorld"
$serverFirstJoinReject = $handleServerPacket.Body.Instructions |
    Where-Object {
        $null -ne $finalizeServerFirstJoin -and
        $null -ne $releaseServerWorld -and
        $_.Offset -gt $finalizeServerFirstJoin.Offset -and
        $_.Offset -lt $releaseServerWorld.Offset -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $_.Operand.Name -eq "SendServerRejection"
    } |
    Select-Object -First 1
$finalizeFailureHandler = $handleServerPacket.Body.ExceptionHandlers |
    Where-Object {
        (Test-CecilInstructionInRange `
            $finalizeServerFirstJoin `
            $_.TryStart `
            $_.TryEnd) -and
        (Test-CecilInstructionInRange `
            $serverFirstJoinReject `
            $_.HandlerStart `
            $_.HandlerEnd)
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $acceptServerReady -and
    $null -ne $readyPinnedServerCharacters -and
    $readyLiveServerCharacters.Count -eq 0 -and
    $null -ne $finalizeServerFirstJoin -and
    $null -ne $releaseServerWorld -and
    $null -ne $serverFirstJoinReject -and
    $null -ne $finalizeFailureHandler -and
    $acceptServerReady.Offset -lt $finalizeServerFirstJoin.Offset -and
    $acceptServerReady.Offset -lt $readyPinnedServerCharacters.Offset -and
    $readyPinnedServerCharacters.Offset -lt
        $finalizeServerFirstJoin.Offset -and
    $finalizeServerFirstJoin.Offset -lt $releaseServerWorld.Offset -and
    $serverFirstJoinReject.Offset -lt $releaseServerWorld.Offset -and
    -not (Test-CecilReachable `
        $finalizeFailureHandler.HandlerStart `
        $releaseServerWorld)) `
    "ReadyAck can release the world before initial persistence, or persistence failure can still release it."

$completeAuthenticatedPeer = Get-PluginMethodDefinition `
    "ServerManager.ServerManagerRuntime" `
    "CompleteAuthenticatedPeer"
$confirmPeerAuthentication = Get-CecilCall `
    $completeAuthenticatedPeer `
    "ServerManager.ConnectionSessionCoordinator" `
    "ConfirmPeerInfoAuthenticated"
$authenticatedPinnedServerCharacters = Get-CecilCall `
    $completeAuthenticatedPeer `
    "ServerManager.ConnectionSessionSnapshot" `
    "get_ServerCharactersEnabled"
$authenticatedLiveServerCharacters = @(
    $completeAuthenticatedPeer.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerPlugin" -and
            $_.Operand.Name -eq "get_EnableServerCharacters"
        })
$openPinnedServerCharacter = Get-CecilCall `
    $completeAuthenticatedPeer `
    "ServerManager.CharacterSnapshotService" `
    "OpenOrCreateServerSession"
Assert-True (
    $null -ne $confirmPeerAuthentication -and
    $null -ne $authenticatedPinnedServerCharacters -and
    $authenticatedLiveServerCharacters.Count -eq 0 -and
    $null -ne $openPinnedServerCharacter -and
    $confirmPeerAuthentication.Offset -lt
        $authenticatedPinnedServerCharacters.Offset -and
    $authenticatedPinnedServerCharacters.Offset -lt
        $openPinnedServerCharacter.Offset) `
    "Authenticated character setup reads live config instead of its challenge-pinned session flag."

$serverSaveHandler = Get-PluginMethodDefinition `
    "ServerManager.ServerManagerRuntime" `
    "HandleServerCharacterFragment"
$terminateRejected = Get-CecilCall `
    $serverSaveHandler `
    "ServerManager.ServerManagerRuntime" `
    "TerminateRejectedCharacterSave"
$directSchedule = Get-CecilCall `
    $serverSaveHandler `
    "ServerManager.ServerManagerRuntime" `
    "ScheduleDisconnect"
$acceptedGetter = $serverSaveHandler.Body.Instructions |
    Where-Object {
        $null -ne $terminateRejected -and
        $_.Offset -lt $terminateRejected.Offset -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.CharacterSaveResult" -and
        $_.Operand.Name -eq "get_Accepted"
    } |
    Sort-Object Offset -Descending |
    Select-Object -First 1
Assert-True ($null -ne $terminateRejected) `
    "A rejected character save no longer enters terminal session teardown."
Assert-True ($null -eq $directSchedule) `
    "The save handler bypasses terminal teardown with a direct disconnect."
Assert-True ($null -ne $acceptedGetter) `
    "The save handler no longer checks CharacterSaveResult.Accepted."

$acceptedBranch = $acceptedGetter.Next
while ($null -ne $acceptedBranch -and
    $acceptedBranch.Offset -lt $terminateRejected.Offset -and
    $acceptedBranch.OpCode.FlowControl.ToString() -ne "Cond_Branch") {
    $acceptedBranch = $acceptedBranch.Next
}
Assert-True ($null -ne $acceptedBranch -and
    $acceptedBranch.OpCode.FlowControl.ToString() -eq "Cond_Branch") `
    "The Accepted check no longer controls rejected-save teardown."
$reachingTerminate = @(
    @(Get-CecilSuccessors $acceptedBranch) |
        Where-Object { Test-CecilReachable $_ $terminateRejected })
Assert-True ($reachingTerminate.Count -eq 1) `
    "Rejected-save teardown is not confined to exactly one Accepted branch."

$terminalHelper = Get-PluginMethodDefinition `
    "ServerManager.ServerManagerRuntime" `
    "TerminateRejectedCharacterSave"
$rejectSession = Get-CecilCall `
    $terminalHelper `
    "ServerManager.ConnectionSessionCoordinator" `
    "RejectSession"
$removeFragments = Get-CecilCall `
    $terminalHelper `
    "ServerManager.BoundedFragmentReassembler" `
    "RemovePeer"
$closeCharacterSession = Get-CecilCall `
    $terminalHelper `
    "ServerManager.CharacterSnapshotService" `
    "CloseServerSession"
$discardBuffer = Get-CecilCall `
    $terminalHelper `
    "ServerManager.BufferedWorldSocket" `
    "Discard"
$scheduleTerminalDisconnect = Get-CecilCall `
    $terminalHelper `
    "ServerManager.ServerManagerRuntime" `
    "ScheduleDisconnect"
Assert-True (
    $null -ne $rejectSession -and
    $null -ne $removeFragments -and
    $null -ne $closeCharacterSession -and
    $null -ne $discardBuffer -and
    $null -ne $scheduleTerminalDisconnect) `
    "Rejected-save teardown is missing a required cleanup stage."
Assert-True (
    $rejectSession.Offset -lt $removeFragments.Offset -and
    $removeFragments.Offset -lt $closeCharacterSession.Offset -and
    $closeCharacterSession.Offset -lt $discardBuffer.Offset -and
    $discardBuffer.Offset -lt $scheduleTerminalDisconnect.Offset) `
    "Rejected-save teardown no longer closes admission state before disconnect."

$serverHandleSaveCall = Get-CecilCall `
    $serverSaveHandler `
    "ServerManager.CharacterSnapshotService" `
    "HandleSaveRequest"
$serverSendAcknowledgement = Get-CecilCall `
    $serverSaveHandler `
    "ServerManager.ServerManagerRuntime" `
    "SendCharacterPayload"
$shadowStartedText = Get-CecilStringInstruction `
    $serverSaveHandler `
    "CharacterShadowValidationStarted identity="
$shadowAcceptedText = Get-CecilStringInstruction `
    $serverSaveHandler `
    "CharacterShadowAccepted identity="
$shadowAcknowledgedText = Get-CecilStringInstruction `
    $serverSaveHandler `
    "CharacterShadowAckSent identity="
$shadowRejectedText = Get-CecilStringInstruction `
    $serverSaveHandler `
    "CharacterShadowRejected identity="
$acknowledgementFailureText = Get-CecilStringInstruction `
    $serverSaveHandler `
    "Character RAM shadow was accepted, but acknowledgement"
$acknowledgementFailureLog = $serverSaveHandler.Body.Instructions |
    Where-Object {
        $null -ne $acknowledgementFailureText -and
        $_.Offset -gt $acknowledgementFailureText.Offset -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "LogWarning"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $serverHandleSaveCall -and
    $null -ne $serverSendAcknowledgement -and
    $null -ne $shadowStartedText -and
    $null -ne $shadowAcceptedText -and
    $null -ne $shadowAcknowledgedText -and
    $null -ne $shadowRejectedText -and
    $null -ne $acknowledgementFailureText -and
    $null -ne $acknowledgementFailureLog) `
    "Character RAM-shadow acceptance lifecycle logging is incomplete."
Assert-True (
    $shadowStartedText.Offset -lt $serverHandleSaveCall.Offset -and
    $serverHandleSaveCall.Offset -lt $shadowAcceptedText.Offset -and
    $shadowAcceptedText.Offset -lt $serverSendAcknowledgement.Offset -and
    $serverSendAcknowledgement.Offset -lt $shadowAcknowledgedText.Offset) `
    "Shadow acceptance or acknowledgement logging moved outside its ordering."
Assert-True (
    $serverHandleSaveCall.Offset -lt $shadowRejectedText.Offset -and
    $shadowRejectedText.Offset -lt $terminateRejected.Offset) `
    "Rejected shadow logging no longer precedes terminal teardown."
Assert-True (
    $serverSendAcknowledgement.Offset -lt $acknowledgementFailureText.Offset -and
    $acknowledgementFailureText.Offset -lt $acknowledgementFailureLog.Offset) `
    "Post-acceptance acknowledgement failure logging is no longer retained."

$handleSaveRequest = Get-PluginMethodDefinition `
    "ServerManager.CharacterSnapshotService" `
    "HandleSaveRequestCore"
$encodeAccepted = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.CharacterEnvelopeCodec" `
    "ToZPackage"
$evaluateLiveCandidate = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.CharacterRepository" `
    "EvaluateLiveCandidate"
$materializeInventoryRequest = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.ValheimPlayerProfileCodec" `
    "ReplaceInventorySnapshot"
$materializedAdmissionCreate = $handleSaveRequest.Body.Instructions |
    Where-Object {
        $null -ne $materializeInventoryRequest -and
        $null -ne $evaluateLiveCandidate -and
        $_.Offset -gt $materializeInventoryRequest.Offset -and
        $_.Offset -lt $evaluateLiveCandidate.Offset -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.CharacterEnvelope" -and
        $_.Operand.Name -eq "Create"
    } |
    Select-Object -First 1
$repositoryCommitCalls = @(
    $handleSaveRequest.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.CharacterRepository" -and
            $_.Operand.Name -eq "Commit"
        })
$setLiveSnapshot = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.CharacterSnapshotService" `
    "SetLiveSnapshotLocked"
$tryCommitRevision = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.CharacterSession" `
    "TryCommitRevision"
$markClosed = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.CharacterSession" `
    "MarkClosed"
$recordSaveRequest = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.CharacterSession" `
    "RecordSaveRequest"
$beginSaveAttempt = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.CharacterSession" `
    "BeginSaveAttempt"
$recordSuccessfulAcceptance = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.CharacterSession" `
    "RecordSuccessfulAcceptance"
$completeSaveSuccess = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.CharacterSession" `
    "CompleteSaveSuccess"
$completeSaveFailure = Get-CecilCall `
    $handleSaveRequest `
    "ServerManager.CharacterSession" `
    "CompleteSaveFailure"
$storageException = $handleSaveRequest.Body.Instructions |
    Where-Object {
        $null -ne $markClosed -and
        $_.Offset -gt $markClosed.Offset -and
        $_.OpCode.Code.ToString() -eq "Newobj" -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.CharacterStorageException"
    } |
    Select-Object -First 1
$storageThrow = $handleSaveRequest.Body.Instructions |
    Where-Object {
        $null -ne $storageException -and
        $_.Offset -gt $storageException.Offset -and
        $_.OpCode.FlowControl.ToString() -eq "Throw"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $encodeAccepted -and
    $null -ne $evaluateLiveCandidate -and
    $repositoryCommitCalls.Count -eq 0 -and
    $null -ne $setLiveSnapshot -and
    $encodeAccepted.Offset -lt $evaluateLiveCandidate.Offset) `
    "Save admission no longer prepares its ACK before RAM-only evaluation, or writes disk directly."
Assert-True (
    $null -ne $materializeInventoryRequest -and
    $null -ne $materializedAdmissionCreate -and
    $materializeInventoryRequest.Offset -lt
        $materializedAdmissionCreate.Offset -and
    $materializedAdmissionCreate.Offset -lt
        $evaluateLiveCandidate.Offset -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $snapshotServiceSource,
        'if\s*\(request\.Kind\s*==\s*' +
        'CharacterEnvelopeKind\.InventorySaveRequest\).*?' +
        'ReplaceInventorySnapshot\(\s*session\.Identity,\s*' +
        'liveBase\.LatestEnvelope\.PayloadUnsafe,\s*' +
        'request\.PayloadUnsafe,\s*out validatedRequest\).*?' +
        'CharacterEnvelope\.Create\(\s*' +
        'CharacterEnvelopeKind\.SaveRequest,\s*' +
        'request\.Revision,\s*request\.BaseRevision,',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "The server no longer materializes kind 5 over the current full RAM shadow at the same unified revision before admission."
Assert-True (
    $null -ne $tryCommitRevision -and
    $null -ne $markClosed -and
    $null -ne $storageException -and
    $null -ne $storageThrow) `
    "The post-admission revision failure path is incomplete."

$postCommitCatch = @(
    $handleSaveRequest.Body.ExceptionHandlers |
        Where-Object {
            $_.HandlerType.ToString() -eq "Catch" -and
            (Test-CecilInstructionInRange `
                $tryCommitRevision `
                $_.TryStart `
                $_.TryEnd)
        })
Assert-True ($postCommitCatch.Count -eq 0) `
    "The post-admission revision path is again converted into SaveRejected."

Assert-True (
    [Text.RegularExpressions.Regex]::IsMatch(
        $snapshotServiceSource,
        'if\s*\(!session\.TryCommitRevision\(.*?\)\)\s*' +
        '\{.*?session\.MarkClosed\(\);.*?' +
        'throw new CharacterStorageException\(terminalError\);\s*\}',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    $markClosed.Offset -lt $storageException.Offset -and
    $storageException.Offset -lt $storageThrow.Offset -and
    (Test-CecilReachable $markClosed $storageThrow)) `
    "A failed revision advance no longer closes the session and throws."

$saveTelemetryFinally = @(
    $handleSaveRequest.Body.ExceptionHandlers |
        Where-Object {
            $_.HandlerType.ToString() -eq "Finally" -and
            (Test-CecilInstructionInRange `
                $completeSaveFailure `
                $_.HandlerStart `
                $_.HandlerEnd)
        })
Assert-True (
    $null -ne $recordSaveRequest -and
    $null -ne $beginSaveAttempt -and
    $null -ne $recordSuccessfulAcceptance -and
    $null -ne $completeSaveSuccess -and
    $null -ne $completeSaveFailure -and
    $recordSaveRequest.Offset -lt $beginSaveAttempt.Offset -and
    $beginSaveAttempt.Offset -lt $evaluateLiveCandidate.Offset -and
    $evaluateLiveCandidate.Offset -lt $tryCommitRevision.Offset -and
    $tryCommitRevision.Offset -lt $setLiveSnapshot.Offset -and
    $setLiveSnapshot.Offset -lt $recordSuccessfulAcceptance.Offset -and
    $recordSuccessfulAcceptance.Offset -lt $completeSaveSuccess.Offset -and
    $saveTelemetryFinally.Count -eq 1) `
    "Character save-health telemetry no longer brackets each live-shadow admission attempt."

$runtimeTick = Get-PluginMethodDefinition `
    "ServerManager.ServerManagerRuntime" `
    "Tick"
$monitorSaveHealthCall = Get-CecilCall `
    $runtimeTick `
    "ServerManager.ServerManagerRuntime" `
    "MonitorServerCharacterSaveHealth"
$monitorSaveHealth = Get-PluginMethodDefinition `
    "ServerManager.ServerManagerRuntime" `
    "MonitorServerCharacterSaveHealth"
$healthThresholdConfig = Get-CecilCall `
    $monitorSaveHealth `
    "ServerManager.ServerManagerPlugin" `
    "get_CharacterShadowHealthWarningMinutes"
$healthThresholdFieldRead = $monitorSaveHealth.Body.Instructions |
    Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldsfld -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.Name -eq "CharacterShadowHealthWarningThreshold"
    } |
    Select-Object -First 1
$runtimeStaticInitializer = Get-PluginMethodDefinition `
    "ServerManager.ServerManagerRuntime" ".cctor"
$healthThresholdConversion = Get-CecilCall `
    $runtimeStaticInitializer `
    "System.TimeSpan" `
    "FromMinutes"
$healthWarningAudit = Get-CecilCall `
    $monitorSaveHealth `
    "ServerManager.Events.ServerEventRuntime" `
    "RecordCharacterShadowWarning"
$healthConsoleWarning = Get-CecilCall `
    $monitorSaveHealth `
    "BepInEx.Logging.ManualLogSource" `
    "LogWarning"
$claimLongUnsavedWarnings = Get-CecilCall `
    $monitorSaveHealth `
    "ServerManager.CharacterSnapshotService" `
    "ClaimLongUnsavedWarnings"
Assert-True ($null -ne $monitorSaveHealthCall) `
    "Runtime Tick no longer monitors server-character save health."
Assert-True (
    $null -eq $healthThresholdConfig -and
    $null -ne $healthThresholdFieldRead -and
    $null -ne $healthThresholdConversion -and
    $healthThresholdConversion.Previous.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldc_R8 -and
    [double]$healthThresholdConversion.Previous.Operand -eq 45 -and
    $null -ne $claimLongUnsavedWarnings -and
    $null -ne $healthWarningAudit -and
    $null -eq $healthConsoleWarning -and
    $healthThresholdFieldRead.Offset -lt $claimLongUnsavedWarnings.Offset -and
    $claimLongUnsavedWarnings.Offset -lt $healthWarningAudit.Offset -and
    $null -ne (Get-CecilStringInstruction $monitorSaveHealth "incoming")) `
    "Shadow-health monitoring must claim fixed 45-minute warnings and route them to audit, not only the console."
Assert-True ([Text.RegularExpressions.Regex]::IsMatch(
    $runtimeSource,
    'private static readonly TimeSpan CharacterShadowHealthWarningThreshold\s*=\s*' +
    'TimeSpan\.FromMinutes\(45\);')) `
    "The observational shadow-health threshold is no longer fixed at 45 minutes."

$processSavePipeline = Get-PluginMethodDefinition `
    "ServerManager.ServerManagerRuntime" `
    "ProcessClientCharacterSavePipeline"
$processSavePipelineCalls = @(
    $processSavePipeline.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$acknowledgementOverdueCall = $processSavePipelineCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ClientCharacterSavePipeline" -and
        $_.Operand.Name -eq "IsAcknowledgementOverdue"
    } |
    Select-Object -First 1
$pipelineCloseCalls = @(
    $processSavePipelineCalls |
        Where-Object {
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ClientCharacterSavePipeline" -and
            $_.Operand.Name -eq "Close"
        })
$timeoutPipelineClose = $pipelineCloseCalls |
    Where-Object {
        $null -ne $acknowledgementOverdueCall -and
        $_.Offset -gt $acknowledgementOverdueCall.Offset
    } |
    Select-Object -First 1
$timeoutResumeExit = $processSavePipelineCalls |
    Where-Object {
        $null -ne $timeoutPipelineClose -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $_.Operand.Name -eq "ResumeDeferredClientExit" -and
        $_.Offset -gt $timeoutPipelineClose.Offset
    } |
    Select-Object -First 1
$timeoutFailClient = $processSavePipelineCalls |
    Where-Object {
        $null -ne $timeoutPipelineClose -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $_.Operand.Name -eq "FailClient" -and
        $_.Offset -gt $timeoutPipelineClose.Offset
    } |
    Select-Object -First 1
$overdueBranch = $acknowledgementOverdueCall.Next
while ($null -ne $overdueBranch -and
    $null -ne $timeoutPipelineClose -and
    $overdueBranch.Offset -lt $timeoutPipelineClose.Offset -and
    $overdueBranch.OpCode.FlowControl.ToString() -ne "Cond_Branch") {
    $overdueBranch = $overdueBranch.Next
}
$timeoutCloseBranches = @(
    @(Get-CecilSuccessors $overdueBranch) |
        Where-Object { Test-CecilReachable $_ $timeoutPipelineClose })
Assert-True (
    $null -ne $acknowledgementOverdueCall -and
    $null -ne $timeoutPipelineClose -and
    $null -ne $overdueBranch -and
    $overdueBranch.OpCode.FlowControl.ToString() -eq "Cond_Branch" -and
    $timeoutCloseBranches.Count -eq 1 -and
    $null -ne $timeoutResumeExit -and
    $null -ne $timeoutFailClient -and
    $timeoutPipelineClose.Offset -lt $timeoutResumeExit.Offset -and
    $timeoutPipelineClose.Offset -lt $timeoutFailClient.Offset) `
    "An overdue client save no longer closes before terminal exit handling."

$tryStartSaveCall = $processSavePipelineCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ClientCharacterSavePipeline" -and
        $_.Operand.Name -eq "TryStartNext"
    } |
    Select-Object -Last 1
$sendClientSaveCall = $processSavePipelineCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $_.Operand.Name -eq "SendClientSave"
    } |
    Select-Object -Last 1
$sendFailureHandler = $processSavePipeline.Body.ExceptionHandlers |
    Where-Object {
        Test-CecilInstructionInRange `
            $sendClientSaveCall `
            $_.TryStart `
            $_.TryEnd
    } |
    Select-Object -First 1
$sendFailureCalls = @(
    $processSavePipelineCalls |
        Where-Object {
            $null -ne $sendFailureHandler -and
            (Test-CecilInstructionInRange `
                $_ `
                $sendFailureHandler.HandlerStart `
                $sendFailureHandler.HandlerEnd)
        })
$sendFailureClose = $sendFailureCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ClientCharacterSavePipeline" -and
        $_.Operand.Name -eq "Close"
    } |
    Select-Object -First 1
$sendFailureResume = $sendFailureCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $_.Operand.Name -eq "ResumeDeferredClientExit"
    } |
    Select-Object -First 1
$sendFailureFailClient = $sendFailureCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $_.Operand.Name -eq "FailClient"
    } |
    Select-Object -First 1
$sendRetryCalls = @(
    $sendFailureCalls |
        Where-Object {
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerRuntime" -and
            $_.Operand.Name -eq "SendClientSave"
        })
Assert-True (
    $null -ne $tryStartSaveCall -and
    $null -ne $sendClientSaveCall -and
    $tryStartSaveCall.Offset -lt $sendClientSaveCall.Offset -and
    $null -ne $sendFailureHandler -and
    $null -ne $sendFailureClose -and
    $null -ne $sendFailureResume -and
    $null -ne $sendFailureFailClient -and
    $sendFailureClose.Offset -lt $sendFailureResume.Offset -and
    $sendFailureClose.Offset -lt $sendFailureFailClient.Offset -and
    $sendRetryCalls.Count -eq 0) `
    "An ambiguous client-save send failure can remain in flight or retry in-session."

$applySaveResponse = Get-PluginMethodDefinition `
    "ServerManager.ServerManagerRuntime" `
    "ApplySaveResponse"
$completeAcceptedSave = Get-CecilCall `
    $applySaveResponse `
    "ServerManager.ClientCharacterSavePipeline" `
    "TryCompleteAcknowledgement"
$acceptClientRevision = Get-CecilCall `
    $applySaveResponse `
    "ServerManager.CharacterClientState" `
    "TryAcceptRevision"
$materializeAcceptedInventory = Get-CecilCall `
    $applySaveResponse `
    "ServerManager.ValheimPlayerProfileCodec" `
    "ReplaceInventorySnapshot"
$rejectedSaveText = Get-CecilStringInstruction `
    $applySaveResponse `
    "server rejected the character save"
Assert-True (
    $null -ne $completeAcceptedSave -and
    $completeAcceptedSave.Operand.Parameters.Count -eq 5 -and
    $completeAcceptedSave.Operand.Parameters[0].ParameterType.FullName -eq
        "System.Int64" -and
    $completeAcceptedSave.Operand.Parameters[1].ParameterType.FullName -eq
        "System.Int64" -and
    $completeAcceptedSave.Operand.Parameters[2].ParameterType.FullName -eq
        "System.Byte[]&" -and
    $completeAcceptedSave.Operand.Parameters[3].ParameterType.FullName -eq
        "ServerManager.ClientCharacterSaveReason&" -and
    $completeAcceptedSave.Operand.Parameters[4].ParameterType.FullName -eq
        "System.String&" -and
    $null -ne $materializeAcceptedInventory -and
    $null -ne $acceptClientRevision -and
    $null -ne $completeAcceptedSave -and
    $null -ne $rejectedSaveText -and
    $rejectedSaveText.Offset -lt $completeAcceptedSave.Offset -and
    $completeAcceptedSave.Offset -lt $materializeAcceptedInventory.Offset -and
    $materializeAcceptedInventory.Offset -lt $acceptClientRevision.Offset) `
    "An ACK no longer matches its capture before advancing the materialized RAM base and unified revision."

$applyInitialCharacter = Get-PluginMethodDefinition `
    "ServerManager.ServerManagerRuntime" `
    "ApplyInitialServerCharacter"
$sendInitialReady = Get-CecilCall `
    $applyInitialCharacter `
    "ServerManager.ServerManagerRuntime" `
    "SendProtocolOrThrow"
$rejectUsedLocalFirstJoin = Get-CecilCall `
    $applyInitialCharacter `
    "ServerManager.LocalCharacterFirstJoinGuard" `
    "ShouldRejectUsedLocalFirstJoin"
$setInitialOriginalProfile = Get-CecilCall `
    $applyInitialCharacter `
    "ServerManager.ServerManagerRuntime/ClientConnection" `
    "set_OriginalProfile"
$setInitialManagedProfile = Get-CecilCall `
    $applyInitialCharacter `
    "ServerManager.ServerManagerRuntime/ClientConnection" `
    "set_ManagedProfile"
$setInitialGameProfile = Get-CecilCall `
    $applyInitialCharacter `
    "ServerManager.ValheimPrivateAccess" `
    "SetGamePlayerProfile"
$earlyFirstJoinFailure = $applyInitialCharacter.Body.Instructions |
    Where-Object {
        $null -ne $setInitialOriginalProfile -and
        $_.Offset -lt $setInitialOriginalProfile.Offset -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $_.Operand.Name -eq "FailClient"
    } |
    Select-Object -First 1
$firstJoinBranch = $applyInitialCharacter.Body.Instructions |
    Where-Object {
        $null -ne $rejectUsedLocalFirstJoin -and
        $null -ne $setInitialOriginalProfile -and
        $_.Offset -gt $rejectUsedLocalFirstJoin.Offset -and
        $_.Offset -lt $setInitialOriginalProfile.Offset -and
        $_.OpCode.FlowControl.ToString() -eq "Cond_Branch"
    } |
    Select-Object -First 1
$rejectFirstJoinPath = $null
$allowFirstJoinPath = $null
if ($null -ne $firstJoinBranch) {
    foreach ($successor in @(Get-CecilSuccessors $firstJoinBranch)) {
        if (Test-CecilReachable $successor $earlyFirstJoinFailure) {
            $rejectFirstJoinPath = $successor
        }

        if (Test-CecilReachable $successor $setInitialOriginalProfile) {
            $allowFirstJoinPath = $successor
        }
    }
}
Assert-True (
    $null -ne $rejectUsedLocalFirstJoin -and
    $null -ne $setInitialOriginalProfile -and
    $null -ne $setInitialManagedProfile -and
    $null -ne $setInitialGameProfile -and
    $null -ne $earlyFirstJoinFailure -and
    $null -ne $firstJoinBranch -and
    $null -ne $rejectFirstJoinPath -and
    $null -ne $allowFirstJoinPath -and
    $rejectUsedLocalFirstJoin.Offset -lt $earlyFirstJoinFailure.Offset -and
    $earlyFirstJoinFailure.Offset -lt $setInitialOriginalProfile.Offset -and
    $rejectUsedLocalFirstJoin.Offset -lt $setInitialManagedProfile.Offset -and
    $rejectUsedLocalFirstJoin.Offset -lt $setInitialGameProfile.Offset -and
    $rejectUsedLocalFirstJoin.Offset -lt $sendInitialReady.Offset -and
    -not (Test-CecilReachable `
        $rejectFirstJoinPath `
        $setInitialOriginalProfile) -and
    -not (Test-CecilReachable `
        $rejectFirstJoinPath `
        $setInitialManagedProfile) -and
    -not (Test-CecilReachable `
        $rejectFirstJoinPath `
        $setInitialGameProfile) -and
    -not (Test-CecilReachable $rejectFirstJoinPath $sendInitialReady)) `
    "A used local first join can mutate/swap the profile or send ReadyAck before rejection."

# The live profile remains a normal Local/Cloud character slot. An asynchronous
# server response must never overwrite a newer local save with its older capture.
$decodeManagedProfile = Get-CecilCall $applyInitialCharacter 'ServerManager.ValheimPlayerProfileCodec' 'DeserializeProfileFromBytes'
$selectedFilename = Get-CecilCall $applyInitialCharacter 'PlayerProfile' 'GetFilename'
$selectedFileSource = @($applyInitialCharacter.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.FieldReference] -and
    $_.Operand.DeclaringType.FullName -eq 'PlayerProfile' -and $_.Operand.Name -eq 'm_fileSource'
})
Assert-True ($null -ne $decodeManagedProfile -and $null -ne $selectedFilename -and
    $selectedFileSource.Count -eq 1 -and
    $selectedFilename.Offset -lt $selectedFileSource[0].Offset -and
    $selectedFileSource[0].Next.Offset -eq $decodeManagedProfile.Offset -and
    $decodeManagedProfile.Offset -lt $rejectUsedLocalFirstJoin.Offset
) 'The managed remote profile must preserve the selected filename and Local/Cloud source without saving before admission.'

foreach ($handlerName in @('ApplyInitialServerCharacter', 'ApplySaveResponse', 'HandleClientBackupCaptureCommitted')) {
    $handler = Get-PluginMethodDefinition 'ServerManager.ServerManagerRuntime' $handlerName
    $diskWrites = @($handler.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        ($_.Operand.Name -in @('TryMirrorAcceptedCharacter', 'WriteAcceptedSnapshot', 'SavePlayerToDisk') -or
         ($_.Operand.DeclaringType.FullName -eq 'PlayerProfile' -and $_.Operand.Name -eq 'Save'))
    })
    Assert-True ($diskWrites.Count -eq 0) "$handlerName can overwrite a newer local save with an older server response."
}
Assert-True ($null -eq $pluginDefinition.MainModule.GetType('ServerManager.LocalCharacterMirror')) 'The obsolete accepted-state local writer remains in the plugin.'

$restoreClientProfile = Get-PluginMethodDefinition 'ServerManager.ServerManagerRuntime' 'RestoreClientProfile'
$restoreOriginalProfile = Get-CecilCall $restoreClientProfile 'ServerManager.ValheimPrivateAccess' 'SetGamePlayerProfile'
$readAdmissionReady = Get-CecilCall $restoreClientProfile 'ServerManager.ServerManagerRuntime/ClientConnection' 'get_ReadyAcknowledgementSent'
$admissionGate = $readAdmissionReady.Next
while ($null -ne $admissionGate -and $admissionGate.Offset -lt $restoreOriginalProfile.Offset -and
    $admissionGate.OpCode.FlowControl.ToString() -ne 'Cond_Branch') { $admissionGate = $admissionGate.Next }
Assert-True ($null -ne $restoreOriginalProfile -and $null -ne $readAdmissionReady -and
    $readAdmissionReady.Offset -lt $restoreOriginalProfile.Offset -and
    $admissionGate.OpCode.Name -in @('brtrue', 'brtrue.s') -and
    -not (Test-CecilReachable $admissionGate.Operand $restoreOriginalProfile) -and
    (Test-CecilReachable $admissionGate.Next $restoreOriginalProfile)
) 'Successful remote-session cleanup can restore the pre-join profile over the latest local state, or failed admission lost restoration.'

$plugin = [Reflection.Assembly]::LoadFrom($pluginPath)
$pipelineType = $plugin.GetType(
    "ServerManager.ClientCharacterSavePipeline",
    $true)
$dispatchType = $plugin.GetType(
    "ServerManager.ClientCharacterSaveDispatch",
    $true)
$reasonType = $plugin.GetType(
    "ServerManager.ClientCharacterSaveReason",
    $true)
$identityType = $plugin.GetType(
    "ServerManager.CharacterIdentity",
    $true)
$characterSessionType = $plugin.GetType(
    "ServerManager.CharacterSession",
    $true)
$clientStateType = $plugin.GetType(
    "ServerManager.CharacterClientState",
    $true)
$runtimeType = $plugin.GetType(
    "ServerManager.ServerManagerRuntime",
    $true)
$bufferType = $plugin.GetType(
    "ServerManager.BufferedWorldSocket",
    $true)
$protocolCodecType = $plugin.GetType(
    "ServerManager.ProtocolPacketCodec",
    $true)
$firstJoinGuardType = $plugin.GetType(
    "ServerManager.LocalCharacterFirstJoinGuard",
    $false)
$characterEnvelopeType = $plugin.GetType(
    "ServerManager.CharacterEnvelope",
    $true)
$characterEnvelopeKindType = $plugin.GetType(
    "ServerManager.CharacterEnvelopeKind",
    $true)

$instanceFlags = [Reflection.BindingFlags]::Instance -bor
    [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic
$pipelineConstructor = $pipelineType.GetConstructor(
    $instanceFlags,
    $null,
    [Type[]]@([long], [long]),
    $null)
Assert-True ($null -ne $pipelineConstructor) `
    "The save-pipeline constructor changed."

$staticFlags = [Reflection.BindingFlags]::Static -bor
    [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic
$firstJoinGuardOwner = if ($null -ne $firstJoinGuardType) {
    $firstJoinGuardType
}
else {
    $null
}
$rejectUsedLocalFirstJoinMethod = if ($null -ne $firstJoinGuardOwner) {
    $firstJoinGuardOwner.GetMethods($staticFlags) |
    Where-Object {
        $_.Name -eq "ShouldRejectUsedLocalFirstJoin" -and
        $_.ReturnType -eq [bool] -and
        $_.GetParameters().Count -eq 3 -and
        $_.GetParameters()[0].ParameterType -eq $characterEnvelopeType -and
        $_.GetParameters()[1].ParameterType -eq [bool] -and
        $_.GetParameters()[2].ParameterType -eq [bool]
    } |
    Select-Object -First 1
}
else {
    $null
}
Assert-True ($null -ne $rejectUsedLocalFirstJoinMethod) `
    "The targeted used-local-character first-join classifier is missing."

$createCharacterEnvelope = $characterEnvelopeType.GetMethods($staticFlags) |
    Where-Object {
        $_.Name -eq "Create" -and
        $_.GetParameters().Count -eq 8
    } |
    Select-Object -First 1
Assert-True ($null -ne $createCharacterEnvelope) `
    "CharacterEnvelope.Create changed."
$snapshotKind = [Enum]::Parse($characterEnvelopeKindType, "Snapshot")
$firstJoinIdentity = [Activator]::CreateInstance(
    $identityType,
    [object[]]@("Steam_76561198000000000", "FirstJoinSmoke"))
function New-FirstJoinEnvelope {
    param(
        [long]$Revision,
        [long]$BaseRevision
    )

    return $createCharacterEnvelope.Invoke(
        $null,
        [object[]]@(
            $snapshotKind,
            $Revision,
            $BaseRevision,
            [Guid]::NewGuid(),
            $firstJoinIdentity,
            [DateTime]::UtcNow,
            38,
            [byte[]]@(0x01)))
}

$cleanFirstJoinEnvelope = New-FirstJoinEnvelope 1 0
$registeredCleanEnvelope = New-FirstJoinEnvelope 1 1
$nonSentinelEnvelope = New-FirstJoinEnvelope 2 1
function Test-RejectUsedLocalFirstJoin {
    param(
        [object]$Envelope,
        [bool]$SelectedHasWorldHistory,
        [bool]$ManagedHasPlayerData
    )

    return [bool]$rejectUsedLocalFirstJoinMethod.Invoke(
        $null,
        [object[]]@(
            $Envelope,
            $SelectedHasWorldHistory,
            $ManagedHasPlayerData))
}

Assert-True (
    Test-RejectUsedLocalFirstJoin `
        $cleanFirstJoinEnvelope `
        $true `
        $false) `
    "A used local character was admitted over an unmaterialized server first join."
Assert-True (
    -not (Test-RejectUsedLocalFirstJoin `
        $cleanFirstJoinEnvelope `
        $false `
        $false)) `
    "A genuinely fresh local character was rejected on first join."
Assert-True (
    -not (Test-RejectUsedLocalFirstJoin `
        $registeredCleanEnvelope `
        $true `
        $false)) `
    "An already registered clean revision-1 profile was mistaken for a fresh first join after a process restart."
Assert-True (
    -not (Test-RejectUsedLocalFirstJoin `
        $cleanFirstJoinEnvelope `
        $true `
        $true)) `
    "A used local character was rejected despite a materialized authoritative snapshot."
Assert-True (
    -not (Test-RejectUsedLocalFirstJoin `
        $nonSentinelEnvelope `
        $true `
        $false)) `
    "A non-first-join authoritative revision entered the targeted rejection path."
$createOriginEnvelope = $characterEnvelopeType.GetMethods($staticFlags) |
    Where-Object { $_.Name -eq "CreateWithOrigin" -and $_.GetParameters().Count -eq 9 } |
    Select-Object -First 1
Assert-True ($null -ne $createOriginEnvelope) "Authoritative origin envelope factory is missing."
$starterTemplateEnvelope = $createOriginEnvelope.Invoke($null, [object[]]@(
    $snapshotKind, [long]1, [long]1, [Guid]::NewGuid(), $firstJoinIdentity,
    [DateTime]::UtcNow, [int]43, [byte[]]@(0x01), $true))
Assert-True (Test-RejectUsedLocalFirstJoin $starterTemplateEnvelope $true $true) `
    "A materialized START ITEMS template bypassed the used-local-character guard after reconnect."
Assert-True (-not (Test-RejectUsedLocalFirstJoin $starterTemplateEnvelope $false $true)) `
    "A fresh local character was rejected by START ITEMS origin metadata."
$offer = Get-InstanceMethod $pipelineType "Offer" 2
$tryStart = Get-InstanceMethod $pipelineType "TryStartNext" 3
$tryComplete = Get-InstanceMethod `
    $pipelineType `
    "TryCompleteAcknowledgement" `
    5
$isOverdue = Get-InstanceMethod `
    $pipelineType `
    "IsAcknowledgementOverdue" `
    1
$isDrained = Get-InstanceMethod $pipelineType "IsDrainedThrough" 1
$close = Get-InstanceMethod $pipelineType "Close" 0
$recordHealthRequest = Get-InstanceMethod `
    $characterSessionType `
    "RecordSaveRequest" `
    0
$beginHealthAttempt = Get-InstanceMethod `
    $characterSessionType `
    "BeginSaveAttempt" `
    0
$recordHealthAcceptance = Get-InstanceMethod `
    $characterSessionType `
    "RecordSuccessfulAcceptance" `
    1
$completeHealthSuccess = Get-InstanceMethod `
    $characterSessionType `
    "CompleteSaveSuccess" `
    0
$completeHealthFailure = Get-InstanceMethod `
    $characterSessionType `
    "CompleteSaveFailure" `
    1
$tryCreateHealthSnapshot = Get-InstanceMethod `
    $characterSessionType `
    "TryCreateOpenSaveHealthSnapshot" `
    2
$tryClaimHealthWarning = Get-InstanceMethod `
    $characterSessionType `
    "TryClaimLongUnsavedWarning" `
    3
Assert-True (
    $null -ne $recordHealthRequest -and
    $null -ne $beginHealthAttempt -and
    $null -ne $recordHealthAcceptance -and
    $null -ne $completeHealthSuccess -and
    $null -ne $completeHealthFailure -and
    $null -ne $tryCreateHealthSnapshot -and
    $null -ne $tryClaimHealthWarning) `
    "CharacterSession save-health API is incomplete."
$vanillaReason = [Enum]::Parse($reasonType, "Vanilla")
$inventoryReason = [Enum]::Parse($reasonType, "InventoryDirty")
$finalReason = [Enum]::Parse($reasonType, "GracefulExit")
$periodicFullReason = [Enum]::Parse($reasonType, "PeriodicFull")

$maximumStartsField = $pipelineType.GetField(
    "MaximumStartsPerWindow",
    [Reflection.BindingFlags]"Static,NonPublic")
$maximumRoutineStartsField = $pipelineType.GetField(
    "MaximumRoutineStartsPerWindow",
    [Reflection.BindingFlags]"Static,NonPublic")
$maximumRoutineFullStartsField = $pipelineType.GetField(
    "MaximumRoutineFullStartsPerWindow",
    [Reflection.BindingFlags]"Static,NonPublic")
$isFullProfileReason = $pipelineType.GetMethod(
    "IsFullProfileReason",
    [Reflection.BindingFlags]"Static,NonPublic")
Assert-True (
    $null -ne $maximumStartsField -and
    [int]$maximumStartsField.GetRawConstantValue() -eq 13 -and
    $null -ne $maximumRoutineStartsField -and
    [int]$maximumRoutineStartsField.GetRawConstantValue() -eq 12 -and
    $null -ne $maximumRoutineFullStartsField -and
    [int]$maximumRoutineFullStartsField.GetRawConstantValue() -eq 1 -and
    $null -ne $isFullProfileReason -and
    -not [bool]$isFullProfileReason.Invoke(
        $null,
        [object[]]@($inventoryReason)) -and
    [bool]$isFullProfileReason.Invoke(
        $null,
        [object[]]@($vanillaReason)) -and
    [bool]$isFullProfileReason.Invoke(
        $null,
        [object[]]@($periodicFullReason)) -and
    [bool]$isFullProfileReason.Invoke(
        $null,
        [object[]]@($finalReason))) `
    "The inventory/full reason classification or 12-routine/13-final/one-full pacing limits changed."

function New-Pipeline {
    param(
        [long]$Window = 10,
        [long]$AcknowledgementTimeout = 50
    )

    return $pipelineConstructor.Invoke(
        [object[]]@($Window, $AcknowledgementTimeout))
}

function Offer-Snapshot {
    param(
        [object]$Pipeline,
        [byte]$Marker,
        [object]$Reason
    )

    return [uint64]$offer.Invoke(
        $Pipeline,
        [object[]]@([byte[]]@($Marker), $Reason))
}

function Try-Start {
    param(
        [object]$Pipeline,
        [long]$Revision,
        [long]$Timestamp
    )

    $arguments = [object[]]@($Revision, $Timestamp, $null)
    $started = [bool]$tryStart.Invoke($Pipeline, $arguments)
    return [pscustomobject]@{
        Started = $started
        Dispatch = $arguments[2]
    }
}

function Complete-Ack {
    param(
        [object]$Pipeline,
        [long]$BaseRevision,
        [long]$Revision
    )

    $arguments = [object[]]@(
        $BaseRevision,
        $Revision,
        $null,
        $null,
        $null)
    $valid = [bool]$tryComplete.Invoke($Pipeline, $arguments)
    return [pscustomobject]@{
        Valid = $valid
        ProfileBytes = [byte[]]$arguments[2]
        Reason = $arguments[3]
        Error = [string]$arguments[4]
    }
}

function New-HealthSession {
    param(
        [long]$Revision,
        [long]$OpenedTimestamp
    )

    $session = [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
        $characterSessionType)
    Set-InstanceField $session "_revisionLock" ([object]::new())
    Set-InstanceField $session "_saveLock" ([object]::new())
    Set-InstanceField $session "_saveHealthLock" ([object]::new())
    Set-InstanceField $session "_sessionOpenedTimestamp" $OpenedTimestamp
    Set-InstanceField $session "_currentRevision" $Revision
    Set-InstanceField $session "_longUnsavedWarningRevision" $Revision
    Set-InstanceField $session "_lastSaveError" ([string]::Empty)
    Set-InstanceField `
        $session `
        "<Identity>k__BackingField" `
        ([Activator]::CreateInstance(
            $identityType,
            [object[]]@("steam_health", "HealthCharacter")))
    Set-InstanceField `
        $session `
        "<SessionId>k__BackingField" `
        ([Guid]::NewGuid())
    return $session
}

function Get-HealthSnapshot {
    param(
        [object]$Session,
        [long]$CapturedTimestamp
    )

    $arguments = [object[]]@($CapturedTimestamp, $null)
    $created = [bool]$tryCreateHealthSnapshot.Invoke($Session, $arguments)
    Assert-True ($created -and $null -ne $arguments[1]) `
        "An open character session did not expose save health."
    return $arguments[1]
}

function Claim-HealthWarning {
    param(
        [object]$Session,
        [TimeSpan]$Threshold,
        [long]$CapturedTimestamp
    )

    $arguments = [object[]]@($Threshold, $CapturedTimestamp, $null)
    $claimed = [bool]$tryClaimHealthWarning.Invoke($Session, $arguments)
    return [pscustomobject]@{
        Claimed = $claimed
        Snapshot = $arguments[2]
    }
}

$pipeline = New-Pipeline
$captureA = Offer-Snapshot $pipeline 1 $vanillaReason
$startA = Try-Start $pipeline 10 0
Assert-True $startA.Started "The first save was not dispatched."
Assert-True (
    (Get-InstanceProperty $startA.Dispatch "CaptureId") -eq $captureA) `
    "The first capture ID changed during dispatch."
Assert-True (
    (Get-InstanceProperty $startA.Dispatch "BaseRevision") -eq 10) `
    "The first save did not use the acknowledged base revision."
Assert-True (
    (Get-InstanceProperty $startA.Dispatch "Revision") -eq 11) `
    "The first save target revision is invalid."
Assert-True (
    (Get-InstanceProperty $startA.Dispatch "Reason") -eq $vanillaReason) `
    "The dispatched full profile lost its capture reason."

$captureB = Offer-Snapshot $pipeline 2 $inventoryReason
$captureC = Offer-Snapshot $pipeline 3 $inventoryReason
Assert-True (
    (Get-InstanceProperty $pipeline "InFlightCaptureId") -eq $captureA) `
    "A pending save replaced the active in-flight save."
Assert-True (
    (Get-InstanceProperty $pipeline "PendingCaptureId") -eq $captureC) `
    "The latest pending save did not replace the older pending save."
$blockedByInFlight = Try-Start $pipeline 10 2
Assert-True (-not $blockedByInFlight.Started) `
    "A second save was sent before the active save was acknowledged."

$wrongBase = Complete-Ack $pipeline 9 10
Assert-True (
    -not $wrongBase.Valid -and
    $wrongBase.ProfileBytes.Length -eq 0 -and
    [int]$wrongBase.Reason -eq 0) `
    "An acknowledgement with the wrong base revision was accepted."
$ackA = Complete-Ack $pipeline 10 11
Assert-True (
    $ackA.Valid -and
    $ackA.Reason -eq $vanillaReason -and
    (Test-ByteArrayEqual $ackA.ProfileBytes ([byte[]]@(1)))) `
    "The exact first acknowledgement lost its in-flight reason or profile payload."
$ackA.ProfileBytes[0] = 0xff
$ackARepeated = Complete-Ack $pipeline 10 11
Assert-True (
    -not $ackARepeated.Valid -and
    $ackARepeated.ProfileBytes.Length -eq 0 -and
    [int]$ackARepeated.Reason -eq 0) `
    "A completed acknowledgement was reusable after payload ownership transferred."

$startC = Try-Start $pipeline 11 10
Assert-True $startC.Started `
    "The latest pending save was not promoted within the expanded routine budget."
Assert-True (
    (Get-InstanceProperty $startC.Dispatch "CaptureId") -eq $captureC) `
    "An overwritten pending snapshot was dispatched."
Assert-True (
    (Get-InstanceProperty $startC.Dispatch "BaseRevision") -eq 11 -and
    (Get-InstanceProperty $startC.Dispatch "Revision") -eq 12 -and
    (Get-InstanceProperty $startC.Dispatch "Reason") -eq
        $inventoryReason) `
    "The promoted save did not use the newly acknowledged revision."
$ackC = Complete-Ack $pipeline 11 12
Assert-True ($ackC.Valid -and $ackC.Reason -eq $inventoryReason) `
    "The exact promoted inventory acknowledgement was rejected or misclassified."

$captureD = Offer-Snapshot $pipeline 4 $finalReason
$reservedFinalStart = Try-Start $pipeline 12 12
Assert-True $reservedFinalStart.Started `
    "The graceful-exit save could not use the reserved second start."
Assert-True (
    (Get-InstanceProperty $reservedFinalStart.Dispatch "CaptureId") -eq
        $captureD) `
    "The final pending capture was not dispatched."
Assert-True (
    -not [bool]$isOverdue.Invoke($pipeline, [object[]]@([long]62))) `
    "An acknowledgement timed out at its exact deadline."
Assert-True (
    [bool]$isOverdue.Invoke($pipeline, [object[]]@([long]63))) `
    "An acknowledgement did not time out after its deadline."

$wrongRevision = Complete-Ack $pipeline 12 99
Assert-True (
    -not $wrongRevision.Valid -and
    $wrongRevision.ProfileBytes.Length -eq 0 -and
    [int]$wrongRevision.Reason -eq 0) `
    "A non-contiguous acknowledgement revision was accepted."
$ackD = Complete-Ack $pipeline 12 13
Assert-True ($ackD.Valid -and $ackD.Reason -eq $finalReason) `
    "The exact final acknowledgement was rejected or misclassified."
Assert-True (
    [bool]$isDrained.Invoke($pipeline, [object[]]@($captureD))) `
    "The acknowledged final capture did not satisfy the drain target."

# The pipeline owns one pending slot. A full profile absorbs an older pending
# inventory, while a later inventory cannot evict that full profile. The
# runtime's dirty bit supplies the coalesced inventory again after the full
# profile has entered the unified revision stream.
$priorityPipeline = New-Pipeline -Window 10 -AcknowledgementTimeout 50
$priorityInventory = Offer-Snapshot `
    $priorityPipeline `
    51 `
    $inventoryReason
$priorityFull = Offer-Snapshot `
    $priorityPipeline `
    52 `
    $periodicFullReason
$ignoredBehindFull = Offer-Snapshot `
    $priorityPipeline `
    53 `
    $inventoryReason
Assert-True (
    $priorityInventory -eq 1 -and
    $priorityFull -eq 2 -and
    $ignoredBehindFull -eq $priorityFull -and
    (Get-InstanceProperty $priorityPipeline "PendingCaptureId") -eq
        $priorityFull -and
    [bool](Get-InstanceProperty `
        $priorityPipeline `
        "HasPendingFullProfile")) `
    "An inventory update evicted a pending full profile or consumed a phantom capture ID."
$priorityFullStart = Try-Start $priorityPipeline 40 0
$priorityFullPayload = [byte[]](Get-InstanceProperty `
    $priorityFullStart.Dispatch `
    "PayloadBytes")
Assert-True (
    $priorityFullStart.Started -and
    (Get-InstanceProperty $priorityFullStart.Dispatch "Reason") -eq
        $periodicFullReason -and
    $priorityFullPayload.Length -eq 1 -and
    $priorityFullPayload[0] -eq 52) `
    "The pending full profile did not absorb the older inventory update."
$priorityFullAck = Complete-Ack $priorityPipeline 40 41
Assert-True (
    $priorityFullAck.Valid -and
    $priorityFullAck.Reason -eq $periodicFullReason) `
    "The full-profile priority fixture lost its acknowledgement reason."

$coalescedAfterFull = Offer-Snapshot `
    $priorityPipeline `
    53 `
    $inventoryReason
$priorityInventoryStart = Try-Start $priorityPipeline 41 1
$priorityInventoryPayload = [byte[]](Get-InstanceProperty `
    $priorityInventoryStart.Dispatch `
    "PayloadBytes")
Assert-True (
    $coalescedAfterFull -eq 3 -and
    $priorityInventoryStart.Started -and
    (Get-InstanceProperty $priorityInventoryStart.Dispatch "BaseRevision") -eq 41 -and
    (Get-InstanceProperty $priorityInventoryStart.Dispatch "Revision") -eq 42 -and
    (Get-InstanceProperty $priorityInventoryStart.Dispatch "Reason") -eq
        $inventoryReason -and
    $priorityInventoryPayload.Length -eq 1 -and
    $priorityInventoryPayload[0] -eq 53) `
    "The runtime-coalesced inventory could not follow its full profile in the same revision stream."
$priorityInventoryAck = Complete-Ack $priorityPipeline 41 42
Assert-True (
    $priorityInventoryAck.Valid -and
    $priorityInventoryAck.Reason -eq $inventoryReason) `
    "The post-full inventory acknowledgement was not classified exactly."

# Inventory traffic may use the twelve routine slots in the eleven-second
# window, but a second routine full profile remains independently throttled.
$inventoryPacingPipeline = New-Pipeline `
    -Window 11 `
    -AcknowledgementTimeout 100
$pacingRevision = [long]100
for ($index = 0; $index -lt 12; ++$index) {
    $pacingCapture = Offer-Snapshot `
        $inventoryPacingPipeline `
        ([byte](60 + $index)) `
        $inventoryReason
    $pacingStart = Try-Start `
        $inventoryPacingPipeline `
        $pacingRevision `
        ([long]$index)
    Assert-True $pacingStart.Started `
        "A coalesced inventory update was throttled before the twelfth routine slot."
    $pacingAck = Complete-Ack `
        $inventoryPacingPipeline `
        $pacingRevision `
        ($pacingRevision + 1)
    Assert-True (
        $pacingAck.Valid -and
        (Get-InstanceProperty $pacingStart.Dispatch "CaptureId") -eq
            $pacingCapture -and
        $pacingAck.Reason -eq $inventoryReason) `
        "An inventory pacing acknowledgement lost unified revision identity."
    ++$pacingRevision
}
$reservedGracefulCapture = Offer-Snapshot `
    $inventoryPacingPipeline `
    79 `
    $finalReason
$reservedGracefulStart = Try-Start `
    $inventoryPacingPipeline `
    $pacingRevision `
    11
$reservedGracefulAck = Complete-Ack `
    $inventoryPacingPipeline `
    $pacingRevision `
    ($pacingRevision + 1)
Assert-True (
    $reservedGracefulStart.Started -and
    $reservedGracefulAck.Valid -and
    (Get-InstanceProperty $reservedGracefulStart.Dispatch "CaptureId") -eq
        $reservedGracefulCapture -and
    $reservedGracefulAck.Reason -eq $finalReason) `
    "Twelve routine updates consumed the thirteenth graceful-exit reserve."
++$pacingRevision
$thirteenthInventory = Offer-Snapshot `
    $inventoryPacingPipeline `
    80 `
    $inventoryReason
Assert-True (-not (Try-Start `
    $inventoryPacingPipeline `
    $pacingRevision `
    11).Started) `
    "A thirteenth routine update bypassed the eleven-second pacing window."
Assert-True (-not (Try-Start `
    $inventoryPacingPipeline `
    $pacingRevision `
    12).Started) `
    "The graceful-exit reserve was incorrectly reusable as a routine slot."
$releasedInventory = Try-Start `
    $inventoryPacingPipeline `
    $pacingRevision `
    13
Assert-True (
    $releasedInventory.Started -and
    (Get-InstanceProperty $releasedInventory.Dispatch "CaptureId") -eq
        $thirteenthInventory) `
    "The next inventory update remained throttled after a routine slot expired."

$fullPacingPipeline = New-Pipeline -Window 11 -AcknowledgementTimeout 100
$firstPeriodic = Offer-Snapshot `
    $fullPacingPipeline `
    81 `
    $periodicFullReason
$firstPeriodicStart = Try-Start $fullPacingPipeline 200 0
$firstPeriodicAck = Complete-Ack $fullPacingPipeline 200 201
Assert-True (
    $firstPeriodicStart.Started -and
    $firstPeriodicAck.Valid -and
    (Get-InstanceProperty $firstPeriodicStart.Dispatch "CaptureId") -eq
        $firstPeriodic) `
    "The first periodic full profile did not enter the revision stream."
$secondPeriodic = Offer-Snapshot `
    $fullPacingPipeline `
    82 `
    $periodicFullReason
Assert-True (-not (Try-Start $fullPacingPipeline 201 11).Started) `
    "A second routine full profile bypassed its exact pacing boundary."
$secondPeriodicStart = Try-Start $fullPacingPipeline 201 12
Assert-True (
    $secondPeriodicStart.Started -and
    (Get-InstanceProperty $secondPeriodicStart.Dispatch "CaptureId") -eq
        $secondPeriodic) `
    "A periodic full profile remained throttled after its full-only window expired."

$floodPipeline = New-Pipeline -Window 100 -AcknowledgementTimeout 100
$floodHead = Offer-Snapshot $floodPipeline 1 $vanillaReason
$floodStart = Try-Start $floodPipeline 20 0
Assert-True $floodStart.Started "The flood head was not dispatched."
$latestFloodCapture = [uint64]0
for ($index = 2; $index -le 100; ++$index) {
    $latestFloodCapture = Offer-Snapshot `
        $floodPipeline `
        ([byte]$index) `
        $inventoryReason
}
Assert-True (
    (Get-InstanceProperty $floodPipeline "InFlightCaptureId") -eq
        $floodHead) `
    "Rapid offers replaced the in-flight capture."
Assert-True (
    (Get-InstanceProperty $floodPipeline "PendingCaptureId") -eq
        $latestFloodCapture) `
    "Rapid offers retained more than the latest pending capture."

$floodAck = Complete-Ack $floodPipeline 20 21
Assert-True $floodAck.Valid "The flood head acknowledgement failed."
$latestFloodStart = Try-Start $floodPipeline 21 101
Assert-True $latestFloodStart.Started `
    "The latest flood capture was not promoted."
$latestPayload = [byte[]](
    Get-InstanceProperty $latestFloodStart.Dispatch "PayloadBytes")
Assert-True (
    $latestPayload.Length -eq 1 -and $latestPayload[0] -eq 100) `
    "The promoted flood payload was not the latest snapshot."
Assert-True (
    (Get-InstanceProperty $latestFloodStart.Dispatch "Reason") -eq
        $inventoryReason) `
    "Flood coalescing lost the latest inventory reason."

$close.Invoke($floodPipeline, [object[]]@())
Assert-True (
    -not [bool](Get-InstanceProperty $floodPipeline "HasInFlight") -and
    -not [bool](Get-InstanceProperty $floodPipeline "HasPending")) `
    "Closing the save pipeline retained snapshot state."
$closedFloodAck = Complete-Ack $floodPipeline 21 22
Assert-True (
    -not $closedFloodAck.Valid -and
    $closedFloodAck.ProfileBytes.Length -eq 0 -and
    [int]$closedFloodAck.Reason -eq 0) `
    "Closing a save pipeline retained its in-flight acknowledgement payload."

$timedOutPipeline = New-Pipeline -Window 10 -AcknowledgementTimeout 5
$timedOutCapture = Offer-Snapshot `
    $timedOutPipeline `
    21 `
    $vanillaReason
$timedOutStart = Try-Start $timedOutPipeline 30 100
Assert-True ($timedOutStart.Started -and
    (Get-InstanceProperty $timedOutStart.Dispatch "CaptureId") -eq
        $timedOutCapture) `
    "The timeout fixture did not dispatch its tracked capture."
$timedOutPending = Offer-Snapshot `
    $timedOutPipeline `
    22 `
    $inventoryReason
Assert-True (
    (Get-InstanceProperty $timedOutPipeline "PendingCaptureId") -eq
        $timedOutPending) `
    "The timeout fixture did not retain its latest pending capture."
Assert-True (-not [bool]$isOverdue.Invoke(
    $timedOutPipeline,
    [object[]]@([long]105))) `
    "A tracked capture timed out at its exact acknowledgement deadline."
Assert-True ([bool]$isOverdue.Invoke(
    $timedOutPipeline,
    [object[]]@([long]106))) `
    "A tracked capture remained live after its acknowledgement deadline."
$close.Invoke($timedOutPipeline, [object[]]@())
Assert-True (
    [bool](Get-InstanceProperty $timedOutPipeline "Closed") -and
    -not [bool](Get-InstanceProperty $timedOutPipeline "HasInFlight") -and
    -not [bool](Get-InstanceProperty $timedOutPipeline "HasPending")) `
    "Terminal timeout cleanup retained an in-flight or pending capture."
$timedOutAck = Complete-Ack $timedOutPipeline 30 31
Assert-True (
    -not $timedOutAck.Valid -and
    $timedOutAck.ProfileBytes.Length -eq 0 -and
    [int]$timedOutAck.Reason -eq 0) `
    "A timed-out and closed pipeline retained a acknowledgeable profile payload."
$closedPipelineRejectedOffer = $false
try {
    Offer-Snapshot $timedOutPipeline 23 $vanillaReason | Out-Null
}
catch {
    $closedPipelineRejectedOffer =
        $_.Exception.InnerException -is [InvalidOperationException]
}
Assert-True $closedPipelineRejectedOffer `
    "A timed-out session accepted a new capture after terminal cleanup."

$recoveredPipeline = New-Pipeline -Window 10 -AcknowledgementTimeout 5
$recoveredCapture = Offer-Snapshot `
    $recoveredPipeline `
    24 `
    $vanillaReason
$recoveredStart = Try-Start $recoveredPipeline 30 200
Assert-True ($recoveredStart.Started -and
    (Get-InstanceProperty $recoveredStart.Dispatch "BaseRevision") -eq 30 -and
    (Get-InstanceProperty $recoveredStart.Dispatch "Revision") -eq 31) `
    "A new session could not recover from the last authoritative revision."
$recoveredAck = Complete-Ack $recoveredPipeline 30 31
Assert-True (
    $recoveredAck.Valid -and
    $recoveredAck.Reason -eq $vanillaReason) `
    "The recovered session rejected or misclassified its exact acknowledgement."
Assert-True ([bool]$isDrained.Invoke(
    $recoveredPipeline,
    [object[]]@($recoveredCapture))) `
    "The recovered session did not drain its acknowledged capture."

$failedPeerPipeline = New-Pipeline -Window 10 -AcknowledgementTimeout 5
$healthyPeerPipeline = New-Pipeline -Window 10 -AcknowledgementTimeout 5
$failedPeerCapture = Offer-Snapshot `
    $failedPeerPipeline `
    31 `
    $vanillaReason
$healthyPeerCapture = Offer-Snapshot `
    $healthyPeerPipeline `
    41 `
    $vanillaReason
$failedPeerStart = Try-Start $failedPeerPipeline 40 0
$healthyPeerStart = Try-Start $healthyPeerPipeline 70 0
Assert-True ($failedPeerStart.Started -and $healthyPeerStart.Started) `
    "Independent peer save pipelines did not dispatch independently."
$failedPeerLatest = Offer-Snapshot `
    $failedPeerPipeline `
    32 `
    $inventoryReason
$healthyPeerLatest = Offer-Snapshot `
    $healthyPeerPipeline `
    42 `
    $inventoryReason
Assert-True ([bool]$isOverdue.Invoke(
    $failedPeerPipeline,
    [object[]]@([long]6))) `
    "The failed peer fixture did not reach its terminal timeout."
$close.Invoke($failedPeerPipeline, [object[]]@())
Assert-True (
    [bool](Get-InstanceProperty $failedPeerPipeline "Closed") -and
    -not [bool](Get-InstanceProperty $failedPeerPipeline "HasInFlight") -and
    -not [bool](Get-InstanceProperty $failedPeerPipeline "HasPending")) `
    "One peer's terminal failure retained its save state."
Assert-True (
    -not [bool](Get-InstanceProperty $healthyPeerPipeline "Closed") -and
    [bool](Get-InstanceProperty $healthyPeerPipeline "HasInFlight") -and
    (Get-InstanceProperty $healthyPeerPipeline "PendingCaptureId") -eq
        $healthyPeerLatest) `
    "One peer's failure contaminated another peer's save state."
$healthyPeerAck = Complete-Ack $healthyPeerPipeline 70 71
Assert-True $healthyPeerAck.Valid `
    "The healthy peer could not acknowledge while another peer failed."
$healthyPeerLatestStart = Try-Start $healthyPeerPipeline 71 11
Assert-True ($healthyPeerLatestStart.Started -and
    (Get-InstanceProperty $healthyPeerLatestStart.Dispatch "CaptureId") -eq
        $healthyPeerLatest) `
    "The healthy peer did not promote its latest pending capture."
$healthyPeerLatestAck = Complete-Ack $healthyPeerPipeline 71 72
Assert-True $healthyPeerLatestAck.Valid `
    "The healthy peer rejected its promoted capture acknowledgement."
Assert-True ([bool]$isDrained.Invoke(
    $healthyPeerPipeline,
    [object[]]@($healthyPeerLatest))) `
    "The healthy peer did not complete after another peer timed out."
Assert-True ($failedPeerCapture -eq 1 -and $failedPeerLatest -eq 2 -and
    $healthyPeerCapture -eq 1 -and $healthyPeerLatest -eq 2) `
    "Independent peers unexpectedly shared capture sequence state."

$warningThreshold = [TimeSpan]::FromSeconds(10)
$warningStopwatchTicks = [long](
    $warningThreshold.TotalSeconds * [Diagnostics.Stopwatch]::Frequency)
$openedTimestamp = [long]100
$failedHealthSession = New-HealthSession 7 $openedTimestamp
$initialHealth = Get-HealthSnapshot $failedHealthSession $openedTimestamp
Assert-True (
    $null -eq (Get-InstanceProperty $initialHealth "LastRequestTimestamp") -and
    $null -eq (Get-InstanceProperty $initialHealth "LastSaveStartedTimestamp") -and
    $null -eq (Get-InstanceProperty `
        $initialHealth `
        "LastSuccessfulAcceptanceTimestamp") -and
    -not [bool](Get-InstanceProperty $initialHealth "SaveInProgress") -and
    (Get-InstanceProperty $initialHealth "SuccessfulAcceptanceCount") -eq 0 -and
    (Get-InstanceProperty $initialHealth "ConsecutiveFailureCount") -eq 0) `
    "A fresh session reported stale save-health state."

$recordHealthRequest.Invoke($failedHealthSession, [object[]]@()) | Out-Null
$requestedHealth = Get-HealthSnapshot `
    $failedHealthSession `
    ([Diagnostics.Stopwatch]::GetTimestamp())
Assert-True (
    $null -ne (Get-InstanceProperty $requestedHealth "LastRequestTimestamp") -and
    $null -eq (Get-InstanceProperty `
        $requestedHealth `
        "LastSuccessfulAcceptanceTimestamp") -and
    -not [bool](Get-InstanceProperty $requestedHealth "SaveInProgress")) `
    "Recording a save request changed accepted-shadow or in-flight state."

$beginHealthAttempt.Invoke($failedHealthSession, [object[]]@()) | Out-Null
$startedHealth = Get-HealthSnapshot `
    $failedHealthSession `
    ([Diagnostics.Stopwatch]::GetTimestamp())
$requestTimestamp = [long](Get-InstanceProperty `
    $startedHealth `
    "LastRequestTimestamp")
$startedTimestamp = [long](Get-InstanceProperty `
    $startedHealth `
    "LastSaveStartedTimestamp")
Assert-True (
    [bool](Get-InstanceProperty $startedHealth "SaveInProgress") -and
    $requestTimestamp -le $startedTimestamp) `
    "Beginning a save attempt did not expose one active attempt."

$longFailure = "x" * 3000
$completeHealthFailure.Invoke(
    $failedHealthSession,
    [object[]]@($longFailure)) | Out-Null
$failedHealth = Get-HealthSnapshot `
    $failedHealthSession `
    ([Diagnostics.Stopwatch]::GetTimestamp())
Assert-True (
    -not [bool](Get-InstanceProperty $failedHealth "SaveInProgress") -and
    (Get-InstanceProperty $failedHealth "ConsecutiveFailureCount") -eq 1 -and
    (Get-InstanceProperty $failedHealth "LastError").Length -eq 2048 -and
    $null -eq (Get-InstanceProperty `
        $failedHealth `
        "LastSuccessfulAcceptanceTimestamp") -and
    (Get-InstanceProperty $failedHealth "SuccessfulAcceptanceCount") -eq 0) `
    "A failed save did not terminally clear and bound its health record."

$beforeInitialWarning = Claim-HealthWarning `
    $failedHealthSession `
    $warningThreshold `
    ($openedTimestamp + $warningStopwatchTicks - 1)
Assert-True (-not $beforeInitialWarning.Claimed) `
    "A long-unaccepted warning fired before the configured age boundary."
$initialWarning = Claim-HealthWarning `
    $failedHealthSession `
    $warningThreshold `
    ($openedTimestamp + $warningStopwatchTicks)
Assert-True (
    $initialWarning.Claimed -and
    $null -ne $initialWarning.Snapshot -and
    [bool](Get-InstanceProperty `
        $initialWarning.Snapshot `
        "LongUnsavedWarningClaimed") -and
    (Get-InstanceProperty $initialWarning.Snapshot "CurrentRevision") -eq 7) `
    "Request or failure activity reset the session-open acceptance baseline."
$duplicateInitialWarning = Claim-HealthWarning `
    $failedHealthSession `
    $warningThreshold `
    ($openedTimestamp + (2 * $warningStopwatchTicks))
Assert-True (-not $duplicateInitialWarning.Claimed) `
    "The same unsaved revision emitted more than one warning."

$successfulHealthSession = New-HealthSession 20 ([long]500)
$beginHealthAttempt.Invoke(
    $successfulHealthSession,
    [object[]]@()) | Out-Null
$completeHealthFailure.Invoke(
    $successfulHealthSession,
    [object[]]@("first failure")) | Out-Null
$beginHealthAttempt.Invoke(
    $successfulHealthSession,
    [object[]]@()) | Out-Null
$completeHealthFailure.Invoke(
    $successfulHealthSession,
    [object[]]@("second failure")) | Out-Null
$oldRevisionWarning = Claim-HealthWarning `
    $successfulHealthSession `
    $warningThreshold `
    ([long]500 + $warningStopwatchTicks)
Assert-True $oldRevisionWarning.Claimed `
    "The success-reset fixture did not claim its prior warning epoch."

Set-InstanceField $successfulHealthSession "_currentRevision" ([long]21)
$beginHealthAttempt.Invoke(
    $successfulHealthSession,
    [object[]]@()) | Out-Null
$recordHealthAcceptance.Invoke(
    $successfulHealthSession,
    [object[]]@([long]21)) | Out-Null
$completeHealthSuccess.Invoke(
    $successfulHealthSession,
    [object[]]@()) | Out-Null
$successfulHealth = Get-HealthSnapshot `
    $successfulHealthSession `
    ([Diagnostics.Stopwatch]::GetTimestamp())
$successfulBaseline = [long](Get-InstanceProperty `
    $successfulHealth `
    "LastSuccessfulAcceptanceTimestamp")
Assert-True (
    (Get-InstanceProperty $successfulHealth "CurrentRevision") -eq 21 -and
    (Get-InstanceProperty $successfulHealth "SuccessfulAcceptanceCount") -eq 1 -and
    (Get-InstanceProperty $successfulHealth "ConsecutiveFailureCount") -eq 0 -and
    (Get-InstanceProperty $successfulHealth "LastError") -eq [string]::Empty -and
    -not [bool](Get-InstanceProperty $successfulHealth "SaveInProgress") -and
    -not [bool](Get-InstanceProperty `
        $successfulHealth `
        "LongUnsavedWarningClaimed")) `
    "A successful shadow acceptance did not reset failures and its warning epoch."

$recordHealthRequest.Invoke(
    $successfulHealthSession,
    [object[]]@()) | Out-Null
$beginHealthAttempt.Invoke(
    $successfulHealthSession,
    [object[]]@()) | Out-Null
$completeHealthFailure.Invoke(
    $successfulHealthSession,
    [object[]]@("transient failure")) | Out-Null
$postCommitFailureHealth = Get-HealthSnapshot `
    $successfulHealthSession `
    ([Diagnostics.Stopwatch]::GetTimestamp())
Assert-True (
    -not [bool](Get-InstanceProperty `
        $postCommitFailureHealth `
        "SaveInProgress") -and
    (Get-InstanceProperty `
        $postCommitFailureHealth `
        "ConsecutiveFailureCount") -eq 1 -and
    (Get-InstanceProperty `
        $postCommitFailureHealth `
        "LastSuccessfulAcceptanceTimestamp") -eq $successfulBaseline -and
    (Get-InstanceProperty `
        $postCommitFailureHealth `
        "SuccessfulAcceptanceCount") -eq 1) `
    "A later request or failure replaced the accepted-shadow baseline."

$beforePostCommitWarning = Claim-HealthWarning `
    $successfulHealthSession `
    $warningThreshold `
    ($successfulBaseline + $warningStopwatchTicks - 1)
Assert-True (-not $beforePostCommitWarning.Claimed) `
    "A post-acceptance warning fired before the configured age boundary."
$postCommitWarning = Claim-HealthWarning `
    $successfulHealthSession `
    $warningThreshold `
    ($successfulBaseline + $warningStopwatchTicks)
Assert-True (
    $postCommitWarning.Claimed -and
    (Get-InstanceProperty $postCommitWarning.Snapshot "CurrentRevision") -eq 21) `
    "A request or failed attempt reset the last accepted-shadow baseline."
$duplicatePostCommitWarning = Claim-HealthWarning `
    $successfulHealthSession `
    $warningThreshold `
    ($successfulBaseline + (2 * $warningStopwatchTicks))
Assert-True (-not $duplicatePostCommitWarning.Claimed) `
    "An accepted revision emitted more than one long-unaccepted warning."

$identity = [Activator]::CreateInstance(
    $identityType,
    [object[]]@("steam_1", "Character"))
$clientState = [Activator]::CreateInstance(
    $clientStateType,
    [object[]]@($identity, [Guid]::NewGuid(), [long]10))
$acceptRevision = Get-InstanceMethod `
    $clientStateType `
    "TryAcceptRevision" `
    2
$jumpAccepted = [bool]$acceptRevision.Invoke(
    $clientState,
    [object[]]@([long]10, [long]99))
Assert-True (-not $jumpAccepted) `
    "CharacterClientState accepted a non-contiguous revision jump."
Assert-True (
    (Get-InstanceProperty $clientState "Revision") -eq 10) `
    "A rejected revision jump mutated client revision state."
$nextAccepted = [bool]$acceptRevision.Invoke(
    $clientState,
    [object[]]@([long]10, [long]11))
Assert-True $nextAccepted `
    "CharacterClientState rejected the exact next revision."

$beforeApplicationQuitSource = [Text.RegularExpressions.Regex]::Match(
    $runtimeSource,
    'internal static bool BeforeApplicationQuit\(\)[\s\S]*?' +
        'internal static void BeforeApplicationQuitting').Value
$beforeApplicationQuittingSource = [Text.RegularExpressions.Regex]::Match(
    $runtimeSource,
    'internal static void BeforeApplicationQuitting\(\)[\s\S]*?' +
        'internal static void BeforeDisconnect').Value
$resumeDeferredExitSource = [Text.RegularExpressions.Regex]::Match(
    $runtimeSource,
    'private static void ResumeDeferredClientExit\([\s\S]*?' +
        'private static void RecoverCancelledApplicationQuit').Value
$recoverCancelledQuitSource = [Text.RegularExpressions.Regex]::Match(
    $runtimeSource,
    'private static void RecoverCancelledApplicationQuit\(\)[\s\S]*?' +
        'private static long CreateInitialFullProfileHeartbeatDeadline').Value
Assert-True (
    $runtimeSource.Contains(
        'private static bool _applicationQuitResumePending;') -and
    -not $runtimeSource.Contains('ClientApplicationQuitResumeGate') -and
    $beforeApplicationQuitSource.Contains(
        'if (_applicationQuitResumePending ||') -and
    $resumeDeferredExitSource.Contains(
        '_applicationQuitResumePending = true;') -and
    $resumeDeferredExitSource.IndexOf(
        '_applicationQuitResumePending = true;',
        [StringComparison]::Ordinal) -lt
        $resumeDeferredExitSource.IndexOf(
            'UnityEngine.Application.Quit();',
            [StringComparison]::Ordinal) -and
    $beforeApplicationQuittingSource.Contains(
        '_applicationQuitResumePending = false;') -and
    $recoverCancelledQuitSource.Contains(
        'if (!_applicationQuitResumePending)') -and
    $recoverCancelledQuitSource.Contains(
        '_applicationQuitResumePending = false;')) `
    "The one-state application-quit pass/veto recovery contract changed."

$allStaticMethods = [Reflection.BindingFlags]::Static -bor
    [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic
foreach ($runtimeMethodName in @(
    "HandleServerFinalSaveBegin",
    "HandleClientFinalSaveReady",
    "ProcessDeferredClientExit",
    "ProcessExpiredServerFinalSaveDrains",
    "RecoverCancelledApplicationQuit")) {
    Assert-True ($null -ne $runtimeType.GetMethod(
        $runtimeMethodName,
        $allStaticMethods)) `
        "Missing final-save runtime method $runtimeMethodName."
}
Assert-True ($null -ne (Get-InstanceMethod `
    $bufferType `
    "TryRestrictOutboundForFinalSave" `
    0)) `
    "The server socket final-save outbound restriction is missing."
Assert-True ($null -ne (Get-InstanceMethod `
    $bufferType `
    "TryCompleteFinalSaveRestriction" `
    0)) `
    "The server socket final-save inbound restriction is missing."
$protocolWireVersion = $protocolCodecType.GetField(
    "WireVersion",
    [Reflection.BindingFlags]"Static,Public").GetRawConstantValue()
Assert-True ($protocolWireVersion -eq 21) `
    "Server character controls and scoped library manifest challenges are not protected by wire version 21."

$controlLimitsType = $plugin.GetType(
    "ServerManager.ConnectionProtocolLimits",
    $true)
$controlLimitsArguments = [object[]]::new(6)
$controlLimitsArguments[0] = 512 * 1024
$controlLimitsArguments[1] = 256 * 1024
$controlLimitsArguments[2] = 256 * 1024
$controlLimitsArguments[3] = 512
$controlLimits = $controlLimitsType.GetConstructors()[0].Invoke(
    $controlLimitsArguments)
$controlPacketType = $plugin.GetType("ServerManager.ProtocolPacket", $true)
$controlKindType = $plugin.GetType("ServerManager.ProtocolPacketKind", $true)
$controlSequenceType = $plugin.GetType("ServerManager.ProtocolSequence", $true)
$controlPackageType = $gameAssembly.GetType("ZPackage", $true)
$controlPackageConstructor = $controlPackageType.GetConstructor(
    [Type[]]@([byte[]]))
$controlGetArray = $controlPackageType.GetMethod("GetArray", [Type[]]@())
$controlEncode = $protocolCodecType.GetMethod("Encode", $allStaticMethods)
$controlDecode = $protocolCodecType.GetMethod("TryDecode", $allStaticMethods)
$controlHeaderBytes = $protocolCodecType.GetField(
    "FixedHeaderBytes",
    $allStaticMethods).GetRawConstantValue()
$controlSessionId = [byte[]](1..16)
$controlNonce = [byte[]](17..48)
$controlFactoryArguments = [object[]]@(
    $controlSessionId,
    $controlNonce,
    $controlLimits)

foreach ($controlSpec in @(
    @{ Name = "FinalSaveBegin"; Sequence = 6 },
    @{ Name = "FinalSaveReady"; Sequence = 7 },
    @{ Name = "OperationalKickRequest"; Sequence = 8 },
    @{ Name = "OperationalKickComplete"; Sequence = 9 })) {
    $controlName = $controlSpec.Name
    $controlFactory = $protocolCodecType.GetMethod(
        "Create$controlName",
        $allStaticMethods)
    Assert-True ($null -ne $controlFactory) `
        "The $controlName packet factory is missing."
    $controlPackage = $controlFactory.Invoke($null, $controlFactoryArguments)
    $controlBytes = [byte[]]$controlGetArray.Invoke($controlPackage, $null)
    $controlDecodeArguments = [object[]]@(
        $controlPackage,
        $controlLimits,
        $null,
        $null)
    $controlDecoded = [bool]$controlDecode.Invoke(
        $null,
        $controlDecodeArguments)
    $controlPacket = $controlDecodeArguments[2]
    Assert-True ($controlDecoded -and
        $null -eq $controlDecodeArguments[3] -and
        $controlPacket.Kind.ToString() -eq $controlName -and
        [byte]$controlPacket.Kind -eq $controlSpec.Sequence -and
        $controlPacket.Sequence -eq $controlSpec.Sequence -and
        $controlSequenceType.GetField(
            $controlName,
            $allStaticMethods).GetRawConstantValue() -eq $controlSpec.Sequence -and
        $controlPacket.Payload.Length -eq 0 -and
        $controlBytes.Length -eq $controlHeaderBytes -and
        (Test-ByteArrayEqual $controlPacket.SessionId $controlSessionId) -and
        (Test-ByteArrayEqual $controlPacket.Nonce $controlNonce)) `
        "The $controlName packet did not round-trip its authenticated empty envelope."

    $nonemptyControlPacket = $controlPacketType.GetConstructors()[0].Invoke(
        [object[]]@(
            [Enum]::Parse($controlKindType, $controlName),
            [uint32]$controlSpec.Sequence,
            $controlSessionId,
            $controlNonce,
            [byte[]]@(1)))
    $nonemptyControlEncodeRejected = $false
    try {
        $controlEncode.Invoke(
            $null,
            [object[]]@($nonemptyControlPacket, $controlLimits)) | Out-Null
    }
    catch {
        $nonemptyControlEncodeRejected =
            $_.Exception.InnerException -is [IO.InvalidDataException]
    }
    Assert-True $nonemptyControlEncodeRejected `
        "The $controlName encoder accepted a nonempty control payload."

    $nonemptyControlBytes = [byte[]]::new($controlHeaderBytes + 1)
    [Array]::Copy($controlBytes, $nonemptyControlBytes, $controlHeaderBytes)
    [Array]::Copy(
        [BitConverter]::GetBytes([int]1),
        0,
        $nonemptyControlBytes,
        $controlHeaderBytes - 4,
        4)
    $nonemptyControlBytes[$controlHeaderBytes] = 1
    $nonemptyPackageArguments = [object[]]::new(1)
    $nonemptyPackageArguments[0] = $nonemptyControlBytes
    $controlDecodeArguments[0] = $controlPackageConstructor.Invoke(
        $nonemptyPackageArguments)
    $nonemptyControlDecoded = [bool]$controlDecode.Invoke(
        $null,
        $controlDecodeArguments)
    Assert-True (-not $nonemptyControlDecoded -and
        $null -eq $controlDecodeArguments[2] -and
        $controlDecodeArguments[3].Code.ToString() -eq "MalformedPacket") `
        "The $controlName decoder accepted a nonempty control payload."

    $legacyControlBytes = [byte[]]$controlBytes.Clone()
    $legacyControlBytes[4] = 11
    $legacyControlBytes[5] = 0
    $legacyPackageArguments = [object[]]::new(1)
    $legacyPackageArguments[0] = $legacyControlBytes
    $controlDecodeArguments[0] = $controlPackageConstructor.Invoke(
        $legacyPackageArguments)
    $legacyControlDecoded = [bool]$controlDecode.Invoke(
        $null,
        $controlDecodeArguments)
    Assert-True (-not $legacyControlDecoded -and
        $null -eq $controlDecodeArguments[2] -and
        $controlDecodeArguments[3].Code.ToString() -eq "ProtocolVersionMismatch") `
        "The $controlName decoder still accepts wire version 11."
}

$gracefulTimeoutField = $runtimeType.GetField(
    "GracefulExitSaveTimeoutSeconds",
    $allStaticMethods)
Assert-True ($null -ne $gracefulTimeoutField -and
    $gracefulTimeoutField.GetRawConstantValue() -eq 65) `
    "The final drain does not cover both possible acknowledgement windows."

Write-Output (
    "Character save single-flight, full-priority/latest-inventory coalescing, " +
    "unified-revision exact-ACK, reason-aware pacing, " +
    "timeout, drain, final gate, quit-veto recovery, flood-coalescing, " +
    "independent local saves with ACK RAM-only advancement, " +
    "terminal cleanup, new-session recovery, peer isolation, rejected-session " +
    "teardown, shadow logging, post-acceptance isolation, save-health telemetry, " +
    "and revision smoke tests passed.")
