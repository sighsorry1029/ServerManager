param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
# Plugin-only admission keeps the fixed challenge layout and bounds display-only name metadata.
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
$createManifest = $codec.GetMethod('CreateManifestResponse', $static)
$reportedNameProperty = $packetType.GetProperty('ReportedCharacterName', $instance)
$normalizeName = $codec.GetMethod('NormalizeReportedCharacterName', $static)
$decodeChallenge = $codec.GetMethod('TryDecodeChallengeOptions', $static)
$headerBytes = [int]$codec.GetField('FixedHeaderBytes', $static).GetRawConstantValue()
$minimumBytes = [int]$challengeType.GetField('EncodedBytes', $static).GetRawConstantValue()
$script:checks = 0
function Assert-True([bool]$Condition, [string]$Message) {
    ++$script:checks
    if (-not $Condition) { throw $Message }
}
function New-Options {
    return $script:challengeType.GetConstructors()[0].Invoke([object[]]@(
        $true, $true, 262144, $true, $true, $true, $true, $true, $true, $true,
        30, $true, [single]2000, $true, [single]50000))
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
    Assert-True $packet.Success 'A plugin-only challenge envelope failed to decode.'
    $decoded = Read-Options $packet.Packet
    Assert-True $decoded.Success 'A plugin-only challenge payload failed to decode.'
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
function Get-ReportedName($Packet) {
    return [string]$script:reportedNameProperty.GetValue($Packet)
}
function Assert-InvalidEnvelope([byte[]]$Bytes, [string]$Description) {
    $decoded = Read-Packet (New-Package $Bytes)
    Assert-True (-not $decoded.Success -and $null -eq $decoded.Packet -and $null -ne $decoded.Rejection) $Description
}
function New-NameEnvelope([byte[]]$NameBytes, [int]$DeclaredLength = -1) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write($script:manifestWithoutMetadata)
        $writer.Write([uint16]$(if ($DeclaredLength -lt 0) { $NameBytes.Length } else { $DeclaredLength }))
        $writer.Write($NameBytes)
        $writer.Flush()
        return ,$stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

# The fixed challenge fields and all plugin admission limits remain unchanged.
$roundTrip = Round-Trip (New-Options)
$baseOptions = [byte[]]$roundTrip.Packet.Payload
Assert-True ($minimumBytes -eq 20 -and $baseOptions.Length -eq 20 -and
    [BitConverter]::ToInt32($baseOptions, 16) -eq 0) 'The challenge must not request libraries.'
Assert-True ($roundTrip.Options.EnforceManifest -and $roundTrip.Options.ServerCharactersEnabled -and
    $roundTrip.Options.MaximumDamage -eq [single]50000 -and
    $roundTrip.Options.ProcessScanIntervalSeconds -eq 30) 'Plugin-only challenge changed gameplay/admission settings.'
$updated = $challengeType.GetMethod('WithGameplayLimits', $instance).Invoke($roundTrip.Options, [object[]]@([single]3500, [single]75000))
$updatedRoundTrip = Round-Trip $updated
Assert-True ($updatedRoundTrip.Options.MaximumCarryWeight -eq [single]3500 -and
    $updatedRoundTrip.Options.MaximumDamage -eq [single]75000 -and
    $updatedRoundTrip.Options.EnforceManifest) 'Live limits no longer round-trip.'
foreach ($count in @(-1, 1, 128, [int]::MaxValue)) {
    Assert-InvalidPayload (New-RawPayload @() $count) 'A retired library request was accepted.'
}
Assert-InvalidPayload (New-RawPayload @('assembly:newtonsoft.json')) 'A variable-length library request was accepted.'
for ($length = 0; $length -lt $baseOptions.Length; ++$length) {
    $truncated = [byte[]]::new($length)
    [Buffer]::BlockCopy($baseOptions, 0, $truncated, 0, $length)
    Assert-InvalidPayload $truncated 'A truncated challenge was accepted.'
}
Assert-InvalidPayload ([byte[]]@($baseOptions + 0)) 'Trailing challenge bytes were accepted.'
$manifest = New-Packet 'ManifestResponse' 2 ([byte[]]@(1, 2, 3))
$manifestPackage = $encode.Invoke($null, [object[]]@($manifest, $limits))
$decodedManifest = Read-Packet $manifestPackage
Assert-True ($decodedManifest.Success -and $decodedManifest.Packet.Kind.ToString() -eq 'ManifestResponse') `
    'Ordinary plugin manifest transmission was removed.'
Assert-True ([int]$codec.GetField('WireVersion', $static).GetRawConstantValue() -eq 24) `
    'Optional center displays must be gated by handshake wire version 24.'
Assert-True ((Get-ReportedName $decodedManifest.Packet) -eq '') 'Absent display hints must remain empty.'
$rawManifest = [byte[]]@(1, 2, 3)
$manifestWithoutMetadata = [byte[]]$manifestPackage.GetArray()[0..($headerBytes + $rawManifest.Length - 1)]
foreach ($name in @('halla', 'MAGNUS Carlson', '띠 오', '우주괴물딱지', '')) {
    $package = $createManifest.Invoke($null, [object[]]@($sessionId, $nonce, $rawManifest, $limits, $name))
    $decoded = Read-Packet $package
    Assert-True $decoded.Success "Reported character name '$name' failed to decode."
    Assert-True ((Get-ReportedName $decoded.Packet) -ceq $name) 'Reported names changed spelling, case or interior spaces.'
    Assert-True ([Convert]::ToBase64String($decoded.Packet.Payload) -ceq [Convert]::ToBase64String($rawManifest)) `
        'Display-only metadata changed the raw manifest supplied to admission validation.'
    Assert-True ($package.Size() -eq $headerBytes + $rawManifest.Length + 2 + [Text.Encoding]::UTF8.GetByteCount($name)) `
        'Manifest metadata was not encoded as a bounded suffix.'
}

# Only manifests carry metadata. Other existing message bodies and sizes stay byte-for-byte fixed.
foreach ($sample in @(
    @{ Kind = 'ManifestAccepted'; Payload = [byte[]]@() },
    @{ Kind = 'Challenge'; Payload = $baseOptions },
    @{ Kind = 'ReadyAck'; Payload = [byte[]](1..16) },
    @{ Kind = 'PolicyUpdate'; Payload = [byte[]]::new(8) }
)) {
    $plain = New-Packet $sample.Kind 1 $sample.Payload
    $plainPackage = $encode.Invoke($null, [object[]]@($plain, $limits))
    $reportedNameProperty.SetValue($plain, 'Do not transmit this name')
    $hintedPackage = $encode.Invoke($null, [object[]]@($plain, $limits))
    $decoded = Read-Packet $hintedPackage
    Assert-True ($decoded.Success -and (Get-ReportedName $decoded.Packet) -eq '') `
        "Non-manifest $($sample.Kind) carried a character name."
    Assert-True ([Convert]::ToBase64String($plainPackage.GetArray()) -ceq [Convert]::ToBase64String($hintedPackage.GetArray())) `
        "Display metadata changed the existing $($sample.Kind) wire layout."
    Assert-InvalidEnvelope ([byte[]]@($hintedPackage.GetArray() + 0 + 0)) `
        "Non-manifest $($sample.Kind) accepted a metadata suffix."
}

# Do not trust incoming UTF-8 or its byte count, even before Steam authentication.
Assert-InvalidEnvelope $manifestWithoutMetadata 'A manifest without the required suffix decoded.'
Assert-InvalidEnvelope ([byte[]]@($manifestWithoutMetadata + 0)) 'A truncated name length decoded.'
Assert-InvalidEnvelope (New-NameEnvelope ([byte[]]@()) 1) 'A truncated name body decoded.'
Assert-InvalidEnvelope (New-NameEnvelope ([byte[]]::new(385))) 'An oversized incoming name decoded.'
Assert-InvalidEnvelope (New-NameEnvelope ([byte[]]@()) 65535) 'A forged maximum name length decoded.'
foreach ($badUtf8 in @(
    [byte[]]@(0xC0, 0xAF), [byte[]]@(0xED, 0xA0, 0x80), [byte[]]@(0xE2, 0x82), [byte[]]@(0xFF)
)) {
    Assert-InvalidEnvelope (New-NameEnvelope $badUtf8) 'Malformed UTF-8 in a display hint decoded.'
}
Assert-InvalidEnvelope ([byte[]]@($manifestPackage.GetArray() + 0)) 'Trailing manifest bytes decoded.'
foreach ($previousVersion in @(22, 23)) {
    foreach ($currentPackage in @($roundTrip.Package, $manifestPackage)) {
        $oldWire = [byte[]]$currentPackage.GetArray().Clone()
        $oldWire[4] = [byte]$previousVersion
        $oldWire[5] = 0
        $oldWireResult = Read-Packet (New-Package $oldWire)
        Assert-True (-not $oldWireResult.Success -and $null -eq $oldWireResult.Packet -and
            $oldWireResult.Rejection.Code.ToString() -eq 'ProtocolVersionMismatch') `
            "Wire-$previousVersion was not rejected before Challenge/Manifest processing."
    }
}

$unsafeName = "  ha`r`nlla`0" + [char]0x202E + [char]0x2028 + [char]0x2029 + [char]0xD800 + '  '
$normalized = [string]$normalizeName.Invoke($null, [object[]]@($unsafeName))
Assert-True ($normalized -eq 'ha  lla') 'Control, format, separator or surrogate characters survived name normalization.'
$unsafeDecoded = Read-Packet (New-Package (New-NameEnvelope ([Text.Encoding]::UTF8.GetBytes(" halla`nname " + [char]0x202E))))
Assert-True ($unsafeDecoded.Success -and (Get-ReportedName $unsafeDecoded.Packet) -eq 'halla name') `
    'Incoming names were not sanitized independently of the sender.'
$hugeName = '한' * 1000000
$hugePackage = $createManifest.Invoke($null, [object[]]@($sessionId, $nonce, $rawManifest, $limits, $hugeName))
$hugeDecoded = Read-Packet $hugePackage
Assert-True ($hugeDecoded.Success -and (Get-ReportedName $hugeDecoded.Packet).Length -eq 96 -and
    $hugePackage.Size() -eq $headerBytes + $rawManifest.Length + 2 + (96 * 3)) `
    'Huge local character names escaped the fixed character/byte budget.'
$incomingBound = Read-Packet (New-Package (New-NameEnvelope ([Text.Encoding]::UTF8.GetBytes(('a' * 384)))))
Assert-True ($incomingBound.Success -and (Get-ReportedName $incomingBound.Packet).Length -eq 96) `
    'Valid incoming bytes escaped the display-name character bound.'

# Manifest limits still apply to the original payload; total packet limits also include metadata.
$maximumManifest = [byte[]]::new($limits.MaxManifestBytes)
$maximumPackage = $createManifest.Invoke($null, [object[]]@($sessionId, $nonce, $maximumManifest, $limits, '띠 오'))
$maximumDecoded = Read-Packet $maximumPackage
Assert-True ($maximumDecoded.Success -and $maximumDecoded.Packet.Payload.Length -eq $limits.MaxManifestBytes) `
    'Adding a display hint unexpectedly reduced the configured raw manifest limit.'
$tightLimits = $limitsType.GetConstructors()[0].Invoke([object[]]@(($headerBytes + 3 + 2), 3, 262144, 512, $null, $null))
$tightEmptyPackage = $createManifest.Invoke($null, [object[]]@($sessionId, $nonce, $rawManifest, $tightLimits, ''))
Assert-True ($tightEmptyPackage.Size() -eq $tightLimits.MaxPacketBytes) 'Exact total packet size failed at the boundary.'
$rejected = $false
try { $null = $createManifest.Invoke($null, [object[]]@($sessionId, $nonce, $rawManifest, $tightLimits, 'a')) }
catch { $rejected = $true }
Assert-True $rejected 'Name metadata bypassed the maximum total packet size during encode.'
$tightDecodeArguments = [object[]]@($maximumPackage, $tightLimits, $null, $null)
$tightSuccess = $decode.Invoke($null, $tightDecodeArguments)
Assert-True (-not $tightSuccess -and $tightDecodeArguments[3].Code.ToString() -eq 'PayloadTooLarge') `
    'Maximum total packet size was not enforced before name/manifest decode.'
$retired = [byte[]]$manifestPackage.GetArray().Clone()
$retired[6] = 15
Assert-True (-not (Read-Packet (New-Package $retired)).Success) 'Retired library update messages still decode.'
$oversized = New-Packet 'ManifestResponse' 2 ([byte[]]::new($limits.MaxManifestBytes + 1))
$rejected = $false
try { $null = $encode.Invoke($null, [object[]]@($oversized, $limits)) } catch { $rejected = $true }
Assert-True $rejected 'Plugin manifest size limits were weakened.'
Assert-True ($null -eq $plugin.GetType('ServerManager.DependencyManifestScanner', $false)) `
    'The standalone DLL collector remains in the final plugin.'
Write-Host "Plugin-only manifest protocol smoke tests passed ($script:checks checks)."
