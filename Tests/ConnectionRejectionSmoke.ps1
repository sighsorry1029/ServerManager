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
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    ++$script:assertions
    if (-not $Condition) { throw $Message }
}
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
public static class ConnectionRejectionProbe {
    public static readonly List<string[]> Records = new List<string[]>();
    public static bool ThrowAudit;
    public static int Sends;
    public static void Record(string account, string name, string category, string stage,
        string reason, string source, string identity, string detail, string plugins) {
        if (ThrowAudit) throw new InvalidOperationException("fixture audit sink");
        Records.Add(new [] { account, name, category, stage, reason, source, identity, detail, plugins });
    }
    public static void FailSend() { Sends++; throw new InvalidOperationException("fixture disconnected transport"); }
}
'@
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
$runtimeIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerManagerRuntime'
$coordinatorIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ConnectionSessionCoordinator'
function Method-Calls($Method) {
    return @($Method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object Operand)
}
$applyIL = $runtimeIL.Methods | Where-Object Name -eq 'ApplyInitialServerCharacter'
$applyCalls = @(Method-Calls $applyIL)
Assert-True (($applyCalls | Where-Object Name -eq 'ReportClientFreshCharacterRejection').Count -eq 1) 'Fresh local refusal must report exactly once before the existing failure path.'
$reportInstruction = $applyIL.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'ReportClientFreshCharacterRejection' }
$nextFailure = $applyIL.Body.Instructions | Where-Object { $_.Offset -gt $reportInstruction.Offset -and $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'FailClient' } | Select-Object -First 1
$setProfile = $applyIL.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'SetGamePlayerProfile' } | Select-Object -First 1
Assert-True ($reportInstruction.Offset -lt $nextFailure.Offset -and $nextFailure.Offset -lt $setProfile.Offset) 'Refusal must precede failure and must not replace the selected profile.'
$serverReportIL = $runtimeIL.Methods | Where-Object Name -eq 'HandleServerClientCharacterRejection'
$serverCalls = @(Method-Calls $serverReportIL)
Assert-True (($serverCalls | Where-Object Name -eq 'TryResolveActiveDetectionPeer').Count -eq 1 -and
    ($serverCalls | Where-Object Name -eq 'AcceptClientCharacterRejection').Count -eq 1) 'A refusal must verify final server identity and coordinator binding.'
Assert-True (($serverCalls | Where-Object { $_.Name -in @('AcceptReadyAck', 'FinalizePendingInitialSnapshot', 'ReleaseWorld') }).Count -eq 0) 'Refusal cannot acknowledge, commit, or release world data.'
$sendIL = $runtimeIL.Methods | Where-Object Name -eq 'SendServerRejection'
$sendCalls = @(Method-Calls $sendIL)
Assert-True ([array]::IndexOf([string[]]@($sendCalls.Name), 'RecordConnectionRejection') -lt [array]::IndexOf([string[]]@($sendCalls.Name), 'RejectSession')) 'Identity and audit must be captured before terminal rejection.'
foreach ($name in @('RecordConnectionRejection', 'RecordLocalHostConnectionRejection', 'ReportClientFreshCharacterRejection')) {
    $calls = @(Method-Calls ($runtimeIL.Methods | Where-Object Name -eq $name))
    Assert-True (($calls | Where-Object { $_.Name -like 'Log*' }).Count -eq 0) 'Optional failure isolation must not re-enter a throwing logger.'
}
$releaseIL = $runtimeIL.Methods | Where-Object Name -eq 'ReleaseWorld'
Assert-True (($releaseIL.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'WorldReleased' -and $_.OpCode.Name -eq 'stfld' }).Count -eq 1) 'Actual successful world release must pin the admission-complete marker.'

# In-memory fixture only: avoid Unity startup and peer/native transport access.
# Parser, coordinator, refusal, marker and audit routing bodies stay production.
# The event sink is captured here; CharacterAuditSmoke exercises its real sinks.
function Clear-Body($Method) {
    $Method.Body.Instructions.Clear(); $Method.Body.ExceptionHandlers.Clear(); $Method.Body.Variables.Clear()
}
$initializer = $runtimeIL.Methods | Where-Object Name -eq '.cctor'
Clear-Body $initializer
$initializer.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
$resolverIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerPeerResolver'
$resolveIL = $resolverIL.Methods | Where-Object Name -eq 'TryResolvePeer'
Clear-Body $resolveIL
foreach ($index in @(2, 3)) {
    $resolveIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg, $resolveIL.Parameters[$index]))
    $resolveIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldnull))
    $resolveIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Stind_Ref))
}
$resolveIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1))
$resolveIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
$eventIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.Events.ServerEventRuntime'
$recordIL = $eventIL.Methods | Where-Object Name -eq 'RecordConnectionRejected'
Clear-Body $recordIL
foreach ($parameter in $recordIL.Parameters) {
    $recordIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg, $parameter))
}
$recordIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call,
    $definition.MainModule.ImportReference([ConnectionRejectionProbe].GetMethod('Record'))))
$recordIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
$transportIL = $runtimeIL.Methods | Where-Object Name -eq 'SendProtocolOrThrow'
Clear-Body $transportIL
$transportIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call,
    $definition.MainModule.ImportReference([ConnectionRejectionProbe].GetMethod('FailSend'))))
$transportIL.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
$stream = [IO.MemoryStream]::new()
try { $definition.Write($stream); $plugin = [Reflection.Assembly]::Load($stream.ToArray()) }
finally { $stream.Dispose(); $definition.Dispose() }
$runtime = $plugin.GetType('ServerManager.ServerManagerRuntime', $true)
$coordinatorType = $plugin.GetType('ServerManager.ConnectionSessionCoordinator', $true)
$sessionType = $coordinatorType.GetNestedType('Session', $instance)
$stateType = $plugin.GetType('ServerManager.ConnectionSessionState', $true)
$packetType = $plugin.GetType('ServerManager.ProtocolPacket', $true)
$kindType = $plugin.GetType('ServerManager.ProtocolPacketKind', $true)
$codec = $plugin.GetType('ServerManager.ProtocolPacketCodec', $true)
$limitsType = $plugin.GetType('ServerManager.ConnectionProtocolLimits', $true)
$limitsCtor = $limitsType.GetConstructors()[0]
$limitParameters = $limitsCtor.GetParameters()
$limitArguments = [object[]]::new($limitParameters.Length)
for ($index = 0; $index -lt $limitParameters.Length; ++$index) {
    $parameter = $limitParameters[$index]
    $limitArguments[$index] = if ($null -eq $parameter.DefaultValue) { $null } else {
        [Management.Automation.LanguagePrimitives]::ConvertTo($parameter.DefaultValue, $parameter.ParameterType)
    }
}
$limits = $limitsCtor.Invoke($limitArguments)
$coordinator = $coordinatorType.GetConstructors()[0].Invoke([object[]]@($limits, $null, $null))
$runtime.GetField('_coordinator', $static).SetValue($null, $coordinator)
$runtime.GetField('_connectionLimits', $static).SetValue($null, $limits)
$runtime.GetField('SteamAuthenticationGate', $static).SetValue($null, [object]::new())
foreach ($fieldName in @('ConnectionRejections', 'LocalHostConnectionRejections', 'SteamAuthenticationsByRpc')) {
    $field = $runtime.GetField($fieldName, $static)
    $field.SetValue($null, [Activator]::CreateInstance($field.FieldType))
}
$sessions = $coordinatorType.GetField('sessions', $instance).GetValue($coordinator)
$rpcType = [ZRpc]
$sessionId = [byte[]](1..16)
$nonce = [byte[]](21..52)
$messageId = [byte[]](61..76)
function New-Fixture([string]$State = 'CharacterSent', [bool]$Authenticated = $true, [bool]$Characters = $true) {
    $rpc = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($rpcType)
    $session = [Activator]::CreateInstance($sessionType, $true)
    $values = @{ Rpc = $rpc; State = [Enum]::Parse($stateType, $State); SessionId = $sessionId; Nonce = $nonce;
        LastSequence = [uint32]4; AbsoluteDeadline = [long]::MaxValue; PhaseDeadline = [long]::MaxValue;
        CharacterMessageId = $messageId; CharacterTransferPrepared = $true; PeerInfoAuthenticated = $Authenticated;
        ServerCharactersEnabled = $Characters }
    foreach ($key in $values.Keys) { $sessionType.GetField($key, $instance).SetValue($session, $values[$key]) }
    $sessions.Add($rpc, $session)
    return [pscustomobject]@{ Rpc = $rpc; Session = $session }
}
function New-Refusal([byte[]]$Id = $sessionId, [byte[]]$Challenge = $nonce, [byte[]]$Transfer = $messageId,
    [byte]$Reason = 1, [uint32]$Sequence = 12, [string]$Kind = 'ClientCharacterRejection') {
    $payload = [byte[]](@($Reason) + @($Transfer))
    return $packetType.GetConstructors()[0].Invoke([object[]]@([Enum]::Parse($kindType, $Kind), $Sequence, $Id, $Challenge, $payload))
}
$accept = $coordinatorType.GetMethod('AcceptClientCharacterRejection', $instance)
$audit = $runtime.GetMethod('RecordConnectionRejection', $static)
$markerMethod = $runtime.GetMethod('GetConnectionRejectionMarker', $static)
$freshFactory = $codec.GetMethod('CreateFreshCharacterRejection', $static)
$decode = $codec.GetMethods($static) | Where-Object { $_.Name -eq 'TryDecode' -and $_.GetParameters()[0].ParameterType -eq [ZPackage] }
$package = $freshFactory.Invoke($null, [object[]]@($sessionId, $nonce, $messageId, $limits))
$decodeArgs = [object[]]@($package, $limits, $null, $null)
Assert-True ($decode.Invoke($null, $decodeArgs) -and $decodeArgs[2].Payload.Length -eq 17 -and $decodeArgs[2].Sequence -eq 12) 'Fresh refusal must round-trip with only a fixed code and transfer ID.'
$bytes = [byte[]]$package.GetArray()
$bytes[4] = 16
$decodeArgs = [object[]]@([ZPackage]::new($bytes), $limits, $null, $null)
Assert-True (-not $decode.Invoke($null, $decodeArgs) -and $decodeArgs[3].Code.ToString() -eq 'ProtocolVersionMismatch') 'Previous wire version must fail closed.'
Assert-True ($decodeArgs[3].GetType().GetProperty('AuditDetail', $instance).GetValue($decodeArgs[3]).Contains('reported 16')) 'Protocol version details must retain the observed version locally.'
$fixture = New-Fixture
$result = $accept.Invoke($coordinator, [object[]]@($null, $fixture.Rpc, (New-Refusal), $true))
Assert-True (-not $result.Succeeded -and $result.Rejection.Code.ToString() -eq 'ClientCharacterRejected' -and $result.Session.State.ToString() -eq 'Rejected') 'A valid client refusal must reject, never acknowledge readiness.'
Assert-True ($result.Session.GetType().GetProperty('StateBeforeRejection', $instance).GetValue($result.Session).ToString() -eq 'CharacterSent') 'Terminal rejection must preserve its prior stage.'
$releaseArgs = [object[]]@($null, $fixture.Rpc, $null)
Assert-True (-not $coordinatorType.GetMethod('CanReleaseWorld').Invoke($coordinator, $releaseArgs)) 'Refusal cannot release world data.'
$duplicate = $accept.Invoke($coordinator, [object[]]@($null, $fixture.Rpc, (New-Refusal), $true))
Assert-True (-not $duplicate.Succeeded -and $duplicate.Session.State.ToString() -eq 'Rejected') 'A repeated refusal must remain terminal.'
$audit.Invoke($null, [object[]]@($fixture.Rpc, $result.Rejection, 'client_reported', $null)) | Out-Null
$audit.Invoke($null, [object[]]@($fixture.Rpc, $duplicate.Rejection, 'server_observed', $null)) | Out-Null
Assert-True ([ConnectionRejectionProbe]::Records.Count -eq 1 -and [ConnectionRejectionProbe]::Records[0][5] -eq 'client_reported' -and
    [ConnectionRejectionProbe]::Records[0][6] -eq 'unavailable') 'One attempt must audit once; missing server identity cannot be fabricated.'

$wrongId = [byte[]]$sessionId.Clone(); $wrongId[0] = 99
$wrongNonce = [byte[]]$nonce.Clone(); $wrongNonce[0] = 99
$wrongTransfer = [byte[]]$messageId.Clone(); $wrongTransfer[0] = 99
foreach ($test in @(
    @{ Packet = (New-Refusal -Id $wrongId); Code = 'SessionIdMismatch' },
    @{ Packet = (New-Refusal -Challenge $wrongNonce); Code = 'NonceMismatch' },
    @{ Packet = (New-Refusal -Transfer $wrongTransfer); Code = 'CharacterTransferIdMismatch' },
    @{ Packet = (New-Refusal -Reason 2); Code = 'InvalidTransition' },
    @{ Packet = (New-Refusal -Sequence 11); Code = 'OutOfOrderMessage' },
    @{ Packet = (New-Refusal -Kind 'ReadyAck'); Code = 'UnexpectedMessageType' }
)) {
    $bad = New-Fixture
    $rejected = $accept.Invoke($coordinator, [object[]]@($null, $bad.Rpc, $test.Packet, $true))
    Assert-True ($rejected.Rejection.Code.ToString() -eq $test.Code) "Malformed refusal must reject as protocol evidence: $($test.Code)"
}
foreach ($state in @('Connected', 'Challenged', 'ManifestValidated', 'Ready')) {
    $bad = New-Fixture $state
    $rejected = $accept.Invoke($coordinator, [object[]]@($null, $bad.Rpc, (New-Refusal), $true))
    Assert-True ($rejected.Rejection.Code.ToString() -ne 'ClientCharacterRejected') 'Only the initial CharacterSent phase may report the local guard.'
}
foreach ($caseFlags in @(@($false, $true, $true), @($true, $false, $true), @($true, $true, $false))) {
    $bad = New-Fixture -Authenticated $caseFlags[0] -Characters $caseFlags[1]
    $rejected = $accept.Invoke($coordinator, [object[]]@($null, $bad.Rpc, (New-Refusal), $caseFlags[2]))
    Assert-True ($rejected.Rejection.Code.ToString() -eq 'InvalidTransition') 'Authentication, character mode and pinned fresh origin are all required.'
}
$ready = New-Fixture 'Ready'
$before = [ConnectionRejectionProbe]::Records.Count
$audit.Invoke($null, [object[]]@($ready.Rpc, $result.Rejection, 'server_observed', $null)) | Out-Null
Assert-True ([ConnectionRejectionProbe]::Records.Count -eq $before + 1) 'Ready ACK before initial commit/world release must still audit admission failure.'
$admitted = New-Fixture 'Ready'
$marker = $markerMethod.Invoke($null, @($admitted.Rpc))
$marker.GetType().GetField('WorldReleased', $instance).SetValue($marker, $true)
$audit.Invoke($null, [object[]]@($admitted.Rpc, $result.Rejection, 'server_observed', $null)) | Out-Null
Assert-True ([ConnectionRejectionProbe]::Records.Count -eq $before + 1) 'An already admitted player must not receive a duplicate connection audit for a later save/security failure.'
[ConnectionRejectionProbe]::ThrowAudit = $true
$sinkFailure = New-Fixture
$audit.Invoke($null, [object[]]@($sinkFailure.Rpc, $result.Rejection, 'server_observed', $null)) | Out-Null
[ConnectionRejectionProbe]::ThrowAudit = $false
Assert-True $true 'A failed optional audit sink must not throw into rejection handling.'
$clientType = $runtime.GetNestedType('ClientConnection', $instance)
$client = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($clientType)
foreach ($pair in @{ Rpc = $fixture.Rpc; SessionId = $sessionId; Nonce = $nonce; ManifestAccepted = $true }.GetEnumerator()) {
    $clientType.GetField('<' + $pair.Key + '>k__BackingField', $instance).SetValue($client, $pair.Value)
}
$report = $runtime.GetMethod('ReportClientFreshCharacterRejection', $static)
$report.Invoke($null, [object[]]@($client, $messageId)) | Out-Null
$report.Invoke($null, [object[]]@($client, $messageId)) | Out-Null
Assert-True ([ConnectionRejectionProbe]::Sends -eq 1 -and -not $clientType.GetProperty('ReadyAcknowledgementSent', $instance).GetValue($client)) 'Best-effort failed report must send once and never acknowledge readiness.'

# A refusal audit must identify the actual mismatch without copying raw
# diagnostic messages. Only the local detail may contain comparison hashes.
function New-Model([string]$Name, [object[]]$Arguments) {
    $type = $plugin.GetType('ServerManager.' + $Name, $true)
    $ctor = $type.GetConstructors($instance) | Where-Object { $_.GetParameters().Count -eq $Arguments.Count } | Select-Object -First 1
    return $ctor.Invoke($Arguments)
}
function New-ModelList([string]$Name) {
    $type = [Collections.Generic.List``1].MakeGenericType($plugin.GetType('ServerManager.' + $Name, $true))
    return ,([Activator]::CreateInstance($type))
}
$rules = New-ModelList 'IntegrityPolicyRule'
$entries = New-ModelList 'IntegrityManifestEntry'
$diagnostics = New-ModelList 'IntegrityDiagnostic'
$required = [Enum]::Parse($plugin.GetType('ServerManager.IntegrityRequirement', $true), 'Required')
$allowedA = 'a' * 64; $allowedB = 'b' * 64; $allowedD = 'd' * 64; $observed = 'c' * 64
foreach ($number in 0..9) {
    $guid = 'fixture.plugin.' + $number
    $name = 'Plugin ' + $number
    if ($number -ne 1) {
        $rules.Add((New-Model 'IntegrityPolicyRule' ([object[]]@($guid, $name, $required, [string[]]@($allowedA, $allowedB, $allowedD)))))
    }
    if ($number -ne 0) {
        $entries.Add((New-Model 'IntegrityManifestEntry' ([object[]]@($guid, $name, $observed))))
    }
    $code = if ($number -eq 0) { 'validation.required_plugin_missing' } elseif ($number -eq 1) { 'validation.unlisted_plugin_present' } else { 'validation.hash_not_allowed' }
    $diagnostics.Add((New-Model 'IntegrityDiagnostic' ([object[]]@($code, 'NEVER_COPY_DIAGNOSTIC https://secret.invalid/token C:\secret\file.dll', $guid))))
}
$policy = New-Model 'IntegrityPolicySnapshot' ([object[]]@([long]1, $rules))
$manifest = New-Model 'IntegrityManifest' ([object[]]@(,$entries))
$integrity = $plugin.GetType('ServerManager.ServerIntegrityService', $true)
$detail = $integrity.GetMethod('BuildRejectionAuditDetail', $static).Invoke($null, [object[]]@($policy, $manifest, $diagnostics))
$summary = $integrity.GetMethod('BuildRejectionPluginSummary', $static).Invoke($null, [object[]]@($policy, $manifest, $diagnostics))
Assert-True ($detail.Length -le 4096 -and $summary.Length -le 1024) 'Mismatch detail and optional plugin summary must obey independent limits.'
foreach ($text in @($detail, $summary)) {
    foreach ($fragment in @('Plugin 0', 'fixture.plugin.0', 'mismatch=missing', 'mismatch=unlisted', 'mismatch=hash_not_allowed', 'mismatches_shown=5; mismatches_omitted=5')) {
        Assert-True ($text.Contains($fragment)) "Structured mismatch summary lost $fragment."
    }
    Assert-True (-not $text.Contains('NEVER_COPY_DIAGNOSTIC') -and -not $text.Contains('secret.invalid') -and -not $text.Contains('file.dll')) 'Raw diagnostic paths/secrets must never be copied into summaries.'
}
Assert-True ($detail.Contains($allowedA) -and $detail.Contains($allowedB) -and $detail.Contains($observed) -and
    -not $detail.Contains($allowedD) -and $detail.Contains('expected_sha256_omitted=1')) 'Local mismatch detail must show bounded allowed and reported SHA values plus omitted hash count.'
Assert-True ($summary -notmatch '[a-fA-F0-9]{64}' -and -not $summary.Contains('expected_sha256') -and -not $summary.Contains('reported_sha256')) 'Optional webhook plugin summary must not contain comparison hashes.'
$withAudit = $result.Rejection.GetType().GetMethod('WithConnectionAudit', $instance)
$privateRejection = $withAudit.Invoke($result.Rejection, [object[]]@('character', 'fixture_reason', 'PRIVATE_AUDIT_DETAIL', 'PRIVATE_PLUGIN_SUMMARY', 'character_apply'))
$rejectFactory = $codec.GetMethod('CreateReject', $static)
$rejectPackage = $rejectFactory.Invoke($null, [object[]]@($sessionId, $nonce, $privateRejection, $limits))
$wireText = [Text.Encoding]::UTF8.GetString($rejectPackage.GetArray())
Assert-True (-not $wireText.Contains('PRIVATE_AUDIT_DETAIL') -and -not $wireText.Contains('PRIVATE_PLUGIN_SUMMARY')) 'Audit metadata must never serialize into the client rejection packet.'

Write-Output "Connection rejection wire, phase, lifetime, replay, mismatch privacy and optional-sink smoke tests passed ($script:assertions assertions)."
