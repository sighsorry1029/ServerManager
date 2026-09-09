param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.dll', 'assembly_utils.dll', 'assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
$plugin = [Reflection.Assembly]::LoadFrom($pluginPath)
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$codec = $plugin.GetType('ServerManager.ProtocolPacketCodec', $true)
$challengeType = $plugin.GetType('ServerManager.ProtocolChallengeOptions', $true)
$packetType = $plugin.GetType('ServerManager.ProtocolPacket', $true)
$kindType = $plugin.GetType('ServerManager.ProtocolPacketKind', $true)
$limitsType = $plugin.GetType('ServerManager.ConnectionProtocolLimits', $true)
$limits = $limitsType.GetConstructors()[0].Invoke([object[]]@(524288, 262144, 262144, 512, $null, $null))
$sessionId = [byte[]](1..16)
$nonce = [byte[]](1..32)
$encode = $codec.GetMethod('Encode', $static)
$decode = $codec.GetMethod('TryDecode', $static)
$createChallenge = $codec.GetMethod('CreateChallenge', $static)
$decodeChallenge = $codec.GetMethod('TryDecodeChallengeOptions', $static)
$headerBytes = [int]$codec.GetField('FixedHeaderBytes', $static).GetRawConstantValue()
$minimumBytes = [int]$challengeType.GetField('EncodedBytes', $static).GetRawConstantValue()
$maximumBytes = [int]$challengeType.GetField('MaxEncodedBytes', $static).GetRawConstantValue()
$script:checks = 0
function Assert-True([bool]$Condition, [string]$Message) {
    ++$script:checks
    if (-not $Condition) { throw $Message }
}
function New-Options([string[]]$Keys = @()) {
    return $script:challengeType.GetConstructors()[0].Invoke([object[]]@(
        $true, $true, 262144, $true, $true, $true, $true, $true, $true, $true,
        30, $true, [single]2000, $true, [single]50000, $Keys))
}
function New-Packet([string]$Kind, [uint32]$Sequence, [byte[]]$Payload) {
    return $script:packetType.GetConstructors()[0].Invoke([object[]]@(
        [Enum]::Parse($script:kindType, $Kind), $Sequence, $script:sessionId, $script:nonce, $Payload))
}
function Read-Packet($Package) {
    $arguments = [object[]]@($Package, $script:limits, $null, $null)
    $success = $script:decode.Invoke($null, $arguments)
    return [pscustomobject]@{ Success = $success; Packet = $arguments[2]; Rejection = $arguments[3] }
}
function Read-Options($Packet) {
    $arguments = [object[]]@($Packet, $null, $null)
    $success = $script:decodeChallenge.Invoke($null, $arguments)
    return [pscustomobject]@{ Success = $success; Options = $arguments[1]; Rejection = $arguments[2] }
}
function Round-Trip($Options) {
    $package = $script:createChallenge.Invoke($null, [object[]]@($script:sessionId, $script:nonce, $Options, $script:limits))
    $packet = Read-Packet $package
    Assert-True $packet.Success 'A bounded library challenge envelope failed to decode.'
    $decoded = Read-Options $packet.Packet
    Assert-True $decoded.Success 'A bounded library challenge payload failed to decode.'
    return [pscustomobject]@{ Package = $package; Packet = $packet.Packet; Options = $decoded.Options }
}
function New-RawPayload([string[]]$Keys, [int]$Count = -2147483648) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write($script:baseOptions, 0, 16)
        $writer.Write([int]$(if ($Count -eq [int]::MinValue) { $Keys.Count } else { $Count }))
        foreach ($key in $Keys) {
            $bytes = [Text.Encoding]::UTF8.GetBytes($key)
            $writer.Write([int]$bytes.Length)
            $writer.Write($bytes)
        }
        $writer.Flush()
        return ,$stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}
function Assert-InvalidPayload([byte[]]$Payload, [string]$Description) {
    $result = Read-Options (New-Packet 'Challenge' 1 $Payload)
    Assert-True (-not $result.Success -and $null -eq $result.Options -and $null -ne $result.Rejection) $Description
}
function New-Package([byte[]]$Bytes) {
    return [ZPackage]::new($Bytes)
}

Assert-True ($codec.GetField('WireVersion', $static).GetRawConstantValue() -eq 21) 'Library identities need wire-v21 without legacy decoding.'
Assert-True ($minimumBytes -eq 20 -and $maximumBytes -eq 33300) 'The challenge library list lost its tight encoded bounds.'
$empty = Round-Trip (New-Options)
Assert-True ($empty.Packet.Payload.Length -eq 20 -and $empty.Options.LibraryKeys.Count -eq 0) 'Empty library scope changed unrelated challenge semantics.'
$baseOptions = [byte[]]$empty.Packet.Payload
$inputKeys = [string[]]@('assembly:z-library', 'assembly:a.library')
$options = New-Options $inputKeys
$inputKeys[0] = 'assembly:mutated'
Assert-True (($options.LibraryKeys -join ',') -ceq 'assembly:a.library,assembly:z-library' -and
    ([Collections.IList]$options.LibraryKeys).IsReadOnly) 'Challenge identities are not a sorted immutable copy.'
$roundTrip = Round-Trip $options
Assert-True (($roundTrip.Options.LibraryKeys -join ',') -ceq ($options.LibraryKeys -join ',') -and
    $roundTrip.Options.MaximumDamage -eq [single]50000 -and $roundTrip.Options.ProcessScanIntervalSeconds -eq 30) `
    'Library challenge round-trip changed identities or unrelated gameplay settings.'
$updated = $challengeType.GetMethod('WithGameplayLimits', $instance).Invoke($options, [object[]]@([single]3500, [single]75000))
Assert-True (($updated.LibraryKeys -join ',') -ceq ($options.LibraryKeys -join ',') -and $updated.MaximumDamage -eq [single]75000) `
    'A live gameplay-cap change discarded the pinned library scope.'

$maximumKeys = [string[]]@(0..127 | ForEach-Object { 'assembly:' + ('a' * 244) + $_.ToString('D3') })
$maximum = Round-Trip (New-Options $maximumKeys)
Assert-True ($maximum.Packet.Payload.Length -eq $maximumBytes -and $maximum.Options.LibraryKeys.Count -eq 128) `
    'The 128-key / 256-byte identity boundary failed to round-trip.'
foreach ($invalidKeys in @(
    @{ Keys = [string[]]@('assembly:x', 'assembly:x'); Label = 'duplicate' },
    @{ Keys = [string[]]@('Assembly:x'); Label = 'prefix case' },
    @{ Keys = [string[]]@('assembly:X'); Label = 'simple-name case' },
    @{ Keys = [string[]]@('assembly:../secret'); Label = 'relative path' },
    @{ Keys = [string[]]@('assembly:C:\secret'); Label = 'absolute path' },
    @{ Keys = [string[]]@('assembly:..'); Label = 'parent path' },
    @{ Keys = [string[]]@('assembly:'); Label = 'missing identity' },
    @{ Keys = [string[]]@('plugin.id'); Label = 'plugin instead of assembly identity' },
    @{ Keys = [string[]]@('assembly:two names'); Label = 'whitespace' },
    @{ Keys = [string[]]@('assembly:' + ('a' * 248)); Label = 'oversized identity' },
    @{ Keys = [string[]]@($maximumKeys + 'assembly:z'); Label = 'oversized count' }
)) {
    $threw = $false
    try { $null = New-Options $invalidKeys.Keys }
    catch { $threw = $_.Exception.GetBaseException() -is [ArgumentException] }
    Assert-True $threw ('Constructor accepted ' + $invalidKeys.Label)
    Assert-InvalidPayload (New-RawPayload $invalidKeys.Keys) ('Decoder accepted ' + $invalidKeys.Label)
}
Assert-InvalidPayload (New-RawPayload @('assembly:z', 'assembly:a')) 'Decoder accepted an unsorted library list.'
foreach ($count in @(-1, 129, [int]::MaxValue)) {
    Assert-InvalidPayload (New-RawPayload @() $count) 'Decoder accepted a forged library count.'
}
$onePayload = New-RawPayload @('assembly:x')
foreach ($size in @(-1, 0, 257, [int]::MaxValue)) {
    $forged = [byte[]]$onePayload.Clone()
    [Buffer]::BlockCopy([BitConverter]::GetBytes([int]$size), 0, $forged, 20, 4)
    Assert-InvalidPayload $forged 'Decoder trusted an invalid inner identity length.'
}
$invalidUtf8 = [byte[]]$onePayload.Clone()
$invalidUtf8[$invalidUtf8.Length - 1] = 255
Assert-InvalidPayload $invalidUtf8 'Decoder accepted invalid UTF-8 through replacement characters.'
for ($length = 0; $length -lt $onePayload.Length; ++$length) {
    $truncated = [byte[]]::new($length)
    [Buffer]::BlockCopy($onePayload, 0, $truncated, 0, $length)
    Assert-InvalidPayload $truncated 'Decoder accepted a truncated challenge.'
}
$trailing = [byte[]]::new($baseOptions.Length + 1)
[Buffer]::BlockCopy($baseOptions, 0, $trailing, 0, $baseOptions.Length)
Assert-InvalidPayload $trailing 'Decoder ignored trailing bytes after the library list.'
Assert-InvalidPayload ([byte[]]::new($maximumBytes + 1)) 'Decoder accepted an oversized challenge body.'

Assert-True ([int][Enum]::Parse($kindType, 'LibraryManifestUpdate') -eq 15) 'Library update kind must remain 15.'
foreach ($sequence in @([uint32]1, [uint32]2, [uint32]::MaxValue)) {
    $update = New-Packet 'LibraryManifestUpdate' $sequence ([byte[]]@(1, 2, 3))
    $decoded = Read-Packet ($encode.Invoke($null, [object[]]@($update, $limits)))
    Assert-True ($decoded.Success -and $decoded.Packet.Sequence -eq $sequence -and
        $decoded.Packet.Kind.ToString() -eq 'LibraryManifestUpdate') 'Codec replaced the runtime-owned library update sequence with a fixed value.'
}
$maxUpdate = New-Packet 'LibraryManifestUpdate' 1 ([byte[]]::new($limits.MaxManifestBytes))
Assert-True ((Read-Packet ($encode.Invoke($null, [object[]]@($maxUpdate, $limits)))).Success) `
    'Library update rejected the existing manifest payload limit.'
foreach ($invalidUpdate in @(
    (New-Packet 'LibraryManifestUpdate' 0 ([byte[]]@(1))),
    (New-Packet 'LibraryManifestUpdate' 1 ([byte[]]::new($limits.MaxManifestBytes + 1)))
)) {
    $threw = $false
    try { $null = $encode.Invoke($null, [object[]]@($invalidUpdate, $limits)) }
    catch { $threw = $_.Exception.GetBaseException() -is [ArgumentException] }
    Assert-True $threw 'Encoder accepted an invalid library update sequence or size.'
}
$updateFrame = $encode.Invoke($null, [object[]]@((New-Packet 'LibraryManifestUpdate' 1 ([byte[]]@(1))), $limits)).GetArray()
$zeroSequence = [byte[]]$updateFrame.Clone()
[Buffer]::BlockCopy([BitConverter]::GetBytes([uint32]0), 0, $zeroSequence, 7, 4)
Assert-True (-not (Read-Packet (New-Package $zeroSequence)).Success) 'Decoder accepted library sequence zero.'
$oldVersion = [byte[]]$updateFrame.Clone()
$oldVersion[4] = 20; $oldVersion[5] = 0
Assert-True (-not (Read-Packet (New-Package $oldVersion)).Success) 'Decoder retained legacy wire-v20 support.'
$oversizedFrame = [byte[]]::new($headerBytes + $limits.MaxManifestBytes + 1)
[Buffer]::BlockCopy($updateFrame, 0, $oversizedFrame, 0, $headerBytes)
[Buffer]::BlockCopy([BitConverter]::GetBytes([int]($limits.MaxManifestBytes + 1)), 0, $oversizedFrame, $headerBytes - 4, 4)
Assert-True (-not (Read-Packet (New-Package $oversizedFrame)).Success) 'Decoder accepted an oversized library manifest update.'
Write-Host "Scoped library protocol smoke tests passed ($script:checks checks)."
