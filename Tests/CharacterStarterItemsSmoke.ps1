param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell's legacy Add-Type compiler does not support the production
# source's C# syntax. Run this in-memory source-linked fixture with pwsh/Roslyn;
# it still does not build the plugin or touch any installed game files.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $starterPowerShell = (Get-Command pwsh -ErrorAction Stop).Source
    & $starterPowerShell -NoProfile -File $PSCommandPath -Configuration $Configuration -GamePath $GamePath
    if ($LASTEXITCODE -ne 0) { throw 'The source-linked starter fixture failed.' }
    exit 0
}
$projectRoot = Split-Path -Parent $PSScriptRoot
# Compile the production starter builder with inert catalog/inventory fixtures.
# No plugin build, Unity player, game process or live .fch path is involved.
Add-Type -Path @(
    (Join-Path $projectRoot 'Character/CharacterStarterProfile.cs'),
    (Join-Path $PSScriptRoot 'CharacterStarterItemsHarness.cs'))
[CharacterStarterItemsHarness]::Run()

$service = Get-Content (Join-Path $projectRoot 'Character/CharacterSnapshotService.cs') -Raw
$repository = Get-Content (Join-Path $projectRoot 'Character/CharacterRepository.cs') -Raw
$envelope = Get-Content (Join-Path $projectRoot 'Character/CharacterEnvelopeCodec.cs') -Raw
$guard = Get-Content (Join-Path $projectRoot 'Character/LocalCharacterFirstJoinGuard.cs') -Raw
$hostRuntimeSource = Get-Content (Join-Path $projectRoot 'Networking/LocalHostCharacterRuntime.cs') -Raw
$starterSource = Get-Content (Join-Path $projectRoot 'Character/CharacterStarterProfile.cs') -Raw
function Assert-Source([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
Assert-Source ($service.Contains('() => _profileCodec.CreateInitialProfileBytes(')) 'Starter factory must remain in the repository missing-profile branch.'
Assert-Source ($repository.Contains('requiresFreshLocalCharacter: !persistIfMissing')) 'New service profiles require an explicit initial-origin marker; import path must not infer it.'
Assert-Source ($service.Contains('The new character requires an accepted full save before inventory-only saves.')) 'New templates must require a full accepted baseline.'
Assert-Source ($service.Contains('Client saves may not set authoritative initial-origin metadata.')) 'Client-origin metadata forgery must be rejected.'
Assert-Source ($guard.Contains('envelope.RequiresFreshLocalCharacter ||')) 'Materializing starters must not bypass the used-local first-join guard.'
Assert-Source ($hostRuntimeSource.Contains('materialized && !accepted.RequiresFreshLocalCharacter')) 'Host inventory fast path must wait for the full accepted baseline.'
Assert-Source ($envelope.Contains('CurrentProtocolVersion = 2') -and $envelope.Contains('initialOrigin > 1') -and $envelope.Contains('writer.Write(envelope.RequiresFreshLocalCharacter)')) 'Envelope must version and round-trip strict origin metadata.'
Assert-Source (-not $starterSource.Contains('CreateEmptyProfileBytes(') -and
    -not $starterSource.Contains('settings.StartItems.Count == 0')) 'Explicitly empty startItems must use the complete materialized override path.'
Assert-Source (-not $starterSource.Contains('template.m_defaultItems') -and
    -not $starterSource.Contains('template.m_random') -and
    -not $starterSource.Contains('source.GetAllItems()')) 'Starter override must ignore prefab inventory contents and vanilla/default/random item configuration.'
Assert-Source (-not $starterSource.Contains('.EquipItem(') -and
    -not $starterSource.Contains('Player.m_localPlayer') -and
    -not $starterSource.Contains('Instantiate(') -and
    -not $starterSource.Contains('InvokeRPC(')) 'Starter equipment must remain serialized detached data, without a live player, spawn or RPC side effect.'

# Optional installed-metadata cross-check. The source-linked behavioral fixture
# above needs no game installation. When present, read the actual serialization
# and load/equip seams with Cecil; never load or execute game/Unity assemblies.
$starterGameAssembly = Join-Path $GamePath 'valheim_Data\Managed\assembly_valheim.dll'
$starterCecil = Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll'
if ((Test-Path -LiteralPath $starterGameAssembly) -and (Test-Path -LiteralPath $starterCecil)) {
    [Reflection.Assembly]::LoadFrom($starterCecil) | Out-Null
    $starterDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($starterGameAssembly)
    try {
        $itemData = ($starterDefinition.MainModule.Types | Where-Object Name -eq 'ItemDrop').NestedTypes |
            Where-Object Name -eq 'ItemData'
        $itemType = $itemData.NestedTypes | Where-Object Name -eq 'ItemType'
        foreach ($fixtureType in [Enum]::GetValues([ItemDrop+ItemData+ItemType])) {
            $actualType = $itemType.Fields | Where-Object Name -eq $fixtureType.ToString()
            Assert-Source ($null -ne $actualType -and $actualType.Constant -eq [int]$fixtureType) `
                "Starter fixture ItemType changed in vanilla: $fixtureType."
        }
        $inventoryType = $starterDefinition.MainModule.Types | Where-Object Name -eq 'Inventory'
        $save = $inventoryType.Methods | Where-Object Name -eq 'Save'
        Assert-Source (@($save.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq 'ItemDrop/ItemData' -and $_.Operand.Name -eq 'Save'
        }).Count -eq 1) 'Inventory.Save must delegate compact item serialization to ItemData.Save.'
        $itemSave = $itemData.Methods | Where-Object Name -eq 'Save'
        Assert-Source (@($itemSave.Body.Instructions | Where-Object {
            $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'm_equipped'
        }).Count -eq 1 -and @($itemSave.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Write' -and
            $_.Operand.Parameters.Count -eq 1 -and $_.Operand.Parameters[0].ParameterType.FullName -eq 'System.Byte'
        }).Count -ge 4) 'ItemData.Save must retain equipment intent in compact flags.'
        $itemLoad = $itemData.Methods | Where-Object { $_.Name -eq 'Load' -and $_.Parameters.Count -eq 3 }
        Assert-Source (@($itemLoad.Body.Instructions | Where-Object {
            $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'm_equipped'
        }).Count -eq 1) 'ItemData.Load must decode equipment intent.'
        $addLoadedItem = @($inventoryType.Methods | Where-Object {
            $_.Name -eq 'AddItem' -and $_.Parameters[0].ParameterType.FullName -eq 'System.Int32' -and
            @($_.Parameters | Where-Object Name -eq 'equipped').Count -eq 1
        })
        Assert-Source ($addLoadedItem.Count -eq 1 -and @($addLoadedItem[0].Body.Instructions | Where-Object {
            $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'm_equipped'
        }).Count -eq 1) 'Loaded inventory construction must preserve decoded equipment intent.'
        $inventoryLoad = $inventoryType.Methods | Where-Object { $_.Name -eq 'Load' -and $_.Parameters.Count -eq 1 }
        Assert-Source (@($inventoryLoad.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq 'ItemDrop/ItemData' -and $_.Operand.Name -eq 'Load'
        }).Count -eq 1) 'Inventory.Load must consume compact item data.'
        $playerType = $starterDefinition.MainModule.Types | Where-Object Name -eq 'Player'
        $playerLoad = $playerType.Methods | Where-Object Name -eq 'Load'
        $loadInventory = @($playerLoad.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Load' -and
            $_.Operand.DeclaringType.FullName -eq 'Inventory'
        }) | Select-Object -First 1
        $equipInventory = @($playerLoad.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'EquipInventoryItems'
        }) | Select-Object -First 1
        Assert-Source ($null -ne $loadInventory -and $null -ne $equipInventory -and
            $loadInventory.Offset -lt $equipInventory.Offset) 'Vanilla Player.Load must consume the saved inventory before applying equipment intent.'
        $equip = $playerType.Methods | Where-Object Name -eq 'EquipInventoryItems'
        Assert-Source (@($equip.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'GetEquippedItems'
        }).Count -eq 1 -and @($equip.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'EquipItem'
        }).Count -eq 1) 'Vanilla must retain the real item-equipment validation during Player.Load.'
        Write-Host 'Installed vanilla wearable enum and Inventory.Save/Load -> Player equipment seams verified (metadata only).'
    }
    finally { $starterDefinition.Dispose() }
}
else {
    Write-Host 'SKIP: optional installed vanilla equipment metadata check (game/Cecil not present at GamePath).'
}
Write-Host 'Character START ITEMS smoke passed (production wearable builder behavior and authority seams).'
