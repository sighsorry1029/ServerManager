param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"

# The source-linked coordinate fixture uses the production C# language version.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $activityPowerShell = (Get-Command pwsh -ErrorAction Stop).Source
    & $activityPowerShell -NoProfile -File $PSCommandPath -Configuration $Configuration -GamePath $GamePath
    if ($LASTEXITCODE -ne 0) { throw 'The player activity integration smoke failed.' }
    exit 0
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Get-TypeDefinition {
    param($Assembly, [string]$FullName)
    return $Assembly.MainModule.Types |
        Where-Object FullName -eq $FullName |
        Select-Object -First 1
}

function Get-MethodDefinition {
    param($Type, [string]$Name)
    return $Type.Methods |
        Where-Object Name -eq $Name |
        Select-Object -First 1
}

function Test-CallsMethod {
    param($Method, [string]$DeclaringType, [string]$MethodName)
    if ($null -eq $Method -or -not $Method.HasBody) {
        return $false
    }

    return $null -ne ($Method.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq $DeclaringType -and
            $_.Operand.Name -eq $MethodName
        } |
        Select-Object -First 1)
}

function Get-MethodCallCount {
    param($Method, [string]$DeclaringType, [string]$MethodName)
    if ($null -eq $Method -or -not $Method.HasBody) {
        return 0
    }

    return @($Method.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq $DeclaringType -and
            $_.Operand.Name -eq $MethodName
        }).Count
}

function Test-ContainsString {
    param($Method, [string]$Value)
    if ($null -eq $Method -or -not $Method.HasBody) {
        return $false
    }

    return $null -ne ($Method.Body.Instructions |
        Where-Object {
            $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and
            $_.Operand -eq $Value
        } |
        Select-Object -First 1)
}

function Test-ReferencesMember {
    param($Method, [string]$MemberName)
    if ($null -eq $Method -or -not $Method.HasBody) {
        return $false
    }

    return $null -ne ($Method.Body.Instructions |
        Where-Object {
            ($_.Operand -is [Mono.Cecil.FieldReference] -or
             $_.Operand -is [Mono.Cecil.MethodReference]) -and
            $_.Operand.Name -eq $MemberName
        } |
        Select-Object -First 1)
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $projectRoot (
    "bin\$Configuration\ServerManager.dll")
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"
$runtimeSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Networking\ServerManagerRuntime.cs"))
$handleCharacterFragmentSource = [Text.RegularExpressions.Regex]::Match(
    $runtimeSource,
    'private static void HandleServerCharacterFragment\([\s\S]*?' +
        'private static void HandleClientCharacterFragment\(').Value
Assert-True (Test-Path -LiteralPath $assemblyPath) `
    "The built ServerManager assembly is missing."
Assert-True (Test-Path -LiteralPath $cecilPath) `
    "Mono.Cecil is missing from the selected Valheim installation."

Add-Type -Path $cecilPath
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyPath)
try {
    $activityType = Get-TypeDefinition $assembly `
        "ServerManager.PlayerLogging.PlayerActivityRuntime"
    $runtimeType = Get-TypeDefinition $assembly "ServerManager.ServerManagerRuntime"
    $pluginType = Get-TypeDefinition $assembly "ServerManager.ServerManagerPlugin"
    $writerType = Get-TypeDefinition $assembly `
        "ServerManager.PlayerLogging.PlayerTelemetryLogWriter"
    $gameplayLimitType = Get-TypeDefinition $assembly `
        "ServerManager.GameplayLimitValidation"
    $clientEventObservationType = Get-TypeDefinition $assembly `
        "ServerManager.Events.ClientEventObservation"
    $legacyEventType = Get-TypeDefinition $assembly `
        "ServerManager.PlayerLogging.PlayerTelemetryEventType"
    $legacyReliabilityType = Get-TypeDefinition $assembly `
        "ServerManager.PlayerLogging.PlayerTelemetryReliability"
    $challengeType = Get-TypeDefinition $assembly `
        "ServerManager.ProtocolChallengeOptions"
    $packetKindType = Get-TypeDefinition $assembly `
        "ServerManager.ProtocolPacketKind"
    $protocolSequenceType = Get-TypeDefinition $assembly `
        "ServerManager.ProtocolSequence"
    Assert-True ($null -ne $activityType) `
        "The player activity runtime was not compiled."
    Assert-True ($null -ne $runtimeType) `
        "The main ServerManager runtime was not compiled."
    Assert-True ($null -ne $pluginType) `
        "The ServerManager plugin configuration was not compiled."
    Assert-True ($null -ne $writerType) `
        "The player plain-text log writer was not compiled."
    Assert-True ($null -ne $gameplayLimitType) `
        "The shared gameplay-limit helper was not compiled."
    Assert-True ($null -ne $clientEventObservationType) `
        "The shared client-event observation helper was not compiled."
    Assert-True ($null -eq $legacyEventType) `
        "The plain-text activity path retained the legacy JSON event enum."
    Assert-True ($null -eq $legacyReliabilityType) `
        "The plain-text activity path retained the legacy JSON reliability enum."

    foreach ($methodName in @(
            "Start",
            "Tick",
            "OnPlayerReady",
            "OnPeerDisconnected",
            "ObserveRoutedDamage",
            "ObserveServerLocalDamage",
            "ObserveListenHostDeath",
            "OnCharacterShadowAccepted",
            "OnCharacterSaveRejected",
            "Stop")) {
        Assert-True ($null -ne (Get-MethodDefinition $activityType $methodName)) `
            "Player activity producer is missing $methodName."
    }

    $observeRoutedDamage = Get-MethodDefinition `
        $activityType `
        "ObserveRoutedDamage"
    $observeServerLocalDamage = Get-MethodDefinition `
        $activityType `
        "ObserveServerLocalDamage"
    $beforeLocalPlayerDamage = Get-MethodDefinition `
        $runtimeType `
        "BeforeLocalPlayerDamage"
    Assert-True (
        $observeRoutedDamage.Parameters.Count -eq 4 -and
        $observeRoutedDamage.Parameters[0].ParameterType.FullName -eq "ZRpc" -and
        $observeRoutedDamage.Parameters[1].ParameterType.FullName -eq
            "ServerManager.RoutedDamageObservation" -and
        $observeRoutedDamage.Parameters[2].ParameterType.FullName -eq
            "System.Boolean" -and
        $observeRoutedDamage.Parameters[3].ParameterType.FullName -eq
            "System.Boolean") `
        "Remote damage telemetry lost its target observation or attribution contract."
    Assert-True (
        $observeServerLocalDamage.Parameters.Count -eq 4 -and
        $observeServerLocalDamage.Parameters[0].ParameterType.FullName -eq
            "System.Object" -and
        $observeServerLocalDamage.Parameters[1].ParameterType.FullName -eq
            "HitData" -and
        $observeServerLocalDamage.Parameters[2].ParameterType.FullName -eq
            "System.Boolean" -and
        $observeServerLocalDamage.Parameters[3].ParameterType.FullName -eq
            "System.Boolean" -and
        $beforeLocalPlayerDamage.Parameters.Count -eq 2 -and
        $beforeLocalPlayerDamage.Parameters[0].ParameterType.FullName -eq
            "System.Object" -and
        $beforeLocalPlayerDamage.Parameters[1].ParameterType.FullName -eq
            "HitData") `
        "Server-local damage telemetry lost its target or player-attribution contract."

    $activityFullName = $activityType.FullName
    foreach ($binding in @(
            @("Tick", "Tick"),
            @("BeforeNetworkStart", "Start"),
            @("BeforeLocalPlayerDamage", "ObserveServerLocalDamage"),
            @("BeforeServerRoutedRpcDamage", "ObserveRoutedDamage"),
            @("HandleServerCharacterFragment", "OnCharacterShadowAccepted"),
            @("HandleServerCharacterFragment", "OnCharacterSaveRejected"),
            @("CleanupPeer", "OnPeerDisconnected"))) {
        $source = Get-MethodDefinition $runtimeType $binding[0]
        Assert-True (Test-CallsMethod $source $activityFullName $binding[1]) `
            "ServerManagerRuntime.$($binding[0]) does not call player activity $($binding[1])."
    }

    # Shutdown cleanup is intentionally staged through a nonfatal wrapper, so
    # Stop is compiled into a delegate instead of remaining a direct call from
    # AfterNetworkShutdown. Keep the stage and its ordering ahead of services
    # whose disposal/release would prevent the final player log flush.
    $afterShutdownStart = $runtimeSource.IndexOf(
        "internal static void AfterNetworkShutdown(",
        [StringComparison]::Ordinal)
    $afterShutdownEnd = $runtimeSource.IndexOf(
        "internal static void ReportNetworkShutdownHookFailure(",
        $afterShutdownStart,
        [StringComparison]::Ordinal)
    Assert-True (
        $afterShutdownStart -ge 0 -and
        $afterShutdownEnd -gt $afterShutdownStart) `
        "Could not isolate staged network-shutdown cleanup."
    $afterShutdownSource = $runtimeSource.Substring(
        $afterShutdownStart,
        $afterShutdownEnd - $afterShutdownStart)
    $activityShutdownMatch = [Text.RegularExpressions.Regex]::Match(
        $afterShutdownSource,
        'RunNetworkShutdownStep\(\s*"player activity log shutdown",\s*' +
        '\(\)\s*=>\s*PlayerActivityRuntime\.Stop\(' +
        '"server_shutdown"\)\s*\);',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    $characterDisposeOffset = $afterShutdownSource.IndexOf(
        '"character service disposal"',
        [StringComparison]::Ordinal)
    $dataRootReleaseOffset = $afterShutdownSource.IndexOf(
        '"server data-root release"',
        [StringComparison]::Ordinal)
    Assert-True (
        $activityShutdownMatch.Success -and
        $characterDisposeOffset -gt $activityShutdownMatch.Index -and
        $dataRootReleaseOffset -gt $characterDisposeOffset) `
        "Player activity shutdown is not staged before character/data-root release."

    $readyCall = $runtimeType.Methods |
        Where-Object {
            Test-CallsMethod $_ $activityFullName "OnPlayerReady"
        } |
        Select-Object -First 1
    Assert-True ($null -ne $readyCall) `
        "Ready acknowledgement does not register the per-player log state."

    $onPlayerReady = Get-MethodDefinition $activityType "OnPlayerReady"
    Assert-True (($onPlayerReady.Parameters.ParameterType.FullName -join '|') -ceq
        'ZRpc|ServerManager.ServerPeerIdentity|ServerManager.CharacterSession') `
        "Ready logging should receive only the authenticated peer and accepted character session."
    Assert-True (Test-CallsMethod `
            $onPlayerReady `
            $activityFullName `
            "TryResolveAuthenticatedSteam64") `
        "OnPlayerReady does not derive its log key from final Steam authentication."
    $resolveAuthenticatedSteam64 = Get-MethodDefinition `
        $activityType `
        "TryResolveAuthenticatedSteam64"
    Assert-True (Test-CallsMethod `
            $resolveAuthenticatedSteam64 `
            "ServerManager.ServerManagerRuntime" `
            "TryResolveActiveDetectionPeer") `
        "Player logging does not revalidate the generation-bound final Steam session."
    Assert-True (-not (Test-ReferencesMember `
            $resolveAuthenticatedSteam64 `
            "GetPeerID")) `
        "Player logging still depends on a concrete live socket after final authentication."
    Assert-True (Test-CallsMethod `
            $onPlayerReady `
            "ServerManager.CharacterSession" `
            "CaptureCurrentSemanticState") `
        "Ready logging no longer reads the accepted character snapshot."
    Assert-True (Test-CallsMethod `
            $onPlayerReady `
            $activityFullName `
            "WriteInventoryDetailSnapshot") `
        "Ready logging no longer writes the accepted character inventory snapshot."
    Assert-True (Test-CallsMethod `
            $onPlayerReady `
            $activityFullName `
            "TryEnsureLoginWritten") `
        "Ready logging no longer initializes the per-character log stream."
    Assert-True (Test-ContainsString `
            $onPlayerReady `
            "Per-player activity log registration skipped because the final Steam identity could not be revalidated.") `
        "A final-authentication mismatch can still fail without a server diagnostic."
    Assert-True (Test-ReferencesMember $onPlayerReady "get_PlayerId") `
        "Ready logging no longer uses the authoritative CharacterSession player ID."
    $tryEnsureLoginWritten = Get-MethodDefinition `
        $activityType `
        "TryEnsureLoginWritten"
    Assert-True (Test-ContainsString `
            $tryEnsureLoginWritten `
            "[unknown] Logged in.") `
        "The per-character stream lost its human-readable login record."

    $writerMessageTryWrite = $writerType.Methods |
        Where-Object {
            $_.Name -eq "TryWrite" -and $_.Parameters.Count -eq 4 -and
            $_.Parameters[0].ParameterType.FullName -eq "System.String" -and
            $_.Parameters[1].ParameterType.FullName -eq "System.String" -and
            $_.Parameters[2].ParameterType.FullName -eq "System.Int64" -and
            $_.Parameters[3].ParameterType.FullName -eq "System.String"
        } |
        Select-Object -First 1
    $writerTimedTryWrite = $writerType.Methods |
        Where-Object {
            $_.Name -eq "TryWrite" -and $_.Parameters.Count -eq 5 -and
            $_.Parameters[0].ParameterType.FullName -eq "System.String" -and
            $_.Parameters[1].ParameterType.FullName -eq "System.String" -and
            $_.Parameters[2].ParameterType.FullName -eq "System.Int64" -and
            $_.Parameters[3].ParameterType.FullName -eq "System.DateTime" -and
            $_.Parameters[4].ParameterType.FullName -eq "System.String"
        } |
        Select-Object -First 1
    Assert-True ($null -ne $writerMessageTryWrite) `
        "The plain-text writer lost TryWrite(Steam64, character, playerID, message)."
    Assert-True ($null -ne $writerTimedTryWrite) `
        "The plain-text writer lost its timestamp-preserving character stream API."

    $writerMessageTryWriteBlock = $writerType.Methods |
        Where-Object {
            $_.Name -eq "TryWriteBlock" -and $_.Parameters.Count -eq 5 -and
            $_.Parameters[0].ParameterType.FullName -eq "System.String" -and
            $_.Parameters[1].ParameterType.FullName -eq "System.String" -and
            $_.Parameters[2].ParameterType.FullName -eq "System.Int64" -and
            $_.Parameters[3].ParameterType.FullName -eq "System.String" -and
            $_.Parameters[4].ParameterType.FullName -eq
                'System.Collections.Generic.IReadOnlyList`1<System.String>'
        } |
        Select-Object -First 1
    $writerTimedTryWriteBlock = $writerType.Methods |
        Where-Object {
            $_.Name -eq "TryWriteBlock" -and $_.Parameters.Count -eq 6 -and
            $_.Parameters[0].ParameterType.FullName -eq "System.String" -and
            $_.Parameters[1].ParameterType.FullName -eq "System.String" -and
            $_.Parameters[2].ParameterType.FullName -eq "System.Int64" -and
            $_.Parameters[3].ParameterType.FullName -eq "System.DateTime" -and
            $_.Parameters[4].ParameterType.FullName -eq "System.String" -and
            $_.Parameters[5].ParameterType.FullName -eq
                'System.Collections.Generic.IReadOnlyList`1<System.String>'
        } |
        Select-Object -First 1
    Assert-True ($null -ne $writerMessageTryWriteBlock) `
        "The plain-text writer lost its current-time atomic block API."
    Assert-True ($null -ne $writerTimedTryWriteBlock) `
        "The plain-text writer lost its timestamp-preserving atomic block API."

    $activityMessageTryWrite = $activityType.Methods |
        Where-Object {
            $_.Name -eq "TryWrite" -and $_.Parameters.Count -eq 2 -and
            $_.Parameters[1].ParameterType.FullName -eq "System.String"
        } |
        Select-Object -First 1
    $activityTimedTryWrite = $activityType.Methods |
        Where-Object {
            $_.Name -eq "TryWrite" -and $_.Parameters.Count -eq 3 -and
            $_.Parameters[1].ParameterType.FullName -eq "System.DateTime" -and
            $_.Parameters[2].ParameterType.FullName -eq "System.String"
        } |
        Select-Object -First 1
    Assert-True ($null -ne $activityMessageTryWrite) `
        "Player activity no longer emits a direct human-readable message."
    Assert-True ($null -ne $activityTimedTryWrite) `
        "Player activity lost timestamp-preserving plain-text messages."
    $activityTryWriteCore = Get-MethodDefinition $activityType "TryWriteCore"
    Assert-True (Test-CallsMethod `
            $activityTryWriteCore `
            $writerType.FullName `
            "TryWrite") `
        "Player activity messages are not routed to the plain-text writer."
    $activityTryWriteBlock = Get-MethodDefinition $activityType "TryWriteBlock"
    Assert-True ($null -ne $activityTryWriteBlock) `
        "Player activity lost its atomic inventory-block wrapper."
    Assert-True (Test-CallsMethod `
            $activityTryWriteBlock `
            $writerType.FullName `
            "TryWriteBlock") `
        "Inventory blocks are not routed to the atomic plain-text writer API."
    Assert-True ($null -eq (Get-MethodDefinition $activityType "BuildSummary")) `
        "Player activity retained the legacy JSON field-to-summary adapter."

    $onCharacterShadowAccepted = Get-MethodDefinition `
        $activityType `
        "OnCharacterShadowAccepted"
    Assert-True (
        $onCharacterShadowAccepted.Parameters.Count -eq 2) `
        "Accepted player-log observation retained an unused final-save parameter."
    Assert-True (Test-CallsMethod $onCharacterShadowAccepted $activityFullName "RecordAcceptedShadow") `
        "Remote character activity no longer uses shared accepted-shadow logging."
    $onCharacterShadowAccepted = Get-MethodDefinition $activityType "RecordAcceptedShadow"
    foreach ($semanticWriter in @(
            "WriteInventoryDelta",
            "WriteSkillDelta")) {
        Assert-True (Test-CallsMethod `
                $onCharacterShadowAccepted `
                $activityFullName `
                $semanticWriter) `
            "Accepted character RAM shadows are not wired to $semanticWriter."
    }
    Assert-True (-not (Test-CallsMethod `
            $onCharacterShadowAccepted `
            $activityFullName `
            "TryWrite")) `
        "A routine successful character shadow acceptance is still written as activity noise."
    Assert-True (-not (Test-ContainsString $onCharacterShadowAccepted "committed")) `
        "A routine successful character shadow acceptance retained its legacy commit status message."
    $tickState = Get-MethodDefinition $activityType "TickState"
    Assert-True (
        (Test-ReferencesMember $tickState "_inventorySnapshotTicks") -and
        (Test-CallsMethod `
            $tickState `
            $activityFullName `
            "WriteInventoryDetailSnapshot") -and
        -not (Test-CallsMethod `
            $onCharacterShadowAccepted `
            $activityFullName `
            "WriteInventoryDetailSnapshot")) `
        "Periodic full inventory logs are no longer independently timed from accepted deltas."
    Assert-True (
        [Text.RegularExpressions.Regex]::IsMatch(
            $handleCharacterFragmentSource,
            'SendCharacterPayload\([\s\S]*?' +
            'try\s*\{\s*' +
            'PlayerActivityRuntime\.OnCharacterShadowAccepted\([\s\S]*?' +
            'catch\s*\(Exception exception\)\s*' +
            'when\s*\(!IntegrityCanonical\.IsFatal\(exception\)\)',
            [Text.RegularExpressions.RegexOptions]::Singleline)) `
        "Player activity formatting is no longer isolated after the character ACK send."

    Assert-True (Test-CallsMethod $onPlayerReady $activityFullName "WriteInventoryDetailSnapshot") `
        "Ready logging is not wired to always-on detailed inventory output."
    Assert-True ($null -eq (Get-MethodDefinition $activityType "WriteInventorySnapshot")) `
        "Always-detailed inventory logging retained its obsolete summary-only branch."

    $writeInventoryDetail = Get-MethodDefinition `
        $activityType `
        "WriteInventoryDetailSnapshot"
    $appendInventoryDetail = Get-MethodDefinition `
        $activityType `
        "AppendInventoryDetail"
    foreach ($visibleMember in @(
            "get_Stack",
            "get_Quality",
            "get_CustomData")) {
        Assert-True (Test-ReferencesMember `
                $appendInventoryDetail `
                $visibleMember) `
            "Readable inventory details lost $visibleMember."
    }
    foreach ($omittedMember in @(
            "get_Durability",
            "get_Equipped")) {
        Assert-True (-not (Test-ReferencesMember `
                $appendInventoryDetail `
                $omittedMember)) `
            "Compact inventory details unexpectedly retained $omittedMember."
    }
    Assert-True (Test-ContainsString $appendInventoryDetail " Q") `
        "Readable inventory details lost the compact Q<quality> form."
    $activitySource = [IO.File]::ReadAllText((
        Join-Path $projectRoot "PlayerLogging\PlayerActivityRuntime.cs"))
    Assert-True ([Text.RegularExpressions.Regex]::IsMatch(
        $activitySource,
        'item\.Stack\s*==\s*1\s*\?\s*string\.Empty\s*:\s*" x"\s*\+\s*Invariant\(item\.Stack\)')) `
        "Inventory formatting must omit only x1 while preserving other stack values."
    Assert-True ([Text.RegularExpressions.Regex]::IsMatch(
        $activitySource,
        'item\.Quality\s*==\s*1\s*\?\s*string\.Empty\s*:\s*" Q"\s*\+\s*Invariant\(item\.Quality\)')) `
        "Inventory formatting must omit only Q1 while preserving other quality values."
    foreach ($sensitiveMember in @(
            "get_CustomDataSha256",
            "get_CrafterId")) {
        Assert-True (-not (Test-ReferencesMember `
                $appendInventoryDetail `
                $sensitiveMember)) `
            "Readable inventory details expose $sensitiveMember."
    }
    Assert-True (Test-CallsMethod $writeInventoryDetail $activityFullName "TryWriteBlock") `
        "The detailed inventory snapshot is not emitted as one atomic block."
    Assert-True (Test-CallsMethod $writeInventoryDetail $activityFullName "HashInventoryDetail") `
        "Repeated full inventory bodies are no longer compared with a bounded fingerprint."
    Assert-True (Test-ReferencesMember $writeInventoryDetail "set_LastInventorySnapshotCheckTimestamp") `
        "Skipped/rejected inventory checks no longer advance their independent timer."
    Assert-True (Test-CallsMethod $writeInventoryDetail $writerType.FullName "GetStatistics") `
        "Inventory deduplication no longer observes asynchronous disk write failures."
    Assert-True (Test-CallsMethod $appendInventoryDetail $activityFullName "InventoryItemName") `
        "Detailed inventory output no longer resolves saved item hashes to prefab names."
    $inventoryItemName = Get-MethodDefinition $activityType "InventoryItemName"
    foreach ($identityMember in @("get_PrefabName", "get_PrefabHash")) {
        Assert-True (Test-ReferencesMember $inventoryItemName $identityMember) `
            "Inventory name resolution lost $identityMember."
    }
    $writeInventoryDelta = Get-MethodDefinition $activityType "WriteInventoryDelta"
    Assert-True (Test-ContainsString $writeInventoryDelta " Inv: ") `
        "Inventory delta output lost its compact Inv label."
    Assert-True (-not (Test-ContainsString $writeInventoryDelta " Inventory changed: ")) `
        "Inventory delta output retained the repeated long label."
    Assert-True (Test-CallsMethod $writeInventoryDelta $activityFullName "InventoryDeltaItemName") `
        "Inventory delta output no longer resolves saved item hashes to prefab names."
    $resolveInventoryItemName = Get-MethodDefinition $activityType "ResolveInventoryItemName"
    Assert-True (Test-CallsMethod $resolveInventoryItemName "ObjectDB" "TryGetItemPrefab") `
        "Inventory item names are not resolved through Valheim's public ObjectDB hash lookup."
    Assert-True (Test-ContainsString $resolveInventoryItemName "unknown:") `
        "Unresolved inventory items lost their stable unknown-item fallback."
    Assert-True (Test-CallsMethod `
            $appendInventoryDetail `
            $activityFullName `
            "QuoteJsonString") `
        "Inventory custom-data does not use deterministic JSON string escaping."
    foreach ($inventoryText in @(
            "  - ",
            "    CustomData:",
            "      ",
            ": ")) {
        Assert-True (Test-ContainsString $appendInventoryDetail $inventoryText) `
            "Indented inventory output lost '$inventoryText'."
    }
    $quoteJsonString = Get-MethodDefinition $activityType "QuoteJsonString"
    Assert-True ($null -ne $quoteJsonString) `
        "Inventory custom-data lost its JSON string escaper."
    Assert-True (-not (Test-CallsMethod `
            $quoteJsonString `
            $activityFullName `
            "Clip")) `
        "Inventory custom-data values are truncated before being written."
    foreach ($hashText in @("SHA-256", "CustomDataSha256")) {
        Assert-True (-not (Test-ContainsString `
                $appendInventoryDetail `
                $hashText)) `
            "Readable inventory output retained '$hashText'."
    }

    $writeSkillDelta = Get-MethodDefinition $activityType "WriteSkillDelta"
    Assert-True (Test-CallsMethod `
            $writeSkillDelta `
            $activityFullName `
            "IntegerSkillLevel") `
        "Skill logging is no longer restricted to readable integer levels."
    Assert-True (Test-ContainsString $writeSkillDelta " -> ") `
        "Skill logging lost its readable old-to-new format."
    $integerSkillLevel = Get-MethodDefinition $activityType "IntegerSkillLevel"
    Assert-True ($integerSkillLevel.ReturnType.FullName -eq "System.Double") `
        "Mod skill levels must not narrow to Int32 for logging."
    Assert-True ($null -eq ($integerSkillLevel.Body.Instructions |
            Where-Object { $_.OpCode.Code.ToString() -match '^Conv(_Ovf)?_[IU][1248]' } |
            Select-Object -First 1)) `
        "Finite mod skill levels can overflow an integer conversion."
    Assert-True (Test-CallsMethod $integerSkillLevel "System.Math" "Floor") `
        "Skill display must retain floor-level observation without a vanilla cap."
    $getSkillName = Get-MethodDefinition $activityType "GetSkillName"
    Assert-True (-not (Test-ContainsString $getSkillName "unknown")) `
        "Unknown mod skill IDs must remain distinguishable in player logs."
    Assert-True (Test-CallsMethod $getSkillName "System.Int32" "ToString") `
        "Mod skill names lost their numeric ID fallback."
    Assert-True (Test-CallsMethod $getSkillName `
            "System.Globalization.CultureInfo" "get_InvariantCulture") `
        "Numeric mod skill names must not depend on the server locale."

    $activityStart = Get-MethodDefinition $activityType "Start"
    foreach ($removedSetting in @(
            "EnablePlayerActivityLogging",
            "LogPlayerCoordinates",
            "LogCharacterInventoryDetails",
            "PlayerLogFilesToKeep",
            "CoordinateLogIntervalSeconds",
            "CoordinateMinimumMovementMeters",
            "CoordinateHeartbeatSeconds",
            "LogPlayerDamage",
            "DamageLogCooldownSeconds",
            "DamageAggregationSeconds",
            "LogCharacterInventory",
            "LogCharacterSkills",
            "InventoryFullSnapshotIntervalSeconds",
            "PlayerLogMaximumFileMiB")) {
        Assert-True ($null -eq ($pluginType.Properties |
                Where-Object Name -eq $removedSetting |
                Select-Object -First 1)) `
            "The fixed player-log policy retained cfg property $removedSetting."
        Assert-True (-not (Test-CallsMethod $activityStart $pluginType.FullName `
                "get_$removedSetting")) `
            "Player logging still reads the removed $removedSetting cfg value."
        foreach ($activityMethod in $activityType.Methods) {
            Assert-True (-not (Test-ReferencesMember $activityMethod "get_$removedSetting")) `
                "A stale $removedSetting value can still suppress or change player logging."
        }
    }
    Assert-True (Test-CallsMethod $activityStart $writerType.FullName "Start") `
        "Always-on player logging no longer starts its bounded writer."
    foreach ($removedState in @("_logDamage", "_logInventory", "_logSkills",
            "_logCoordinates", "_logInventoryDetails")) {
        Assert-True ($null -eq ($activityType.Fields |
                Where-Object Name -eq $removedState |
                Select-Object -First 1)) `
            "Always-on player activity categories retained the redundant $removedState branch."
    }

    $fixedLogValues = [ordered]@{
        CoordinateIntervalSeconds = 300
        CoordinateInitialDelaySeconds = 5
        DamageCooldownSeconds = 10
        InventorySnapshotIntervalSeconds = 300
        MaximumLogFileBytes = 67108864L
        MaximumLogFilesPerPlayer = 30
    }
    foreach ($entry in $fixedLogValues.GetEnumerator()) {
        $constant = $activityType.Fields |
            Where-Object Name -eq $entry.Key |
            Select-Object -First 1
        Assert-True ($null -ne $constant -and $constant.IsLiteral -and
            $constant.HasConstant -and $constant.Constant -eq $entry.Value) `
            "Fixed player-log policy $($entry.Key) changed from $($entry.Value)."
    }
    foreach ($fixedTimer in @(
            "_coordinateIntervalTicks",
            "_coordinateInitialDelayTicks",
            "_damageCooldownTicks",
            "_inventorySnapshotTicks")) {
        $timerField = $activityType.Fields |
            Where-Object Name -eq $fixedTimer |
            Select-Object -First 1
        Assert-True ($null -ne $timerField -and $timerField.IsInitOnly) `
            "Fixed player-log timer $fixedTimer remains mutable."
    }
    Assert-True (Test-CallsMethod $activityStart `
        "ServerManager.PlayerLogging.PlayerTelemetryLogOptions" "set_MaximumFileBytes") `
        "Player logging silently fell back to the standalone writer's smaller file limit."
    Assert-True ($null -ne ($activityStart.Body.Instructions |
        Where-Object {
            $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldc_I4 -and
            $_.Operand -eq 67108864
        } |
        Select-Object -First 1)) `
        "Player logging no longer explicitly applies its fixed 64 MiB rotation size."
    $retentionWrite = $activityStart.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.PlayerLogging.PlayerTelemetryLogOptions" -and
            $_.Operand.Name -eq "set_MaximumFilesPerPlayer"
        } | Select-Object -First 1
    Assert-True ($null -ne $retentionWrite -and
        $retentionWrite.Previous.OpCode.Code.ToString() -in @("Ldc_I4", "Ldc_I4_S") -and
        [int]$retentionWrite.Previous.Operand -eq 30) `
        "Player logging no longer explicitly applies its fixed 30-file per-account retention."

    $tryWriteDamage = Get-MethodDefinition $activityType "TryWriteDamage"
    Assert-True (
        $tryWriteDamage.Parameters.Count -eq 5 -and
        $tryWriteDamage.Parameters[1].ParameterType.FullName -eq "HitData" -and
        $tryWriteDamage.Parameters[2].ParameterType.FullName -eq "System.Boolean" -and
        $tryWriteDamage.Parameters[3].ParameterType.FullName -eq
            "ServerManager.PlayerLogging.PlayerActivityRuntime/DamageCounterpart" -and
        $tryWriteDamage.Parameters[4].ParameterType.FullName -eq "System.Int64") `
        "Damage logging lost its direction, counterpart, or cooldown contract."
    foreach ($damageMessage in @(
            " hit ",
            " was hit by ",
            " for ",
            " raw damage with ",
            " raw damage")) {
        Assert-True (Test-ContainsString $tryWriteDamage $damageMessage) `
            "Compact damage output lost '$damageMessage'."
    }
    foreach ($damageHelper in @(
            "ActivityPrefix",
            "DamageCounterpartName",
            "ResolveCurrentWeapon")) {
        Assert-True (Test-CallsMethod `
                $tryWriteDamage `
                $activityFullName `
                $damageHelper) `
            "Compact damage output is not wired to $damageHelper."
    }
    Assert-True (Test-CallsMethod `
            $tryWriteDamage `
            "ServerManager.GameplayLimitValidation" `
            "TryMeasureRawDamage") `
        "Damage logging no longer uses the shared bounded raw-damage sum."
    Assert-True (
        $null -eq (Get-MethodDefinition $activityType "TryMeasureDamage") -and
        $null -eq (Get-MethodDefinition $activityType "TryAddDamageComponent")) `
        "Player logging reintroduced a private copy of damage-component summation."
    foreach ($obsoleteDamageText in @(
            "Player: ",
            " (ID: ",
            " - Took damage (before resistances): ",
            " - Dealt damage: ",
            ". Weapon ",
            ", lvl",
            " raw damage at ",
            " at ")) {
        Assert-True (-not (Test-ContainsString `
                $tryWriteDamage `
                $obsoleteDamageText)) `
            "Damage output retained obsolete text '$obsoleteDamageText'."
    }
    $formatDamageAmount = Get-MethodDefinition $activityType "FormatDamageAmount"
    Assert-True (Test-ContainsString $formatDamageAmount "0.0") `
        "Damage amounts are no longer rounded to one decimal place."
    $formatCoordinate = Get-MethodDefinition $activityType "FormatFloat"
    Assert-True (Test-ContainsString $formatCoordinate "0") `
        "General player activity coordinates are no longer rounded to whole units."

    $activityPrefix = Get-MethodDefinition $activityType "ActivityPrefix"
    Assert-True ($null -ne $activityPrefix) `
        "Player events lost their shared timestamp-independent activity prefix."
    $positionActivityPrefix = $activityType.Methods |
        Where-Object {
            $_.Name -eq "ActivityPrefix" -and $_.Parameters.Count -eq 2
        } |
        Select-Object -First 1
    Assert-True ($null -ne $positionActivityPrefix) `
        "Player events lost their integer-coordinate activity prefix."
    Assert-True (Test-ContainsString $activityPrefix "[unknown]") `
        "The shared activity prefix lost its unknown-coordinate marker."
    Assert-True (Test-CallsMethod $tickState $activityFullName "WritePosition") `
        "Always-on periodic coordinate records lost their position writer."
    $writePosition = Get-MethodDefinition $activityType "WritePosition"
    Assert-True (
        (Test-ReferencesMember $tickState "get_NextCoordinateTimestamp") -and
        (Test-ReferencesMember $writePosition "set_NextCoordinateTimestamp") -and
        (Test-ReferencesMember $writePosition "_coordinateIntervalTicks")) `
        "Recurring Position records no longer use the independent five-minute deadline."
    foreach ($positionHelper in @("TryGetPosition", "IsFinitePosition")) {
        Assert-True (Test-CallsMethod $writePosition $activityFullName $positionHelper) `
            "Position records lost $positionHelper validation."
        Assert-True (Test-CallsMethod $activityPrefix $activityFullName $positionHelper) `
            "Event prefixes no longer resolve valid live coordinates through $positionHelper."
    }
    Assert-True (
        (Test-ReferencesMember $activityPrefix "get_FirstCoordinateEligibleTimestamp") -and
        -not (Test-ReferencesMember $activityPrefix "get_NextCoordinateTimestamp") -and
        -not (Test-ReferencesMember $activityPrefix "_coordinateIntervalTicks")) `
        "Event coordinates became gated by the periodic Position deadline instead of initial grace."
    foreach ($removedPositionPolicy in @(
            "CoordinateMinimumMovementMeters", "CoordinateHeartbeatSeconds",
            "_coordinateHeartbeatTicks")) {
        Assert-True ($null -eq ($activityType.Fields |
                Where-Object Name -eq $removedPositionPolicy | Select-Object -First 1)) `
            "Position logging retained obsolete movement/heartbeat policy $removedPositionPolicy."
    }
    Assert-True ($null -eq (Get-MethodDefinition $activityType "DistanceSquared")) `
        "Position logging retained its unused movement-distance helper."
    Assert-True (-not (Test-ReferencesMember $activityPrefix "_logCoordinates")) `
        "A removed toggle can still suppress event coordinates."
    foreach ($prefixPart in @("[", "]")) {
        Assert-True (Test-ContainsString $positionActivityPrefix $prefixPart) `
            "The coordinate activity prefix lost '$prefixPart'."
    }
    Assert-True (Test-CallsMethod `
            $positionActivityPrefix `
            $activityFullName `
            "FormatFloat") `
        "The coordinate activity prefix no longer formats whole coordinates."
    Assert-True ($null -eq (Get-MethodDefinition $activityType "PlayerName")) `
        "Player log bodies still repeat the character name from the filename."
    Assert-True ($null -eq (Get-MethodDefinition $activityType "PlayerPrefix")) `
        "General activity output retained the obsolete Player: prefix helper."
    $obsoletePlayerLabel = $activityType.Methods |
        Where-Object {
            $_.HasBody -and $null -ne ($_.Body.Instructions |
                Where-Object {
                    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and
                    $_.Operand -eq "Player: "
                } |
                Select-Object -First 1)
        } |
        Select-Object -First 1
    Assert-True ($null -eq $obsoletePlayerLabel) `
        "A player activity event still emits the redundant Player: label."
    Assert-True ($null -eq (
            Get-MethodDefinition $activityType "DamagePlayerPrefix")) `
        "Damage output retained its verbose Player/ID/coordinates prefix."
    Assert-True ($null -eq (
            Get-MethodDefinition $activityType "DamageCoordinates")) `
        "Damage output retained its separate trailing-coordinate formatter."
    $tryResolvePlayerId = Get-MethodDefinition $activityType "TryResolvePlayerId"
    Assert-True (Test-CallsMethod `
            $tryResolvePlayerId `
            $activityFullName `
            "ResolvePlayerObject") `
        "Server Characters-off logging no longer waits for the observed player ID."
    Assert-True (Test-ReferencesMember $tryResolvePlayerId "GetPlayerID") `
        "The delayed log identity resolver no longer reads Player.GetPlayerID()."
    $tryResolveListenHostPlayerId = Get-MethodDefinition `
        $activityType `
        "TryResolveListenHostPlayerId"
    Assert-True (Test-CallsMethod `
            $tryResolveListenHostPlayerId `
            "ServerManager.ValheimPrivateAccess" `
            "GetGamePlayerProfile") `
        "Listen-host log routing no longer reads the selected PlayerProfile."
    Assert-True (Test-ReferencesMember `
            $tryResolveListenHostPlayerId `
            "GetPlayerID") `
        "Listen-host log routing no longer compares profile and live player IDs."

    $cleanPrefabName = Get-MethodDefinition `
        $clientEventObservationType `
        "CleanPrefabInstanceName"
    Assert-True (Test-ContainsString $cleanPrefabName "(Clone)") `
        "Prefab display no longer recognizes the exact Unity (Clone) suffix."
    Assert-True ($null -eq (Get-MethodDefinition $activityType "CleanPrefabName")) `
        "Player logging reintroduced a private prefab-instance cleaner."
    foreach ($displayResolver in @(
            "DamageCounterpartName",
            "ResolveCurrentWeapon")) {
        Assert-True (Test-CallsMethod `
                (Get-MethodDefinition $activityType $displayResolver) `
                "ServerManager.Events.ClientEventObservation" `
                "CleanPrefabInstanceName") `
            "$displayResolver no longer strips the exact Unity (Clone) suffix."
    }
    Assert-True (Test-CallsMethod `
            (Get-MethodDefinition $clientEventObservationType "CleanPrefab") `
            "ServerManager.Events.ClientEventObservation" `
            "CleanPrefabInstanceName") `
        "Event and player-log prefab display no longer share the same suffix rule."
    $resolveCurrentWeapon = Get-MethodDefinition `
        $activityType `
        "ResolveCurrentWeapon"
    Assert-True (-not (Test-ReferencesMember `
            $resolveCurrentWeapon `
            "m_quality")) `
        "Compact outgoing damage output retained weapon quality."

    $activityPeerStateType = $activityType.NestedTypes |
        Where-Object Name -eq "ActivityPeerState" |
        Select-Object -First 1
    Assert-True ($null -ne $activityPeerStateType) `
        "The player activity state type is missing."
    $activityStateConstructor = Get-MethodDefinition $activityPeerStateType ".ctor"
    Assert-True (
        (Test-ReferencesMember $activityStateConstructor "_coordinateInitialDelayTicks") -and
        -not (Test-ReferencesMember $activityStateConstructor "_coordinateIntervalTicks")) `
        "The initial remote-coordinate grace period is no longer independent of the recurring cadence."
    Assert-True ($null -eq ($activityPeerStateType.Properties |
            Where-Object Name -eq "LastPositionLogTimestamp" | Select-Object -First 1)) `
        "Position logging retained obsolete heartbeat timestamp state."
    foreach ($identityField in @("PlayerId", "OpenedAtUtc", "LoginWritten")) {
        Assert-True ($null -ne ($activityPeerStateType.Properties |
                Where-Object Name -eq $identityField |
                Select-Object -First 1)) `
            "Per-character file routing lost the $identityField state."
    }
    foreach ($cooldownField in @(
            "NextOutgoingDamageLogTimestamp",
            "NextIncomingDamageLogTimestamp")) {
        Assert-True ($null -ne ($activityPeerStateType.Properties |
                Where-Object Name -eq $cooldownField |
                Select-Object -First 1)) `
            "Damage logging lost the independent $cooldownField field."
    }
    Assert-True ($null -eq ($activityType.NestedTypes |
            Where-Object Name -eq "DamageWindow" |
            Select-Object -First 1)) `
        "Per-hit damage aggregation survived the plain-log simplification."

    $observeServerLocalDamageMethod = Get-MethodDefinition `
        $activityType `
        "ObserveServerLocalDamage"
    Assert-True (Test-CallsMethod `
            $observeServerLocalDamageMethod `
            $activityFullName `
            "ResolveServerLocalSource") `
        "Server-local NPC/environment damage is no longer resolved for player incoming logs."
    Assert-True (Test-ContainsString `
            $observeServerLocalDamageMethod `
            "listen_host_local_player") `
        "Listen-host damage is mislabeled as an inbound connection source."

    $addIncomingDamage = Get-MethodDefinition $activityType "AddIncomingDamage"
    Assert-True (Test-CallsMethod `
            $addIncomingDamage `
            $activityFullName `
            "TryWriteDamage") `
        "Eligible incoming hits no longer reach the readable damage logger."
    Assert-True (Test-CallsMethod `
            $addIncomingDamage `
            "ServerManager.PlayerLogging.PlayerActivityRuntime/DamageCounterpart" `
            "Detached") `
        "Recent incoming evidence retains a live peer-state reference."

    $writeDeath = Get-MethodDefinition $activityType "WriteDeath"
    Assert-True (Test-CallsMethod `
            $writeDeath `
            $activityFullName `
            "ClearRecentIncomingDamage") `
        "A single incoming hit can be reused to corroborate multiple death reports."
    Assert-True (Test-ContainsString `
            $writeDeath `
            " (unverified client report).") `
        "Uncorroborated deaths lost their explicit unverified marker."
    $matchDeath = Get-MethodDefinition $activityType "DeathReportMatchesSource"
    Assert-True (Test-ContainsString $matchDeath "connection_character") `
        "Death corroboration lost the authenticated inbound-player source allowlist."
    Assert-True (Test-ContainsString $matchDeath "listen_host_local_player") `
        "Death corroboration lost the server-local listen-host source allowlist."

    $reportLocalDeath = Get-MethodDefinition $runtimeType "ReportLocalDeath"
    Assert-True (Test-CallsMethod `
            $reportLocalDeath `
            $activityFullName `
            "ObserveListenHostDeath") `
        "Listen-host death reports are not connected to per-player logging."
    $activityStop = Get-MethodDefinition $activityType "Stop"
    Assert-True (Test-CallsMethod `
            $activityStart `
            "ServerManager.Events.ServerEventRuntime" `
            "add_AuthenticatedPlayerDeathPublished") `
        "Remote authenticated death reports are not subscribed at logging start."
    Assert-True (Test-CallsMethod `
            $activityStop `
            "ServerManager.Events.ServerEventRuntime" `
            "remove_AuthenticatedPlayerDeathPublished") `
        "Remote death subscription is not removed at logging shutdown."

    Assert-True ($null -eq (Get-MethodDefinition $activityType "DeriveStorageKey")) `
        "Player activity logging retained the legacy account-plus-character hash derivation."
    Assert-True ($null -eq ($activityType.Fields |
            Where-Object Name -eq "StorageDomain" |
            Select-Object -First 1)) `
        "Player activity logging retained the legacy hashed storage domain."

    $formatSteam64 = Get-MethodDefinition `
        $activityType `
        "TryFormatIndividualSteam64"
    Assert-True (Test-CallsMethod `
            $formatSteam64 `
            $writerType.FullName `
            "IsValidIndividualSteam64") `
        "Runtime and writer do not share the same canonical Steam64 validator."

    $clientTelemetrySurfacePattern =
        "(?i)(coordinate|position|activity|telemetry)"
    Assert-True ($null -eq ($challengeType.Fields |
            Where-Object Name -match $clientTelemetrySurfacePattern |
            Select-Object -First 1)) `
        "Player logging unexpectedly added a client telemetry challenge field."
    Assert-True ($null -eq ($challengeType.Properties |
            Where-Object Name -match $clientTelemetrySurfacePattern |
            Select-Object -First 1)) `
        "Player logging unexpectedly added client telemetry challenge policy state."

    foreach ($protocolSurface in @($packetKindType, $protocolSequenceType)) {
        Assert-True ($null -ne $protocolSurface) `
            "A connection protocol surface type was not compiled."
        Assert-True ($null -eq ($protocolSurface.Fields |
                Where-Object Name -match $clientTelemetrySurfacePattern |
                Select-Object -First 1)) `
            "Player logging unexpectedly added a client telemetry packet or sequence."
    }

    $rpcNameFields = @($runtimeType.Fields |
        Where-Object Name -like "*RpcName")
    Assert-True ($rpcNameFields.Count -eq 6) `
        "The reviewed custom RPC surface changed; inspect it for client telemetry."
    Assert-True (($rpcNameFields | Where-Object Name -eq "AdminCommandRpcName").Constant -eq
        "sighsorry.ServerManager.Commands.v1") `
        "The added reviewed RPC must remain session-bound administration, not client telemetry."
    foreach ($rpcNameField in $rpcNameFields) {
        Assert-True ($rpcNameField.Name -notmatch $clientTelemetrySurfacePattern) `
            "Player logging unexpectedly added a client telemetry RPC name."
        if ($rpcNameField.HasConstant) {
            Assert-True (
                ([string]$rpcNameField.Constant) -notmatch
                $clientTelemetrySurfacePattern) `
                "Player logging unexpectedly added a client telemetry RPC value."
        }
    }

    $beforeNewConnection = Get-MethodDefinition $runtimeType "BeforeNewConnection"
    Assert-True ((Get-MethodCallCount `
                $beforeNewConnection `
                "ServerManager.RawProtocolRpcTransport" `
                "Register") -eq 6) `
        "The reviewed custom RPC registration count changed; inspect it for client telemetry."

    # Execute the actual cadence/prefix/inventory methods and state constructor in memory.
    # Only the game objects, clock, log sink and unrelated activity producers are
    # inert boundaries; the scheduling and validation logic is not copied.
    $activitySource = Get-Content -LiteralPath (
        Join-Path $projectRoot 'PlayerLogging/PlayerActivityRuntime.cs') -Raw
    $coordinateImplementation = foreach ($fieldName in @(
            'CoordinateIntervalSeconds', 'CoordinateInitialDelaySeconds',
            'InventorySnapshotIntervalSeconds', 'MaximumInventoryDetailEntries', '_coordinateIntervalTicks',
            '_coordinateInitialDelayTicks', '_inventorySnapshotTicks')) {
        $declaration = [regex]::Match($activitySource,
            '(?m)^    private (?:const|static readonly) \w+ ' + $fieldName + '\s*=[\s\S]*?;')
        Assert-True $declaration.Success "Cannot source-link coordinate field $fieldName."
        $declaration.Value
    }
    $coordinateImplementation += foreach ($methodName in @(
            'TickState', 'CompleteState', 'WritePosition', 'ActivityPrefix',
            'TryGetPosition', 'IsFinitePosition', 'SecondsToTicks', 'AddTicks',
            'IsFinite', 'FormatFloat', 'WriteInventoryDetailSnapshot',
            'HashInventoryDetail', 'AppendInventoryDetail', 'QuoteJsonString', 'Invariant')) {
        $declarations = [regex]::Matches($activitySource,
            '(?m)^    private static [^\r\n]*\b' + $methodName + '\([\s\S]*?^    \}')
        $expectedCount = if ($methodName -eq 'ActivityPrefix') { 2 } elseif ($methodName -eq 'Invariant') { 3 } else { 1 }
        Assert-True ($declarations.Count -eq $expectedCount) `
            "Cannot source-link coordinate method $methodName unambiguously."
        $declarations | ForEach-Object Value
    }
    $stateDeclaration = [regex]::Match($activitySource,
        '(?m)^    private sealed class ActivityPeerState\s*\{[\s\S]*?^    \}')
    Assert-True $stateDeclaration.Success 'Cannot source-link the coordinate state constructor.'
    $coordinateImplementation += $stateDeclaration.Value
    $coordinateFixture = @'
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
public static class PlayerActivityCoordinateSmoke
{
    private static readonly List<string> Messages = new List<string>();
    private static readonly List<string[]> InventoryBlocks = new List<string[]>();
    private static TimeZoneInfo _serverTimeZone = TimeZoneInfo.Local;
    private static int BlockAttempts = 0;
    private static DateTime LastBlockOccurredAtUtc;
    private static FakeWriter _writer = new FakeWriter();
    private static bool FailDuringNextEnqueue = false;
    private sealed class FakeWriterStatistics { internal long WriteFailures; }
    private sealed class FakeWriter
    {
        internal long WriteFailures = 0;
        internal int StatisticsReads = 0;
        internal FakeWriterStatistics GetStatistics() { ++StatisticsReads; return new FakeWriterStatistics { WriteFailures = WriteFailures }; }
    }
    private const long DeathCorrelationTicks = 1000;
    private static bool AcceptWrites = true;
    private static class Stopwatch { internal const long Frequency = 1000; internal static long Now = 0; internal static long GetTimestamp() => Now; }
    private struct Vector3 { internal float x, y, z; internal Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    private sealed class Transform { internal Vector3 position = new Vector3(10, 20, 30); }
    private sealed class Player { internal Transform transform = new Transform(); }
    private sealed class ZNetPeer
    {
        internal Vector3 Position = new Vector3(10, 20, 30);
        internal bool Unavailable = false;
        internal int Reads = 0;
        internal Vector3 GetRefPos() { ++Reads; if (Unavailable) throw new InvalidOperationException(); return Position; }
    }
    private sealed class CharacterSemanticSnapshot
    {
        internal List<CharacterSemanticItemState> Items = new List<CharacterSemanticItemState>();
    }
    private sealed class CharacterSemanticItemState
    {
        internal string PrefabName = "Wood";
        internal int Stack = 1;
        internal int Quality = 1;
        internal int PositionX = 0;
        internal int PositionY = 0;
        internal float Durability = 100;
        internal bool Equipped = false;
        internal Dictionary<string, string> CustomData = new Dictionary<string, string>();
    }
    private sealed class DamageCounterpart { }
    private static class IntegrityCanonical { internal static bool IsFatal(Exception exception) => false; }
    private static string Clip(string value, int maximumCharacters) => value;
    private static string FormatReason(string reason) => reason.Replace('_', ' ');
    private static bool TryEnsureLoginWritten(ActivityPeerState state) => true;
    private static void ClearRecentIncomingDamage(ActivityPeerState state) { }
    private static string InventoryItemName(CharacterSemanticItemState item) => item.PrefabName;
    private static bool TryWriteBlock(ActivityPeerState state, DateTime occurredAtUtc, string header, IReadOnlyList<string> lines)
    {
        ++BlockAttempts;
        if (!AcceptWrites) return false;
        if (FailDuringNextEnqueue) { ++_writer.WriteFailures; FailDuringNextEnqueue = false; }
        LastBlockOccurredAtUtc = occurredAtUtc;
        InventoryBlocks.Add(lines.ToArray());
        return true;
    }
    private static bool TryWrite(ActivityPeerState state, string message) { if (!AcceptWrites) return false; Messages.Add(message); return true; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Coordinate fixture: " + message); }
    private static ActivityPeerState Open(bool remote = true)
    {
        Messages.Clear(); AcceptWrites = true; Stopwatch.Now = 100000;
        InventoryBlocks.Clear(); BlockAttempts = 0;
        _writer = new FakeWriter(); FailDuringNextEnqueue = false;
        return new ActivityPeerState("account", "character", 1, remote ? new ZNetPeer() : null, remote ? null : new Player(), Stopwatch.Now, DateTime.UtcNow);
    }
    private static void Tick(ActivityPeerState state, long now) { Stopwatch.Now = now; TickState(state, now); }
    public static void Run()
    {
        ActivityPeerState state = Open();
        Require(state.FirstCoordinateEligibleTimestamp == 105000 && state.NextCoordinateTimestamp == 105000, "remote initial delay must be five seconds");
        Tick(state, 104999);
        Require(Messages.Count == 0 && ActivityPrefix(state) == "[unknown]" && state.Peer!.Reads == 0, "remote coordinates escaped initial grace");
        Tick(state, 105000);
        Require(Messages.Count == 1 && Messages[0] == "[10, 20, 30] Position." && state.NextCoordinateTimestamp == 405000, "first record or five-minute deadline is wrong");
        state.Peer!.Position = new Vector3(90, 80, 70);
        Tick(state, 106000);
        Require(ActivityPrefix(state) == "[90, 80, 70]" && Messages.Count == 1 && state.NextCoordinateTimestamp == 405000, "live event prefixes must not wait for or alter the Position timer");
        Tick(state, 165000);
        Tick(state, 404999);
        Require(Messages.Count == 1, "movement or the former 60-second heartbeat emitted an early Position record");
        state.Peer.Position = new Vector3(11, 20, 30);
        Tick(state, 405000);
        Require(Messages.Count == 2 && Messages[1] == "[11, 20, 30] Position.", "sub-three-meter movement suppressed a due record");
        Tick(state, 705000);
        Require(Messages.Count == 3 && Messages[2] == Messages[1], "stationary players must still log every five minutes");
        state.Peer.Position = new Vector3(50, 60, 70);
        Stopwatch.Now = 706000; Messages.Clear(); CompleteState(state, "peer_disconnected");
        Require(Messages.Count == 2 && Messages[0] == "[50, 60, 70] Position." && Messages[1] == "[50, 60, 70] Logged out (peer disconnected).", "logout must force a fresh Position before logout, bypassing the timer");
        state.Peer.Position = new Vector3(51, 61, 71);
        Require(ActivityPrefix(state) == "[51, 61, 71]", "prefix did not observe the latest event coordinates");
        state.Peer.Unavailable = true;
        Require(ActivityPrefix(state) == "[51, 61, 71]", "last-observed fallback was lost");
        state.HasObservedPosition = false;
        Require(ActivityPrefix(state) == "[50, 60, 70]", "last-logged fallback was lost");

        state = Open(); Stopwatch.Now = 104999; CompleteState(state, "peer_disconnected");
        Require(Messages.Count == 1 && Messages[0] == "[unknown] Logged out (peer disconnected)." && state.Peer!.Reads == 0 && !state.HasLoggedPosition, "forced early logout bypassed remote initial grace");
        state = Open(false);
        Require(state.FirstCoordinateEligibleTimestamp == 100000 && ActivityPrefix(state) == "[10, 20, 30]", "listen-host coordinates should be immediately eligible");
        Tick(state, 100000);
        Require(Messages.Count == 1 && state.NextCoordinateTimestamp == 400000, "listen-host first Position should be immediate with the same recurring cadence");

        foreach (Vector3 invalid in new[] { new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0), new Vector3(0, 0, float.NegativeInfinity), new Vector3(10000001, 0, 0), new Vector3(0, -10000001, 0) })
        {
            state = Open(); state.Peer!.Position = invalid; Tick(state, 105000);
            Require(Messages.Count == 0 && !state.HasLoggedPosition && ActivityPrefix(state) == "[unknown]" && state.NextCoordinateTimestamp == 405000, "invalid coordinates must be rejected without a tight retry loop");
        }
        state = Open(); state.Peer!.Unavailable = true; Tick(state, 105000);
        Require(Messages.Count == 0 && !state.HasLoggedPosition && state.NextCoordinateTimestamp == 405000, "unavailable positions must be skipped safely");
        state.Peer.Unavailable = false; Tick(state, 105001);
        Require(Messages.Count == 0 && ActivityPrefix(state) == "[10, 20, 30]", "event prefixes should recover independently while Position waits for its next deadline");
        Tick(state, 405000);
        Require(Messages.Count == 1, "a later valid position did not recover at the next deadline");
        state = Open(); AcceptWrites = false; Tick(state, 105000);
        Require(!state.HasLoggedPosition && state.NextCoordinateTimestamp == 405000, "failed writes must not claim a logged position or cause a tight retry loop");

        // Only successful full log blocks establish a deduplication baseline.
        state = Open();
        CharacterSemanticItemState item = new CharacterSemanticItemState();
        item.CustomData.Add("z", "last"); item.CustomData.Add("a", "first");
        CharacterSemanticSnapshot snapshot = new CharacterSemanticSnapshot();
        snapshot.Items.Add(item); state.LatestSemanticSnapshot = snapshot;
        WriteInventoryDetailSnapshot(state, snapshot, 100000);
        Require(InventoryBlocks.Count == 1 && InventoryBlocks[0][0] == "  - Wood", "initial full inventory must be recorded and omit x1/Q1");
        Require(InventoryBlocks[0][2].Contains("\"a\":") && InventoryBlocks[0][3].Contains("\"z\":"), "custom-data keys must have deterministic ordering");
        Require(state.LastInventorySnapshotFingerprint?.Length == 32, "each session must retain only a bounded content fingerprint");
        Require(state.LastInventorySnapshotLocalDate == TimeZoneInfo.ConvertTimeFromUtc(LastBlockOccurredAtUtc, _serverTimeZone).Date, "dedup date and queued block timestamp must agree");
        Tick(state, 399999);
        Require(BlockAttempts == 1, "inventory must not be checked before its own five-minute deadline");
        item.CustomData.Clear(); item.CustomData.Add("a", "first"); item.CustomData.Add("z", "last");
        item.Equipped = true; item.Durability = 1; state.Peer!.Position = new Vector3(90, 80, 70);
        Tick(state, 400000);
        Require(BlockAttempts == 1 && state.LastInventorySnapshotCheckTimestamp == 400000, "same visible body must ignore dictionary order, durability, equipment and coordinates while advancing the check timer");
        Tick(state, 400001);
        Require(BlockAttempts == 1 && state.LastInventorySnapshotCheckTimestamp == 400000, "deduplication must not produce a per-tick formatting loop");

        item.Stack = 2; Tick(state, 700000);
        Require(InventoryBlocks.Count == 2 && InventoryBlocks[1][0] == "  - Wood x2", "changed stack must be recorded");
        item.Quality = 2; Tick(state, 1000000);
        Require(InventoryBlocks.Count == 3 && InventoryBlocks[2][0] == "  - Wood x2 Q2", "changed quality must be recorded");
        item.PrefabName = "Stone"; Tick(state, 1300000);
        Require(InventoryBlocks.Count == 4 && InventoryBlocks[3][0] == "  - Stone x2 Q2", "changed prefab must be recorded");
        string largeValue = new string('x', 65536) + "CUSTOM_DATA_END";
        item.CustomData["a"] = largeValue; Tick(state, 1600000);
        Require(InventoryBlocks.Count == 5 && InventoryBlocks[4][2].Contains(largeValue), "custom-data changes and large values must be preserved without truncation");
        Require(!InventoryBlocks.SelectMany(lines => lines).Any(line => line.Contains("SHA-256") || line.Contains("fingerprint")), "dedup hashes must never appear in player logs");
        byte[] acceptedFingerprint = state.LastInventorySnapshotFingerprint!;
        item.CustomData["a"] = "after queue rejection"; AcceptWrites = false; Tick(state, 1900000);
        Require(InventoryBlocks.Count == 5 && ReferenceEquals(state.LastInventorySnapshotFingerprint, acceptedFingerprint) && state.LastInventorySnapshotCheckTimestamp == 1900000, "queue rejection must retain the accepted baseline and advance the check timer");
        int attemptsAfterRejection = BlockAttempts;
        AcceptWrites = true; Tick(state, 1900001); Tick(state, 2199999);
        Require(BlockAttempts == attemptsAfterRejection, "queue rejection must not retry a large block every tick");
        Tick(state, 2200000);
        Require(InventoryBlocks.Count == 6 && !ReferenceEquals(state.LastInventorySnapshotFingerprint, acceptedFingerprint), "a rejected changed body must recover at the next interval");

        state.LastInventorySnapshotLocalDate = state.LastInventorySnapshotLocalDate.AddDays(-1);
        Tick(state, 2500000);
        Require(InventoryBlocks.Count == 7, "the first full block on a new local date must be logged even if unchanged");
        Tick(state, 2800000);
        Require(InventoryBlocks.Count == 7, "subsequent same-date unchanged blocks must be suppressed");
        WriteInventoryDetailSnapshot(state, snapshot, 2800001, force: true);
        Require(InventoryBlocks.Count == 8, "new managed listen-host session readiness must force its initial block");
        ActivityPeerState reconnected = new ActivityPeerState("account", "character", 1, new ZNetPeer(), null, 2800002, DateTime.UtcNow);
        WriteInventoryDetailSnapshot(reconnected, snapshot, 2800002);
        Require(InventoryBlocks.Count == 9, "a new connection/session must not inherit the prior session's dedup baseline");
        snapshot.Items.Clear(); WriteInventoryDetailSnapshot(reconnected, snapshot, 3100002);
        Require(InventoryBlocks.Count == 10 && InventoryBlocks[9].Single() == "  - [empty]", "inventory becoming empty must be recorded");
        WriteInventoryDetailSnapshot(reconnected, snapshot, 3400002);
        Require(InventoryBlocks.Count == 10, "unchanged empty inventories must deduplicate too");

        // Model an asynchronous append failure, including one racing enqueue.
        state = Open(); state.LatestSemanticSnapshot = snapshot;
        FailDuringNextEnqueue = true;
        WriteInventoryDetailSnapshot(state, snapshot, 100000);
        Require(state.LastInventorySnapshotWriteFailures == 0 && _writer.WriteFailures == 1, "failure baseline must be captured before queue acceptance, not after a fast disk failure");
        Tick(state, 399999);
        Require(_writer.StatisticsReads == 1, "writer statistics must not be polled before the five-minute inventory check");
        Tick(state, 400000);
        Require(InventoryBlocks.Count == 2 && state.LastInventorySnapshotWriteFailures == 1, "unchanged inventory must be retried after a post-queue disk failure");
        Tick(state, 700000);
        Require(InventoryBlocks.Count == 2, "recovered inventory must deduplicate once failure count is unchanged");
        ++_writer.WriteFailures; AcceptWrites = false; Tick(state, 1000000);
        Require(state.LastInventorySnapshotWriteFailures == 1 && state.LastInventorySnapshotCheckTimestamp == 1000000, "rejected recovery must not acknowledge the new disk-failure count or loop every tick");
        int rejectedRecoveryAttempts = BlockAttempts;
        AcceptWrites = true; Tick(state, 1000001);
        Require(BlockAttempts == rejectedRecoveryAttempts, "rejected disk-failure recovery must respect the full check interval");
        Tick(state, 1300000);
        Require(InventoryBlocks.Count == 3 && state.LastInventorySnapshotWriteFailures == 2, "disk-failure recovery must survive an intervening queue rejection");
        Tick(state, 1600000);
        Require(InventoryBlocks.Count == 3, "successful recovery must restore normal deduplication");
    }
    // SOURCE_LINKED_COORDINATE_IMPLEMENTATION
}
'@
    Add-Type -TypeDefinition $coordinateFixture.Replace(
        '// SOURCE_LINKED_COORDINATE_IMPLEMENTATION',
        ($coordinateImplementation -join [Environment]::NewLine))
    [PlayerActivityCoordinateSmoke]::Run()

    Write-Host (
        "Player activity integration smoke passed: five-second coordinate grace, five-minute Position cadence, live event prefixes, forced logout, per-character identity, accepted-body inventory deduplication/date rollover/retry cadence, intact custom data, atomic blocks, and no new client telemetry surface.")
}
finally {
    $assembly.Dispose()
}
