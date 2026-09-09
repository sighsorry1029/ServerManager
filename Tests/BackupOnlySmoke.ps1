param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim",
    [switch]$FixtureOnly
)

$ErrorActionPreference = 'Stop'
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    ++$script:assertions
    if (-not $Condition) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern = '*') {
    try { & $Action | Out-Null }
    catch {
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        Assert-True ($failure.Message -like $Pattern) "Expected '$Pattern', got '$($failure.Message)'."
        return
    }
    throw "Expected failure '$Pattern'."
}
$allInstance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$allStatic = [Reflection.BindingFlags]'Static,Public,NonPublic'
function Invoke-Hidden([object]$Instance, [string]$Name, [object[]]$Arguments = @()) {
    $method = $Instance.GetType().GetMethods($script:allInstance) |
        Where-Object { $_.Name -eq $Name -and $_.GetParameters().Count -eq $Arguments.Count } |
        Select-Object -First 1
    Assert-True ($null -ne $method) "Missing method $Name."
    $converted = [object[]]::new($Arguments.Count)
    $parameters = $method.GetParameters()
    for ($index = 0; $index -lt $Arguments.Count; ++$index) {
        $type = $parameters[$index].ParameterType
        if ($type.IsByRef) { $type = $type.GetElementType() }
        if ($null -ne $Arguments[$index]) {
            $converted[$index] = [Management.Automation.LanguagePrimitives]::ConvertTo($Arguments[$index], $type)
        }
    }
    $result = $method.Invoke($Instance, $converted)
    for ($index = 0; $index -lt $Arguments.Count; ++$index) {
        if ($parameters[$index].ParameterType.IsByRef) { $Arguments[$index] = $converted[$index] }
    }
    return ,$result
}
function New-Instance([string]$Name, [object[]]$Arguments = @()) {
    $type = $script:plugin.GetType('ServerManager.' + $Name, $true)
    $ctor = $type.GetConstructors($script:allInstance) |
        Where-Object { $_.GetParameters().Count -eq $Arguments.Count } | Select-Object -First 1
    Assert-True ($null -ne $ctor) "Missing constructor $Name."
    $converted = [object[]]::new($Arguments.Count)
    $parameters = $ctor.GetParameters()
    for ($index = 0; $index -lt $Arguments.Count; ++$index) {
        $converted[$index] = [Management.Automation.LanguagePrimitives]::ConvertTo(
            $Arguments[$index], $parameters[$index].ParameterType)
    }
    return $ctor.Invoke($converted)
}
function Get-Hidden([object]$Instance, [string]$Name) {
    return ,($Instance.GetType().GetProperty($Name, $script:allInstance).GetValue($Instance))
}
function Test-Bytes([byte[]]$Left, [byte[]]$Right) {
    return [Convert]::ToBase64String($Left) -ceq [Convert]::ToBase64String($Right)
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$binaryRoot = Join-Path $projectRoot "bin\$Configuration"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
$pluginPath = Join-Path $binaryRoot 'ServerManager.dll'
Assert-True (Test-Path -LiteralPath $pluginPath) 'Build ServerManager first.'
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GamePath 'BepInEx\core\BepInEx.dll'))) | Out-Null
foreach ($name in @('netstandard.dll', 'com.rlabrecque.steamworks.net.dll', 'Splatform.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($managedRoot)
$resolver.AddSearchDirectory((Join-Path $GamePath 'BepInEx\core'))
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
function Clear-Body($Method) {
    $Method.Body.Instructions.Clear()
    $Method.Body.ExceptionHandlers.Clear()
    $Method.Body.Variables.Clear()
}
function Load-TestDependency([string]$Path, [scriptblock]$Patch) {
    $definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path, $script:reader)
    $stream = [IO.MemoryStream]::new()
    try {
        & $Patch $definition
        $definition.Write($stream)
        return [Reflection.Assembly]::Load($stream.ToArray())
    }
    finally { $stream.Dispose(); $definition.Dispose() }
}
# In-memory shims remove Unity-native startup only. All session, validation,
# repository and checkpoint bodies run from the actual compiled plugin.
Load-TestDependency (Join-Path $binaryRoot 'UnityEngine.CoreModule.dll') {
    param($definition)
    $type = $definition.MainModule.Types | Where-Object FullName -eq 'UnityEngine.Object'
    $method = $type.Methods | Where-Object Name -eq '.cctor' | Select-Object -First 1
    Clear-Body $method
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
} | Out-Null
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $binaryRoot 'UnityEngine.dll'))) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $managedRoot 'UnityEngine.PhysicsModule.dll')) | Out-Null
Load-TestDependency (Join-Path $managedRoot 'assembly_utils.dll') {
    param($definition)
    $type = $definition.MainModule.Types | Where-Object FullName -eq 'Utils'
    $method = $type.Methods | Where-Object { $_.Name -eq 'GenerateUID' -and $_.Parameters.Count -eq 0 } | Select-Object -First 1
    Clear-Body $method
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldc_I8, [long]76561198012345678))
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
} | Out-Null
$game = Load-TestDependency (Join-Path $managedRoot 'assembly_valheim.dll') {
    param($definition)
    $version = $definition.MainModule.Types | Where-Object FullName -eq 'Version'
    ($version.Fields | Where-Object Name -eq 'm_playerVersion').Constant = [int]43
}
$fixtureAssemblyResolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    foreach ($loaded in [AppDomain]::CurrentDomain.GetAssemblies()) {
        if ($loaded.FullName -eq $eventArgs.Name) { return $loaded }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($fixtureAssemblyResolver)
$plugin = Load-TestDependency $pluginPath {
    param($definition)
    # Runtime admission is exercised later with explicit in-memory fields;
    # suppress only unrelated Unity-bound singleton initialization.
    $runtimeType = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerManagerRuntime'
    $runtimeInitializer = $runtimeType.Methods | Where-Object Name -eq '.cctor' | Select-Object -First 1
    Clear-Body $runtimeInitializer
    $runtimeInitializer.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
    # A backup capture must never synthesize a replacement profile or START ITEMS.
    $type = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ValheimPlayerProfileCodec'
    $method = $type.Methods | Where-Object Name -eq 'CreateInitialProfileBytes' | Select-Object -First 1
    Clear-Body $method
    $ctor = $definition.MainModule.ImportReference([InvalidOperationException].GetConstructor(@([string])))
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldstr, 'Backup capture called the starter factory.'))
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Newobj, $ctor))
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Throw))
}
$resolver.Dispose()

function New-InventoryPayload([object[]]$Items = @()) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    function Item-Value([Collections.IDictionary]$Item, [string]$Key, $Fallback) {
        if ($Item.Contains($Key)) { return $Item[$Key] }
        return $Fallback
    }
    try {
        $writer.Write([int]106); $writer.Write([int]$Items.Count)
        $slot = 0
        foreach ($item in $Items) {
            $writer.Write([string](Item-Value $item 'Prefab' ''))
            $writer.Write([int](Item-Value $item 'Stack' 1))
            $writer.Write([single](Item-Value $item 'Durability' 0))
            $writer.Write([int](Item-Value $item 'X' $slot)); $writer.Write([int](Item-Value $item 'Y' 0))
            ++$slot
            $writer.Write([bool](Item-Value $item 'Equipped' $false))
            $writer.Write([int](Item-Value $item 'Quality' 1)); $writer.Write([int](Item-Value $item 'Variant' 0))
            $writer.Write([long](Item-Value $item 'CrafterId' 0)); $writer.Write([string](Item-Value $item 'CrafterName' ''))
            $custom = Item-Value $item 'Custom' @{}
            $writer.Write([int]$custom.Count)
            foreach ($key in $custom.Keys) { $writer.Write([string]$key); $writer.Write([string]$custom[$key]) }
            $writer.Write([int](Item-Value $item 'WorldLevel' 0)); $writer.Write([bool](Item-Value $item 'PickedUp' $false))
        }
        $writer.Flush()
        return ,$stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}
function New-ProfilePayload([string]$Name, [long]$PlayerId, [string]$Seed,
    [string]$ItemPrefab = '', [bool]$UsedCheats = $false, [bool]$HasPlayerData = $true,
    [object[]]$SkillValues = @(), [object[]]$InventoryItems = $null) {
    $inner = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($inner)
    try {
        $writer.Write([int]29)
        foreach ($value in @([single]25, [single]25, [single]50, [single]0)) { $writer.Write($value) }
        $writer.Write(''); $writer.Write([single]0)
        if ($null -eq $InventoryItems) { $InventoryItems = if ($ItemPrefab) { @(@{ Prefab = $ItemPrefab }) } else { @() } }
        $writer.Write([byte[]](New-InventoryPayload $InventoryItems))
        for ($index = 0; $index -lt 8; ++$index) { $writer.Write([int]0) }
        $writer.Write(''); $writer.Write('')
        for ($index = 0; $index -lt 6; ++$index) { $writer.Write([single]0) }
        $writer.Write([int]0); $writer.Write([int]0)
        $writer.Write([int]2); $writer.Write([int]$SkillValues.Count)
        foreach ($skill in $SkillValues) {
            $writer.Write([int]$skill.Id); $writer.Write([single]$skill.Level); $writer.Write([single]$skill.Accumulator)
        }
        $writer.Write([int]0)
        $writer.Write([single]50); $writer.Write([single]0); $writer.Write([single]0)
        $writer.Flush()
        [byte[]]$innerBytes = $inner.ToArray()
    }
    finally { $writer.Dispose(); $inner.Dispose() }
    $outer = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($outer)
    try {
        $writer.Write([int]43); $writer.Write([int]105)
        for ($index = 0; $index -lt 105; ++$index) { $writer.Write([single]0) }
        $writer.Write($false); $writer.Write([int]0)
        $writer.Write($Name); $writer.Write($PlayerId); $writer.Write($Seed)
        $writer.Write($UsedCheats); $writer.Write([long]0)
        for ($index = 0; $index -lt 6; ++$index) { $writer.Write([int]0) }
        $writer.Write($HasPlayerData)
        if ($HasPlayerData) { $writer.Write([int]$innerBytes.Length); $writer.Write($innerBytes) }
        $writer.Flush()
        return ,($outer.ToArray())
    }
    finally { $writer.Dispose(); $outer.Dispose() }
}
function New-Request($Identity, [Guid]$SessionId, [long]$Revision, [long]$BaseRevision,
    [byte[]]$Payload, [string]$Kind = 'SaveRequest') {
    return $script:plugin.GetType('ServerManager.CharacterEnvelope').GetMethod('Create').Invoke($null,
        [object[]]@([Enum]::Parse($script:plugin.GetType('ServerManager.CharacterEnvelopeKind'), $Kind),
            $Revision, $BaseRevision, $SessionId, $Identity, [DateTime]::UtcNow, [int]43, $Payload))
}
function Get-HostSession($Opened) {
    $lookup = [object[]]@($Opened.Snapshot.SessionId, $null)
    Assert-True (Invoke-Hidden $script:service 'TryGetLocalHostSession' $lookup) 'Captured host session missing.'
    return $lookup[1]
}
function Set-Settings([bool]$BackupOnly = $true, [int]$Quota = 5) {
    $loadOnJoin = (-not $BackupOnly).ToString().ToLowerInvariant()
    $yaml = "serverSettings:`n  loadServerCharacterOnJoin: $loadOnJoin`n  maxCharactersPerAccount: $Quota`nforbiddenItems: [ForbiddenSword]`nstartItems:`n  - Wood, 20`n"
    $settings = $script:plugin.GetType('ServerManager.ServerSettings').GetMethod('Parse').Invoke($null, @($yaml))
    Assert-True ($settings.LoadServerCharacterOnJoin -eq (-not $BackupOnly)) 'Server-loading YAML polarity did not select the requested capture mode.'
    Invoke-Hidden $script:service 'ApplyServerSettings' @($settings) | Out-Null
}

# Direct-storage tests share these headless dependency shims and native profile
# builders without running capture scenarios or mutating any game/plugin file.
if ($FixtureOnly) { return }

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('smbackup-' + [Guid]::NewGuid().ToString('N'))
$service = $null
$keys = $null
try {
    $options = New-Instance 'CharacterStorageOptions'
    $options.SaveRequestBurstCapacity = 100
    $layout = New-Instance 'CharacterStorageLayout' @($testRoot)
    $keys = New-Instance 'CharacterStorageKeyProvider' @($layout)
    $codec = New-Instance 'CharacterEnvelopeCodec' @($options)
    $profiles = New-Instance 'ValheimPlayerProfileCodec' @($options)
    $script:fixtureAdmin = $false
    $adminType = [Func``2].MakeGenericType($plugin.GetType('ServerManager.CharacterIdentity'), [bool])
    $adminResolver = [Management.Automation.LanguagePrimitives]::ConvertTo(
        [scriptblock]{ param($identity) return $script:fixtureAdmin }, $adminType)
    $mode = [Enum]::Parse($plugin.GetType('ServerManager.CharacterSemanticPolicyMode'), 'Enforce')
    $policy = New-Instance 'CharacterSemanticPolicy' @($mode, 'ForbiddenSword', [single]800,
        [single]800, [single]500, [single]100, [single]1000)
    $validator = New-Instance 'CharacterSemanticRevisionValidator' @($profiles,
        (New-Instance 'CharacterSemanticEvaluator' @($policy)), $adminResolver)
    $observe = [Enum]::Parse($plugin.GetType('ServerManager.CharacterSemanticPolicyMode'), 'Observe')
    $storedPolicy = New-Instance 'CharacterSemanticPolicy' @($observe, '', [single]800,
        [single]800, [single]500, [single]100, [single]1000)
    $storedValidator = New-Instance 'CharacterSemanticRevisionValidator' @($profiles,
        (New-Instance 'CharacterSemanticEvaluator' @($storedPolicy)))
    $repository = New-Instance 'CharacterRepository' @($layout, $options, $profiles, $validator, $null)
    $service = New-Instance 'CharacterSnapshotService' @($options,
        (New-Instance 'CharacterPeerIdentityResolver'), $keys, $codec, $profiles, $repository, $storedValidator)
    Set-Settings
    $identity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000001', 'BackupHero')
    $storageKey = $keys.DeriveStorageKey($identity)
    $profilePath = $layout.GetProfilePath($storageKey)
    $live = $service.GetType().GetField('_liveSnapshots', $allInstance).GetValue($service)
    [byte[]]$capture1 = New-ProfilePayload 'BackupHero' 101 'local-progress'

    # The candidate has no authority until readiness; disconnect must leave no
    # primary, retained overlay, or unusable lease behind.
    $aborted = Invoke-Hidden $service 'OpenBackupLocalHostSession' @($identity, $capture1)
    $session = Get-HostSession $aborted
    Assert-True ($aborted.PendingInitialCommit -and (Get-Hidden $session 'BackupOnly') -and
        -not $aborted.Snapshot.RequiresFreshLocalCharacter -and
        (Test-Bytes $aborted.Snapshot.GetPayloadCopy() $capture1)) 'Local progress was reset or did not enter pending backup admission.'
    Assert-True (-not (Test-Path -LiteralPath $profilePath) -and $live.Count -eq 0) 'Unready capture published state.'
    $early = New-Request $identity $session.SessionId 2 1 $capture1
    Assert-True (-not (Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($session.SessionId, $early)).Accepted) 'Pending capture admitted a gameplay save.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($session.SessionId) | Out-Null
    Assert-True ($live.Count -eq 0 -and -not (Test-Path -LiteralPath $profilePath)) 'Aborted first capture persisted.'

    $opened = Invoke-Hidden $service 'OpenBackupLocalHostSession' @($identity, $capture1)
    $session = Get-HostSession $opened
    $retainedBytes = $service.GetType().GetField('_retainedSnapshotPayloadBytes', $allInstance)
    $before = [long]$retainedBytes.GetValue($service)
    try {
        $retainedBytes.SetValue($service, [long](300 * 1024 * 1024))
        Assert-Throws { Invoke-Hidden $service 'FinalizePendingLocalHostSnapshot' @($session.SessionId) } '*capacity*'
        Assert-True ((Get-Hidden $session 'PendingInitialCommit') -and
            $live.Count -eq 0 -and -not (Test-Path -LiteralPath $profilePath)) 'Failed capacity admission left a durable capture.'
    }
    finally { $retainedBytes.SetValue($service, $before) }
    Assert-True (Invoke-Hidden $service 'FinalizePendingLocalHostSnapshot' @($session.SessionId)) 'Ready first capture was not committed.'
    Assert-True (-not (Invoke-Hidden $service 'FinalizePendingLocalHostSnapshot' @($session.SessionId))) 'Capture finalize is not idempotent.'
    $disk = Invoke-Hidden $repository 'Load' @($identity, $storageKey)
    Assert-True ((Test-Path -LiteralPath $profilePath) -and
        (Test-Bytes $disk.Envelope.GetPayloadCopy() $capture1)) 'Materialized first capture did not establish its durable .fch.'

    # Mode reload applies to the next connection; full/inventory requests keep
    # the ordinary sequence checks and checkpoint pipeline on this connection.
    Set-Settings $false
    Assert-True (Get-Hidden $session 'BackupOnly') 'Live reload changed the open session mode.'
    [byte[]]$capture2 = New-ProfilePayload 'BackupHero' 101 'gameplay-save'
    $save2 = New-Request $identity $session.SessionId 2 1 $capture2
    Assert-True (Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($session.SessionId, $save2)).Accepted 'Backup gameplay save rejected.'
    Assert-True (-not (Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($session.SessionId, $save2)).Accepted) 'Backup mode accepted a stale revision.'
    $checkpoint = Invoke-Hidden $service 'BeginCheckpoint'
    $entries = Get-Hidden $checkpoint 'Entries'
    Assert-True ($entries.Count -eq 1) 'Backup character missing from checkpoint.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($session.SessionId) | Out-Null

    Set-Settings
    [byte[]]$capture3 = New-ProfilePayload 'BackupHero' 101 'offline-progress'
    $replacement = Invoke-Hidden $service 'OpenBackupLocalHostSession' @($identity, $capture3)
    Assert-True ($replacement.Snapshot.Revision -eq 3 -and $replacement.PendingInitialCommit -and
        (Test-Bytes (Get-Hidden $live[$storageKey] 'LatestEnvelope').GetPayloadCopy() $capture2)) 'Pending replacement changed the prior RAM baseline or reused its revision.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($replacement.Snapshot.SessionId) | Out-Null
    Assert-True (Test-Bytes (Get-Hidden $live[$storageKey] 'LatestEnvelope').GetPayloadCopy() $capture2) 'Aborted replacement changed acknowledged RAM.'
    $replacement = Invoke-Hidden $service 'OpenBackupLocalHostSession' @($identity, $capture3)
    Invoke-Hidden $service 'FinalizePendingLocalHostSnapshot' @($replacement.Snapshot.SessionId) | Out-Null
    Assert-True (Test-Bytes ((Invoke-Hidden $repository 'Load' @($identity, $storageKey)).Envelope.GetPayloadCopy()) $capture1) 'Existing capture bypassed checkpoint and replaced disk.'
    Invoke-Hidden $service 'CommitCheckpointEntry' @($checkpoint, $entries[0]) | Out-Null
    Assert-True ((Test-Bytes ((Invoke-Hidden $repository 'Load' @($identity, $storageKey)).Envelope.GetPayloadCopy()) $capture2) -and
        (Test-Bytes (Get-Hidden $live[$storageKey] 'LatestEnvelope').GetPayloadCopy() $capture3)) 'Older in-flight checkpoint lost its cutoff or overwrote the replacement RAM.'
    Set-Settings $false
    Invoke-Hidden $service 'CloseLocalHostSession' @($replacement.Snapshot.SessionId) | Out-Null
    $authoritative = Invoke-Hidden $service 'OpenOrCreateLocalHostSession' @($identity)
    Assert-True (-not (Get-Hidden (Get-HostSession $authoritative) 'BackupOnly') -and
        $authoritative.Snapshot.Revision -eq 3 -and
        (Test-Bytes $authoritative.Snapshot.GetPayloadCopy() $capture3)) 'Mode-off reconnect did not select latest acknowledged RAM.'
    $inventory = [byte[]]@([BitConverter]::GetBytes([int]106) + [BitConverter]::GetBytes([int]0))
    $inventorySave = New-Request $identity $authoritative.Snapshot.SessionId 4 3 $inventory 'InventorySaveRequest'
    Assert-True (Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($authoritative.Snapshot.SessionId, $inventorySave)).Accepted 'Capture did not establish inventory fast-path baseline.'
    $latest = Invoke-Hidden $service 'GetLocalHostSnapshot' @($authoritative.Snapshot.SessionId)
    $finalCheckpoint = Invoke-Hidden $service 'BeginCheckpoint'
    foreach ($entry in (Get-Hidden $finalCheckpoint 'Entries')) {
        Invoke-Hidden $service 'CommitCheckpointEntry' @($finalCheckpoint, $entry) | Out-Null
    }
    Assert-True (Test-Bytes ((Invoke-Hidden $repository 'Load' @($identity, $storageKey)).Envelope.GetPayloadCopy()) $latest.GetPayloadCopy()) 'Replacement checkpoint did not rebase against the completed cutoff.'
    Assert-True (@(Get-ChildItem -LiteralPath $layout.GetAccountDirectory($storageKey) -File -Filter "$storageKey.*.fch").Count -ge 2) 'Replacement checkpoints did not rotate prior .fch backups.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($authoritative.Snapshot.SessionId) | Out-Null

    Set-Settings
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($identity,
        (New-ProfilePayload 'BackupHero' 999 'collision')) } '*PlayerProfile ID*'
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($identity, [byte[]]@(1, 2, 3)) }
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($identity,
        (New-ProfilePayload 'WrongName' 101 'name-mismatch')) }
    Assert-True (Test-Bytes (Get-Hidden $live[$storageKey] 'LatestEnvelope').GetPayloadCopy() $latest.GetPayloadCopy()) 'Rejected capture changed RAM.'

    # Absolute incoming policy remains enforceable, including when the same
    # accepted stored bytes would be reopenable under the stored Observe mode.
    $policyIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000002', 'PolicyHero')
    $forbidden = New-ProfilePayload 'PolicyHero' 202 'forbidden' -ItemPrefab 'ForbiddenSword'
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($policyIdentity, $forbidden) } '*forbidden*ForbiddenSword*'
    $script:fixtureAdmin = $true
    # Incoming prefab policy remains independent of ObjectDB. A verified admin
    # has the same narrow, auditable policy exemption as ordinary save requests.
    $adminItem = Invoke-Hidden $service 'OpenBackupLocalHostSession' @($policyIdentity, $forbidden)
    Assert-True (@($adminItem.SemanticObservations -match 'admin_bypass:forbidden_prefab').Count -eq 1 -and
        (Test-Bytes $adminItem.Snapshot.GetPayloadCopy() $forbidden)) 'The administrator prefab exemption altered raw data or lost its observation.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($adminItem.Snapshot.SessionId) | Out-Null
    $script:fixtureAdmin = $false
    $cheats = New-ProfilePayload 'PolicyHero' 202 'cheat-used' -UsedCheats $true
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($policyIdentity, $cheats) } '*policy*'
    $script:fixtureAdmin = $true
    $allowed = Invoke-Hidden $service 'OpenBackupLocalHostSession' @($policyIdentity, $cheats)
    Assert-True (@($allowed.SemanticObservations -match 'admin_bypass:used_cheats').Count -eq 1) 'Verified administrator exemption was lost at capture.'
    $captureAudit = Get-Hidden (Get-Hidden $allowed 'AuditFindings') 'AuditObservations'
    Assert-True ($captureAudit.Count -eq 1 -and (Get-Hidden $captureAudit[0] 'ReasonCode') -eq 'used_cheats') `
        'Backup capture lost generated audit metadata before publishing its open result.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($allowed.Snapshot.SessionId) | Out-Null
    $script:fixtureAdmin = $false
    Set-Settings $true 1
    $quotaIdentity = New-Instance 'CharacterIdentity' @($identity.AccountId, 'AnotherHero')
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($quotaIdentity,
        (New-ProfilePayload 'AnotherHero' 303 'quota')) } '*quota*'

    # A fresh local profile is retained verbatim and uses the normal first-full
    # promotion boundary; receiving START ITEMS is not part of backup mode.
    $freshIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000003', 'FreshHero')
    $freshKey = $keys.DeriveStorageKey($freshIdentity)
    $freshPath = $layout.GetProfilePath($freshKey)
    $freshPending = Invoke-Hidden $layout 'GetPendingProfilePath' @($freshKey)
    [byte[]]$freshBytes = New-ProfilePayload 'FreshHero' 303 'fresh-local' -HasPlayerData $false
    $fresh = Invoke-Hidden $service 'OpenBackupLocalHostSession' @($freshIdentity, $freshBytes)
    Assert-True ($fresh.Snapshot.RequiresFreshLocalCharacter -and
        (Test-Bytes $fresh.Snapshot.GetPayloadCopy() $freshBytes)) 'Fresh capture changed its original bytes or storage origin.'
    Invoke-Hidden $service 'FinalizePendingLocalHostSnapshot' @($fresh.Snapshot.SessionId) | Out-Null
    Assert-True ((Test-Path -LiteralPath $freshPending) -and -not (Test-Path -LiteralPath $freshPath)) 'Fresh capture skipped pending-file boundary.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($fresh.Snapshot.SessionId) | Out-Null
    [byte[]]$freshChanged = New-ProfilePayload 'FreshHero' 303 'still-unspawned' -HasPlayerData $false
    $fresh = Invoke-Hidden $service 'OpenBackupLocalHostSession' @($freshIdentity, $freshChanged)
    Invoke-Hidden $service 'FinalizePendingLocalHostSnapshot' @($fresh.Snapshot.SessionId) | Out-Null
    Assert-True ($fresh.Snapshot.Revision -eq 2 -and
        (Test-Bytes ((Invoke-Hidden $repository 'Load' @($freshIdentity, $freshKey)).Envelope.GetPayloadCopy()) $freshBytes)) 'Second unmaterialized capture overwrote its durable pending baseline.'
    $pendingCheckpoint = Invoke-Hidden $service 'BeginCheckpoint'
    Assert-True (@((Get-Hidden $pendingCheckpoint 'Entries') | Where-Object {
        (Get-Hidden $_ 'StorageKey') -eq $freshKey
    }).Count -eq 0) 'A still-unmaterialized replacement was checkpointed as an established profile.'
    Invoke-Hidden $service 'DiscardCheckpoint' @($pendingCheckpoint) | Out-Null
    Invoke-Hidden $service 'CloseLocalHostSession' @($fresh.Snapshot.SessionId) | Out-Null
    Set-Settings $false
    $fresh = Invoke-Hidden $service 'OpenOrCreateLocalHostSession' @($freshIdentity)
    Assert-True ($fresh.Snapshot.RequiresFreshLocalCharacter -and $fresh.Snapshot.Revision -eq 2) 'Mode-off reconnect lost the pending origin after a backup replacement.'
    [byte[]]$materialized = New-ProfilePayload 'FreshHero' 303 'spawned-local'
    $full = New-Request $freshIdentity $fresh.Snapshot.SessionId 3 2 $materialized
    Assert-True (Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($fresh.Snapshot.SessionId, $full)).Accepted 'First full local profile did not materialize.'
    Assert-True ((Test-Path -LiteralPath $freshPath) -and -not (Test-Path -LiteralPath $freshPending)) 'First full backup save was not durably promoted.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($fresh.Snapshot.SessionId) | Out-Null

    Set-Settings $true 1
    $aliasIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000004', 'AliasHero')
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($aliasIdentity,
        (New-ProfilePayload 'AliasHero' 101 'duplicate')) } '*PlayerProfile ID*another server character*'
    $pendingIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000005', 'PendingHero')
    $pendingRpc = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($game.GetType('ZRpc'))
    $pendingRemote = Invoke-Hidden $service 'OpenBackupSessionCore' @($pendingIdentity, $pendingRpc,
        (New-ProfilePayload 'PendingHero' 404 'pending-remote'))
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($aliasIdentity,
        (New-ProfilePayload 'AliasHero' 404 'pending-duplicate')) } '*PlayerProfile ID*another server character*'
    $pendingQuota = New-Instance 'CharacterIdentity' @($pendingIdentity.AccountId, 'PendingQuota')
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($pendingQuota,
        (New-ProfilePayload 'PendingQuota' 505 'pending-quota')) } '*quota*'
    $service.CloseServerSession($pendingRpc)
    $released = Invoke-Hidden $service 'OpenBackupLocalHostSession' @($aliasIdentity,
        (New-ProfilePayload 'AliasHero' 404 'released-reservation'))
    Invoke-Hidden $service 'CloseLocalHostSession' @($released.Snapshot.SessionId) | Out-Null

    # The real remote core shares leases with the local host. The public API
    # still requires a real peer; the inert RPC is only a dictionary key here.
    $rpc = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($game.GetType('ZRpc'))
    $remote = Invoke-Hidden $service 'OpenBackupSessionCore' @($identity, $rpc, $latest.GetPayloadCopy())
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($identity, $capture3) } '*active session*'
    Assert-Throws { Invoke-Hidden $service 'OpenBackupServerSession' @($null, $capture3) } '*peer*'
    Invoke-Hidden $service 'FinalizePendingInitialSnapshot' @($rpc) | Out-Null
    $service.CloseServerSession($rpc)

    # A real service replacement has no RAM revisions and selects the last
    # completed vanilla .fch baseline, independently of later accepted captures.
    $service.Dispose()
    $keys = New-Instance 'CharacterStorageKeyProvider' @($layout)
    $service = New-Instance 'CharacterSnapshotService' @($options,
        (New-Instance 'CharacterPeerIdentityResolver'), $keys, $codec, $profiles, $repository, $storedValidator)
    Set-Settings $false
    $cold = Invoke-Hidden $service 'OpenOrCreateLocalHostSession' @($identity)
    Assert-True ($cold.Snapshot.Revision -eq 1 -and
        (Test-Bytes $cold.Snapshot.GetPayloadCopy() $latest.GetPayloadCopy())) 'Cold reconnect did not select last completed disk checkpoint.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($cold.Snapshot.SessionId) | Out-Null

    # Control messages have fixed empty payloads and require the new wire
    # version; older peers cannot accidentally enter the capture handshake.
    $limitsType = $plugin.GetType('ServerManager.ConnectionProtocolLimits')
    $limitsCtor = $limitsType.GetConstructors()[0]
    $limitArgs = [object[]]::new($limitsCtor.GetParameters().Count)
    for ($index = 0; $index -lt $limitArgs.Length; ++$index) {
        $parameter = $limitsCtor.GetParameters()[$index]
        $limitArgs[$index] = [Management.Automation.LanguagePrimitives]::ConvertTo($parameter.DefaultValue, $parameter.ParameterType)
    }
    $limits = $limitsCtor.Invoke($limitArgs)
    $packetCodec = $plugin.GetType('ServerManager.ProtocolPacketCodec')
    $decode = $packetCodec.GetMethods() | Where-Object { $_.Name -eq 'TryDecode' -and $_.GetParameters()[0].ParameterType.Name -eq 'ZPackage' }
    Assert-True ($packetCodec.GetField('WireVersion', $allStatic).GetRawConstantValue() -eq 21) 'The current backup handshake and scoped library manifest require wire version 21.'
    foreach ($kind in @('BackupCaptureRequest', 'BackupCaptureCommitted')) {
        $kindValue = [Enum]::Parse($plugin.GetType('ServerManager.ProtocolPacketKind'), $kind)
        $sequence = [uint32]$plugin.GetType('ServerManager.ProtocolSequence').GetField($kind).GetRawConstantValue()
        $packet = New-Instance 'ProtocolPacket' @($kindValue, $sequence, [byte[]](1..16), [byte[]](21..52), [byte[]]@())
        $package = $packetCodec.GetMethod('Encode').Invoke($null, @($packet, $limits))
        $decodeArgs = [object[]]@($package, $limits, $null, $null)
        Assert-True ($decode.Invoke($null, $decodeArgs) -and $decodeArgs[2].Kind.ToString() -eq $kind) 'Backup control round-trip failed.'
        $badPacket = New-Instance 'ProtocolPacket' @($kindValue, $sequence, [byte[]](1..16), [byte[]](21..52), [byte[]]@(1))
        Assert-Throws { $packetCodec.GetMethod('Encode').Invoke($null, @($badPacket, $limits)) } '*empty*'
        [byte[]]$oldWire = $package.GetArray()
        $oldWire[4] = 17
        $oldPackage = [Activator]::CreateInstance($game.GetType('ZPackage'), [object[]]@(,$oldWire))
        $decodeArgs = [object[]]@($oldPackage, $limits, $null, $null)
        Assert-True (-not $decode.Invoke($null, $decodeArgs) -and
            $decodeArgs[3].Code.ToString() -eq 'ProtocolVersionMismatch') 'Previous client version was not rejected.'
    }

    # Bind the ACTUAL runtime admission callback to the ACTUAL fragment
    # reassembler. In particular no CharacterSession exists at initial capture;
    # authenticated coordinator/request state must admit it before allocation.
    $runtimeType = $plugin.GetType('ServerManager.ServerManagerRuntime')
    foreach ($fieldName in @('ServerFinalSaveDrains', 'ServerDetectionStates', 'SaveAdmissionHistory', 'GlobalSaveAdmissions')) {
        $field = $runtimeType.GetField($fieldName, $allStatic)
        $field.SetValue($null, [Activator]::CreateInstance($field.FieldType))
    }
    $fragmentLimits = New-Instance 'FragmentTransportLimits' @([int]256, [int]64,
        [int]16384, [int]16384, [int]32, [int]1, [int]1048576, [TimeSpan]::FromSeconds(25))
    $coordinator = New-Instance 'ConnectionSessionCoordinator' @($limits, $null, $null)
    foreach ($pair in @{
        _serverInboundFragmentLimits = $fragmentLimits; _serverCharacterOptions = $options;
        _serverCharacterService = $service; _coordinator = $coordinator
    }.GetEnumerator()) { $runtimeType.GetField($pair.Key, $allStatic).SetValue($null, $pair.Value) }
    $coordinatorType = $coordinator.GetType()
    $connectionType = $coordinatorType.GetNestedType('Session', $allInstance)
    $connectionStates = $coordinatorType.GetField('sessions', $allInstance).GetValue($coordinator)
    $detectionType = $runtimeType.GetNestedType('ServerDetectionState', $allInstance)
    $detections = $runtimeType.GetField('ServerDetectionStates', $allStatic).GetValue($null)
    $history = $runtimeType.GetField('SaveAdmissionHistory', $allStatic).GetValue($null)
    $admission = $runtimeType.GetMethod('AdmitClientSaveAssembly', $allStatic)
    $reassemblerCtor = $plugin.GetType('ServerManager.BoundedFragmentReassembler').GetConstructors()[0]
    $admissionDelegate = [Delegate]::CreateDelegate($reassemblerCtor.GetParameters()[3].ParameterType, $admission)
    $reassembler = $reassemblerCtor.Invoke(@($fragmentLimits, $null, $true, $admissionDelegate))
    $createTransfer = $plugin.GetType('ServerManager.BoundedFragmentCodec').GetMethod('CreateCharacterTransfer')
    [byte[]]$connectionId = 1..16
    [byte[]]$connectionNonce = 21..52
    function New-CapturePeer([string]$Key, [hashtable]$Overrides = @{}) {
        $peerRpc = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($script:game.GetType('ZRpc'))
        $connection = [Activator]::CreateInstance($script:connectionType, $true)
        $values = @{ Rpc = $peerRpc; State = [Enum]::Parse($script:plugin.GetType('ServerManager.ConnectionSessionState'), 'ManifestValidated');
            SessionId = $script:connectionId; Nonce = $script:connectionNonce; LastSequence = [uint32]3;
            AbsoluteDeadline = [long]::MaxValue; PhaseDeadline = [long]::MaxValue;
            PeerInfoAdmitted = $true; PeerInfoAuthenticated = $true; ServerCharactersEnabled = $true;
            CharacterTransferPrepared = $false }
        $state = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($script:detectionType)
        foreach ($pair in @{ BackupOnly = $true; BackupCaptureRequested = $true;
            BackupCaptureStorageKey = $Key; TerminalActionApplied = $false }.GetEnumerator()) {
            $script:detectionType.GetProperty($pair.Key, $script:allInstance).SetValue($state, $pair.Value)
        }
        foreach ($pair in $Overrides.GetEnumerator()) {
            if ($values.ContainsKey($pair.Key)) { $values[$pair.Key] = $pair.Value }
            else { $script:detectionType.GetProperty($pair.Key, $script:allInstance).SetValue($state, $pair.Value) }
        }
        foreach ($pair in $values.GetEnumerator()) {
            $script:connectionType.GetField($pair.Key, $script:allInstance).SetValue($connection, $pair.Value)
        }
        $script:connectionStates.Add($peerRpc, $connection)
        $script:detections.Add($peerRpc, $state)
        return [pscustomobject]@{ Rpc = $peerRpc; Connection = $connection; State = $state }
    }
    function Send-CaptureFragments($Peer, [byte[]]$Payload = $capture1,
        [byte[]]$Nonce = $connectionNonce) {
        $transfer = $script:createTransfer.Invoke($null, [object[]]@(
            $script:connectionId, $Nonce, $Payload, $false, $script:limits, $script:fragmentLimits))
        $result = $null
        foreach ($package in $transfer.Packets) {
            $decodeArgs = [object[]]@($package, $script:limits, $null, $null)
            Assert-True ($script:decode.Invoke($null, $decodeArgs)) 'Capture fragment encoding failed.'
            $result = $script:reassembler.AcceptPackage($Peer.Rpc, $script:connectionId, $script:connectionNonce, $decodeArgs[2])
            if ($result.Status.ToString() -eq 'Rejected') { break }
        }
        return $result
    }
    try {
        foreach ($overrides in @(
            @{ PeerInfoAuthenticated = $false }, @{ ServerCharactersEnabled = $false },
            @{ BackupOnly = $false }, @{ BackupCaptureRequested = $false },
            @{ BackupCaptureStorageKey = '' }, @{ CharacterTransferPrepared = $true },
            @{ TerminalActionApplied = $true },
            @{ State = [Enum]::Parse($plugin.GetType('ServerManager.ConnectionSessionState'), 'Ready') }
        )) {
            $invalidPeer = New-CapturePeer $storageKey $overrides
            $rejected = Send-CaptureFragments $invalidPeer
            Assert-True ($rejected.Status.ToString() -eq 'Rejected' -and
                $rejected.Rejection.Code.ToString() -eq 'InvalidTransition') 'A capture skipped authenticated request/phase admission.'
        }
        Assert-True ($history.Count -eq 0) 'Rejected pre-session traffic allocated save admission history.'
        $capturingPeer = New-CapturePeer $storageKey
        [byte[]]$wrongNonce = $connectionNonce.Clone(); $wrongNonce[0] = 255
        $wrongChallenge = Send-CaptureFragments $capturingPeer $capture1 $wrongNonce
        Assert-True ($wrongChallenge.Status.ToString() -eq 'Rejected' -and
            $wrongChallenge.Rejection.Code.ToString() -eq 'NonceMismatch' -and $history.Count -eq 0) 'Wrong capture nonce reached admission.'
        $reassembled = Send-CaptureFragments $capturingPeer
        Assert-True ($reassembled.Status.ToString() -eq 'Completed' -and
            (Test-Bytes $reassembled.Payload $capture1) -and $history.Count -eq 1 -and
            $history.ContainsKey($storageKey)) 'Authenticated raw capture was rejected before a CharacterSession exists.'
        $oversize = $admission.Invoke($null, [object[]]@($capturingPeer.Rpc, [int]($options.MaxPayloadBytes + 1)))
        Assert-True ($oversize.Code.ToString() -eq 'PayloadTooLarge') 'Initial capture used envelope bounds instead of raw-profile bounds.'
        for ($index = 0; $index -lt 12; ++$index) {
            Assert-True ($null -eq $admission.Invoke($null, [object[]]@($capturingPeer.Rpc, [int]1))) 'Valid capture burst budget was not available.'
        }
        $reconnectedPeer = New-CapturePeer $storageKey
        $quota = Send-CaptureFragments $reconnectedPeer
        Assert-True ($quota.Status.ToString() -eq 'Rejected' -and
            $quota.Rejection.Code.ToString() -eq 'CharacterSaveRateExceeded') 'Reconnecting bypassed the authenticated character capture budget.'
        $gameplay = Invoke-Hidden $service 'OpenBackupSessionCore' @($identity, $capturingPeer.Rpc, $capture1)
        Invoke-Hidden $service 'FinalizePendingInitialSnapshot' @($capturingPeer.Rpc) | Out-Null
        $detectionType.GetProperty('BackupCaptureRequested', $allInstance).SetValue($capturingPeer.State, $false)
        $connectionType.GetField('State', $allInstance).SetValue($capturingPeer.Connection,
            [Enum]::Parse($plugin.GetType('ServerManager.ConnectionSessionState'), 'Ready'))
        $gameplayQuota = $admission.Invoke($null, [object[]]@($capturingPeer.Rpc, [int]1))
        Assert-True ($gameplayQuota.Code.ToString() -eq 'CharacterSaveRateExceeded') 'Gameplay saves did not share the prior capture admission budget.'
        $service.CloseServerSession($capturingPeer.Rpc)
    }
    finally { $reassembler.Dispose(); $coordinator.Dispose() }
    Write-Host "Backup-only smoke passed ($script:assertions assertions; real repository, validation and checkpoint bodies)."
}
finally {
    if ($null -ne $service) { $service.Dispose() }
    elseif ($null -ne $keys) { $keys.Dispose() }
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($fixtureAssemblyResolver)
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-True ($resolvedRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot).StartsWith('smbackup-', [StringComparison]::Ordinal)) 'Unsafe test cleanup target.'
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
