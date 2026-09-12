param(
    [string]$Configuration = "Debug",
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
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

function Assert-SourcePattern {
    param(
        [string]$Source,
        [string]$Pattern,
        [string]$Message
    )

    $matched = [Text.RegularExpressions.Regex]::IsMatch(
        $Source,
        $Pattern,
        [Text.RegularExpressions.RegexOptions]::Singleline)
    Assert-True $matched $Message
}

function Test-Finding {
    param(
        [object]$Findings,
        [string]$Code
    )

    foreach ($finding in $Findings) {
        if (([string]$finding).IndexOf($Code, [StringComparison]::Ordinal) -ge 0) {
            return $true
        }
    }

    return $false
}

function New-GenericList {
    param(
        [Type]$ElementType,
        [object[]]$Values
    )

    $listType = [Collections.Generic.List``1].MakeGenericType($ElementType)
    $list = [Activator]::CreateInstance($listType)
    $add = $listType.GetMethod("Add")
    foreach ($value in $Values) {
        $add.Invoke($list, [object[]]@($value)) | Out-Null
    }

    return ,$list
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
Assert-True (Test-Path -LiteralPath $pluginPath) `
    "Build ServerManager before running this smoke test."

# Check the fixed stored policy and runtime object graph as well as the pure validator
# behavior. The behavioral calls below cannot by themselves prove that the server
# injects the Observe validator into stored-snapshot reads and the Enforce validator
# into incoming commits.
$pluginSource = Get-Content -LiteralPath (Join-Path $projectRoot "Plugin.cs") -Raw
$runtimeSource = Get-Content `
    -LiteralPath (Join-Path $projectRoot "Networking\ServerManagerRuntime.cs") `
    -Raw
$reloadSource = Get-Content -LiteralPath (Join-Path $projectRoot `
    'Networking\ServerManagerRuntime.Settings.cs') -Raw
$serviceSource = Get-Content -LiteralPath (Join-Path $projectRoot `
    'Character\CharacterSnapshotService.cs') -Raw

Assert-SourcePattern `
    $runtimeSource `
    ('CharacterSemanticPolicy\s+storedSnapshotPolicy\s*=\s*' +
        '_storedSnapshotSemanticPolicy\s*\?\?=\s*CreateCharacterSemanticPolicy\(\s*' +
        'CharacterSemanticPolicyMode\.Observe\s*\)') `
    "Stored snapshots no longer use the fixed Observe policy."
Assert-True (-not $pluginSource.Contains("StoredSnapshotPolicyMode") -and
    -not $pluginSource.Contains('"Stored Snapshot Policy"')) `
    "The fixed stored-snapshot policy still has a configurable override."
Assert-True (-not $pluginSource.Contains("IncomingSavePolicyMode") -and
    -not $pluginSource.Contains("IncomingSavePolicyValues") -and
    -not $pluginSource.Contains('"Incoming Save Policy"') -and
    -not $runtimeSource.Contains("ServerManagerPlugin.IncomingSavePolicyMode")) `
    "The fixed incoming policy still reads a configurable or stale selector."
Assert-SourcePattern `
    $runtimeSource `
    ('CharacterSemanticPolicy\s+incomingSavePolicy\s*=\s*' +
        '_incomingSaveSemanticPolicy\s*\?\?=\s*CreateCharacterSemanticPolicy\(\s*' +
        'CharacterSemanticPolicyMode\.Enforce\s*\)') `
    "Incoming saves no longer use fixed Enforce independently of stored Observe."
Assert-SourcePattern `
    $runtimeSource `
    'CharacterSemanticRevisionValidator\s+storedSnapshotValidator\s*=\s*new\s*\(\s*serverProfileCodec,\s*new CharacterSemanticEvaluator\(storedSnapshotPolicy\)\s*\);' `
    "The stored-snapshot validator is not built from the stored policy without an admin resolver."
Assert-SourcePattern `
    $runtimeSource `
    'CharacterSemanticRevisionValidator\s+incomingSaveValidator\s*=\s*new\s*\(\s*serverProfileCodec,\s*new CharacterSemanticEvaluator\(incomingSavePolicy\),\s*IsCharacterPolicyAdmin\s*\);' `
    "The incoming-save validator is not built from Enforce and the trusted runtime admin resolver."
Assert-SourcePattern `
    $runtimeSource `
    'CharacterRepository\s+repository\s*=\s*new\s*\(.*?incomingSaveValidator,\s*IsCharacterCreationAdmin\s*\);' `
    "CharacterRepository is not wired to the incoming-save validator and separate authenticated character-quota resolver."
Assert-SourcePattern `
    $runtimeSource `
    'new CharacterSnapshotService\(.*?semanticValidator:\s*storedSnapshotValidator\s*\);' `
    "CharacterSnapshotService is not wired to the stored-snapshot validator."
Assert-SourcePattern `
    $runtimeSource `
    ('private static CharacterSemanticPolicy CreateCharacterSemanticPolicy\(\s*' +
        'CharacterSemanticPolicyMode mode\s*\)\s*\{\s*return CharacterSemanticPolicy\.FromSettings\(\s*' +
        'CurrentServerSettings,\s*mode\s*\);\s*\}') `
    "Startup no longer uses the shared policy factory."

foreach ($modeSpec in @(
    @{ Variable = 'storedPolicy'; Mode = 'Observe' },
    @{ Variable = 'incomingPolicy'; Mode = 'Enforce' })) {
    Assert-SourcePattern $reloadSource (
        'CharacterSemanticPolicy\s+' + $modeSpec.Variable + '\s*=\s*CharacterSemanticPolicy\.FromSettings\(\s*' +
        'settings,\s*CharacterSemanticPolicyMode\.' + $modeSpec.Mode + '\s*\);') `
        "Live reload no longer uses the shared factory for the fixed $($modeSpec.Mode) policy."
}
Assert-SourcePattern $serviceSource (
    'CharacterSemanticEvaluator\s+stored\s*=\s*CreateSettingsEvaluator\(\s*' +
    'settings,\s*CharacterSemanticPolicyMode\.Observe\s*\);.*?' +
    'CharacterSemanticEvaluator\s+incoming\s*=\s*CreateSettingsEvaluator\(\s*' +
    'settings,\s*CharacterSemanticPolicyMode\.Enforce\s*\);.*?' +
    '_repository\.ApplyServerSettings\(settings, incoming\);.*?validator\.ApplyEvaluator\(stored\);') `
    'Live service reload no longer preserves independent stored Observe and incoming Enforce validators.'
Assert-SourcePattern $serviceSource (
    'new CharacterSemanticEvaluator\(CharacterSemanticPolicy\.FromSettings\(settings,\s*mode\)\)') `
    'Service reload no longer uses the shared policy factory.'

$plugin = [Reflection.Assembly]::LoadFrom($pluginPath)
$policyModeType = $plugin.GetType(
    "ServerManager.CharacterSemanticPolicyMode",
    $true)
$policyType = $plugin.GetType(
    "ServerManager.CharacterSemanticPolicy",
    $true)
$itemType = $plugin.GetType(
    "ServerManager.CharacterSemanticItemState",
    $true)
$skillType = $plugin.GetType(
    "ServerManager.CharacterSemanticSkillState",
    $true)
$snapshotType = $plugin.GetType(
    "ServerManager.CharacterSemanticSnapshot",
    $true)
$evaluatorType = $plugin.GetType(
    "ServerManager.CharacterSemanticEvaluator",
    $true)
$validatorType = $plugin.GetType(
    "ServerManager.CharacterSemanticRevisionValidator",
    $true)
$profileCodecType = $plugin.GetType(
    "ServerManager.ValheimPlayerProfileCodec",
    $true)
$identityType = $plugin.GetType(
    "ServerManager.CharacterIdentity",
    $true)
$envelopeType = $plugin.GetType(
    "ServerManager.CharacterEnvelope",
    $true)
$envelopeKindType = $plugin.GetType(
    "ServerManager.CharacterEnvelopeKind",
    $true)

$policyConstructor = $policyType.GetConstructors() |
    Where-Object { $_.GetParameters().Count -eq 7 } |
    Select-Object -First 1
$itemConstructor = $itemType.GetConstructors() |
    Select-Object -First 1
$skillConstructor = $skillType.GetConstructors() |
    Select-Object -First 1
$snapshotConstructor = $snapshotType.GetConstructors() |
    Where-Object { $_.GetParameters().Count -eq 7 } |
    Select-Object -First 1
$evaluatorConstructor = $evaluatorType.GetConstructors() |
    Select-Object -First 1
$instanceAll = [Reflection.BindingFlags]::Instance -bor
    [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic
$staticAll = [Reflection.BindingFlags]::Static -bor
    [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
$settingsType = $plugin.GetType('ServerManager.ServerSettings', $true)
$settings = $settingsType.GetMethod('Parse', $staticAll).Invoke($null, [object[]]@(
    "forbiddenItems: [ForbiddenSword, ForbiddenBow]`nstatCaps: { health: 1234, stamina: 678, eitr: 321 }"))
$policyFactory = $policyType.GetMethod('FromSettings', $staticAll)
Assert-True ($null -ne $policyFactory) 'The shared semantic policy factory is missing.'
foreach ($mode in @('Observe', 'Enforce')) {
    $policy = $policyFactory.Invoke($null, [object[]]@($settings, [Enum]::Parse($policyModeType, $mode)))
    Assert-True ($policy.Mode.ToString() -eq $mode) 'The factory changed the requested policy mode.'
    Assert-True ($policy.MaximumHealth -eq 1234 -and $policy.MaximumStamina -eq 678 -and
        $policy.MaximumEitr -eq 321) 'The factory did not preserve configured stat limits.'
    Assert-True ($policy.SkillLevelBurstAllowance -eq 2 -and $policy.SkillLevelsPerMinute -eq 10) `
        'The factory changed the fixed skill allowance.'
    Assert-True ($policy.ForbiddenItemPrefabCount -eq 2 -and
        $policyType.GetMethod('IsForbiddenItemPrefab', $instanceAll).Invoke($policy, @('ForbiddenSword')) -and
        -not $policyType.GetMethod('IsForbiddenItemPrefab', $instanceAll).Invoke($policy, @('Wood'))) `
        'The factory changed forbidden-item membership.'
}
$validatorConstructor = $validatorType.GetConstructors($instanceAll) |
    Where-Object {
        $parameters = $_.GetParameters()
        $parameters.Count -eq 2 -and
        $parameters[0].ParameterType -eq $profileCodecType -and
        $parameters[1].ParameterType -eq $evaluatorType
    } |
    Select-Object -First 1
$adminResolverType = [Func``2].MakeGenericType($identityType, [bool])
$adminValidatorConstructor = $validatorType.GetConstructor(
    $instanceAll,
    $null,
    [Type[]]@($profileCodecType, $evaluatorType, $adminResolverType),
    $null)
$evaluateAuthoritative = $validatorType.GetMethod(
    "EvaluateAuthoritative",
    $instanceAll)
$evaluateIncoming = $validatorType.GetMethods($instanceAll) |
    Where-Object {
        $_.Name -eq "Evaluate" -and
        $_.GetParameters().Count -eq 8
    } |
    Select-Object -First 1
$createEnvelope = $envelopeType.GetMethod("Create")

Assert-True ($null -ne $policyConstructor) `
    "The semantic policy constructor is missing."
Assert-True ($null -ne $snapshotConstructor) `
    "The public semantic snapshot constructor is missing."
Assert-True ($null -ne $validatorConstructor) `
    "The independent semantic validator constructor is missing."
Assert-True ($null -ne $adminValidatorConstructor) `
    "The trusted incoming admin resolver constructor is missing."
Assert-True (
    $null -ne $evaluateAuthoritative -and
    $null -ne $evaluateIncoming) `
    "The stored or incoming semantic validation entry point is missing."

function New-Policy {
    param([string]$Mode)

    $arguments = [object[]]::new(7)
    $arguments[0] = [Enum]::Parse($policyModeType, $Mode)
    $arguments[1] = "ForbiddenSword"
    $arguments[2] = [single]1000
    $arguments[3] = [single]1000
    $arguments[4] = [single]1000
    $arguments[5] = [single]2
    $arguments[6] = [single]10
    return $policyConstructor.Invoke($arguments)
}

function New-Snapshot {
    param(
        [object[]]$Items = @(),
        [bool]$HasPlayerData = $true,
        [object[]]$Skills = @()
    )

    $skillList = New-GenericList $skillType $Skills
    $itemsList = New-GenericList $itemType $Items
    $arguments = [object[]]::new(7)
    $arguments[0] = $HasPlayerData
    $arguments[1] = [single]100
    $arguments[2] = [single]100
    $arguments[3] = [single]100
    $arguments[4] = $skillList
    $arguments[5] = $itemsList
    $arguments[6] = [string[]]@()
    return $snapshotConstructor.Invoke($arguments)
}

$itemArguments = [object[]]::new(5)
$itemArguments[0] = "ForbiddenSword"
$itemArguments[1] = 1
$itemArguments[2] = 1
$itemArguments[3] = 0
$itemArguments[4] = [string[]]@()
$forbiddenItem = $itemConstructor.Invoke($itemArguments)

$identity = [Activator]::CreateInstance(
    $identityType,
    [object[]]@("steamworks:76561198000000000", "PolicySplitTest"))
$baseline = New-Snapshot
$violatingSnapshot = New-Snapshot -Items @($forbiddenItem)
$observeEvaluator = $evaluatorConstructor.Invoke(
    [object[]]@((New-Policy "Observe")))
$enforceEvaluator = $evaluatorConstructor.Invoke(
    [object[]]@((New-Policy "Enforce")))

# The codec is deliberately not initialized: supplying currentSemanticSnapshot to
# Evaluate exercises the normal repository seam without parsing a Valheim payload.
$profileCodec = [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
    $profileCodecType)
$storedValidator = $validatorConstructor.Invoke(
    [object[]]@($profileCodec, $observeEvaluator))
$incomingValidator = $validatorConstructor.Invoke(
    [object[]]@($profileCodec, $enforceEvaluator))

$sessionId = [Guid]::NewGuid()
$snapshotKind = [Enum]::Parse($envelopeKindType, "Snapshot")
$saveRequestKind = [Enum]::Parse($envelopeKindType, "SaveRequest")
$currentEnvelope = $createEnvelope.Invoke(
    $null,
    [object[]]@(
        $snapshotKind,
        [long]1,
        [long]0,
        $sessionId,
        $identity,
        [DateTime]::UtcNow,
        43,
        [byte[]]@(1)))
$candidateEnvelope = $createEnvelope.Invoke(
    $null,
    [object[]]@(
        $saveRequestKind,
        [long]2,
        [long]1,
        $sessionId,
        $identity,
        [DateTime]::UtcNow,
        43,
        [byte[]]@(2)))

$storedResult = $evaluateAuthoritative.Invoke(
    $storedValidator,
    [object[]]@($identity, $violatingSnapshot))
Assert-True (-not $storedResult.Rejected) `
    "Stored=Observe rejected an existing authoritative snapshot."
Assert-True (
    (Test-Finding `
        $storedResult.Observations `
        "would_reject:forbidden_prefab")) `
    "Stored=Observe did not retain the forbidden-prefab observation."
Assert-True (
    -not (Test-Finding $storedResult.Violations "forbidden_prefab")) `
    "Stored=Observe emitted the forbidden prefab as a rejection."

$incomingArguments = [object[]]@(
    $identity,
    $currentEnvelope,
    $candidateEnvelope,
    [long]76561198000000000,
    $baseline,
    $violatingSnapshot,
    $baseline,
    [TimeSpan]::FromMinutes(1))
$incomingResult = $evaluateIncoming.Invoke(
    $incomingValidator,
    $incomingArguments)
Assert-True $incomingResult.Rejected `
    "Incoming=Enforce accepted the same forbidden-prefab revision."
Assert-True (
    (Test-Finding $incomingResult.Violations "forbidden_prefab")) `
    "Incoming=Enforce did not emit the forbidden-prefab rejection."
Assert-True (
    -not (Test-Finding `
        $incomingResult.Observations `
        "would_reject:forbidden_prefab")) `
    "Incoming=Enforce downgraded the forbidden prefab to an observation."

# Exercise the opposite entry points as a cross-talk guard: each validator must
# retain its own mode regardless of which validation method is called.
$observeIncomingResult = $evaluateIncoming.Invoke(
    $storedValidator,
    $incomingArguments)
Assert-True (-not $observeIncomingResult.Rejected) `
    "The Observe validator inherited the Enforce validator's mode."
Assert-True (
    (Test-Finding `
        $observeIncomingResult.Observations `
        "would_reject:forbidden_prefab")) `
    "The Observe validator lost its finding after the Enforce validator ran."

$enforceStoredResult = $evaluateAuthoritative.Invoke(
    $incomingValidator,
    [object[]]@($identity, $violatingSnapshot))
Assert-True $enforceStoredResult.Rejected `
    "The Enforce validator inherited the Observe validator's mode."
Assert-True (
    (Test-Finding $enforceStoredResult.Violations "forbidden_prefab")) `
    "The Enforce validator did not retain its policy on the stored entry point."

# Build a typed callback without making the fixture reference Valheim/plugin
# assemblies; its mutable answer proves privileges are not cached per profile.
Add-Type -TypeDefinition @"
using System;
using System.Reflection;

public sealed class SemanticAdminPolicyProbe
{
    public bool IsAdmin;
    public bool Throw;
    public bool ThrowFatal;
    public int Calls;
    public object LastIdentity;

    public Delegate CreateResolver(Type identityType)
    {
        return (Delegate)GetType().GetMethod(
            "CreateTypedResolver", BindingFlags.Instance | BindingFlags.NonPublic)
            .MakeGenericMethod(identityType).Invoke(this, null);
    }

    private Delegate CreateTypedResolver<T>()
    {
        return new Func<T, bool>(Resolve);
    }

    private bool Resolve<T>(T identity)
    {
        Calls++;
        LastIdentity = identity;
        if (ThrowFatal) throw new OutOfMemoryException("Synthetic fatal resolver failure.");
        if (Throw) throw new InvalidOperationException("Synthetic resolver failure.");
        return IsAdmin;
    }
}
"@

$adminProbe = New-Object SemanticAdminPolicyProbe
$adminResolver = $adminProbe.CreateResolver($identityType)
$adminValidator = $adminValidatorConstructor.Invoke(
    [object[]]@($profileCodec, $enforceEvaluator, $adminResolver))
$nonAdminResult = $evaluateIncoming.Invoke($adminValidator, $incomingArguments)
Assert-True ($nonAdminResult.Rejected -and $adminProbe.Calls -eq 1 -and
    [object]::ReferenceEquals($adminProbe.LastIdentity, $identity)) `
    "The false resolver did not enforce the exact incoming server identity."

$adminProbe.IsAdmin = $true
$adminResult = $evaluateIncoming.Invoke($adminValidator, $incomingArguments)
Assert-True (-not $adminResult.Rejected -and
    $adminResult.Violations.Count -eq 0 -and
    $adminResult.Observations.Count -eq 1 -and
    $adminProbe.Calls -eq 2 -and
    (Test-Finding $adminResult.Observations "[admin_bypass:forbidden_prefab:ForbiddenSword]")) `
    "A trusted incoming administrator did not receive the bounded forbidden-prefab observation."

$callsBeforeAuthoritative = $adminProbe.Calls
$adminAuthoritativeResult = $evaluateAuthoritative.Invoke(
    $adminValidator,
    [object[]]@($identity, $violatingSnapshot))
Assert-True ($adminAuthoritativeResult.Rejected -and
    $adminProbe.Calls -eq $callsBeforeAuthoritative -and
    (Test-Finding $adminAuthoritativeResult.Violations "forbidden_prefab")) `
    "Stored/offline authoritative validation consulted or inherited the incoming admin exemption."

$adminObserveValidator = $adminValidatorConstructor.Invoke(
    [object[]]@($profileCodec, $observeEvaluator, $adminResolver))
$adminObserveResult = $evaluateIncoming.Invoke($adminObserveValidator, $incomingArguments)
Assert-True (-not $adminObserveResult.Rejected -and
    (Test-Finding $adminObserveResult.Observations "would_reject:forbidden_prefab") -and
    -not (Test-Finding $adminObserveResult.Observations "admin_bypass:")) `
    "The admin resolver changed baseline Observe disposition."

# Inventory quantity changes are not anomaly observations on incoming/stored
# validation; trusted admins retain exactly the same skill checks as players.
$largeItemArguments = [object[]]$itemArguments.Clone()
$largeItemArguments[0] = "Wood"
$largeItemArguments[1] = [int]::MaxValue
$largeItem = $itemConstructor.Invoke($largeItemArguments)
$largeInventory = New-Snapshot -Items @($largeItem, $largeItem)
$largeInventoryArguments = [object[]]$incomingArguments.Clone()
$largeInventoryArguments[5] = $largeInventory
foreach ($inventoryValidator in @($storedValidator, $incomingValidator, $adminValidator)) {
    $largeInventoryResult = $evaluateIncoming.Invoke($inventoryValidator, $largeInventoryArguments)
    Assert-True (-not $largeInventoryResult.Rejected -and
        $largeInventoryResult.Observations.Count -eq 0 -and
        $largeInventoryResult.Violations.Count -eq 0) `
        "The validator reported a large legal inventory gain as an anomaly."
}
$largeStoredResult = $evaluateAuthoritative.Invoke(
    $storedValidator, [object[]]@($identity, $largeInventory))
Assert-True (-not $largeStoredResult.Rejected -and $largeStoredResult.Observations.Count -eq 0) `
    "Stored authoritative validation reported a large inventory as an anomaly."

$skillBaseline = New-Snapshot -Skills @(
    ($skillConstructor.Invoke([object[]]@([int]1, [single]10, [single]0))))
$skillAnomaly = New-Snapshot -Items @($largeItem) -Skills @(
    ($skillConstructor.Invoke([object[]]@([int]1, [single]23, [single]1000))))
$skillIncomingArguments = [object[]]$incomingArguments.Clone()
$skillIncomingArguments[4] = $skillBaseline
$skillIncomingArguments[5] = $skillAnomaly
$skillIncomingArguments[6] = $skillBaseline
foreach ($skillValidator in @($storedValidator, $incomingValidator, $adminValidator)) {
    $skillIncomingResult = $evaluateIncoming.Invoke($skillValidator, $skillIncomingArguments)
    Assert-True (-not $skillIncomingResult.Rejected -and
        $skillIncomingResult.Observations.Count -eq 2 -and
        (Test-Finding $skillIncomingResult.Observations "skill_gain:1") -and
        (Test-Finding $skillIncomingResult.Observations "skill_accumulator:1")) `
        "The incoming validator lost always-on skill gain/accumulator observations or added item gains."
}
$adminCallsBeforeStoredSkill = $adminProbe.Calls
foreach ($absoluteValidator in @($storedValidator, $incomingValidator, $adminValidator)) {
    $storedSkillResult = $evaluateAuthoritative.Invoke(
        $absoluteValidator, [object[]]@($identity, $skillAnomaly))
    Assert-True (-not $storedSkillResult.Rejected -and
        $storedSkillResult.Observations.Count -eq 1 -and
        (Test-Finding $storedSkillResult.Observations "skill_accumulator:1") -and
        -not (Test-Finding $storedSkillResult.Observations "skill_gain:")) `
        "Stored/offline validation lost its accumulator observation or invented a skill transition."
}
Assert-True ($adminProbe.Calls -eq $adminCallsBeforeStoredSkill) `
    "Stored/offline skill checks consulted the incoming admin exemption."

$structuralArguments = [object[]]$incomingArguments.Clone()
$structuralArguments[5] = New-Snapshot -Items @($forbiddenItem) -HasPlayerData $false
$structuralResult = $evaluateIncoming.Invoke($adminValidator, $structuralArguments)
Assert-True ($structuralResult.Rejected -and
    (Test-Finding $structuralResult.Violations "missing_player_data") -and
    (Test-Finding $structuralResult.Observations "admin_bypass:forbidden_prefab")) `
    "The incoming admin exemption suppressed a mixed structural violation."

$adminProbe.IsAdmin = $false
$revokedResult = $evaluateIncoming.Invoke($adminValidator, $incomingArguments)
Assert-True ($revokedResult.Rejected -and
    (Test-Finding $revokedResult.Violations "forbidden_prefab") -and
    -not (Test-Finding $revokedResult.Observations "admin_bypass:")) `
    "Revoking administrator status did not enforce the very next incoming revision."

$adminProbe.IsAdmin = $true
$adminProbe.Throw = $true
$failedResolverResult = $evaluateIncoming.Invoke($adminValidator, $incomingArguments)
Assert-True ($failedResolverResult.Rejected -and
    (Test-Finding $failedResolverResult.Violations "forbidden_prefab") -and
    -not (Test-Finding $failedResolverResult.Observations "admin_bypass:")) `
    "A failed admin resolver did not deny the forbidden-prefab exemption."

$adminProbe.ThrowFatal = $true
$fatalPropagated = $false
try {
    $evaluateIncoming.Invoke($adminValidator, $incomingArguments) | Out-Null
}
catch {
    $exception = $_.Exception
    while ($null -ne $exception.InnerException) {
        $exception = $exception.InnerException
    }
    $fatalPropagated = $exception -is [OutOfMemoryException]
}
Assert-True $fatalPropagated `
    "The admin resolver swallowed a fatal process exception."

$adminProbe.ThrowFatal = $false
$adminProbe.Throw = $false
$withUsedCheats = $snapshotType.GetMethod('WithUsedCheats', $instanceAll)
$plainSnapshot = New-Snapshot
$flaggedSnapshot = $withUsedCheats.Invoke($plainSnapshot, [object[]]@($true))
Assert-True ($flaggedSnapshot.UsedCheats -and -not $plainSnapshot.UsedCheats -and
    [object]::ReferenceEquals($flaggedSnapshot.Items, $plainSnapshot.Items)) `
    'The immutable cheat flag copy mutated its source or copied the inventory unnecessarily.'
$cheatArguments = [object[]]$incomingArguments.Clone()
$cheatArguments[5] = $flaggedSnapshot
$adminProbe.IsAdmin = $false
$cheatResult = $evaluateIncoming.Invoke($adminValidator, $cheatArguments)
Assert-True (-not $cheatResult.Rejected -and
    (Test-Finding $cheatResult.Observations '[used_cheats]')) `
    'Valheim achievement metadata rejected a non-admin profile or was not audited.'
$adminProbe.IsAdmin = $true
$cheatResult = $evaluateIncoming.Invoke($adminValidator, $cheatArguments)
Assert-True (-not $cheatResult.Rejected -and
    (Test-Finding $cheatResult.Observations '[used_cheats]')) `
    'Valheim achievement metadata changed behavior for an administrator.'
$auditProperty = $cheatResult.GetType().GetProperty('AuditObservations', $instanceAll)
$auditEntries = $auditProperty.GetValue($cheatResult)
$auditKind = $auditEntries[0].GetType().GetProperty('Kind', $instanceAll).GetValue($auditEntries[0])
$auditCode = $auditEntries[0].GetType().GetProperty('ReasonCode', $instanceAll).GetValue($auditEntries[0])
$auditDetail = $auditEntries[0].GetType().GetProperty('Detail', $instanceAll).GetValue($auditEntries[0])
Assert-True ($auditEntries.Count -eq $cheatResult.Observations.Count -and
    $auditKind.ToString() -eq 'RevisionObserved' -and $auditCode -eq 'used_cheats' -and
    $auditDetail -ceq $cheatResult.Observations[0]) `
    'The semantic producer did not generate immutable used-cheats metadata beside its unchanged display text.'
$adminProbe.IsAdmin = $false
$nonAdminCheat = $evaluateIncoming.Invoke($adminValidator, $cheatArguments)
Assert-True (-not $nonAdminCheat.Rejected -and
    (Test-Finding $nonAdminCheat.Observations '[used_cheats]')) `
    'Used-cheats achievement metadata became dependent on administrator state.'
$adminProbe.IsAdmin = $true
foreach ($model in @('CharacterSessionOpenResult', 'CharacterSaveResult')) {
    $type = $plugin.GetType('ServerManager.' + $model, $true)
    $property = $type.GetProperty('AuditFindings', $instanceAll)
    Assert-True ($null -ne $property -and $null -eq $property.GetSetMethod($true)) `
        'Audit metadata can be replaced after a result is exposed.'
}
$callsBeforeStored = $adminProbe.Calls
$cheatStored = $evaluateAuthoritative.Invoke($storedValidator, [object[]]@($identity, $flaggedSnapshot))
$cheatImport = $evaluateAuthoritative.Invoke($adminValidator, [object[]]@($identity, $flaggedSnapshot))
Assert-True (-not $cheatStored.Rejected -and
    (Test-Finding $cheatStored.Observations '[used_cheats]') -and
    -not $cheatImport.Rejected -and
    (Test-Finding $cheatImport.Observations '[used_cheats]') -and
    $adminProbe.Calls -eq $callsBeforeStored) `
    'Used-cheats metadata was not observed consistently across stored and import validation.'
$cheatArguments[5] = $withUsedCheats.Invoke((New-Snapshot -HasPlayerData $false), [object[]]@($true))
$cheatStructural = $evaluateIncoming.Invoke($adminValidator, $cheatArguments)
Assert-True ($cheatStructural.Rejected -and
    (Test-Finding $cheatStructural.Violations 'missing_player_data')) `
    'Achievement metadata bypassed structural validation.'
$cheatArguments[5] = $flaggedSnapshot
$adminProbe.IsAdmin = $false
Assert-True (-not $evaluateIncoming.Invoke($adminValidator, $cheatArguments).Rejected) `
    'Revoked administrator state made achievement metadata reject a revision.'
$adminProbe.IsAdmin = $true
$adminProbe.Throw = $true
Assert-True (-not $evaluateIncoming.Invoke($adminValidator, $cheatArguments).Rejected) `
    'An administrator lookup failure made achievement metadata reject a revision.'

# Execute the actual compiled resolver, retaining its host/remote branches,
# canonical parser, session identity comparison and exception filter. Replace only
# Unity/Steam environment calls; never start a game,
# Steam session, network socket or runtime static initializer. Existing service and
# authentication tests own the unchanged successful remote path.
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll', 'UnityEngine.dll', 'assembly_utils.dll',
    'SoftReferenceableAssets.dll', 'com.rlabrecque.steamworks.net.dll', 'Splatform.dll', 'assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (Split-Path -Parent $pluginPath) $name))) | Out-Null
}
$probeReferences = @((Join-Path $managedRoot 'com.rlabrecque.steamworks.net.dll'))
if ($PSVersionTable.PSEdition -eq 'Core') {
    $probeReferences += @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
}
Add-Type -ReferencedAssemblies $probeReferences -TypeDefinition @'
using System;
using Steamworks;
public static class CharacterPolicyEnvironment {
    public static object Instance;
    public static bool Server, Dedicated, Admin, ThrowSteam;
    public static int Backend, AdminReads;
    public static ulong SteamId;
    public static string AdminHostId;
    public static object GetInstance() { return Instance; }
    public static bool IsNull(object left, object right) { return ReferenceEquals(left, right); }
    public static bool IsServer(object server) { return Server; }
    public static bool IsDedicated(object server) { return Dedicated; }
    public static CSteamID GetSteamID() {
        if (ThrowSteam) throw new InvalidOperationException("fixture Steam unavailable");
        return new CSteamID(SteamId);
    }
    public static bool IsAdmin(object server, string hostId) {
        AdminReads++; AdminHostId = hostId; return Admin;
    }
}
'@
$runtimeName = 'ServerManager.ServerManagerRuntime'
$allStatic = [Reflection.BindingFlags]'Static,Public,NonPublic'
$allInstance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
function Clear-PolicyFixtureBody($Method) {
    $Method.Body.Instructions.Clear()
    $Method.Body.ExceptionHandlers.Clear()
    $Method.Body.Variables.Clear()
}
function Add-PolicyFixtureInstruction($Method, $Opcode, $Operand = $null) {
    $instruction = if ($null -eq $Operand) { [Mono.Cecil.Cil.Instruction]::Create($Opcode) }
        else { [Mono.Cecil.Cil.Instruction]::Create($Opcode, $Operand) }
    $Method.Body.Instructions.Add($instruction)
}
function New-CharacterPolicyFixture([switch]$IgnoreStartupFailure) {
    $definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
    $stream = [IO.MemoryStream]::new()
    try {
        $definition.Name.Name = 'CharacterPolicyFixture_' + [Guid]::NewGuid().ToString('N')
        $module = $definition.MainModule
        $runtime = $module.Types | Where-Object FullName -eq $runtimeName
        $initializer = $runtime.Methods | Where-Object Name -eq '.cctor'
        Clear-PolicyFixtureBody $initializer
        Add-PolicyFixtureInstruction $initializer ([Mono.Cecil.Cil.OpCodes]::Ret)
        foreach ($field in $runtime.Fields) { $field.IsInitOnly = $false }
        $guard = $runtime.Methods | Where-Object Name -eq 'IsCharacterPolicyAdmin'
        $adminLookup = $runtime.Methods | Where-Object Name -eq 'IsCurrentServerAdmin'
        foreach ($method in @($guard, $adminLookup)) {
            foreach ($instruction in @($method.Body.Instructions)) {
                if ($instruction.Operand -is [Mono.Cecil.MethodReference]) {
                    $call = $instruction.Operand
                    $replacement = $null
                    if ($call.DeclaringType.FullName -eq 'ZNet') {
                        $replacement = switch ($call.Name) {
                            'get_instance' { 'GetInstance' }
                            'IsServer' { 'IsServer' }
                            'IsDedicated' { 'IsDedicated' }
                            'IsAdmin' { 'IsAdmin' }
                        }
                    } elseif ($call.DeclaringType.FullName -eq 'Steamworks.SteamUser' -and $call.Name -eq 'GetSteamID') {
                        $replacement = 'GetSteamID'
                    } elseif ($call.DeclaringType.FullName -eq 'UnityEngine.Object' -and $call.Name -eq 'op_Equality') {
                        $replacement = 'IsNull'
                    }
                    if ($replacement) {
                        $instruction.OpCode = [Mono.Cecil.Cil.OpCodes]::Call
                        $instruction.Operand = $module.ImportReference([CharacterPolicyEnvironment].GetMethod($replacement))
                        if ($replacement -eq 'GetInstance') {
                            $method.Body.GetILProcessor().InsertAfter($instruction,
                                [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Castclass,
                                    $module.ImportReference([ZNet])))
                        }
                    }
                } elseif ($instruction.Operand -is [Mono.Cecil.FieldReference] -and
                    $instruction.Operand.Name -eq 'm_onlineBackend') {
                    $instruction.Operand = $module.ImportReference([CharacterPolicyEnvironment].GetField('Backend'))
                }
            }
        }
        if ($IgnoreStartupFailure) {
            $read = @($guard.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq '_localHostStartupFailed'
            })
            Assert-True ($read.Count -eq 1) 'Resolver lost the startup failure guard.'
            $read[0].OpCode = [Mono.Cecil.Cil.OpCodes]::Ldc_I4_0
            $read[0].Operand = $null
        }
        # Keep the unchanged remote chain visible in compiled IL. Successful
        # remote authentication is exercised by the existing integration suite;
        # this fixture executes only its no-auth rejection, without faking peers.
        $remoteCalls = @('TryGetValue', 'get_Phase', 'TryResolveActiveDetectionPeer',
            'get_HostId', 'TryGetServerSession', 'EqualsIdentity', 'IsCurrentServerAdmin')
        $previousOffset = -1
        foreach ($name in $remoteCalls) {
            $call = @($guard.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $name
            })
            Assert-True ($call.Count -eq 1 -and $call[0].Offset -gt $previousOffset) `
                "The compiled remote authentication/session chain changed at $name."
            $previousOffset = $call[0].Offset
        }
        $definition.Write($stream)
        return [Reflection.Assembly]::Load($stream.ToArray())
    } finally { $stream.Dispose(); $definition.Dispose() }
}
function Set-PolicyFixtureField($Target, [string]$Name, $Value) {
    if ($Target -is [Type]) { $Target.GetField($Name, $allStatic).SetValue($null, $Value) }
    else { $Target.GetType().GetField($Name, $allInstance).SetValue($Target, $Value) }
}
function New-PolicyFixtureIdentity($Assembly, [string]$Account, [string]$Name = 'PolicySplitTest') {
    return [Activator]::CreateInstance($Assembly.GetType('ServerManager.CharacterIdentity'), [object[]]@($Account, $Name))
}
$fixture = New-CharacterPolicyFixture
$runtime = $fixture.GetType($runtimeName, $true)
$resolver = $runtime.GetMethod('IsCharacterPolicyAdmin', $allStatic)
$network = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([ZNet])
$otherNetwork = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([ZNet])
[CharacterPolicyEnvironment]::Instance = $network
[CharacterPolicyEnvironment]::SteamId = 76561198000000000
$hostIdentity = New-PolicyFixtureIdentity $fixture 'steamworks:76561198000000000'
$authField = $runtime.GetField('SteamAuthenticationsById', $allStatic)
$auth = [Activator]::CreateInstance($authField.FieldType)
Set-PolicyFixtureField $runtime 'SteamAuthenticationsById' $auth
Set-PolicyFixtureField $runtime 'SteamAuthenticationGate' ([object]::new())
$hostCases = 0
foreach ($server in @($false, $true)) { foreach ($dedicated in @($false, $true)) {
foreach ($backend in [Enum]::GetValues([OnlineBackendType])) { foreach ($requested in @($false, $true)) {
foreach ($failed in @($false, $true)) { foreach ($sameNetwork in @($false, $true)) {
foreach ($admin in @($false, $true)) {
    [CharacterPolicyEnvironment]::Server = $server
    [CharacterPolicyEnvironment]::Dedicated = $dedicated
    [CharacterPolicyEnvironment]::Backend = [int]$backend
    [CharacterPolicyEnvironment]::Admin = $admin
    [CharacterPolicyEnvironment]::AdminReads = 0
    Set-PolicyFixtureField $runtime '_localHostRequested' $requested
    Set-PolicyFixtureField $runtime '_localHostStartupFailed' $failed
    Set-PolicyFixtureField $runtime '_localHostNetwork' $(if ($sameNetwork) { $network } else { $otherNetwork })
    $localBranch = $server -and -not $dedicated -and $backend -eq [OnlineBackendType]::Steamworks
    $operator = $requested -and -not $failed -and $sameNetwork
    $expected = $localBranch -and ($operator -or $admin)
    Assert-True ($resolver.Invoke($null, [object[]]@($hostIdentity)) -eq $expected) `
        "Host resolver mismatch: server=$server dedicated=$dedicated backend=$backend requested=$requested failed=$failed same=$sameNetwork admin=$admin"
    Assert-True ([CharacterPolicyEnvironment]::AdminReads -eq [int]($localBranch -and -not $operator)) `
        'Host resolver skipped its existing adminlist fallback or read it despite the operator exemption.'
    if ([CharacterPolicyEnvironment]::AdminReads -ne 0) {
        Assert-True ([CharacterPolicyEnvironment]::AdminHostId -ceq '76561198000000000') `
            'The adminlist fallback checked a different Steam account.'
    }
    $hostCases++
}}}}}}}
# No LocalHostCharacterRuntime._active or Player is constructed: the passing
# operator cases above cover Prepare/OpenBackupLocalHostSession before activation.
[CharacterPolicyEnvironment]::Server = $true
[CharacterPolicyEnvironment]::Dedicated = $false
[CharacterPolicyEnvironment]::Backend = [int][OnlineBackendType]::Steamworks
[CharacterPolicyEnvironment]::Admin = $false
Set-PolicyFixtureField $runtime '_localHostRequested' $true
Set-PolicyFixtureField $runtime '_localHostStartupFailed' $false
Set-PolicyFixtureField $runtime '_localHostNetwork' $network
Assert-True (-not $resolver.Invoke($null, [object[]]@($null))) 'Null identity received an exemption.'
$invalidIdentity = New-PolicyFixtureIdentity $fixture 'steamworks:0'
Assert-True (-not $resolver.Invoke($null, [object[]]@($invalidIdentity))) 'Invalid Steam ID received an exemption.'
[CharacterPolicyEnvironment]::Instance = $null
Assert-True (-not $resolver.Invoke($null, [object[]]@($hostIdentity))) 'Missing server received an exemption.'
[CharacterPolicyEnvironment]::Instance = $network
[CharacterPolicyEnvironment]::ThrowSteam = $true
Assert-True (-not $resolver.Invoke($null, [object[]]@($hostIdentity))) 'Steam lookup failure granted an exemption.'
[CharacterPolicyEnvironment]::ThrowSteam = $false

$remoteId = [uint64]76561198000000001
$remoteIdentity = New-PolicyFixtureIdentity $fixture ('steamworks:' + $remoteId)
Assert-True (-not $resolver.Invoke($null, [object[]]@($remoteIdentity))) 'Another account inherited local operator status without authentication.'
[CharacterPolicyEnvironment]::Dedicated = $true
Assert-True (-not $resolver.Invoke($null, [object[]]@($hostIdentity))) `
    'A dedicated server inherited the same-process host exemption without a remote session.'

# Negative control: removing a real production guard must change the outcome.
$broken = New-CharacterPolicyFixture -IgnoreStartupFailure
$brokenRuntime = $broken.GetType($runtimeName)
Set-PolicyFixtureField $brokenRuntime '_localHostRequested' $true
Set-PolicyFixtureField $brokenRuntime '_localHostStartupFailed' $true
Set-PolicyFixtureField $brokenRuntime '_localHostNetwork' $network
[CharacterPolicyEnvironment]::Dedicated = $false
[CharacterPolicyEnvironment]::Admin = $false
Set-PolicyFixtureField $runtime '_localHostStartupFailed' $true
Assert-True (-not $resolver.Invoke($null, [object[]]@($hostIdentity))) 'Failed startup received the operator exemption.'
$brokenIdentity = New-PolicyFixtureIdentity $broken 'steamworks:76561198000000000'
Assert-True ($brokenRuntime.GetMethod('IsCharacterPolicyAdmin', $allStatic).Invoke($null, [object[]]@($brokenIdentity))) `
    'Negative control did not expose removal of the failed-startup guard.'
Write-Host "Character semantic policy split passed; actual runtime resolver: $hostCases host cases, unauthenticated remote rejection, identity/failure checks and guard mutation."
