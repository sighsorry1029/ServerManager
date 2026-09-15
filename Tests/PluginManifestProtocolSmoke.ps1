param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
# Plugin-only admission keeps the fixed challenge layout and rejects retired extensions.
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
