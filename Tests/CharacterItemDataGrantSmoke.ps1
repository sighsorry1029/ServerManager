param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $presetPowerShell = (Get-Command pwsh -ErrorAction Stop).Source
    & $presetPowerShell -NoProfile -File $PSCommandPath -Configuration $Configuration -GamePath $GamePath
    if ($LASTEXITCODE -ne 0) { throw 'The source-linked item-data grant fixture failed.' }
    exit 0
}
$projectRoot = Split-Path -Parent $PSScriptRoot
# Compile the actual preset-grant implementation with inert ItemData/Inventory
# boundaries. No plugin build, Unity initialization, network or game files.
$presetSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Character/CharacterAdminActions.cs') -Raw
$presetFields = foreach ($presetFieldName in @('ShallowClone', 'AddItemAt')) {
    $presetField = [regex]::Match($presetSource, ('(?s)private static readonly MethodInfo ' + $presetFieldName + '\s*=.*?;'))
    if (-not $presetField.Success) { throw "Cannot locate the production reflection field: $presetFieldName" }
    $presetField.Value
}
$presetStart = $presetSource.IndexOf('        private static ItemDrop.ItemData CloneBeforeMetadataLoad(', [StringComparison]::Ordinal)
$presetEnd = $presetSource.IndexOf('        private static ServerManagerCommandResult ApplySkill(', [StringComparison]::Ordinal)
if ($presetStart -lt 0 -or $presetEnd -le $presetStart) {
    throw 'Cannot locate the production preset-grant field and method boundaries; update the fixture extraction explicitly.'
}
$presetMethods = $presetSource.Substring($presetStart, $presetEnd - $presetStart)
foreach ($presetMethodName in @('CloneBeforeMetadataLoad', 'GivePresetItems', 'TryFindPresetSlot', 'AddPresetStackAt', 'VerifyPresetData')) {
    if ($presetMethods -notmatch ('private static [^\r\n]*\b' + $presetMethodName + '\(')) {
        throw "Missing source-linked production method: $presetMethodName"
    }
}
$presetHarness = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CharacterItemDataGrantSmoke.cs') -Raw
$presetMarker = '// SOURCE_LINKED_PRESET_IMPLEMENTATION'
if (($presetHarness.Split(@($presetMarker), [StringSplitOptions]::None)).Length -ne 2) {
    throw 'The source-linked preset fixture must have exactly one implementation insertion point.'
}
$presetHarness = $presetHarness.Replace($presetMarker, ($presetFields -join [Environment]::NewLine) + [Environment]::NewLine + $presetMethods)
Add-Type -TypeDefinition $presetHarness
[CharacterItemDataGrantSmoke]::Run()
