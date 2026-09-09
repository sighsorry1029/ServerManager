param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Get-MethodDefinition {
    param([string]$TypeName, [string]$Name)
    $type = $script:pluginDefinition.MainModule.Types |
        Where-Object FullName -eq $TypeName | Select-Object -First 1
    Assert-True ($null -ne $type) "Missing type $TypeName."
    $method = $type.Methods |
        Where-Object Name -eq $Name | Select-Object -First 1
    Assert-True ($null -ne $method -and $method.HasBody) `
        "Missing method $TypeName.$Name."
    return $method
}

function Get-Calls {
    param($Method, [string]$Name)
    return @($Method.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq $Name
    })
}

function Get-MethodSource {
    param([string]$Source, [string]$Name)
    $pattern = '(?m)^[ \t]+(?:internal|private|public) static [^\r\n]*\b' +
        [Regex]::Escape($Name) + '\s*\('
    $start = [Regex]::Match($Source, $pattern)
    Assert-True $start.Success "Missing source method $Name."
    $tail = $Source.Substring($start.Index + $start.Length)
    $next = [Regex]::Match(
        $tail, '(?m)^[ \t]+(?:internal|private|public) static ')
    if ($next.Success) {
        return $Source.Substring($start.Index, $start.Length + $next.Index)
    }
    return $Source.Substring($start.Index)
}

function Get-PropertyValue {
    param($Instance, [string]$Name)
    $property = $Instance.GetType().GetProperty($Name, $script:instanceFlags)
    Assert-True ($null -ne $property) "Missing state property $Name."
    return $property.GetValue($Instance)
}

function Assert-CallBefore {
    param($Method, [string]$Before, [string]$After, [string]$Message)
    $first = @(Get-Calls $Method $Before) | Select-Object -First 1
    $second = @(Get-Calls $Method $After) | Select-Object -First 1
    Assert-True ($null -ne $first -and $null -ne $second -and
        $first.Offset -lt $second.Offset) $Message
}

function Test-Reachable {
    param($Start, $Target, $Excluded = $null)
    $pending = [Collections.Generic.Queue[object]]::new()
    $visited = @{}
    if ($null -ne $Start) { $pending.Enqueue($Start) }
    while ($pending.Count -ne 0) {
        $current = $pending.Dequeue()
        if ($visited.ContainsKey($current.Offset) -or
            ($null -ne $Excluded -and $current.Offset -eq $Excluded.Offset)) {
            continue
        }
        if ($current.Offset -eq $Target.Offset) { return $true }
        $visited[$current.Offset] = $true
        $flow = $current.OpCode.FlowControl.ToString()
        if ($flow -eq "Branch" -or $flow -eq "Cond_Branch") {
            foreach ($branch in @($current.Operand)) {
                if ($branch -is [Mono.Cecil.Cil.Instruction]) {
                    $pending.Enqueue($branch)
                }
            }
        }
        if ($flow -notin @("Branch", "Return", "Throw") -and
            $null -ne $current.Next) {
            $pending.Enqueue($current.Next)
        }
    }
    return $false
}

function Assert-GuardProtectsCall {
    param($Method, [string]$Guard, [string]$Effect, [string]$Message,
        $ScopeStart = $null)
    $guardCall = @(Get-Calls $Method $Guard) | Select-Object -Last 1
    $effectCall = @(Get-Calls $Method $Effect) | Select-Object -First 1
    Assert-True ($null -ne $guardCall -and $null -ne $effectCall) $Message
    $branch = $guardCall.Next
    while ($null -ne $branch -and
        $branch.OpCode.FlowControl.ToString() -ne "Cond_Branch") {
        $branch = $branch.Next
    }
    if ($null -eq $ScopeStart) {
        $ScopeStart = $Method.Body.Instructions[0]
    }
    Assert-True ($null -ne $branch -and
        -not (Test-Reachable $ScopeStart $effectCall $branch) -and
        ((Test-Reachable $branch.Operand $effectCall) -xor
            (Test-Reachable $branch.Next $effectCall))) $Message
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$gameAssemblyPath = Join-Path $GamePath `
    "valheim_Data\Managed\assembly_valheim.dll"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"
foreach ($path in @($pluginPath, $gameAssemblyPath, $cecilPath)) {
    Assert-True (Test-Path -LiteralPath $path) "Required assembly missing: $path"
}

[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
foreach ($dependency in @("UnityEngine.CoreModule.dll", "UnityEngine.PhysicsModule.dll",
    "UnityEngine.dll", "assembly_utils.dll", "SoftReferenceableAssets.dll",
    "com.rlabrecque.steamworks.net.dll", "Splatform.dll")) {
    [Reflection.Assembly]::LoadFrom((Join-Path (
        Join-Path $GamePath "valheim_Data\Managed") $dependency)) | Out-Null
}
[Reflection.Assembly]::LoadFrom($gameAssemblyPath) | Out-Null
$pluginDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
$plugin = [Reflection.Assembly]::LoadFrom($pluginPath)
$runtimeType = $plugin.GetType("ServerManager.ServerManagerRuntime", $true)
$staticFlags = [Reflection.BindingFlags]"Static,Public,NonPublic"
$instanceFlags = [Reflection.BindingFlags]"Instance,Public,NonPublic"
$runtimeName = "ServerManager.ServerManagerRuntime"
$runtimeSource = Get-Content -LiteralPath (
    Join-Path $projectRoot "Networking\ServerManagerRuntime.cs") -Raw

# Pure state construction is safe outside Unity. Keep the operational pending
# state distinct from the ordinary logout gate, with one immutable deadline.
$drainType = $plugin.GetType(
    "ServerManager.ServerManagerRuntime+ServerFinalSaveDrain", $true)
Assert-True ($null -ne $drainType) "The final-save drain state is missing."
$deadlineProperty = $drainType.GetProperty("DeadlineTimestamp", $instanceFlags)
Assert-True ($null -ne $deadlineProperty -and
    $null -eq $deadlineProperty.GetSetMethod($true)) `
    "A duplicate kick/gate can extend the original drain deadline."
$normalConstructor = $drainType.GetConstructor(
    $instanceFlags, $null, [Type[]]@([long]), $null)
$operationalConstructor = $drainType.GetConstructor(
    $instanceFlags, $null, [Type[]]@([long], [bool]), $null)
Assert-True ($null -ne $normalConstructor -and
    $null -ne $operationalConstructor) `
    "Normal and operational drain construction must remain distinct."
$originalDeadline = [long]123456789
$normalDrain = $normalConstructor.Invoke([object[]]@($originalDeadline))
$operationalDrain = $operationalConstructor.Invoke(
    [object[]]@($originalDeadline, $true))
Assert-True (-not (Get-PropertyValue $normalDrain "OperationalKick") -and
    (Get-PropertyValue $normalDrain "GateStarted") -and
    -not (Get-PropertyValue $normalDrain "SaveAcknowledged")) `
    "Normal logout drain no longer starts in its existing unacknowledged gate."
Assert-True ((Get-PropertyValue $operationalDrain "OperationalKick") -and
    -not (Get-PropertyValue $operationalDrain "GateStarted") -and
    -not (Get-PropertyValue $operationalDrain "InboundBarrierCompleted") -and
    -not (Get-PropertyValue $operationalDrain "SaveAssemblyAdmitted") -and
    -not (Get-PropertyValue $operationalDrain "SaveAcknowledged") -and
    -not (Get-PropertyValue $operationalDrain "TimeoutRejected")) `
    "A new operational request already entered or acknowledged the final gate."
$gateProperty = $drainType.GetProperty("GateStarted", $instanceFlags)
$gateProperty.SetValue($operationalDrain, $true)
Assert-True ((Get-PropertyValue $operationalDrain "DeadlineTimestamp") -eq
    $originalDeadline -and
    -not (Get-PropertyValue $operationalDrain "SaveAcknowledged")) `
    "Starting the operational gate changes its deadline or acknowledges a save."
$operationalTimeout = $runtimeType.GetField(
    "OperationalKickSaveTimeoutSeconds", $staticFlags)
Assert-True ($null -ne $operationalTimeout -and
    $operationalTimeout.GetRawConstantValue() -eq 5) `
    "Operational kicks must use a fixed five-second maximum."
$gracefulTimeout = $runtimeType.GetField(
    "GracefulExitSaveTimeoutSeconds", $staticFlags)
Assert-True ($null -ne $gracefulTimeout -and
    $gracefulTimeout.GetRawConstantValue() -eq 65) `
    "The separate normal logout timeout changed with operational kicks."

$exitKindType = $plugin.GetType(
    "ServerManager.ServerManagerRuntime+DeferredClientExitKind", $true)
Assert-True ([int][Enum]::Parse($exitKindType, "OperationalKick") -eq 3) `
    "Operational kicks are not a distinct deferred-client exit kind."

$beginKick = Get-MethodDefinition $runtimeName "TryBeginOperationalKick"
$completeKick = Get-MethodDefinition $runtimeName "CompleteOperationalKick"
$clientRequest = Get-MethodDefinition $runtimeName "HandleClientOperationalKickRequest"
$serverComplete = Get-MethodDefinition $runtimeName "HandleServerOperationalKickComplete"
$beginGate = Get-MethodDefinition $runtimeName "HandleServerFinalSaveBegin"
$serverFragment = Get-MethodDefinition $runtimeName "HandleServerCharacterFragment"
$admitAssembly = Get-MethodDefinition $runtimeName "AdmitClientSaveAssembly"
$expiry = Get-MethodDefinition $runtimeName "ProcessExpiredServerFinalSaveDrains"
$resumeExit = Get-MethodDefinition $runtimeName "ResumeDeferredClientExit"
$processExit = Get-MethodDefinition $runtimeName "ProcessDeferredClientExit"
$beginSource = Get-MethodSource $runtimeSource "TryBeginOperationalKick"
$clientRequestSource = Get-MethodSource $runtimeSource "HandleClientOperationalKickRequest"
$completeSource = Get-MethodSource $runtimeSource "HandleServerOperationalKickComplete"
$beginGateSource = Get-MethodSource $runtimeSource "HandleServerFinalSaveBegin"
$fragmentSource = Get-MethodSource $runtimeSource "HandleServerCharacterFragment"
$admitSource = Get-MethodSource $runtimeSource "AdmitClientSaveAssembly"
$processExitSource = Get-MethodSource $runtimeSource "ProcessDeferredClientExit"

foreach ($requiredCall in @("TryGetSnapshot", "TryGetServerSession",
    "TryResolveActiveDetectionPeer", "CreateOperationalKickRequest")) {
    Assert-True (@(Get-Calls $beginKick $requiredCall).Count -ge 1) `
        "Operational kick eligibility lost $requiredCall."
}
Assert-True ($beginSource.Contains("ConnectionSessionState.Ready") -and
    $beginSource.Contains("character.IsClosed") -and
    $beginSource.Contains("PendingKickTimestamp") -and
    $beginSource.Contains("OperationalKickSaveTimeoutTicks")) `
    "Non-ready, unmanaged, or security-rejected peers can enter operational grace."
Assert-True (@(Get-Calls $beginKick "TryRestrictOutboundForFinalSave").Count -eq 0 -and
    @(Get-Calls $beginKick "TryCompleteFinalSaveRestriction").Count -eq 0) `
    "The kick request isolates world traffic before the client final-save barrier."
Assert-CallBefore $beginKick "TryGetValue" "CreateOperationalKickRequest" `
    "Repeated operational kicks are not detected before sending a new request."
$duplicateBranch = [Regex]::Match($beginSource,
    'if \(ServerFinalSaveDrains.TryGetValue(?<body>[\s\S]*?)' +
    'ServerFinalSaveDrains.Add\(').Groups["body"].Value
Assert-True ($duplicateBranch.Contains("existing.DeadlineTimestamp") -and
    $duplicateBranch.Contains("return true;") -and
    -not $duplicateBranch.Contains("new ServerFinalSaveDrain")) `
    "Duplicate operational kicks create a replacement drain or extend their deadline."

# A pending operational request must not consume the one final-save admission.
# Normal single-flight traffic can finish before FinalSaveBegin starts the gate.
Assert-True ($beginGateSource.Contains("OperationalKick") -and
    $beginGateSource.Contains("GateStarted")) `
    "FinalSaveBegin cannot promote a pending operational kick into its gate."
Assert-True ($fragmentSource.Contains("finalDrain?.GateStarted") -and
    $admitSource.Contains("GateStarted")) `
    "Pre-gate operational traffic consumes the final-save isolation/admission."
Assert-CallBefore $serverFragment "get_GateStarted" "TryCompleteFinalSaveRestriction" `
    "An operational request closes inbound world traffic before its gate begins."
Assert-CallBefore $admitAssembly "get_GateStarted" "set_SaveAssemblyAdmitted" `
    "Pre-gate traffic can consume the sole admitted final assembly."

# Operational final uploads must be full profiles. Exercise the exact compiled
# request-reader closure in isolation: the envelope codec is managed-only, so
# neither a live Unity player nor an authoritative repository is needed here.
$snapshotServiceName = "ServerManager.CharacterSnapshotService"
$saveEntry = Get-MethodDefinition $snapshotServiceName "HandleSaveRequest"
Assert-True ($saveEntry.Parameters.Count -eq 3 -and
    $saveEntry.Parameters[2].Name -eq "requireFullProfile" -and
    $saveEntry.Parameters[2].HasConstant -and
    -not [bool]$saveEntry.Parameters[2].Constant) `
    "Ordinary save requests no longer default to the existing full/inventory policy."
$readerReference = $saveEntry.Body.Instructions | Where-Object {
    $_.OpCode.Name -eq "ldftn" -and
    $_.Operand -is [Mono.Cecil.MethodReference] -and
    $_.Operand.Name.StartsWith("<HandleSaveRequest>")
} | Select-Object -First 1
Assert-True ($null -ne $readerReference) "The bounded request-reader closure is missing."
$readerDefinition = $readerReference.Operand.Resolve()
Assert-True (@(Get-Calls $readerDefinition "FromZPackage").Count -eq 1 -and
    @(Get-Calls $saveEntry "HandleSaveRequestCore").Count -eq 1) `
    "Final-profile policy decodes twice or bypasses the common save validator."
$serviceType = $plugin.GetType($snapshotServiceName, $true)
$serviceForReader = [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
    $serviceType)
$storageOptionsType = $plugin.GetType("ServerManager.CharacterStorageOptions", $true)
$envelopeCodecType = $plugin.GetType("ServerManager.CharacterEnvelopeCodec", $true)
$envelopeType = $plugin.GetType("ServerManager.CharacterEnvelope", $true)
$envelopeKindType = $plugin.GetType("ServerManager.CharacterEnvelopeKind", $true)
$identityType = $plugin.GetType("ServerManager.CharacterIdentity", $true)
$options = [Activator]::CreateInstance($storageOptionsType)
$envelopeCodec = [Activator]::CreateInstance(
    $envelopeCodecType, [object[]]@($options))
$serviceType.GetField("_envelopeCodec", $instanceFlags).SetValue(
    $serviceForReader, $envelopeCodec)
$readerType = $plugin.GetType(
    $readerDefinition.DeclaringType.FullName.Replace("/", "+"), $true)
$reader = [Activator]::CreateInstance($readerType, $true)
$readerMethod = $readerType.GetMethod($readerDefinition.Name, $instanceFlags)
$serviceField = $readerType.GetFields($instanceFlags) |
    Where-Object FieldType -eq $serviceType | Select-Object -First 1
$serviceField.SetValue($reader, $serviceForReader)
$requireFullField = $readerType.GetField("requireFullProfile", $instanceFlags)
$packageField = $readerType.GetField("package", $instanceFlags)
$createEnvelope = $envelopeType.GetMethods($staticFlags) |
    Where-Object { $_.Name -eq 'Create' -and $_.GetParameters().Count -eq 8 } |
    Select-Object -First 1
$toPackage = $envelopeCodecType.GetMethod("ToZPackage", $instanceFlags)
$identity = [Activator]::CreateInstance(
    $identityType, [object[]]@("76561198000000001", "OperationalSmoke"))
$profileVersion = $plugin.GetType("ServerManager.ValheimPlayerProfileCodec", $true).
    GetField("SupportedPlayerProfileVersion", $staticFlags).GetRawConstantValue()
foreach ($case in @(
    @{ Kind = "SaveRequest"; RequireFull = $true; Reject = $false },
    @{ Kind = "InventorySaveRequest"; RequireFull = $true; Reject = $true },
    @{ Kind = "Snapshot"; RequireFull = $true; Reject = $true },
    @{ Kind = "SaveRequest"; RequireFull = $false; Reject = $false },
    @{ Kind = "InventorySaveRequest"; RequireFull = $false; Reject = $false }
)) {
    $kind = [Enum]::Parse($envelopeKindType, $case.Kind)
    $envelope = $createEnvelope.Invoke($null, [object[]]@(
        $kind, [long]2, [long]1, [Guid]::NewGuid(), $identity,
        [DateTime]::UtcNow, $profileVersion, [byte[]]@(1, 2, 3, 4, 5, 6, 7, 8)))
    $packageField.SetValue($reader,
        $toPackage.Invoke($envelopeCodec, [object[]]@($envelope)))
    $requireFullField.SetValue($reader, $case.RequireFull)
    $failure = $null
    $decoded = $null
    try {
        $decoded = $readerMethod.Invoke($reader, [object[]]@())
    }
    catch {
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) {
            $failure = $failure.InnerException
        }
    }
    if ($case.Reject) {
        Assert-True ($null -ne $failure -and
            $failure.GetType().FullName -eq "ServerManager.CharacterProtocolException" -and
            $failure.Message.Contains("final full-profile snapshot")) `
            "Operational final reader accepted $($case.Kind) instead of a full profile."
    }
    else {
        Assert-True ($null -eq $failure -and $null -ne $decoded -and
            (Get-PropertyValue $decoded "Kind") -eq $kind) `
            "The full-profile guard changed an allowed $($case.Kind) request."
    }
}
$saveCore = Get-MethodDefinition $snapshotServiceName "HandleSaveRequestCore"
Assert-CallBefore $saveCore "Invoke" "ValidateSaveRequest" `
    "The full-profile guard bypasses ordinary session/revision request validation."
Assert-CallBefore $saveCore "ValidateSnapshotDetailed" "EvaluateLiveCandidate" `
    "Operational full profiles bypass the existing profile/semantic validator."
Assert-CallBefore $saveCore "EvaluateLiveCandidate" "TryCommitRevision" `
    "Operational final revision is committed before semantic validation."
Assert-True (@(Get-Calls $saveCore "get_StatLimitFindings").Count -ge 1) `
    "The shared full-profile admission path drops existing stat-limit findings."
Assert-CallBefore $serverFragment "SendCharacterPayload" "RecordCharacterStatLimits" `
    "Operational final saves bypass the existing post-ACK stat-limit handling."
Assert-True ($fragmentSource.Contains(
    "requireFullProfile: finalDrain?.OperationalKick == true && finalDrain.GateStarted")) `
    "The full-profile-only policy applies outside a gated operational kick."
$reassembleCall = @(Get-Calls $serverFragment "AcceptPackage") | Select-Object -First 1
$saveServiceCall = @(Get-Calls $serverFragment "HandleSaveRequest") | Select-Object -First 1
$deadlineReads = @(Get-Calls $serverFragment "get_DeadlineTimestamp")
Assert-True (@($deadlineReads | Where-Object Offset -lt $reassembleCall.Offset).Count -ge 1 -and
    @($deadlineReads | Where-Object {
        $_.Offset -gt $reassembleCall.Offset -and $_.Offset -lt $saveServiceCall.Offset
    }).Count -ge 1) `
    "The operational deadline is not checked both before and after fragment reassembly."
$lateDeadlineRead = $deadlineReads | Select-Object -Last 1
$operationalRead = @(Get-Calls $serverFragment "get_OperationalKick") |
    Where-Object {
        $_.Offset -gt $reassembleCall.Offset -and $_.Offset -lt $lateDeadlineRead.Offset
    } | Select-Object -Last 1
Assert-True ($null -ne $operationalRead -and
    $operationalRead.Next.OpCode.Name -in @("brfalse", "brfalse.s")) `
    "The post-reassembly deadline is not gated by an operational drain."
# Release short-circuits non-operational traffic around the deadline check;
# only the true operational path must be dominated by its comparison. Debug
# materializes the combined Boolean first, so whole-method dominance happened
# to pass there even though it is not the actual policy being proved.
$operationalPath = $operationalRead.Next.Next
Assert-True (-not (Test-Reachable $operationalPath $saveServiceCall $lateDeadlineRead)) `
    "An operational completed fragment can skip reading its original deadline."
Assert-GuardProtectsCall $serverFragment "get_DeadlineTimestamp" "HandleSaveRequest" `
    "A fragment completing reassembly after the deadline still reaches save admission." `
    $operationalPath
$deadlineBranch = $lateDeadlineRead.Next
while ($null -ne $deadlineBranch -and
    $deadlineBranch.OpCode.FlowControl.ToString() -ne "Cond_Branch") {
    $deadlineBranch = $deadlineBranch.Next
}
$lateTimeout = @(Get-Calls $serverFragment "CompleteOperationalKick") |
    Where-Object {
        $_.Offset -gt $lateDeadlineRead.Offset -and $_.Offset -lt $saveServiceCall.Offset
    } | Select-Object -First 1
Assert-True ($null -ne $lateTimeout -and
    ((Test-Reachable $deadlineBranch.Operand $lateTimeout) -xor
        (Test-Reachable $deadlineBranch.Next $lateTimeout)) -and
    ((Test-Reachable $deadlineBranch.Operand $lateTimeout) -xor
        (Test-Reachable $deadlineBranch.Operand $saveServiceCall)) -and
    -not (Test-Reachable $lateTimeout $saveServiceCall)) `
    "The deadline comparison does not separate timeout disconnect from save admission."

# Request/completion tokens are authenticated against the live session, and a
# completion only closes the peer after the final accepted RAM-shadow ACK.
foreach ($definition in @($clientRequest, $serverComplete)) {
    Assert-True (@(Get-Calls $definition "FixedTimeEquals").Count -ge 2) `
        "$($definition.Name) no longer binds both session ID and nonce."
}
Assert-True ($clientRequestSource.Contains("ProtocolSequence.OperationalKickRequest") -and
    $completeSource.Contains("ProtocolSequence.OperationalKickComplete") -and
    $completeSource.Contains("SaveAcknowledged") -and
    $completeSource.Contains("GateStarted") -and
    $completeSource.Contains("OperationalKick")) `
    "Forged, premature, or non-operational completions can end an active peer."
Assert-True (@(Get-Calls $clientRequest "TryBeginDeferredClientExit").Count -eq 1) `
    "Operational kick no longer reuses the client quiescence/final-save pipeline."
Assert-True (@(Get-Calls $serverComplete "CompleteOperationalKick").Count -eq 1) `
    "An authenticated post-ACK completion does not finish the operational kick."
Assert-GuardProtectsCall $serverComplete "get_SaveAcknowledged" "CompleteOperationalKick" `
    "A client completion can disconnect the peer without a final accepted ACK."
Assert-CallBefore $serverFragment "SendCharacterPayload" "set_SaveAcknowledged" `
    "Operational completion is enabled before the accepted response is sent."
Assert-True (@(Get-Calls $processExit "IsDrainedThrough").Count -eq 1 -and
    $processExitSource.Contains("DeferredClientExitKind.OperationalKick") -and
    @(Get-Calls $processExit "CreateOperationalKickComplete").Count -eq 1 -and
    @(Get-Calls $resumeExit "CreateOperationalKickComplete").Count -eq 0) `
    "Client operational completion no longer follows the accepted final capture."
Assert-GuardProtectsCall $processExit "IsDrainedThrough" "CreateOperationalKickComplete" `
    "A client sends operational completion on an unacknowledged/failed final capture."
Assert-True ($clientRequestSource.Contains("_deferredClientExit.OperationalKickRequested = true") -and
    @(Get-Calls $clientRequest "CaptureDeferredClientExitSnapshot").Count -eq 0 -and
    @(Get-Calls $clientRequest "OfferClientSave").Count -eq 0) `
    "A simultaneous local exit and operational request create a competing capture."

Assert-True (@(Get-Calls $expiry "CompleteOperationalKick").Count -eq 1 -and
    @(Get-Calls $expiry "get_DeadlineTimestamp").Count -ge 1) `
    "The fixed operational deadline does not force the final kick."
$kickedNotification = @($completeKick.Body.Instructions | Where-Object {
    $_.Operand -is [string] -and $_.Operand -ceq "Kicked"
})
$immediateDisconnect = @(Get-Calls $completeKick "DisconnectServerPeer") |
    Select-Object -First 1
Assert-True ($kickedNotification.Count -eq 1 -and
    @(Get-Calls $completeKick "Invoke" | Where-Object {
        $_.Operand.DeclaringType.FullName -eq "ZRpc"
    }).Count -eq 1 -and
    @(Get-Calls $completeKick "InternalKick").Count -eq 0 -and
    $null -ne $immediateDisconnect) `
    "Operational completion lost vanilla Kicked notification or queues a second InternalKick cleanup."
Assert-True (@($completeKick.Body.ExceptionHandlers | Where-Object {
    $_.HandlerType.ToString() -eq "Finally" -and
    $immediateDisconnect.Offset -ge $_.HandlerStart.Offset -and
    ($null -eq $_.HandlerEnd -or $immediateDisconnect.Offset -lt $_.HandlerEnd.Offset)
}).Count -ge 1) `
    "Operational completion does not force immediate disconnect when notification fails."

# Security actions must never acquire operational grace or create a new save.
foreach ($methodName in @("ExecuteTerminalDetectionAction", "SendServerRejection",
    "TerminateRejectedCharacterSave")) {
    $securityMethod = Get-MethodDefinition $runtimeName $methodName
    Assert-True (@(Get-Calls $securityMethod "TryBeginOperationalKick").Count -eq 0 -and
        @(Get-Calls $securityMethod "CreateOperationalKickRequest").Count -eq 0) `
        "Security path $methodName enters operational save grace."
}

# Target only user-initiated kick entrypoints. InternalKick is shared by ban and
# allowlist enforcement and must remain untouched as a Harmony target.
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (
    Split-Path -Parent $pluginPath) "0Harmony.dll"))) | Out-Null
$entryType = $plugin.GetType("ServerManager.OperationalKickEntryPointPatch", $true)
$getTargets = $entryType.GetMethod("TargetMethods", $staticFlags)
$targets = @($getTargets.Invoke($null, [object[]]@()))
Assert-True ($targets.Count -eq 2 -and
    @($targets | Where-Object Name -eq "Kick").Count -eq 1 -and
    @($targets | Where-Object Name -eq "RPC_Kick").Count -eq 1 -and
    @($targets | Where-Object { $_.DeclaringType.Name -ne "ZNet" }).Count -eq 0) `
    "Vanilla operational interception includes non-kick/security entrypoints."

# Run the real transpiler against the installed game's original instructions,
# without installing a Harmony patch or executing a game method. Use Harmony
# from the transpiler signature to avoid duplicate byte/LoadFrom type contexts.
$transpiler = $entryType.GetMethod("Transpiler", $staticFlags)
$instructionType = $transpiler.GetParameters()[0].ParameterType.GetGenericArguments()[0]
$patchProcessor = $instructionType.Assembly.GetType("HarmonyLib.PatchProcessor", $true)
$createGenerator = $patchProcessor.GetMethods() | Where-Object {
    $_.Name -eq "CreateILGenerator" -and $_.GetParameters().Count -eq 1
} | Select-Object -First 1
$getOriginal = $patchProcessor.GetMethods() | Where-Object {
    $_.Name -eq "GetOriginalInstructions" -and $_.GetParameters().Count -eq 2 -and
    -not $_.GetParameters()[1].ParameterType.IsByRef
} | Select-Object -First 1
foreach ($target in $targets) {
    $generator = $createGenerator.Invoke($null, [object[]]@($target))
    $original = $getOriginal.Invoke($null, [object[]]@($target, $generator))
    $originalOpOperands = @($original | ForEach-Object {
        $_.opcode.Name + ":" + [string]$_.operand
    })
    $originalCount = $original.Count
    $patched = @($transpiler.Invoke($null, [object[]]@($original, $generator, $target)))
    Assert-True ($patched.Count -eq $originalCount + 4) `
        "$($target.Name) inserts more than the four-instruction operational guard."
    $gateCalls = @($patched | Where-Object {
        $_.operand -is [Reflection.MethodInfo] -and
        $_.operand.Name -eq "TryBeginOperationalKickByUser"
    })
    Assert-True ($gateCalls.Count -eq 1) `
        "$($target.Name) does not contain exactly one operational kick guard."
    $gateIndex = [Array]::IndexOf($patched, $gateCalls[0])
    $skip = $patched[$gateIndex + 1]
    $originalKick = $patched[$gateIndex + 4]
    $afterKick = $patched[$gateIndex + 5]
    Assert-True ($skip.opcode -eq [Reflection.Emit.OpCodes]::Brtrue -and
        $originalKick.operand -is [Reflection.MethodInfo] -and
        $originalKick.operand.Name -eq "InternalKick" -and
        $originalKick.operand.GetParameters()[0].ParameterType -eq [string] -and
        $afterKick.labels.Contains($skip.operand)) `
        "$($target.Name) does not preserve the exact vanilla fallback with a true-only skip."
    $restored = @($patched[($gateIndex + 2)..($patched.Count - 1)])
    if ($gateIndex -gt 2) {
        $restored = @($patched[0..($gateIndex - 3)]) + $restored
    }
    $restoredOpOperands = @($restored | ForEach-Object {
        $_.opcode.Name + ":" + [string]$_.operand
    })
    Assert-True ([string]::Join("|", $restoredOpOperands) -ceq
        [string]::Join("|", $originalOpOperands)) `
        "$($target.Name) alters vanilla authorization, client dispatch, or original instruction flow."
}
$getHost = $entryType.GetMethod("GetVanillaSteamHostCandidate", $staticFlags)
foreach ($case in @(
    @{ User = "76561198000000001"; Expected = "76561198000000001" },
    @{ User = "Alice"; Expected = "Alice" },
    @{ User = "Steam_76561198000000001"; Expected = "76561198000000001" },
    @{ User = "steam_76561198000000001"; Expected = $null },
    @{ User = "Xbox_123"; Expected = $null },
    @{ User = "_Alice"; Expected = "_Alice" },
    @{ User = "Steam_"; Expected = "Steam_" },
    @{ User = "Steam_123_extra"; Expected = "123_extra" },
    @{ User = "player_name"; Expected = $null }
)) {
    $actual = $getHost.Invoke($null, [object[]]@($case.User))
    Assert-True ([string]::Equals($actual, $case.Expected, [StringComparison]::Ordinal)) `
        "Operational kick target parsing diverges from vanilla for $($case.User)."
}
$entryDefinition = Get-MethodDefinition "ServerManager.OperationalKickEntryPointPatch" `
    "TryBeginOperationalKickByUser"
Assert-CallBefore $entryDefinition "GetPeerByHostName" "GetPeerByPlayerName" `
    "Operational kick resolves an ambiguous player name ahead of the host ID."
$eventSource = Get-Content -LiteralPath (
    Join-Path $projectRoot "Events\ServerEventRuntime.cs") -Raw
$moderationSource = Get-MethodSource $eventSource "ExecuteModeration"
$moderationBranches = [Regex]::Match($moderationSource,
    'if \(command.Kind == ServerEventCommandKind.Kick\)' +
    '(?<kick>[\s\S]*?)else if \(command.Kind == ServerEventCommandKind.Ban\)' +
    '(?<ban>[\s\S]*?)\r?\n            else\r?\n')
Assert-True ($moderationBranches.Success -and
    $moderationBranches.Groups["kick"].Value.Contains("consoleKick(exactConnectedTransportId)") -and
    $moderationBranches.Groups["kick"].Value.Contains("server.Kick(exactConnectedTransportId)") -and
    -not $moderationBranches.Groups["ban"].Value.Contains("TryBeginOperationalKick") -and
    $moderationBranches.Groups["ban"].Value.Contains("server.Disconnect(connectedPeer)")) `
    "API/Discord kick no longer uses the patched vanilla entry with its pinned identity, or ban acquired save grace."

$consoleKick = Get-MethodSource $eventSource "ExecuteConsoleKick"
Assert-True ($consoleKick.Contains('ExecuteCommand(new QueuedCommand(ServerEventCommandKind.Kick, target, reason), invoke)')) `
    "Raw RCON kick must reuse the same readiness/identity/audit path as the integration API."

# Disconnect still closes only the session overlay; disk persistence belongs to
# the existing world checkpoint path, not to the kick acknowledgement.
foreach ($method in @($beginKick, $completeKick, $serverComplete)) {
    foreach ($diskMethod in @("WriteAllBytes", "WriteSnapshot", "SaveWorld",
        "BeginCheckpoint", "CommitCheckpoint")) {
        Assert-True (@(Get-Calls $method $diskMethod).Count -eq 0) `
            "Operational kick unexpectedly forces disk/world save $diskMethod."
    }
}
$cleanup = Get-MethodDefinition $runtimeName "CleanupPeer"
Assert-True (@(Get-Calls $cleanup "CloseServerSession").Count -eq 1) `
    "Operational disconnect bypasses retained-shadow session closure."

Write-Output ("Operational kick five-second bound, authenticated request/complete, " +
    "two-phase gate, full-profile-only final admission, accepted-ACK ordering, " +
    "vanilla kick transpilers, single finally-disconnect, security bypass, and RAM-only " +
    "disconnect smoke tests passed.")
