param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
Assert-True (Test-Path -LiteralPath $pluginPath) 'Build ServerManager before running this smoke test.'
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.dll', 'assembly_utils.dll',
    'SoftReferenceableAssets.dll', 'com.rlabrecque.steamworks.net.dll', 'Splatform.dll', 'assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (Split-Path -Parent $pluginPath) $name))) | Out-Null
}
$assembly = [Reflection.Assembly]::LoadFrom($pluginPath)
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$validationType = $assembly.GetType('ServerManager.GameplayLimitValidation', $true)
$shouldBlock = $validationType.GetMethod('ShouldBlockDamage', $staticFlags)
$isValidDamage = $validationType.GetMethod('IsValidDamage', $staticFlags)
$hitType = [HitData]
$damageField = $hitType.GetField('m_damage', $instanceFlags)
$multiplierField = $hitType.GetField('m_backstabBonus', $instanceFlags)

function New-TestHit([string]$Component = 'm_damage', [single]$Damage = 60000, [single]$Multiplier = 1) {
    $hit = [Activator]::CreateInstance($hitType)
    $components = $damageField.GetValue($hit)
    $components.GetType().GetField($Component, $instanceFlags).SetValue($components, $Damage)
    $damageField.SetValue($hit, $components)
    $multiplierField.SetValue($hit, $Multiplier)
    return $hit
}
function Test-Blocked($Hit, [bool]$Admin) {
    return $shouldBlock.Invoke($null, [object[]]@($Hit, [single]55000, $Admin))
}

# The same pure decision is wired into both listen-host and remote-client damage
# prefixes below. It must waive only finite caps, never malformed HitData.
foreach ($hit in @((New-TestHit), (New-TestHit 'm_fire' 10000 6),
    (New-TestHit 'm_spirit' ([single]::MaxValue) ([single]::MaxValue)))) {
    Assert-True ($isValidDamage.Invoke($null, [object[]]@($hit))) 'Finite oversized damage must remain structurally valid.'
    Assert-True (Test-Blocked $hit $false) 'A non-admin can exceed the damage cap.'
    Assert-True (-not (Test-Blocked $hit $true)) 'An admin cannot bypass a finite damage cap.'
}
foreach ($admin in @($false, $true)) {
    Assert-True (-not (Test-Blocked (New-TestHit 'm_damage' 55000) $admin)) 'Damage exactly at the cap was rejected.'
    Assert-True (-not (Test-Blocked $null $admin)) 'A null hit changed the existing no-damage behavior.'
    foreach ($component in @('m_damage', 'm_blunt', 'm_slash', 'm_pierce', 'm_chop', 'm_pickaxe',
        'm_fire', 'm_frost', 'm_lightning', 'm_poison', 'm_spirit')) {
        foreach ($invalid in @([single]::NaN, [single]::PositiveInfinity, [single]::NegativeInfinity, [single]-1)) {
            $hit = New-TestHit $component $invalid
            Assert-True (-not $isValidDamage.Invoke($null, [object[]]@($hit))) "Invalid component $component was structurally accepted."
            Assert-True (Test-Blocked $hit $admin) "Invalid component $component bypassed damage blocking (admin=$admin)."
        }
    }
    foreach ($invalid in @([single]::NaN, [single]::PositiveInfinity, [single]::NegativeInfinity, [single]-1)) {
        Assert-True (Test-Blocked (New-TestHit 'm_damage' 1 $invalid) $admin) "An invalid multiplier bypassed damage blocking (admin=$admin)."
    }
}

$evidenceType = $assembly.GetType('ServerManager.DetectionEvidence', $true)
$catalogType = $assembly.GetType('ServerManager.DetectionEvidenceCatalog', $true)
$isNumeric = $catalogType.GetMethod('IsGameplayLimit', $staticFlags)
$isReportable = $catalogType.GetMethod('IsClientReportable', $staticFlags)
$numericNames = @('CarryWeightLimitExceeded', 'MaximumDamageLimitExceeded',
    'MaximumHealthLimitExceeded', 'MaximumStaminaLimitExceeded', 'MaximumEitrLimitExceeded')
foreach ($evidence in [Enum]::GetValues($evidenceType)) {
    Assert-True ($isNumeric.Invoke($null, [object[]]@($evidence)) -eq ($numericNames -contains $evidence.ToString())) `
        "The numeric admin exemption incorrectly classifies $evidence."
}
$invalidEvidence = [Enum]::Parse($evidenceType, 'InvalidGameplayValue')
Assert-True ([int]$invalidEvidence -eq 406 -and $isReportable.Invoke($null, [object[]]@($invalidEvidence))) `
    'InvalidGameplayValue must stay client-reportable wire evidence 406, outside all numeric exemptions.'

# Real expiring guard state supplies the pure damage decision. No Unity player,
# Steam session, native process, or real adminlist is constructed by this test.
$guardType = $assembly.GetType('ServerManager.CheatCommandGuard', $true)
$configure = $guardType.GetMethod('Configure', $staticFlags)
$applyGrant = $guardType.GetMethod('ApplyAdminEntitlement', $staticFlags)
$hasGrant = $guardType.GetMethod('HasActiveAdminEntitlement', $staticFlags)
$reset = $guardType.GetMethod('Reset', $staticFlags)
$expiry = $guardType.GetField('_adminEntitlementExpiryTimestamp', $staticFlags)
$oversizedHit = New-TestHit
try {
    $null = $configure.Invoke($null, [object[]]@($true, $true, $true))
    Assert-True (Test-Blocked $oversizedHit ($hasGrant.Invoke($null, $null))) 'An unentitled client bypassed a finite damage cap.'
    $null = $applyGrant.Invoke($null, [object[]]@($true, 10000))
    Assert-True (-not (Test-Blocked $oversizedHit ($hasGrant.Invoke($null, $null)))) 'A current entitlement did not release finite damage.'
    Assert-True (Test-Blocked (New-TestHit 'm_fire' ([single]::NaN)) ($hasGrant.Invoke($null, $null))) `
        'A current entitlement released malformed damage.'
    $expiry.SetValue($null, [Diagnostics.Stopwatch]::GetTimestamp() - 1)
    Assert-True (Test-Blocked $oversizedHit ($hasGrant.Invoke($null, $null))) 'An expired entitlement still released finite damage.'
    $null = $applyGrant.Invoke($null, [object[]]@($true, 10000))
    $null = $applyGrant.Invoke($null, [object[]]@($false, 0))
    Assert-True (Test-Blocked $oversizedHit ($hasGrant.Invoke($null, $null))) 'A revoked entitlement still released finite damage.'
    $null = $configure.Invoke($null, [object[]]@($true, $true, $false))
    $null = $applyGrant.Invoke($null, [object[]]@($true, 10000))
    Assert-True (Test-Blocked $oversizedHit ($hasGrant.Invoke($null, $null))) 'A disabled pinned admin policy accepted a grant.'
}
finally { $null = $reset.Invoke($null, $null) }

# Cancellation must restore only its own inbound gate. A discarded socket or
# one that never reached world readiness must never be reopened by promotion.
$socketType = $assembly.GetType('ServerManager.BufferedWorldSocket', $true)
$releaseDetection = $socketType.GetMethod('ReleaseDetectionOnlyInbound', $instanceFlags)
$protectedSocketFields = @('_finalSaveInboundRestricted', '_finalSaveOutboundRestricted',
    '<Overflowed>k__BackingField', '<InboundViolation>k__BackingField')
foreach ($released in @($false, $true)) {
    foreach ($discarded in @($false, $true)) {
        $socket = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($socketType)
        $socketType.GetField('_gate', $instanceFlags).SetValue($socket, [object]::new())
        $socketType.GetField('_released', $instanceFlags).SetValue($socket, $released)
        $socketType.GetField('_discarded', $instanceFlags).SetValue($socket, $discarded)
        $socketType.GetField('_detectionOnlyInbound', $instanceFlags).SetValue($socket, $true)
        foreach ($name in $protectedSocketFields) { $socketType.GetField($name, $instanceFlags).SetValue($socket, $true) }
        $null = $releaseDetection.Invoke($socket, $null)
        Assert-True ($socketType.GetField('_detectionOnlyInbound', $instanceFlags).GetValue($socket) -eq
            (-not $released -or $discarded)) 'Detection cancellation reopened a pre-ready or quarantined socket.'
        Assert-True ($socketType.GetField('_released', $instanceFlags).GetValue($socket) -eq $released -and
            $socketType.GetField('_discarded', $instanceFlags).GetValue($socket) -eq $discarded) `
            'Detection cancellation changed world-readiness or quarantine state.'
        foreach ($name in $protectedSocketFields) {
            Assert-True ($socketType.GetField($name, $instanceFlags).GetValue($socket)) "Detection cancellation cleared unrelated gate $name."
        }
    }
}

$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
function Get-Definition([string]$TypeName, [string]$MethodName) {
    $type = $definition.MainModule.Types | Where-Object FullName -eq $TypeName | Select-Object -First 1
    $method = $type.Methods | Where-Object Name -eq $MethodName | Select-Object -First 1
    Assert-True ($null -ne $method -and $method.HasBody) "Missing runtime method $TypeName.$MethodName."
    return $method
}
function Get-Call($Method, [string]$Name) {
    return $Method.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $Name
    } | Select-Object -First 1
}
. (Join-Path $PSScriptRoot 'CecilControlFlow.ps1')
function Assert-Dominates($Method, $Guard, $Target, [string]$Message) {
    Assert-True ($null -ne $Guard -and $null -ne $Target -and
        -not (Test-CecilReachable $Method.Body.Instructions[0] $Target @($Guard))) $Message
}
function Assert-PredicateControls($Predicate, $Target, [string]$Message) {
    $branch = $Predicate.Next
    while ($null -ne $branch -and $branch.Offset -lt $Target.Offset -and
        $branch.OpCode.FlowControl.ToString() -ne 'Cond_Branch') { $branch = $branch.Next }
    Assert-True ($null -ne $branch -and $branch.Offset -lt $Target.Offset -and
        ((Test-CecilReachable $branch.Operand $Target) -xor (Test-CecilReachable $branch.Next $Target))) $Message
}
$runtimeName = 'ServerManager.ServerManagerRuntime'
$numericBypass = Get-Definition $runtimeName 'HasNumericStatLimitAdminBypass'
$liveAdmin = Get-Call $numericBypass 'IsCurrentServerAdmin'
foreach ($gate in @('IsGameplayLimit', 'get_Policy', 'get_AllowAdminCheatCommands')) {
    Assert-Dominates $numericBypass (Get-Call $numericBypass $gate) $liveAdmin "Numeric bypass skipped pinned gate $gate."
}
foreach ($gate in @('IsGameplayLimit', 'get_AllowAdminCheatCommands')) {
    Assert-PredicateControls (Get-Call $numericBypass $gate) $liveAdmin "False numeric/admin policy gate $gate still checks for an exemption."
}
$adminCheck = Get-Definition $runtimeName 'IsCurrentServerAdmin'
$boundAdminHost = Get-Call $adminCheck 'get_HostId'
$nativeAdminCheck = Get-Call $adminCheck 'IsAdmin'
$liveAdminList = Get-Call $adminCheck 'GetAdminList'
$steamAliasCheck = Get-Call $adminCheck 'GetSteamListEntries'
Assert-Dominates $adminCheck $boundAdminHost $nativeAdminCheck `
    'Admin membership no longer uses the authenticated connection host ID.'
Assert-True ($null -ne $liveAdminList -and $null -ne $steamAliasCheck -and
    $nativeAdminCheck.Offset -lt $liveAdminList.Offset -and
    $liveAdminList.Offset -lt $steamAliasCheck.Offset) `
    'Valheim 1.0 Steam-prefix fallback no longer checks the live server-owned admin list after native IsAdmin.'
# The local-host guard is executed below. A reachability-only CFG check invents
# impossible paths through Debug Boolean locals and cannot prove this guard.
$hostDamage = Get-Definition $runtimeName 'BeforeLocalPlayerDamage'
Assert-Dominates $hostDamage (Get-Call $hostDamage 'IsCurrentLocalHostStatLimitAdmin') (Get-Call $hostDamage 'ShouldBlockDamage') `
    'Listen-host damage does not use the shared decision and current adminlist membership.'
$clientDamage = Get-Definition 'ServerManager.ClientDetectionAgent' 'ShouldAllowLocalPlayerDamage'
$clientDecision = Get-Call $clientDamage 'ShouldBlockDamage'
foreach ($gate in @('get_AllowAdminCheatCommands', 'IsValidDamage')) {
    Assert-Dominates $clientDamage (Get-Call $clientDamage $gate) $clientDecision "Client damage skipped $gate."
}
$clientEntitlement = Get-Call $clientDamage 'HasActiveAdminEntitlement'
Assert-True ($null -ne $clientEntitlement -and $clientEntitlement.Offset -lt $clientDecision.Offset -and
    $clientEntitlement.Operand.DeclaringType.FullName -eq 'ServerManager.CheatCommandGuard') `
    'Client damage does not use the expiring command-guard entitlement.'
Assert-PredicateControls (Get-Call $clientDamage 'get_AllowAdminCheatCommands') $clientEntitlement `
    'Client damage checks an entitlement despite a disabled pinned admin policy.'
$entitlementHandler = Get-Definition $runtimeName 'HandleClientAdminEntitlement'
$applyEntitlementCall = Get-Call $entitlementHandler 'ApplyAdminEntitlement'
foreach ($gate in @('TryDecode', 'get_ReadyAcknowledgementSent', 'get_NextAdminEntitlementSequence', 'set_NextAdminEntitlementSequence')) {
    Assert-Dominates $entitlementHandler (Get-Call $entitlementHandler $gate) $applyEntitlementCall `
        "Client stat entitlement bypassed authenticated handler gate $gate."
}
$identityChecks = @($entitlementHandler.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'FixedTimeEquals'
})
Assert-True ($identityChecks.Count -eq 2) 'The stat entitlement lost session/nonce authentication.'
foreach ($check in $identityChecks) {
    Assert-Dominates $entitlementHandler $check $applyEntitlementCall 'A stat entitlement can bypass session/nonce authentication.'
}
$routedDamage = Get-Definition $runtimeName 'BeforeServerRoutedRpcDamage'
$routedBypass = Get-Call $routedDamage 'HasNumericStatLimitAdminBypass'
foreach ($gate in @('TryGetSnapshot', 'get_State', 'get_PeerInfoAuthenticated', 'TryResolveActiveDetectionPeer', 'IsValidDamage')) {
    # Malformed paths can bypass IsValidDamage when framing already failed, but
    # no admin-allow decision may precede its structural validity inspection.
    if ($gate -eq 'IsValidDamage') {
        $validityCall = Get-Call $routedDamage $gate
        Assert-True ($null -ne $validityCall -and $validityCall.Offset -lt $routedBypass.Offset) 'Admin routed damage skipped structural validation.'
    }
    else { Assert-Dominates $routedDamage (Get-Call $routedDamage $gate) $routedBypass "Server stat bypass skipped authenticated-peer gate $gate." }
}
$applyAction = Get-Definition $runtimeName 'ApplyDetectionAction'
Assert-Dominates $applyAction (Get-Call $applyAction 'HasNumericStatLimitAdminBypass') (Get-Call $applyAction 'LogDetection') `
    'Numeric response can be applied before the fresh admin check.'
$terminal = Get-Definition $runtimeName 'ExecuteTerminalDetectionAction'
$terminalBypass = Get-Call $terminal 'TryCancelNumericDetectionResponseForAdmin'
foreach ($terminalOperation in @('GetBannedList', 'SendServerRejection', 'InternalKick')) {
    Assert-Dominates $terminal $terminalBypass (Get-Call $terminal $terminalOperation) `
        "Pending numeric sanction skipped current admin revalidation before $terminalOperation."
}
Assert-PredicateControls $terminalBypass (Get-Call $terminal 'InternalKick') `
    'Successful numeric cancellation still reaches the terminal kick.'
$cancel = Get-Definition $runtimeName 'TryCancelNumericDetectionResponseForAdmin'
$cancelGate = Get-Call $cancel 'HasNumericStatLimitAdminBypass'
$release = Get-Call $cancel 'ReleaseDetectionOnlyInbound'
foreach ($operation in @('set_PendingKickTimestamp', 'ReleaseDetectionOnlyInbound',
    'RecordNumericStatLimitAdminBypass', 'set_TerminalEvidence')) {
    Assert-Dominates $cancel $cancelGate (Get-Call $cancel $operation) "Numeric cancellation skipped live numeric/admin validation before $operation."
    Assert-PredicateControls $cancelGate (Get-Call $cancel $operation) "A rejected numeric cancellation can mutate state through $operation."
}
$cancelAudit = Get-Call $cancel 'RecordNumericStatLimitAdminBypass'
Assert-True ($release.Offset -lt $cancelAudit.Offset) 'Numeric cancellation did not release its inbound gate before recording success.'
$refresh = Get-Definition $runtimeName 'TryRefreshAdminCommandEntitlement'
foreach ($member in @('get_LastAdminEntitlementGranted', 'set_LastAdminEntitlementGranted', 'get_PolicyGeneration', 'set_PolicyGeneration')) {
    Assert-True ($null -ne (Get-Call $refresh $member)) "Admin membership changes no longer re-arm observations through $member."
}
$refreshCancellation = Get-Call $refresh 'TryCancelNumericDetectionResponseForAdmin'
$generationSetter = Get-Call $refresh 'set_PolicyGeneration'
Assert-True ($null -ne $refreshCancellation -and $refreshCancellation.Offset -lt $generationSetter.Offset -and
    $refreshCancellation.Offset -lt (Get-Call $refresh 'Send').Offset) `
    'Admin promotion advertises its new generation/entitlement before cancelling a pending numeric kick.'
Assert-Dominates $refresh (Get-Call $refresh 'TryResolveActiveDetectionPeer') $refreshCancellation `
    'Admin promotion cancels a pending kick before revalidating the authenticated peer.'
Assert-PredicateControls (Get-Call $refresh 'IsCurrentServerAdmin') $refreshCancellation `
    'A non-admin entitlement refresh can cancel a pending response.'

# Keep a pending numeric kick from swallowing an update ACK while it is still
# filtering inbound traffic. Restrict this CFG traversal to one loop iteration.
$synchronize = Get-Definition $runtimeName 'SynchronizeServerPolicies'
$pendingGuard = Get-Call $synchronize 'get_PendingKickTimestamp'
$policySend = Get-Call $synchronize 'SendProtocolOrThrow'
Assert-Dominates $synchronize $pendingGuard $policySend 'A policy update can be sent without checking for a pending kick.'
$pendingBranch = $pendingGuard.Next
while ($null -ne $pendingBranch -and $pendingBranch.OpCode.FlowControl.ToString() -ne 'Cond_Branch') {
    $pendingBranch = $pendingBranch.Next
}
Assert-True ($null -ne $pendingBranch -and
    ((Test-CecilReachable $pendingBranch.Operand $policySend @($pendingGuard)) -xor
     (Test-CecilReachable $pendingBranch.Next $policySend @($pendingGuard)))) `
    'A pending kick does not skip the policy update send for its connection.'

# Invoke the compiled cancellation helper directly for every ineligible evidence
# class. These paths must return before any Unity/Steam access, without mutating
# pending sanctions. Numeric evidence with a disabled pinned policy is likewise
# ineligible. No copied state-machine implementation or substituted method body.
$runtimeType = $assembly.GetType($runtimeName, $true)
$cancelMethod = $runtimeType.GetMethod('TryCancelNumericDetectionResponseForAdmin', $staticFlags)
$stateType = $assembly.GetType('ServerManager.ServerManagerRuntime+ServerDetectionState', $true)
$policyType = $assembly.GetType('ServerManager.ProtocolChallengeOptions', $true)
foreach ($evidence in [Enum]::GetValues($evidenceType)) {
    $state = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($stateType)
    $policy = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($policyType)
    $policyType.GetProperty('AllowAdminCheatCommands', $instanceFlags).SetValue($policy,
        ($numericNames -notcontains $evidence.ToString()), $null)
    $stateType.GetProperty('Policy', $instanceFlags).SetValue($state, $policy, $null)
    $stateType.GetProperty('TerminalEvidence', $instanceFlags).SetValue($state, $evidence, $null)
    $stateType.GetProperty('TerminalSource', $instanceFlags).SetValue($state, 'client_reported', $null)
    $stateType.GetProperty('PendingKickTimestamp', $instanceFlags).SetValue($state, [long]123, $null)
    Assert-True (-not $cancelMethod.Invoke($null, [object[]]@($null, $null, $null, $state))) `
        "Cancellation accepted ineligible evidence/policy $evidence."
    Assert-True ($stateType.GetProperty('PendingKickTimestamp', $instanceFlags).GetValue($state, $null) -eq 123 -and
        $stateType.GetProperty('TerminalEvidence', $instanceFlags).GetValue($state, $null) -eq $evidence -and
        $stateType.GetProperty('TerminalSource', $instanceFlags).GetValue($state, $null) -ceq 'client_reported' -and
        -not $stateType.GetProperty('TerminalActionApplied', $instanceFlags).GetValue($state, $null)) `
        "Cancellation changed an ineligible pending sanction for $evidence."
}
$applySource = [IO.File]::ReadAllText((Join-Path $projectRoot 'Networking\ServerManagerRuntime.cs'))
Assert-True ([Text.RegularExpressions.Regex]::IsMatch($applySource,
    'state\.PendingKickTimestamp != 0 &&\s*!DetectionEvidenceCatalog\.IsGameplayLimit\(report\.Evidence\)\)\s*' +
    '\{\s*state\.TerminalEvidence = report\.Evidence;\s*state\.TerminalSource = source;')) `
    'A pending numeric kick can mask a later non-numeric sanction during admin promotion.'
$definition.Dispose()

# Run the compiled local-host guard in memory with only the environment calls
# replaced. Its branches, Boolean locals, canonical Steam parser, string data
# flow and exception filter stay intact; no game/native method is invoked.
$probeReferences = @((Join-Path $managedRoot 'com.rlabrecque.steamworks.net.dll'))
if ($PSVersionTable.PSEdition -eq 'Core') {
    $probeReferences += @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
}
Add-Type -ReferencedAssemblies $probeReferences -TypeDefinition @'
using System;
using Steamworks;
public static class LocalHostAdminProbe {
    public static bool Server, Dedicated, Admin, ThrowSteam;
    public static int Backend, SteamReads, AdminReads;
    public static ulong SteamId;
    public static string AdminHostId;
    public static bool IsServer(object server) { return Server; }
    public static bool IsDedicated(object server) { return Dedicated; }
    public static CSteamID GetSteamID() {
        SteamReads++;
        if (ThrowSteam) throw new InvalidOperationException("fixture Steam unavailable");
        return new CSteamID(SteamId);
    }
    public static bool IsAdmin(object server, string hostId) {
        AdminReads++; AdminHostId = hostId; return Admin;
    }
}
'@
function New-LocalHostAdminFixture([switch]$OmitDedicatedGuard) {
    $fixture = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
    $stream = [IO.MemoryStream]::new()
    try {
        $fixture.Name.Name = 'LocalHostAdminFixture_' + [Guid]::NewGuid().ToString('N')
        $runtime = $fixture.MainModule.Types | Where-Object FullName -eq $runtimeName
        $initializer = $runtime.Methods | Where-Object Name -eq '.cctor'
        $initializer.Body.Instructions.Clear()
        $initializer.Body.ExceptionHandlers.Clear()
        $initializer.Body.Variables.Clear()
        $initializer.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
        $guard = $runtime.Methods | Where-Object Name -eq 'IsCurrentLocalHostStatLimitAdmin'
        foreach ($name in @('IsServer', 'IsDedicated', 'GetSteamID', 'IsAdmin')) {
            $call = Get-Call $guard $name
            Assert-True ($null -ne $call) "Local-host guard lost environment call $name."
            $call.OpCode = [Mono.Cecil.Cil.OpCodes]::Call
            $call.Operand = $fixture.MainModule.ImportReference([LocalHostAdminProbe].GetMethod($name))
            if ($OmitDedicatedGuard -and $name -eq 'IsDedicated') {
                # Deliberate broken fixture: consume the predicate and always
                # continue as a listen host. The decision matrix must reject it.
                Assert-True ($call.Next.OpCode.Name -in @('brtrue', 'brtrue.s')) `
                    'The missing-dedicated-guard fixture needs its branch updated.'
                $call.Next.OpCode = [Mono.Cecil.Cil.OpCodes]::Pop
                $call.Next.Operand = $null
            }
        }
        $backendReads = @($guard.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'm_onlineBackend'
        })
        Assert-True ($backendReads.Count -eq 1) 'Local-host guard lost the current backend read.'
        $backendReads[0].Operand = $fixture.MainModule.ImportReference([LocalHostAdminProbe].GetField('Backend'))
        $fixture.Write($stream)
        $fixtureAssembly = [Reflection.Assembly]::Load($stream.ToArray())
        return $fixtureAssembly.GetType($runtimeName, $true).GetMethod('IsCurrentLocalHostStatLimitAdmin', $staticFlags)
    }
    finally { $stream.Dispose(); $fixture.Dispose() }
}
$hostGuard = New-LocalHostAdminFixture
foreach ($server in @($false, $true)) {
    foreach ($dedicated in @($false, $true)) {
        foreach ($backend in [Enum]::GetValues([OnlineBackendType])) {
            foreach ($steamId in @([uint64]76561198000000001, [uint64]0, [uint64]::MaxValue)) {
                foreach ($admin in @($false, $true)) {
                    [LocalHostAdminProbe]::Server = $server
                    [LocalHostAdminProbe]::Dedicated = $dedicated
                    [LocalHostAdminProbe]::Backend = [int]$backend
                    [LocalHostAdminProbe]::SteamId = $steamId
                    [LocalHostAdminProbe]::Admin = $admin
                    [LocalHostAdminProbe]::SteamReads = 0
                    [LocalHostAdminProbe]::AdminReads = 0
                    [LocalHostAdminProbe]::AdminHostId = $null
                    $eligibleHost = $server -and -not $dedicated -and $backend -eq [OnlineBackendType]::Steamworks
                    $validAccount = $steamId -eq [uint64]76561198000000001
                    $expected = $eligibleHost -and $validAccount -and $admin
                    $case = "server=$server dedicated=$dedicated backend=$backend steam=$steamId admin=$admin"
                    Assert-True ($hostGuard.Invoke($null, [object[]]@($null)) -eq $expected) "Local-host exemption changed: $case."
                    Assert-True ([LocalHostAdminProbe]::SteamReads -eq [int]$eligibleHost -and
                        [LocalHostAdminProbe]::AdminReads -eq [int]($eligibleHost -and $validAccount)) `
                        "Local-host exemption bypassed role/backend/canonical-account validation: $case."
                    if ([LocalHostAdminProbe]::AdminReads -gt 0) {
                        Assert-True ([LocalHostAdminProbe]::AdminHostId -ceq $steamId.ToString([Globalization.CultureInfo]::InvariantCulture)) `
                            'The adminlist lookup no longer uses the current canonical Steam host ID.'
                    }
                }
            }
        }
    }
}
[LocalHostAdminProbe]::Server = $true
[LocalHostAdminProbe]::Dedicated = $false
[LocalHostAdminProbe]::Backend = [int][OnlineBackendType]::Steamworks
[LocalHostAdminProbe]::SteamId = 76561198000000001
[LocalHostAdminProbe]::Admin = $true
[LocalHostAdminProbe]::ThrowSteam = $true
try {
    Assert-True (-not $hostGuard.Invoke($null, [object[]]@($null))) 'Unavailable Steam identity did not deny the local-host exemption.'
}
finally { [LocalHostAdminProbe]::ThrowSteam = $false }
[LocalHostAdminProbe]::Dedicated = $true
Assert-True (-not $hostGuard.Invoke($null, [object[]]@($null))) 'A dedicated server received the local-host exemption.'
$brokenGuard = New-LocalHostAdminFixture -OmitDedicatedGuard
Assert-True ($brokenGuard.Invoke($null, [object[]]@($null))) `
    'The negative-control fixture did not expose the missing dedicated-server guard.'
Write-Output 'Admin stat caps passed: five numeric-only exemptions, malformed damage rejection, expiring entitlements, authenticated live membership, and pending-sanction revalidation.'
