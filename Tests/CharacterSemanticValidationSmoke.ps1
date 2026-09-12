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

function New-GenericList {
    param(
        [Type]$ElementType,
        [object[]]$Values
    )

    $listType = [System.Collections.Generic.List``1].MakeGenericType(
        $ElementType)
    $list = [Activator]::CreateInstance($listType)
    $add = $listType.GetMethod("Add")
    foreach ($value in $Values) {
        $add.Invoke($list, [object[]]@($value)) | Out-Null
    }

    return ,$list
}

function Assert-Finding {
    param(
        [object]$Findings,
        [string]$Code,
        [string]$Message
    )

    $matched = $false
    foreach ($finding in $Findings) {
        if ([string]$finding -like "*$Code*") {
            $matched = $true
            break
        }
    }

    Assert-True $matched $Message
}

function Get-SnapshotSkillLevel {
    param(
        [object]$Snapshot,
        [int]$SkillType
    )

    $skills = $Snapshot.Skills
    $itemProperty = $skills.GetType().GetProperty("Item")
    if ($null -ne $itemProperty) {
        try {
            $skill = $itemProperty.GetValue(
                $skills,
                [object[]]@([int]$SkillType))
            return [single]$skill.Level
        }
        catch {
            # Fall through to a stable test failure below.
        }
    }

    throw "Skill $SkillType is missing from the semantic snapshot."
}

function New-CustomDataDictionaryBytes {
    param(
        [bool]$ReverseCustomDataOrder = $false,
        [string]$AValue = "secret-a",
        [bool]$DuplicateKeys = $false
    )

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([int]2)
        if ($DuplicateKeys) {
            $writer.Write("a.key")
            $writer.Write($AValue)
            $writer.Write("a.key")
            $writer.Write("secret-z")
        }
        elseif ($ReverseCustomDataOrder) {
            $writer.Write("a.key")
            $writer.Write($AValue)
            $writer.Write("z.key")
            $writer.Write("secret-z")
        }
        else {
            $writer.Write("z.key")
            $writer.Write("secret-z")
            $writer.Write("a.key")
            $writer.Write($AValue)
        }
        $writer.Flush()
        return $stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
Assert-True (Test-Path -LiteralPath $pluginPath) `
    "Build ServerManager before running this smoke test."

$plugin = [Reflection.Assembly]::LoadFrom($pluginPath)
$steamworksPath = Join-Path $GamePath `
    "valheim_Data\Managed\com.rlabrecque.steamworks.net.dll"
Assert-True (Test-Path -LiteralPath $steamworksPath) `
    "The installed Steamworks assembly is required to validate readable storage keys."
[Reflection.Assembly]::LoadFrom($steamworksPath) | Out-Null

Add-Type -TypeDefinition @"
using System;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;

public sealed class SemanticRevisionValidatorProxy : RealProxy
{
    private readonly object _result;

    public SemanticRevisionValidatorProxy(Type interfaceType, object result)
        : base(interfaceType)
    {
        _result = result;
    }

    public int EvaluateCalls { get; private set; }

    public override IMessage Invoke(IMessage message)
    {
        IMethodCallMessage call = (IMethodCallMessage)message;
        if (call.MethodName == "Evaluate")
        {
            EvaluateCalls++;
        }

        return new ReturnMessage(
            _result,
            null,
            0,
            call.LogicalCallContext,
            call);
    }

    public object CreateRepository(
        ConstructorInfo constructor,
        object layout,
        object options,
        object profileCodec)
    {
        return constructor.Invoke(
            new[]
            {
                layout,
                options,
                profileCodec,
                GetTransparentProxy(),
                null
            });
    }
}

public static class SemanticRepositoryFixture
{
    public static object CreateSession(
        Type sessionType,
        object identity,
        string storageKey,
        Guid sessionId,
        long revision,
        long playerId,
        byte[] payloadSha256,
        object skillObservationWindow)
    {
        object session =
            FormatterServices.GetUninitializedObject(sessionType);
        SetField(sessionType, session, "_revisionLock", new object());
        SetField(sessionType, session, "_saveLock", new object());
        SetField(sessionType, session, "_currentRevision", revision);
        SetField(
            sessionType,
            session,
            "_currentPayloadSha256",
            (byte[])payloadSha256.Clone());
        SetField(
            sessionType,
            session,
            "_skillObservationWindow",
            skillObservationWindow);
        SetField(
            sessionType,
            session,
            "<Identity>k__BackingField",
            identity);
        SetField(
            sessionType,
            session,
            "<StorageKey>k__BackingField",
            storageKey);
        SetField(
            sessionType,
            session,
            "<SessionId>k__BackingField",
            sessionId);
        SetField(
            sessionType,
            session,
            "<PlayerId>k__BackingField",
            playerId);
        return session;
    }

    public static object GetField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(
            name,
            BindingFlags.Instance |
            BindingFlags.NonPublic);
        if (field == null)
        {
            throw new MissingFieldException(instance.GetType().FullName, name);
        }

        return field.GetValue(instance);
    }

    private static void SetField(
        Type type,
        object instance,
        string name,
        object value)
    {
        FieldInfo field = type.GetField(
            name,
            BindingFlags.Instance |
            BindingFlags.NonPublic);
        if (field == null)
        {
            throw new MissingFieldException(type.FullName, name);
        }

        field.SetValue(instance, value);
    }
}
"@

$policyModeType = $plugin.GetType(
    "ServerManager.CharacterSemanticPolicyMode",
    $true)
$policyType = $plugin.GetType(
    "ServerManager.CharacterSemanticPolicy",
    $true)
$skillType = $plugin.GetType(
    "ServerManager.CharacterSemanticSkillState",
    $true)
$itemType = $plugin.GetType(
    "ServerManager.CharacterSemanticItemState",
    $true)
$snapshotType = $plugin.GetType(
    "ServerManager.CharacterSemanticSnapshot",
    $true)
$evaluatorType = $plugin.GetType(
    "ServerManager.CharacterSemanticEvaluator",
    $true)
$identityType = $plugin.GetType(
    "ServerManager.CharacterIdentity",
    $true)
$skillWindowType = $plugin.GetType(
    "ServerManager.CharacterSkillObservationWindow",
    $true)
$snapshotServiceType = $plugin.GetType(
    "ServerManager.CharacterSnapshotService",
    $true)
$profileCodecType = $plugin.GetType(
    "ServerManager.ValheimPlayerProfileCodec",
    $true)
$envelopeType = $plugin.GetType(
    "ServerManager.CharacterEnvelope",
    $true)
$envelopeKindType = $plugin.GetType(
    "ServerManager.CharacterEnvelopeKind",
    $true)
$storageOptionsType = $plugin.GetType(
    "ServerManager.CharacterStorageOptions",
    $true)
$storageLayoutType = $plugin.GetType(
    "ServerManager.CharacterStorageLayout",
    $true)
$envelopeCodecType = $plugin.GetType(
    "ServerManager.CharacterEnvelopeCodec",
    $true)
$repositoryType = $plugin.GetType(
    "ServerManager.CharacterRepository",
    $true)
$sessionType = $plugin.GetType(
    "ServerManager.CharacterSession",
    $true)
$revisionValidatorType = $plugin.GetType(
    "ServerManager.ICharacterRevisionValidator",
    $true)

$policyConstructor = $policyType.GetConstructors() |
    Where-Object { $_.GetParameters().Count -eq 7 } |
    Select-Object -First 1
$skillConstructor = $skillType.GetConstructors() |
    Select-Object -First 1
$itemConstructor = $itemType.GetConstructors() |
    Select-Object -First 1
$snapshotConstructor = $snapshotType.GetConstructors() |
    Select-Object -First 1
$evaluatorConstructor = $evaluatorType.GetConstructors() |
    Select-Object -First 1
$evaluate = $evaluatorType.GetMethod("Evaluate")
$instanceNonPublic = [Reflection.BindingFlags]::Instance -bor
    [Reflection.BindingFlags]::NonPublic
$staticNonPublic = [Reflection.BindingFlags]::Static -bor
    [Reflection.BindingFlags]::NonPublic
$evaluateRevision = $evaluatorType.GetMethod(
    "EvaluateRevision",
    $instanceNonPublic)
$evaluateAbsolute = $evaluatorType.GetMethod(
    "EvaluateAbsolute",
    $instanceNonPublic)
$innerReaderType = $profileCodecType.GetNestedType(
    "InnerPlayerDataReader",
    [Reflection.BindingFlags]::NonPublic)
$innerReaderConstructor = $innerReaderType.GetConstructors(
    $instanceNonPublic) |
    Select-Object -First 1
$readSemanticDictionary = $innerReaderType.GetMethod(
    "ReadSemanticStringDictionary",
    $instanceNonPublic)
$detailedItemConstructor = $itemType.GetConstructors(
    $instanceNonPublic) |
    Where-Object { $_.GetParameters().Count -eq 7 } |
    Select-Object -First 1

Assert-True ($null -ne $policyConstructor) `
    "The semantic policy constructor is missing."
Assert-True ($policyType.GetConstructors().Count -eq 1) `
    "The semantic policy retained a legacy constructor with removed observation controls."
Assert-True ($null -ne $evaluate) `
    "The pure semantic evaluator is missing."
Assert-True ($evaluate.GetParameters().Count -eq 4 -and
    $evaluateRevision.GetParameters().Count -eq 5 -and
    $evaluateRevision.GetParameters()[1].Name -eq "skillObservationBaseline" -and
    $evaluateRevision.GetParameters()[4].Name -eq "bypassAdminPolicy" -and
    $evaluateRevision.GetParameters()[4].ParameterType -eq [bool] -and
    $evaluateRevision.GetParameters()[4].IsOptional -and
    $evaluateRevision.GetParameters()[4].DefaultValue -eq $false -and
    $evaluateAbsolute.GetParameters().Count -eq 2 -and
    $null -eq $snapshotType.GetProperty('ItemDefinitionCatalogValidated', [Reflection.BindingFlags]'Instance,Public,NonPublic')) `
    "The evaluator restored item-catalog coupling or lost the bounded, opt-in incoming prefab exemption."
foreach ($removedObservationMember in @(
        "ObserveRevisionTransitions", "ObservedPerPrefabItemGain",
        "ObservedTotalItemGain")) {
    Assert-True (@($policyType.GetMembers(
            [Reflection.BindingFlags]"Public,NonPublic,Instance,Static") |
        Where-Object Name -eq $removedObservationMember).Count -eq 0) `
        "Removed observation policy member $removedObservationMember remains in the implementation."
}
Assert-True ($null -eq $evaluatorType.GetMethod("ObserveItemGains", $instanceNonPublic)) `
    "The removed item-gain evaluator method remains in the implementation."
foreach ($removedWorldLevelMember in @(
        "MaximumItemWorldLevel", "ItemWorldLevelLimitCount",
        "TryResolveMaximumWorldLevel", "ParseWorldLevelLimits",
        "ForbiddenCustomDataKeyCount", "ForbiddenCustomDataPrefixCount",
        "IsForbiddenCustomDataKey")) {
    Assert-True (@($policyType.GetMembers(
            [Reflection.BindingFlags]"Public,NonPublic,Instance,Static") |
        Where-Object Name -eq $removedWorldLevelMember).Count -eq 0) `
        "Removed item metadata policy member $removedWorldLevelMember remains in the implementation."
}
Assert-True (
    $null -ne $evaluateRevision -and
    $null -ne $evaluateAbsolute) `
    "The revision or absolute semantic evaluator is missing."
Assert-True (
    $null -ne $innerReaderConstructor -and
    $null -ne $readSemanticDictionary) `
    "The bounded semantic custom-data reader is missing."
Assert-True ($itemType.GetConstructors().Count -eq 1) `
    "The existing public semantic item constructor changed shape."
Assert-True ($null -ne $detailedItemConstructor) `
    "The detailed semantic item constructor is missing."

$readerConstructorArguments = [object[]]::new(2)
$readerConstructorArguments[0] =
    [byte[]](New-CustomDataDictionaryBytes)
$readerConstructorArguments[1] = [int]8192
$dictionaryReader = $innerReaderConstructor.Invoke(
    $readerConstructorArguments)
$readDictionaryArguments = [object[]]::new(3)
$readDictionaryArguments[0] = "item custom data"
$readDictionaryArguments[1] = $null
$readDictionaryArguments[2] = $false
$semanticDictionary = $readSemanticDictionary.Invoke(
    $dictionaryReader,
    $readDictionaryArguments)

$readerConstructorArguments[0] =
    [byte[]](New-CustomDataDictionaryBytes `
        -ReverseCustomDataOrder $true)
$reorderedDictionary = $readSemanticDictionary.Invoke(
    $innerReaderConstructor.Invoke($readerConstructorArguments),
    $readDictionaryArguments)
$readerConstructorArguments[0] =
    [byte[]](New-CustomDataDictionaryBytes `
        -AValue "changed-secret-a")
$changedValueDictionary = $readSemanticDictionary.Invoke(
    $innerReaderConstructor.Invoke($readerConstructorArguments),
    $readDictionaryArguments)
$largeCustomDataValue = "x" * (60 * 1024)
$readerConstructorArguments[0] =
    [byte[]](New-CustomDataDictionaryBytes `
        -AValue $largeCustomDataValue)
$largeValueDictionary = $readSemanticDictionary.Invoke(
    $innerReaderConstructor.Invoke($readerConstructorArguments),
    $readDictionaryArguments)
$duplicateCustomDataRejected = $false
try {
    $readerConstructorArguments[0] =
        [byte[]](New-CustomDataDictionaryBytes -DuplicateKeys $true)
    $readSemanticDictionary.Invoke(
        $innerReaderConstructor.Invoke($readerConstructorArguments),
        $readDictionaryArguments) | Out-Null
}
catch {
    if ($_.Exception.ToString().IndexOf(
            "duplicate key",
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $duplicateCustomDataRejected = $true
    }
    else {
        throw
    }
}
Assert-True $duplicateCustomDataRejected `
    "The semantic custom-data reader accepted duplicate keys."
Assert-True (
    $semanticDictionary.Count -eq 2 -and
    $semanticDictionary[0].Key -eq "a.key" -and
    $semanticDictionary[0].Value -eq "secret-a" -and
    $semanticDictionary[1].Key -eq "z.key" -and
    $semanticDictionary[1].Value -eq "secret-z" -and
    $reorderedDictionary[0].Key -eq "a.key" -and
    $reorderedDictionary[1].Key -eq "z.key" -and
    $changedValueDictionary[0].Value -eq "changed-secret-a") `
    "Item custom-data entries were not retained in deterministic key order."

$detailedItemArguments = [object[]]::new(7)
$detailedItemArguments[0] = "DetailedSword"
$detailedItemArguments[1] = [int]2
$detailedItemArguments[2] = [int]2
$detailedItemArguments[3] = [int]2
$detailedItemArguments[4] = [int]3
$detailedItemArguments[5] = [int]4
$detailedItemArguments[6] = $semanticDictionary
$detailedItem = $detailedItemConstructor.Invoke($detailedItemArguments)
Assert-True (
    $detailedItem.PrefabName -eq "DetailedSword" -and
    $detailedItem.Stack -eq 2 -and
    $detailedItem.Quality -eq 2 -and
    $detailedItem.WorldLevel -eq 2 -and
    $detailedItem.PositionX -eq 3 -and
    $detailedItem.PositionY -eq 4) `
    "The bounded parser discarded retained semantic inventory item details."
Assert-True (
    $detailedItem.CustomData.Count -eq 2 -and
    $detailedItem.CustomData[0].Key -eq "a.key" -and
    $detailedItem.CustomData[0].Value -eq "secret-a" -and
    $detailedItem.CustomData[1].Key -eq "z.key" -and
    $detailedItem.CustomData[1].Value -eq "secret-z") `
    "Item custom-data values were not retained in ordinal key order."
Assert-True (
    $largeValueDictionary.Count -eq 2 -and
    $largeValueDictionary[0].Value.Length -eq $largeCustomDataValue.Length -and
    $largeValueDictionary[0].Value -ceq $largeCustomDataValue) `
    "An accepted large custom-data value was truncated or changed."
Assert-True (
    $null -eq $itemType.GetProperty("Durability") -and
    $null -eq $itemType.GetProperty("Equipped") -and
    $null -eq $itemType.GetProperty("Variant") -and
    $null -eq $itemType.GetProperty("CrafterId") -and
    $null -eq $itemType.GetProperty("PickedUp") -and
    $null -eq $itemType.GetProperty("CustomDataSha256") -and
    $null -eq $itemType.GetProperty("CustomDataKeys") -and
    $null -eq $itemType.GetProperty("CrafterName") -and
    $null -eq $itemType.GetProperty("CustomDataValues") -and
    $null -ne $itemType.GetProperty("CustomData")) `
    "Unused or duplicate semantic item state was retained."
$profileCodecSource = Get-Content -LiteralPath (
    Join-Path $projectRoot "Character\ValheimPlayerProfileCodec.cs") -Raw
foreach ($requiredParserWire in @(
    "positionX,",
    "positionY,",
    "customData));")) {
    Assert-True (
        $profileCodecSource.IndexOf(
            $requiredParserWire,
            [StringComparison]::Ordinal) -ge 0) `
        "The inventory parser no longer forwards '$requiredParserWire'."
}
foreach ($removedParserState in @(
    "ComputeCanonicalDictionarySha256",
    "CustomDataSha256",
    "customData.Keys")) {
    Assert-True (
        $profileCodecSource.IndexOf(
            $removedParserState,
            [StringComparison]::Ordinal) -lt 0) `
        "The inventory parser retained '$removedParserState'."
}
Assert-True (
    $null -eq $profileCodecType.GetNestedType(
        "SemanticStringDictionary",
        [Reflection.BindingFlags]::NonPublic)) `
    "The removed semantic dictionary carrier type is still present."

function New-Policy {
    param(
        [string]$Mode = "Enforce",
        [string]$ForbiddenPrefabs = "",
        [single]$MaximumHealth = 1000,
        [single]$MaximumStamina = 1000,
        [single]$MaximumEitr = 1000,
        [single]$SkillBurst = 2,
        [single]$SkillRate = 10
    )

    $modeValue = [Enum]::Parse($policyModeType, $Mode)
    return $policyConstructor.Invoke(
        [object[]]@(
            $modeValue,
            $ForbiddenPrefabs,
            $MaximumHealth,
            $MaximumStamina,
            $MaximumEitr,
            $SkillBurst,
            $SkillRate))
}

function New-Skill {
    param(
        [int]$Type,
        [single]$Level,
        [single]$Accumulator = 0
    )

    return $skillConstructor.Invoke(
        [object[]]@($Type, $Level, $Accumulator))
}

function New-Item {
    param(
        [string]$Prefab,
        [int]$Stack,
        [int]$Quality = 1,
        [int]$WorldLevel = 0,
        [string[]]$CustomKeys = @()
    )

    return $itemConstructor.Invoke(
        [object[]]@(
            $Prefab,
            $Stack,
            $Quality,
            $WorldLevel,
            [string[]]$CustomKeys))
}

function New-Snapshot {
    param(
        [bool]$HasPlayerData = $true,
        [single]$MaximumHealth = 100,
        [single]$MaximumStamina = 100,
        [single]$MaximumEitr = 100,
        [object[]]$Skills = @(),
        [object[]]$Items = @(),
        [string[]]$PlayerCustomKeys = @()
    )

    $skillList = New-GenericList $skillType $Skills
    $itemList = New-GenericList $itemType $Items
    return $snapshotConstructor.Invoke(
        [object[]]@(
            $HasPlayerData,
            $MaximumHealth,
            $MaximumStamina,
            $MaximumEitr,
            $skillList,
            $itemList,
            [string[]]$PlayerCustomKeys))
}

function Invoke-Evaluation {
    param(
        [object]$Policy,
        [object]$Previous,
        [object]$Candidate,
        [double]$ElapsedSeconds = 60
    )

    $evaluator = $evaluatorConstructor.Invoke([object[]]@($Policy))
    return $evaluate.Invoke(
        $evaluator,
        [object[]]@(
            $script:identity,
            $Previous,
            $Candidate,
            [TimeSpan]::FromSeconds($ElapsedSeconds)))
}

function Invoke-RevisionEvaluation {
    param(
        [object]$Policy,
        [object]$Previous,
        [object]$Candidate,
        [bool]$BypassForbiddenItemPrefabs = $false
    )

    $evaluator = $evaluatorConstructor.Invoke([object[]]@($Policy))
    return $evaluateRevision.Invoke(
        $evaluator,
        [object[]]@(
            $script:identity,
            $Previous,
            $Candidate,
            [TimeSpan]::FromMinutes(1),
            $BypassForbiddenItemPrefabs))
}

$identity = [Activator]::CreateInstance(
    $identityType,
    [object[]]@("steamworks:76561198000000000", "SemanticTest"))
$baseline = New-Snapshot

$hardPolicy = New-Policy `
    -ForbiddenPrefabs "ForbiddenSword" `
    -MaximumHealth 1000
$hardCandidate = New-Snapshot `
    -MaximumHealth 1001 `
    -MaximumStamina 1002 `
    -MaximumEitr 1003 `
    -Items @(
        (New-Item `
            -Prefab "ForbiddenSword" `
            -Stack 1 `
            -WorldLevel 255 `
            -CustomKeys @("Bad.Key"))) `
    -PlayerCustomKeys @("ServerManager.Forged")
$hardResult = Invoke-Evaluation $hardPolicy $baseline $hardCandidate
Assert-True $hardResult.Rejected `
    "Enforce mode accepted hard character-state violations."
Assert-Finding $hardResult.Violations "forbidden_prefab" `
    "The forbidden prefab was not rejected."
Assert-True ($hardResult.Violations.Count -eq 1 -and
    $hardResult.StatLimitFindings.Count -eq 3) `
    "Numeric limits either rejected the mixed candidate or lost their typed findings."
Assert-True (@($hardResult.Violations | Where-Object {
    [string]$_ -match 'item_custom_data|player_custom_data'
}).Count -eq 0) `
    "The removed custom-data policy still adds findings alongside valid prefab rejections."

$adminHardResult = Invoke-RevisionEvaluation $hardPolicy $baseline $hardCandidate `
    -BypassForbiddenItemPrefabs $true
Assert-True (-not $adminHardResult.Rejected -and
    $adminHardResult.Violations.Count -eq 0 -and
    $adminHardResult.Observations.Count -eq 1 -and
    $adminHardResult.StatLimitFindings.Count -eq 3) `
    "The admin exemption changed more than the forbidden-prefab disposition."
Assert-Finding $adminHardResult.Observations "admin_bypass:forbidden_prefab" `
    "The admitted admin forbidden prefab was not auditable."
$longAuditPrefab = 'F' * 128
$longAuditPolicy = New-Policy -ForbiddenPrefabs $longAuditPrefab
$longAuditSnapshot = New-Snapshot -Items @((New-Item -Prefab $longAuditPrefab -Stack 1)) `
    -Skills @((New-Skill -Type ([int]::MaxValue) -Level 90))
$longAuditResult = Invoke-RevisionEvaluation $longAuditPolicy $baseline $longAuditSnapshot `
    -BypassForbiddenItemPrefabs $true
$metadataProperty = $longAuditResult.GetType().GetProperty('AuditObservations', $instanceNonPublic)
$metadata = $metadataProperty.GetValue($longAuditResult)
Assert-True ($metadata.Count -eq $longAuditResult.Observations.Count -and $metadata.Count -eq 2) `
    'Bounded typed findings diverged from the public observation list.'
for ($index = 0; $index -lt $metadata.Count; ++$index) {
    $entry = $metadata[$index]
    $detail = $entry.GetType().GetProperty('Detail', $instanceNonPublic).GetValue($entry)
    $key = $entry.GetType().GetProperty('DedupeKey', $instanceNonPublic).GetValue($entry)
    Assert-True ($detail -ceq $longAuditResult.Observations[$index] -and
        $key -ceq $detail.Substring(0, $detail.IndexOf(']') + 1) -and $key.Length -lt 256) `
        'Long prefab or maximum skill token changed the existing stable audit key.'
}
$strictLongAudit = Invoke-RevisionEvaluation $longAuditPolicy $baseline $longAuditSnapshot
Assert-True ($strictLongAudit.GetType().GetProperty('RejectionReasonCode', $instanceNonPublic).
    GetValue($strictLongAudit) -eq 'forbidden_prefab') 'Forbidden-item rejection lost its typed reason.'
for ($adminStatIndex = 0; $adminStatIndex -lt 3; ++$adminStatIndex) {
    $adminStat = $adminHardResult.StatLimitFindings[$adminStatIndex]
    $ordinaryStat = $hardResult.StatLimitFindings[$adminStatIndex]
    Assert-True ($adminStat.Code -ceq $ordinaryStat.Code -and
        $adminStat.Value -eq $ordinaryStat.Value -and
        $adminStat.Limit -eq $ordinaryStat.Limit) `
        "The forbidden-prefab exemption modified independent stat-limit evidence."
}
Assert-True ($hardCandidate.Items[0].PrefabName -ceq "ForbiddenSword" -and
    $hardCandidate.Items[0].Stack -eq 1 -and
    $hardCandidate.Items[0].WorldLevel -eq 255 -and
    $hardCandidate.Items[0].CustomData[0].Key -ceq "Bad.Key" -and
    $hardCandidate.PlayerCustomDataKeys[0] -ceq "ServerManager.Forged" -and
    $hardCandidate.MaximumHealth -eq 1001) `
    "The admin exemption rewrote the incoming snapshot."

$adminMissingPlayerResult = Invoke-RevisionEvaluation $hardPolicy $baseline `
    (New-Snapshot -HasPlayerData $false -Items @((New-Item -Prefab "ForbiddenSword" -Stack 1))) `
    -BypassForbiddenItemPrefabs $true
Assert-True $adminMissingPlayerResult.Rejected `
    "An admin forbidden prefab bypassed the mandatory inner Player-data invariant."
Assert-Finding $adminMissingPlayerResult.Violations "missing_player_data" `
    "The mixed invalid admin candidate lost its structural finding."

$adminModItemResult = Invoke-RevisionEvaluation $hardPolicy $baseline $hardCandidate `
    -BypassForbiddenItemPrefabs $true
Assert-True (-not $adminModItemResult.Rejected -and $adminModItemResult.StatLimitFindings.Count -eq 3) `
    "Catalog-independent item policy lost the administrator exemption or independent stat findings."

$boundedPrefabs = @()
$boundedItems = @()
for ($boundedIndex = 0; $boundedIndex -lt 20; ++$boundedIndex) {
    $boundedPrefab = "Forbidden" + $boundedIndex
    $boundedPrefabs += $boundedPrefab
    $boundedItems += New-Item -Prefab $boundedPrefab -Stack 1
    $boundedItems += New-Item -Prefab $boundedPrefab -Stack 1
}
$boundedAdminResult = Invoke-RevisionEvaluation `
    (New-Policy -ForbiddenPrefabs ($boundedPrefabs -join ',')) `
    $baseline (New-Snapshot -Items $boundedItems) -BypassForbiddenItemPrefabs $true
Assert-True (-not $boundedAdminResult.Rejected -and
    $boundedAdminResult.Observations.Count -eq 16 -and
    @($boundedAdminResult.Observations | Select-Object -Unique).Count -eq 16) `
    "Admin bypass diagnostics lost the existing finding bound or per-prefab deduplication."

# Numeric thresholds always produce typed evidence without rejecting, clamping,
# or duplicating it into semantic observation strings, regardless of item policy.
$statCandidate = New-Snapshot -MaximumHealth 1001 -MaximumStamina 1002 -MaximumEitr 1003
foreach ($statMode in @("Disabled", "Observe", "Enforce")) {
    $statResult = Invoke-Evaluation (New-Policy -Mode $statMode) $baseline $statCandidate
    Assert-True (-not $statResult.Rejected -and
        $statResult.Violations.Count -eq 0 -and
        $statResult.Observations.Count -eq 0 -and
        $statResult.StatLimitFindings.Count -eq 3) `
        "Numeric limits were rejected, suppressed, or double-reported in $statMode."
    $expectedStats = @("maximum_health", "maximum_stamina", "maximum_eitr")
    for ($statIndex = 0; $statIndex -lt $expectedStats.Count; ++$statIndex) {
        $stat = $statResult.StatLimitFindings[$statIndex]
        Assert-True ($stat.Code -ceq $expectedStats[$statIndex] -and
            $stat.Value -eq (1001 + $statIndex) -and $stat.Limit -eq 1000 -and
            -not $stat.GetType().GetProperty("Code").CanWrite -and
            -not $stat.GetType().GetProperty("Value").CanWrite -and
            -not $stat.GetType().GetProperty("Limit").CanWrite) `
            "Typed stat evidence changed its code/value/limit or became mutable."
    }
    Assert-True ($statCandidate.MaximumHealth -eq 1001 -and
        $statCandidate.MaximumStamina -eq 1002 -and $statCandidate.MaximumEitr -eq 1003) `
        "The numeric-limit evaluator clamped the accepted candidate."
    $boundaryResult = Invoke-Evaluation (New-Policy -Mode $statMode) $baseline `
        (New-Snapshot -MaximumHealth 1000 -MaximumStamina 1000 -MaximumEitr 1000)
    Assert-True (-not $boundaryResult.Rejected -and $boundaryResult.StatLimitFindings.Count -eq 0) `
        "A value equal to its numeric limit produced a finding in $statMode."
}

$observePolicy = New-Policy `
    -Mode "Observe" `
    -ForbiddenPrefabs "ForbiddenSword"
$observeResult = Invoke-Evaluation `
    $observePolicy `
    $baseline `
    $hardCandidate
Assert-True (-not $observeResult.Rejected) `
    "Observe mode rejected a candidate revision."
Assert-Finding $observeResult.Observations "would_reject:forbidden_prefab" `
    "Observe mode did not retain the hard-rule finding."

foreach ($customDataMode in @("Enforce", "Observe")) {
    $customDataCandidate = New-Snapshot `
        -Items @((New-Item -Prefab "Ordinary" -Stack 1 `
            -CustomKeys @("Bad.Key", "ServerManager.ItemProbe"))) `
        -PlayerCustomKeys @("Bad.Key", "ServerManager.PlayerProbe")
    $customDataResult = Invoke-Evaluation `
        (New-Policy -Mode $customDataMode) $baseline $customDataCandidate
    Assert-True (-not $customDataResult.Rejected -and
        $customDataResult.Violations.Count -eq 0 -and
        $customDataResult.Observations.Count -eq 0) `
        "Formerly forbidden exact/prefixed item or player keys produced a finding in $customDataMode."
    Assert-True ($customDataCandidate.Items[0].CustomData.Count -eq 2 -and
        $customDataCandidate.Items[0].CustomData[0].Key -ceq "Bad.Key" -and
        $customDataCandidate.Items[0].CustomData[1].Key -ceq "ServerManager.ItemProbe" -and
        $customDataCandidate.PlayerCustomDataKeys.Count -eq 2 -and
        $customDataCandidate.PlayerCustomDataKeys[1] -ceq "ServerManager.PlayerProbe") `
        "Semantic evaluation removed or rewrote custom-data keys in $customDataMode."
}

$worldPolicy = New-Policy -Mode "Enforce"
foreach ($itemWorldLevel in @(0, 1, 2, 10, 255)) {
    $worldBoundary = New-Snapshot -Items @(
        (New-Item -Prefab "Ordinary" -Stack 1 -WorldLevel $itemWorldLevel))
    $worldBoundaryResult = Invoke-Evaluation $worldPolicy $baseline $worldBoundary
    Assert-True (-not $worldBoundaryResult.Rejected -and
        $worldBoundaryResult.Violations.Count -eq 0 -and
        $worldBoundaryResult.Observations.Count -eq 0) `
        "In-range world level $itemWorldLevel produced a policy finding under Enforce."
    Assert-True ($worldBoundary.Items[0].WorldLevel -eq $itemWorldLevel) `
        "Semantic evaluation rewrote item world level $itemWorldLevel."
}

$modItemResult = Invoke-Evaluation `
    $worldPolicy `
    $baseline `
    $worldBoundary
Assert-True (-not $modItemResult.Rejected) `
    "A valid raw item unexpectedly requires the server ObjectDB catalog."
$observeModItemResult = Invoke-Evaluation `
    $observePolicy `
    $baseline `
    $hardCandidate
Assert-True (-not $observeModItemResult.Rejected -and $observeModItemResult.Observations.Count -gt 0) `
    "Stored Observe must retain raw mod items and still report configured forbidden-item policy."

$deltaPolicy = New-Policy -SkillBurst 1 -SkillRate 2
$deltaPrevious = New-Snapshot `
    -Skills @((New-Skill -Type 1 -Level 10)) `
    -Items @((New-Item -Prefab "Arrow" -Stack 10))
$deltaCandidate = New-Snapshot `
    -Skills @((New-Skill -Type 1 -Level 20)) `
    -Items @(
        (New-Item -Prefab "Arrow" -Stack 10),
        (New-Item -Prefab "Arrow" -Stack 10),
        (New-Item -Prefab "Wood" -Stack 10))
$deltaResult = Invoke-Evaluation `
    $deltaPolicy `
    $deltaPrevious `
    $deltaCandidate `
    -ElapsedSeconds 60
Assert-True (-not $deltaResult.Rejected) `
    "Transition observations rejected a candidate revision."
Assert-Finding $deltaResult.Observations "skill_gain:1" `
    "An abnormal server-timed skill gain was not observed."
Assert-True ($deltaResult.Observations.Count -eq 1) `
    "Inventory quantity changes added findings to the skill-only transition observation."

# Full and accepted-revision evaluation must ignore inventory gains of any size,
# while the aggregate remains available to accepted player item-change logging.
$largeItemCandidate = New-Snapshot -Items @(
    (New-Item -Prefab "Arrow" -Stack ([int]::MaxValue)),
    (New-Item -Prefab "Arrow" -Stack ([int]::MaxValue)),
    (New-Item -Prefab "Wood" -Stack ([int]::MaxValue)))
Assert-True ($largeItemCandidate.ItemTotalsByPrefab["Arrow"] -eq
    ([long][int]::MaxValue * 2)) `
    "Removing item observations lost the long per-prefab totals used by player logs."
foreach ($observationMode in @("Disabled", "Observe", "Enforce")) {
    $observationPolicy = New-Policy -Mode $observationMode
    foreach ($itemOnlyResult in @(
            (Invoke-Evaluation $observationPolicy $deltaPrevious $largeItemCandidate),
            (Invoke-RevisionEvaluation $observationPolicy $deltaPrevious $largeItemCandidate),
            (Invoke-RevisionEvaluation $observationPolicy $deltaPrevious $largeItemCandidate `
                -BypassForbiddenItemPrefabs $true))) {
        Assert-True (-not $itemOnlyResult.Rejected -and
            $itemOnlyResult.Violations.Count -eq 0 -and
            $itemOnlyResult.Observations.Count -eq 0) `
            "Large legal inventory gains produced an anomaly observation in $observationMode."
    }

    # Default skill allowance remains exactly 2 + 10 levels per server minute,
    # independent of item-policy mode or the trusted-admin item exemption.
    $skillBoundary = New-Snapshot -Skills @((New-Skill -Type 1 -Level 22))
    $skillAboveBoundary = New-Snapshot -Skills @((New-Skill -Type 1 -Level 23))
    $boundarySkillResult = Invoke-Evaluation $observationPolicy $deltaPrevious $skillBoundary
    Assert-True ($boundarySkillResult.Observations.Count -eq 0) `
        "The 2 + 10/min skill allowance boundary changed in $observationMode."
    foreach ($skillOnlyResult in @(
            (Invoke-Evaluation $observationPolicy $deltaPrevious $skillAboveBoundary),
            (Invoke-RevisionEvaluation $observationPolicy $deltaPrevious $skillAboveBoundary),
            (Invoke-RevisionEvaluation $observationPolicy $deltaPrevious $skillAboveBoundary `
                -BypassForbiddenItemPrefabs $true))) {
        Assert-True (-not $skillOnlyResult.Rejected -and
            $skillOnlyResult.Observations.Count -eq 1) `
            "Always-on skill observation rejected, disappeared, or duplicated in $observationMode."
        Assert-Finding $skillOnlyResult.Observations "skill_gain:1" `
            "The 2 + 10/min skill allowance was not observed in $observationMode."
    }
}

$windowFlags = [Reflection.BindingFlags]::Instance -bor
    [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic
$skillWindowConstructor = $skillWindowType.GetConstructors($windowFlags) |
    Where-Object { $_.GetParameters().Count -eq 3 } |
    Select-Object -First 1
$beginSkillWindow = $skillWindowType.GetMethod(
    "BeginSession",
    $instanceNonPublic)
$commitSkillWindow = $skillWindowType.GetMethod(
    "CommitAccepted",
    $instanceNonPublic)
$endSkillWindow = $skillWindowType.GetMethod(
    "EndSession",
    $instanceNonPublic)
$getSkillBaseline = $skillWindowType.GetMethod(
    "GetBaseline",
    $instanceNonPublic)
Assert-True (
    $null -ne $skillWindowConstructor -and
    $null -ne $beginSkillWindow -and
    $null -ne $commitSkillWindow -and
    $null -ne $endSkillWindow -and
    $null -ne $getSkillBaseline) `
    "The retained skill observation window is incomplete."

$windowHash1 = [byte[]](1..32)
$windowHash2 = [byte[]](33..64)
$rollingIntermediate = New-Snapshot `
    -Skills @((New-Skill -Type 1 -Level 11))
$rollingCandidate = New-Snapshot `
    -Skills @((New-Skill -Type 1 -Level 13))
$rollingWindow = $skillWindowConstructor.Invoke(
    [object[]]@([long]1, $windowHash1, $deltaPrevious))
$beginSkillWindow.Invoke(
    $rollingWindow,
    [object[]]@([long]1, $windowHash1, $deltaPrevious)) | Out-Null
$commitSkillWindow.Invoke(
    $rollingWindow,
    [object[]]@(
        [long]1,
        [long]2,
        $windowHash1,
        $windowHash2,
        $rollingIntermediate)) | Out-Null
$endSkillWindow.Invoke($rollingWindow, [object[]]@()) | Out-Null
$beginSkillWindow.Invoke(
    $rollingWindow,
    [object[]]@([long]2, $windowHash2, $rollingIntermediate)) | Out-Null
$baselineArguments = [object[]]@(
    [long]2,
    $windowHash2,
    $null,
    $null)
$getSkillBaseline.Invoke(
    $rollingWindow,
    $baselineArguments) | Out-Null
$retainedSkillBaseline = $baselineArguments[2]
Assert-True (
    (Get-SnapshotSkillLevel $retainedSkillBaseline 1) -eq 10) `
    "An accepted save or reconnect reset the skill burst baseline."

$rollingPolicy = New-Policy `
    -SkillBurst 2 `
    -SkillRate 0
$rollingEvaluator = $evaluatorConstructor.Invoke(
    [object[]]@($rollingPolicy))
$rollingResult = $evaluateRevision.Invoke(
    $rollingEvaluator,
    [object[]]@(
        $identity,
        $retainedSkillBaseline,
        $rollingCandidate,
        [TimeSpan]::Zero,
        $false))
Assert-Finding $rollingResult.Observations "skill_gain:1" `
    "Split saves and reconnect reset the cumulative skill-rate envelope."
$endSkillWindow.Invoke($rollingWindow, [object[]]@()) | Out-Null

$nextLevelRequirement =
    [Math]::Pow([Math]::Floor(10 + 1), 1.5) * 0.5 + 0.5
$rolloverPolicy = New-Policy `
    -SkillBurst ([single]0.2) `
    -SkillRate 0
$rolloverPrevious = New-Snapshot -Skills @(
    (New-Skill `
        -Type 1 `
        -Level 10 `
        -Accumulator ([single]($nextLevelRequirement * 0.9))))
$rolloverCandidate = New-Snapshot -Skills @(
    (New-Skill -Type 1 -Level 11 -Accumulator 0))
$rolloverResult = Invoke-Evaluation `
    $rolloverPolicy `
    $rolloverPrevious `
    $rolloverCandidate `
    -ElapsedSeconds 0
Assert-True ($rolloverResult.Observations.Count -eq 0) `
    "A normal skill level rollover was counted as a full level gain."

$accumulatorPolicy = New-Policy `
    -SkillBurst 100 `
    -SkillRate 0
$accumulatorCandidate = New-Snapshot -Skills @(
    (New-Skill `
        -Type 1 `
        -Level 10 `
        -Accumulator ([single]($nextLevelRequirement + 1))))
$accumulatorResult = Invoke-Evaluation `
    $accumulatorPolicy `
    $baseline `
    $accumulatorCandidate `
    -ElapsedSeconds 0
Assert-Finding $accumulatorResult.Observations "skill_accumulator:1" `
    "An accumulator outside the vanilla next-level envelope was not observed."
$absoluteEvaluator = $evaluatorConstructor.Invoke(
    [object[]]@($accumulatorPolicy))
$absoluteResult = $evaluateAbsolute.Invoke(
    $absoluteEvaluator,
    [object[]]@(
        $identity,
        $accumulatorCandidate))
Assert-Finding $absoluteResult.Observations "skill_accumulator:1" `
    "Stored-snapshot validation omitted the absolute accumulator observation."
$absoluteSkillRateFinding = $false
foreach ($finding in $absoluteResult.Observations) {
    if ([string]$finding -like "*skill_gain:*") {
        $absoluteSkillRateFinding = $true
    }
}
Assert-True (-not $absoluteSkillRateFinding) `
    "Absolute stored-snapshot validation ran a skill-rate transition."
foreach ($accumulatorMode in @("Disabled", "Observe", "Enforce")) {
    $alwaysOnPolicy = New-Policy -Mode $accumulatorMode -SkillBurst 100 -SkillRate 0
    $alwaysOnEvaluator = $evaluatorConstructor.Invoke([object[]]@($alwaysOnPolicy))
    foreach ($alwaysOnResult in @(
            (Invoke-Evaluation $alwaysOnPolicy $baseline $accumulatorCandidate),
            (Invoke-RevisionEvaluation $alwaysOnPolicy $baseline $accumulatorCandidate),
            (Invoke-RevisionEvaluation $alwaysOnPolicy $baseline $accumulatorCandidate `
                -BypassForbiddenItemPrefabs $true),
            ($evaluateAbsolute.Invoke($alwaysOnEvaluator,
                [object[]]@($identity, $accumulatorCandidate))))) {
        Assert-True (-not $alwaysOnResult.Rejected -and
            $alwaysOnResult.Observations.Count -eq 1) `
            "Accumulator observation was suppressed, rejected, or duplicated in $accumulatorMode."
        Assert-Finding $alwaysOnResult.Observations "skill_accumulator:1" `
            "Always-on skill accumulator observation was lost in $accumulatorMode."
    }
}

# A mod-owned skill ID has no vanilla XP curve. Preserve its raw accumulator
# without manufacturing XP findings; level changes still use the configured
# observation window. Built-in skills outside the vanilla curve range likewise
# fall back to level delta instead of clamping values or producing NaN math.
foreach ($rawSkillId in @(-700123, 1980891425, [int]::MinValue, [int]::MaxValue)) {
    $customBefore = New-Snapshot -Skills @((New-Skill -Type $rawSkillId -Level 125 -Accumulator ([single]::MinValue)))
    $customAfter = New-Snapshot -Skills @((New-Skill -Type $rawSkillId -Level 125 -Accumulator ([single]::MaxValue)))
    foreach ($customMode in @('Disabled', 'Observe', 'Enforce')) {
        $customPolicy = New-Policy -Mode $customMode -SkillBurst 2 -SkillRate 0
        $customResult = Invoke-Evaluation $customPolicy $customBefore $customAfter
        Assert-True (-not $customResult.Rejected -and $customResult.Observations.Count -eq 0) `
            "A custom skill XP value was interpreted using a vanilla accumulator curve."
        $customGained = New-Snapshot -Skills @((New-Skill -Type $rawSkillId -Level 128 -Accumulator ([single]::MaxValue)))
        $customGainResult = Invoke-Evaluation $customPolicy $customBefore $customGained
        Assert-True (-not $customGainResult.Rejected -and $customGainResult.Observations.Count -eq 1) `
            "Custom skill level observation was suppressed, rejected, or mixed with vanilla XP findings."
        Assert-Finding $customGainResult.Observations ("skill_gain:" + $rawSkillId) `
            "A custom skill level increase lost its exact numeric ID in observation evidence."
    }
}
foreach ($rawSkillLevel in @([single]-5, [single]150)) {
    $rawBefore = New-Snapshot -Skills @((New-Skill -Type 1 -Level $rawSkillLevel -Accumulator ([single]-20)))
    $rawAfter = New-Snapshot -Skills @((New-Skill -Type 1 -Level ($rawSkillLevel + 3) -Accumulator ([single]::MaxValue)))
    $rawResult = Invoke-Evaluation (New-Policy -SkillBurst 2 -SkillRate 0) $rawBefore $rawAfter
    Assert-True (-not $rawResult.Rejected -and $rawResult.Observations.Count -eq 1) `
        "An out-of-vanilla-range built-in skill used invalid XP arithmetic instead of raw level delta."
    Assert-Finding $rawResult.Observations 'skill_gain:1' `
        "Raw built-in skill level changes no longer produce bounded-policy observations."
}

$splitCandidate = New-Snapshot -Items @(
    (New-Item -Prefab "Arrow" -Stack 5),
    (New-Item -Prefab "Arrow" -Stack 5))
$splitResult = Invoke-Evaluation `
    $deltaPolicy `
    $deltaPrevious `
    $splitCandidate
Assert-True ($splitResult.Observations.Count -eq 0) `
    "Stack splitting without a quantity increase produced an observation."

$emptyBaseline = New-Snapshot -HasPlayerData $false
$firstSaveResult = Invoke-Evaluation `
    $deltaPolicy `
    $emptyBaseline `
    $deltaCandidate `
    -ElapsedSeconds 1
Assert-True ($firstSaveResult.Observations.Count -eq 0) `
    "The first clean-profile save produced transition observations."

$materializationHash1 = [byte[]](65..96)
$materializationHash2 = [byte[]](97..128)
$materializationWindow = $skillWindowConstructor.Invoke(
    [object[]]@(
        [long]1,
        $materializationHash1,
        $emptyBaseline))
$beginSkillWindow.Invoke(
    $materializationWindow,
    [object[]]@(
        [long]1,
        $materializationHash1,
        $emptyBaseline)) | Out-Null
$commitSkillWindow.Invoke(
    $materializationWindow,
    [object[]]@(
        [long]1,
        [long]2,
        $materializationHash1,
        $materializationHash2,
        $deltaCandidate)) | Out-Null
$materializationArguments = [object[]]@(
    [long]2,
    $materializationHash2,
    $null,
    $null)
$getSkillBaseline.Invoke(
    $materializationWindow,
    $materializationArguments) | Out-Null
Assert-True (
    (Get-SnapshotSkillLevel $materializationArguments[2] 1) -eq 20) `
    "The first accepted materialization did not establish its skill baseline."
$endSkillWindow.Invoke(
    $materializationWindow,
    [object[]]@()) | Out-Null

$disabledPolicy = New-Policy `
    -Mode "Disabled" `
    -ForbiddenPrefabs "ForbiddenSword"
$disabledResult = Invoke-Evaluation `
    $disabledPolicy `
    $baseline `
    $hardCandidate
Assert-True (
    -not $disabledResult.Rejected -and
    $disabledResult.Observations.Count -eq 0) `
    "Disabled semantic policy still produced a decision."
$missingPlayerResult = Invoke-Evaluation `
    $disabledPolicy `
    $baseline `
    (New-Snapshot -HasPlayerData $false)
Assert-True $missingPlayerResult.Rejected `
    "Disabled policy accepted a candidate without inner Player data."

$duplicateRejected = $false
try {
    New-Policy -ForbiddenPrefabs "Duplicate,Duplicate" | Out-Null
}
catch {
    $duplicateRejected = $true
}
Assert-True $duplicateRejected `
    "A duplicate exact policy token was silently accepted."

$crlfPolicy = New-Policy -ForbiddenPrefabs "One`r`nTwo"
Assert-True ($crlfPolicy.ForbiddenItemPrefabCount -eq 2) `
    "CRLF-separated policy entries were not parsed deterministically."

$formatCharacterRejected = $false
try {
    New-Policy `
        -ForbiddenPrefabs ("Visible" + [char]0x202E + "Spoof") |
        Out-Null
}
catch {
    $formatCharacterRejected = $true
}
Assert-True $formatCharacterRejected `
    "A Unicode bidi-format policy token was accepted into diagnostics."

$staticPublic = [Reflection.BindingFlags]::Static -bor
    [Reflection.BindingFlags]::Public
$createEnvelope = $envelopeType.GetMethod(
    "Create",
    $staticPublic)
$isInitialUnmaterialized =
    $snapshotServiceType.GetMethod(
        "IsInitialUnmaterializedSnapshot",
        $staticNonPublic)
$snapshotKind = [Enum]::Parse(
    $envelopeKindType,
    "Snapshot")
$initialEnvelope = $createEnvelope.Invoke(
    $null,
    [object[]]@(
        $snapshotKind,
        [long]1,
        [long]0,
        [Guid]::NewGuid(),
        $identity,
        [DateTime]::UtcNow,
        43,
        [byte[]]@(1)))
$forgedInitialEnvelope = $createEnvelope.Invoke(
    $null,
    [object[]]@(
        $snapshotKind,
        [long]1,
        [long]1,
        [Guid]::NewGuid(),
        $identity,
        [DateTime]::UtcNow,
        43,
        [byte[]]@(1)))
Assert-True (
    [bool]$isInitialUnmaterialized.Invoke(
        $null,
        [object[]]@($initialEnvelope, $emptyBaseline))) `
    "The exact revision-1/base-0 clean snapshot was not recognized."
Assert-True (
    -not [bool]$isInitialUnmaterialized.Invoke(
        $null,
        [object[]]@($forgedInitialEnvelope, $emptyBaseline))) `
    "A revision-1 snapshot with a nonzero base bypassed stored-state policy."

$repositoryConstructor = $repositoryType.GetConstructors($windowFlags) |
    Where-Object { $_.GetParameters().Count -eq 5 } |
    Select-Object -First 1
$repositoryEvaluateLiveCandidate = $repositoryType.GetMethods($instanceNonPublic) |
    Where-Object {
        $_.Name -eq "EvaluateLiveCandidate" -and
        $_.GetParameters().Count -eq 5
    } |
    Select-Object -First 1
$repositoryPersistCheckpointEntry = $repositoryType.GetMethods($instanceNonPublic) |
    Where-Object {
        $_.Name -eq "PersistCheckpointEntry" -and
        $_.GetParameters().Count -eq 2
    } |
    Select-Object -First 1
$removedBatchCheckpoint = @($repositoryType.GetMethods($instanceNonPublic) |
    Where-Object { $_.Name -eq "PersistCheckpoint" })
$removedImmediateCommit = @($repositoryType.GetMethods($instanceNonPublic) |
    Where-Object { $_.Name -eq "Commit" })
$repositoryCountProfilesForAccount = $repositoryType.GetMethod(
    "CountProfilesForAccount",
    $instanceNonPublic)
Assert-True (
    $null -ne $repositoryConstructor -and
    $null -ne $repositoryEvaluateLiveCandidate -and
    $null -ne $repositoryPersistCheckpointEntry -and
    $removedBatchCheckpoint.Count -eq 0 -and
    $removedImmediateCommit.Count -eq 0) `
    "The RAM-only semantic admission or per-entry world-checkpoint durability seam is missing."
Assert-True ($null -ne $repositoryCountProfilesForAccount) `
    "The repository account-quota counting seam is missing."

$repositoryTestRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("ServerManagerSemantic-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($repositoryTestRoot) | Out-Null
try {
    $storageOptions = [Activator]::CreateInstance($storageOptionsType)
    $storageLayoutConstructor = $storageLayoutType.GetConstructor(
        [Type[]]@([string]))
    $storageLayout = $storageLayoutConstructor.Invoke(
        [object[]]@([string]$repositoryTestRoot))
    $storageKey = "Steam_76561198000000000_semantictest"
    $storageLayout.EnsureDirectories()
    $expectedStorageRoot = [IO.Path]::GetFullPath($repositoryTestRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $profilePath = $storageLayout.GetProfilePath($storageKey)
    $expectedProfilePath = [IO.Path]::Combine(
        [IO.Path]::Combine($expectedStorageRoot, "76561198000000000"),
        $storageKey + ".fch")
    $backupDirectory = $storageLayout.GetAccountDirectory($storageKey)
    $expectedBackupDirectory = [IO.Path]::Combine(
        $expectedStorageRoot,
        "76561198000000000")
    Assert-True (
        [string]::Equals(
            $profilePath,
            $expectedProfilePath,
            [StringComparison]::OrdinalIgnoreCase)) `
        "The primary character path is not inside the matching SteamID directory."
    Assert-True (
        [string]::Equals(
            $backupDirectory,
            $expectedBackupDirectory,
            [StringComparison]::OrdinalIgnoreCase)) `
        "Backups do not share the primary's SteamID directory."
    Assert-True (
        -not [IO.Directory]::Exists((
            [IO.Path]::Combine($expectedStorageRoot, "profiles")))) `
        "EnsureDirectories recreated the obsolete profiles directory."
    Assert-True (
        -not [IO.Directory]::Exists((
            [IO.Path]::Combine($expectedStorageRoot, "backups")))) `
        "EnsureDirectories recreated the obsolete backups directory."
    Assert-True (-not [IO.Directory]::Exists($backupDirectory)) `
        "A read-only account path getter created a storage directory."
    $storageCodec = [Activator]::CreateInstance(
        $envelopeCodecType,
        [object[]]@($storageOptions))
    # This fixture isolates RAM semantic admission and deliberately never asks
    # the repository to decode disk. A non-null uninitialized codec avoids
    # pulling Unity/Valheim runtime dependencies into this focused test.
    $storageProfileCodec =
        [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
            $profileCodecType)
    $rejectingProxy = [SemanticRevisionValidatorProxy]::new(
        $revisionValidatorType,
        $missingPlayerResult)
    $repository = $rejectingProxy.CreateRepository(
        $repositoryConstructor,
        $storageLayout,
        $storageOptions,
        $storageProfileCodec)

    $authoritativePayload =
        [Text.Encoding]::UTF8.GetBytes("authoritative-before")
    $candidatePayload =
        [Text.Encoding]::UTF8.GetBytes("candidate-after")
    $repositorySessionId = [Guid]::NewGuid()
    $authoritativeEnvelope = $createEnvelope.Invoke(
        $null,
        [object[]]@(
            $snapshotKind,
            [long]7,
            [long]6,
            $repositorySessionId,
            $identity,
            [DateTime]::UtcNow,
            43,
            $authoritativePayload))
    $saveRequestKind = [Enum]::Parse(
        $envelopeKindType,
        "SaveRequest")
    $saveRequestEnvelope = $createEnvelope.Invoke(
        $null,
        [object[]]@(
            $saveRequestKind,
            [long]8,
            [long]7,
            $repositorySessionId,
            $identity,
            [DateTime]::UtcNow,
            43,
            $candidatePayload))

    [IO.Directory]::CreateDirectory(
        [IO.Path]::GetDirectoryName($profilePath)) | Out-Null
    $encodeEnvelope = $envelopeCodecType.GetMethod(
        "Encode",
        [Type[]]@($envelopeType))
    $encodedAuthoritative = [byte[]]$encodeEnvelope.Invoke(
        $storageCodec,
        [object[]]@($authoritativeEnvelope))
    $nestedPrimaryDirectory = [IO.Path]::Combine(
        $repositoryTestRoot,
        "not-primary")
    [IO.Directory]::CreateDirectory($nestedPrimaryDirectory) | Out-Null
    [IO.File]::WriteAllBytes(
        [IO.Path]::Combine(
            $nestedPrimaryDirectory,
            $storageKey + ".character"),
        $encodedAuthoritative)
    $nestedOnlyProfileCount = [int]$repositoryCountProfilesForAccount.Invoke(
        $repository,
        [object[]]@($identity.AccountId))
    Assert-True ($nestedOnlyProfileCount -eq 0) `
        "A nested .character file was counted as an authoritative primary."
    [IO.File]::WriteAllBytes($profilePath, $encodedAuthoritative)
    $primaryBefore = [IO.File]::ReadAllBytes($profilePath)
    $backupsBefore = [IO.Directory]::GetFiles(
        $backupDirectory,
        "$storageKey.*.fch",
        [IO.SearchOption]::TopDirectoryOnly)
    $repositorySkillWindow = $skillWindowConstructor.Invoke(
        [object[]]@(
            [long]7,
            $authoritativeEnvelope.GetPayloadSha256Copy(),
            $deltaPrevious))
    $beginSkillWindow.Invoke(
        $repositorySkillWindow,
        [object[]]@(
            [long]7,
            $authoritativeEnvelope.GetPayloadSha256Copy(),
            $deltaPrevious)) | Out-Null
    $repositorySession =
        [SemanticRepositoryFixture]::CreateSession(
            $sessionType,
            $identity,
            $storageKey,
            $repositorySessionId,
            [long]7,
            [long]42,
            $authoritativeEnvelope.GetPayloadSha256Copy(),
            $repositorySkillWindow)

    $admissionOutcome = $repositoryEvaluateLiveCandidate.Invoke(
        $repository,
        [object[]]@(
            $repositorySession,
            $authoritativeEnvelope,
            $saveRequestEnvelope,
            $baseline,
            [DateTime]::UtcNow))

    $primaryAfter = [IO.File]::ReadAllBytes($profilePath)
    $backupsAfter = [IO.Directory]::GetFiles(
        $backupDirectory,
        "$storageKey.*.fch",
        [IO.SearchOption]::TopDirectoryOnly)
    $temporaryFiles = [IO.Directory]::GetFiles(
        $repositoryTestRoot,
        ".$storageKey.*",
        [IO.SearchOption]::AllDirectories)
    $sessionRevision =
        [long][SemanticRepositoryFixture]::GetField(
            $repositorySession,
            "_currentRevision")
    $repositoryBaselineArguments = [object[]]@(
        [long]7,
        $authoritativeEnvelope.GetPayloadSha256Copy(),
        $null,
        $null)
    $getSkillBaseline.Invoke(
        $repositorySkillWindow,
        $repositoryBaselineArguments) | Out-Null

    Assert-True (
        $admissionOutcome.Status.ToString() -eq "Rejected") `
        "The fake hard policy did not reject live-shadow admission."
    Assert-True ($rejectingProxy.EvaluateCalls -eq 1) `
        "The repository did not evaluate the exact current revision once."
    Assert-True (
        [Convert]::ToBase64String($primaryAfter) -eq
        [Convert]::ToBase64String($primaryBefore)) `
        "A semantic rejection changed the authoritative primary bytes."
    Assert-True (
        $backupsAfter.Count -eq $backupsBefore.Count -and
        $temporaryFiles.Count -eq 0) `
        "A semantic rejection rotated a backup or left a temporary file."
    Assert-True ($sessionRevision -eq 7) `
        "A semantic rejection advanced the session revision."
    Assert-True (
        (Get-SnapshotSkillLevel `
            $repositoryBaselineArguments[2] `
            1) -eq 10) `
        "A semantic rejection advanced the retained skill baseline."
    Assert-True (
        $admissionOutcome.Current.Revision -eq 7 -and
        [Convert]::ToBase64String(
            $admissionOutcome.Current.GetPayloadCopy()) -eq
        [Convert]::ToBase64String($authoritativePayload)) `
        "A semantic rejection did not retain the authoritative response payload."
}
finally {
    if ($null -ne $repositorySkillWindow) {
        $endSkillWindow.Invoke(
            $repositorySkillWindow,
            [object[]]@()) | Out-Null
    }

    $resolvedRepositoryTestRoot =
        [IO.Path]::GetFullPath($repositoryTestRoot)
    $resolvedSystemTemp =
        [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (
        $resolvedRepositoryTestRoot.StartsWith(
            $resolvedSystemTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName(
            $resolvedRepositoryTestRoot).StartsWith(
                "ServerManagerSemantic-",
                [StringComparison]::Ordinal)) {
        Remove-Item `
            -LiteralPath $resolvedRepositoryTestRoot `
            -Recurse `
            -Force
    }
}

$repositorySource = Get-Content -LiteralPath (
    Join-Path $projectRoot "Character\CharacterRepository.cs") -Raw
$snapshotServiceSource = Get-Content -LiteralPath (
    Join-Path $projectRoot "Character\CharacterSnapshotService.cs") -Raw
$evaluateMethodStart = $repositorySource.IndexOf(
    "internal RepositoryCommitOutcome EvaluateLiveCandidate(",
    [StringComparison]::Ordinal)
$persistMethodStart = $repositorySource.IndexOf(
    "internal string PersistCheckpointEntry(",
    [StringComparison]::Ordinal)
$evaluationIndex = $repositorySource.IndexOf(
    "_revisionValidator.Evaluate(",
    $evaluateMethodStart,
    [StringComparison]::Ordinal)
$replaceIndex = $repositorySource.IndexOf(
    "ReplaceAtomicallyWithBackup(",
    $persistMethodStart,
    [StringComparison]::Ordinal)
Assert-True (
    $evaluateMethodStart -ge 0 -and
    $evaluationIndex -gt $evaluateMethodStart -and
    $evaluationIndex -lt $persistMethodStart -and
    $replaceIndex -gt $persistMethodStart -and
    $snapshotServiceSource.Contains(
        "_repository.EvaluateLiveCandidate(") -and
    -not $snapshotServiceSource.Contains("_repository.Commit(")) `
    "Semantic policy is no longer enforced at RAM-shadow admission before checkpoint-only durability."

Write-Output (
    "Character semantic hard-policy, Observe mode, in-range world-level acceptance and preservation, " +
    "bounded forbidden-prefab admin observations without structural/stat exemptions, " +
    "custom-data key acceptance/preservation, stat ceiling, catalog-independent raw items, " +
    "removed item-gain observations, always-on 2 + 10/min skill observations, " +
    "retained reconnect skill window, accumulator rollover, first-save " +
    "suppression, stored-snapshot gate, parser, RAM-shadow admission rejection, " +
    "and checkpoint-only durability smoke tests passed.")
