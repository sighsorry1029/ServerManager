param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'Valheim107Fixtures.ps1')

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-ByteArrayEqual {
    param(
        [byte[]]$Left,
        [byte[]]$Right
    )

    if ($null -eq $Left -or $null -eq $Right -or
        $Left.Length -ne $Right.Length) {
        return $false
    }

    $difference = 0
    for ($index = 0; $index -lt $Left.Length; ++$index) {
        $difference = $difference -bor ($Left[$index] -bxor $Right[$index])
    }

    return $difference -eq 0
}

function Test-ByteRangeEqual {
    param(
        [byte[]]$Left,
        [int]$LeftOffset,
        [byte[]]$Right,
        [int]$RightOffset,
        [int]$Count
    )

    if ($Count -lt 0 -or
        $LeftOffset -lt 0 -or
        $RightOffset -lt 0 -or
        $LeftOffset + $Count -gt $Left.Length -or
        $RightOffset + $Count -gt $Right.Length) {
        return $false
    }

    $difference = 0
    for ($index = 0; $index -lt $Count; ++$index) {
        $difference = $difference -bor (
            $Left[$LeftOffset + $index] -bxor
            $Right[$RightOffset + $index])
    }

    return $difference -eq 0
}

function Get-UnwrappedException {
    param([Exception]$Exception)

    $current = $Exception
    while (($current -is [Reflection.TargetInvocationException] -or
            $current -is
                [Management.Automation.MethodInvocationException]) -and
        $null -ne $current.InnerException) {
        $current = $current.InnerException
    }

    return $current
}

function Assert-ThrowsLike {
    param(
        [scriptblock]$Action,
        [string]$MessagePattern,
        [string]$FailureMessage
    )

    $caught = $null
    try {
        & $Action | Out-Null
    }
    catch {
        $caught = Get-UnwrappedException $_.Exception
    }

    Assert-True ($null -ne $caught) $FailureMessage
    Assert-True ($caught.Message -like $MessagePattern) `
        "$FailureMessage Actual error: $($caught.Message)"
    return $caught
}

function New-CustomDictionaryFixture {
    param(
        [string]$Key = "ServerManager.Probe",
        [string]$Value = "retained-value",
        [int]$Count = 1,
        [bool]$DuplicateKeys = $false,
        [bool]$TruncatedValue = $false
    )
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write($Count)
        for ($index = 0; $index -lt $Count; ++$index) {
            $entryKey = if ($index -eq 0 -or $DuplicateKeys) { $Key } else { "$Key.$index" }
            $writer.Write($entryKey)
            if ($TruncatedValue) {
                # Valid 7-bit length for 64 KiB, followed by only one value byte.
                $writer.Write([byte[]]@(0x80, 0x80, 0x04, 0x41))
                break
            }
            $writer.Write($Value)
        }
        $writer.Flush()
        return $stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

function New-InventoryDiagnosticFixture {
    param(
        [bool]$IncludePrefab = $true,
        [int]$PrefabHash = 123,
        [uint16]$Stack = 1,
        [byte]$CheatFlags = 0
    )

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        # Independent compact inventory-109 wire records. The second record
        # tests ordinal/slot reporting even when the first item is valid.
        $writer.Write([int]109)
        $writer.Write([uint16]2)
        $writer.Write([int]0)
        $writer.Write([byte[]]@(0, 0, 0, 64))
        $writer.Write([int]456)
        $writer.Write([byte]0)

        $writer.Write([int]0)
        $flags = 8 -bor 128
        if ($IncludePrefab) { $flags = $flags -bor 64 }
        $writer.Write([byte[]]@(3, 1, 0, $flags))
        $writer.Write($Stack)
        if ($IncludePrefab) { $writer.Write($PrefabHash) }
        $writer.Write([byte]1)
        $writer.Write('private.fixture.key')
        $writer.Write('private-fixture-value')
        $writer.Write($CheatFlags)
        $writer.Flush()
        return $stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

function New-InventorySnapshotBytes {
    param(
        [string]$PrefabName = "",
        [int]$Stack = 1,
        [int]$PositionX = 0,
        [int]$PositionY = 0,
        [string]$CustomValue = "",
        [int]$WorldLevel = 0,
        [byte[]]$CustomDictionary = $null
    )

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([int]109)
        $writer.Write([uint16](-not [string]::IsNullOrEmpty($PrefabName)))
        if (-not [string]::IsNullOrEmpty($PrefabName)) {
            if ($null -eq $CustomDictionary) {
                $customStream = [IO.MemoryStream]::new()
                $customWriter = [IO.BinaryWriter]::new($customStream)
                try {
                    $customWriter.Write([int](-not [string]::IsNullOrEmpty($CustomValue)))
                    if ($CustomValue) { $customWriter.Write('fast.path.marker'); $customWriter.Write($CustomValue) }
                    $customWriter.Flush(); $CustomDictionary = $customStream.ToArray()
                } finally { $customWriter.Dispose(); $customStream.Dispose() }
            }
            [Valheim107Fixture]::Item($writer, $PrefabName, $Stack, 12.5, $PositionX, $PositionY, $false,
                1, 0, 76561198000000001, 'fast-path-crafter', $CustomDictionary, $WorldLevel, $true, $false)
        }

        $writer.Flush()
        return $stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function New-InventoryOverlapFixture {
    param([int]$SecondX = 0, [int]$Y = 4)
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([int]109)
        $writer.Write([uint16]2)
        [byte[]]$custom = New-CustomDictionaryFixture -Key 'private.overlap.key' -Value 'private-overlap-value'
        [Valheim107Fixture]::Item($writer, 'Stone', 7, 12.5, 0, $Y, $false,
            2, 0, 1, 'private-crafter', $custom, 1, $true, $false)
        [Valheim107Fixture]::Item($writer, 'Coal', 3, 8.5, $SecondX, $Y, $false,
            3, 0, 2, 'private-crafter', $custom, 7, $true, $false)
        $writer.Flush()
        return $stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

function New-InnerPlayerDataFixture {
    param(
        [byte[]]$InventorySnapshot,
        [byte[]]$PlayerCustomDictionary = $null,
        [string]$Beard = 'beard-after-inventory',
        [string]$Hair = 'hair-after-inventory',
        [single[]]$Colors = @(0.1, 0.2, 0.3, 0.4, 0.5, 0.6),
        [int]$Model = 1
    )

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([int]33)
        $writer.Write([single]25)
        $writer.Write([single]24)
        $writer.Write([single]50)
        $writer.Write([single]3)
        $writer.Write("guardian-marker")
        $writer.Write([single]2)

        $inventoryOffset = [int]$stream.Position
        $writer.Write($InventorySnapshot)
        $inventoryLength = [int]$stream.Position - $inventoryOffset

        $writer.Write([int]1)
        $writer.Write("recipe-after-inventory")
        for ($index = 0; $index -lt 7; ++$index) {
            $writer.Write([int]0)
        }

        $appearanceOffset = [int]$stream.Position
        $writer.Write($Beard)
        $writer.Write($Hair)
        foreach ($color in $Colors) {
            $writer.Write([single]$color)
        }

        $writer.Write($Model)
        $appearanceLength = [int]$stream.Position - $appearanceOffset
        $writer.Write([int]0)
        $writer.Write([int]2)
        $writer.Write([int]0)
        if ($null -ne $PlayerCustomDictionary) {
            $writer.Write($PlayerCustomDictionary)
        }
        else {
            $writer.Write([int]1)
            $writer.Write("player.fast.path.marker")
            $writer.Write("suffix-value")
        }
        $writer.Write([single]49)
        $writer.Write([single]5)
        $writer.Write([single]4)
        $writer.Write([int]0) # build menu byte array
        $writer.Flush()

        return [pscustomobject]@{
            Bytes = $stream.ToArray()
            InventoryOffset = $inventoryOffset
            InventoryLength = $inventoryLength
            AppearanceOffset = $appearanceOffset
            AppearanceLength = $appearanceLength
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Invoke-ZPackageWrite {
    param(
        [object]$Package,
        [Reflection.MethodInfo]$Method,
        [object]$Value
    )

    $arguments = [object[]]::new(1)
    $arguments[0] = $Value
    $Method.Invoke($Package, $arguments) | Out-Null
}

function Invoke-OneArgument {
    param(
        [Reflection.MethodInfo]$Method,
        [object]$Instance,
        [object]$Value
    )

    $arguments = [object[]]::new(1)
    $arguments[0] = $Value
    return $Method.Invoke($Instance, $arguments)
}

function Invoke-TwoArguments {
    param(
        [Reflection.MethodInfo]$Method,
        [object]$Instance,
        [object]$First,
        [object]$Second
    )

    $arguments = [object[]]::new(2)
    $arguments[0] = $First
    $arguments[1] = $Second
    return $Method.Invoke($Instance, $arguments)
}

function New-PlayerProfilePayload {
    param(
        [string]$CharacterName,
        [long]$PlayerId,
        [byte[]]$PlayerData,
        [bool]$HasPlayerData = $true,
        [object[]]$WorldData = @()
    )

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([int]46); [Valheim107Fixture]::Statistics($writer)
        $writer.Write($false); $writer.Write([int]$WorldData.Count)
        foreach ($world in $WorldData) {
            $writer.Write([long]$world.WorldId)
            $writer.Write([bool]$world.HaveCustomSpawnPoint)
            foreach ($coordinate in $world.SpawnPoint) { $writer.Write([single]$coordinate) }
            $writer.Write([bool]$world.HaveLogoutPoint)
            foreach ($coordinate in $world.LogoutPoint) { $writer.Write([single]$coordinate) }
            $writer.Write([bool]$world.HaveDeathPoint)
            foreach ($coordinate in $world.DeathPoint) { $writer.Write([single]$coordinate) }
            foreach ($coordinate in $world.HomePoint) { $writer.Write([single]$coordinate) }
            $writer.Write($null -ne $world.MapData)
            if ($null -ne $world.MapData) {
                $writer.Write([int]$world.MapData.Length); $writer.Write([byte[]]$world.MapData)
            }
        }
        $writer.Write($CharacterName); $writer.Write($PlayerId)
        $writer.Write('fast-path-seed'); $writer.Write($false); $writer.Write([long]0)
        $writer.Write($HasPlayerData)
        if ($HasPlayerData) { $writer.Write([int]$PlayerData.Length); $writer.Write($PlayerData) }
        $writer.Flush(); return ,$stream.ToArray()
    } finally { $writer.Dispose(); $stream.Dispose() }

}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$gameAssemblyPath = Join-Path $GamePath `
    "valheim_Data\Managed\assembly_valheim.dll"
$utilsAssemblyPath = Join-Path $GamePath `
    "valheim_Data\Managed\assembly_utils.dll"
$steamworksPath = Join-Path $GamePath `
    "valheim_Data\Managed\com.rlabrecque.steamworks.net.dll"
$netstandardPath = Join-Path $GamePath `
    "valheim_Data\Managed\netstandard.dll"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"
$unityCorePath = Join-Path $projectRoot `
    "bin\$Configuration\UnityEngine.CoreModule.dll"
$unityFacadePath = Join-Path $projectRoot `
    "bin\$Configuration\UnityEngine.dll"

foreach ($requiredPath in @(
    $pluginPath,
    $gameAssemblyPath,
    $utilsAssemblyPath,
    $cecilPath,
    $unityCorePath,
    $unityFacadePath)) {
    Assert-True (Test-Path -LiteralPath $requiredPath) `
        "Required inventory fast-path smoke assembly is missing: $requiredPath"
}

[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
foreach ($dependencyPath in @($netstandardPath, $steamworksPath)) {
    if (Test-Path -LiteralPath $dependencyPath) {
        [Reflection.Assembly]::LoadFrom($dependencyPath) | Out-Null
    }
}

$gameResolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$gameResolver.AddSearchDirectory((
    Join-Path $GamePath "valheim_Data\Managed"))
$gameResolver.AddSearchDirectory((
    Join-Path $GamePath "BepInEx\core"))
$gameReaderParameters = [Mono.Cecil.ReaderParameters]::new()
$gameReaderParameters.AssemblyResolver = $gameResolver

# Unity native icalls cannot execute in a standalone PowerShell CLR. Keep the
# installed API surface but replace only Object's native cctor and Utils UID
# generation in in-memory, test-only dependency images.
$unityCoreDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $unityCorePath,
    $gameReaderParameters)
$unityCoreStream = [IO.MemoryStream]::new()
try {
    $unityObjectDefinition = $unityCoreDefinition.MainModule.Types |
        Where-Object FullName -eq "UnityEngine.Object" |
        Select-Object -First 1
    $objectOffsetField = $unityObjectDefinition.Fields |
        Where-Object Name -eq "OffsetOfInstanceIDInCPlusPlusObject" |
        Select-Object -First 1
    $objectCctor = $unityObjectDefinition.Methods |
        Where-Object Name -eq ".cctor" |
        Select-Object -First 1
    Assert-True (
        $null -ne $objectOffsetField -and
        $null -ne $objectCctor -and
        $objectCctor.HasBody) `
        "The UnityEngine.Object test-only cctor seam changed."
    $objectCctor.Body.ExceptionHandlers.Clear()
    $objectCctor.Body.Variables.Clear()
    $objectCctor.Body.Instructions.Clear()
    $objectCctor.Body.InitLocals = $false
    $objectCctor.Body.Instructions.Add(
        [Mono.Cecil.Cil.Instruction]::Create(
            [Mono.Cecil.Cil.OpCodes]::Ldc_I4_0))
    $objectCctor.Body.Instructions.Add(
        [Mono.Cecil.Cil.Instruction]::Create(
            [Mono.Cecil.Cil.OpCodes]::Stsfld,
            $objectOffsetField))
    $objectCctor.Body.Instructions.Add(
        [Mono.Cecil.Cil.Instruction]::Create(
            [Mono.Cecil.Cil.OpCodes]::Ret))
    $unityCoreDefinition.Write($unityCoreStream)
    [Reflection.Assembly]::Load($unityCoreStream.ToArray()) | Out-Null
}
finally {
    $unityCoreStream.Dispose()
    $unityCoreDefinition.Dispose()
}

[Reflection.Assembly]::Load(
    [IO.File]::ReadAllBytes($unityFacadePath)) | Out-Null

$utilsDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $utilsAssemblyPath,
    $gameReaderParameters)
$utilsStream = [IO.MemoryStream]::new()
try {
    $utilsTypeDefinition = $utilsDefinition.MainModule.Types |
        Where-Object FullName -eq "Utils" |
        Select-Object -First 1
    $generateUidDefinition = $utilsTypeDefinition.Methods |
        Where-Object {
            $_.Name -eq "GenerateUID" -and
            $_.Parameters.Count -eq 0 -and
            $_.ReturnType.FullName -eq "System.Int64"
        } |
        Select-Object -First 1
    Assert-True (
        $null -ne $generateUidDefinition -and
        $generateUidDefinition.HasBody) `
        "Utils.GenerateUID() changed in the installed game."
    $generateUidDefinition.Body.ExceptionHandlers.Clear()
    $generateUidDefinition.Body.Variables.Clear()
    $generateUidDefinition.Body.Instructions.Clear()
    $generateUidDefinition.Body.InitLocals = $false
    $generateUidDefinition.Body.Instructions.Add(
        [Mono.Cecil.Cil.Instruction]::Create(
            [Mono.Cecil.Cil.OpCodes]::Ldc_I8,
            [long]76561198012345678))
    $generateUidDefinition.Body.Instructions.Add(
        [Mono.Cecil.Cil.Instruction]::Create(
            [Mono.Cecil.Cil.OpCodes]::Ret))
    $utilsDefinition.Write($utilsStream)
    [Reflection.Assembly]::Load($utilsStream.ToArray()) | Out-Null
}
finally {
    $utilsStream.Dispose()
    $utilsDefinition.Dispose()
}

$gameDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $gameAssemblyPath,
    $gameReaderParameters)
$gameStream = [IO.MemoryStream]::new()
try {
    $versionType = $gameDefinition.MainModule.Types |
        Where-Object FullName -eq "Version" |
        Select-Object -First 1
    $playerVersionField = $versionType.Fields |
        Where-Object Name -eq "c_PlayerVersion" |
        Select-Object -First 1
    Assert-True (
        $null -ne $playerVersionField -and
        $playerVersionField.HasConstant) `
        "The installed PlayerProfile version marker changed."
    Assert-True ($playerVersionField.Constant -eq 46) 'Expected original Valheim 1.0.7 profile marker.'
    $gameDefinition.Write($gameStream)
    $gameAssembly = [Reflection.Assembly]::Load($gameStream.ToArray())
}
finally {
    $gameStream.Dispose()
    $gameDefinition.Dispose()
    $gameResolver.Dispose()
}

$plugin = [Reflection.Assembly]::Load(
    [IO.File]::ReadAllBytes($pluginPath))

$script:zPackageType = $gameAssembly.GetType("ZPackage", $true)
$script:zPackageWriteInt = $script:zPackageType.GetMethod(
    "Write",
    [Type[]]@([int]))
$script:zPackageWriteLong = $script:zPackageType.GetMethod(
    "Write",
    [Type[]]@([long]))
$script:zPackageWriteSingle = $script:zPackageType.GetMethod(
    "Write",
    [Type[]]@([single]))
$script:zPackageWriteBool = $script:zPackageType.GetMethod(
    "Write",
    [Type[]]@([bool]))
$script:zPackageWriteString = $script:zPackageType.GetMethod(
    "Write",
    [Type[]]@([string]))
$script:zPackageWriteByteArray = $script:zPackageType.GetMethod(
    "Write",
    [Type[]]@([byte[]]))
$script:zPackageGetArray = $script:zPackageType.GetMethod(
    "GetArray",
    [Type[]]@())
Assert-True (
    $null -ne $script:zPackageWriteInt -and
    $null -ne $script:zPackageWriteLong -and
    $null -ne $script:zPackageWriteSingle -and
    $null -ne $script:zPackageWriteBool -and
    $null -ne $script:zPackageWriteString -and
    $null -ne $script:zPackageWriteByteArray -and
    $null -ne $script:zPackageGetArray) `
    "The Valheim ZPackage fixture seam changed."

$storageOptionsType = $plugin.GetType(
    "ServerManager.CharacterStorageOptions",
    $true)
$identityType = $plugin.GetType(
    "ServerManager.CharacterIdentity",
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
$envelopeCodecType = $plugin.GetType(
    "ServerManager.CharacterEnvelopeCodec",
    $true)
$protocolExceptionType = $plugin.GetType(
    "ServerManager.CharacterProtocolException",
    $true)

$options = [Activator]::CreateInstance($storageOptionsType)
$defaultPayloadField = $storageOptionsType.GetField(
    "DefaultMaxPayloadBytes",
    [Reflection.BindingFlags]"Static,Public")
$defaultEnvelopeOverheadField = $storageOptionsType.GetField(
    "DefaultMaxEnvelopeOverheadBytes",
    [Reflection.BindingFlags]"Static,Public")
Assert-True ($null -ne $defaultPayloadField -and
    $null -ne $defaultEnvelopeOverheadField -and
    [int]$defaultPayloadField.GetRawConstantValue() -eq 10 * 1024 * 1024 -and
    [int]$defaultEnvelopeOverheadField.GetRawConstantValue() -eq 16 * 1024 -and
    $options.MaxPayloadBytes -eq 10 * 1024 * 1024 -and
    $options.MaxEnvelopeBytes -eq (10 * 1024 * 1024 + 16 * 1024)) `
    "Default character admission must be 10 MiB with a separate 16 KiB envelope allowance."
$profileCodec = [Activator]::CreateInstance(
    $profileCodecType,
    [object[]]@($options))
$envelopeCodec = [Activator]::CreateInstance(
    $envelopeCodecType,
    [object[]]@($options))
$identity = [Activator]::CreateInstance(
    $identityType,
    [object[]]@("Steam_76561198000000001", "FastPathHero"))

$validateInventory = $profileCodecType.GetMethod(
    "ValidateInventorySnapshot",
    [Reflection.BindingFlags]"Instance,Public")
$replaceInventory = $profileCodecType.GetMethod(
    "ReplaceInventorySnapshot",
    [Reflection.BindingFlags]"Instance,NonPublic")
$validateSnapshot = $profileCodecType.GetMethod(
    "ValidateSnapshot",
    [Reflection.BindingFlags]"Instance,Public")
$extractValidatedSnapshot = $profileCodecType.GetMethods(
    [Reflection.BindingFlags]"Instance,NonPublic") |
    Where-Object {
        $_.Name -eq "ExtractValidatedSnapshot" -and
        $_.GetParameters().Count -eq 2 -and
        $_.GetParameters()[0].ParameterType -eq $identityType
    } |
    Select-Object -First 1
$maximumInventoryField = $profileCodecType.GetField(
    "MaximumInventorySnapshotBytes",
    [Reflection.BindingFlags]"Static,Public")
$createEnvelope = $envelopeType.GetMethod(
    "Create",
    [Reflection.BindingFlags]"Static,Public")
$encodeEnvelope = $envelopeCodecType.GetMethod(
    "Encode",
    [Reflection.BindingFlags]"Instance,Public")
$decodeEnvelope = $envelopeCodecType.GetMethod(
    "Decode",
    [Reflection.BindingFlags]"Instance,Public")
$currentProtocolField = $envelopeCodecType.GetField(
    "CurrentProtocolVersion",
    [Reflection.BindingFlags]"Static,Public")

Assert-True (
    $null -ne $validateInventory -and
    $null -ne $replaceInventory -and
    $replaceInventory.GetParameters().Count -eq 4 -and
    $replaceInventory.GetParameters()[3].ParameterType.IsByRef -and
    $null -ne $validateSnapshot -and
    $null -ne $extractValidatedSnapshot -and
    $null -ne $maximumInventoryField -and
    $null -ne $createEnvelope -and
    $createEnvelope.GetParameters().Count -eq 8 -and
    $null -ne $encodeEnvelope -and
    $null -ne $decodeEnvelope -and
    $null -ne $currentProtocolField) `
    "The inventory fast-path codec or envelope seam changed."

$maximumInventoryBytes = [int]$maximumInventoryField.GetRawConstantValue()
Assert-True ($maximumInventoryBytes -eq 1024 * 1024) `
    "The standalone inventory snapshot bound is no longer exactly 1 MiB."
Assert-True (
    [int]$currentProtocolField.GetRawConstantValue() -eq 2) `
    "Starter-origin metadata requires persisted envelope v2."

[byte[]]$baseInventory = New-InventorySnapshotBytes `
    -PrefabName "BaseInventoryItem" `
    -Stack 2 `
    -WorldLevel 10 `
    -CustomValue "base-value"

$overlapFlag = $protocolExceptionType.GetProperty('IsInventoryOverlap',
    [Reflection.BindingFlags]'Instance,NonPublic')
$firstOverlapHash = $protocolExceptionType.GetProperty('FirstOverlapPrefabHash',
    [Reflection.BindingFlags]'Instance,NonPublic')
$secondOverlapHash = $protocolExceptionType.GetProperty('SecondOverlapPrefabHash',
    [Reflection.BindingFlags]'Instance,NonPublic')
foreach ($overlapY in @(0, 4, 255)) {
    # Expanded mod slots remain valid. Only shared coordinates are rejected.
    [byte[]]$separateSlots = New-InventoryOverlapFixture -SecondX 1 -Y $overlapY
    Invoke-OneArgument $validateInventory $profileCodec $separateSlots | Out-Null
    [byte[]]$overlapBytes = New-InventoryOverlapFixture -Y $overlapY
    [byte[]]$overlapBefore = $overlapBytes.Clone()
    $overlapError = Assert-ThrowsLike `
        { Invoke-OneArgument $validateInventory $profileCodec $overlapBytes } `
        '*overlapping items*' 'Shared inventory coordinates were accepted.'
    Assert-True ($overlapError.GetType() -eq $protocolExceptionType -and
        $overlapFlag.GetValue($overlapError) -and
        $firstOverlapHash.GetValue($overlapError) -eq [Valheim107Fixture]::Hash('Stone') -and
        $secondOverlapHash.GetValue($overlapError) -eq [Valheim107Fixture]::Hash('Coal')) `
        'Typed overlap diagnostics do not identify both colliding prefabs.'
    foreach ($detail in @("Zero-based slot (0, $overlapY), 2 items", 'item #1', 'item #2',
        ('prefabHash=0x{0:X8}' -f [Valheim107Fixture]::Hash('Stone')),
        ('prefabHash=0x{0:X8}' -f [Valheim107Fixture]::Hash('Coal')),
        'stack=7, quality=2, worldLevel=1', 'stack=3, quality=3, worldLevel=7')) {
        Assert-True ($overlapError.Message.Contains($detail)) "Missing overlap diagnostic: $detail"
    }
    Assert-True (-not $overlapError.Message.Contains('private')) `
        'Overlap diagnostics exposed crafter or custom data.'
    $overlapInner = New-InnerPlayerDataFixture $overlapBytes
    [byte[]]$overlapProfile = New-PlayerProfilePayload `
        -CharacterName 'FastPathHero' -PlayerId ([long]76561198000000001) -PlayerData $overlapInner.Bytes
    $fullOverlapError = Assert-ThrowsLike `
        { Invoke-TwoArguments $validateSnapshot $profileCodec $identity $overlapProfile } `
        '*overlapping items*' 'Full-profile validation accepted shared inventory coordinates.'
    Assert-True ($overlapFlag.GetValue($fullOverlapError) -and
        $fullOverlapError.Message -ceq $overlapError.Message -and
        (Test-ByteArrayEqual $overlapBytes $overlapBefore)) `
        'Full and inventory overlap classification differ, or rejected input was modified.'
}
Write-Output 'Overlap diagnostics: both items, original/expanded slots, full-profile parity and private-data exclusion passed.'

[byte[]]$replacementInventory = New-InventorySnapshotBytes `
    -PrefabName "FastInventoryItem" `
    -Stack 7 `
    -PositionX 3 `
    -PositionY 4 `
    -WorldLevel 255 `
    -CustomValue "replacement-value"
Invoke-OneArgument `
    $validateInventory `
    $profileCodec `
    $replacementInventory | Out-Null

[byte[]]$trailingInventory = [byte[]]::new(
    $replacementInventory.Length + 1)
[Array]::Copy(
    $replacementInventory,
    $trailingInventory,
    $replacementInventory.Length)
$trailingInventory[$trailingInventory.Length - 1] = 0x7f
$trailingError = Assert-ThrowsLike `
    { Invoke-OneArgument `
        $validateInventory `
        $profileCodec `
        $trailingInventory } `
    "*trailing*" `
    "The standalone inventory parser accepted trailing bytes."
Assert-True ($trailingError.GetType() -eq $protocolExceptionType) `
    "Trailing inventory bytes did not fail as a protocol error."
Assert-True (-not $overlapFlag.GetValue($trailingError)) `
    'An unrelated format failure was classified as a retryable overlap.'

[byte[]]$oversizedInventory = [byte[]]::new(
    $maximumInventoryBytes + 1)
$oversizedError = Assert-ThrowsLike `
    { Invoke-OneArgument `
        $validateInventory `
        $profileCodec `
        $oversizedInventory } `
    "*invalid length*" `
    "The inventory parser accepted more than 1 MiB."
Assert-True ($oversizedError.GetType() -eq $protocolExceptionType) `
    "An oversized inventory did not fail as a protocol error."

$inner = New-InnerPlayerDataFixture $baseInventory
[byte[]]$fullProfile = New-PlayerProfilePayload `
    -CharacterName "FastPathHero" `
    -PlayerId ([long]76561198000000001) `
    -PlayerData $inner.Bytes
[byte[]]$fullProfileBefore = [byte[]]$fullProfile.Clone()
[byte[]]$replacementBefore = [byte[]]$replacementInventory.Clone()
$replaceArguments = [object[]]::new(4)
$replaceArguments[0] = $identity
$replaceArguments[1] = $fullProfile
$replaceArguments[2] = $replacementInventory
$replaceArguments[3] = $null
[byte[]]$materialized = $replaceInventory.Invoke(
    $profileCodec,
    $replaceArguments)
$validated = $replaceArguments[3]

Assert-True (
    Test-ByteArrayEqual $fullProfile $fullProfileBefore) `
    "Inventory materialization mutated the authoritative full-profile input."
Assert-True (
    Test-ByteArrayEqual $replacementInventory $replacementBefore) `
    "Inventory materialization mutated the inventory payload input."
Assert-True (
    $materialized.Length -eq
        $fullProfile.Length - $baseInventory.Length +
        $replacementInventory.Length) `
    "Inventory materialization produced an unexpected full-profile length."

$outerPlayerDataOffset = $fullProfile.Length - $inner.Bytes.Length
$oldInventoryStart = $outerPlayerDataOffset + $inner.InventoryOffset
$oldInventoryEnd = $oldInventoryStart + $inner.InventoryLength
$newInventoryStart = $oldInventoryStart
$newInventoryEnd = $newInventoryStart + $replacementInventory.Length
$outerLengthOffset = $outerPlayerDataOffset - 4
Assert-True (
    Test-ByteRangeEqual `
        $fullProfile `
        0 `
        $materialized `
        0 `
        $outerLengthOffset) `
    "Inventory materialization changed the outer profile prefix."
Assert-True (
    Test-ByteRangeEqual `
        $fullProfile `
        $outerPlayerDataOffset `
        $materialized `
        $outerPlayerDataOffset `
        ($oldInventoryStart - $outerPlayerDataOffset)) `
    "Inventory materialization changed the inner player-data prefix."
$suffixLength = $fullProfile.Length - $oldInventoryEnd
Assert-True (
    Test-ByteRangeEqual `
        $fullProfile `
        $oldInventoryEnd `
        $materialized `
        $newInventoryEnd `
        $suffixLength) `
    "Inventory materialization did not preserve the profile suffix byte-for-byte."
Assert-True (
    Test-ByteRangeEqual `
        $replacementInventory `
        0 `
        $materialized `
        $newInventoryStart `
        $replacementInventory.Length) `
    "The validated inventory bytes were not spliced into the full profile exactly."

$newInnerLength = [BitConverter]::ToInt32(
    $materialized,
    $outerLengthOffset)
Assert-True (
    $newInnerLength -eq
        $inner.Bytes.Length - $baseInventory.Length +
        $replacementInventory.Length) `
    "Inventory materialization did not update the outer player-data length."
Invoke-TwoArguments `
    $validateSnapshot `
    $profileCodec `
    $identity `
    $materialized | Out-Null

$instanceNonPublic = [Reflection.BindingFlags]"Instance,NonPublic"
$validatedType = $validated.GetType()
$validatedPlayerId = [long]$validatedType.GetProperty(
    "PlayerId",
    $instanceNonPublic).GetValue($validated)
$semantic = $validatedType.GetProperty(
    "SemanticSnapshot",
    $instanceNonPublic).GetValue($validated)
$items = $semantic.Items
Assert-True (
    $validatedPlayerId -eq [long]76561198000000001 -and
    $semantic.HasPlayerData -and
    $items.Count -eq 1 -and
    $items[0].PrefabHash -eq [Valheim107Fixture]::Hash("FastInventoryItem") -and
    $items[0].Stack -eq 7 -and
    $items[0].PositionX -eq 3 -and
    $items[0].PositionY -eq 4 -and
    $items[0].WorldLevel -eq 255 -and
    $items[0].CustomData.Count -eq 1 -and
    $items[0].CustomData[0].Value -ceq "replacement-value" -and
    $semantic.PlayerCustomDataKeys.Count -eq 1 -and
    $semantic.PlayerCustomDataKeys[0] -ceq
        "player.fast.path.marker") `
    "The materialized full profile lost identity, item, or suffix semantics."

# World level is preserved item data, not a server-world-dependent policy.
# Exercise both accepted full profiles and inventory-only materialization with
# the same fixture builder, including both serialization boundaries.
$semanticProperty = $validatedType.GetProperty("SemanticSnapshot", $instanceNonPublic)
foreach ($itemWorldLevel in @(0, 1, 2, 10, 255)) {
    [byte[]]$levelInventory = New-InventorySnapshotBytes `
        -PrefabName "WorldLevelDataItem" -WorldLevel $itemWorldLevel
    [byte[]]$levelInventoryBefore = $levelInventory.Clone()
    Invoke-OneArgument $validateInventory $profileCodec $levelInventory | Out-Null
    $levelInner = New-InnerPlayerDataFixture $levelInventory
    [byte[]]$levelProfile = New-PlayerProfilePayload `
        -CharacterName "FastPathHero" -PlayerId ([long]76561198000000001) `
        -PlayerData $levelInner.Bytes
    [byte[]]$levelProfileBefore = $levelProfile.Clone()
    $levelValidated = Invoke-TwoArguments `
        $extractValidatedSnapshot $profileCodec $identity $levelProfile
    $levelSemantic = $semanticProperty.GetValue($levelValidated)
    Assert-True ($levelSemantic.Items.Count -eq 1 -and
        $levelSemantic.Items[0].WorldLevel -eq $itemWorldLevel -and
        (Test-ByteArrayEqual $levelProfile $levelProfileBefore)) `
        "Full-profile parsing rewrote or lost world level $itemWorldLevel."

    $levelReplaceArguments = [object[]]::new(4)
    $levelReplaceArguments[0] = $identity
    $levelReplaceArguments[1] = $fullProfile
    $levelReplaceArguments[2] = $levelInventory
    $levelReplaceArguments[3] = $null
    [byte[]]$levelMaterialized = $replaceInventory.Invoke(
        $profileCodec, $levelReplaceArguments)
    $levelMergedSemantic = $semanticProperty.GetValue($levelReplaceArguments[3])
    Assert-True ($levelMergedSemantic.Items.Count -eq 1 -and
        $levelMergedSemantic.Items[0].WorldLevel -eq $itemWorldLevel -and
        (Test-ByteRangeEqual $levelInventory 0 $levelMaterialized `
            $newInventoryStart $levelInventory.Length) -and
        (Test-ByteArrayEqual $levelInventory $levelInventoryBefore) -and
        (Test-ByteArrayEqual $fullProfile $fullProfileBefore)) `
        "The inventory fast path clamped, dropped, or rewrote world level $itemWorldLevel."
}

# World level is one byte in inventory 109; out-of-range integers cannot be
# represented. Reject unknown flag bits instead, through the same three paths.
foreach ($invalidCheatFlags in @(2, 128, 255)) {
    [byte[]]$invalidLevelInventory = New-InventorySnapshotBytes `
        -PrefabName "WorldLevelDataItem" -WorldLevel 255
    $invalidLevelInventory[$invalidLevelInventory.Length - 1] = [byte]$invalidCheatFlags
    $invalidLevelError = Assert-ThrowsLike `
        { Invoke-OneArgument $validateInventory $profileCodec $invalidLevelInventory } `
        "*invalid inventory item data*" `
        "Standalone inventory parsing accepted unknown item flag bits $invalidCheatFlags."
    Assert-True ($invalidLevelError.GetType() -eq $protocolExceptionType) `
        "An invalid inventory world level did not fail as a protocol error."

    $invalidLevelInner = New-InnerPlayerDataFixture $invalidLevelInventory
    [byte[]]$invalidLevelProfile = New-PlayerProfilePayload `
        -CharacterName "FastPathHero" -PlayerId ([long]76561198000000001) `
        -PlayerData $invalidLevelInner.Bytes
    $invalidLevelProfileError = Assert-ThrowsLike `
        { Invoke-TwoArguments $validateSnapshot $profileCodec $identity $invalidLevelProfile } `
        "*invalid inventory item data*" `
        "Full-profile parsing accepted unknown item flag bits $invalidCheatFlags."
    Assert-True ($invalidLevelProfileError.GetType() -eq $protocolExceptionType) `
        "An invalid full-profile world level did not fail as a protocol error."

    $invalidLevelReplaceArguments = [object[]]::new(4)
    $invalidLevelReplaceArguments[0] = $identity
    $invalidLevelReplaceArguments[1] = $fullProfile
    $invalidLevelReplaceArguments[2] = $invalidLevelInventory
    $invalidLevelReplaceArguments[3] = $null
    $invalidLevelReplaceError = Assert-ThrowsLike `
        { $replaceInventory.Invoke($profileCodec, $invalidLevelReplaceArguments) } `
        "*invalid inventory item data*" `
        "Inventory materialization accepted unknown item flag bits $invalidCheatFlags."
    Assert-True ($invalidLevelReplaceError.GetType() -eq $protocolExceptionType -and
        (Test-ByteArrayEqual $fullProfile $fullProfileBefore)) `
        "Rejected world-level data altered the authoritative full-profile input."
}

# Diagnostics must identify the failed record and each invalid field without
# changing acceptance, guessing its creating mod, or exposing custom data.
foreach ($case in @(
    @{ Name = 'missing identity'; Args = @{ IncludePrefab = $false }; Details = @(
        'prefabHash=0x00000000', 'saved prefab identity is missing', 'may have been created without assigning m_dropPrefab') },
    @{ Name = 'explicit zero hash'; Args = @{ PrefabHash = 0 }; Details = @(
        'saved prefab hash is zero despite prefab flag 0x40 being set') },
    @{ Name = 'zero stack'; Args = @{ Stack = 0 }; Details = @(
        'prefabHash=0x0000007B', 'Invalid stack=0') },
    @{ Name = 'unsupported flag'; Args = @{ CheatFlags = 2 }; Details = @(
        'cheatFlags=0x02', 'Unsupported cheat flag bits=0x02') },
    @{ Name = 'high unsupported flag'; Args = @{ CheatFlags = 128 }; Details = @(
        'cheatFlags=0x80', 'Unsupported cheat flag bits=0x80') },
    @{ Name = 'combined fields'; Args = @{ IncludePrefab = $false; Stack = 0; CheatFlags = 255 }; Details = @(
        'saved prefab identity is missing', 'Invalid stack=0', 'cheatFlags=0xFF', 'Unsupported cheat flag bits=0xFE') }
)) {
    $fixtureArguments = $case.Args
    [byte[]]$diagnosticInventory = New-InventoryDiagnosticFixture @fixtureArguments
    [byte[]]$diagnosticBefore = $diagnosticInventory.Clone()
    $diagnosticError = Assert-ThrowsLike `
        { Invoke-OneArgument $validateInventory $profileCodec $diagnosticInventory } `
        '*invalid inventory item data*' "Accepted $($case.Name)."
    Assert-True ($diagnosticError.GetType() -eq $protocolExceptionType) `
        "Changed exception type for $($case.Name)."
    foreach ($detail in (@('Item #2/2 at zero-based slot (3, 1)') + $case.Details)) {
        Assert-True ($diagnosticError.Message.Contains($detail)) `
            "Missing diagnostic '$detail' for $($case.Name): $($diagnosticError.Message)"
    }
    Assert-True (-not $diagnosticError.Message.Contains('private.fixture.key') -and
        -not $diagnosticError.Message.Contains('private-fixture-value')) `
        'Inventory diagnostics leaked custom data.'
    if ($case.Name -eq 'explicit zero hash') {
        Assert-True (-not $diagnosticError.Message.Contains('m_dropPrefab')) `
            'A serialized zero hash was incorrectly attributed to an unassigned prefab reference.'
    }

    $diagnosticInner = New-InnerPlayerDataFixture $diagnosticInventory
    [byte[]]$diagnosticProfile = New-PlayerProfilePayload `
        -CharacterName 'FastPathHero' -PlayerId ([long]76561198000000001) `
        -PlayerData $diagnosticInner.Bytes
    $diagnosticProfileError = Assert-ThrowsLike `
        { Invoke-TwoArguments $validateSnapshot $profileCodec $identity $diagnosticProfile } `
        '*invalid inventory item data*' "Full-profile parsing accepted $($case.Name)."
    Assert-True ($diagnosticProfileError.Message -ceq $diagnosticError.Message -and
        (Test-ByteArrayEqual $diagnosticInventory $diagnosticBefore)) `
        'Full-profile and inventory diagnostics differ, or input bytes changed.'
}
foreach ($allowedCheatFlags in @(0, 1)) {
    [byte[]]$validDiagnosticInventory = New-InventoryDiagnosticFixture -CheatFlags $allowedCheatFlags
    Invoke-OneArgument $validateInventory $profileCodec $validDiagnosticInventory | Out-Null
}
Write-Output 'Inventory diagnostics: missing/zero prefab identity, zero stack, unsupported/allowed flags, combined failures and full-profile parity passed.'

# Removing key-name policy must neither strip values nor relax wire structure.
$largeRetainedCustomValue = "v" * (60 * 1024)
foreach ($retainedCustomKey in @("Bad.Key", "ServerManager.Probe")) {
    [byte[]]$customDictionary = New-CustomDictionaryFixture `
        -Key $retainedCustomKey -Value $largeRetainedCustomValue
    [byte[]]$customInventory = New-InventorySnapshotBytes `
        -PrefabName "CustomBaseItem" -CustomDictionary $customDictionary
    $customInner = New-InnerPlayerDataFixture $customInventory $customDictionary
    [byte[]]$customProfile = New-PlayerProfilePayload `
        -CharacterName "FastPathHero" -PlayerId ([long]76561198000000001) `
        -PlayerData $customInner.Bytes
    [byte[]]$customProfileBefore = $customProfile.Clone()
    $customValidated = Invoke-TwoArguments `
        $extractValidatedSnapshot $profileCodec $identity $customProfile
    $customSemantic = $semanticProperty.GetValue($customValidated)
    Assert-True ($customSemantic.Items[0].CustomData[0].Key -ceq $retainedCustomKey -and
        $customSemantic.Items[0].CustomData[0].Value -ceq $largeRetainedCustomValue -and
        $customSemantic.PlayerCustomDataKeys[0] -ceq $retainedCustomKey -and
        (Test-ByteArrayEqual $customProfile $customProfileBefore)) `
        "Full-profile parsing removed, truncated, or rewrote custom data '$retainedCustomKey'."
    [byte[]]$customReplacement = New-InventorySnapshotBytes `
        -PrefabName "CustomReplacementItem" -CustomDictionary $customDictionary
    $customReplaceArguments = [object[]]@($identity, $customProfile, $customReplacement, $null)
    [byte[]]$customMerged = $replaceInventory.Invoke($profileCodec, $customReplaceArguments)
    $customMergedSemantic = $semanticProperty.GetValue($customReplaceArguments[3])
    $customInventoryStart = $customProfile.Length - $customInner.Bytes.Length + $customInner.InventoryOffset
    $oldCustomSuffix = $customInventoryStart + $customInventory.Length
    $newCustomSuffix = $customInventoryStart + $customReplacement.Length
    Assert-True ($customMergedSemantic.Items[0].CustomData[0].Key -ceq $retainedCustomKey -and
        $customMergedSemantic.Items[0].CustomData[0].Value -ceq $largeRetainedCustomValue -and
        $customMergedSemantic.PlayerCustomDataKeys[0] -ceq $retainedCustomKey -and
        (Test-ByteRangeEqual $customReplacement 0 $customMerged $customInventoryStart $customReplacement.Length) -and
        (Test-ByteRangeEqual $customProfile $oldCustomSuffix $customMerged $newCustomSuffix `
            ($customProfile.Length - $oldCustomSuffix))) `
        "The inventory fast path changed complete item/player custom-data bytes for '$retainedCustomKey'."
}

foreach ($malformedCustomCase in @("duplicate", "count", "negative_count", "oversize", "truncated")) {
    $fixtureOptions = switch ($malformedCustomCase) {
        "duplicate" { @{ Count = 2; DuplicateKeys = $true } }
        "count" { @{ Count = 257 } }
        "negative_count" { @{ Count = -1 } }
        "oversize" { @{ Value = "v" * (64 * 1024 + 1) } }
        "truncated" { @{ TruncatedValue = $true } }
    }
    [byte[]]$badCustomDictionary = New-CustomDictionaryFixture @fixtureOptions
    [byte[]]$badCustomInventory = New-InventorySnapshotBytes `
        -PrefabName "MalformedCustomItem" -CustomDictionary $badCustomDictionary
    $badCustomInner = New-InnerPlayerDataFixture $badCustomInventory
    [byte[]]$badCustomProfile = New-PlayerProfilePayload `
        -CharacterName "FastPathHero" -PlayerId ([long]76561198000000001) `
        -PlayerData $badCustomInner.Bytes
    $badCustomReplace = [object[]]@($identity, $fullProfile, $badCustomInventory, $null)
    foreach ($badCustomAction in @(
        { Invoke-OneArgument $validateInventory $profileCodec $badCustomInventory },
        { Invoke-TwoArguments $validateSnapshot $profileCodec $identity $badCustomProfile },
        { $replaceInventory.Invoke($profileCodec, $badCustomReplace) })) {
        $customError = Assert-ThrowsLike $badCustomAction "*custom data*" `
            "Malformed item custom data ($malformedCustomCase) bypassed codec validation."
        Assert-True ($customError.GetType() -eq $protocolExceptionType) `
            "Malformed item custom data must fail as a protocol error."
    }
    # Player dictionaries have their own 1024-entry ceiling, independent of items.
    if ($malformedCustomCase -eq "count") { $fixtureOptions.Count = 1025 }
    [byte[]]$badPlayerDictionary = New-CustomDictionaryFixture @fixtureOptions
    $badPlayerInner = New-InnerPlayerDataFixture $baseInventory $badPlayerDictionary
    [byte[]]$badPlayerProfile = New-PlayerProfilePayload `
        -CharacterName "FastPathHero" -PlayerId ([long]76561198000000001) `
        -PlayerData $badPlayerInner.Bytes
    $badPlayerReplace = [object[]]@($identity, $badPlayerProfile, $replacementInventory, $null)
    foreach ($badPlayerAction in @(
        { Invoke-TwoArguments $validateSnapshot $profileCodec $identity $badPlayerProfile },
        { $replaceInventory.Invoke($profileCodec, $badPlayerReplace) })) {
        $playerError = Assert-ThrowsLike $badPlayerAction "*player custom data*" `
            "Malformed player custom data ($malformedCustomCase) bypassed codec validation."
        Assert-True ($playerError.GetType() -eq $protocolExceptionType) `
            "Malformed player custom data must fail as a protocol error."
    }
}

[byte[]]$emptyProfile = New-PlayerProfilePayload `
    -CharacterName "FastPathHero" `
    -PlayerId ([long]76561198000000001) `
    -PlayerData ([byte[]]::new(0)) `
    -HasPlayerData $false
$emptyReplaceArguments = [object[]]::new(4)
$emptyReplaceArguments[0] = $identity
$emptyReplaceArguments[1] = $emptyProfile
$emptyReplaceArguments[2] = $replacementInventory
$emptyReplaceArguments[3] = $null
$emptyBaseError = Assert-ThrowsLike `
    { $replaceInventory.Invoke(
        $profileCodec,
        $emptyReplaceArguments) } `
    "*requires an existing full Player profile*" `
    "An inventory-only save was accepted without a full-profile base."
Assert-True ($emptyBaseError.GetType() -eq $protocolExceptionType) `
    "A missing full-profile base did not fail as a protocol error."

# Initial customization must transfer cosmetics only, leaving the authoritative
# seed's inventory/progression and the selected local profile untouched.
$preserveAppearance = $profileCodecType.GetMethod('PreserveInitialAppearance',
    [Reflection.BindingFlags]'Instance,NonPublic')
$deserializeProfile = $profileCodecType.GetMethod('DeserializeProfileFromBytes')
$serializeProfile = $profileCodecType.GetMethod('SerializeProfileToBytes')
$localSource = [Enum]::Parse($deserializeProfile.GetParameters()[2].ParameterType, 'Local')
$createOrigin = $envelopeType.GetMethod('CreateWithOrigin',
    [Reflection.BindingFlags]'Static,NonPublic')
$appearanceInner = New-InnerPlayerDataFixture $replacementInventory `
    -Beard 'Beard2' -Hair 'HairVeryDifferentLength' -Colors @(0.9, 0.8, 0.7, 0.6, 0.5, 0.4) -Model 0
[byte[]]$appearanceProfile = New-PlayerProfilePayload -CharacterName 'FastPathHero' `
    -PlayerId 1234 -PlayerData $appearanceInner.Bytes
$selected = $deserializeProfile.Invoke($profileCodec, [object[]]@($appearanceProfile, $null, $localSource))
$managed = $deserializeProfile.Invoke($profileCodec, [object[]]@($fullProfile, $null, $localSource))
$playerDataField = $deserializeProfile.ReturnType.GetField('m_playerData',
    [Reflection.BindingFlags]'Instance,NonPublic,Public')
[byte[]]$selectedBefore = $serializeProfile.Invoke($profileCodec, [object[]]@($selected))
$freshSnapshot = $createOrigin.Invoke($null, [object[]]@(
    [Enum]::Parse($envelopeKindType, 'Snapshot'), [long]1, [long]0, [Guid]::NewGuid(),
    $identity, [DateTime]::UtcNow, [int]46, $fullProfile, $true))
Assert-True ($preserveAppearance.Invoke($profileCodec, [object[]]@($freshSnapshot, $selected, $managed))) `
    'A fresh server seed did not preserve the selected appearance.'
[byte[]]$mergedAppearance = $playerDataField.GetValue($managed)
Assert-True ($mergedAppearance.Length -eq $inner.Bytes.Length - $inner.AppearanceLength + $appearanceInner.AppearanceLength) `
    'Appearance merge produced an incorrect player-data length.'
Assert-True (Test-ByteRangeEqual $mergedAppearance 0 $inner.Bytes 0 $inner.AppearanceOffset) `
    'Initial appearance imported local inventory or progression.'
Assert-True (Test-ByteRangeEqual $mergedAppearance $inner.AppearanceOffset `
    $appearanceInner.Bytes $appearanceInner.AppearanceOffset $appearanceInner.AppearanceLength) `
    'Initial beard, hair, skin/hair colors or model were not preserved.'
Assert-True (Test-ByteRangeEqual $mergedAppearance ($inner.AppearanceOffset + $appearanceInner.AppearanceLength) `
    $inner.Bytes ($inner.AppearanceOffset + $inner.AppearanceLength) `
    ($inner.Bytes.Length - $inner.AppearanceOffset - $inner.AppearanceLength)) `
    'Initial appearance changed skills, food, custom data or other server suffix data.'
[byte[]]$selectedAfter = $serializeProfile.Invoke($profileCodec, [object[]]@($selected))
Assert-True (Test-ByteArrayEqual $selectedBefore $selectedAfter) 'Appearance merge mutated the selected profile.'
[byte[]]$savedAppearance = $serializeProfile.Invoke($profileCodec, [object[]]@($managed))
$roundTrip = $deserializeProfile.Invoke($profileCodec, [object[]]@($savedAppearance, $null, $localSource))
Assert-True (Test-ByteArrayEqual $mergedAppearance ($playerDataField.GetValue($roundTrip))) `
    'The next full-profile serialization lost the initial appearance.'

foreach ($origin in @(@([long]1, [long]0, $false), @([long]2, [long]1, $true))) {
    $existingSnapshot = $createOrigin.Invoke($null, [object[]]@(
        [Enum]::Parse($envelopeKindType, 'Snapshot'), $origin[0], $origin[1], [Guid]::NewGuid(),
        $identity, [DateTime]::UtcNow, [int]46, $fullProfile, $origin[2]))
    $existing = $deserializeProfile.Invoke($profileCodec, [object[]]@($fullProfile, $null, $localSource))
    Assert-True (-not $preserveAppearance.Invoke($profileCodec, [object[]]@($existingSnapshot, $selected, $existing))) `
        'An established server character accepted local appearance.'
    Assert-True (Test-ByteArrayEqual $inner.Bytes ($playerDataField.GetValue($existing))) `
        'An established server character was modified.'
}
$worldDataField = $deserializeProfile.ReturnType.GetField('m_worldData', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$worldData = $worldDataField.GetValue($selected)
$worldValueType = $worldDataField.FieldType.GetGenericArguments()[1]
$worldData.Add([long]123, [Activator]::CreateInstance($worldValueType))
Assert-True (-not $preserveAppearance.Invoke($profileCodec, [object[]]@($freshSnapshot, $selected, $managed))) `
    'A used local character was allowed to supply initial appearance.'
$worldData.Clear()

$badAppearance = New-InnerPlayerDataFixture $replacementInventory -Colors @([single]::NaN, 0, 0, 0, 0, 0)
$playerDataField.SetValue($selected, $badAppearance.Bytes)
$null = Assert-ThrowsLike {
    $preserveAppearance.Invoke($profileCodec, [object[]]@($freshSnapshot, $selected, $managed))
} '*skin*' 'Non-finite initial appearance was accepted.'
Assert-True (Test-ByteArrayEqual $mergedAppearance ($playerDataField.GetValue($managed))) `
    'Rejected appearance partially modified the server profile.'
Write-Host 'Initial appearance: cosmetics-only merge, full save round trip, existing/used character guards and invalid-input isolation passed.'

$inventoryKind = [Enum]::Parse(
    $envelopeKindType,
    "InventorySaveRequest")
$saveRequestKind = [Enum]::Parse(
    $envelopeKindType,
    "SaveRequest")
$snapshotKind = [Enum]::Parse(
    $envelopeKindType,
    "Snapshot")
Assert-True ([int]$inventoryKind -eq 5) `
    "InventorySaveRequest no longer uses network message kind 5."

$sessionId = [Guid]::NewGuid()
$createdUtc = [DateTime]::SpecifyKind(
    [DateTime]::UtcNow,
    [DateTimeKind]::Utc)

# Exercise only the envelope's size layer here. These opaque synthetic bytes
# are deliberately not a valid PlayerProfile; the schema rejection below
# proves that successful envelope decoding is not character-state admission.
[byte[]]$boundaryPayload = [byte[]]::new($options.MaxPayloadBytes)
$boundaryPayload[0] = 0x53
$boundaryPayload[[int]($boundaryPayload.Length / 2)] = 0xA5
$boundaryPayload[$boundaryPayload.Length - 1] = 0x7E
[byte[]]$overBoundaryPayload = [byte[]]::new($options.MaxPayloadBytes + 1)
[Array]::Copy($boundaryPayload, $overBoundaryPayload, $boundaryPayload.Length)
$overBoundaryPayload[$overBoundaryPayload.Length - 1] = 0xCC
$relaxedEnvelopeOptions = [Activator]::CreateInstance($storageOptionsType)
$relaxedEnvelopeOptions.MaxPayloadBytes = $options.MaxPayloadBytes + 1
# Keep the same outer-envelope allowance. Thus strict decoding must reject
# the oversized payload itself, not an oversized or otherwise malformed frame.
$relaxedEnvelopeCodec = [Activator]::CreateInstance(
    $envelopeCodecType,
    [object[]]@($relaxedEnvelopeOptions))
foreach ($boundaryKind in @($snapshotKind, $saveRequestKind)) {
    $boundaryArguments = [object[]]@(
        $boundaryKind,
        [long]2,
        [long]1,
        $sessionId,
        $identity,
        $createdUtc,
        [int]46,
        $boundaryPayload)
    $boundaryEnvelope = $createEnvelope.Invoke($null, $boundaryArguments)
    # Direct reflection retains the returned byte[] as one object instead of
    # emitting ten million byte objects through a PowerShell function pipeline.
    [byte[]]$boundaryEncoded = $encodeEnvelope.Invoke(
        $envelopeCodec, [object[]]@($boundaryEnvelope))
    $boundaryDecoded = Invoke-OneArgument $decodeEnvelope $envelopeCodec $boundaryEncoded
    Assert-True ($boundaryEncoded.Length -gt $options.MaxPayloadBytes -and
        $boundaryEncoded.Length -le $options.MaxEnvelopeBytes -and
        $boundaryDecoded.PayloadLength -eq $options.MaxPayloadBytes -and
        $boundaryDecoded.Kind -eq $boundaryKind -and
        $boundaryDecoded.Revision -eq 2 -and
        $boundaryDecoded.BaseRevision -eq 1 -and
        $boundaryDecoded.SessionId -eq $sessionId -and
        $boundaryDecoded.AccountId -ceq $identity.AccountId -and
        $boundaryDecoded.CharacterName -ceq $identity.CharacterName -and
        (Test-ByteArrayEqual $boundaryDecoded.GetPayloadSha256Copy() `
            $boundaryEnvelope.GetPayloadSha256Copy())) `
        "An exact 10 MiB $boundaryKind payload did not round-trip through the envelope size layer."

    $overBoundaryArguments = [object[]]$boundaryArguments.Clone()
    $overBoundaryArguments[7] = $overBoundaryPayload
    $overBoundaryEnvelope = $createEnvelope.Invoke($null, $overBoundaryArguments)
    $overBoundaryEncodeError = Assert-ThrowsLike `
        { Invoke-OneArgument $encodeEnvelope $envelopeCodec $overBoundaryEnvelope } `
        "*character payload exceeds the configured limit*" `
        "Encoding accepted a 10 MiB + 1 byte $boundaryKind payload."
    Assert-True ($overBoundaryEncodeError.GetType() -eq $protocolExceptionType) `
        "An oversized $boundaryKind encode did not raise CharacterProtocolException."

    [byte[]]$overBoundaryEncoded = $encodeEnvelope.Invoke(
        $relaxedEnvelopeCodec, [object[]]@($overBoundaryEnvelope))
    Assert-True ($overBoundaryEncoded.Length -le $options.MaxEnvelopeBytes) `
        "The oversized-payload decode fixture accidentally exceeded the outer envelope cap."
    $overBoundaryDecodeError = Assert-ThrowsLike `
        { Invoke-OneArgument $decodeEnvelope $envelopeCodec $overBoundaryEncoded } `
        "*character payload exceeds the configured limit*" `
        "Decoding accepted a well-formed 10 MiB + 1 byte $boundaryKind payload."
    Assert-True ($overBoundaryDecodeError.GetType() -eq $protocolExceptionType) `
        "An oversized $boundaryKind decode did not raise CharacterProtocolException."
}
$syntheticSchemaError = Assert-ThrowsLike `
    { Invoke-TwoArguments $validateSnapshot $profileCodec $identity $boundaryPayload } `
    "*raw PlayerProfile version is unsupported*" `
    "Synthetic size-test bytes were incorrectly admitted as a valid PlayerProfile."
Assert-True ($syntheticSchemaError.GetType() -eq $protocolExceptionType) `
    "Synthetic profile bytes did not retain the separate structural validation failure."

$createInventoryArguments = [object[]]::new(8)
$createInventoryArguments[0] = $inventoryKind
$createInventoryArguments[1] = [long]2
$createInventoryArguments[2] = [long]1
$createInventoryArguments[3] = $sessionId
$createInventoryArguments[4] = $identity
$createInventoryArguments[5] = $createdUtc
$createInventoryArguments[6] = [int]46
$createInventoryArguments[7] = $replacementInventory
$inventoryEnvelope = $createEnvelope.Invoke(
    $null,
    $createInventoryArguments)
[byte[]]$encodedInventory = Invoke-OneArgument `
    $encodeEnvelope `
    $envelopeCodec `
    $inventoryEnvelope
$decodedInventory = Invoke-OneArgument `
    $decodeEnvelope `
    $envelopeCodec `
    $encodedInventory
Assert-True (
    [BitConverter]::ToInt32($encodedInventory, 4) -eq 2 -and
    $decodedInventory.Kind.ToString() -eq "InventorySaveRequest" -and
    $decodedInventory.Revision -eq 2 -and
    $decodedInventory.BaseRevision -eq 1 -and
    $decodedInventory.SessionId -eq $sessionId -and
    (Test-ByteArrayEqual `
        ([byte[]]$decodedInventory.GetPayloadCopy()) `
        $replacementInventory)) `
    "InventorySaveRequest kind 5 did not round-trip through envelope v2."

$badRevisionArguments = [object[]]$createInventoryArguments.Clone()
$badRevisionArguments[1] = [long]3
$badRevisionEnvelope = $createEnvelope.Invoke(
    $null,
    $badRevisionArguments)
$badRevisionError = Assert-ThrowsLike `
    { Invoke-OneArgument `
        $encodeEnvelope `
        $envelopeCodec `
        $badRevisionEnvelope } `
    "*exactly baseRevision + 1*" `
    "An inventory request skipped the unified revision stream."
Assert-True ($badRevisionError.GetType() -eq $protocolExceptionType) `
    "A skipped inventory revision did not fail as a protocol error."

$shortPayloadArguments = [object[]]$createInventoryArguments.Clone()
$shortPayloadArguments[7] = [byte[]]::new(5)
$shortPayloadEnvelope = $createEnvelope.Invoke(
    $null,
    $shortPayloadArguments)
Assert-ThrowsLike `
    { Invoke-OneArgument `
        $encodeEnvelope `
        $envelopeCodec `
        $shortPayloadEnvelope } `
    "*invalid length*" `
    "An undersized inventory request entered the wire protocol." | Out-Null

$emptyInventoryArguments = [object[]]$createInventoryArguments.Clone()
$emptyInventoryArguments[7] = [byte[]]([BitConverter]::GetBytes([int]109) + [BitConverter]::GetBytes([uint16]0))
$emptyInventoryEnvelope = $createEnvelope.Invoke($null, $emptyInventoryArguments)
[byte[]]$emptyInventoryWire = Invoke-OneArgument $encodeEnvelope $envelopeCodec $emptyInventoryEnvelope
$emptyInventoryDecoded = Invoke-OneArgument $decodeEnvelope $envelopeCodec $emptyInventoryWire
Assert-True (Test-ByteArrayEqual ($emptyInventoryDecoded.GetPayloadCopy()) $emptyInventoryArguments[7]) `
    "A valid empty inventory could not clear the previous six-byte-header inventory snapshot."

$oversizedPayloadArguments = [object[]]$createInventoryArguments.Clone()
$oversizedPayloadArguments[7] = $oversizedInventory
$oversizedPayloadEnvelope = $createEnvelope.Invoke(
    $null,
    $oversizedPayloadArguments)
Assert-ThrowsLike `
    { Invoke-OneArgument `
        $encodeEnvelope `
        $envelopeCodec `
        $oversizedPayloadEnvelope } `
    "*invalid length*" `
    "An inventory request larger than 1 MiB entered the wire protocol." | Out-Null

# The server converts kind 5 into an ordinary full SaveRequest at the same
# revision before its live shadow advances. The checkpoint format therefore
# remains the existing full Snapshot envelope rather than an inventory delta.
$admissionArguments = [object[]]::new(8)
$admissionArguments[0] = $saveRequestKind
$admissionArguments[1] = [long]$decodedInventory.Revision
$admissionArguments[2] = [long]$decodedInventory.BaseRevision
$admissionArguments[3] = $decodedInventory.SessionId
$admissionArguments[4] = $identity
$admissionArguments[5] = $decodedInventory.CreatedUtc
$admissionArguments[6] = [int]46
$admissionArguments[7] = $materialized
$admissionEnvelope = $createEnvelope.Invoke(
    $null,
    $admissionArguments)
$checkpointArguments = [object[]]::new(8)
$checkpointArguments[0] = $snapshotKind
$checkpointArguments[1] = [long]$admissionEnvelope.Revision
$checkpointArguments[2] = [long]$admissionEnvelope.BaseRevision
$checkpointArguments[3] = $sessionId
$checkpointArguments[4] = $identity
$checkpointArguments[5] = $createdUtc
$checkpointArguments[6] = [int]46
$checkpointArguments[7] = $admissionEnvelope.GetPayloadCopy()
$checkpointEnvelope = $createEnvelope.Invoke(
    $null,
    $checkpointArguments)
[byte[]]$encodedCheckpoint = Invoke-OneArgument `
    $encodeEnvelope `
    $envelopeCodec `
    $checkpointEnvelope
$decodedCheckpoint = Invoke-OneArgument `
    $decodeEnvelope `
    $envelopeCodec `
    $encodedCheckpoint
Assert-True (
    $admissionEnvelope.Kind.ToString() -eq "SaveRequest" -and
    $admissionEnvelope.Revision -eq $decodedInventory.Revision -and
    $decodedCheckpoint.Kind.ToString() -eq "Snapshot" -and
    $decodedCheckpoint.Revision -eq $decodedInventory.Revision -and
    (Test-ByteArrayEqual `
        ([byte[]]$decodedCheckpoint.GetPayloadCopy()) `
        $materialized) -and
    -not (Test-ByteArrayEqual `
        ([byte[]]$decodedCheckpoint.GetPayloadCopy()) `
        $replacementInventory)) `
    "Inventory acceptance did not materialize an ordinary full checkpoint at the unified revision."
Invoke-TwoArguments `
    $validateSnapshot `
    $profileCodec `
    $identity `
    ([byte[]]$decodedCheckpoint.GetPayloadCopy()) | Out-Null

# Exercise actual outer world records, separate from item WorldLevel above.
# Changing the original profile fields models the result of capture at B; this
# standalone CLR test does not execute Unity transforms or SaveLogoutPoint.
$worldFields = @{}
foreach ($fieldName in @('m_haveCustomSpawnPoint', 'm_spawnPoint',
    'm_haveLogoutPoint', 'm_logoutPoint', 'm_haveDeathPoint', 'm_deathPoint',
    'm_homePoint', 'm_mapData')) {
    $worldFields[$fieldName] = $worldValueType.GetField($fieldName,
        [Reflection.BindingFlags]'Instance,NonPublic,Public')
    Assert-True ($null -ne $worldFields[$fieldName]) "World profile field changed: $fieldName"
}
$vanillaFileCodec = $plugin.GetType('ServerManager.VanillaCharacterFileCodec', $true)
$encodeNativeFile = $vanillaFileCodec.GetMethod('Encode', [Reflection.BindingFlags]'Static,NonPublic')
$decodeNativeFile = $vanillaFileCodec.GetMethod('Decode',
    [Reflection.BindingFlags]'Static,NonPublic', $null, [Type[]]@([byte[]], [int]), $null)
Assert-True ($null -ne $encodeNativeFile -and $null -ne $decodeNativeFile) `
    'The native .fch wrapper codec seam changed.'

foreach ($capturedHaveLogoutPoint in @($true, $false)) {
    # Reverse-sorted IDs also verify that canonical serialization keeps each
    # world's coordinates associated with its ID when it reorders records.
    $coordinateWorlds = @(
        [pscustomobject]@{
            WorldId = [long]2101; HaveCustomSpawnPoint = $true
            SpawnPoint = @(11.25, 22.5, -33.75)
            HaveLogoutPoint = -not $capturedHaveLogoutPoint
            LogoutPoint = @(100.25, 40.5, -200.75) # A: previous full snapshot
            HaveDeathPoint = $true; DeathPoint = @(-41.25, 52.5, 63.75)
            HomePoint = @(71.25, 82.5, -93.75); MapData = [byte[]]@(1, 3, 5)
        },
        [pscustomobject]@{
            WorldId = [long]-9002; HaveCustomSpawnPoint = $false
            SpawnPoint = @(-111.25, 122.5, 133.75)
            HaveLogoutPoint = $true; LogoutPoint = @(-301.25, 62.5, 403.75)
            HaveDeathPoint = $false; DeathPoint = @(141.25, -152.5, 163.75)
            HomePoint = @(-171.25, 182.5, 193.75); MapData = $null
        })
    [byte[]]$coordinateBase = New-PlayerProfilePayload -CharacterName 'FastPathHero' `
        -PlayerId ([long]76561198000000001) -PlayerData $inner.Bytes -WorldData $coordinateWorlds
    $previousCoordinateProfile = $deserializeProfile.Invoke($profileCodec,
        [object[]]@($coordinateBase, $null, $localSource))
    $capturedCoordinateProfile = $deserializeProfile.Invoke($profileCodec,
        [object[]]@($coordinateBase, $null, $localSource))
    $capturedWorld = $worldDataField.GetValue($capturedCoordinateProfile)[[long]2101]
    $capturedPoint = [single[]]@(504.25, 86.5, -607.75) # B: next full snapshot
    $worldFields['m_logoutPoint'].SetValue($capturedWorld,
        [Activator]::CreateInstance($worldFields['m_logoutPoint'].FieldType,
            [object[]]@($capturedPoint[0], $capturedPoint[1], $capturedPoint[2])))
    $worldFields['m_haveLogoutPoint'].SetValue($capturedWorld, $capturedHaveLogoutPoint)
    $capturedWorldExpected = $coordinateWorlds[0].PSObject.Copy()
    $capturedWorldExpected.LogoutPoint = $capturedPoint
    $capturedWorldExpected.HaveLogoutPoint = $capturedHaveLogoutPoint
    $capturedWorldsExpected = @($capturedWorldExpected, $coordinateWorlds[1])

    [byte[]]$coordinateSnapshot = $serializeProfile.Invoke($profileCodec,
        [object[]]@($capturedCoordinateProfile))
    [byte[]]$coordinateSnapshotBefore = $coordinateSnapshot.Clone()
    $coordinateRoundTrip = $deserializeProfile.Invoke($profileCodec,
        [object[]]@($coordinateSnapshot, $null, $localSource))
    $coordinateReplaceArguments = [object[]]@($identity, $coordinateSnapshot, $replacementInventory, $null)
    [byte[]]$coordinateSplice = $replaceInventory.Invoke($profileCodec, $coordinateReplaceArguments)
    $coordinateSpliceProfile = $deserializeProfile.Invoke($profileCodec,
        [object[]]@($coordinateSplice, $null, $localSource))
    Assert-True ((Test-ByteArrayEqual $coordinateSnapshot $coordinateSnapshotBefore) -and
        (Test-ByteRangeEqual $coordinateSnapshot 0 $coordinateSplice 0 `
            ($coordinateSnapshot.Length - $inner.Bytes.Length - 4))) `
        'Inventory-only materialization changed the captured world-data prefix or its input.'
    [byte[]]$coordinatePlayerData = $playerDataField.GetValue($coordinateSpliceProfile)
    Assert-True (Test-ByteRangeEqual $coordinatePlayerData $inner.InventoryOffset `
        $replacementInventory 0 $replacementInventory.Length) `
        'The coordinate fixture did not actually materialize the replacement inventory.'

    # A reconnect Snapshot carries the full materialized profile, not a delta.
    # This is an envelope/selected-payload round trip, not a live reconnect.
    $coordinateCheckpoint = $createEnvelope.Invoke($null, [object[]]@(
        $snapshotKind, [long]3, [long]2, $sessionId, $identity, $createdUtc, [int]46, $coordinateSplice))
    [byte[]]$coordinateCheckpointWire = $encodeEnvelope.Invoke($envelopeCodec,
        [object[]]@($coordinateCheckpoint))
    $coordinateCheckpointDecoded = Invoke-OneArgument $decodeEnvelope $envelopeCodec $coordinateCheckpointWire
    [byte[]]$reconnectPayload = $coordinateCheckpointDecoded.GetPayloadCopy()
    Assert-True (Test-ByteArrayEqual $coordinateSplice $reconnectPayload) `
        'The reconnect Snapshot did not select the full profile containing B.'
    [byte[]]$coordinateNativeFile = $encodeNativeFile.Invoke($null,
        [object[]]@($reconnectPayload, [int]$options.MaxPayloadBytes))
    [byte[]]$coordinateNativePayload = $decodeNativeFile.Invoke($null,
        [object[]]@($coordinateNativeFile, [int]$options.MaxPayloadBytes))
    Assert-True (Test-ByteArrayEqual $reconnectPayload $coordinateNativePayload) `
        'The native .fch wrapper changed the selected full profile.'
    $coordinateNativeProfile = $deserializeProfile.Invoke($profileCodec,
        [object[]]@($coordinateNativePayload, $null, $localSource))

    foreach ($phase in @(
        @{ Name = 'previous A'; Profile = $previousCoordinateProfile; Worlds = $coordinateWorlds },
        @{ Name = 'serialized B'; Profile = $coordinateRoundTrip; Worlds = $capturedWorldsExpected },
        @{ Name = 'inventory splice'; Profile = $coordinateSpliceProfile; Worlds = $capturedWorldsExpected },
        @{ Name = 'reconnect/native .fch'; Profile = $coordinateNativeProfile; Worlds = $capturedWorldsExpected })) {
        $actualWorlds = $worldDataField.GetValue($phase.Profile)
        Assert-True ($actualWorlds.Count -eq 2) "$($phase.Name) lost a world profile."
        foreach ($expectedWorld in $phase.Worlds) {
            Assert-True ($actualWorlds.ContainsKey([long]$expectedWorld.WorldId)) `
                "$($phase.Name) lost world ID $($expectedWorld.WorldId)."
            $actualWorld = $actualWorlds[[long]$expectedWorld.WorldId]
            foreach ($flagName in @('HaveCustomSpawnPoint', 'HaveLogoutPoint', 'HaveDeathPoint')) {
                $fieldName = 'm_' + [char]::ToLowerInvariant($flagName[0]) + $flagName.Substring(1)
                Assert-True ($worldFields[$fieldName].GetValue($actualWorld) -eq $expectedWorld.$flagName) `
                    "$($phase.Name) changed $flagName for world $($expectedWorld.WorldId)."
            }
            foreach ($pointName in @('SpawnPoint', 'LogoutPoint', 'DeathPoint', 'HomePoint')) {
                $fieldName = 'm_' + [char]::ToLowerInvariant($pointName[0]) + $pointName.Substring(1)
                $actualPoint = $worldFields[$fieldName].GetValue($actualWorld)
                $expectedPoint = $expectedWorld.$pointName
                Assert-True ($actualPoint.x -eq [single]$expectedPoint[0] -and
                    $actualPoint.y -eq [single]$expectedPoint[1] -and
                    $actualPoint.z -eq [single]$expectedPoint[2]) `
                    "$($phase.Name) changed $pointName for world $($expectedWorld.WorldId)."
            }
            $actualMapData = $worldFields['m_mapData'].GetValue($actualWorld)
            Assert-True (($null -eq $actualMapData -and $null -eq $expectedWorld.MapData) -or
                (Test-ByteArrayEqual $actualMapData $expectedWorld.MapData)) `
                "$($phase.Name) changed map data for world $($expectedWorld.WorldId)."
        }
    }
}
Write-Output ('World-coordinate payloads: A-to-B capture result, true/false logout flags, two-world isolation, ' +
    'inventory splice, Snapshot and native .fch round trips passed (no live Unity/reconnect execution).')

Write-Output (
    "Exact 10 MiB full-envelope admission with 16 KiB overhead, 10 MiB + 1 rejection, " +
    "inventory kind 5 envelope v2, unified revision, 1 MiB/trailing-byte " +
    "bounds, full-profile splice/world-level preservation, 0..255 format limits, semantic validation, missing-base " +
    "rejection, and full checkpoint materialization smoke tests passed.")
