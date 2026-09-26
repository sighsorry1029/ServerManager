param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Configuration = "Debug",
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'

$assemblyPath = Join-Path $ProjectRoot (
    'bin\{0}\ServerManager.dll' -f $Configuration)
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "Built ServerManager assembly is missing: $assemblyPath"
}

$assembly = [Reflection.Assembly]::LoadFrom(
    (Resolve-Path -LiteralPath $assemblyPath))

# The real activity formatter's static state references Valheim types. Windows
# PowerShell needs the game's facade, while PowerShell Core already provides it.
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $netstandardPath = Join-Path $GamePath 'valheim_Data\Managed\netstandard.dll'
    [Reflection.Assembly]::LoadFrom($netstandardPath) | Out-Null
}

if ($null -ne $assembly.GetType(
        'ServerManager.PlayerLogging.PlayerTelemetryEventType') -or
    $null -ne $assembly.GetType(
        'ServerManager.PlayerLogging.PlayerTelemetryReliability')) {
    throw 'Legacy structured telemetry enums are still public runtime types.'
}
$writerType = $assembly.GetType(
    'ServerManager.PlayerLogging.PlayerTelemetryLogWriter',
    $true)
$publicTryWrite = @($writerType.GetMethods() | Where-Object {
    $_.Name -eq 'TryWrite' -and $_.IsPublic
})
if ($publicTryWrite.Count -ne 2 -or
    @($publicTryWrite | Where-Object {
        $parameters = $_.GetParameters()
        ($parameters.Count -eq 4 -and
         $parameters[0].ParameterType -eq [string] -and
         $parameters[1].ParameterType -eq [string] -and
         $parameters[2].ParameterType -eq [long] -and
         $parameters[3].ParameterType -eq [string]) -or
        ($parameters.Count -eq 5 -and
         $parameters[0].ParameterType -eq [string] -and
         $parameters[1].ParameterType -eq [string] -and
         $parameters[2].ParameterType -eq [long] -and
         $parameters[3].ParameterType -eq [DateTime] -and
         $parameters[4].ParameterType -eq [string])
    }).Count -ne 2) {
    throw 'PlayerTelemetryLogWriter does not expose only the two plain APIs.'
}
$internalTryWriteBlock = @($writerType.GetMethods(
        [Reflection.BindingFlags]'Instance,NonPublic') | Where-Object {
    $_.Name -eq 'TryWriteBlock' -and $_.IsAssembly
})
if ($internalTryWriteBlock.Count -ne 2 -or
    @($internalTryWriteBlock | Where-Object {
        $parameters = $_.GetParameters()
        ($parameters.Count -eq 5 -and
         $parameters[0].ParameterType -eq [string] -and
         $parameters[1].ParameterType -eq [string] -and
         $parameters[2].ParameterType -eq [long] -and
         $parameters[3].ParameterType -eq [string] -and
         $parameters[4].ParameterType -eq
            [Collections.Generic.IReadOnlyList[string]]) -or
        ($parameters.Count -eq 6 -and
         $parameters[0].ParameterType -eq [string] -and
         $parameters[1].ParameterType -eq [string] -and
         $parameters[2].ParameterType -eq [long] -and
         $parameters[3].ParameterType -eq [DateTime] -and
         $parameters[4].ParameterType -eq [string] -and
         $parameters[5].ParameterType -eq
            [Collections.Generic.IReadOnlyList[string]])
    }).Count -ne 2) {
    throw 'PlayerTelemetryLogWriter does not retain exactly two internal atomic block APIs.'
}
$timedTryWriteBlock = $internalTryWriteBlock | Where-Object {
    $parameters = $_.GetParameters()
    $parameters.Count -eq 6 -and
    $parameters[3].ParameterType -eq [DateTime]
} | Select-Object -First 1
$activityType = $assembly.GetType(
    'ServerManager.PlayerLogging.PlayerActivityRuntime', $true)
$appendInventoryDetail = $activityType.GetMethod(
    'AppendInventoryDetail', [Reflection.BindingFlags]'Static,NonPublic')
$itemType = $assembly.GetType('ServerManager.CharacterSemanticItemState', $true)
$detailedItemConstructor = $itemType.GetConstructors(
    [Reflection.BindingFlags]'Instance,NonPublic') | Where-Object {
    $_.GetParameters().Count -eq 7 -and
    $_.GetParameters()[0].ParameterType -eq [string]
} | Select-Object -First 1
$hashedItemConstructor = $itemType.GetConstructors(
    [Reflection.BindingFlags]'Instance,NonPublic') | Where-Object {
    $_.GetParameters().Count -eq 7 -and
    $_.GetParameters()[0].ParameterType -eq [int]
} | Select-Object -First 1
$bundledYamlType = $assembly.GetType(
    'YamlDotNet.RepresentationModel.YamlStream', $true)
if ($null -eq $appendInventoryDetail -or
    $null -eq $detailedItemConstructor -or
    $null -eq $hashedItemConstructor -or
    $bundledYamlType.Assembly -ne $assembly) {
    throw 'The real inventory formatter or bundled YAML parser is missing.'
}

Add-Type -TypeDefinition @'
using System;

namespace ServerManager.PlayerLogging.Tests
{
    public sealed class BlockingStoppedCallback : IDisposable
    {
        private readonly System.Threading.ManualResetEventSlim _entered =
            new System.Threading.ManualResetEventSlim(false);

        public bool Wait(TimeSpan timeout) { return _entered.Wait(timeout); }

        public void Invoke(object diagnostic)
        {
            object kind = diagnostic.GetType().GetProperty("Kind").GetValue(
                diagnostic,
                null);
            if (string.Equals(
                    Convert.ToString(kind),
                    "Stopped",
                    StringComparison.Ordinal))
            {
                _entered.Set();
                System.Threading.Thread.Sleep(250);
            }
        }

        public void Dispose() { _entered.Dispose(); }
    }
}
'@

function New-WriterOptions {
    param(
        [string]$Root,
        [int]$MaximumRecordBytes = 2048,
        [long]$MaximumFileBytes = 4096,
        [int]$MaximumFilesPerPlayer = 6
    )

    $value =
        [ServerManager.PlayerLogging.PlayerTelemetryLogOptions]::new($Root)
    $value.MaximumQueuedEvents = 256
    $value.MaximumQueuedBytes = 262144
    $value.MaximumRecordBytes = $MaximumRecordBytes
    $value.MaximumBatchEvents = 64
    $value.MaximumBatchBytes = 16384
    $value.MaximumFileBytes = $MaximumFileBytes
    $value.MaximumFilesPerPlayer = $MaximumFilesPerPlayer
    $value.BatchWindow = [TimeSpan]::FromMilliseconds(5)
    $value.ShutdownDrainTimeout = [TimeSpan]::FromSeconds(10)
    return $value
}

function Get-LocalLogName {
    param(
        [DateTime]$Utc,
        [string]$CharacterName,
        [long]$PlayerId
    )

    $local = [TimeZoneInfo]::ConvertTime(
        [DateTimeOffset]::new($Utc),
        [TimeZoneInfo]::Local)
    $normalizedName = $CharacterName.Normalize(
        [Text.NormalizationForm]::FormKC)
    return $normalizedName + '_' +
        $PlayerId.ToString([Globalization.CultureInfo]::InvariantCulture) + '_' +
        $local.ToString(
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture) + '.log'
}

function Get-ExpectedLine {
    param(
        [DateTime]$Utc,
        [string]$Message
    )

    $local = [TimeZoneInfo]::ConvertTime(
        [DateTimeOffset]::new($Utc),
        [TimeZoneInfo]::Local)
    return '[' + $local.ToString(
        'HH:mm:ss',
        [Globalization.CultureInfo]::InvariantCulture) + '] ' + $Message
}

# A completed segment may be compressed by the background worker. Logical
# names remain stable: these helpers read either form without hiding duplicate
# or conflicting source/archive fixtures in the dedicated gzip tests below.
function Test-LogPath {
    param([string]$Path)
    return (Test-Path -LiteralPath $Path) -or
        (Test-Path -LiteralPath ($Path + '.gz'))
}

function Read-LogBytes {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { $Path += '.gz' }
    if (-not $Path.EndsWith('.gz', [StringComparison]::Ordinal)) {
        return ,([IO.File]::ReadAllBytes($Path))
    }
    $inputStream = [IO.File]::OpenRead($Path)
    try {
        $gzip = [IO.Compression.GZipStream]::new(
            $inputStream, [IO.Compression.CompressionMode]::Decompress)
        try {
            $outputStream = [IO.MemoryStream]::new()
            try {
                $gzip.CopyTo($outputStream)
                return ,($outputStream.ToArray())
            }
            finally { $outputStream.Dispose() }
        }
        finally { $gzip.Dispose() }
    }
    finally { $inputStream.Dispose() }
}

function Read-LogText {
    param([string]$Path)
    return [Text.UTF8Encoding]::new($false, $true).GetString((Read-LogBytes $Path))
}

function Write-GzipFixture {
    param([string]$Path, [byte[]]$Bytes)
    $outputStream = [IO.File]::Open(
        $Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $gzip = [IO.Compression.GZipStream]::new(
            $outputStream, [IO.Compression.CompressionMode]::Compress)
        try { $gzip.Write($Bytes, 0, $Bytes.Length) }
        finally { $gzip.Dispose() }
    }
    finally { $outputStream.Dispose() }
}

function Wait-ForArchive {
    param([string]$Path)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path) -and
        $watch.Elapsed -lt [TimeSpan]::FromSeconds(10)) {
        [Threading.Thread]::Sleep(20)
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Background archive was not completed: $Path"
    }
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryRoot `
    ('ServerManager-player-telemetry-smoke-' + [Guid]::NewGuid().ToString('N'))
$playerKey = '76561198000000001'
$characterName = 'halla'
$playerId = [long]770260545
# Keep generic format/rotation fixtures outside the real past-date archive
# sweep. Dedicated archive fixtures below use yesterday/today explicitly.
$primaryDateUtc = [DateTime]::UtcNow.Date.AddDays(30).AddHours(23).AddMinutes(59)
$nextDateUtc = $primaryDateUtc.AddDays(2)
$primaryFileName = Get-LocalLogName $primaryDateUtc $characterName $playerId
$nextFileName = Get-LocalLogName $nextDateUtc $characterName $playerId
$logFilePattern = '^' +
    [regex]::Escape($characterName + '_' + $playerId + '_') +
    '\d{4}-\d{2}-\d{2}\.log(?:\.\d{2,})?(?:\.gz)?$'

try {
    $mainRoot = Join-Path $testRoot 'main'
    $options = New-WriterOptions $mainRoot 2048 2500 4
    $diagnostics = [System.Collections.Generic.List[
        ServerManager.PlayerLogging.PlayerTelemetryDiagnostic]]::new()
    $writer = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    $writer.Initialize(
        $options,
        [Action[ServerManager.PlayerLogging.PlayerTelemetryDiagnostic]] {
            param($diagnostic)
            $diagnostics.Add($diagnostic)
        })

    if (Test-Path -LiteralPath $mainRoot) {
        throw 'Initialize unexpectedly performed filesystem I/O.'
    }

    $writer.Start()
    $injectedUtc = $primaryDateUtc.AddSeconds(100)
    $injectedMessage = "[1, 2, 3]`tInjected line" +
        ([string][char]1) + ' tail'
    for ($index = 0; $index -lt 80; ++$index) {
        if (-not $writer.TryWrite(
                $playerKey,
                $characterName,
                $playerId,
                $primaryDateUtc.AddSeconds($index + 1),
                ('[1, 2, 3] Position sample ' + $index +
                 ' ' + ('z' * 40)))) {
            throw "A valid record was rejected at index $index."
        }
    }
    if (-not $writer.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            $injectedUtc,
            $injectedMessage)) {
        throw 'A valid plain-text record was rejected.'
    }

    if (-not $writer.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            $nextDateUtc,
            ('Oversized message ' + ('q' * 5000)))) {
        throw 'A safely truncatable message was rejected.'
    }
    if ($writer.TryWrite(
            'not-a-steam-id', $characterName, $playerId,
            $primaryDateUtc, 'invalid') -or
        $writer.TryWrite(
            $playerKey, $characterName, $playerId, $primaryDateUtc, '') -or
        $writer.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            [DateTime]::SpecifyKind(
                $primaryDateUtc,
                [DateTimeKind]::Unspecified),
            'unspecified timestamp') -or
        $writer.TryWrite(
            $playerKey, '   ', $playerId, $primaryDateUtc, 'blank name') -or
        $writer.TryWrite(
            $playerKey, $characterName, 0, $primaryDateUtc, 'zero player ID')) {
        throw 'An invalid player log record was accepted.'
    }
    if (-not $writer.Stop([TimeSpan]::FromSeconds(10))) {
        throw 'The primary writer did not drain.'
    }

    $playerDirectory = Join-Path $mainRoot $playerKey
    $files = @(Get-ChildItem -LiteralPath $playerDirectory -File |
        Where-Object {
            $_.Name -match $logFilePattern
        })
    if ($files.Count -lt 2 -or $files.Count -gt 4) {
        throw 'Plain log rotation/retention produced the wrong file count.'
    }
    if ($files.Name -notcontains $primaryFileName -or
        $files.Name -notcontains $nextFileName) {
        throw 'Records were not assigned to server-local .log date files.'
    }

    $allLines = @()
    foreach ($file in $files) {
        $bytes = Read-LogBytes $file.FullName
        if ($bytes.Length -ge 3 -and
            $bytes[0] -eq 0xef -and
            $bytes[1] -eq 0xbb -and
            $bytes[2] -eq 0xbf) {
            throw 'A player log file unexpectedly contains a UTF-8 BOM.'
        }
        $allLines += @(([Text.Encoding]::UTF8.GetString($bytes)).Split(
            [char[]]@([char]10), [StringSplitOptions]::RemoveEmptyEntries))
    }
    if ($allLines.Count -lt 2) {
        throw 'The primary writer did not retain readable plain-text records.'
    }
    foreach ($line in $allLines) {
        if ($line -notmatch '^\[\d{2}:\d{2}:\d{2}\] .+') {
            throw "A record does not use the required plain format: $line"
        }
        if ($line -match '^\[\d{4}-\d{2}-\d{2} ') {
            throw 'A record repeats the date already carried by its filename.'
        }
        if ($line.Contains("`t") -or
            $line.IndexOf([char]1) -ge 0 -or
            $line.StartsWith('{')) {
            throw 'A record retained control characters or JSON framing.'
        }
        if ([Text.Encoding]::UTF8.GetByteCount($line + "`n") -gt
            $options.MaximumRecordBytes) {
            throw 'A safely truncated record exceeded the byte cap.'
        }
    }
    $expectedSanitized = Get-ExpectedLine `
        $injectedUtc `
        '[1, 2, 3] Injected line  tail'
    if ($allLines -notcontains $expectedSanitized) {
        throw 'CR/LF/tab/control characters were not normalized to spaces.'
    }

    # A multi-line inventory is one queue record. Only its header receives a
    # timestamp; continuation lines remain adjacent and retain safe indentation.
    $blockRoot = Join-Path $testRoot 'atomic-block'
    $blockOptions = New-WriterOptions $blockRoot 8192 32768 4
    $blockOptions.BatchWindow = [TimeSpan]::Zero
    $blockWriter =
        [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    $blockWriter.Initialize($blockOptions)
    $blockWriter.Start()
    $blockUtc = $primaryDateUtc.AddSeconds(200)
    $blockHeader = '[-371, 40, 1591] Inventory:'
    $largeCustomValue = 'v' * 12000
    $customData = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    $customData.Add('', '')
    $customData.Add('Bad.Key: [#] {value} "quoted" \ slash',
        "line`nwith `"quotes`" and \ slash`r`nnext`ttab")
    $customData.Add('Empty.Value', '')
    $customData.Add('Case.Key', 'true')
    $customData.Add('case.key', 'null')
    $customData.Add('Leading.Zero', '001')
    $customData.Add('ServerManager.LargeValue', $largeCustomValue)
    $controlCharacters = -join [char[]]((0..31) + (127..159))
    $customData.Add(('Controls.' + $controlCharacters), $controlCharacters)
    $yamlLineBreaks = -join [char[]]@(0x85, 0x2028, 0x2029)
    $customData.Add(('LineBreaks.' + $yamlLineBreaks),
        ('before' + $yamlLineBreaks + 'after'))
    $unicodeValue = (-join [char[]]@(0xD55C, 0xAE00)) +
        [char]::ConvertFromUtf32(0x1F680)
    $customData.Add('Unicode', $unicodeValue)

    # Use the actual item formatter, not a hand-written copy of its output.
    $generatedLines = [Collections.Generic.List[string]]::new()
    $itemCases = @(
        @('ShieldWood', 1, 2, $customData, '  - ShieldWood Q2'),
        @('Bow', 1, 1, [Collections.Generic.Dictionary[string, string]]::new(),
            '  - Bow'),
        @('Wood', 50, 1, [Collections.Generic.Dictionary[string, string]]::new(),
            '  - Wood x50'),
        @('FineWood', 3, 4, [Collections.Generic.Dictionary[string, string]]::new(),
            '  - FineWood x3 Q4'))
    foreach ($itemCase in $itemCases) {
        $item = $detailedItemConstructor.Invoke([object[]]@(
            $itemCase[0], [int]$itemCase[1], [int]$itemCase[2],
            [int]0, [int]0, [int]0, $itemCase[3]))
        $appendInventoryDetail.Invoke($null, [object[]]@(
            $generatedLines, $item)) | Out-Null
    }
    [string[]]$blockLines = $generatedLines.ToArray()
    $itemHeaders = @($blockLines | Where-Object { $_.StartsWith('  - ') })
    if ($itemHeaders.Count -ne $itemCases.Count) {
        throw 'The inventory formatter lost or duplicated an item header.'
    }
    for ($index = 0; $index -lt $itemCases.Count; ++$index) {
        if ($itemHeaders[$index] -cne $itemCases[$index][4]) {
            throw 'Inventory headers must omit x1/Q1 and retain larger stacks/qualities.'
        }
    }
    $unknownLines = [Collections.Generic.List[string]]::new()
    $unknownItem = $hashedItemConstructor.Invoke([object[]]@(
        [int]0x12345678, [int]1, [int]1, [int]0, [int]0, [int]0,
        [Collections.Generic.Dictionary[string, string]]::new()))
    $appendInventoryDetail.Invoke($null, [object[]]@(
        $unknownLines, $unknownItem)) | Out-Null
    if ($unknownLines.Count -ne 1 -or
        $unknownLines[0] -cne '  - unknown:12345678') {
        throw 'An unresolved inventory hash did not retain its stable unknown-item identity.'
    }
    if (@($blockLines | Where-Object { $_ -ceq '    CustomData:' }).Count -ne 1) {
        throw 'CustomData casing changed or an empty item dictionary produced a block.'
    }
    foreach ($escape in @('\u0085', '\u2028', '\u2029')) {
        if (-not ($blockLines -join "`n").Contains($escape)) {
            throw "Inventory custom-data did not escape YAML line break $escape."
        }
    }
    if (-not $blockWriter.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            $blockUtc.AddSeconds(-1),
            '[0, 0, 0] before block') -or
        -not ([bool]$timedTryWriteBlock.Invoke(
            $blockWriter,
            [object[]]@(
                $playerKey,
                $characterName,
                $playerId,
                $blockUtc,
                $blockHeader,
                $blockLines))) -or
        -not $blockWriter.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            $blockUtc.AddSeconds(1),
            '[1, 1, 1] after block') -or
        -not $blockWriter.Stop([TimeSpan]::FromSeconds(5))) {
        throw 'The atomic inventory-block writer failed.'
    }
    $blockPath = Join-Path (
        Join-Path $blockRoot $playerKey) (
            Get-LocalLogName $blockUtc $characterName $playerId)
    $blockContent = [IO.File]::ReadAllText($blockPath)
    $expectedBlock = (Get-ExpectedLine $blockUtc $blockHeader) + "`n" +
        ($blockLines -join "`n") + "`n"
    if (-not $blockContent.Contains($expectedBlock)) {
        throw 'Inventory continuation lines were timestamped, split, interleaved, or truncated.'
    }
    if ($blockContent.Contains('SHA-256') -or
        $blockContent.Contains('CustomDataSha256')) {
        throw 'The readable inventory block exposed a custom-data hash.'
    }

    # Copy the persisted item block and replace only its item heading with a
    # restoration entry. Original CustomData indentation, strings and escapes
    # must load with the YAML parser bundled in the shipping plugin.
    $persistedLines = $blockContent.Split([char]10)
    $itemStart = [Array]::IndexOf($persistedLines, '  - ShieldWood Q2')
    $itemEnd = [Array]::IndexOf($persistedLines, '  - Bow')
    if ($itemStart -lt 0 -or $itemEnd -le $itemStart + 1) {
        throw 'Could not isolate the persisted custom-data item for YAML copying.'
    }
    $restorationYaml = '  - id: restoringnow' + "`n" +
        ($persistedLines[($itemStart + 1)..($itemEnd - 1)] -join "`n") + "`n"
    $bundledYaml = [Activator]::CreateInstance($bundledYamlType, $true)
    $yamlReader = [IO.StringReader]::new($restorationYaml)
    try { $bundledYaml.Load($yamlReader) }
    finally { $yamlReader.Dispose() }
    if ($bundledYaml.Documents.Count -ne 1 -or
        $bundledYaml.Documents[0].RootNode.Children.Count -ne 1) {
        throw 'The copied log fragment did not parse as one restoration entry.'
    }
    $restorationEntry = $bundledYaml.Documents[0].RootNode.Children[0]
    $restorationFields = @($restorationEntry.Children.GetEnumerator())
    $idField = @($restorationFields | Where-Object { $_.Key.Value -ceq 'id' })
    $customDataField = @($restorationFields | Where-Object {
        $_.Key.Value -ceq 'CustomData'
    })
    if ($restorationFields.Count -ne 2 -or $idField.Count -ne 1 -or
        $idField[0].Value.Value -cne 'restoringnow' -or
        $customDataField.Count -ne 1 -or
        $customDataField[0].Value.Children.Count -ne $customData.Count) {
        throw 'Copied inventory YAML lost its id or exact CustomData mapping.'
    }
    foreach ($entry in $customDataField[0].Value.Children.GetEnumerator()) {
        $key = [string]$entry.Key.Value
        $value = [string]$entry.Value.Value
        if (-not $customData.ContainsKey($key) -or
            -not [string]::Equals($customData[$key], $value,
                [StringComparison]::Ordinal)) {
            throw 'Copied log CustomData did not round-trip its exact key/value strings.'
        }
    }

    # One authenticated Steam directory can contain independent character
    # streams. A reconnect appends to the same character/player-ID/date file.
    $streamsRoot = Join-Path $testRoot 'character-streams'
    $streamsOptions = New-WriterOptions $streamsRoot 2048 32768 10
    $streamsOptions.BatchWindow = [TimeSpan]::Zero
    $streamsWriter =
        [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    $streamsWriter.Initialize($streamsOptions)
    $streamsWriter.Start()
    $otherPlayerId = [long]770260546
    $koreanPlayerId = [long]770260547
    $unsafePlayerId = [long]770260548
    $upperPlayerId = [long]770260549
    $koreanCharacterName = -join ([char[]]@(
        0xB2E4,
        0xB8FD,
        0xC774))
    $compatibilityCharacterName = -join ([char[]]@(
        0xFF48,
        0xFF41,
        0xFF4C,
        0xFF4C,
        0xFF41))
    if (-not $streamsWriter.TryWrite(
            $playerKey, $characterName, $playerId,
            $primaryDateUtc, '[0, 0, 0] first session') -or
        -not $streamsWriter.TryWrite(
            $playerKey, $characterName, $playerId,
            $primaryDateUtc.AddSeconds(1), '[1, 0, 0] first session continued') -or
        -not $streamsWriter.TryWrite(
            $playerKey, $characterName, $otherPlayerId,
            $primaryDateUtc, '[0, 1, 0] cloned-name profile') -or
        -not $streamsWriter.TryWrite(
            $playerKey, $koreanCharacterName, $koreanPlayerId,
            $primaryDateUtc, '[0, 0, 1] Korean name') -or
        -not $streamsWriter.TryWrite(
            $playerKey, 'hall/a', $unsafePlayerId,
            $primaryDateUtc, '[1, 1, 1] sanitized filename') -or
        -not $streamsWriter.TryWrite(
            $playerKey, 'hall\a', $unsafePlayerId,
            $primaryDateUtc, '[2, 2, 2] same sanitized filename') -or
        -not $streamsWriter.TryWrite(
            $playerKey, 'Halla', $upperPlayerId,
            $primaryDateUtc, '[3, 3, 3] uppercase filename') -or
        -not $streamsWriter.TryWrite(
            $playerKey, $compatibilityCharacterName, $playerId,
            $primaryDateUtc, '[4, 4, 4] same normalized filename') -or
        -not $streamsWriter.Stop([TimeSpan]::FromSeconds(5))) {
        throw 'Per-character stream routing rejected a valid record.'
    }
    $streamsDirectory = Join-Path $streamsRoot $playerKey
    $expectedStreamFiles = @(
        Get-LocalLogName $primaryDateUtc $characterName $playerId
        Get-LocalLogName $primaryDateUtc $characterName $otherPlayerId
        Get-LocalLogName $primaryDateUtc $koreanCharacterName $koreanPlayerId
        Get-LocalLogName $primaryDateUtc 'hall_a' $unsafePlayerId
        Get-LocalLogName $primaryDateUtc 'Halla' $upperPlayerId)
    $actualStreamFiles = @(Get-ChildItem -LiteralPath $streamsDirectory -File)
    if ($actualStreamFiles.Count -ne $expectedStreamFiles.Count) {
        throw 'Simplified character stream routing created unexpected files.'
    }
    foreach ($name in $expectedStreamFiles) {
        if ($actualStreamFiles.Name -cnotcontains $name) {
            throw "Missing expected character stream file: $name"
        }
    }
    $primaryStreamPath = Join-Path $streamsDirectory $expectedStreamFiles[0]
    if (@(Get-Content -LiteralPath $primaryStreamPath).Count -ne 3 -or
        @(Get-Content -LiteralPath (
            Join-Path $streamsDirectory $expectedStreamFiles[3])).Count -ne 2) {
        throw 'Equivalent normalized or sanitized names did not share one player-ID stream.'
    }

    $reconnectWriter =
        [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    $reconnectWriter.Initialize($streamsOptions)
    $reconnectWriter.Start()
    if (-not $reconnectWriter.TryWrite(
            $playerKey, $characterName, $playerId,
            $primaryDateUtc.AddSeconds(2), '[2, 0, 0] reconnected') -or
        -not $reconnectWriter.Stop([TimeSpan]::FromSeconds(5)) -or
        @(Get-Content -LiteralPath $primaryStreamPath).Count -ne 4) {
        throw 'A reconnect did not append to its existing character stream.'
    }

    # Filename safety remains bounded and deterministic without synthetic
    # suffixes, including valid surrogate pairs and truncated-name collisions.
    $safeNamesRoot = Join-Path $testRoot 'safe-character-names'
    $safeNamesOptions = New-WriterOptions $safeNamesRoot 2048 32768 20
    $safeNamesWriter =
        [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    $safeNamesWriter.Initialize($safeNamesOptions)
    $safeNamesWriter.Start()
    $emoji = [char]0xD83D + [string][char]0xDE00
    $nameCases = @(
        @{ Input = 'tail. '; Expected = 'tail'; PlayerId = [long]9001 }
        @{ Input = ('a<>:"/\|?*~' + [char]1 + "`t" + 'b');
           Expected = ('a' + ('_' * 12) + 'b'); PlayerId = [long]9002 }
        @{ Input = (('n' * 64) + 'a'); Expected = ('n' * 64); PlayerId = [long]9003 }
        @{ Input = (('n' * 64) + 'b'); Expected = ('n' * 64); PlayerId = [long]9003 }
        @{ Input = ('hero' + $emoji); Expected = 'hero__'; PlayerId = [long]9004 }
        @{ Input = (('n' * 63) + $emoji); Expected = (('n' * 63) + '_'); PlayerId = [long]9005 }
        @{ Input = ([string][char]0x00C9 + 'owyn');
           Expected = ([string][char]0x00C9 + 'owyn'); PlayerId = [long]9006 }
        @{ Input = 'halla~123456abcdef'; Expected = 'halla_123456abcdef'; PlayerId = [long]9007 }
        @{ Input = (('n' * 63) + ' .hidden'); Expected = ('n' * 63); PlayerId = [long]9008 })
    foreach ($nameCase in $nameCases) {
        if (-not $safeNamesWriter.TryWrite(
                $playerKey, $nameCase.Input, $nameCase.PlayerId,
                $primaryDateUtc, 'safe filename probe')) {
            throw 'A safely normalizable, sanitizable, or truncatable name was rejected.'
        }
    }
    foreach ($invalidName in @('.', '..', '.  ', [string][char]0xD800)) {
        if ($safeNamesWriter.TryWrite(
                $playerKey, $invalidName, 9099,
                $primaryDateUtc, 'invalid filename probe')) {
            throw 'An empty-after-sanitization or malformed Unicode name was accepted.'
        }
    }
    if (-not $safeNamesWriter.Stop([TimeSpan]::FromSeconds(5))) {
        throw 'The safe-character-name writer did not drain.'
    }
    $safeNamesDirectory = Join-Path $safeNamesRoot $playerKey
    $actualSafeNames = @(Get-ChildItem -LiteralPath $safeNamesDirectory -File)
    $expectedSafeNames = @($nameCases | ForEach-Object {
        Get-LocalLogName $primaryDateUtc $_.Expected $_.PlayerId
    } | Sort-Object -Unique)
    if ($actualSafeNames.Count -ne $expectedSafeNames.Count -or
        @($actualSafeNames | Where-Object { $_.Name.Contains('~') }).Count -ne 0) {
        throw 'Filename safety generated a synthetic suffix or unexpected file.'
    }
    foreach ($name in $expectedSafeNames) {
        if ($actualSafeNames.Name -cnotcontains $name) {
            throw "Missing exact sanitized, suffix-free filename: $name"
        }
    }
    $truncatedStreamPath = Join-Path $safeNamesDirectory (
        Get-LocalLogName $primaryDateUtc ('n' * 64) 9003)
    if (@(Get-Content -LiteralPath $truncatedStreamPath).Count -ne 2) {
        throw 'Names with the same 64-character prefix did not share one player-ID stream.'
    }

    $statistics = $writer.GetStatistics()
    if ($statistics.Accepted -ne 82 -or $statistics.Rejected -ne 5 -or
        $statistics.QueueDrops -ne 0 -or
        $statistics.ByteBudgetDrops -ne 0 -or
        $statistics.WriteFailures -ne 0) {
        throw 'Writer counters did not match accepted and rejected records.'
    }

    # UTC calendar boundaries must be grouped by the actual server-local date.
    $boundaryRoot = Join-Path $testRoot 'utc-local-boundary'
    $boundaryWriter =
        [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    $boundaryWriter.Initialize((New-WriterOptions $boundaryRoot))
    $boundaryAfterUtc = $primaryDateUtc.AddMinutes(2)
    $boundaryLocalInput = $boundaryAfterUtc.ToLocalTime()
    $boundaryWriter.Start()
    if (-not $boundaryWriter.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            $primaryDateUtc,
            'before UTC midnight') -or
        -not $boundaryWriter.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            $boundaryAfterUtc,
            'after UTC midnight') -or
        -not $boundaryWriter.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            $boundaryLocalInput,
            'local DateTime input')) {
        throw 'A UTC/local boundary probe record was rejected.'
    }
    if (-not $boundaryWriter.Stop([TimeSpan]::FromSeconds(5))) {
        throw 'The UTC/local boundary writer did not drain.'
    }
    $expectedBoundaryFiles = @(
        Get-LocalLogName $primaryDateUtc $characterName $playerId
        Get-LocalLogName $boundaryAfterUtc $characterName $playerId
    ) | Sort-Object -Unique
    $actualBoundaryFiles = @(Get-ChildItem -LiteralPath (
        Join-Path $boundaryRoot $playerKey) -File)
    if ($actualBoundaryFiles.Count -ne $expectedBoundaryFiles.Count) {
        throw 'UTC-midnight records were split using the wrong local date.'
    }
    foreach ($name in $expectedBoundaryFiles) {
        if ($actualBoundaryFiles.Name -notcontains $name) {
            throw "Missing expected server-local date file: $name"
        }
    }
    $boundaryLines = @($actualBoundaryFiles | ForEach-Object {
        Get-Content -LiteralPath $_.FullName
    })
    if ($boundaryLines -notcontains (
            Get-ExpectedLine $boundaryAfterUtc 'local DateTime input')) {
        throw 'A Local DateTime was not normalized to the expected instant.'
    }

    # A crash tail is preserved but the next record starts on a fresh line.
    $newlineRoot = Join-Path $testRoot 'newline-repair'
    $newlineOptions = New-WriterOptions $newlineRoot
    $newlineOptions.BatchWindow = [TimeSpan]::Zero
    $newlineWriter =
        [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    $newlineWriter.Initialize($newlineOptions)
    $newlinePlayerDirectory = Join-Path $newlineRoot $playerKey
    [IO.Directory]::CreateDirectory($newlinePlayerDirectory) | Out-Null
    $newlinePath = Join-Path $newlinePlayerDirectory $primaryFileName
    $malformedTail = '[incomplete tail'
    [IO.File]::WriteAllText(
        $newlinePath,
        $malformedTail,
        [Text.UTF8Encoding]::new($false))
    $newlineWriter.Start()
    if (-not $newlineWriter.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            $primaryDateUtc,
            'newline repair probe') -or
        -not $newlineWriter.Stop([TimeSpan]::FromSeconds(5))) {
        throw 'The newline-repair writer failed.'
    }
    $newlineContent = [IO.File]::ReadAllText($newlinePath)
    $expectedRepairLine = Get-ExpectedLine `
        $primaryDateUtc `
        'newline repair probe'
    if (-not $newlineContent.Contains(
            $malformedTail + "`n" + $expectedRepairLine + "`n")) {
        throw 'A partial active-file tail was concatenated to the next record.'
    }

    # Rotation advances monotonically and never fills an old generation gap.
    $rotationRoot = Join-Path $testRoot 'monotonic-rotation'
    $rotationOptions = New-WriterOptions $rotationRoot 512 600 10
    $rotationOptions.BatchWindow = [TimeSpan]::Zero
    $rotationWriter =
        [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    $rotationWriter.Initialize($rotationOptions)
    $rotationPlayerDirectory = Join-Path $rotationRoot $playerKey
    [IO.Directory]::CreateDirectory($rotationPlayerDirectory) | Out-Null
    $rotationActivePath = Join-Path $rotationPlayerDirectory $primaryFileName
    $rotationOnePath = $rotationActivePath + '.01'
    $rotationThreePath = $rotationActivePath + '.03'
    $rotationFourPath = $rotationActivePath + '.04'
    $rotationActiveSeed = ('a' * 590) + "`n"
    [IO.File]::WriteAllText(
        $rotationActivePath,
        $rotationActiveSeed,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $rotationOnePath,
        "segment-one`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $rotationThreePath,
        "segment-three`n",
        [Text.UTF8Encoding]::new($false))
    $otherRotationPath = Join-Path $rotationPlayerDirectory (
        Get-LocalLogName $primaryDateUtc 'other' 991)
    [IO.File]::WriteAllText(
        ($otherRotationPath + '.99'),
        "other-character-segment`n",
        [Text.UTF8Encoding]::new($false))
    $rotationWriter.Start()
    if (-not $rotationWriter.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            $primaryDateUtc,
            'monotonic rotation probe') -or
        -not $rotationWriter.Stop([TimeSpan]::FromSeconds(5))) {
        throw 'The monotonic-rotation writer failed.'
    }
    if ((Test-LogPath ($rotationActivePath + '.02')) -or
        -not (Test-LogPath $rotationFourPath) -or
        (Read-LogText $rotationFourPath) -ne $rotationActiveSeed) {
        throw 'Rotation reused a gap, shifted, or overwrote a segment.'
    }

    # Old synthetic-suffix files are outside the new canonical naming scheme.
    # They must not consume the retention budget, advance rotation generations,
    # receive new records, or be renamed/deleted as an implicit migration.
    $legacyRoot = Join-Path $testRoot 'legacy-suffix-files-ignored'
    $legacyOptions = New-WriterOptions $legacyRoot 512 600 2
    $legacyOptions.BatchWindow = [TimeSpan]::Zero
    $legacyPlayerDirectory = Join-Path $legacyRoot $playerKey
    [IO.Directory]::CreateDirectory($legacyPlayerDirectory) | Out-Null
    $olderUppercasePath = Join-Path $legacyPlayerDirectory (
        Get-LocalLogName $primaryDateUtc.AddDays(-1) 'Older' 9301)
    [IO.File]::WriteAllText(
        $olderUppercasePath,
        "older-canonical-uppercase-stream`n",
        [Text.UTF8Encoding]::new($false))
    $legacyActivePath = Join-Path $legacyPlayerDirectory (
        Get-LocalLogName $primaryDateUtc 'halla~012345abcdef' $playerId)
    $legacyOldPath = Join-Path $legacyPlayerDirectory (
        (Get-LocalLogName $primaryDateUtc.AddDays(-10) 'Halla~fedcba543210' $playerId) + '.03')
    $legacyPaths = @($legacyActivePath, ($legacyActivePath + '.99'), $legacyOldPath)
    $legacySnapshots = @{}
    foreach ($legacyPath in $legacyPaths) {
        $legacyContent = 'untouched legacy fixture: ' + [IO.Path]::GetFileName($legacyPath)
        [IO.File]::WriteAllText(
            $legacyPath,
            $legacyContent,
            [Text.UTF8Encoding]::new($false))
        $legacySnapshots[$legacyPath] = @{
            Content = $legacyContent
            LastWriteTimeUtc = [IO.File]::GetLastWriteTimeUtc($legacyPath)
        }
    }
    foreach ($probe in @('x', 'y')) {
        $legacyWriter =
            [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
        try {
            $legacyWriter.Initialize($legacyOptions)
            $legacyWriter.Start()
            if (-not $legacyWriter.TryWrite(
                    $playerKey, $characterName, $playerId,
                    $primaryDateUtc, ($probe * 450)) -or
                -not $legacyWriter.Stop([TimeSpan]::FromSeconds(5))) {
                throw 'The legacy-suffix exclusion writer failed.'
            }
        }
        finally { $legacyWriter.Dispose() }
        if (@(Get-ChildItem -LiteralPath $legacyPlayerDirectory -File).Count -ne 5) {
            throw 'Legacy suffix files affected the canonical retention budget.'
        }
        foreach ($legacyPath in $legacyPaths) {
            if (-not (Test-Path -LiteralPath $legacyPath) -or
                [IO.File]::ReadAllText($legacyPath) -cne $legacySnapshots[$legacyPath].Content -or
                [IO.File]::GetLastWriteTimeUtc($legacyPath) -ne $legacySnapshots[$legacyPath].LastWriteTimeUtc) {
                throw 'An old suffix file was appended, migrated, rotated, or pruned.'
            }
        }
        if ($probe -eq 'x' -and -not (Test-Path -LiteralPath $olderUppercasePath)) {
            throw 'Ignored legacy suffix files prematurely evicted a canonical log.'
        }
    }
    $legacyCanonicalActivePath = Join-Path $legacyPlayerDirectory $primaryFileName
    if ((Test-Path -LiteralPath $olderUppercasePath) -or
        -not (Test-Path -LiteralPath $legacyCanonicalActivePath) -or
        -not (Test-LogPath ($legacyCanonicalActivePath + '.01')) -or
        (Test-LogPath ($legacyCanonicalActivePath + '.100'))) {
        throw 'Uppercase canonical retention or suffix-free rotation generations failed.'
    }

    # Retention remains a hard cap even when one batch spans two active dates.
    $hardCapRoot = Join-Path $testRoot 'protected-hard-cap'
    $hardCapOptions = New-WriterOptions $hardCapRoot 2048 4096 1
    $hardCapOptions.BatchWindow = [TimeSpan]::FromSeconds(1)
    $hardCapWriter =
        [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    $hardCapWriter.Initialize($hardCapOptions)
    $hardCapWriter.Start()
    if (-not $hardCapWriter.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            $primaryDateUtc,
            'older protected date') -or
        -not $hardCapWriter.TryWrite(
            $playerKey,
            'other_name',
            -991,
            $nextDateUtc,
            'newer protected date') -or
        -not $hardCapWriter.Stop([TimeSpan]::FromSeconds(5))) {
        throw 'The protected-date hard-cap writer failed.'
    }
    $hardCapFiles = @(Get-ChildItem -LiteralPath (
        Join-Path $hardCapRoot $playerKey) -File)
    $hardCapNextFileName = Get-LocalLogName $nextDateUtc 'other_name' -991
    if ($hardCapFiles.Count -ne 1 -or
        $hardCapFiles[0].Name -ne $hardCapNextFileName) {
        throw 'Multiple character streams bypassed the Steam-account hard file cap.'
    }

    # The fixed server policy counts rotated segments and different character
    # streams together: exactly 30 files survive, while file 31 evicts only the
    # oldest file for this Steam account. All data is in the disposable test root.
    $thirtyRoot = Join-Path $testRoot 'thirty-file-account-retention'
    $defaultOptions = [ServerManager.PlayerLogging.PlayerTelemetryLogOptions]::new($thirtyRoot)
    if ($defaultOptions.MaximumFilesPerPlayer -ne 30) {
        throw 'Standalone writer retention no longer defaults to the fixed 30-file server policy.'
    }
    $thirtyOptions = New-WriterOptions $thirtyRoot 512 600 30
    $thirtyPlayerDirectory = Join-Path $thirtyRoot $playerKey
    [IO.Directory]::CreateDirectory($thirtyPlayerDirectory) | Out-Null
    $thirtyOldestPath = Join-Path $thirtyPlayerDirectory (
        Get-LocalLogName $primaryDateUtc.AddDays(-4) 'oldest' 8000)
    [IO.File]::WriteAllText($thirtyOldestPath, "oldest-account-record`n", [Text.UTF8Encoding]::new($false))
    $thirtyRetainedPaths = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt 14; ++$index) {
        $streamPath = Join-Path $thirtyPlayerDirectory (
            Get-LocalLogName $primaryDateUtc.AddDays(-1) ("retained_" + $index) ([long](8100 + $index)))
        foreach ($suffix in @('', '.01')) {
            $retainedPath = $streamPath + $suffix
            [IO.File]::WriteAllText($retainedPath, "retained-account-record`n", [Text.UTF8Encoding]::new($false))
            $thirtyRetainedPaths.Add($retainedPath)
        }
    }
    $otherAccountDirectory = Join-Path $thirtyRoot '76561198000000002'
    [IO.Directory]::CreateDirectory($otherAccountDirectory) | Out-Null
    $otherAccountPath = Join-Path $otherAccountDirectory (
        Get-LocalLogName $primaryDateUtc.AddDays(-10) 'other_account' 8200)
    [IO.File]::WriteAllText($otherAccountPath, "other-account-record`n", [Text.UTF8Encoding]::new($false))

    $thirtyWriter = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    try {
        $thirtyWriter.Initialize($thirtyOptions)
        $thirtyWriter.Start()
        if (-not $thirtyWriter.TryWrite($playerKey, $characterName, $playerId,
                $primaryDateUtc, ('x' * 450)) -or
            -not $thirtyWriter.Stop([TimeSpan]::FromSeconds(5))) {
            throw 'The 30-file boundary writer did not accept and drain.'
        }
    }
    finally { $thirtyWriter.Dispose() }
    if (@(Get-ChildItem -LiteralPath $thirtyPlayerDirectory -File).Count -ne 30 -or
        -not (Test-Path -LiteralPath $thirtyOldestPath)) {
        throw 'Exactly 30 account files prematurely triggered retention.'
    }

    $thirtyRotationWriter = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    try {
        $thirtyRotationWriter.Initialize($thirtyOptions)
        $thirtyRotationWriter.Start()
        if (-not $thirtyRotationWriter.TryWrite($playerKey, $characterName, $playerId,
                $primaryDateUtc, ('y' * 450)) -or
            -not $thirtyRotationWriter.Stop([TimeSpan]::FromSeconds(5))) {
            throw 'The 31st-file rotation writer did not accept and drain.'
        }
    }
    finally { $thirtyRotationWriter.Dispose() }
    $thirtyActivePath = Join-Path $thirtyPlayerDirectory $primaryFileName
    if (@(Get-ChildItem -LiteralPath $thirtyPlayerDirectory -File).Count -ne 30 -or
        (Test-Path -LiteralPath $thirtyOldestPath) -or
        -not (Test-Path -LiteralPath $thirtyActivePath) -or
        -not (Test-LogPath ($thirtyActivePath + '.01'))) {
        throw 'Rotation did not count as file 31 or retention failed to remove the oldest account file.'
    }
    foreach ($retainedPath in $thirtyRetainedPaths) {
        if (-not (Test-LogPath $retainedPath) -or
            (Read-LogText $retainedPath) -ne "retained-account-record`n") {
            throw 'Account retention changed the surviving character/date/rotation groups.'
        }
    }
    if (@(Get-ChildItem -LiteralPath $otherAccountDirectory -File).Count -ne 1 -or
        [IO.File]::ReadAllText($otherAccountPath) -ne "other-account-record`n") {
        throw 'One Steam account retention budget removed another account log.'
    }

    # Startup maintenance must archive even an inactive account. Today's active
    # file stays directly readable; completed segments archive regardless of date.
    $archiveRoot = Join-Path $testRoot 'gzip-inactive-startup'
    $archiveAccount = Join-Path $archiveRoot $playerKey
    [IO.Directory]::CreateDirectory($archiveAccount) | Out-Null
    $todayUtc = [DateTime]::Now.Date.AddHours(12).ToUniversalTime()
    $yesterdayUtc = $todayUtc.AddDays(-1)
    $archivePath = Join-Path $archiveAccount (
        Get-LocalLogName $yesterdayUtc $characterName $playerId)
    $todayPath = Join-Path $archiveAccount (
        Get-LocalLogName $todayUtc $characterName $playerId)
    $archiveText = "[12:00:00] [-1, 2, 3] Inventory:`n  - Wood x50`n" +
        "    CustomData:`n      `"unicode`": `"" +
        (-join [char[]]@(0xD55C, 0xAE00)) + "`"`n" + ('v' * 32768) + "`n"
    $archiveBytes = [Text.UTF8Encoding]::new($false).GetBytes($archiveText)
    [IO.File]::WriteAllBytes($archivePath, $archiveBytes)
    [IO.File]::WriteAllText($todayPath, "today-stays-readable`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(($todayPath + '.01'), "completed-segment`n", [Text.UTF8Encoding]::new($false))
    $nonLogPath = Join-Path $archiveAccount 'notes.log'
    [IO.File]::WriteAllText($nonLogPath, 'not a canonical player log', [Text.UTF8Encoding]::new($false))
    $archiveWriter = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    try {
        $archiveWriter.Initialize((New-WriterOptions $archiveRoot))
        $archiveWriter.Start()
        Wait-ForArchive ($archivePath + '.gz')
        Wait-ForArchive ($todayPath + '.01.gz')
        if (-not $archiveWriter.Stop([TimeSpan]::FromSeconds(5))) {
            throw 'The inactive-account archive writer did not drain.'
        }
    }
    finally { $archiveWriter.Dispose() }
    if ((Test-Path -LiteralPath $archivePath) -or
        (Test-Path -LiteralPath ($todayPath + '.01')) -or
        -not (Test-Path -LiteralPath $todayPath) -or
        (Test-Path -LiteralPath ($todayPath + '.gz')) -or
        (Read-LogText $archivePath) -cne $archiveText -or
        (Read-LogText ($todayPath + '.01')) -cne "completed-segment`n" -or
        [IO.File]::ReadAllText($nonLogPath) -cne 'not a canonical player log') {
        throw 'Startup gzip was not lossless, touched today/unknown files, or retained a verified source.'
    }
    if ((Get-Item -LiteralPath ($archivePath + '.gz')).Length -ge $archiveBytes.Length -or
        @(Get-ChildItem -LiteralPath $archiveAccount -Filter '*.tmp' -File).Count -ne 0) {
        throw 'A compressible inventory did not shrink or left an unfinished temporary archive.'
    }

    # Reopening an archived date preserves the old compressed bytes as a
    # completed numbered segment. New events use one plain active .log again,
    # avoiding a separate compressed segment for every delayed batch.
    $archivedBefore = [Convert]::ToBase64String([IO.File]::ReadAllBytes($archivePath + '.gz'))
    $lateWriter = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    try {
        $lateWriter.Initialize((New-WriterOptions $archiveRoot))
        $lateWriter.Start()
        if (-not $lateWriter.TryWrite($playerKey, $characterName, $playerId,
                $yesterdayUtc, 'late event after archive') -or
            -not $lateWriter.Stop([TimeSpan]::FromSeconds(5))) {
            throw 'A delayed event could not be written after archive completion.'
        }
    }
    finally { $lateWriter.Dispose() }
    if (-not (Test-Path -LiteralPath ($archivePath + '.01.gz')) -or
        (Test-Path -LiteralPath ($archivePath + '.01')) -or
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($archivePath + '.01.gz')) -cne $archivedBefore -or
        (Read-LogText $archivePath) -cne (
            (Get-ExpectedLine $yesterdayUtc 'late event after archive') + "`n")) {
        throw 'Late/clock-rollback logging altered the original archive or lost the new event.'
    }
    $lateArchiveWriter = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    try {
        $lateArchiveWriter.Initialize((New-WriterOptions $archiveRoot))
        $lateArchiveWriter.Start()
        Wait-ForArchive ($archivePath + '.gz')
        if (-not $lateArchiveWriter.Stop([TimeSpan]::FromSeconds(5))) {
            throw 'The reopened-date archive writer did not drain.'
        }
    }
    finally { $lateArchiveWriter.Dispose() }
    if ((Test-Path -LiteralPath $archivePath) -or
        (Read-LogText $archivePath) -cne (
            (Get-ExpectedLine $yesterdayUtc 'late event after archive') + "`n")) {
        throw 'The reopened past-date active file was not archived losslessly on the next sweep.'
    }

    # Simulate a clock rollback by supplying today's date with an archive
    # already present. Its old bytes move aside, while today's new .log stays
    # plain and accepts later batches without generating extra archives.
    $rollbackRoot = Join-Path $testRoot 'gzip-clock-rollback'
    $rollbackAccount = Join-Path $rollbackRoot $playerKey
    [IO.Directory]::CreateDirectory($rollbackAccount) | Out-Null
    $rollbackPath = Join-Path $rollbackAccount (
        Get-LocalLogName $todayUtc $characterName $playerId)
    Write-GzipFixture ($rollbackPath + '.gz') ([Text.Encoding]::UTF8.GetBytes("before-clock-rollback`n"))
    $rollbackGzip = [Convert]::ToBase64String([IO.File]::ReadAllBytes($rollbackPath + '.gz'))
    foreach ($message in @('rollback first', 'rollback second')) {
        $rollbackWriter = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
        try {
            $rollbackWriter.Initialize((New-WriterOptions $rollbackRoot))
            $rollbackWriter.Start()
            if (-not $rollbackWriter.TryWrite($playerKey, $characterName, $playerId,
                    $todayUtc, $message) -or
                -not $rollbackWriter.Stop([TimeSpan]::FromSeconds(5))) {
                throw 'The clock-rollback writer did not drain.'
            }
        }
        finally { $rollbackWriter.Dispose() }
    }
    if (-not (Test-Path -LiteralPath $rollbackPath) -or
        (Test-Path -LiteralPath ($rollbackPath + '.gz')) -or
        (Test-LogPath ($rollbackPath + '.02')) -or
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($rollbackPath + '.01.gz')) -cne $rollbackGzip -or
        (Read-LogText $rollbackPath) -cne (
            (Get-ExpectedLine $todayUtc 'rollback first') + "`n" +
            (Get-ExpectedLine $todayUtc 'rollback second') + "`n")) {
        throw 'Clock rollback did not preserve compressed history and one plain active stream.'
    }

    # Existing .gz generations count when allocating a rotation suffix.
    $compressedRotationRoot = Join-Path $testRoot 'gzip-generation'
    $compressedRotationAccount = Join-Path $compressedRotationRoot $playerKey
    [IO.Directory]::CreateDirectory($compressedRotationAccount) | Out-Null
    $compressedActivePath = Join-Path $compressedRotationAccount $primaryFileName
    [IO.File]::WriteAllText($compressedActivePath, $rotationActiveSeed, [Text.UTF8Encoding]::new($false))
    Write-GzipFixture ($compressedActivePath + '.03.gz') (
        [Text.Encoding]::UTF8.GetBytes("existing-generation-three`n"))
    $compressedRotationWriter = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    try {
        $compressedRotationWriter.Initialize((New-WriterOptions $compressedRotationRoot 512 600 10))
        $compressedRotationWriter.Start()
        if (-not $compressedRotationWriter.TryWrite($playerKey, $characterName, $playerId,
                $primaryDateUtc, 'rotate after archived generation')) {
            throw 'The compressed-generation writer rejected a valid event.'
        }
        Wait-ForArchive ($compressedActivePath + '.04.gz')
        if (-not $compressedRotationWriter.Stop([TimeSpan]::FromSeconds(5))) {
            throw 'The compressed-generation writer did not drain.'
        }
    }
    finally { $compressedRotationWriter.Dispose() }
    if (-not (Test-Path -LiteralPath ($compressedActivePath + '.04.gz')) -or
        (Test-LogPath ($compressedActivePath + '.01')) -or
        (Test-LogPath ($compressedActivePath + '.02')) -or
        (Read-LogText ($compressedActivePath + '.03')) -cne "existing-generation-three`n" -or
        (Read-LogText ($compressedActivePath + '.04')) -cne $rotationActiveSeed) {
        throw 'A .gz generation was ignored, overwritten, or renumbered during rotation.'
    }

    # Recovery after a crash between archive rename and source deletion removes
    # the source only after equality verification. A conflicting/corrupt .gz or
    # a locked source must leave every original byte available for recovery.
    $conflictRoot = Join-Path $testRoot 'gzip-crash-recovery'
    $conflictAccount = Join-Path $conflictRoot $playerKey
    [IO.Directory]::CreateDirectory($conflictAccount) | Out-Null
    $conflictFixtures = @{}
    foreach ($fixture in @('equal', 'different', 'corrupt', 'locked')) {
        $fixturePath = Join-Path $conflictAccount (
            Get-LocalLogName $yesterdayUtc $fixture ([long]9000))
        $fixtureBytes = [Text.Encoding]::UTF8.GetBytes("original-$fixture`n")
        [IO.File]::WriteAllBytes($fixturePath, $fixtureBytes)
        if ($fixture -eq 'equal') { Write-GzipFixture ($fixturePath + '.gz') $fixtureBytes }
        elseif ($fixture -eq 'different') {
            Write-GzipFixture ($fixturePath + '.gz') ([Text.Encoding]::UTF8.GetBytes("different-content`n"))
        }
        elseif ($fixture -eq 'corrupt') {
            [IO.File]::WriteAllBytes(($fixturePath + '.gz'), [byte[]]@(1, 2, 3, 4, 5))
        }
        $conflictFixtures[$fixture] = @{
            Path = $fixturePath
            Bytes = [Convert]::ToBase64String($fixtureBytes)
            Gzip = if (Test-Path -LiteralPath ($fixturePath + '.gz')) {
                [Convert]::ToBase64String([IO.File]::ReadAllBytes($fixturePath + '.gz'))
            } else { $null }
        }
    }
    $lockedSource = [IO.File]::Open($conflictFixtures.locked.Path,
        [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archiveClosedLog = $writerType.GetMethod('ArchiveClosedLog',
        [Reflection.BindingFlags]'Instance,NonPublic')
    if ($null -eq $archiveClosedLog) { throw 'The archive primitive is unavailable.' }
    $archiveVerifier = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    try {
        # Invoke the exact worker primitive to exercise each failure path
        # deterministically, without depending on filesystem enumeration order.
        foreach ($fixture in @('equal', 'different', 'corrupt', 'locked')) {
            $archiveFailed = $false
            $archiveError = ''
            try {
                $archiveClosedLog.Invoke($archiveVerifier,
                    [object[]]@([string]$conflictFixtures[$fixture].Path)) | Out-Null
            }
            catch {
                $archiveFailed = $true
                $archiveError = $_.Exception.ToString()
            }
            if ($archiveFailed -ne ($fixture -ne 'equal')) {
                throw "Unexpected archive outcome for $fixture source/destination. $archiveError"
            }
        }
        $archiveAttempt = $writerType.GetMethod('TryArchiveClosedLog',
            [Reflection.BindingFlags]'Instance,NonPublic')
        $archiveAttempt.Invoke($archiveVerifier,
            [object[]]@([string]$conflictFixtures['different'].Path)) | Out-Null
        if ($archiveVerifier.GetStatistics().WriteFailures -ne 0) {
            throw 'An archive failure must not masquerade as a lost append and invalidate inventory baselines.'
        }
    }
    finally { $archiveVerifier.Dispose(); $lockedSource.Dispose() }
    foreach ($fixture in @('equal', 'different', 'corrupt', 'locked')) {
        $saved = $conflictFixtures[$fixture]
        if ($fixture -eq 'equal') {
            if ((Test-Path -LiteralPath $saved.Path) -or
                [Convert]::ToBase64String((Read-LogBytes $saved.Path)) -cne $saved.Bytes) {
                throw 'An equal crash-recovery archive was not safely reconciled.'
            }
        }
        elseif (-not (Test-Path -LiteralPath $saved.Path) -or
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($saved.Path)) -cne $saved.Bytes) {
            throw "Archive failure deleted or changed the $fixture source."
        }
        if ($null -ne $saved.Gzip -and
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($saved.Path + '.gz')) -cne $saved.Gzip) {
            throw "Archive recovery overwrote the $fixture destination."
        }
    }

    # A surviving source/.gz pair represents one logical file, not two budget
    # entries. A conflicting destination remains untouched until normal age-
    # based retention evicts that complete logical group.
    $pairedRoot = Join-Path $testRoot 'gzip-paired-retention'
    $pairedAccount = Join-Path $pairedRoot $playerKey
    [IO.Directory]::CreateDirectory($pairedAccount) | Out-Null
    $pairedPath = Join-Path $pairedAccount (
        Get-LocalLogName $primaryDateUtc.AddDays(-1) 'paired' 999)
    [IO.File]::WriteAllText($pairedPath, 'retained source', [Text.UTF8Encoding]::new($false))
    Write-GzipFixture ($pairedPath + '.gz') ([Text.Encoding]::UTF8.GetBytes('conflicting archive'))
    foreach ($offset in @(0, 1)) {
        $pairedWriter = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
        try {
            $pairedWriter.Initialize((New-WriterOptions $pairedRoot 2048 4096 2))
            $pairedWriter.Start()
            if (-not $pairedWriter.TryWrite($playerKey, $characterName, $playerId,
                    $primaryDateUtc.AddDays($offset), 'retention probe') -or
                -not $pairedWriter.Stop([TimeSpan]::FromSeconds(5))) {
                throw 'The source/archive-pair retention writer did not drain.'
            }
        }
        finally { $pairedWriter.Dispose() }
        if ($offset -eq 0) {
            if (@(Get-ChildItem -LiteralPath $pairedAccount -File).Count -ne 3 -or
                -not (Test-Path -LiteralPath $pairedPath) -or
                -not (Test-Path -LiteralPath ($pairedPath + '.gz'))) {
                throw 'A source/archive pair consumed two retention slots.'
            }
        }
        elseif (@(Get-ChildItem -LiteralPath $pairedAccount -File).Count -ne 2 -or
            (Test-LogPath $pairedPath)) {
            throw 'Retention failed to evict both members of the oldest logical group.'
        }
    }

    # Never follow account-directory reparse points while sweeping. Junctions
    # do not need Windows symlink privileges and the target is still entirely
    # within this disposable fixture tree.
    $junctionRoot = Join-Path $testRoot 'gzip-no-reparse'
    $junctionTarget = Join-Path $testRoot 'gzip-junction-target'
    [IO.Directory]::CreateDirectory($junctionRoot) | Out-Null
    [IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
    $junctionFile = Join-Path $junctionTarget (
        Get-LocalLogName $yesterdayUtc $characterName $playerId)
    [IO.File]::WriteAllText($junctionFile, 'outside logging root', [Text.UTF8Encoding]::new($false))
    $junctionPath = Join-Path $junctionRoot $playerKey
    $junctionCreated = $false
    try {
        try {
            New-Item -ItemType Junction -Path $junctionPath -Target $junctionTarget -ErrorAction Stop | Out-Null
            $junctionCreated = $true
        }
        catch {
            Write-Host ('SKIP: account-junction archive test unavailable: ' + $_.Exception.Message)
        }
        if ($junctionCreated) {
            $junctionWriter = [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
            try {
                $junctionWriter.Initialize((New-WriterOptions $junctionRoot))
                $junctionWriter.Start()
                [Threading.Thread]::Sleep(500)
                if (-not $junctionWriter.Stop([TimeSpan]::FromSeconds(5))) {
                    throw 'The no-reparse archive writer did not drain.'
                }
            }
            finally { $junctionWriter.Dispose() }
            if (-not (Test-Path -LiteralPath $junctionFile) -or
                (Test-Path -LiteralPath ($junctionFile + '.gz')) -or
                [IO.File]::ReadAllText($junctionFile) -cne 'outside logging root') {
                throw 'Startup archive followed an account-directory reparse point.'
            }
        }
    }
    finally {
        if ($junctionCreated) { [IO.Directory]::Delete($junctionPath) }
    }

    # A timed-out Stop is terminal and Dispose waits for the original worker.
    $lifecycleRoot = Join-Path $testRoot 'lifecycle'
    $lifecycleOptions = New-WriterOptions $lifecycleRoot
    $lifecycleOptions.BatchWindow = [TimeSpan]::Zero
    $blockingCallback =
        [ServerManager.PlayerLogging.Tests.BlockingStoppedCallback]::new()
    $callbackDelegate = [Delegate]::CreateDelegate(
        [Action[ServerManager.PlayerLogging.PlayerTelemetryDiagnostic]],
        $blockingCallback,
        $blockingCallback.GetType().GetMethod('Invoke'))
    $lifecycleWriter =
        [ServerManager.PlayerLogging.PlayerTelemetryLogWriter]::new()
    $lifecycleWriter.Initialize($lifecycleOptions, $callbackDelegate)
    $lifecycleWriter.Start()
    if (-not $lifecycleWriter.TryWrite(
            $playerKey,
            $characterName,
            $playerId,
            'lifecycle probe')) {
        throw 'The lifecycle probe record was rejected.'
    }
    if ($lifecycleWriter.Stop([TimeSpan]::Zero)) {
        throw 'A zero-timeout Stop unexpectedly completed.'
    }
    $restartRejected = $false
    try {
        $lifecycleWriter.Start()
    }
    catch [InvalidOperationException] {
        $restartRejected = $true
    }
    if (-not $restartRejected -or
        -not $blockingCallback.Wait([TimeSpan]::FromSeconds(5))) {
        throw 'The stopping writer accepted a restart or failed to stop.'
    }
    $disposeWatch = [Diagnostics.Stopwatch]::StartNew()
    $lifecycleWriter.Dispose()
    $disposeWatch.Stop()
    if ($disposeWatch.ElapsedMilliseconds -lt 100) {
        throw 'Dispose did not wait for the live worker callback.'
    }
    $blockingCallback.Dispose()

    Write-Host (
        'Player log writer smoke test passed: canonical Steam64/character/playerID/local-date routing, plain UTF-8 formatting, compact lossless CustomData blocks, rotation/retention, inactive startup gzip, late-event archive preservation, compressed generation allocation, source/archive conflict recovery, logical-group retention, no-reparse sweep, crash-tail repair, and lifecycle drain passed.')
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $expectedPrefix = $temporaryRoot.TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $safeLeaf = [IO.Path]::GetFileName($resolvedTestRoot).StartsWith(
        'ServerManager-player-telemetry-smoke-',
        [StringComparison]::Ordinal)
    if ($safeLeaf -and
        $resolvedTestRoot.StartsWith(
            $expectedPrefix,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
