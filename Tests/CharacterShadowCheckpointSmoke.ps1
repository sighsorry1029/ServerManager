param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'Valheim107Fixtures.ps1')

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Test-Bytes([byte[]]$Left, [byte[]]$Right) {
    return $null -ne $Left -and $null -ne $Right -and
        [Convert]::ToBase64String($Left) -ceq [Convert]::ToBase64String($Right)
}

function Get-InstanceMethod {
    param([Type]$Type, [string]$Name, [int]$ParameterCount)
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

function Get-PropertyValue {
    param([object]$Instance, [string]$Name)
    $flags = [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::Public -bor
        [Reflection.BindingFlags]::NonPublic
    $property = $Instance.GetType().GetProperty($Name, $flags)
    Assert-True ($null -ne $property) "Missing property $Name."
    return $property.GetValue($Instance)
}

function New-ReflectedInstance {
    param([Type]$Type, [object[]]$Arguments)
    $flags = [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::Public -bor
        [Reflection.BindingFlags]::NonPublic
    $constructor = $Type.GetConstructors($flags) |
        Where-Object { $_.GetParameters().Count -eq $Arguments.Count } |
        Select-Object -First 1
    Assert-True ($null -ne $constructor) `
        "Missing $($Type.FullName) constructor with $($Arguments.Count) arguments."
    $unwrapped = [object[]]::new($Arguments.Count)
    $parameters = $constructor.GetParameters()
    for ($index = 0; $index -lt $Arguments.Count; ++$index) {
        $unwrapped[$index] =
            [Management.Automation.LanguagePrimitives]::ConvertTo(
                $Arguments[$index],
                $parameters[$index].ParameterType)
    }
    return $constructor.Invoke($unwrapped)
}

function Get-CecilMethod {
    param([string]$TypeName, [string]$MethodName)
    $type = $script:pluginDefinition.MainModule.Types |
        Where-Object FullName -eq $TypeName |
        Select-Object -First 1
    Assert-True ($null -ne $type) "Missing plugin type $TypeName."
    $method = $type.Methods |
        Where-Object { $_.Name -eq $MethodName -and $_.HasBody } |
        Select-Object -First 1
    Assert-True ($null -ne $method) "Missing method $TypeName.$MethodName."
    return $method
}

function Get-CecilCall {
    param($Method, [string]$DeclaringType, [string]$MethodName)
    return $Method.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq $DeclaringType -and
            $_.Operand.Name -eq $MethodName
        } |
        Select-Object -First 1
}

function Get-CallsByType {
    param($Method, [string]$DeclaringType, [string]$MethodName)
    return @($Method.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq $DeclaringType -and
            $_.Operand.Name -eq $MethodName
        })
}

function Invoke-ExpectCharacterStorageFailure {
    param([Reflection.MethodInfo]$Method, [object]$Instance, [object[]]$Arguments)
    try {
        $Method.Invoke($Instance, $Arguments) | Out-Null
    }
    catch {
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) {
            $failure = $failure.InnerException
        }
        if ($failure.GetType().FullName -eq
            "ServerManager.CharacterStorageException") {
            return
        }

        throw $failure
    }

    throw "Expected CharacterStorageException was not raised."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$gameAssemblyPath = Join-Path $GamePath "valheim_Data\Managed\assembly_valheim.dll"
$steamworksPath = Join-Path $GamePath `
    "valheim_Data\Managed\com.rlabrecque.steamworks.net.dll"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"
Assert-True (Test-Path -LiteralPath $pluginPath) `
    "Build ServerManager before running this smoke test."
Assert-True (Test-Path -LiteralPath $gameAssemblyPath) `
    "The installed Valheim assembly was not found."
Assert-True (Test-Path -LiteralPath $cecilPath) `
    "Mono.Cecil from BepInEx was not found."
Assert-True (Test-Path -LiteralPath $steamworksPath) `
    "The installed Steamworks assembly is required to validate readable storage keys."

[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
$gameAssembly = [Reflection.Assembly]::LoadFrom($gameAssemblyPath)
[Reflection.Assembly]::LoadFrom($steamworksPath) | Out-Null
$script:pluginDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $pluginPath)
$plugin = [Reflection.Assembly]::LoadFrom($pluginPath)

$snapshotServiceSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Character\CharacterSnapshotService.cs"))
$repositorySource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Character\CharacterRepository.cs"))
$runtimeSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Networking\ServerManagerRuntime.cs"))
$patchSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "RuntimePatches.cs"))
$eventSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Events\ServerEventRuntime.cs"))

# Save ACKs are live-shadow admission, never a direct disk commit.
$handleSave = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "HandleSaveRequestCore"
$repositoryDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.CharacterRepository" |
    Select-Object -First 1
$legacyImmediateCommitMethods = @(
    $repositoryDefinition.Methods |
        Where-Object Name -eq "Commit")
$evaluateLive = Get-CecilCall `
    $handleSave `
    "ServerManager.CharacterRepository" `
    "EvaluateLiveCandidate"
$directCommit = Get-CallsByType `
    $handleSave `
    "ServerManager.CharacterRepository" `
    "Commit"
$directCheckpoint = Get-CallsByType `
    $handleSave `
    "ServerManager.CharacterRepository" `
    "PersistCheckpointEntry"
$setLive = Get-CecilCall `
    $handleSave `
    "ServerManager.CharacterSnapshotService" `
    "SetLiveSnapshotLocked"
$advanceSession = Get-CecilCall `
    $handleSave `
    "ServerManager.CharacterSession" `
    "TryCommitRevision"
Assert-True (
    $null -ne $evaluateLive -and
    $legacyImmediateCommitMethods.Count -eq 0 -and
    $directCommit.Count -eq 0 -and
    $directCheckpoint.Count -eq 0 -and
    $null -ne $advanceSession -and
    $null -ne $setLive -and
    $evaluateLive.Offset -lt $advanceSession.Offset -and
    $advanceSession.Offset -lt $setLive.Offset) `
    "Save admission is no longer validation plus an in-memory shadow replacement."

# Disconnect releases only the active lease; Open consults a retained shadow first.
$openSession = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "OpenOrCreateServerSession"
$openSessionCore = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "OpenOrCreateSessionCore"
$prepareInitial = Get-CecilCall `
    $openSessionCore `
    "ServerManager.CharacterRepository" `
    "PrepareInitialSnapshot"
$retainedLookupText = "_liveSnapshots.TryGetValue(storageKey, out retainedLive)"
$retainedLookupOffset = $snapshotServiceSource.IndexOf(
    $retainedLookupText,
    [StringComparison]::Ordinal)
$prepareInitialOffset = $snapshotServiceSource.IndexOf(
    "_repository.PrepareInitialSnapshot(",
    [StringComparison]::Ordinal)
$closeSession = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "CloseSessionCore"
$closeRemovesOverlay = Get-CecilCall `
    $closeSession `
    "ServerManager.CharacterSnapshotService" `
    "RemoveLiveSnapshotLocked"
Assert-True (
    $null -ne $prepareInitial -and
    $retainedLookupOffset -ge 0 -and
    $retainedLookupOffset -lt $prepareInitialOffset -and
    $null -eq $closeRemovesOverlay) `
    "Disconnect no longer retains the RAM shadow or reconnect no longer prefers it over disk."

# Resource exhaustion must reject rather than silently evict an authoritative shadow.
$serviceDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.CharacterSnapshotService" |
    Select-Object -First 1
$maximumEntriesField = $serviceDefinition.Fields |
    Where-Object Name -eq "MaximumRetainedLiveSnapshots" |
    Select-Object -First 1
$maximumBytesField = $serviceDefinition.Fields |
    Where-Object Name -eq "MaximumRetainedLiveSnapshotPayloadBytes" |
    Select-Object -First 1
Assert-True (
    $null -ne $maximumEntriesField -and
    [int]$maximumEntriesField.Constant -eq 4096 -and
    $null -ne $maximumBytesField -and
    [long]$maximumBytesField.Constant -eq 268435456 -and
    $snapshotServiceSource.Contains(
        "No active or uncheckpointed snapshot was evicted.")) `
    "The retained-shadow 4096-entry/256-MiB fail-closed budget changed."

$beginServiceCheckpoint = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "BeginCheckpoint"
$beginCheckpointEntryConstructor = Get-CecilCall `
    $beginServiceCheckpoint `
    "ServerManager.CharacterCheckpointEntry" `
    ".ctor"
$checkpointEntryDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.CharacterCheckpointEntry" |
    Select-Object -First 1
$legacyCheckpointEntryProperties = @(
    $checkpointEntryDefinition.Properties |
        Where-Object Name -in @("SemanticSnapshot", "PlayerId"))
$commitCheckpointEntry = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "CommitCheckpointEntry"
$persistFrozenEntry = Get-CecilCall `
    $commitCheckpointEntry `
    "ServerManager.CharacterRepository" `
    "PersistCheckpointEntry"
$removeCaughtUpShadow = Get-CecilCall `
    $commitCheckpointEntry `
    "ServerManager.CharacterSnapshotService" `
    "RemoveLiveSnapshotLocked"
$preserveNewerShadow = Get-CecilCall `
    $commitCheckpointEntry `
    "ServerManager.CharacterSnapshotService/CharacterLiveSnapshot" `
    "WithDurable"
$replaceRebasedShadow = Get-CecilCall `
    $commitCheckpointEntry `
    "ServerManager.CharacterSnapshotService" `
    "SetLiveSnapshotLocked"
$ordinarySetLive = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "SetLiveSnapshotLocked"
$ordinaryAddLive = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "AddLiveSnapshotLocked"
$discardServiceCheckpoint = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "DiscardCheckpoint"
$discardServiceCheckpointEntry = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "DiscardCheckpointEntry"
$beginCheckpointPayloadPin = Get-CecilCall `
    $beginServiceCheckpoint `
    "ServerManager.CharacterSnapshotService" `
    "AddCheckpointEntryPayloadReferencesLocked"
$commitCheckpointEntryRelease = Get-CecilCall `
    $commitCheckpointEntry `
    "ServerManager.CharacterSnapshotService" `
    "ReleaseCheckpointEntryLocked"
$discardCheckpointEntryRelease = Get-CecilCall `
    $discardServiceCheckpointEntry `
    "ServerManager.CharacterSnapshotService" `
    "ReleaseCheckpointEntryLocked"
$discardCheckpointPayloadRelease = Get-CecilCall `
    $discardServiceCheckpoint `
    "ServerManager.CharacterSnapshotService" `
    "ReleaseCheckpointPayloadsLocked"
$retainedPayloadReferencesField = $serviceDefinition.Fields |
    Where-Object Name -eq "_retainedPayloadReferences" |
    Select-Object -First 1
$registeredCheckpointsFieldDefinition = $serviceDefinition.Fields |
    Where-Object Name -eq "_registeredCheckpoints" |
    Select-Object -First 1
$maximumRegisteredCheckpointsField = $serviceDefinition.Fields |
    Where-Object Name -eq "MaximumRegisteredCheckpoints" |
    Select-Object -First 1
$retainedPayloadBytesFieldDefinition = $serviceDefinition.Fields |
    Where-Object Name -eq "_retainedSnapshotPayloadBytes" |
    Select-Object -First 1
$referenceComparerDefinition = $serviceDefinition.NestedTypes |
    Where-Object Name -eq "ByteArrayReferenceComparer" |
    Select-Object -First 1
$checkpointSemanticReparse = Get-CecilCall `
    $commitCheckpointEntry `
    "ServerManager.CharacterSnapshotService" `
    "ValidateSnapshotDetailed"
$addCheckpointPayloads = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "AddCheckpointEntryPayloadReferencesLocked"
$removeCheckpointPayloads = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "RemoveCheckpointEntryPayloadReferencesLocked"
$persistCheckpointEntryMethod = Get-CecilMethod `
    "ServerManager.CharacterRepository" `
    "PersistCheckpointEntry"
$checkpointEncode = Get-CecilCall `
    $persistCheckpointEntryMethod `
    "ServerManager.VanillaCharacterFileCodec" `
    "Encode"
$persistCheckpointSourceStart = $repositorySource.IndexOf(
    "internal string PersistCheckpointEntry(",
    [StringComparison]::Ordinal)
$persistCheckpointSourceEnd = $repositorySource.IndexOf(
    "private static void ValidateCheckpointRebase(",
    $persistCheckpointSourceStart,
    [StringComparison]::Ordinal)
Assert-True (
    $persistCheckpointSourceStart -ge 0 -and
    $persistCheckpointSourceEnd -gt $persistCheckpointSourceStart) `
    "Could not isolate CharacterRepository.PersistCheckpointEntry."
$persistCheckpointSource = $repositorySource.Substring(
    $persistCheckpointSourceStart,
    $persistCheckpointSourceEnd - $persistCheckpointSourceStart)
$encodeOffset = $persistCheckpointSource.IndexOf(
    "VanillaCharacterFileCodec.Encode(",
    [StringComparison]::Ordinal)
$baseValidationOffset = $persistCheckpointSource.IndexOf(
    "ValidateCheckpointRebase(entry, expectedDurableBase);",
    [StringComparison]::Ordinal)
$exactReplayOffset = $persistCheckpointSource.IndexOf(
    "if (SnapshotPayloadMatches(current, entry.Snapshot))",
    [StringComparison]::Ordinal)
$baseMatchOffset = $persistCheckpointSource.IndexOf(
    "if (!SnapshotPayloadMatches(current, expectedDurableBase))",
    [StringComparison]::Ordinal)
$replaceOffset = $persistCheckpointSource.IndexOf(
    "string warning = ReplaceAtomicallyWithBackup(",
    [StringComparison]::Ordinal)
$readbackOffset = $persistCheckpointSource.IndexOf(
    "CharacterEnvelope verified = ReadAndValidateSnapshot(",
    [StringComparison]::Ordinal)
Assert-True (
    $null -ne $beginCheckpointEntryConstructor -and
    $beginCheckpointEntryConstructor.Operand.Parameters.Count -eq 4 -and
    $legacyCheckpointEntryProperties.Count -eq 0 -and
    $null -ne (Get-CecilCall `
        $beginServiceCheckpoint `
        "ServerManager.CharacterCheckpointBatch" `
        ".ctor") -and
    $null -ne $persistFrozenEntry -and
    $null -ne $removeCaughtUpShadow -and
    $null -ne $preserveNewerShadow -and
    $null -ne $replaceRebasedShadow -and
    $null -ne $beginCheckpointPayloadPin -and
    $null -ne $commitCheckpointEntryRelease -and
    $null -ne $discardCheckpointEntryRelease -and
    $null -ne $discardCheckpointPayloadRelease -and
    $null -ne $retainedPayloadReferencesField -and
    $null -ne $registeredCheckpointsFieldDefinition -and
    $null -ne $maximumRegisteredCheckpointsField -and
    [int]$maximumRegisteredCheckpointsField.Constant -eq 64 -and
    $null -ne $retainedPayloadBytesFieldDefinition -and
    $null -ne $referenceComparerDefinition -and
    (Get-CallsByType `
        $addCheckpointPayloads `
        "ServerManager.CharacterSnapshotService" `
        "AddPayloadReferenceLocked").Count -eq 2 -and
    (Get-CallsByType `
        $removeCheckpointPayloads `
        "ServerManager.CharacterSnapshotService" `
        "RemovePayloadReferenceLocked").Count -eq 2 -and
    $snapshotServiceSource.Contains(
        "entry.DurableBase.PayloadUnsafe,") -and
    $snapshotServiceSource.Contains(
        "entry.Snapshot.PayloadUnsafe))") -and
    $snapshotServiceSource.Contains(
        "new Dictionary<byte[], int>(ByteArrayReferenceComparer.Instance)") -and
    $snapshotServiceSource.Contains("return ReferenceEquals(left, right);") -and
    $snapshotServiceSource.Contains(
        "System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(") -and
    $snapshotServiceSource.Contains(
        "_registeredCheckpoints.Count >= MaximumRegisteredCheckpoints") -and
    $snapshotServiceSource.Contains(
        "_registeredCheckpoints.Add(") -and
    -not $snapshotServiceSource.Contains(
        "if (_registeredCheckpoints.Count != 0)") -and
    $null -eq $checkpointSemanticReparse -and
    $null -ne $checkpointEncode -and
    $baseValidationOffset -ge 0 -and
    $exactReplayOffset -gt $baseValidationOffset -and
    $baseMatchOffset -gt $exactReplayOffset -and
    $encodeOffset -gt $baseMatchOffset -and
    $replaceOffset -gt $encodeOffset -and
    $readbackOffset -gt $replaceOffset -and
    $persistCheckpointSource.Contains(
        "if (!SnapshotPayloadMatches(verified, entry.Snapshot))") -and
    $null -ne (Get-CecilCall `
        $ordinarySetLive `
        "ServerManager.CharacterSnapshotService" `
        "EnsureLiveSnapshotCapacityLocked") -and
    $null -ne (Get-CecilCall `
        $ordinaryAddLive `
        "ServerManager.CharacterSnapshotService" `
        "EnsureLiveSnapshotCapacityLocked") -and
    $persistFrozenEntry.Offset -lt $preserveNewerShadow.Offset -and
    $preserveNewerShadow.Offset -lt $replaceRebasedShadow.Offset -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $snapshotServiceSource,
        'if\s*\(durable\.Revision\s*>\s*entry\.Snapshot\.Revision\).*?' +
        'ReleaseCheckpointEntryLocked\(checkpoint,\s*entry\);.*?' +
        'if\s*\(durable\.Revision\s*==\s*entry\.Snapshot\.Revision\)',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $snapshotServiceSource,
        '_repository\.PersistCheckpointEntry\(.*?' +
        'if\s*\(!current\.DurableEnvelope\.MatchesSnapshot\(\s*' +
        'entry\.Snapshot\s*\)\).*?' +
        'SetLiveSnapshotLocked\(\s*' +
        'entry\.StorageKey,\s*' +
        'current\.WithDurable\(entry\.Snapshot\)\s*\);.*?' +
        'ReleaseCheckpointEntryLocked\(checkpoint,\s*entry\);',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "Per-entry checkpoint persistence/rebase, independent payload release, multiple-generation registration, semantic reuse, or ordinary admission caps changed."

# World save hooks: capture at PrepareSave, prove primary success inside the
# worker, and consume/commit only on the main thread.
$worldSavePatchStart = $patchSource.IndexOf(
    'internal static class ServerEventWorldSavePatch', [StringComparison]::Ordinal)
$worldSavePatchEnd = $patchSource.IndexOf(
    'internal static class VerifiedWorldSaveWorkerPatch', [StringComparison]::Ordinal)
Assert-True ($worldSavePatchStart -ge 0 -and $worldSavePatchEnd -gt $worldSavePatchStart) `
    "World-save patch source boundaries are missing."
$worldSavePatchSource = $patchSource.Substring(
    $worldSavePatchStart, $worldSavePatchEnd - $worldSavePatchStart)
$saveWorldPatch = Get-CecilMethod `
    "ServerManager.ServerEventWorldSavePatch" `
    "Prefix"
$preparePatch = Get-CecilMethod `
    "ServerManager.ServerEventWorldSavePatch" `
    "Transpiler"
$workerPrefix = Get-CecilMethod `
    "ServerManager.VerifiedWorldSaveWorkerPatch" `
    "Prefix"
$workerFinalizer = Get-CecilMethod `
    "ServerManager.VerifiedWorldSaveWorkerPatch" `
    "Finalizer"
$transpiler = Get-CecilMethod `
    "ServerManager.VerifiedWorldSaveWorkerPatch" `
    "Transpiler"
$hostSaveGate = Get-CecilCall `
    $saveWorldPatch `
    "ServerManager.ServerManagerRuntime" `
    "AllowLocalHostSave"
$worldInvocationObservation = Get-CecilCall `
    $saveWorldPatch `
    "ServerManager.ServerManagerRuntime" `
    "BeforeWorldSaveInvocation"
Assert-True ($null -ne (Get-CecilCall `
        $saveWorldPatch `
        "ServerManager.ServerManagerRuntime" `
        "BeforeWorldSaveInvocation")) `
    "ZNet.SaveWorld no longer opens ServerManager save bookkeeping."
Assert-True (
    $saveWorldPatch.ReturnType.FullName -eq "System.Boolean" -and
    $saveWorldPatch.Parameters.Count -eq 0 -and
    $null -ne $hostSaveGate -and $null -ne $worldInvocationObservation -and
    $hostSaveGate.Offset -lt $worldInvocationObservation.Offset -and
    $patchSource.Contains(
        "if (!ServerManagerRuntime.AllowLocalHostSave()) return false;") -and
    [Text.RegularExpressions.Regex]::Matches(
        $patchSource,
        '\[HarmonyPatch\(typeof\(ZNet\),\s*"SaveWorld"\)\]').Count -eq 1 -and
    @($preparePatch.Body.Instructions |
        Where-Object {
            $_.Operand -is [string] -and
            $_.Operand.Contains("unique ZNet.SaveWorld boundary")
        }).Count -gt 0 -and
    $patchSource.Contains(
        "nameof(ServerManagerRuntime.BeforeWorldSnapshotPrepared)") -and
    $patchSource.Contains("method.DeclaringType == typeof(ZDOMan)") -and
    $patchSource.Contains("method.Name,") -and
    $patchSource.Contains('"PrepareSave"') -and
    $patchSource.Contains("method.GetParameters().Length == 0") -and
    $patchSource.Contains("anchorCount != 1 || insertionIndex < 0") -and
    $patchSource.Contains("code[insertionIndex].opcode != OpCodes.Ldarg_0") -and
    $patchSource.Contains("zdoManField.DeclaringType != typeof(ZNet)") -and
    $patchSource.Contains("zdoManField.FieldType != typeof(ZDOMan)") -and
    $patchSource.Contains("code.Insert(insertionIndex, callGate);") -and
    -not $worldSavePatchSource.Contains("continueSave") -and
    -not $worldSavePatchSource.Contains("new CodeInstruction(OpCodes.Brtrue") -and
    -not $worldSavePatchSource.Contains("new CodeInstruction(OpCodes.Ret)")) `
    "ZNet.SaveWorld lost its exact observational PrepareSave anchor or the explicit failed-host-startup save guard."
Assert-True (-not $eventSource.Contains("OnWorldSaveFailed(")) `
    "The obsolete unbound world-save failure adapter was reintroduced."
Assert-True (
    $null -ne (Get-CecilCall `
        $workerPrefix `
        "ServerManager.ServerManagerRuntime" `
        "BeforeWorldSaveWorker") -and
    $null -ne (Get-CecilCall `
        $workerFinalizer `
        "ServerManager.ServerManagerRuntime" `
        "AfterWorldSaveWorker") -and
    $workerFinalizer.ReturnType.FullName -eq "System.Exception" -and
    @($transpiler.Body.Instructions |
        Where-Object {
            $_.Operand -is [string] -and
            $_.Operand.Contains("verified Valheim world-save success seam")
        }).Count -gt 0) `
    "SaveWorldThread no longer finalizes worker completion on every exit while retaining a fail-closed verified-success seam."
$workerPatchDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.VerifiedWorldSaveWorkerPatch" |
    Select-Object -First 1
$legacyWorkerPostfixes = @(
    $workerPatchDefinition.Methods |
        Where-Object Name -eq "Postfix")
$workerPatchStart = $patchSource.IndexOf(
    "internal static class VerifiedWorldSaveWorkerPatch",
    [StringComparison]::Ordinal)
$workerPatchEnd = $patchSource.IndexOf(
    "[HarmonyPatch(",
    $workerPatchStart + 1,
    [StringComparison]::Ordinal)
Assert-True (
    $workerPatchStart -ge 0 -and
    $workerPatchEnd -gt $workerPatchStart) `
    "Could not isolate the SaveWorldThread worker patch."
$workerPatchSource = $patchSource.Substring(
    $workerPatchStart,
    $workerPatchEnd - $workerPatchStart)
Assert-True (
    $legacyWorkerPostfixes.Count -eq 0 -and
    $workerPrefix.Body.ExceptionHandlers.Count -ge 1 -and
    $workerFinalizer.Body.ExceptionHandlers.Count -ge 1 -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $workerPatchSource,
        'private static void Prefix\(\)\s*\{\s*try\s*\{\s*' +
        'ServerManagerRuntime\.BeforeWorldSaveWorker\(\);\s*\}\s*' +
        'catch\s*\(Exception exception\).*?' +
        'TryLogObservationFailure\(',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $workerPatchSource,
        'private static Exception\? Finalizer\(Exception\? __exception\)' +
        '.*?try\s*\{\s*ServerManagerRuntime\.AfterWorldSaveWorker\(\);\s*\}' +
        '.*?catch\s*\(Exception observationException\).*?' +
        'TryLogObservationFailure\(' +
        '.*?return __exception;',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $workerPatchSource,
        'private static void TryLogObservationFailure\(string message\).*?' +
        'try\s*\{.*?LogError\(message\);.*?' +
        'catch\s*\(Exception loggingException\).*?' +
        'Preserve the worker''s original result and exception',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "SaveWorldThread observation hooks can escape into vanilla, completion regressed to a success-only postfix, or the original exception is no longer preserved."
Assert-True (
    $patchSource.Contains('PrimarySaveSuccessLogPrefix = "World save (5/5) done. Total time [";') -and
    $patchSource.Contains("nameof(ServerManagerRuntime.MarkWorldSaveWorkerSucceeded)") -and
    $patchSource.Contains("anchorCount != 1 || insertionIndex < 0") -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $workerPatchSource,
        'method\.DeclaringType\s*==\s*typeof\(ZLog\).*?' +
        'string\.Equals\(method\.Name,\s*"Log".*?' +
        'insertionIndex\s*=\s*scan;.*?' +
        'code\.Insert\(\s*insertionIndex,\s*' +
        'new CodeInstruction\(OpCodes\.Call,\s*successMarker\)\);',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    -not $workerPatchSource.Contains("insertionIndex = scan + 1;")) `
    "The worker success marker is no longer inserted immediately before the exact primary-success ZLog.Log call."

$beforePrepare = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "BeforeWorldSnapshotPrepared"
$beforePrepareCore = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "BeforeWorldSnapshotPreparedCore"
$processResults = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "ProcessCompletedWorldSaveCheckpoints"
$afterInvocationFailed = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "AfterWorldSaveInvocationFailed"
$afterWorker = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "AfterWorldSaveWorker"
$discardRuntimeCheckpoint = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "DiscardCharacterCheckpoint"
$adoptCheckpointEntry = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "AdoptCharacterCheckpointEntry"
$tryPendingCheckpoint = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "TryCommitPendingCharacterCheckpoint"
$tryDueCheckpoints = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "TryCommitDueCharacterCheckpoints"
$retrySeconds = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "GetCharacterCheckpointRetrySeconds"
$durableCheckpointCall = Get-CecilCall `
    $tryPendingCheckpoint `
    "ServerManager.CharacterSnapshotService" `
    "CommitCheckpointEntry"
$discardSupersededEntry = Get-CecilCall `
    $adoptCheckpointEntry `
    "ServerManager.CharacterSnapshotService" `
    "DiscardCheckpointEntry"
$publishCheckpointResult = Get-CecilCall `
    $processResults `
    "ServerManager.ServerManagerRuntime" `
    "PublishWorldCharacterCheckpointResult"
$adoptFromResults = Get-CecilCall `
    $processResults `
    "ServerManager.ServerManagerRuntime" `
    "AdoptCharacterCheckpointEntry"
$commitFromResults = Get-CecilCall `
    $processResults `
    "ServerManager.ServerManagerRuntime" `
    "TryCommitPendingCharacterCheckpoint"
$commitDueFromResults = Get-CecilCall `
    $processResults `
    "ServerManager.ServerManagerRuntime" `
    "TryCommitDueCharacterCheckpoints"
$tick = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "Tick"
Assert-True ($null -ne (Get-CecilCall `
        $beforePrepareCore `
        "ServerManager.CharacterSnapshotService" `
        "BeginCheckpoint")) `
    "The world snapshot boundary no longer freezes a character checkpoint."
Assert-True (
    $null -ne $durableCheckpointCall -and
    $null -ne $adoptFromResults -and
    $null -ne $commitFromResults -and
    $null -ne $commitDueFromResults -and
    $null -ne $publishCheckpointResult -and
    $null -ne (Get-CecilCall `
        $tick `
        "ServerManager.ServerManagerRuntime" `
        "ProcessCompletedWorldSaveCheckpoints")) `
    "Verified worker results are no longer adopted and persisted per identity from the main-thread Tick path."
Assert-True (
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'for\s*\(int index\s*=\s*0;\s*index\s*<' +
        '\s*checkpoint\.Entries\.Count;\s*\+\+index\).*?' +
        'try\s*\{.*?AdoptCharacterCheckpointEntry\(.*?' +
        'catch\s*\(Exception exception\)\s*when\s*\(' +
        '\s*!IntegrityCanonical\.IsFatal\(exception\)\).*?' +
        'TryCommitPendingCharacterCheckpoint\(',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "A single character adoption failure can stop the remaining identities or the successful identities are not attempted independently."
Assert-True (
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'if\s*\(!attempt\.PrimaryWorldSaveSucceeded\)\s*' +
        '\{.*?OnWorldSaveCheckpointFailed\(.*?continue;\s*\}' +
        '.*?AdoptCharacterCheckpointEntry\(',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "A failed primary world save can reach the character disk checkpoint."
Assert-True (
    $null -ne (Get-CecilCall `
        $beforePrepareCore `
        "ServerManager.ServerManagerRuntime" `
        "DiscardCharacterCheckpoint") -and
    $null -ne (Get-CecilCall `
        $afterInvocationFailed `
        "ServerManager.ServerManagerRuntime" `
        "DiscardCharacterCheckpoint") -and
    $null -ne (Get-CecilCall `
        $afterWorker `
        "ServerManager.ServerManagerRuntime" `
        "DiscardCharacterCheckpoint") -and
    $null -ne (Get-CecilCall `
        $processResults `
        "ServerManager.ServerManagerRuntime" `
        "DiscardCharacterCheckpoint") -and
    $null -ne (Get-CecilCall `
        $discardRuntimeCheckpoint `
        "ServerManager.CharacterSnapshotService" `
        "DiscardCheckpoint") -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'A prepared world-save checkpoint was replaced.*?' +
        'DiscardCharacterCheckpoint\(displaced,\s*error\);.*?' +
        'OnWorldSaveCheckpointFailed\(\s*displaced\.OperationId',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'if\s*\(queued\s*>\s*MaximumQueuedWorldSaveWorkerResults\).*?' +
        'DiscardCharacterCheckpoint\(\s*attempt,.*?return;',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'if\s*\(!attempt\.PrimaryWorldSaveSucceeded\).*?' +
        'DiscardCharacterCheckpoint\(\s*attempt,.*?continue;',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    $runtimeSource.Contains("attempt.CharacterCheckpoint = null;")) `
    "An abandoned registered checkpoint is no longer released on prepared displacement, invocation failure, worker queue overflow, or verified world failure."
Assert-True (
    $null -ne $discardSupersededEntry -and
    $runtimeSource.Contains(
        "attempt.CharacterCheckpoint = service.BeginCheckpoint();") -and
    $runtimeSource.Contains(
        "PendingCharacterDiskWrites = new(CharacterStorageLayout.StorageKeyComparer)") -and
    $runtimeSource.Contains(
        "PendingCharacterCheckpointAdoptions = new();") -and
    $runtimeSource.Contains(
        "internal CharacterSnapshotService Service { get; }") -and
    $runtimeSource.Contains(
        "internal PendingCharacterCheckpointEntry Head { get; set; }") -and
    $runtimeSource.Contains(
        "internal PendingCharacterCheckpointEntry? Tail { get; set; }") -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'tail\.Service\.DiscardCheckpointEntry\(' +
        'tail\.Checkpoint,\s*tail\.Entry\);' +
        '\s*pending\.Tail\s*=\s*incoming;') -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'pending\.Head\s*=\s*tail;.*?pending\.Tail\s*=\s*null;.*?' +
        'pending\.FailureCount\s*=\s*0;.*?' +
        'pending\.NextAttemptTimestamp\s*=\s*Stopwatch\.GetTimestamp\(\);',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'new PendingCharacterCheckpointEntry\(\s*attempt\.OperationId,\s*' +
        'service,\s*checkpoint,\s*entry\).*?' +
        'catch\s*\(Exception exception\).*?' +
        'PendingCharacterCheckpointAdoptions\.Add\(.*?' +
        'TryAdoptDueCharacterCheckpoints\(force:\s*false\);',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'AdoptCharacterCheckpointEntry\(pending\.Entry\);\s*' +
        'PendingCharacterCheckpointAdoptions\.Remove\(pending\);.*?' +
        'catch\s*\(Exception exception\).*?' +
        'pending\.RecordFailure\(exception\);',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "Per-key head/tail retention, captured-service ownership, or retryable adoption no longer preserves and coalesces pending generations safely."

$beforeWorldInvocation = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "BeforeWorldSaveInvocation"
$invocationDrain = Get-CecilCall `
    $beforeWorldInvocation `
    "ServerManager.ServerManagerRuntime" `
    "ProcessCompletedWorldSaveCheckpoints"
$invocationEvent = Get-CecilCall `
    $beforeWorldInvocation `
    "ServerManager.Events.ServerEventRuntime" `
    "OnWorldSaveStarted"
$invocationAttemptConstructor = Get-CecilCall `
    $beforeWorldInvocation `
    "ServerManager.ServerManagerRuntime/WorldSaveAttempt" `
    ".ctor"
$invocationAttemptStore = $beforeWorldInvocation.Body.Instructions |
    Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stsfld -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.Name -eq "_invokedWorldSaveAttempt"
    } |
    Select-Object -First 1
$prepareCapture = Get-CecilCall `
    $beforePrepareCore `
    "ServerManager.CharacterSnapshotService" `
    "BeginCheckpoint"
$worldSaveAttemptDefinition = $invocationAttemptConstructor.Operand.DeclaringType.Resolve()
Assert-True ($invocationAttemptConstructor.Operand.Parameters.Count -eq 1 -and
    $invocationAttemptConstructor.Operand.Parameters[0].ParameterType.FullName -eq
        "System.String" -and
    @($worldSaveAttemptDefinition.Properties |
        Where-Object Name -eq "CharacterCheckpointEnabled").Count -eq 0) `
    "A world-save operation must no longer carry a character-checkpoint disable flag."
Assert-True (@($beforePrepareCore.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -in @("get_EnableServerCharacters", "get_CharacterCheckpointEnabled")
    }).Count -eq 0 -and $null -ne $prepareCapture -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'CharacterSnapshotService\? service = _serverCharacterService;\s*' +
        'if \(service != null\)\s*\{[\s\S]*?' +
        'attempt\.CharacterService = service;\s*' +
        'attempt\.CharacterCheckpoint = service\.BeginCheckpoint\(\);')) `
    "Every available character service must remain eligible for the world snapshot cutoff."
$beforeInvocationStart = $runtimeSource.IndexOf(
    "internal static void BeforeWorldSaveInvocation()",
    [StringComparison]::Ordinal)
$beforePrepareStart = $runtimeSource.IndexOf(
    "internal static void BeforeWorldSnapshotPrepared()",
    [StringComparison]::Ordinal)
$beforeWorkerStart = $runtimeSource.IndexOf(
    "internal static void BeforeWorldSaveWorker()",
    $beforePrepareStart,
    [StringComparison]::Ordinal)
Assert-True (
    $beforeInvocationStart -ge 0 -and
    $beforePrepareStart -gt $beforeInvocationStart -and
    $beforePrepareStart -ge 0 -and
    $beforeWorkerStart -gt $beforePrepareStart) `
    "Could not isolate BeforeWorldSnapshotPrepared for source-contract checks."
$beforeInvocationSource = $runtimeSource.Substring(
    $beforeInvocationStart,
    $beforePrepareStart - $beforeInvocationStart)
$beforePrepareSource = $runtimeSource.Substring(
    $beforePrepareStart,
    $beforeWorkerStart - $beforePrepareStart)
$invocationOperationOffset = $beforeInvocationSource.IndexOf(
    "ServerEventRuntime.OnWorldSaveStarted();",
    [StringComparison]::Ordinal)
$invocationAttemptOffset = $beforeInvocationSource.IndexOf(
    "new WorldSaveAttempt(",
    [StringComparison]::Ordinal)
$invocationStoreOffset = $beforeInvocationSource.IndexOf(
    "_invokedWorldSaveAttempt = attempt;",
    [StringComparison]::Ordinal)
$beforePrepareThrows = @(
    $beforePrepare.Body.Instructions |
        Where-Object {
            $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Throw -or
            $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Rethrow
        })
Assert-True (
    $beforeWorldInvocation.ReturnType.FullName -eq "System.Void" -and
    $beforeWorldInvocation.Parameters.Count -eq 0 -and
    $beforePrepare.ReturnType.FullName -eq "System.Void" -and
    $null -eq $invocationDrain -and
    $null -ne $invocationEvent -and
    $null -ne $invocationAttemptConstructor -and
    $null -ne $invocationAttemptStore -and
    $invocationEvent.Offset -lt $invocationAttemptConstructor.Offset -and
    $invocationAttemptConstructor.Offset -lt $invocationAttemptStore.Offset -and
    $null -ne $prepareCapture) `
    ("The world-save observation hooks are no longer void or operation/cutoff " +
     "bookkeeping is missing: invocationReturn=" +
     $beforeWorldInvocation.ReturnType.FullName + ", invocationParameters=" +
     $beforeWorldInvocation.Parameters.Count + ", drain=" +
     ($null -eq $invocationDrain) + ", event=" + ($null -ne $invocationEvent) +
     ", attemptCtor=" + ($null -ne $invocationAttemptConstructor) +
     ", attemptStore=" + ($null -ne $invocationAttemptStore) +
     ", prepareReturn=" + $beforePrepare.ReturnType.FullName +
     ", prepareCapture=" + ($null -ne $prepareCapture) + ".")
Assert-True (
    $invocationOperationOffset -ge 0 -and
    $invocationAttemptOffset -gt $invocationOperationOffset -and
    $invocationStoreOffset -gt $invocationAttemptOffset -and
    -not $beforeInvocationSource.Contains(
        "TryCommitPendingCharacterCheckpoint") -and
    -not $beforeInvocationSource.Contains(
        "DrainPendingCharacterCheckpointBeforeShutdown") -and
    -not $beforeInvocationSource.Contains(
        "ProcessCompletedWorldSaveCheckpoints") -and
    -not $beforeInvocationSource.Contains("return false;") -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'CharacterCheckpointShutdownRetryTicks\s*=\s*' +
        'checked\(5L\s*\*\s*Stopwatch\.Frequency\);') -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'private static void DrainPendingCharacterCheckpointBeforeShutdown\(\).*?' +
        'CharacterCheckpointShutdownRetryTicks\);.*?' +
        'while\s*\(\(PendingCharacterDiskWrites\.Count\s*!=\s*0\s*\|\|' +
        '\s*PendingCharacterCheckpointAdoptions\.Count\s*!=\s*0\).*?' +
        'TryAdoptDueCharacterCheckpoints\(force:\s*true\);.*?' +
        'TryCommitPendingCharacterCheckpoint\(\s*storageKeys\[index\],\s*' +
        'force:\s*true\);.*?CharacterCheckpointShutdownUnresolved.*?' +
        'Vanilla shutdown will continue',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    $beforePrepareThrows.Count -eq 0 -and
    -not $beforePrepareSource.Contains("return false;") -and
    -not $beforePrepareSource.Contains(
        "ProcessCompletedWorldSaveCheckpoints") -and
    -not $beforePrepareSource.Contains(
        "TryCommitPendingCharacterCheckpoint") -and
    $beforePrepareSource.Contains(
        "The vanilla world save will continue without a ") -and
    $beforePrepareSource.Contains(
        '"character cutoff for this operation."') -and
    -not $runtimeSource.Contains("_deferredWorldSaveRetryRequested") -and
    -not $runtimeSource.Contains("TryRunDeferredWorldSaveRetry") -and
    -not $runtimeSource.Contains("_pendingCharacterCheckpointRetry")) `
    "A pending character write or cutoff-capture failure can still cancel/defer a vanilla world save, or shutdown is not a bounded best-effort per-key drain."

$tickProcessResults = Get-CecilCall `
    $tick `
    "ServerManager.ServerManagerRuntime" `
    "ProcessCompletedWorldSaveCheckpoints"
Assert-True (
    $null -ne $tickProcessResults -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'private static int GetCharacterCheckpointRetrySeconds\(int failureCount\)' +
        '.*?<=\s*1\s*=>\s*5,.*?2\s*=>\s*15,.*?' +
        '3\s*=>\s*30,.*?_\s*=>\s*60',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'internal static void ProcessCompletedWorldSaveCheckpoints\(\).*?' +
        'TryCommitDueCharacterCheckpoints\(\);\s*\}',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    ([Text.RegularExpressions.Regex]::Matches(
        $runtimeSource,
        'force:\s*false')).Count -eq 3 -and
    ([Text.RegularExpressions.Regex]::Matches(
        $runtimeSource,
        'force:\s*true')).Count -eq 2 -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $runtimeSource,
        'TryCommitPendingCharacterCheckpoint\(\s*' +
        'orderedStorageKeys\[index\],\s*force:\s*false\);',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "Main-thread retry processing or the 5/15/30/60-second per-identity backoff changed."

# StopAll hooks are observational and cannot suppress vanilla shutdown. The
# prefix retains server-role state despite peer failures; the finalizer always
# preserves the original exception and runs each cleanup stage independently.
$shutdownPrefix = Get-CecilMethod `
    "ServerManager.NetworkShutdownCleanupPatch" `
    "Prefix"
$shutdownFinalizer = Get-CecilMethod `
    "ServerManager.NetworkShutdownCleanupPatch" `
    "Finalizer"
$beforeShutdown = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "BeforeNetworkShutdown"
$afterShutdown = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "AfterNetworkShutdown"
$runShutdownStep = Get-CecilMethod `
    "ServerManager.ServerManagerRuntime" `
    "RunNetworkShutdownStep"
$prefixDispatch = Get-CecilCall `
    $shutdownPrefix `
    "ServerManager.ServerManagerRuntime" `
    "BeforeNetworkShutdown"
$prefixFailureReport = Get-CecilCall `
    $shutdownPrefix `
    "ServerManager.ServerManagerRuntime" `
    "ReportNetworkShutdownHookFailure"
$finalizerDispatch = Get-CecilCall `
    $shutdownFinalizer `
    "ServerManager.ServerManagerRuntime" `
    "AfterNetworkShutdown"
$finalizerFailureReport = Get-CecilCall `
    $shutdownFinalizer `
    "ServerManager.ServerManagerRuntime" `
    "ReportNetworkShutdownHookFailure"
$peerCleanupCall = Get-CecilCall `
    $beforeShutdown `
    "ServerManager.ServerManagerRuntime" `
    "CleanupPeer"
$beforeShutdownFailureReports = Get-CallsByType `
    $beforeShutdown `
    "ServerManager.ServerManagerRuntime" `
    "ReportNetworkShutdownHookFailure"
$shutdownStepCalls = Get-CallsByType `
    $afterShutdown `
    "ServerManager.ServerManagerRuntime" `
    "RunNetworkShutdownStep"
$shutdownStepAction = Get-CecilCall `
    $runShutdownStep `
    "System.Action" `
    "Invoke"
$shutdownStepFailureReport = Get-CecilCall `
    $runShutdownStep `
    "ServerManager.ServerManagerRuntime" `
    "ReportNetworkShutdownHookFailure"
Assert-True (
    $shutdownPrefix.ReturnType.FullName -eq "System.Void" -and
    $shutdownFinalizer.ReturnType.FullName -eq "System.Exception" -and
    $shutdownFinalizer.Parameters.Count -eq 3 -and
    $beforeShutdown.ReturnType.FullName -eq "System.Boolean" -and
    $null -ne $prefixDispatch -and
    $null -ne $prefixFailureReport -and
    $null -ne $finalizerDispatch -and
    $null -ne $finalizerFailureReport -and
    $null -ne $peerCleanupCall -and
    $beforeShutdownFailureReports.Count -ge 2 -and
    $shutdownStepCalls.Count -eq 18 -and
    $null -ne $shutdownStepAction -and
    $null -ne $shutdownStepFailureReport) `
    "StopAll hooks can suppress vanilla, lose server-role state, or no longer isolate peer/staged cleanup failures."
$shutdownPatchStart = $patchSource.IndexOf(
    "internal static class NetworkShutdownCleanupPatch",
    [StringComparison]::Ordinal)
$shutdownPatchEnd = $patchSource.IndexOf(
    "[HarmonyPatch(",
    $shutdownPatchStart + 1,
    [StringComparison]::Ordinal)
Assert-True (
    $shutdownPatchStart -ge 0 -and
    $shutdownPatchEnd -gt $shutdownPatchStart) `
    "Could not isolate the StopAll cleanup patch."
$shutdownPatchSource = $patchSource.Substring(
    $shutdownPatchStart,
    $shutdownPatchEnd - $shutdownPatchStart)
Assert-True (
    [Text.RegularExpressions.Regex]::IsMatch(
        $shutdownPatchSource,
        'private static void Prefix\(ZNet __instance, out bool __state\)' +
        '.*?__state\s*=\s*false;.*?try\s*\{' +
        '.*?__state\s*=\s*ServerManagerRuntime\.BeforeNetworkShutdown' +
        '\(__instance\);.*?catch\s*\(Exception exception\)\s*when\s*\(' +
        '\s*!IntegrityCanonical\.IsFatal\(exception\)\).*?' +
        'ReportNetworkShutdownHookFailure\(\s*"StopAll prefix",',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $shutdownPatchSource,
        'private static Exception\? Finalizer\(.*?' +
        'try\s*\{.*?ServerManagerRuntime\.AfterNetworkShutdown\(\s*' +
        '__instance,\s*__state,\s*__exception\s*\);.*?' +
        'catch\s*\(Exception exception\)\s*when\s*\(' +
        '\s*!IntegrityCanonical\.IsFatal\(exception\)\).*?' +
        'ReportNetworkShutdownHookFailure\(\s*"StopAll finalizer",',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    $shutdownPatchSource.Contains("return __exception;")) `
    "StopAll prefix/finalizer lost their outer nonfatal guards or the finalizer no longer returns the original exception."

$prefixInstructions = @($shutdownPrefix.Body.Instructions)
$prefixInitializesStateFalse = $false
for ($instructionIndex = 0;
    $instructionIndex -le $prefixInstructions.Count - 3;
    ++$instructionIndex) {
    if ($prefixInstructions[$instructionIndex].OpCode.Name -eq "ldarg.1" -and
        $prefixInstructions[$instructionIndex + 1].OpCode.Name -eq
            "ldc.i4.0" -and
        $prefixInstructions[$instructionIndex + 2].OpCode.Name -eq
            "stind.i1") {
        $prefixInitializesStateFalse = $true
        break
    }
}
$prefixDispatchHandler = @($shutdownPrefix.Body.ExceptionHandlers |
    Where-Object {
        $_.TryStart.Offset -le $prefixDispatch.Offset -and
        $_.TryEnd.Offset -gt $prefixDispatch.Offset -and
        $_.HandlerStart.Offset -le $prefixFailureReport.Offset -and
        $_.HandlerEnd.Offset -gt $prefixFailureReport.Offset
    })
$finalizerDispatchHandler = @($shutdownFinalizer.Body.ExceptionHandlers |
    Where-Object {
        $_.TryStart.Offset -le $finalizerDispatch.Offset -and
        $_.TryEnd.Offset -gt $finalizerDispatch.Offset -and
        $_.HandlerStart.Offset -le $finalizerFailureReport.Offset -and
        $_.HandlerEnd.Offset -gt $finalizerFailureReport.Offset
    })
$finalizerInstructions = @($shutdownFinalizer.Body.Instructions)
$finalizerReturnsOriginalArgument = $false
for ($instructionIndex = 0;
    $instructionIndex -lt $finalizerInstructions.Count;
    ++$instructionIndex) {
    if ($finalizerInstructions[$instructionIndex].OpCode.Name -ne "ret") {
        continue
    }

    if ($instructionIndex -ge 1 -and
        $finalizerInstructions[$instructionIndex - 1].OpCode.Name -eq
            "ldarg.2") {
        $finalizerReturnsOriginalArgument = $true
        break
    }

    if ($instructionIndex -ge 1 -and
        $finalizerInstructions[$instructionIndex - 1].OpCode.Name -eq
            "ldloc.2") {
        for ($storeIndex = 1;
            $storeIndex -lt $instructionIndex;
            ++$storeIndex) {
            if ($finalizerInstructions[$storeIndex - 1].OpCode.Name -eq
                    "ldarg.2" -and
                $finalizerInstructions[$storeIndex].OpCode.Name -eq
                    "stloc.2") {
                $finalizerReturnsOriginalArgument = $true
                break
            }
        }
    }
}
Assert-True (
    $shutdownPrefix.Parameters.Count -eq 2 -and
    $shutdownPrefix.Parameters[1].Name -eq "__state" -and
    $shutdownPrefix.Parameters[1].ParameterType.IsByReference -and
    $prefixInitializesStateFalse -and
    $prefixDispatchHandler.Count -ge 1 -and
    $shutdownFinalizer.Parameters[2].Name -eq "__exception" -and
    $finalizerDispatchHandler.Count -ge 1 -and
    $finalizerReturnsOriginalArgument) `
    "The StopAll IL can suppress vanilla after hook failure or replace the original exception."

$beforeShutdownStart = $runtimeSource.IndexOf(
    "internal static bool BeforeNetworkShutdown(",
    [StringComparison]::Ordinal)
$afterShutdownStart = $runtimeSource.IndexOf(
    "internal static void AfterNetworkShutdown(",
    [StringComparison]::Ordinal)
$afterShutdownEnd = $runtimeSource.IndexOf(
    "internal static void ReportNetworkShutdownHookFailure(",
    $afterShutdownStart,
    [StringComparison]::Ordinal)
Assert-True (
    $beforeShutdownStart -ge 0 -and
    $afterShutdownStart -gt $beforeShutdownStart -and
    $afterShutdownStart -ge 0 -and
    $afterShutdownEnd -gt $afterShutdownStart) `
    "Could not isolate AfterNetworkShutdown for exception-path checks."
$afterShutdownSource = $runtimeSource.Substring(
    $afterShutdownStart,
    $afterShutdownEnd - $afterShutdownStart)
$beforeShutdownSource = $runtimeSource.Substring(
    $beforeShutdownStart,
    $afterShutdownStart - $beforeShutdownStart)
$shutdownBranches = [Text.RegularExpressions.Regex]::Match(
    $afterShutdownSource,
    'if\s*\(shutdownException\s*!=\s*null\)\s*\{' +
    '(?<failure>.*?)\n\s*return;\s*\}\s*' +
    'if\s*\(wasServer\)\s*\{(?<success>.*?)\n\s*return;\s*\}',
    [Text.RegularExpressions.RegexOptions]::Singleline)
Assert-True ($shutdownBranches.Success) `
    "Could not distinguish failed and successful StopAll cleanup branches."
$failedShutdownSource = $shutdownBranches.Groups["failure"].Value
$successfulShutdownSource = $shutdownBranches.Groups["success"].Value
$successfulDrainOffset = $successfulShutdownSource.IndexOf(
    '"completed StopAll world result drain"',
    [StringComparison]::Ordinal)
$successfulRetryOffset = $successfulShutdownSource.IndexOf(
    '"completed StopAll character retry drain"',
    [StringComparison]::Ordinal)
$successfulPublishOffset = $successfulShutdownSource.IndexOf(
    '"server shutdown event publication"',
    [StringComparison]::Ordinal)
$successfulDisposeOffset = $successfulShutdownSource.IndexOf(
    '"character service disposal"',
    [StringComparison]::Ordinal)
$successfulHostRestoreOffset = $successfulShutdownSource.IndexOf(
    '"local host character restoration"',
    [StringComparison]::Ordinal)
$successfulHostResetOffset = $successfulShutdownSource.IndexOf(
    '"local host lifecycle reset"',
    [StringComparison]::Ordinal)
$successfulDataRootOffset = $successfulShutdownSource.IndexOf(
    '"server data-root release"',
    [StringComparison]::Ordinal)
Assert-True (
    [Text.RegularExpressions.Regex]::IsMatch(
        $beforeShutdownSource,
        'wasServer\s*=\s*znet\.IsServer\(\);.*?' +
        'ZNetPeer\[\]\s+peers\s*=\s*znet\.GetPeers\(\)\.ToArray\(\);' +
        '.*?foreach\s*\(ZNetPeer peer in peers\).*?' +
        'try\s*\{\s*CleanupPeer\(znet,\s*peer\.m_rpc\);\s*\}' +
        '.*?catch\s*\(Exception exception\)\s*when\s*\(' +
        '\s*!IntegrityCanonical\.IsFatal\(exception\)\).*?' +
        '"pre-shutdown peer cleanup".*?' +
        'catch\s*\(Exception exception\)\s*when\s*\(' +
        '\s*!IntegrityCanonical\.IsFatal\(exception\)\).*?' +
        '"pre-shutdown peer enumeration".*?return wasServer;',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    $failedShutdownSource.Contains(
        '"failed StopAll world result drain"') -and
    $failedShutdownSource.Contains(
        '"failed StopAll character retry drain"') -and
    -not $failedShutdownSource.Contains(
        '"server shutdown event publication"') -and
    -not $failedShutdownSource.Contains(
        '"player activity log shutdown"') -and
    -not $failedShutdownSource.Contains(
        '"character service disposal"') -and
    -not $failedShutdownSource.Contains('"server data-root release"') -and
    -not $failedShutdownSource.Contains('"world checkpoint state reset"') -and
    ([Text.RegularExpressions.Regex]::Matches(
        $failedShutdownSource,
        'RunNetworkShutdownStep\(')).Count -eq 2 -and
    ([Text.RegularExpressions.Regex]::Matches(
        $successfulShutdownSource,
        'RunNetworkShutdownStep\(')).Count -eq 12 -and
    $successfulDrainOffset -ge 0 -and
    $successfulRetryOffset -gt $successfulDrainOffset -and
    $successfulPublishOffset -gt $successfulRetryOffset -and
    $successfulHostRestoreOffset -gt $successfulPublishOffset -and
    $successfulHostResetOffset -gt $successfulHostRestoreOffset -and
    $successfulDisposeOffset -gt $successfulHostResetOffset -and
    $successfulDataRootOffset -gt $successfulDisposeOffset -and
    $successfulShutdownSource.Contains(
        "unresolved character checkpoint storage key(s)") -and
    $successfulShutdownSource.Contains(
        '"save was not cancelled; inspect character backups before "') -and
    $successfulShutdownSource.Contains(
        "inspect character backups before ")) `
    "Peer cleanup is no longer isolated, failed StopAll can publish/dispose state, or successful shutdown no longer runs drain then staged best-effort cleanup."

$runShutdownStepStart = $runtimeSource.IndexOf(
    "private static void RunNetworkShutdownStep(",
    [StringComparison]::Ordinal)
$runShutdownStepEnd = $runtimeSource.IndexOf(
    "private static void TryLogNetworkShutdownError(",
    $runShutdownStepStart,
    [StringComparison]::Ordinal)
Assert-True (
    $runShutdownStepStart -ge 0 -and
    $runShutdownStepEnd -gt $runShutdownStepStart) `
    "Could not isolate RunNetworkShutdownStep."
$runShutdownStepSource = $runtimeSource.Substring(
    $runShutdownStepStart,
    $runShutdownStepEnd - $runShutdownStepStart)
Assert-True (
    [Text.RegularExpressions.Regex]::IsMatch(
        $runShutdownStepSource,
        'try\s*\{\s*action\(\);\s*\}\s*' +
        'catch\s*\(Exception exception\)\s*when\s*\(' +
        '\s*!IntegrityCanonical\.IsFatal\(exception\)\)\s*\{' +
        '.*?ReportNetworkShutdownHookFailure\(stage,\s*exception\);',
        [Text.RegularExpressions.RegexOptions]::Singleline) -and
    $successfulShutdownSource.Contains("_integrityService = null;") -and
    $successfulShutdownSource.Contains("_serverCharacterService = null;") -and
    $successfulShutdownSource.Contains('"server save admission cleanup"') -and
    $successfulShutdownSource.Contains('"world checkpoint state reset"')) `
    "One shutdown cleanup exception can stop later dispose/release/reset stages or leave disposed service references published."

# The integration save command must distinguish an immediately failed real
# invocation from a listen-host coroutine that has not invoked SaveWorld yet.
$executeEventCommand = Get-CecilMethod `
    "ServerManager.Events.ServerEventRuntime" `
    "ExecuteCommand"
$requestWorldSave = Get-CecilCall `
    $executeEventCommand `
    "ZNet" `
    "SaveWorldAndPlayerProfiles"
$saveCommandStart = $eventSource.IndexOf(
    "case ServerEventCommandKind.Save:",
    [StringComparison]::Ordinal)
$saveCommandEnd = $eventSource.IndexOf(
    "case ServerEventCommandKind.Announce:",
    $saveCommandStart,
    [StringComparison]::Ordinal)
Assert-True (
    $null -ne $requestWorldSave -and
    $saveCommandStart -ge 0 -and
    $saveCommandEnd -gt $saveCommandStart) `
    "Could not isolate the integration save-command path."
$saveCommandSource = $eventSource.Substring(
    $saveCommandStart,
    $saveCommandEnd - $saveCommandStart)
Assert-True ($saveCommandSource.Contains('DescribeWorldSaveRequest(previousOperationId)')) `
    "The API save must use the shared observed-operation result, also used by raw RCON."
$saveDescriptionStart = $eventSource.IndexOf('internal static ServerManagerCommandResult DescribeWorldSaveRequest(', [StringComparison]::Ordinal)
$saveDescriptionEnd = $eventSource.IndexOf('internal static ServerManagerCommandResult ExecuteConsoleKick(', $saveDescriptionStart, [StringComparison]::Ordinal)
Assert-True ($saveDescriptionStart -ge 0 -and $saveDescriptionEnd -gt $saveDescriptionStart) "Could not isolate save dispatch observation."
$saveCommandSource = $eventSource.Substring($saveDescriptionStart, $saveDescriptionEnd - $saveDescriptionStart)
$scheduledSaveBranch = [Text.RegularExpressions.Regex]::Match(
    $saveCommandSource,
    'if\s*\(server != null && !server\.IsDedicated\(\)\)\s*\{\s*' +
    '(?<result>return Success\(\s*"save_scheduled",.*?\);)\s*\}',
    [Text.RegularExpressions.RegexOptions]::Singleline)
$saveBlockedOffset = $saveCommandSource.IndexOf(
    '"save_blocked"',
    [StringComparison]::Ordinal)
$saveRequestedOffset = $saveCommandSource.IndexOf(
    '"save_requested"',
    [StringComparison]::Ordinal)
Assert-True (
    $scheduledSaveBranch.Success -and
    -not $scheduledSaveBranch.Groups["result"].Value.Contains(
        "operationId") -and
    -not $scheduledSaveBranch.Groups["result"].Value.Contains("Fields(") -and
    $saveCommandSource.Contains(
        "requested.State == ServerManagerSaveState.Failed") -and
    $saveBlockedOffset -ge 0 -and
    $saveRequestedOffset -gt $saveBlockedOffset -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $saveCommandSource,
        'return Failure\(\s*"save_blocked",.*?' +
        'operationId,\s*Fields\(\s*"operation_id",\s*operationId,.*?' +
        '"completion_scope",\s*"failed_before_world_disk"',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "Save command no longer returns save_blocked with the failed operation ID, or listen-host scheduling incorrectly claims an operation ID/failure."

# Begin/per-entry commit and Dispose share one checkpoint gate. Dispose marks itself
# first, then waits; callers recheck after entering so neither direction of the
# race can clear live state during repository I/O or continue after disposal.
$disposeService = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "Dispose"
$finalizeInitialWrapper = Get-CecilMethod `
    "ServerManager.CharacterSnapshotService" `
    "FinalizePendingInitialSnapshot"
$openSessionCoreCall = Get-CecilCall `
    $openSession `
    "ServerManager.CharacterSnapshotService" `
    "OpenOrCreateSessionCore"
$finalizeInitialCoreCall = Get-CecilCall `
    $finalizeInitialWrapper `
    "ServerManager.CharacterSnapshotService" `
    "FinalizePendingInitialSnapshotCore"
$openDisposedChecks = Get-CallsByType `
    $openSession `
    "ServerManager.CharacterSnapshotService" `
    "ThrowIfDisposed"
$finalizeInitialDisposedChecks = Get-CallsByType `
    $finalizeInitialWrapper `
    "ServerManager.CharacterSnapshotService" `
    "ThrowIfDisposed"
$beginDisposedChecks = Get-CallsByType `
    $beginServiceCheckpoint `
    "ServerManager.CharacterSnapshotService" `
    "ThrowIfDisposed"
$commitDisposedChecks = Get-CallsByType `
    $commitCheckpointEntry `
    "ServerManager.CharacterSnapshotService" `
    "ThrowIfDisposed"
$disposeExchange = Get-CecilCall `
    $disposeService `
    "System.Threading.Interlocked" `
    "Exchange"
$disposeMonitorEnter = Get-CecilCall `
    $disposeService `
    "System.Threading.Monitor" `
    "Enter"
$openMonitorEnter = Get-CecilCall `
    $openSession `
    "System.Threading.Monitor" `
    "Enter"
$finalizeInitialMonitorEnter = Get-CecilCall `
    $finalizeInitialWrapper `
    "System.Threading.Monitor" `
    "Enter"
$checkpointGateLoads = @{}
foreach ($method in @(
        $openSession,
        $finalizeInitialWrapper,
        $beginServiceCheckpoint,
        $commitCheckpointEntry,
        $disposeService)) {
    $checkpointGateLoads[$method.FullName] = @(
        $method.Body.Instructions |
            Where-Object {
                $_.Operand -is [Mono.Cecil.FieldReference] -and
                $_.Operand.Name -eq "_checkpointCommitGate"
            }).Count
}
Assert-True (
    $openDisposedChecks.Count -ge 2 -and
    $finalizeInitialDisposedChecks.Count -ge 2 -and
    $beginDisposedChecks.Count -ge 2 -and
    $commitDisposedChecks.Count -ge 2 -and
    $null -ne $disposeExchange -and
    $null -ne $disposeMonitorEnter -and
    $null -ne $openMonitorEnter -and
    $null -ne $finalizeInitialMonitorEnter -and
    $null -ne $openSessionCoreCall -and
    $null -ne $finalizeInitialCoreCall -and
    $openDisposedChecks[0].Offset -lt $openMonitorEnter.Offset -and
    $openMonitorEnter.Offset -lt $openDisposedChecks[1].Offset -and
    $openDisposedChecks[1].Offset -lt $openSessionCoreCall.Offset -and
    $finalizeInitialDisposedChecks[0].Offset -lt
        $finalizeInitialMonitorEnter.Offset -and
    $finalizeInitialMonitorEnter.Offset -lt
        $finalizeInitialDisposedChecks[1].Offset -and
    $finalizeInitialDisposedChecks[1].Offset -lt
        $finalizeInitialCoreCall.Offset -and
    $disposeExchange.Offset -lt $disposeMonitorEnter.Offset -and
    $checkpointGateLoads[$openSession.FullName] -gt 0 -and
    $checkpointGateLoads[$finalizeInitialWrapper.FullName] -gt 0 -and
    $checkpointGateLoads[$beginServiceCheckpoint.FullName] -gt 0 -and
    $checkpointGateLoads[$commitCheckpointEntry.FullName] -gt 0 -and
    $checkpointGateLoads[$disposeService.FullName] -gt 0) `
    "Session open/first-join finalization/checkpoint begin/commit and Dispose no longer close both sides of the service-disposal race with the shared commit gate."

# Public event status must distinguish world-only from a completed retained-shadow checkpoint.
$saveStateType = $plugin.GetType(
    "ServerManager.Events.ServerManagerSaveState",
    $true)
$commitScopeType = $plugin.GetType(
    "ServerManager.Events.ServerManagerCharacterCommitScope",
    $true)
Assert-True (
    [int][Enum]::Parse($saveStateType, "WorldDiskCompleted") -eq 2 -and
    [int][Enum]::Parse($saveStateType, "CheckpointCompleted") -eq 4 -and
    [int][Enum]::Parse($commitScopeType, "NotIncluded") -eq 0 -and
    [int][Enum]::Parse(
        $commitScopeType,
        "AllRetainedShadowsAtCutoff") -eq 1 -and
    [int][Enum]::Parse(
        $commitScopeType,
        "PartialRetainedShadowsAtCutoff") -eq 2 -and
    $eventSource.Contains('"world_disk_and_retained_characters"') -and
    $eventSource.Contains(
        '"world_disk_with_partial_retained_characters"') -and
    $eventSource.Contains('"world_disk_only"') -and
    $eventSource.Contains('"captured_character_count"') -and
    $eventSource.Contains('"persisted_character_count"') -and
    $eventSource.Contains('"pending_character_count"') -and
    [Text.RegularExpressions.Regex]::IsMatch(
        $eventSource,
        'ServerManagerSaveState completedState\s*=\s*' +
        'characterCommitScope\s*==.*?' +
        'AllRetainedShadowsAtCutoff.*?' +
        '\?\s*ServerManagerSaveState\.CheckpointCompleted\s*' +
        ':\s*ServerManagerSaveState\.WorldDiskCompleted;',
        [Text.RegularExpressions.RegexOptions]::Singleline)) `
    "Save events no longer report their exact world/character completion scope."

# Exercise immutable cutoff objects, post-cutoff generation preservation,
# durable N -> N+K per-entry writes, idempotency, and failure isolation.
$optionsType = $plugin.GetType("ServerManager.CharacterStorageOptions", $true)
$layoutType = $plugin.GetType("ServerManager.CharacterStorageLayout", $true)
$codecType = $plugin.GetType("ServerManager.CharacterEnvelopeCodec", $true)
$profileCodecType = $plugin.GetType(
    "ServerManager.ValheimPlayerProfileCodec",
    $true)
$policyModeType = $plugin.GetType(
    "ServerManager.CharacterSemanticPolicyMode",
    $true)
$policyType = $plugin.GetType("ServerManager.CharacterSemanticPolicy", $true)
$evaluatorType = $plugin.GetType(
    "ServerManager.CharacterSemanticEvaluator",
    $true)
$validatorType = $plugin.GetType(
    "ServerManager.CharacterSemanticRevisionValidator",
    $true)
$repositoryType = $plugin.GetType("ServerManager.CharacterRepository", $true)
$identityType = $plugin.GetType("ServerManager.CharacterIdentity", $true)
$envelopeType = $plugin.GetType("ServerManager.CharacterEnvelope", $true)
$envelopeKindType = $plugin.GetType(
    "ServerManager.CharacterEnvelopeKind",
    $true)
$semanticType = $plugin.GetType(
    "ServerManager.CharacterSemanticSnapshot",
    $true)
$semanticSkillType = $plugin.GetType(
    "ServerManager.CharacterSemanticSkillState",
    $true)
$semanticItemType = $plugin.GetType(
    "ServerManager.CharacterSemanticItemState",
    $true)
$checkpointEntryType = $plugin.GetType(
    "ServerManager.CharacterCheckpointEntry",
    $true)
$checkpointBatchType = $plugin.GetType(
    "ServerManager.CharacterCheckpointBatch",
    $true)
$liveType = $plugin.GetType(
    "ServerManager.CharacterSnapshotService+CharacterLiveSnapshot",
    $true)
$serviceType = $plugin.GetType(
    "ServerManager.CharacterSnapshotService",
    $true)
$resolverType = $plugin.GetType(
    "ServerManager.CharacterPeerIdentityResolver",
    $true)
$keyProviderType = $plugin.GetType(
    "ServerManager.CharacterStorageKeyProvider",
    $true)

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "smcp-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$service = $null
try {
    $options = [Activator]::CreateInstance($optionsType)
    $layout = New-ReflectedInstance $layoutType ([object[]]@($temporaryRoot))
    $codec = New-ReflectedInstance $codecType ([object[]]@($options))
    $profileCodec = New-ReflectedInstance `
        $profileCodecType `
        ([object[]]@($options))
    $disabledMode = [Enum]::Parse($policyModeType, "Disabled")
    $policy = New-ReflectedInstance `
        $policyType `
        ([object[]]@(
            $disabledMode,
            "",
            [single]10000,
            [single]10000,
            [single]10000,
            [single]100,
            [single]100))
    $evaluator = New-ReflectedInstance `
        $evaluatorType `
        ([object[]]@($policy))
    $validatorConstructor = $validatorType.GetConstructors(
        [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic) |
        Select-Object -First 1
    $validator = $validatorConstructor.Invoke(
        [object[]]@($profileCodec, $evaluator))
    $repositoryConstructor = $repositoryType.GetConstructors(
        [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic) |
        Select-Object -First 1
    $repository = $repositoryConstructor.Invoke(
        [object[]]@($layout, $options, $profileCodec, $validator, $null))

    $supportedVersion = [int]$profileCodecType.GetField(
        "SupportedPlayerProfileVersion",
        [Reflection.BindingFlags]::Static -bor
        [Reflection.BindingFlags]::Public -bor
        [Reflection.BindingFlags]::NonPublic).GetRawConstantValue()
    $snapshotKind = [Enum]::Parse($envelopeKindType, "Snapshot")
    $createEnvelope = $envelopeType.GetMethods(
        [Reflection.BindingFlags]::Static -bor
        [Reflection.BindingFlags]::Public) |
        Where-Object { $_.Name -eq "Create" -and $_.GetParameters().Count -eq 8 } |
        Select-Object -First 1
    $emptySkills = [Array]::CreateInstance($semanticSkillType, 0)
    $emptyItems = [Array]::CreateInstance($semanticItemType, 0)
    $emptyKeys = [string[]]@()
    $semantic = New-ReflectedInstance `
        $semanticType `
        ([object[]]@(
            $false,
            [single]0,
            [single]0,
            [single]0,
            $emptySkills,
            $emptyItems,
            $emptyKeys))
    $entryConstructor = $checkpointEntryType.GetConstructors(
        [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic) |
        Where-Object { $_.GetParameters().Count -eq 4 } |
        Select-Object -First 1
    $batchConstructor = $checkpointBatchType.GetConstructors(
        [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic) |
        Select-Object -First 1
    $liveConstructor = $liveType.GetConstructors(
        [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic) |
        Select-Object -First 1
    $persistCheckpointEntry = Get-InstanceMethod `
        $repositoryType `
        "PersistCheckpointEntry" `
        2
    $loadOrCreate = Get-InstanceMethod $repositoryType "LoadOrCreate" 5
    $load = Get-InstanceMethod $repositoryType "Load" 2

    $zPackageType = $gameAssembly.GetType("ZPackage", $true)
    $zWriteInt = $zPackageType.GetMethod("Write", [Type[]]@([int]))
    $zWriteSingle = $zPackageType.GetMethod("Write", [Type[]]@([single]))
    $zWriteBool = $zPackageType.GetMethod("Write", [Type[]]@([bool]))
    $zWriteString = $zPackageType.GetMethod("Write", [Type[]]@([string]))
    $zWriteLong = $zPackageType.GetMethod("Write", [Type[]]@([long]))
    $zGetArray = $zPackageType.GetMethod("GetArray", [Type[]]@())

    function New-CheckpointProfilePayload {
        param(
            [string]$CharacterName,
            [long]$PlayerId,
            [byte]$Marker
        )

        $stream = [IO.MemoryStream]::new()
        $writer = [IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([int]46); [Valheim107Fixture]::Statistics($writer)
            $writer.Write($false); $writer.Write([int]0)
            $writer.Write($CharacterName); $writer.Write($PlayerId); $writer.Write($Marker)
            $writer.Flush(); return ,$stream.ToArray()
        } finally { $writer.Dispose(); $stream.Dispose() }

    }

    function New-Envelope {
        param(
            [object]$Identity,
            [long]$Revision,
            [long]$BaseRevision,
            [Guid]$SessionId,
            [byte]$Marker
        )
        $playerId = if ($Identity.CharacterName -eq "ShadowAlpha") {
            [long]101
        }
        else {
            [long]102
        }
        [byte[]]$payload = New-CheckpointProfilePayload `
            $Identity.CharacterName $playerId $Marker
        return $createEnvelope.Invoke(
            $null,
            [object[]]@(
                $snapshotKind,
                $Revision,
                $BaseRevision,
                $SessionId,
                $Identity,
                [DateTime]::UtcNow,
                $supportedVersion,
                $payload))
    }

    function New-Entry {
        param(
            [string]$StorageKey,
            [object]$Identity,
            [object]$Durable,
            [object]$Snapshot
        )
        return $entryConstructor.Invoke(
            [object[]]@(
                $StorageKey,
                $Identity,
                $Durable,
                $Snapshot))
    }

    function New-EntryList {
        $listType = [Collections.Generic.List``1].MakeGenericType(
            $checkpointEntryType)
        return ,([Activator]::CreateInstance($listType))
    }

    function Persist-Entry {
        param([object]$Entry, [object]$ExpectedDurableBase)
        $arguments = [object[]]@($Entry, $ExpectedDurableBase)
        try {
            return [string]$persistCheckpointEntry.Invoke($repository, $arguments)
        }
        catch {
            $failure = $_.Exception
            while ($null -ne $failure.InnerException) {
                $failure = $failure.InnerException
            }
            throw ($failure.ToString())
        }
    }

    $identityA = New-ReflectedInstance `
        $identityType `
        ([object[]]@("steamworks:76561198000000001", "ShadowAlpha"))
    $identityB = New-ReflectedInstance `
        $identityType `
        ([object[]]@("steamworks:76561198000000002", "ShadowBeta"))
    $keyA = "Steam_76561198000000001_shadowalpha"
    $keyB = "Steam_76561198000000002_shadowbeta"
    $sessionA = [Guid]::NewGuid()
    $sessionB = [Guid]::NewGuid()
    $factoryA = [Func[byte[]]]{
        New-CheckpointProfilePayload "ShadowAlpha" ([long]101) 0x11
    }
    $factoryB = [Func[byte[]]]{
        New-CheckpointProfilePayload "ShadowBeta" ([long]102) 0x21
    }
    $storedA = $loadOrCreate.Invoke(
        $repository,
        [object[]]@(
            $identityA,
            $keyA,
            $sessionA,
            $factoryA,
            $supportedVersion))
    $storedB = $loadOrCreate.Invoke(
        $repository,
        [object[]]@(
            $identityB,
            $keyB,
            $sessionB,
            $factoryB,
            $supportedVersion))
    $durableA1 = Get-PropertyValue $storedA "Envelope"
    $durableB1 = Get-PropertyValue $storedB "Envelope"

    $snapshotA4 = New-Envelope $identityA 4 3 $sessionA 0x14
    $entryA4 = New-Entry $keyA $identityA $durableA1 $snapshotA4
    $firstBatch = New-EntryList
    $firstBatch.Add($entryA4)

    # CharacterCheckpointBatch owns its cutoff collection.
    $immutableBatch = $batchConstructor.Invoke(
        [object[]]@(
            [Guid]::NewGuid(),
            [Guid]::NewGuid(),
            [DateTime]::UtcNow,
            $firstBatch))
    $firstBatch.Clear()
    Assert-True ((Get-PropertyValue $immutableBatch "Count") -eq 1) `
        "A checkpoint batch changed after its source list was mutated."
    $immutableEntries = Get-PropertyValue $immutableBatch "Entries"
    Assert-True (
        (Get-PropertyValue (
            Get-PropertyValue $immutableEntries[0] "Snapshot") "Revision") -eq 4) `
        "The immutable checkpoint did not retain its captured revision."

    $firstWarning = Persist-Entry $entryA4 $durableA1
    $loadedA4 = $load.Invoke($repository, [object[]]@($identityA, $keyA))
    $diskA4 = Get-PropertyValue $loadedA4 "Envelope"
    $durableA4 = $snapshotA4
    Assert-True (
        [string]::IsNullOrEmpty($firstWarning) -and
        (Get-PropertyValue $diskA4 "Revision") -eq 1 -and
        (Get-PropertyValue $diskA4 "BaseRevision") -eq 0 -and
        (Test-Bytes $diskA4.GetPayloadCopy() $snapshotA4.GetPayloadCopy())) `
        "A checkpoint did not persist the revision-4 payload under a fresh process-local disk baseline."

    $backupDirectoryA = $layout.GetAccountDirectory($keyA)
    $backupCountBeforeRetry = @(
        Get-ChildItem -LiteralPath $backupDirectoryA -File -Filter "$keyA.*.fch" -ErrorAction SilentlyContinue
    ).Count
    $retryWarning = Persist-Entry $entryA4 $durableA1
    $backupCountAfterRetry = @(
        Get-ChildItem -LiteralPath $backupDirectoryA -File -Filter "$keyA.*.fch" -ErrorAction SilentlyContinue
    ).Count
    Assert-True (
        [string]::IsNullOrEmpty($retryWarning) -and
        $backupCountAfterRetry -eq $backupCountBeforeRetry) `
        "Retrying an identical checkpoint rewrote disk or rotated another backup."

    # One invalid identity must not prevent a separate valid identity from
    # advancing. This is the repository seam used by the runtime's per-key loop.
    $snapshotA6 = New-Envelope $identityA 6 5 $sessionA 0x16
    $validA6 = New-Entry $keyA $identityA $durableA4 $snapshotA6
    $fakeDurableB1 = New-Envelope $identityB 1 0 $sessionB 0x2f
    $snapshotB4 = New-Envelope $identityB 4 3 $sessionB 0x24
    $invalidB4 = New-Entry `
        $keyB `
        $identityB `
        $fakeDurableB1 `
        $snapshotB4
    Invoke-ExpectCharacterStorageFailure `
        $persistCheckpointEntry `
        $repository `
        ([object[]]@($invalidB4, $fakeDurableB1))
    $validA6Warning = Persist-Entry $validA6 $durableA4
    $afterIsolatedFailureA = $load.Invoke(
        $repository,
        [object[]]@($identityA, $keyA))
    $afterIsolatedFailureB = $load.Invoke(
        $repository,
        [object[]]@($identityB, $keyB))
    $diskA6 = Get-PropertyValue $afterIsolatedFailureA "Envelope"
    $diskB1 = Get-PropertyValue $afterIsolatedFailureB "Envelope"
    $durableA6 = $snapshotA6
    Assert-True (
        [string]::IsNullOrEmpty($validA6Warning) -and
        (Get-PropertyValue $diskA6 "Revision") -eq 1 -and
        (Test-Bytes $diskA6.GetPayloadCopy() $snapshotA6.GetPayloadCopy()) -and
        (Test-Bytes $diskB1.GetPayloadCopy() $durableB1.GetPayloadCopy())) `
        "A failed character checkpoint blocked or corrupted another identity's independent write."

    # A later identity can be committed independently, while an exact replay of
    # an already durable identity remains idempotent.
    $entryB4 = New-Entry $keyB $identityB $durableB1 $snapshotB4
    $backupCountABeforeMixedRetry = @(
        Get-ChildItem `
            -LiteralPath $backupDirectoryA `
            -File `
            -Filter "$keyA.*.fch" `
            -ErrorAction SilentlyContinue
    ).Count
    $entryB4Warning = Persist-Entry $entryB4 $durableB1
    $exactA6Warning = Persist-Entry $validA6 $durableA4
    $loadedB4 = $load.Invoke(
        $repository,
        [object[]]@($identityB, $keyB))
    $diskB4 = Get-PropertyValue $loadedB4 "Envelope"
    $backupCountAAfterMixedRetry = @(
        Get-ChildItem `
            -LiteralPath $backupDirectoryA `
            -File `
            -Filter "$keyA.*.fch" `
            -ErrorAction SilentlyContinue
    ).Count
    Assert-True (
        [string]::IsNullOrEmpty($entryB4Warning) -and
        [string]::IsNullOrEmpty($exactA6Warning) -and
        (Get-PropertyValue $diskB4 "Revision") -eq 1 -and
        (Test-Bytes $diskB4.GetPayloadCopy() $snapshotB4.GetPayloadCopy()) -and
        $backupCountAAfterMixedRetry -eq $backupCountABeforeMixedRetry) `
        "Independent follow-up persistence failed or an exact primary replay rotated another backup."

    # A newer post-cutoff overlay keeps its latest revision while its durable
    # base advances only to the frozen generation.
    $liveA4 = $liveConstructor.Invoke(
        [object[]]@(
            $identityA,
            $durableA1,
            $snapshotA4,
            $semantic,
            [long]101))
    $snapshotA5 = New-Envelope $identityA 5 4 $sessionA 0x15
    $withLatest = Get-InstanceMethod $liveType "WithLatest" 3
    $withDurable = Get-InstanceMethod $liveType "WithDurable" 1
    $liveA5 = $withLatest.Invoke(
        $liveA4,
        [object[]]@($snapshotA5, $semantic, [long]101))
    $checkpointedLiveA5 = $withDurable.Invoke(
        $liveA5,
        [object[]]@($snapshotA4))
    Assert-True (
        (Get-PropertyValue (
            Get-PropertyValue $checkpointedLiveA5 "DurableEnvelope") "Revision") -eq 4 -and
        (Get-PropertyValue (
            Get-PropertyValue $checkpointedLiveA5 "LatestEnvelope") "Revision") -eq 5) `
        "A checkpoint collapsed a newer post-cutoff live revision."

    # Exercise both retained-shadow capacity boundaries without allocating
    # hundreds of MiB. The private guard must reject and leave state untouched.
    $resolver = [Activator]::CreateInstance($resolverType)
    $keyProvider = New-ReflectedInstance `
        $keyProviderType `
        ([object[]]@($layout))
    $serviceConstructor = $serviceType.GetConstructors(
        [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic) |
        Select-Object -First 1
    $service = $serviceConstructor.Invoke(
        [object[]]@(
            $options,
            $resolver,
            $keyProvider,
            $codec,
            $profileCodec,
            $repository,
            $validator))

    $serviceFlags = [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic
    $liveDictionaryField = $serviceType.GetField(
        "_liveSnapshots",
        $serviceFlags)
    $liveDictionary = $liveDictionaryField.GetValue($service)
    $liveForBudget = $liveConstructor.Invoke(
        [object[]]@(
            $identityA,
            $durableA4,
            $snapshotA5,
            $semantic,
            [long]101))
    $retainedBytesField = $serviceType.GetField(
        "_retainedSnapshotPayloadBytes",
        $serviceFlags)
    $retainedReferencesField = $serviceType.GetField(
        "_retainedPayloadReferences",
        $serviceFlags)
    $registeredCheckpointsField = $serviceType.GetField(
        "_registeredCheckpoints",
        $serviceFlags)
    $retainedReferences = $retainedReferencesField.GetValue($service)
    $registeredCheckpoints = $registeredCheckpointsField.GetValue($service)
    $ensureCapacity = Get-InstanceMethod `
        $serviceType `
        "EnsureLiveSnapshotCapacityLocked" `
        2
    $addLiveSnapshot = Get-InstanceMethod `
        $serviceType `
        "AddLiveSnapshotLocked" `
        2
    $setLiveSnapshot = Get-InstanceMethod `
        $serviceType `
        "SetLiveSnapshotLocked" `
        2
    $removeLiveSnapshot = Get-InstanceMethod `
        $serviceType `
        "RemoveLiveSnapshotLocked" `
        1
    $beginCheckpointRuntime = Get-InstanceMethod `
        $serviceType `
        "BeginCheckpoint" `
        0
    $discardCheckpointRuntime = Get-InstanceMethod `
        $serviceType `
        "DiscardCheckpoint" `
        1
    $discardCheckpointEntryRuntime = Get-InstanceMethod `
        $serviceType `
        "DiscardCheckpointEntry" `
        2
    $commitCheckpointEntryRuntime = Get-InstanceMethod `
        $serviceType `
        "CommitCheckpointEntry" `
        2
    $addLive = $liveDictionary.GetType().GetMethod("Add")

    # Two cutoffs for the same identity may coexist. Committing the older head
    # advances only its frozen generation; committing the newer tail safely
    # rebases its stale captured base to the now-trusted durable head.
    $snapshotA7 = New-Envelope $identityA 7 6 $sessionA 0x17
    $snapshotA8 = New-Envelope $identityA 8 7 $sessionA 0x18
    $liveA7ForCommit = $liveConstructor.Invoke(
        [object[]]@(
            $identityA,
            $durableA6,
            $snapshotA7,
            $semantic,
            [long]101))
    $addLiveSnapshot.Invoke(
        $service,
        [object[]]@($keyA, $liveA7ForCommit)) | Out-Null
    $checkpointA7 = $beginCheckpointRuntime.Invoke($service, [object[]]@())
    $liveA8ForCommit = $liveConstructor.Invoke(
        [object[]]@(
            $identityA,
            $durableA6,
            $snapshotA8,
            $semantic,
            [long]101))
    $setLiveSnapshot.Invoke(
        $service,
        [object[]]@($keyA, $liveA8ForCommit)) | Out-Null
    $checkpointA8 = $beginCheckpointRuntime.Invoke($service, [object[]]@())
    $entriesA7 = Get-PropertyValue $checkpointA7 "Entries"
    $entriesA8 = Get-PropertyValue $checkpointA8 "Entries"
    $warningA7 = [string]$commitCheckpointEntryRuntime.Invoke(
        $service,
        [object[]]@($checkpointA7, $entriesA7[0]))
    $liveAfterA7 = $liveDictionary[$keyA]
    Assert-True (
        [string]::IsNullOrEmpty($warningA7) -and
        (Get-PropertyValue (
            Get-PropertyValue $liveAfterA7 "DurableEnvelope") "Revision") -eq 7 -and
        (Get-PropertyValue (
            Get-PropertyValue $liveAfterA7 "LatestEnvelope") "Revision") -eq 8 -and
        $registeredCheckpoints.Count -eq 1) `
        "The older per-entry commit collapsed its newer live tail or released the wrong checkpoint generation."
    $warningA8 = [string]$commitCheckpointEntryRuntime.Invoke(
        $service,
        [object[]]@($checkpointA8, $entriesA8[0]))
    $loadedA8 = $load.Invoke($repository, [object[]]@($identityA, $keyA))
    $diskA8 = Get-PropertyValue $loadedA8 "Envelope"
    Assert-True (
        [string]::IsNullOrEmpty($warningA8) -and
        (Get-PropertyValue $diskA8 "Revision") -eq 1 -and
        (Test-Bytes $diskA8.GetPayloadCopy() $snapshotA8.GetPayloadCopy()) -and
        -not $liveDictionary.ContainsKey($keyA) -and
        $registeredCheckpoints.Count -eq 0 -and
        $retainedReferences.Count -eq 0 -and
        [long]$retainedBytesField.GetValue($service) -eq 0) `
        "A later per-entry commit did not safely rebase, release its own pins, or retire a caught-up disconnected shadow."

    function Get-PayloadReferenceCount {
        param([object]$Dictionary, [object]$Payload)
        $arguments = [object[]]::new(1)
        $arguments[0] = $Payload
        return [int]$Dictionary.GetType().GetProperty("Item").GetValue(
            $Dictionary,
            $arguments)
    }

    # Live durable/latest and the immutable cutoff share one reference-counted
    # byte[] union. Pinning the same payload must not charge its bytes twice,
    # but a distinct post-cutoff payload remains charged until discard.
    $payloadUnsafeProperty = $envelopeType.GetProperty(
        "PayloadUnsafe",
        [Reflection.BindingFlags]::Instance -bor
        [Reflection.BindingFlags]::NonPublic)
    $oldPayload = $payloadUnsafeProperty.GetValue($durableA4)
    $cutoffPayload = $payloadUnsafeProperty.GetValue($snapshotA5)
    $replacementPayload = $payloadUnsafeProperty.GetValue($snapshotA6)
    [long]$checkpointUnionBytes = $oldPayload.Length + $cutoffPayload.Length
    [long]$replacementUnionBytes = $checkpointUnionBytes + $replacementPayload.Length
    $newOnlyLive = $liveConstructor.Invoke(
        [object[]]@(
            $identityA,
            $snapshotA6,
            $snapshotA6,
            $semantic,
            [long]101))
    $addLiveSnapshot.Invoke(
        $service,
        [object[]]@("checkpoint-budget", $liveForBudget)) | Out-Null
    $registeredCheckpointA = $beginCheckpointRuntime.Invoke(
        $service,
        [object[]]@())
    $registeredCheckpointB = $beginCheckpointRuntime.Invoke(
        $service,
        [object[]]@())
    Assert-True (
        [long]$retainedBytesField.GetValue($service) -eq $checkpointUnionBytes -and
        $retainedReferences.Count -eq 2 -and
        (Get-PayloadReferenceCount $retainedReferences $oldPayload) -eq 3 -and
        (Get-PayloadReferenceCount $retainedReferences $cutoffPayload) -eq 3 -and
        $registeredCheckpoints.Count -eq 2) `
        "Multiple checkpoints could not coexist or failed to pin both durable-base and target payloads without double-charging bytes."
    $registeredEntriesA = Get-PropertyValue $registeredCheckpointA "Entries"
    $discardCheckpointEntryRuntime.Invoke(
        $service,
        [object[]]@($registeredCheckpointA, $registeredEntriesA[0])) | Out-Null
    Assert-True (
        [long]$retainedBytesField.GetValue($service) -eq $checkpointUnionBytes -and
        (Get-PayloadReferenceCount $retainedReferences $oldPayload) -eq 2 -and
        (Get-PayloadReferenceCount $retainedReferences $cutoffPayload) -eq 2 -and
        $registeredCheckpoints.Count -eq 1) `
        "DiscardCheckpointEntry did not release only its own generation pin."

    # At the cap, replacing the live state with a distinct payload would leave
    # the old payload pinned by the checkpoint and must fail closed.
    $retainedBytesField.SetValue($service, [long]268435456)
    Invoke-ExpectCharacterStorageFailure `
        $setLiveSnapshot `
        $service `
        ([object[]]@("checkpoint-budget", $newOnlyLive))
    Assert-True (
        [object]::ReferenceEquals(
        $liveDictionary["checkpoint-budget"],
            $liveForBudget) -and
        [long]$retainedBytesField.GetValue($service) -eq 268435456 -and
        (Get-PayloadReferenceCount $retainedReferences $oldPayload) -eq 2 -and
        (Get-PayloadReferenceCount $retainedReferences $cutoffPayload) -eq 2) `
        "A post-cutoff admission bypassed the registered-checkpoint payload union cap or mutated state on rejection."

    $retainedBytesField.SetValue($service, $checkpointUnionBytes)
    $setLiveSnapshot.Invoke(
        $service,
        [object[]]@("checkpoint-budget", $newOnlyLive)) | Out-Null
    Assert-True (
        [long]$retainedBytesField.GetValue($service) -eq $replacementUnionBytes -and
        $retainedReferences.Count -eq 3 -and
        (Get-PayloadReferenceCount $retainedReferences $oldPayload) -eq 1 -and
        (Get-PayloadReferenceCount $retainedReferences $cutoffPayload) -eq 1 -and
        (Get-PayloadReferenceCount $retainedReferences $replacementPayload) -eq 1 -and
        $registeredCheckpoints.Count -eq 1) `
        "A post-cutoff replacement did not retain both checkpoint payloads and the distinct new live payload."
    $discardCheckpointRuntime.Invoke(
        $service,
        [object[]]@($registeredCheckpointB)) | Out-Null
    Assert-True (
        [long]$retainedBytesField.GetValue($service) -eq $replacementPayload.Length -and
        $retainedReferences.Count -eq 1 -and
        $registeredCheckpoints.Count -eq 0) `
        "DiscardCheckpoint did not release the abandoned immutable generation."
    $discardCheckpointRuntime.Invoke(
        $service,
        [object[]]@($registeredCheckpointA)) | Out-Null
    $discardCheckpointRuntime.Invoke(
        $service,
        [object[]]@($registeredCheckpointB)) | Out-Null
    Assert-True (
        [long]$retainedBytesField.GetValue($service) -eq $replacementPayload.Length -and
        $retainedReferences.Count -eq 1 -and
        $registeredCheckpoints.Count -eq 0) `
        "DiscardCheckpoint is not idempotent."
    $removeLiveSnapshot.Invoke(
        $service,
        [object[]]@("checkpoint-budget")) | Out-Null
    Assert-True (
        [long]$retainedBytesField.GetValue($service) -eq 0 -and
        $retainedReferences.Count -eq 0 -and
        $liveDictionary.Count -eq 0) `
        "Live/checkpoint payload union accounting leaked after final release."

    for ($index = 0; $index -lt 4096; ++$index) {
        $addLive.Invoke(
            $liveDictionary,
            [object[]]@("budget-$index", $liveForBudget)) | Out-Null
    }
    Invoke-ExpectCharacterStorageFailure `
        $ensureCapacity `
        $service `
        ([object[]]@("budget-overflow", $liveForBudget))
    Assert-True ($liveDictionary.Count -eq 4096) `
        "Entry-cap rejection mutated or evicted retained shadows."
    $liveDictionary.Clear()
    $retainedBytesField.SetValue($service, [long]268435456)
    Invoke-ExpectCharacterStorageFailure `
        $ensureCapacity `
        $service `
        ([object[]]@("byte-overflow", $liveForBudget))
    Assert-True (
        [long]$retainedBytesField.GetValue($service) -eq 268435456 -and
        $liveDictionary.Count -eq 0) `
        "Byte-cap rejection mutated or evicted retained shadows."
}
finally {
    if ($null -ne $service) {
        $service.Dispose()
    }

    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output (
    "Character RAM shadow retention, immutable world-aligned cutoff, " +
    "N-to-N+K per-identity durability, idempotent/failure-isolated persistence, " +
    "post-cutoff preservation, fail-closed memory budgets, verified world-save " +
    "seam, nonblocking world saves, head/tail retry coalescing, shutdown drain, " +
    "and event-scope smoke tests passed.")
