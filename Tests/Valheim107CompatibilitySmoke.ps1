param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BackupOnlySmoke.ps1') -Configuration $Configuration -GamePath $GamePath -FixtureOnly -IsolateGameSaveFlags


$codec = New-Instance 'ValheimPlayerProfileCodec' @((New-Instance 'CharacterStorageOptions'))
$identity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000001', 'SchemaHero')
$codecType = $plugin.GetType('ServerManager.ValheimPlayerProfileCodec')
function Invoke-Codec([string]$Name, [object[]]$Arguments) {
    return ,$script:codecType.GetMethod($Name, $script:allInstance).Invoke($script:codec, $Arguments)
}
$deserialize = $codecType.GetMethods($allInstance) | Where-Object { $_.Name -eq 'DeserializeProfileFromBytes' -and $_.GetParameters().Count -eq 3 }
$source = [Enum]::Parse($deserialize.GetParameters()[2].ParameterType, 'Local')
[byte[]]$payload = New-ProfilePayload 'SchemaHero' 103 'new-schema' `
    -KnownBiomes @('Meadows', 'DeepNorth', 'ModBiome') -BuildMenuState ([byte[]]@(10, 20, 30, 40, 50))
$profile = Invoke-Codec 'DeserializeProfileFromBytes' @($payload, $null, $source)
Assert-True (Test-Bytes $payload (Invoke-Codec 'SerializeProfileToBytes' @($profile))) `
    'Nonempty string biome names or opaque build menu bytes were changed.'
$createdField = $game.GetType('PlayerProfile').GetField('m_dateCreated')
$created = [DateTime]::Parse('2026-09-09T12:34:56Z', [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind)
$createdField.SetValue($profile, $created)

# Fill every group and table; sparse default-only fixtures cannot detect a
# reader that silently drops the new difficulty groups or enemy categories.
for ($g = 0; $g -lt 10; ++$g) {
    $stats = $game.GetType('PlayerProfile').GetField('m_playerStats').GetValue($profile)[$g]
    for ($s = 0; $s -lt 205; ++$s) {
        $key = [Enum]::ToObject($game.GetType('PlayerStatType'), $s)
        $stats.m_stats[$key] = [single]($g * 1000 + $s + 0.25)
    }
    foreach ($table in @('m_knownWorlds', 'm_knownWorldKeys', 'm_knownCommands',
        'm_itemPickupStats', 'm_itemCraftStats', 'm_pickableStats', 'm_foodEatenStats', 'm_piecesPlacedStats')) {
        $stats.$table['entry'] = [single]($g + 1.5)
    }
    for ($e = 0; $e -lt 5; ++$e) { $stats.m_enemyStats[$e]['enemy'] = [single]($g * 10 + $e + 0.5) }
}
[byte[]]$populated = Invoke-Codec 'SerializeProfileToBytes' @($profile)
$roundtrip = Invoke-Codec 'DeserializeProfileFromBytes' @($populated, $null, $source)
Assert-True (Test-Bytes $populated (Invoke-Codec 'SerializeProfileToBytes' @($roundtrip))) `
    'A statistic group, table or non-midnight UTC creation time changed during profile round-trip.'
Invoke-Codec 'ValidateSnapshot' @($identity, $populated) | Out-Null

# Unsupported headers, invalid statistics and malformed length prefixes must
# be rejected before storage. No conversion of old profiles is attempted.
foreach ($mutation in @(
    @{ Offset = 0; Bytes = [BitConverter]::GetBytes([int]43) },
    @{ Offset = 4; Bytes = [BitConverter]::GetBytes([int]105) },
    @{ Offset = 8; Bytes = [BitConverter]::GetBytes([int]11) },
    @{ Offset = 12; Bytes = [BitConverter]::GetBytes([single]::NaN) },
    @{ Offset = (12 + 205 * 4 + 12); Bytes = [BitConverter]::GetBytes([int]6) },
    @{ Offset = ($payload.Length - 9); Bytes = [BitConverter]::GetBytes([int]99999) })) {
    [byte[]]$bad = $payload.Clone()
    [Array]::Copy($mutation.Bytes, 0, $bad, $mutation.Offset, 4)
    Assert-Throws { Invoke-Codec 'ValidateSnapshot' @($identity, $bad) }
}

# Exercise both one-byte and two-byte custom-data counts at their wire boundary.
foreach ($count in @(0, 1, 127, 128, 256)) {
    $data = [ordered]@{}
    for ($i = 0; $i -lt $count; ++$i) { $data['key' + $i] = 'value' + $i }
    [byte[]]$inventory = New-InventoryPayload @(@{ Prefab = 'SwordIron'; Custom = $data })
    Invoke-Codec 'ValidateInventorySnapshot' (,$inventory) | Out-Null
    foreach ($length in @(0, 5, ($inventory.Length - 1))) {
        [byte[]]$truncated = [byte[]]::new($length)
        [Array]::Copy($inventory, $truncated, $length)
        $expectedFailure = if ($length -lt 6) { '*invalid length*' } else { '*truncated*' }
        Assert-Throws { Invoke-Codec 'ValidateInventorySnapshot' (,$truncated) } $expectedFailure
    }
}

# A skipped game save must not acknowledge a managed character save. Preserve
# the game's flag state and exercise the actual prefix body from the built DLL.
# The Unity scene-dependent suppression property is false in this fixture;
# its actual truth table and lifecycle have separate integration coverage.
$saveSystem = $game.GetType('SaveSystem', $true)
$sessionFlags = $saveSystem.GetField('s_sessionFlags', $allStatic)
$oldFlags = $sessionFlags.GetValue($null)
$prefix = $plugin.GetType('ServerManager.ManagedCharacterSavePatch').GetMethod('Prefix', $allStatic)
try {
    $sessionFlags.SetValue($null, [Enum]::ToObject($sessionFlags.FieldType, 0))
    [object[]]$arguments = @($false, $false)
    Assert-True ($prefix.Invoke($null, $arguments) -and $arguments[1]) 'An ordinary permitted save was skipped.'
    $sessionFlags.SetValue($null, [Enum]::Parse($sessionFlags.FieldType, 'DontSaveCharacter'))
    foreach ($isFromRpc in @($false, $true)) {
        [object[]]$arguments = @($isFromRpc, $true)
        Assert-True ($prefix.Invoke($null, $arguments) -and -not $arguments[1]) `
            'A DontSaveCharacter skip could be acknowledged as a completed character save.'
    }
}
finally { $sessionFlags.SetValue($null, $oldFlags) }
Write-Output "Valheim 1.0.7 schema smoke passed ($script:assertions assertions; original game markers, statistics, biome/build state and compact inventory boundaries; Unity startup shims)."
