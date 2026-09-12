param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)
$ErrorActionPreference = "Stop"
# Reuse the established in-memory Unity shims and real repository fixture.
# It performs its own independent host lifecycle assertions first.
. (Join-Path $PSScriptRoot "LocalHostCharacterServiceSmoke.ps1") -Configuration $Configuration -GamePath $GamePath
[AppDomain]::CurrentDomain.add_AssemblyResolve($fixtureAssemblyResolver)

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("smadmin-" + [Guid]::NewGuid().ToString("N"))
$service = $null
$keys = $null
$restoreService = $null
$restoreKeys = $null
try {
    $options = New-Instance "CharacterStorageOptions"
    $options.SaveRequestBurstCapacity = 100
    $layout = New-Instance "CharacterStorageLayout" @($testRoot)
    $keys = New-Instance "CharacterStorageKeyProvider" @($layout)
    $codec = New-Instance "CharacterEnvelopeCodec" @($options)
    $profiles = New-Instance "ValheimPlayerProfileCodec" @($options)
    $mode = [Enum]::Parse($plugin.GetType("ServerManager.CharacterSemanticPolicyMode"), "Enforce")
    $policy = New-Instance "CharacterSemanticPolicy" @($mode, "ForbiddenSword", [single]1000, [single]1000, [single]1000, [single]100, [single]1000)
    $evaluator = New-Instance "CharacterSemanticEvaluator" @($policy)
    $validator = New-Instance "CharacterSemanticRevisionValidator" @($profiles, $evaluator)
    $repository = New-Instance "CharacterRepository" @($layout, $options, $profiles, $validator, $null)
    $resolver = New-Instance "CharacterPeerIdentityResolver"
    $service = New-Instance "CharacterSnapshotService" @($options, $resolver, $keys, $codec, $profiles, $repository, $validator)
    $identity = New-Instance "CharacterIdentity" @("steamworks:76561198000000001", "AdminHero")
    $storageKey = $keys.DeriveStorageKey($identity)
    $profilePath = $layout.GetProfilePath($storageKey)
    function Edit-Offline([string]$Operation, [string]$Skill, [single]$Value = 0) {
        return Invoke-Hidden $script:service "ApplyOfflineSkillAdmin" @($script:identity.AccountId, $script:identity.CharacterName, $Operation, $Skill, $Value)
    }
    $missing = Edit-Offline "set" "Swords" 10
    Assert-True (-not $missing.Success -and $missing.Code -eq "character_not_found" -and -not (Test-Path -LiteralPath $profilePath)) "Missing profile was created by offline administration."
    $opened = Invoke-Hidden $service "OpenOrCreateLocalHostSession" @($identity)
    $sessionId = $opened.Snapshot.SessionId
    Assert-True ((Edit-Offline "set" "Swords" 10).Code -eq "character_busy") "A connecting host lease did not block offline edits."
    $records = Invoke-Hidden $service "GetAdminCharacters"
    Assert-True ($records.Count -eq 1 -and (Get-Hidden $records[0] "IsOnline")) "Connecting host was absent from admin list."
    Invoke-Hidden $service "FinalizePendingLocalHostSnapshot" @($sessionId) | Out-Null
    $lookup = [object[]]@($sessionId, $null)
    Invoke-Hidden $service "TryGetLocalHostSession" $lookup | Out-Null
    [byte[]]$payload = New-ProfilePayload "AdminHero" $lookup[1].PlayerId "retained-ram-marker"
    $request = New-Request $identity $sessionId 2 1 $payload
    Assert-True (Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $request)).Accepted "Admin fixture live save failed."
    Invoke-Hidden $service "CloseLocalHostSession" @($sessionId) | Out-Null
    [byte[]]$beforeDisk = [IO.File]::ReadAllBytes($profilePath)
    $checkpoint = Invoke-Hidden $service "BeginCheckpoint"
    Assert-True ((Edit-Offline "set" "Swords" 10).Code -eq "checkpoint_pending") "Pending checkpoint did not block an offline edit."
    Invoke-Hidden $service "DiscardCheckpoint" @($checkpoint) | Out-Null
    $edited = Edit-Offline "set" "Swords" 42.5
    Assert-True ($edited.Success -and $edited.Code -eq "staged_in_ram" -and $edited.Data["Swords"] -eq "42.5" -and $edited.Data["Swords.xp"] -eq "0") "Skill set or XP reset failed."
    Assert-True ($edited.Message.Contains("Swords = 42.5 (XP 0)") -and $edited.Message.Length -le 1500) "Offline set omitted readable bounded skill values."
    Assert-True (Test-Bytes $beforeDisk ([IO.File]::ReadAllBytes($profilePath))) "Offline skill edit wrote durable bytes before a world checkpoint."
    $records = Invoke-Hidden $service "GetAdminCharacters"
    Assert-True ($records.Count -eq 1 -and (Get-Hidden $records[0] "Revision") -eq 3 -and (Get-Hidden $records[0] "DurableRevision") -eq 2 -and -not (Get-Hidden $records[0] "IsOnline")) "Offline edit lost RAM revision or promoted durable baseline."
    $liveSnapshots = $service.GetType().GetField("_liveSnapshots", $allInstance).GetValue($service)
    $beforeRejectedLive = $liveSnapshots[$storageKey]
    foreach ($removedOperation in @("add", "reset", "ADD", "RESET")) {
        $rejected = Edit-Offline $removedOperation "Swords" 1
        Assert-True (-not $rejected.Success -and $rejected.Code -eq "invalid_skill" -and
            [object]::ReferenceEquals($beforeRejectedLive, $liveSnapshots[$storageKey])) "Removed offline skill operation changed retained RAM."
    }
    Assert-True (Test-Bytes $beforeDisk ([IO.File]::ReadAllBytes($profilePath))) "Removed offline skill operation changed disk."
    Assert-True (-not (Edit-Offline "set" "Swords" 101).Success) "Out-of-range skill value was accepted."
    Assert-True (-not (Edit-Offline "set" "Swords" ([single]::NaN)).Success) "NaN skill value was accepted."
    Assert-True (-not (Edit-Offline "set" "None" 0).Success) "None skill sentinel was accepted."
    Assert-True ((Edit-Offline "get" "Swords").Data["Swords"] -eq "42.5") "Rejected edits changed the skill value."
    Assert-True ((Edit-Offline "get" "Swords").Message.Contains("Swords = 42.5 (XP 0)")) "Skill get discarded values on message-only transports."
    $reopened = Invoke-Hidden $service "OpenOrCreateLocalHostSession" @($identity)
    [byte[]]$materialized = $reopened.Snapshot.GetPayloadCopy()
    Assert-True ($reopened.Snapshot.Revision -eq 3) "Rejoin loaded disk instead of edited RAM."
    $decodeProfile = $plugin.GetType("ServerManager.ValheimPlayerProfileCodec").GetMethod("DeserializeProfileFromBytes")
    $oldProfile = $decodeProfile.Invoke($profiles, [object[]]@($payload, $null, [FileHelpers+FileSource]::Local))
    $newProfile = $decodeProfile.Invoke($profiles, [object[]]@($materialized, $null, [FileHelpers+FileSource]::Local))
    $playerDataField = $game.GetType("PlayerProfile").GetField("m_playerData", $allInstance)
    [byte[]]$oldInner = $playerDataField.GetValue($oldProfile)
    [byte[]]$newInner = $playerDataField.GetValue($newProfile)
    # Skills-v2/count, custom-data count, stamina/eitr and empty build-menu state (28 bytes).
    $skillsOffset = $oldInner.Length - 28
    Assert-True ($newInner.Length -eq $oldInner.Length + 12 -and
        (Test-Bytes ([byte[]]$oldInner[0..($skillsOffset - 1)]) ([byte[]]$newInner[0..($skillsOffset - 1)])) -and
        (Test-Bytes ([byte[]]$oldInner[($oldInner.Length - 20)..($oldInner.Length - 1)]) ([byte[]]$newInner[($newInner.Length - 20)..($newInner.Length - 1)]))) "Skill splice changed non-skill player fields."
    $oldOuterLength = $payload.Length - $oldInner.Length - 4
    Assert-True (Test-Bytes ([byte[]]$payload[0..($oldOuterLength - 1)]) ([byte[]]$materialized[0..($oldOuterLength - 1)])) "Skill splice changed outer identity, stats, or metadata."
    $replaceSkill = $plugin.GetType("ServerManager.ValheimPlayerProfileCodec").GetMethod("ReplaceSkillAdmin", $allInstance)
    foreach ($unsupported in @(
        @{ Offset = 0; Value = 44 },
        @{ Offset = $oldOuterLength + 4; Value = 30 },
        @{ Offset = $oldOuterLength + 4 + $skillsOffset; Value = 3 })) {
        [byte[]]$invalidVersion = $payload.Clone()
        [Array]::Copy([BitConverter]::GetBytes([int]$unsupported.Value), 0, $invalidVersion, [int]$unsupported.Offset, 4)
        Assert-Throws { $replaceSkill.Invoke($profiles, [object[]]@($identity, $invalidVersion, "set", "Swords", [single]5, $null)) } "*unsupported*"
    }
    Invoke-Hidden $service "CloseLocalHostSession" @($reopened.Snapshot.SessionId) | Out-Null
    Assert-True ((Edit-Offline "set" "Swords" 50).Data["Swords"] -eq "50") "A second set did not replace the exact level."
    $zeroed = Edit-Offline "set" "Swords" 0
    $records = Invoke-Hidden $service "GetAdminCharacters"
    $zeroedSkills = (Get-Hidden $records[0] "Snapshot").Skills
    $swordsId = [int][Skills+SkillType]::Swords
    Assert-True ($zeroed.Success -and $zeroed.Data["Swords"] -eq "0" -and $zeroed.Data["Swords.xp"] -eq "0" -and
        $zeroedSkills.ContainsKey($swordsId) -and $zeroedSkills[$swordsId].Level -eq 0 -and
        $zeroedSkills[$swordsId].Accumulator -eq 0) "Offline set zero removed the skill record or retained XP."
    $checkpoint = Invoke-Hidden $service "BeginCheckpoint"
    $entries = Get-Hidden $checkpoint "Entries"
    Assert-True ($entries.Count -eq 1) "Offline RAM edit was absent from the next world checkpoint."
    Invoke-Hidden $service "CommitCheckpointEntry" @($checkpoint, $entries[0]) | Out-Null
    Assert-True ((Invoke-Hidden $repository "Load" @($identity, $storageKey)).Envelope.Revision -eq 1) "World checkpoint failed to expose a fresh process-local disk baseline."
    # Completed offline entries are evicted; this is now a cold disk edit.
    [byte[]]$coldDisk = [IO.File]::ReadAllBytes($profilePath)
    Assert-True (-not $liveSnapshots.ContainsKey($storageKey)) "Completed offline checkpoint retained its RAM entry."
    foreach ($removedOperation in @("add", "reset")) {
        Assert-True ((Edit-Offline $removedOperation "Swords" 1).Code -eq "invalid_skill" -and
            -not $liveSnapshots.ContainsKey($storageKey)) "Removed cold offline operation staged a RAM snapshot."
    }
    Assert-True (Test-Bytes $coldDisk ([IO.File]::ReadAllBytes($profilePath))) "Removed cold offline operation changed disk."
    Assert-True ((Edit-Offline "set" "Swords" 20).Code -eq "staged_in_ram") "Cold stored profile edit failed."
    Assert-True ((Invoke-Hidden $repository "Load" @($identity, $storageKey)).Envelope.Revision -eq 1) "Cold edit wrote disk immediately."
    $records = Invoke-Hidden $service "GetAdminCharacters"
    Assert-True ((Get-Hidden $records[0] "Revision") -eq 2 -and (Get-Hidden $records[0] "DurableRevision") -eq 1) "Cold edit did not restart from the process-local disk baseline."

    # Exercise the actual installed Skills.Load/GetSkillList methods using an
    # inert component, not a fake Player or any Unity-native gameplay behavior.
    $skillsType = $game.GetType("Skills")
    $skillType = $game.GetType("Skills+SkillType")
    $skillDefinitionType = $game.GetType("Skills+SkillDef")
    $skillStateType = $game.GetType("Skills+Skill")
    $localSkills = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($skillsType)
    # Object's managed null comparison only tests this pointer for a live
    # MonoBehaviour. No tested Skills method dereferences it or invokes Unity.
    # The sentinel is confined to this in-memory, test-only inert component.
    $unityObjectType = $skillsType.BaseType
    while ($unityObjectType.FullName -ne "UnityEngine.Object") { $unityObjectType = $unityObjectType.BaseType }
    $unityObjectType.GetField("m_CachedPtr", $allInstance).SetValue($localSkills, [IntPtr]::new(1))
    $definitionsType = [Collections.Generic.List``1].MakeGenericType($skillDefinitionType)
    $definitions = [Activator]::CreateInstance($definitionsType)
    foreach ($type in [Enum]::GetValues($skillType)) {
        if ($type.ToString() -in @("None", "All")) { continue }
        $definition = [Activator]::CreateInstance($skillDefinitionType)
        $skillDefinitionType.GetField("m_skill").SetValue($definition, $type)
        $definitionsType.GetMethod("Add").Invoke($definitions, [object[]]@($definition)) | Out-Null
    }
    $skillsType.GetField("m_skills").SetValue($localSkills, $definitions)
    $dictionaryType = [Collections.Generic.Dictionary``2].MakeGenericType($skillType, $skillStateType)
    $skillsType.GetField("m_skillData", $allInstance).SetValue($localSkills, [Activator]::CreateInstance($dictionaryType))
    $actionsType = $plugin.GetType("ServerManager.CharacterAdminActions")
    $applySkill = $actionsType.GetMethod("ApplySkill", [Reflection.BindingFlags]"Static,NonPublic")
    $editSkills = $actionsType.GetMethod("EditSkills", [Reflection.BindingFlags]"Static,NonPublic")
    $encodeSkills = $actionsType.GetMethod("EncodeSkills", [Reflection.BindingFlags]"Static,NonPublic")
    $semanticSkills = [Array]::CreateInstance($plugin.GetType("ServerManager.CharacterSemanticSkillState"), 2)
    $semanticSkills.SetValue((New-Instance "CharacterSemanticSkillState" @($swordsId, [single]82.5, [single]12.25)), 0)
    $modSkillId = [int]1980891425
    Assert-True (-not [Enum]::IsDefined($skillType, $modSkillId)) "Mod-skill fixture ID is now a vanilla skill."
    $semanticSkills.SetValue((New-Instance "CharacterSemanticSkillState" @($modSkillId, [single]77.5, [single]18.75)), 1)
    function Edit-SkillData([string]$Operation, [string]$Skill, [single]$Value = 0) {
        return ,$script:editSkills.Invoke($null, [object[]]@($script:semanticSkills, $Operation, $Skill, $Value))
    }
    foreach ($removedOperation in @("add", "reset", "ADD", "RESET")) {
        Assert-Throws { Edit-SkillData $removedOperation "Swords" 1 } "*Unsupported skill operation*"
    }
    $zeroedData = Edit-SkillData "set" "Swords" 0
    Assert-True ($zeroedData.Count -eq 2 -and $zeroedData[0].SkillType -eq $swordsId -and
        $zeroedData[0].Level -eq 0 -and $zeroedData[0].Accumulator -eq 0 -and
        [object]::ReferenceEquals($semanticSkills[1], $zeroedData[1]) -and
        $semanticSkills[0].Level -eq 82.5 -and $semanticSkills[0].Accumulator -eq 12.25) "Direct set zero changed its source, removed its record, or changed a mod skill."
    $allData = Edit-SkillData "set" "all" 25
    foreach ($state in $allData) {
        if ($state.SkillType -eq $modSkillId) {
            Assert-True ([object]::ReferenceEquals($semanticSkills[1], $state)) "Set all rewrote the unknown mod skill."
        } else {
            Assert-True ($state.Level -eq 25 -and $state.Accumulator -eq 0) "Set all failed to replace a vanilla skill."
        }
    }
    [byte[]]$seedSkillBytes = $encodeSkills.Invoke($null, [object[]]@(,$semanticSkills))
    [byte[]]$modInner = [byte[]]$oldInner[0..($skillsOffset - 1)] + $seedSkillBytes +
        [byte[]]$oldInner[($oldInner.Length - 20)..($oldInner.Length - 1)]
    [byte[]]$modPayload = [byte[]]$payload[0..($oldOuterLength - 1)] +
        [BitConverter]::GetBytes([int]$modInner.Length) + $modInner
    $modEditArguments = [object[]]@($identity, $modPayload, "set", "all", [single]25, $null)
    [byte[]]$modEditedPayload = $replaceSkill.Invoke($profiles, $modEditArguments)
    $modEditedSemantic = Get-Hidden $modEditArguments[5] "SemanticSnapshot"
    Assert-True ($modEditedSemantic.Skills[$modSkillId].Level -eq 77.5 -and
        $modEditedSemantic.Skills[$modSkillId].Accumulator -eq 18.75 -and
        $modEditedSemantic.Skills[$swordsId].Level -eq 25 -and
        $modEditedSemantic.Skills[$swordsId].Accumulator -eq 0 -and
        (Test-Bytes ([byte[]]$modPayload[0..($oldOuterLength - 1)]) ([byte[]]$modEditedPayload[0..($oldOuterLength - 1)]))) "Raw-profile set all lost mod skill values or changed outer profile data."
    # Unpatched vanilla Skills.Load rejects unknown IDs. Mod-owned raw data is
    # covered above; the actual game-loader fixture uses a vanilla skill only.
    $vanillaSeed = [Array]::CreateInstance($plugin.GetType("ServerManager.CharacterSemanticSkillState"), 1)
    $vanillaSeed.SetValue($semanticSkills[0], 0)
    [byte[]]$vanillaSeedBytes = $encodeSkills.Invoke($null, [object[]]@(,$vanillaSeed))
    $localSkills.Load([ZPackage]::new($vanillaSeedBytes))
    function Invoke-LocalSkill([string[]]$Action) {
        return $script:applySkill.Invoke($null, [object[]]@($script:localSkills, $Action))
    }
    function Get-LocalSkillBytes {
        $package = [ZPackage]::new()
        $script:localSkills.Save($package)
        return ,$package.GetArray()
    }
    $localSet = Invoke-LocalSkill @("skill", "set", "Swords", "82.5")
    Assert-True ($localSet.Success -and $localSet.Data["Swords"] -eq "82.5" -and
        $localSet.Data["Swords.xp"] -eq "0") ("Actual Skills.Load did not set the exact level and reset XP: " + $localSet.Message)
    [byte[]]$beforeRejectedSkills = Get-LocalSkillBytes
    foreach ($removedOperation in @("add", "reset", "ADD", "RESET")) {
        $action = if ($removedOperation -ieq "add") { @("skill", $removedOperation, "Swords", "1") } else { @("skill", $removedOperation, "Swords") }
        $rejected = Invoke-LocalSkill $action
        Assert-True (-not $rejected.Success -and $rejected.Code -eq "invalid_skill" -and
            (Test-Bytes $beforeRejectedSkills (Get-LocalSkillBytes))) "Removed local skill action mutated the actual skill dictionary."
    }
    Assert-True (-not (Invoke-LocalSkill @("skill", "set", "Swords", "NaN")).Success) "Local NaN input was accepted."
    foreach ($badValue in @("-1", "101", "Infinity")) {
        Assert-True (-not (Invoke-LocalSkill @("skill", "set", "Swords", $badValue)).Success) "Local out-of-range or nonfinite skill input was accepted."
    }
    Assert-True (Test-Bytes $beforeRejectedSkills (Get-LocalSkillBytes)) "Rejected local skill values changed data."
    $localGet = Invoke-LocalSkill @("skill", "get", "Swords")
    Assert-True ($localGet.Data["Swords"] -eq "82.5" -and $localGet.Message.Contains("Swords = 82.5 (XP 0)")) "Local rejected edit changed state or query omitted readable values."
    $allGet = Invoke-LocalSkill @("skill", "get")
    Assert-True ($allGet.Data["Swords"] -eq "82.5" -and $allGet.Data["Swords.xp"] -eq "0" -and
        (Test-Bytes $beforeRejectedSkills (Get-LocalSkillBytes))) "Implicit get all hid a skill or mutated data."
    $localZero = Invoke-LocalSkill @("skill", "set", "Swords", "0")
    $localDictionary = $skillsType.GetField("m_skillData", $allInstance).GetValue($localSkills)
    Assert-True ($localZero.Success -and $localZero.Data["Swords"] -eq "0" -and $localZero.Data["Swords.xp"] -eq "0" -and
        $localDictionary.ContainsKey([Enum]::ToObject($skillType, $swordsId))) "Local set zero removed the skill record."
    $localAll = Invoke-LocalSkill @("skill", "set", "all", "25")
    Assert-True ($localAll.Success -and $localAll.Data["Swords"] -eq "25" -and $localAll.Data["Swords.xp"] -eq "0" -and
        $localAll.Data["Axes"] -eq "25" -and $localAll.Data["Axes.xp"] -eq "0") "Actual Skills.Load set all failed to update trained and untrained skills."
    # Missing game definitions must be rejected before Skills.Load clears data.
    $missingSkillDefinition = @($definitions | Where-Object { $_.m_skill.ToString() -eq "Swords" })[0]
    $definitionsType.GetMethod("Remove").Invoke($definitions, [object[]]@($missingSkillDefinition.PSObject.BaseObject)) | Out-Null
    [byte[]]$beforeMissingDefinition = Get-LocalSkillBytes
    $missingDefinition = Invoke-LocalSkill @("skill", "set", "Swords", "50")
    Assert-True (-not $missingDefinition.Success -and $missingDefinition.Code -eq "invalid_skill" -and
        (Test-Bytes $beforeMissingDefinition (Get-LocalSkillBytes))) "Skill-definition validation ran after live mutation."
    $healthResult = $actionsType.GetMethod("HealthResult", [Reflection.BindingFlags]"Static,NonPublic")
    $noHealthChange = $healthResult.Invoke($null, [object[]]@($false, [single]5, [single]25, [single]25))
    Assert-True ($noHealthChange.Code -eq "no_health_change" -and $noHealthChange.Message.Contains("25 -> 25") -and $noHealthChange.Message.Contains("invulnerability")) "A no-op damage request claimed a health change."
    $healed = $healthResult.Invoke($null, [object[]]@($true, [single]10, [single]20, [single]25))
    Assert-True ($healed.Message.Contains("requested: 10") -and $healed.Message.Contains("20 -> 25")) "Health formatting lost requested amount versus actual delta."

    # Restore uses an independent temporary repository. Fixture writes deliberately
    # include malformed backups; the operation under test remains production code.
    $restoreOptions = New-Instance "CharacterStorageOptions"
    $restoreOptions.SaveRequestBurstCapacity = 100
    $restoreLayout = New-Instance "CharacterStorageLayout" @((Join-Path $testRoot "restore"))
    $restoreKeys = New-Instance "CharacterStorageKeyProvider" @($restoreLayout)
    $restoreCodec = New-Instance "CharacterEnvelopeCodec" @($restoreOptions)
    $restoreProfiles = New-Instance "ValheimPlayerProfileCodec" @($restoreOptions)
    $restoreValidator = New-Instance "CharacterSemanticRevisionValidator" @($restoreProfiles, $evaluator)
    $restoreRepository = New-Instance "CharacterRepository" @($restoreLayout, $restoreOptions, $restoreProfiles, $restoreValidator, $null)
    $restoreService = New-Instance "CharacterSnapshotService" @($restoreOptions, $resolver, $restoreKeys, $restoreCodec, $restoreProfiles, $restoreRepository, $restoreValidator)
    $vanillaCodecType = $plugin.GetType("ServerManager.VanillaCharacterFileCodec", $true)
    $encodeFch = $vanillaCodecType.GetMethod("Encode", [Reflection.BindingFlags]"Static,NonPublic")
    function Convert-ToRestoreFch([byte[]]$Payload) {
        $arguments = [object[]]::new(2)
        $arguments[0] = $Payload
        $arguments[1] = $script:restoreOptions.MaxPayloadBytes
        return ,[byte[]]$script:encodeFch.Invoke($null, $arguments)
    }
    $restoreIdentity = New-Instance "CharacterIdentity" @("steamworks:76561198000000002", "RestoreHero")
    $restoreKey = $restoreKeys.DeriveStorageKey($restoreIdentity)
    $restorePath = $restoreLayout.GetProfilePath($restoreKey)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($restorePath)) | Out-Null
    $restorePlayerId = [long]76561198012345678
    $createOriginEnvelope = $envelopeType.GetMethod("CreateWithOrigin", [Reflection.BindingFlags]"Static,NonPublic")
    function New-RestoreEnvelope([object]$Identity, [long]$Revision, [byte[]]$Payload, [bool]$Origin = $false, [int]$ProfileVersion = 46,
        [DateTime]$CreatedUtc = ([DateTime]::UtcNow.AddMinutes(-100 + $Revision))) {
        return $script:createOriginEnvelope.Invoke($null, [object[]]@(
            [Enum]::Parse($script:kindType, "Snapshot"), $Revision, ($Revision - 1),
            [Guid]::NewGuid(), $Identity, $CreatedUtc,
            $ProfileVersion, $Payload, $Origin))
    }
    function Write-RestoreBackup([object]$Envelope, [string]$Key = $script:restoreKey, [int]$Collision = 1) {
        $directory = $script:restoreLayout.GetAccountDirectory($Key)
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $localCreated = $Envelope.CreatedUtc.ToLocalTime()
        $suffix = if ($Collision -eq 1) { "" } else { "." + $Collision.ToString("D2", [Globalization.CultureInfo]::InvariantCulture) }
        $name = $Key + '.' + $localCreated.ToString("yyyy-MM-dd_HH-mm-ss", [Globalization.CultureInfo]::InvariantCulture) +
            $suffix + ".fch"
        $path = Join-Path $directory $name
        [IO.File]::WriteAllBytes($path, (Convert-ToRestoreFch $Envelope.GetPayloadCopy()))
        $record = $script:parseBackupFilename.Invoke($null, [object[]]@($name, $Key))
        return [pscustomobject]@{
            Id = (Get-Hidden $record "BackupId")
            Path = $path
            Envelope = $Envelope
            Record = $record
            Name = $name
        }
    }
    function Get-RestoreBackupFiles([string]$Key) {
        $pattern = '^' + [Regex]::Escape($Key) + '\.\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(?:\.\d{2,4})?\.fch$'
        return @(Get-ChildItem -LiteralPath $script:restoreLayout.GetAccountDirectory($Key) -File |
            Where-Object { $_.Name -cmatch $pattern })
    }
    function Invoke-Restore([string]$Id, [object]$Identity = $script:restoreIdentity) {
        return Invoke-Hidden $script:restoreService "RestoreAdminBackup" @($Identity.AccountId, $Identity.CharacterName, $Id)
    }
    function Get-RestoreBackups([int]$Page) {
        return Invoke-Hidden $script:restoreService "GetAdminBackups" @($script:restoreIdentity.AccountId, $script:restoreIdentity.CharacterName, $Page)
    }
    function Assert-RestoreRejected([string]$Id, [string]$Code, [string]$Reason, [object]$Identity = $script:restoreIdentity) {
        $key = $script:restoreKeys.DeriveStorageKey($Identity)
        $path = $script:restoreLayout.GetProfilePath($key)
        $existed = [IO.File]::Exists($path)
        [byte[]]$before = if ($existed) { [IO.File]::ReadAllBytes($path) } else { @() }
        $liveSnapshots = $script:restoreService.GetType().GetField("_liveSnapshots", $script:allInstance).GetValue($script:restoreService)
        $liveBefore = $liveSnapshots[$key]
        $registered = $script:restoreService.GetType().GetField("_registeredCheckpoints", $script:allInstance).GetValue($script:restoreService)
        $registeredBefore = $registered.Count
        $payloadBytesField = $script:restoreService.GetType().GetField("_retainedSnapshotPayloadBytes", $script:allInstance)
        $payloadBytesBefore = $payloadBytesField.GetValue($script:restoreService)
        $quarantine = $script:restoreService.GetType().GetField("_unconfirmedAdminRestores", $script:allInstance).GetValue($script:restoreService)
        $quarantinedBefore = $quarantine.Contains($key)
        $backupDirectory = $script:restoreLayout.GetAccountDirectory($key)
        $backupsBefore = if ([IO.Directory]::Exists($backupDirectory)) {
            @(Get-ChildItem -LiteralPath $backupDirectory -File | Sort-Object Name | ForEach-Object {
                $_.Name + ":" + [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName))
            }) -join "`n"
        } else { "" }
        $result = Invoke-Restore $Id $Identity
        Assert-True (-not $result.Success -and (-not $Code -or $result.Code -eq $Code)) (
            "$Reason Result: $($result.Code): $($result.Message)")
        Assert-True ([IO.File]::Exists($path) -eq $existed) "$Reason Changed primary existence."
        if ($existed) { Assert-True (Test-Bytes $before ([IO.File]::ReadAllBytes($path))) "$Reason Changed primary bytes." }
        Assert-True ([object]::ReferenceEquals($liveBefore, $liveSnapshots[$key]) -and
            $registered.Count -eq $registeredBefore -and
            $payloadBytesField.GetValue($script:restoreService) -eq $payloadBytesBefore) "$Reason Changed retained RAM or checkpoint ownership."
        $backupsAfter = if ([IO.Directory]::Exists($backupDirectory)) {
            @(Get-ChildItem -LiteralPath $backupDirectory -File | Sort-Object Name | ForEach-Object {
                $_.Name + ":" + [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName))
            }) -join "`n"
        } else { "" }
        Assert-True ($backupsBefore -ceq $backupsAfter) "$Reason Changed retained backup files."
        Assert-True ($quarantine.Contains($key) -eq $quarantinedBefore) "$Reason Changed restore quarantine despite unchanged primary."
    }

    # Exercise the production filename formatter/parser and real File.Replace.
    # Native .fch backups deliberately carry neither runtime revisions nor IDs;
    # the opaque command ID is derived from the canonical filename.
    $repositoryType = $plugin.GetType("ServerManager.CharacterRepository")
    $parseBackupFilename = $repositoryType.GetMethod("ParseAdminBackupFilename", [Reflection.BindingFlags]"Static,NonPublic")
    $formatBackupFilename = $repositoryType.GetMethod("GetAdminBackupFilename", [Reflection.BindingFlags]"Static,NonPublic")
    $preciseUtc = [DateTime]::Parse("2026-09-04T14:04:27.1667313Z", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
    $namingIdentity = New-Instance "CharacterIdentity" @("steamworks:76561198000000006", "FilenameHero")
    $namingKey = $restoreKeys.DeriveStorageKey($namingIdentity)
    $namingPath = $restoreLayout.GetProfilePath($namingKey)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($namingPath)) | Out-Null
    $namingDirectory = $restoreLayout.GetAccountDirectory($namingKey)
    $knownTimestamp = $preciseUtc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", [Globalization.CultureInfo]::InvariantCulture)
    $knownName = $namingKey + '.' + $knownTimestamp + ".fch"
    $knownRecord = $parseBackupFilename.Invoke($null, [object[]]@($knownName, $namingKey))
    $knownHash = [Security.Cryptography.SHA256]::Create()
    try {
        [byte[]]$knownDigest = $knownHash.ComputeHash([Text.UTF8Encoding]::new($false, $true).GetBytes($knownName))
    }
    finally { $knownHash.Dispose() }
    $knownId = -join ($knownDigest[0..15] | ForEach-Object { $_.ToString("x2", [Globalization.CultureInfo]::InvariantCulture) })
    $parsedTime = Get-Hidden $knownRecord "CreatedUtc"
    $expectedLocal = [DateTime]::ParseExact($knownTimestamp, "yyyy-MM-dd_HH-mm-ss", [Globalization.CultureInfo]::InvariantCulture)
    $expectedUtc = [DateTime]::SpecifyKind($expectedLocal, [DateTimeKind]::Local).ToUniversalTime()
    Assert-True ((Get-Hidden $knownRecord "BackupId") -ceq $knownId -and
        (Get-Hidden $knownRecord "CollisionNumber") -eq 1 -and
        (Get-Hidden $knownRecord "Filename") -ceq $knownName -and
        $parsedTime.Kind -eq [DateTimeKind]::Utc -and $parsedTime.Ticks -eq $expectedUtc.Ticks -and
        $formatBackupFilename.Invoke($null, @($knownRecord)) -ceq $knownName) "Canonical local-time backup metadata or derived opaque ID did not round-trip."
    foreach ($collision in @(2, 10, 9999)) {
        $collisionName = $namingKey + '.' + $knownTimestamp + "." + $collision.ToString("D2", [Globalization.CultureInfo]::InvariantCulture) + ".fch"
        $collisionRecord = $parseBackupFilename.Invoke($null, [object[]]@($collisionName, $namingKey))
        Assert-True ((Get-Hidden $collisionRecord "CollisionNumber") -eq $collision -and
            $formatBackupFilename.Invoke($null, @($collisionRecord)) -ceq $collisionName) "Canonical same-second collision suffix did not round-trip."
    }
    $legacyName = "00000000000000000008-00639241271006172842-0123456789abcdef0123456789abcdef.character.bak"
    foreach ($badName in @(
        "", $legacyName, "../$knownName", ($knownName + ".tmp"),
        $knownName.Replace($knownTimestamp, "2026-13-04_23-04-27"),
        ($namingKey + '.' + $knownTimestamp + ".01.fch"),
        ($namingKey + '.' + $knownTimestamp + ".1.fch"),
        ($namingKey + '.' + $knownTimestamp + ".0002.fch"),
        ($namingKey + '.' + $knownTimestamp + ".10000.fch"),
        ('Steam_76561198000000006_otherhero.' + $knownTimestamp + ".fch"),
        ($knownTimestamp + "." + $namingKey + ".fch"))) {
        Assert-Throws { $parseBackupFilename.Invoke($null, [object[]]@($badName, $namingKey)) } "*"
    }

    [byte[]]$namingPayload = New-ProfilePayload "FilenameHero" 1234567 "filename-precision"
    $namingPrevious = New-RestoreEnvelope $namingIdentity 8 $namingPayload $false 46 $preciseUtc
    [byte[]]$namingPreviousBytes = Convert-ToRestoreFch $namingPayload
    [IO.File]::WriteAllBytes($namingPath, $namingPreviousBytes)
    $namingNext = New-RestoreEnvelope $namingIdentity 9 $namingPayload $false 46 $preciseUtc.AddTicks(1)
    $namingWarning = Invoke-Hidden $restoreRepository "RestoreAdminSnapshot" @($namingIdentity, $namingKey, $namingPrevious, $namingNext)
    $createdBackups = @(Get-RestoreBackupFiles $namingKey)
    Assert-True ([string]::IsNullOrEmpty($namingWarning) -and $createdBackups.Count -eq 1 -and
        $createdBackups[0].Name -cmatch ('^' + [Regex]::Escape($namingKey) + '\.\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.fch$') -and
        (Test-Bytes $namingPreviousBytes ([IO.File]::ReadAllBytes($createdBackups[0].FullName)))) "Atomic replacement did not create an exact, readable backup of the previous primary."
    $createdRecord = $parseBackupFilename.Invoke($null, [object[]]@($createdBackups[0].Name, $namingKey))
    $createdId = Get-Hidden $createdRecord "BackupId"
    $readCreatedBackup = Invoke-Hidden $restoreRepository "ReadAdminBackup" @($namingIdentity, $namingKey, $createdId)
    Assert-True ($readCreatedBackup.Revision -eq 1 -and $readCreatedBackup.BaseRevision -eq 0 -and
        -not (Get-Hidden $readCreatedBackup "RequiresFreshLocalCharacter") -and
        (Test-Bytes $readCreatedBackup.GetPayloadCopy() $namingPayload)) "Readable native backup did not synthesize the process-local baseline or preserve payload bytes."
    [byte[]]$corruptBackupBytes = $namingPreviousBytes.Clone()
    $corruptBackupBytes[$corruptBackupBytes.Length - 1] = $corruptBackupBytes[$corruptBackupBytes.Length - 1] -bxor 1
    try {
        [IO.File]::WriteAllBytes($createdBackups[0].FullName, $corruptBackupBytes)
        Assert-Throws { Invoke-Hidden $restoreRepository "ReadAdminBackup" @($namingIdentity, $namingKey, $createdId) } "*SHA-512*"
    }
    finally { [IO.File]::WriteAllBytes($createdBackups[0].FullName, $namingPreviousBytes) }

    # Old files are neither migrated nor silently removed. Backup pre-scan is
    # fail-closed, so replacement must not touch the primary or any backup when
    # a noncanonical filename is present.
    $legacyPath = Join-Path $namingDirectory $legacyName
    [IO.File]::WriteAllBytes($legacyPath, $namingPreviousBytes)
    $originalMaxBackups = $restoreOptions.MaxBackups
    try {
        $restoreOptions.MaxBackups = 1
        Assert-Throws { Invoke-Hidden $restoreRepository "GetAdminBackups" @($namingIdentity, $namingKey) } "*unexpected file*"
        [byte[]]$namingLaterPayload = New-ProfilePayload "FilenameHero" 1234567 "legacy-pre-scan-rejected"
        $namingLater = New-RestoreEnvelope $namingIdentity 10 $namingLaterPayload $false 46 $preciseUtc.AddTicks(2)
        $replaceForNaming = $repositoryType.GetMethods($allInstance) |
            Where-Object { $_.Name -eq "ReplaceAtomicallyWithBackup" -and $_.GetParameters().Count -eq 3 }
        $replaceArguments = [object[]]::new(3)
        $replaceArguments[0] = $namingPath
        $replaceArguments[1] = $namingKey
        $replaceArguments[2] = Convert-ToRestoreFch $namingLater.GetPayloadCopy()
        [byte[]]$primaryBeforeLegacyPreScan = [IO.File]::ReadAllBytes($namingPath)
        $backupSetBeforeLegacyPreScan = @(Get-ChildItem -LiteralPath $namingDirectory -File | Sort-Object Name | ForEach-Object {
            $_.Name + ":" + [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName))
        }) -join "`n"
        Assert-Throws { $replaceForNaming.Invoke($restoreRepository, $replaceArguments) } "*unexpected file*"
        $backupSetAfterLegacyPreScan = @(Get-ChildItem -LiteralPath $namingDirectory -File | Sort-Object Name | ForEach-Object {
            $_.Name + ":" + [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName))
        }) -join "`n"
        Assert-True ((Test-Bytes $primaryBeforeLegacyPreScan ([IO.File]::ReadAllBytes($namingPath))) -and
            $backupSetBeforeLegacyPreScan -ceq $backupSetAfterLegacyPreScan -and
            @(Get-ChildItem -LiteralPath $namingDirectory -File).Count -eq 3) `
            "A noncanonical backup did not fail before primary replacement, or mutated the backup set."
        [IO.File]::Delete($legacyPath)
        $invalidPath = Join-Path $namingDirectory "invalid.character.bak"
        [IO.File]::WriteAllBytes($invalidPath, [byte[]]@(1, 2, 3))
        try {
            $beforePrune = @(Get-ChildItem -LiteralPath $namingDirectory -File | Sort-Object Name | ForEach-Object {
                $_.Name + ":" + [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName))
            }) -join "`n"
            Assert-Throws { Invoke-Hidden $restoreRepository "PruneBackups" @($namingKey, $null) } "*unexpected file*"
            $afterPrune = @(Get-ChildItem -LiteralPath $namingDirectory -File | Sort-Object Name | ForEach-Object {
                $_.Name + ":" + [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName))
            }) -join "`n"
            Assert-True ($beforePrune -ceq $afterPrune) "Malformed backup detection partially pruned the directory."
        }
        finally { [IO.File]::Delete($invalidPath) }

        # Native backups sort by local filename timestamp, then numeric
        # same-second collision. Runtime revisions are not persisted.
        $rotationIdentity = New-Instance "CharacterIdentity" @("steamworks:76561198000000007", "RotationHero")
        $rotationKey = $restoreKeys.DeriveStorageKey($rotationIdentity)
        $rotationDirectory = $restoreLayout.GetAccountDirectory($rotationKey)
        [byte[]]$rotationPayload = New-ProfilePayload "RotationHero" 2345678 "revision-retention"
        $rotationOld = Write-RestoreBackup (New-RestoreEnvelope $rotationIdentity 20 $rotationPayload $false 46 $preciseUtc.AddDays(-2)) $rotationKey
        $rotationMiddle = Write-RestoreBackup (New-RestoreEnvelope $rotationIdentity 2 $rotationPayload $false 46 $preciseUtc) $rotationKey
        $rotationMiddleCollision = Write-RestoreBackup (New-RestoreEnvelope $rotationIdentity 999 $rotationPayload $false 46 $preciseUtc) $rotationKey 2
        $rotationNewest = Write-RestoreBackup (New-RestoreEnvelope $rotationIdentity 1 $rotationPayload $false 46 $preciseUtc.AddDays(2)) $rotationKey
        $siblingRotationIdentity = New-Instance "CharacterIdentity" @($rotationIdentity.AccountId, "RotationHeroSibling")
        $siblingRotationKey = $restoreKeys.DeriveStorageKey($siblingRotationIdentity)
        [byte[]]$siblingRotationPayload = New-ProfilePayload "RotationHeroSibling" 2345679 "same-account-preserve"
        $siblingRotationPath = $restoreLayout.GetProfilePath($siblingRotationKey)
        [IO.File]::WriteAllBytes($siblingRotationPath, (Convert-ToRestoreFch $siblingRotationPayload))
        $siblingRotationBackups = @(1..3 | ForEach-Object {
            Write-RestoreBackup (New-RestoreEnvelope $siblingRotationIdentity $_ $siblingRotationPayload $false 46 $preciseUtc.AddDays(-$_)) $siblingRotationKey
        })
        Assert-True ($restoreLayout.GetAccountDirectory($siblingRotationKey) -ceq $rotationDirectory) "Same-account retention fixtures do not share a folder."
        $rotationListed = Invoke-Hidden $restoreRepository "GetAdminBackups" @($rotationIdentity, $rotationKey)
        Assert-True ((Get-Hidden $rotationListed[0] "BackupId") -ceq $rotationNewest.Id -and
            (Get-Hidden $rotationListed[1] "BackupId") -ceq $rotationMiddleCollision.Id -and
            (Get-Hidden $rotationListed[2] "BackupId") -ceq $rotationMiddle.Id -and
            (Get-Hidden $rotationListed[3] "BackupId") -ceq $rotationOld.Id) "Backup listing did not use local timestamp and numeric collision order."
        $restoreOptions.MaxBackups = 2
        Invoke-Hidden $restoreRepository "PruneBackups" @($rotationKey, $null) | Out-Null
        Assert-True ((Test-Path -LiteralPath $rotationNewest.Path) -and (Test-Path -LiteralPath $rotationMiddleCollision.Path) -and
            -not (Test-Path -LiteralPath $rotationOld.Path) -and -not (Test-Path -LiteralPath $rotationMiddle.Path)) "Retention did not keep the newest timestamp/collision backups."
        $preservedOld = Write-RestoreBackup (New-RestoreEnvelope $rotationIdentity 5000 $rotationPayload $false 46 $preciseUtc.AddDays(-3)) $rotationKey
        Invoke-Hidden $restoreRepository "PruneBackups" @($rotationKey, $preservedOld.Path) | Out-Null
        Assert-True ((Test-Path -LiteralPath $preservedOld.Path) -and (Test-Path -LiteralPath $rotationNewest.Path) -and
            -not (Test-Path -LiteralPath $rotationMiddleCollision.Path)) "Retention removed the explicitly protected previous primary."
        Assert-True ((Invoke-Hidden $restoreRepository "GetAdminBackups" @($siblingRotationIdentity, $siblingRotationKey)).Count -eq 3 -and
            (Test-Bytes ([IO.File]::ReadAllBytes($siblingRotationPath)) (Convert-ToRestoreFch $siblingRotationPayload))) "One character's retention pruned another character's backups or primary in the same account folder."
        foreach ($siblingBackup in $siblingRotationBackups) {
            Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($siblingBackup.Path)) (Convert-ToRestoreFch $siblingRotationPayload)) "Same-account backup bytes changed during another character's retention."
        }

        # Rotation must remain monotonic if the server clock moves backwards.
        # Existing collision gaps are not reused, and the rollback copy made by
        # the current replacement survives the same prune operation.
        $clockIdentity = New-Instance "CharacterIdentity" @("steamworks:76561198000000008", "ClockRollbackHero")
        $clockKey = $restoreKeys.DeriveStorageKey($clockIdentity)
        $clockPath = $restoreLayout.GetProfilePath($clockKey)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($clockPath)) | Out-Null
        [byte[]]$clockPreviousPayload = New-ProfilePayload "ClockRollbackHero" 3456789 "clock-previous"
        [byte[]]$clockNextPayload = New-ProfilePayload "ClockRollbackHero" 3456789 "clock-next"
        [byte[]]$clockPreviousFch = Convert-ToRestoreFch $clockPreviousPayload
        [IO.File]::WriteAllBytes($clockPath, $clockPreviousFch)
        $futureUtc = [DateTime]::SpecifyKind(
            [DateTime]::new(2099, 1, 2, 3, 4, 5),
            [DateTimeKind]::Utc)
        $clockFirst = Write-RestoreBackup (
            New-RestoreEnvelope $clockIdentity 1 $clockPreviousPayload $false 46 $futureUtc) $clockKey
        $clockSecondPath = Join-Path $restoreLayout.GetAccountDirectory($clockKey) ($clockFirst.Name.Substring(0, $clockFirst.Name.Length - 4) + '.02.fch')
        $replaceForNaming.Invoke($restoreRepository, [object[]]@($clockPath, $clockKey, $clockPreviousFch)) | Out-Null
        Assert-True ((Test-Path -LiteralPath $clockSecondPath) -and
            (Test-Bytes ([IO.File]::ReadAllBytes($clockSecondPath)) $clockPreviousFch)) "The second same-second backup did not use the .02 suffix beside the primary."
        [IO.File]::Delete($clockSecondPath)
        $clockThird = Write-RestoreBackup (
            New-RestoreEnvelope $clockIdentity 3 $clockPreviousPayload $false 46 $futureUtc) $clockKey 3
        $clockTimestamp = $clockThird.Name.Substring(($clockKey + '.').Length, "yyyy-MM-dd_HH-mm-ss".Length)
        $clockFourthName = $clockKey + '.' + $clockTimestamp + ".04.fch"
        $clockFourthPath = Join-Path ($restoreLayout.GetAccountDirectory($clockKey)) $clockFourthName
        $clockReplaceArguments = [object[]]@(
            $clockPath,
            $clockKey,
            (Convert-ToRestoreFch $clockNextPayload))
        $clockWarning = $replaceForNaming.Invoke(
            $restoreRepository,
            $clockReplaceArguments)
        Assert-True (
            [string]::IsNullOrEmpty($clockWarning) -and
            -not (Test-Path -LiteralPath $clockFirst.Path) -and
            (Test-Path -LiteralPath $clockThird.Path) -and
            (Test-Path -LiteralPath $clockFourthPath) -and
            (Test-Bytes $clockPreviousFch ([IO.File]::ReadAllBytes($clockFourthPath))) -and
            (Test-Bytes (Convert-ToRestoreFch $clockNextPayload) ([IO.File]::ReadAllBytes($clockPath)))) `
            "Clock rollback reused a collision gap, regressed backup time, or pruned the just-created rollback backup."

        # Max retention plus one is a recoverable crash/prune residue. The next
        # replacement must inspect it, prune before adding another rollback
        # copy, and finish at the configured bound instead of deadlocking all
        # future list/restore/commit operations.
        $overflowIdentity = New-Instance "CharacterIdentity" @("steamworks:76561198000000009", "OverflowHero")
        $overflowKey = $restoreKeys.DeriveStorageKey($overflowIdentity)
        $overflowPath = $restoreLayout.GetProfilePath($overflowKey)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($overflowPath)) | Out-Null
        [byte[]]$overflowOldPayload = New-ProfilePayload "OverflowHero" 4567890 "overflow-old"
        [byte[]]$overflowNewPayload = New-ProfilePayload "OverflowHero" 4567890 "overflow-new"
        [byte[]]$overflowOldFch = Convert-ToRestoreFch $overflowOldPayload
        [IO.File]::WriteAllBytes($overflowPath, $overflowOldFch)
        $overflowUtc = [DateTime]::SpecifyKind(
            [DateTime]::new(2098, 1, 2, 3, 4, 5),
            [DateTimeKind]::Utc)
        for ($collision = 1; $collision -le 1001; ++$collision) {
            Write-RestoreBackup (
                New-RestoreEnvelope $overflowIdentity $collision $overflowOldPayload $false 46 $overflowUtc) `
                $overflowKey $collision | Out-Null
        }
        $restoreOptions.MaxBackups = 1000
        Assert-True ((Invoke-Hidden $restoreRepository "GetAdminBackups" @($overflowIdentity, $overflowKey)).Count -eq 1001) `
            "The maximum-retention plus one recovery set exceeded the bounded inspection window."
        $overflowWarning = $replaceForNaming.Invoke(
            $restoreRepository,
            [object[]]@($overflowPath, $overflowKey, (Convert-ToRestoreFch $overflowNewPayload)))
        $overflowFiles = @(Get-RestoreBackupFiles $overflowKey)
        Assert-True ([string]::IsNullOrEmpty($overflowWarning) -and
            $overflowFiles.Count -eq 1000 -and
            (Test-Bytes (Convert-ToRestoreFch $overflowNewPayload) ([IO.File]::ReadAllBytes($overflowPath))) -and
            @($overflowFiles | Where-Object {
                Test-Bytes $overflowOldFch ([IO.File]::ReadAllBytes($_.FullName))
            }).Count -eq 1000) `
            "A recoverable one-backup overflow blocked replacement or escaped retention repair."
    }
    finally { $restoreOptions.MaxBackups = $originalMaxBackups }

    [byte[]]$restoreCurrentPayload = New-ProfilePayload "RestoreHero" $restorePlayerId "current-marker"
    $restoreCurrent = New-RestoreEnvelope $restoreIdentity 20 $restoreCurrentPayload
    [IO.File]::WriteAllBytes($restorePath, (Convert-ToRestoreFch $restoreCurrentPayload))
    $restoreBackups = @()
    for ($index = 1; $index -le 12; ++$index) {
        [byte[]]$backupPayload = New-ProfilePayload "RestoreHero" $restorePlayerId "backup-marker-$index"
        $restoreBackups += Write-RestoreBackup (New-RestoreEnvelope $restoreIdentity $index $backupPayload)
    }
    $selectedBackup = $restoreBackups[11]
    $pageOne = Get-RestoreBackups 1
    $pageTwo = Get-RestoreBackups 2
    Assert-True ($pageOne.Success -and $pageOne.Code -eq "character_backups" -and
        $pageOne.Data["page"] -eq "1" -and $pageOne.Data["pages"] -eq "2" -and
        $pageOne.Data["count"] -eq "12" -and $pageOne.Data["backup_0"] -eq $selectedBackup.Id -and
        @($pageOne.Data.Keys | Where-Object { $_ -match '^backup_\d+$' }).Count -eq 10 -and
        @($pageTwo.Data.Keys | Where-Object { $_ -match '^backup_\d+$' }).Count -eq 2) "Backup pagination was not bounded to 10 entries."
    $listedIds = @($pageOne.Data.GetEnumerator(); $pageTwo.Data.GetEnumerator()) |
        Where-Object { $_.Key -match '^backup_\d+$' } | ForEach-Object { $_.Value }
    Assert-True (@($listedIds | Select-Object -Unique).Count -eq 12 -and
        @($listedIds | Where-Object { $_ -cnotmatch '^[0-9a-f]{32}$' }).Count -eq 0) "Listing lost stable opaque IDs or duplicated a backup across pages."
    Assert-True ((Get-RestoreBackups 0).Code -eq "invalid_page" -and (Get-RestoreBackups 3).Code -eq "backup_page_not_found") "Invalid backup pages were accepted."
    $missingRestoreIdentity = New-Instance "CharacterIdentity" @("steamworks:76561198000000005", "MissingRestoreHero")
    Assert-RestoreRejected $selectedBackup.Id "character_not_found" "Restore created a missing primary." $missingRestoreIdentity
    Assert-RestoreRejected ([Guid]::NewGuid().ToString("N")) "" "An unknown backup ID was accepted."
    foreach ($badId in @("../outside", "..\outside", $selectedBackup.Path, "ABCDEF0123456789ABCDEF0123456789", "latest")) {
        Assert-RestoreRejected $badId "invalid_backup_id" "An unsafe or noncanonical backup ID was accepted."
    }
    $otherRestoreIdentity = New-Instance "CharacterIdentity" @($restoreIdentity.AccountId, "OtherHero")
    $otherRestoreKey = $restoreKeys.DeriveStorageKey($otherRestoreIdentity)
    $otherRestorePath = $restoreLayout.GetProfilePath($otherRestoreKey)
    Assert-True ([IO.Path]::GetDirectoryName($otherRestorePath) -ceq [IO.Path]::GetDirectoryName($restorePath)) "Same-account restore fixture did not share the Steam directory."
    [byte[]]$otherRestorePayload = New-ProfilePayload "OtherHero" 99887766 "other-character-marker"
    $otherRestoreEnvelope = New-RestoreEnvelope $otherRestoreIdentity 3 $otherRestorePayload
    [IO.File]::WriteAllBytes($otherRestorePath, (Convert-ToRestoreFch $otherRestorePayload))
    [byte[]]$otherRestoreBefore = [IO.File]::ReadAllBytes($otherRestorePath)
    $otherBackup = Write-RestoreBackup (New-RestoreEnvelope $otherRestoreIdentity 2 $otherRestorePayload) $otherRestoreKey
    $differentAccountIdentity = New-Instance "CharacterIdentity" @("steamworks:76561198000000003", "OtherHero")
    $differentAccountKey = $restoreKeys.DeriveStorageKey($differentAccountIdentity)
    $differentAccountPath = $restoreLayout.GetProfilePath($differentAccountKey)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($differentAccountPath)) | Out-Null
    [byte[]]$differentAccountPayload = New-ProfilePayload "OtherHero" 99887767 "different-account-marker"
    [IO.File]::WriteAllBytes($differentAccountPath, (Convert-ToRestoreFch $differentAccountPayload))
    $differentAccountBackup = Write-RestoreBackup (New-RestoreEnvelope $differentAccountIdentity 2 $differentAccountPayload) $differentAccountKey
    Assert-RestoreRejected $differentAccountBackup.Id "" "A different account's backup ID was accepted."
    Assert-RestoreRejected $otherBackup.Id "" "A different character's backup ID was accepted."
    $malformedBackup = Write-RestoreBackup (New-RestoreEnvelope $restoreIdentity 13 $restoreCurrentPayload)
    [IO.File]::WriteAllBytes($malformedBackup.Path, [byte[]]@(1, 2, 3))
    Assert-RestoreRejected $malformedBackup.Id "" "A corrupt backup was restored."
    [IO.File]::Delete($malformedBackup.Path)
    $wrongIdentityBackup = Write-RestoreBackup (New-RestoreEnvelope $otherRestoreIdentity 13 $otherRestorePayload)
    Assert-RestoreRejected $wrongIdentityBackup.Id "" "A misplaced wrong-identity backup was restored."
    [IO.File]::Delete($wrongIdentityBackup.Path)
    [byte[]]$wrongPlayerPayload = New-ProfilePayload "RestoreHero" 11223344 "wrong-player-id"
    $wrongPlayerBackup = Write-RestoreBackup (New-RestoreEnvelope $restoreIdentity 13 $wrongPlayerPayload)
    Assert-RestoreRejected $wrongPlayerBackup.Id "player_id_mismatch" "A different player ID was restored."
    [IO.File]::Delete($wrongPlayerBackup.Path)
    # Native .fch stores the PlayerProfile version in its raw payload rather
    # than in a ServerManager envelope. Unsupported raw versions fail closed.
    [byte[]]$wrongVersionPayload = $restoreCurrentPayload.Clone()
    [Array]::Copy([BitConverter]::GetBytes([int]44), 0, $wrongVersionPayload, 0, 4)
    $wrongVersionBackup = Write-RestoreBackup (New-RestoreEnvelope $restoreIdentity 13 $wrongVersionPayload $false 44)
    try {
        Assert-RestoreRejected $wrongVersionBackup.Id "restore_failed" "A backup with an incompatible raw PlayerProfile version was restored."
    }
    finally { [IO.File]::Delete($wrongVersionBackup.Path) }
    [byte[]]$beforeWrongVersionPrimary = [IO.File]::ReadAllBytes($restorePath)
    try {
        [IO.File]::WriteAllBytes($restorePath, (Convert-ToRestoreFch $wrongVersionPayload))
        Assert-RestoreRejected $selectedBackup.Id "restore_failed" "An incompatible current primary's raw version was ignored."
    }
    finally { [IO.File]::WriteAllBytes($restorePath, $beforeWrongVersionPrimary) }
    $restoreProfile = $decodeProfile.Invoke($restoreProfiles, [object[]]@($restoreCurrentPayload, $null, [FileHelpers+FileSource]::Local))
    [byte[]]$restoreInner = $playerDataField.GetValue($restoreProfile)
    $emptyPlayerOffset = $restoreCurrentPayload.Length - $restoreInner.Length - 5
    [byte[]]$emptyPlayerPayload = [byte[]]::new($emptyPlayerOffset + 1)
    [Array]::Copy($restoreCurrentPayload, $emptyPlayerPayload, $emptyPlayerOffset)
    $emptyPlayerBackup = Write-RestoreBackup (New-RestoreEnvelope $restoreIdentity 13 $emptyPlayerPayload)
    Assert-RestoreRejected $emptyPlayerBackup.Id "backup_not_materialized" "An unmaterialized backup was restored."
    [IO.File]::Delete($emptyPlayerBackup.Path)
    [byte[]]$forbiddenPayload = New-ProfilePayload "RestoreHero" $restorePlayerId "forbidden" 25 25 50 50 0 0 "ForbiddenSword"
    $forbiddenBackup = Write-RestoreBackup (New-RestoreEnvelope $restoreIdentity 13 $forbiddenPayload)
    Assert-RestoreRejected $forbiddenBackup.Id "policy_rejected" "Restore bypassed absolute item policy."
    [IO.File]::Delete($forbiddenBackup.Path)

    # Deterministic Windows failures exercise pre-write and attempted-replace
    # handling without racing file watchers or injecting production test hooks.
    $unconfirmed = $restoreService.GetType().GetField("_unconfirmedAdminRestores", $allInstance).GetValue($restoreService)
    [byte[]]$beforeLockedRestore = [IO.File]::ReadAllBytes($restorePath)
    $primaryLock = [IO.File]::Open($restorePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $lockedRestore = Invoke-Restore $selectedBackup.Id
        Assert-True (-not $lockedRestore.Success -and $lockedRestore.Code -eq "restore_failed" -and
            -not $unconfirmed.Contains($restoreKey)) "An exclusive primary lock was not a safe pre-write restore failure."
    }
    finally { $primaryLock.Dispose() }
    Assert-True (Test-Bytes $beforeLockedRestore ([IO.File]::ReadAllBytes($restorePath))) "The locked-primary restore changed disk bytes."
    $originalAttributes = [IO.File]::GetAttributes($restorePath)
    try {
        [IO.File]::SetAttributes($restorePath, $originalAttributes -bor [IO.FileAttributes]::ReadOnly)
        Assert-RestoreRejected $selectedBackup.Id "restore_failed" "A read-only primary did not safely reject File.Replace."
        Assert-True (-not $unconfirmed.Contains($restoreKey)) "A failed replacement with verified original primary was incorrectly quarantined."
    }
    finally { [IO.File]::SetAttributes($restorePath, $originalAttributes) }
    Assert-True (@(Get-ChildItem -LiteralPath $restoreLayout.GetAccountDirectory($restoreKey) -File -Force | Where-Object {
        $_.Name.StartsWith("." + $restoreKey + ".", [StringComparison]::Ordinal)
    }).Count -eq 0) "Failed restore left a temporary primary behind."

    $connectingIdentity = New-Instance "CharacterIdentity" @("steamworks:76561198000000004", "ConnectingHero")
    $connecting = Invoke-Hidden $restoreService "OpenOrCreateLocalHostSession" @($connectingIdentity)
    Assert-RestoreRejected $selectedBackup.Id "character_busy" "A connecting lease did not block restore." $connectingIdentity
    Invoke-Hidden $restoreService "CloseLocalHostSession" @($connecting.Snapshot.SessionId) | Out-Null
    $restoreOpened = Invoke-Hidden $restoreService "OpenOrCreateLocalHostSession" @($restoreIdentity)
    Assert-RestoreRejected $selectedBackup.Id "character_busy" "An active session did not block restore."
    $restoreRequest = New-Request $restoreIdentity $restoreOpened.Snapshot.SessionId 2 1 $restoreCurrentPayload
    Assert-True (Invoke-Hidden $restoreService "HandleLocalHostSaveRequest" @($restoreOpened.Snapshot.SessionId, $restoreRequest)).Accepted "Restore fixture live update failed."
    Invoke-Hidden $restoreService "CloseLocalHostSession" @($restoreOpened.Snapshot.SessionId) | Out-Null
    Assert-RestoreRejected $selectedBackup.Id "shadow_pending" "Retained offline RAM did not block restore."
    $restoreCheckpoint = Invoke-Hidden $restoreService "BeginCheckpoint"
    $restoreEntries = Get-Hidden $restoreCheckpoint "Entries"
    Assert-RestoreRejected $selectedBackup.Id "checkpoint_pending" "A registered checkpoint did not block restore."
    foreach ($entry in $restoreEntries) { Invoke-Hidden $restoreService "CommitCheckpointEntry" @($restoreCheckpoint, $entry) | Out-Null }
    $skillWindows = $restoreService.GetType().GetField("_skillObservationWindows", $allInstance).GetValue($restoreService)
    Assert-True ($skillWindows.ContainsKey($restoreKey)) "Restore fixture did not retain a skill observation baseline."
    [byte[]]$primaryBeforeRestore = [IO.File]::ReadAllBytes($restorePath)
    $restoreResult = Invoke-Restore $selectedBackup.Id
    Assert-True ($restoreResult.Success -and $restoreResult.Code -in @("restored", "restored_with_warning") -and
        -not $restoreResult.Data.ContainsKey("previous_revision") -and
        -not $restoreResult.Data.ContainsKey("new_revision") -and
        $restoreResult.Message.Contains("new process-local revision baseline")) (
        "Offline restore failed or reported a fictitious disk revision: $($restoreResult.Code): $($restoreResult.Message)")
    $restoredStored = (Invoke-Hidden $restoreRepository "Load" @($restoreIdentity, $restoreKey)).Envelope
    Assert-True ($restoredStored.Revision -eq 1 -and $restoredStored.BaseRevision -eq 0 -and
        -not (Get-Hidden $restoredStored "RequiresFreshLocalCharacter") -and
        (Test-Bytes $restoredStored.GetPayloadCopy() $selectedBackup.Envelope.GetPayloadCopy())) "Restore did not durably preserve the exact old payload under a fresh process-local baseline."
    Assert-True (-not $skillWindows.ContainsKey($restoreKey)) "Restore retained the pre-restore skill observation baseline."
    $previousPrimaryBackups = @(Get-RestoreBackupFiles $restoreKey |
        Where-Object { Test-Bytes $primaryBeforeRestore ([IO.File]::ReadAllBytes($_.FullName)) })
    Assert-True ($previousPrimaryBackups.Count -eq 1 -and (Test-Path -LiteralPath $selectedBackup.Path)) "Restore lost the previous primary or consumed the selected backup."
    Assert-True (Test-Bytes $otherRestoreBefore ([IO.File]::ReadAllBytes($otherRestorePath))) "Restore changed an unrelated character."
    Assert-True ((Test-Bytes ([IO.File]::ReadAllBytes($otherBackup.Path)) (Convert-ToRestoreFch $otherRestorePayload)) -and
        (Test-Bytes ([IO.File]::ReadAllBytes($differentAccountPath)) (Convert-ToRestoreFch $differentAccountPayload)) -and
        (Test-Bytes ([IO.File]::ReadAllBytes($differentAccountBackup.Path)) (Convert-ToRestoreFch $differentAccountPayload))) "Restore changed a same-account backup or a different account's files."
    $afterRestoreBatch = Invoke-Hidden $restoreService "BeginCheckpoint"
    Assert-True ((Get-Hidden $afterRestoreBatch "Entries").Count -eq 0) "Restore left a stale RAM shadow or checkpoint that could overwrite it."
    Invoke-Hidden $restoreService "DiscardCheckpoint" @($afterRestoreBatch) | Out-Null
    $coldRestored = Invoke-Hidden $restoreService "OpenOrCreateLocalHostSession" @($restoreIdentity)
    Assert-True ($coldRestored.Snapshot.Revision -eq 1 -and
        (Test-Bytes $coldRestored.Snapshot.GetPayloadCopy() $selectedBackup.Envelope.GetPayloadCopy())) "Cold login did not load the restored durable profile."
    Invoke-Hidden $restoreService "CloseLocalHostSession" @($coldRestored.Snapshot.SessionId) | Out-Null

    # Simulate the quarantine state, not a successful write: an ambiguous disk
    # outcome must block reconnects and all later offline edits until restart.
    $unconfirmed = $restoreService.GetType().GetField("_unconfirmedAdminRestores", $allInstance).GetValue($restoreService)
    $unconfirmed.Add($otherRestoreKey) | Out-Null
    Assert-RestoreRejected $otherBackup.Id "restore_unconfirmed" "A quarantined character accepted a second restore." $otherRestoreIdentity
    $quarantinedEdit = Invoke-Hidden $restoreService "ApplyOfflineSkillAdmin" @($otherRestoreIdentity.AccountId, $otherRestoreIdentity.CharacterName, "set", "Swords", [single]12)
    Assert-True (-not $quarantinedEdit.Success -and $quarantinedEdit.Code -eq "restore_unconfirmed") "An offline edit bypassed restore quarantine."
    Assert-Throws { Invoke-Hidden $restoreService "OpenOrCreateLocalHostSession" @($otherRestoreIdentity) } "*unavailable until server restart*"
    Assert-True (Test-Bytes $otherRestoreBefore ([IO.File]::ReadAllBytes($otherRestorePath))) "Quarantine rejection changed the primary."

    # Static IL boundaries complement headless data tests: no dropped item or fake Player path.
    $definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
    try {
        $actions = $definition.MainModule.Types | Where-Object FullName -eq "ServerManager.CharacterAdminActions"
        $give = $actions.Methods | Where-Object Name -eq "GiveItem"
        $calls = @($give.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.FullName })
        Assert-True (($calls -join "`n") -notmatch "Instantiate|DropItem|Player::.ctor") "Item grant instantiates/drops a world object or fake Player."
        Assert-True (($calls -join "`n") -match "Inventory::AddItem\(ItemDrop/ItemData\)") "Grant lost its preflighted ItemData AddItem path."
        $apply = $actions.Methods | Where-Object Name -eq "Apply"
        Assert-True (@($apply.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq "TeleportTo" }).Count -eq 1) "Teleport no longer calls the verified public player API."
        $offline = ($definition.MainModule.Types | Where-Object FullName -eq "ServerManager.CharacterSnapshotService").Methods | Where-Object Name -eq "ApplyOfflineSkillAdmin"
        $offlineCalls = @($offline.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
        Assert-True ($offlineCalls -notcontains "PersistCheckpointEntry" -and $offlineCalls -notcontains "PrepareInitialSnapshot" -and $offlineCalls -contains "EvaluateAuthoritative") "Offline edit writes disk, creates profiles, or bypasses absolute validation."
        $restoreMethod = ($definition.MainModule.Types | Where-Object FullName -eq "ServerManager.CharacterSnapshotService").Methods | Where-Object Name -eq "RestoreAdminBackup"
        $restoreCalls = @($restoreMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
        Assert-True ($restoreCalls -contains "RestoreAdminSnapshot" -and $restoreCalls -contains "EvaluateAuthoritative" -and
            $restoreCalls -notcontains "PersistCheckpointEntry" -and $restoreCalls -notcontains "RemoveLiveSnapshotLocked") "Restore bypassed policy, reused checkpoint persistence, or discarded an authoritative RAM snapshot."
        Assert-True (@($restoreMethod.Body.ExceptionHandlers | Where-Object {
            $null -ne $_.CatchType -and $_.CatchType.FullName -eq "ServerManager.CharacterRestoreUnconfirmedException"
        }).Count -eq 1 -and @($restoreMethod.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq "Add" -and
            $_.Operand.DeclaringType.FullName -like "System.Collections.Generic.HashSet*"
        }).Count -ge 1) "The ambiguous restore result no longer has a per-character quarantine path."
    }
    finally { $definition.Dispose() }
    Write-Host "Character admin smoke passed (skill splice, offline RAM/checkpoint/host safety, readable backup names/precision/retention, backup paging/restore/quarantine, bounded-action IL)."
}
finally {
    if ($null -ne $restoreService) { $restoreService.Dispose() }
    elseif ($null -ne $restoreKeys) { $restoreKeys.Dispose() }
    if ($null -ne $service) { $service.Dispose() }
    elseif ($null -ne $keys) { $keys.Dispose() }
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($fixtureAssemblyResolver)
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-True ($resolvedRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolvedRoot).StartsWith("smadmin-", [StringComparison]::Ordinal)) "Unsafe test cleanup target."
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
