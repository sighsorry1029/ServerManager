param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BackupOnlySmoke.ps1') -Configuration $Configuration -GamePath $GamePath -FixtureOnly

# Original game schema/access metadata is retained. The shared fixture only
# suppresses Unity startup, UID generation and unrelated runtime initialization.
$options = New-Instance 'CharacterStorageOptions'
$options.SaveRequestBurstCapacity = 100
$profiles = New-Instance 'ValheimPlayerProfileCodec' @($options)
$identity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000120', 'SchemaHero')
$codecType = $plugin.GetType('ServerManager.ValheimPlayerProfileCodec')
function Invoke-Profile([string]$Name, [object[]]$Arguments = @()) {
    $method = $script:codecType.GetMethods($script:allInstance) |
        Where-Object { $_.Name -eq $Name -and $_.GetParameters().Count -eq $Arguments.Count } |
        Select-Object -First 1
    Assert-True ($null -ne $method) "Missing codec method $Name."
    return ,$method.Invoke($script:profiles, $Arguments)
}
$deserialize = $codecType.GetMethod('DeserializeProfileFromBytes')
$serialize = $codecType.GetMethod('SerializeProfileToBytes')
$source = [Enum]::Parse($deserialize.GetParameters()[2].ParameterType, 'Local')
$playerDataField = $game.GetType('PlayerProfile').GetField('m_playerData', $allInstance)
function Read-Profile([byte[]]$Bytes) {
    return $script:deserialize.Invoke($script:profiles, [object[]]@($Bytes, $null, $script:source))
}
function Write-Profile($Profile) {
    return ,$script:serialize.Invoke($script:profiles, [object[]]@($Profile))
}
function New-Tail([int[]]$Words) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        foreach ($word in $Words) { $writer.Write($word) }
        $writer.Flush(); return ,$stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}
function Assert-Tail([byte[]]$Payload, [byte[]]$Expected, [string]$Phase) {
    [byte[]]$actual = [byte[]]::new($Expected.Length)
    [Array]::Copy($Payload, $Payload.Length - $actual.Length, $actual, 0, $actual.Length)
    Assert-True (Test-Bytes $actual $Expected) "$Phase changed the original Player 34 dictionary bytes."
}

# Int dictionary values are opaque signed game/mod state, not counters to clamp
# or reorder. Two deliberately unsorted tables exercise both new suffixes.
[byte[]]$tail = New-Tail @(3, [int]::MinValue, [int]::MaxValue, 17, -9, -3, 0,
    2, 91, [int]::MinValue, -41, [int]::MaxValue)
$fixture = @{
    Name = 'SchemaHero'; PlayerId = [long]120; Seed = 'original-state'; UsedCheats = $true
    InventoryItems = @(@{ Prefab = 'SwordIron'; Cheated = $true; Custom = [ordered]@{ 'fixture.item' = 'preserved' } })
    SkillValues = @(@{ Id = 102; Level = [single]12; Accumulator = [single]3 })
    PlayerCustomData = [ordered]@{ 'ServerManager.Poison' = '1|7|0.5|12|3'; 'mod.state' = 'untouched' }
    KnownBiomes = @('Meadows', 'DeepNorth'); BuildMenuState = [byte[]]@(10, 20, 30, 40, 50)
    PlayerTail = $tail
}
[byte[]]$current = New-ProfilePayload @fixture
$loaded = Read-Profile $current
Assert-True (Test-Bytes $current (Write-Profile $loaded)) 'Current 47/34/110 payload was not byte-stable.'
Assert-True ($loaded.m_usedCheats) 'Current outer achievement metadata was reset.'
Invoke-Profile 'ValidateSnapshot' @($identity, $current) | Out-Null

# Native 46 files are admitted without mutating any supplied bytes. Canonical
# outer serialization writes 47 but deliberately retains inner 33/inventory109:
# relabeling 109 would turn its historical cheat byte into active 110 metadata.
$legacyFixture = $fixture.Clone()
$legacyFixture.Remove('PlayerTail')
[byte[]]$legacy = New-ProfilePayload @legacyFixture -ProfileVersion 46 -PlayerDataVersion 33 -InventoryVersion 109
[byte[]]$legacyBefore = $legacy.Clone()
$legacyProfile = Read-Profile $legacy
[byte[]]$legacyInner = $playerDataField.GetValue($legacyProfile)
[byte[]]$normalized = Write-Profile $legacyProfile
Assert-True ([BitConverter]::ToInt32($normalized, 0) -eq 47 -and
    [BitConverter]::ToInt32($legacyInner, 0) -eq 33 -and
    [BitConverter]::ToInt32($legacyInner, 25) -eq 109 -and $legacyProfile.m_usedCheats -and
    (Test-Bytes $legacyInner $playerDataField.GetValue((Read-Profile $normalized))) -and
    (Test-Bytes $legacy $legacyBefore)) 'Legacy normalization relabeled inventory, lost cheats or mutated its source.'
Invoke-Profile 'ValidateSnapshot' @($identity, $legacy) | Out-Null
Invoke-Profile 'ValidateSnapshot' @($identity, $normalized) | Out-Null
[byte[]]$expectedNormalized = $legacy.Clone()
[Array]::Copy([BitConverter]::GetBytes([int]47), 0, $expectedNormalized, 0, 4)
Assert-True (Test-Bytes $normalized $expectedNormalized) 'Legacy outer serialization changed more than its profile header.'
[byte[]]$inventory = New-InventoryPayload $fixture.InventoryItems -InventoryVersion 110
Invoke-Profile 'ValidateInventorySnapshot' (,$inventory) | Out-Null
[byte[]]$oldInventory = New-InventoryPayload $fixture.InventoryItems -InventoryVersion 109
Assert-Throws { Invoke-Profile 'ValidateInventorySnapshot' (,$oldInventory) } '*version*'
foreach ($version in @(45, 48)) {
    [byte[]]$badVersion = $current.Clone()
    [Array]::Copy([BitConverter]::GetBytes([int]$version), 0, $badVersion, 0, 4)
    Assert-Throws { Invoke-Profile 'ValidateSnapshot' @($identity, $badVersion) } '*version*'
}

# Every surgical edit must preserve custom status/mod state, build menu and
# both dictionaries. Compare complete expected bytes, not only parsed values.
$replacementItems = @(@{ Prefab = 'Stone'; Stack = 9; X = 3; Y = 4 })
[byte[]]$inventoryReplacement = New-InventoryPayload $replacementItems
$inventoryArguments = [object[]]@($identity, $current, $inventoryReplacement, $null)
[byte[]]$inventoryMerged = Invoke-Profile 'ReplaceInventorySnapshot' $inventoryArguments
$inventoryFixture = $fixture.Clone(); $inventoryFixture.InventoryItems = $replacementItems
Assert-True (Test-Bytes $inventoryMerged (New-ProfilePayload @inventoryFixture)) 'Inventory splice changed unrelated full-profile bytes.'
Assert-Tail $inventoryMerged $tail 'Inventory splice'
$skillArguments = [object[]]@($identity, $current, 'set', 'Run', [single]40, $null)
[byte[]]$skillMerged = Invoke-Profile 'ReplaceSkillAdmin' $skillArguments
Assert-Tail $skillMerged $tail 'Skill admin edit'
$skillFixture = $fixture.Clone(); $skillFixture.SkillValues = @(@{ Id = 102; Level = [single]40; Accumulator = [single]0 })
Assert-True (Test-Bytes $skillMerged (New-ProfilePayload @skillFixture)) 'Skill admin edit changed custom status/mod state or unrelated bytes.'

$selected = Read-Profile (New-ProfilePayload 'SchemaHero' 121 'selected' -Beard 'Beard2' -Hair 'HairLong')
$managed = Read-Profile $current
$createOrigin = $plugin.GetType('ServerManager.CharacterEnvelope').GetMethod('CreateWithOrigin', $allStatic)
$snapshot = $createOrigin.Invoke($null, [object[]]@(
    [Enum]::Parse($plugin.GetType('ServerManager.CharacterEnvelopeKind'), 'Snapshot'),
    [long]1, [long]0, [Guid]::NewGuid(), $identity, [DateTime]::UtcNow, [int]47, $current, $true))
Assert-True (Invoke-Profile 'PreserveInitialAppearance' @($snapshot, $selected, $managed)) 'Initial appearance merge was skipped.'
[byte[]]$appearanceMerged = Write-Profile $managed
$appearanceFixture = $fixture.Clone(); $appearanceFixture.Beard = 'Beard2'; $appearanceFixture.Hair = 'HairLong'
Assert-True (Test-Bytes $appearanceMerged (New-ProfilePayload @appearanceFixture)) 'Appearance merge changed server progression or status bytes.'
Assert-Tail $appearanceMerged $tail 'Appearance merge'

$positionProfile = Read-Profile $current
$worldField = $game.GetType('PlayerProfile').GetField('m_worldData', $allInstance)
$worldType = $worldField.FieldType.GetGenericArguments()[1]
$world = [Activator]::CreateInstance($worldType)
$logoutField = $worldType.GetField('m_logoutPoint', $allInstance)
$logoutField.SetValue($world, [Activator]::CreateInstance($logoutField.FieldType, [object[]]@([single]17, [single]91, [single]-23)))
$worldType.GetField('m_haveLogoutPoint', $allInstance).SetValue($world, $true)
$worldField.GetValue($positionProfile).Add([long]120, $world)
[byte[]]$positionMerged = Write-Profile $positionProfile
Assert-True (Test-Bytes $playerDataField.GetValue((Read-Profile $positionMerged)) $playerDataField.GetValue($loaded)) 'World logout serialization changed inner player bytes.'
Assert-Tail $positionMerged $tail 'World logout serialization'

# Reject each malformed dictionary independently, including exact truncations
# whose outer Player-data length remains correct. No failure may rewrite input.
$badTails = @(
    (New-Tail @(-1, 0)), (New-Tail @(4097, 0)),
    (New-Tail @(2, 7, 1, 7, 2, 0)), (New-Tail @(0, -1)),
    (New-Tail @(0, 4097)), (New-Tail @(0, 2, 7, 1, 7, 2)),
    (New-Tail @(1, 7)), (New-Tail @(0, 1, 7)),
    [byte[]]@(0, 0, 0), [byte[]]@(0, 0, 0, 0, 0, 0, 0),
    [byte[]]((New-Tail @(0, 0)) + [byte]1)
)
foreach ($badTail in $badTails) {
    $malformedFixture = $fixture.Clone(); $malformedFixture.PlayerTail = [byte[]]$badTail
    [byte[]]$malformed = New-ProfilePayload @malformedFixture
    [byte[]]$before = $malformed.Clone()
    Assert-Throws { Invoke-Profile 'ValidateSnapshot' @($identity, $malformed) }
    Assert-True (Test-Bytes $malformed $before) 'Malformed tail validation mutated the input.'
}

# Execute original empty Inventory.Save without constructing a Player. The
# production starter serializer itself is source-linked by StarterItemsSmoke;
# here inspect its compiled tail. Player's full Unity type graph is not a valid
# standalone CLR fixture, so this does not claim live Player.Save validation.
$nativeInventory = [Activator]::CreateInstance($game.GetType('Inventory'), [object[]]@('schema fixture', $null, [int]8, [int]4))
[byte[]]$nativeInventoryBytes = Invoke-Profile 'CaptureInventoryToBytes' @($nativeInventory)
Assert-True ([BitConverter]::ToInt32($nativeInventoryBytes, 0) -eq 110 -and $nativeInventoryBytes.Length -eq 6) 'Original empty Inventory.Save did not emit inventory110.'
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
try {
    $starter = ($definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ValheimPlayerProfileCodec').Methods |
        Where-Object Name -eq 'SerializeStarterPlayerData'
    $buildMenuWrite = $starter.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Write' -and
        $_.Operand.Parameters.Count -eq 1 -and $_.Operand.Parameters[0].ParameterType.FullName -eq 'System.Byte[]'
    } | Select-Object -Last 1
    $tailWrites = @($starter.Body.Instructions | Where-Object {
        $_.Offset -gt $buildMenuWrite.Offset -and $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq 'Write' -and $_.Operand.Parameters.Count -eq 1 -and
        $_.Operand.Parameters[0].ParameterType.FullName -eq 'System.Int32'
    })
    Assert-True ($null -ne $buildMenuWrite -and $tailWrites.Count -eq 2 -and
        $tailWrites[0].Previous.OpCode.Name -eq 'ldc.i4.0' -and $tailWrites[1].Previous.OpCode.Name -eq 'ldc.i4.0') 'Compiled starter does not append two empty int dictionaries after build-menu bytes.'
}
finally { $definition.Dispose() }

# Native 109 and 110 item records have the same byte shape but different cheat
# semantics. Invoke the original ItemData.Load body directly to guard against
# a future accidental marker-only conversion in the profile codec.
$itemType = $game.GetType('ItemDrop+ItemData', $true)
$itemLoad = $itemType.GetMethods($allStatic) | Where-Object { $_.Name -eq 'Load' -and $_.GetParameters().Count -eq 3 }
$itemVersionType = $itemLoad.GetParameters()[2].ParameterType
foreach ($version in @(109, 110)) {
    [byte[]]$inventoryWire = New-InventoryPayload $fixture.InventoryItems -InventoryVersion $version
    [byte[]]$itemWire = [byte[]]::new($inventoryWire.Length - 6)
    [Array]::Copy($inventoryWire, 6, $itemWire, 0, $itemWire.Length)
    $itemPackage = [Activator]::CreateInstance($game.GetType('ZPackage'), [object[]]@(,$itemWire))
    # Load initializes every serialized field itself. Skip the catalog-backed
    # SharedData constructor; it is irrelevant to these raw loader semantics.
    $item = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($itemType)
    $hash = $itemLoad.Invoke($null, [object[]]@($itemPackage, $item, [Enum]::ToObject($itemVersionType, $version)))
    Assert-True ($hash -eq [Valheim107Fixture]::Hash('SwordIron') -and
        $itemType.GetField('m_cheated').GetValue($item) -eq ($version -eq 110)) "Original ItemData.Load $version cheat semantics changed."
}

# Real repository/service paths on owned temp files: native46 cold load,
# inventory-only acceptance, matching authoritative envelope, checkpoint and
# native backup. Full-save/restore46 lifecycle also runs in DirectStorageSmoke.
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('sm120-' + [Guid]::NewGuid().ToString('N'))
$service = $null; $keys = $null
try {
    $layout = New-Instance 'CharacterStorageLayout' @($testRoot)
    $keys = New-Instance 'CharacterStorageKeyProvider' @($layout)
    $envelopeCodec = New-Instance 'CharacterEnvelopeCodec' @($options)
    $mode = [Enum]::Parse($plugin.GetType('ServerManager.CharacterSemanticPolicyMode'), 'Observe')
    $policy = New-Instance 'CharacterSemanticPolicy' @($mode, '', [single]800, [single]800, [single]500, [single]100, [single]1000)
    $validator = New-Instance 'CharacterSemanticRevisionValidator' @($profiles, (New-Instance 'CharacterSemanticEvaluator' @($policy)))
    $repository = New-Instance 'CharacterRepository' @($layout, $options, $profiles, $validator, $null)
    $service = New-Instance 'CharacterSnapshotService' @($options, (New-Instance 'CharacterPeerIdentityResolver'), $keys, $envelopeCodec, $profiles, $repository, $validator)
    Set-Settings $false
    $key = $keys.DeriveStorageKey($identity); $path = $layout.GetProfilePath($key)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    $nativeCodec = $plugin.GetType('ServerManager.VanillaCharacterFileCodec')
    $nativeEncode = $nativeCodec.GetMethod('Encode', $allStatic)
    [byte[]]$native = $nativeEncode.Invoke($null, [object[]]@($legacy, $options.MaxPayloadBytes))
    [IO.File]::WriteAllBytes($path, $native)
    Invoke-Hidden $repository 'ValidateStorageKeyMappings' @($keys) | Out-Null
    $opened = Invoke-Hidden $service 'OpenOrCreateLocalHostSession' @($identity)
    Assert-True ($opened.Snapshot.ValheimProfileVersion -eq 46 -and (Test-Bytes $opened.Snapshot.GetPayloadCopy() $legacy) -and
        (Test-Bytes ([IO.File]::ReadAllBytes($path)) $native)) 'Native46 cold open rewrote bytes or reported the wrong envelope version.'
    $request = New-Request $identity $opened.Snapshot.SessionId 2 1 $inventoryReplacement 'InventorySaveRequest'
    Assert-True (Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($opened.Snapshot.SessionId, $request)).Accepted 'Current inventory request could not update a native46 baseline.'
    $latest = Invoke-Hidden $service 'GetLocalHostSnapshot' @($opened.Snapshot.SessionId)
    $legacyMergedArgs = [object[]]@($identity, $legacy, $inventoryReplacement, $null)
    [byte[]]$legacyMerged = Invoke-Profile 'ReplaceInventorySnapshot' $legacyMergedArgs
    Assert-True ($latest.ValheimProfileVersion -eq 46 -and [BitConverter]::ToInt32($latest.GetPayloadCopy(), 0) -eq 46 -and
        (Test-Bytes $latest.GetPayloadCopy() $legacyMerged)) 'Native46 inventory materialization disagreed with its authoritative envelope.'
    $mismatched = New-Request $identity $opened.Snapshot.SessionId 3 2 $legacy
    Assert-True (-not (Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($opened.Snapshot.SessionId, $mismatched)).Accepted) 'Full SaveRequest47 accepted a mismatched native46 body.'
    $checkpoint = Invoke-Hidden $service 'BeginCheckpoint'
    foreach ($entry in (Get-Hidden $checkpoint 'Entries')) { Invoke-Hidden $service 'CommitCheckpointEntry' @($checkpoint, $entry) | Out-Null }
    $stored = Invoke-Hidden $repository 'Load' @($identity, $key)
    Assert-True ($stored.Envelope.ValheimProfileVersion -eq 46 -and (Test-Bytes $stored.Envelope.GetPayloadCopy() $legacyMerged)) 'Checkpoint rejected or relabeled the accepted native46 inventory update.'
    $backups = Invoke-Hidden $repository 'GetAdminBackups' @($identity, $key)
    Assert-True ($backups.Count -eq 1) 'Native46 inventory checkpoint did not retain one native backup.'
    $backup = Invoke-Hidden $repository 'ReadAdminBackup' @($identity, $key, (Get-Hidden $backups[0] 'BackupId'))
    Assert-True ($backup.ValheimProfileVersion -eq 46 -and (Test-Bytes $backup.GetPayloadCopy() $legacy)) 'Native46 backup lost original payload or schema metadata.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($opened.Snapshot.SessionId) | Out-Null
    Set-Settings $true
    Assert-Throws { Invoke-Hidden $service 'OpenBackupLocalHostSession' @($identity, $legacy) } '*version*'
}
finally {
    if ($null -ne $service) { $service.Dispose() } elseif ($null -ne $keys) { $keys.Dispose() }
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($fixtureAssemblyResolver)
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-True ($resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('sm120-', [StringComparison]::Ordinal)) 'Unsafe test cleanup target.'
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
Write-Output "Valheim 1.0.20 compatibility smoke passed ($script:assertions assertions; legacy/current schemas, preserved dictionaries/status bytes, malformed tails, original Inventory.Save/ItemData.Load, compiled starter tail and native46 inventory checkpoints; headless Unity startup shims, no live game execution)."
