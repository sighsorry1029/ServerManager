param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    & (Get-Command pwsh -ErrorAction Stop).Source -NoProfile -File $PSCommandPath `
        -Configuration $Configuration -GamePath $GamePath
    if ($LASTEXITCODE -ne 0) { throw 'PeerInfo preflight smoke failed.' }
    exit 0
}

$root = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $GamePath 'valheim_Data\Managed'
$bepInEx = Join-Path $GamePath 'BepInEx\core'
foreach ($assemblyPath in @(
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'assembly_utils.dll'),
    (Join-Path $managed 'assembly_valheim.dll'),
    (Join-Path $bepInEx 'BepInEx.dll'),
    (Join-Path $bepInEx '0Harmony.dll'),
    (Join-Path $bepInEx 'Mono.Cecil.dll'),
    (Join-Path $root "bin\$Configuration\ServerManager.dll")
)) {
    [Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null
}

function Assert-True([bool]$Value, [string]$Reason) {
    if (-not $Value) { throw $Reason }
}

function Write-CommonPeerInfo([IO.BinaryWriter]$Writer) {
    $Writer.Write([long]123456789)
    $Writer.Write('1.0.7')
    $Writer.Write([uint32]39)
    $Writer.Write([single]1); $Writer.Write([single]2); $Writer.Write([single]3)
    $Writer.Write('NewCharacter')
    $Writer.Write('PlayFab_76561198000000000')
    $Writer.Write([int]3); $Writer.Write([int]2); $Writer.Write($false)
}

function New-PeerInfo([bool]$ReceivingOnServer) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
    try {
        Write-CommonPeerInfo $writer
        if ($ReceivingOnServer) {
            $writer.Write('password-hash')
            $writer.Write('invite-secret')
            $ticket = [byte[]](1, 2, 3, 4, 5, 6)
            $writer.Write($ticket.Length); $writer.Write($ticket)
        } else {
            $writer.Write('Dedicated World')
            $writer.Write([int]987654321)
            $writer.Write('seed-name')
            $writer.Write([long]456789123)
            $writer.Write([int]2)
            $writer.Write([double]1234.5)
        }
        $writer.Flush()
        return $stream.ToArray()
    } finally {
        $writer.Dispose(); $stream.Dispose()
    }
}

$type = [ServerManager.BoundedPeerInfoRpcTransport]
$method = $type.GetMethod('TryPreflightPeerInfo',
    [Reflection.BindingFlags]'Static, NonPublic')
Assert-True ($null -ne $method) 'The PeerInfo preflight entry point is missing.'
$envelopeMethod = $type.GetMethod('TryPreflightPeerInfoEnvelope',
    [Reflection.BindingFlags]'Static, NonPublic')
Assert-True ($null -ne $envelopeMethod) 'The PeerInfo envelope preflight entry point is missing.'

function Invoke-Preflight([byte[]]$Payload, [bool]$ReceivingOnServer) {
    $arguments = [object[]]@($Payload, 0, $Payload.Length, $ReceivingOnServer, $null)
    $accepted = [bool]$method.Invoke($null, $arguments)
    return [pscustomobject]@{ Accepted = $accepted; Rejection = $arguments[4] }
}

function Invoke-EnvelopePreflight([byte[]]$Payload, [bool]$ReceivingOnServer) {
    $body = [byte[]]::new($Payload.Length + 4)
    [Array]::Copy([BitConverter]::GetBytes($Payload.Length), 0, $body, 0, 4)
    [Array]::Copy($Payload, 0, $body, 4, $Payload.Length)
    $arguments = [object[]]@($body, 0, $body.Length, $ReceivingOnServer,
        [int](512 * 1024), $null)
    $accepted = [bool]$envelopeMethod.Invoke($null, $arguments)
    return [pscustomobject]@{ Accepted = $accepted; Rejection = $arguments[5] }
}

foreach ($direction in @($true, $false)) {
    $payload = New-PeerInfo $direction
    $valid = Invoke-Preflight $payload $direction
    Assert-True ($valid.Accepted -and $null -eq $valid.Rejection) `
        'A complete Valheim 1.0.7 PeerInfo payload was rejected.'
    $validEnvelope = Invoke-EnvelopePreflight $payload $direction
    Assert-True ($validEnvelope.Accepted -and $null -eq $validEnvelope.Rejection) `
        'A complete length-prefixed Valheim 1.0.7 PeerInfo RPC body was rejected.'

    $trailing = [byte[]]::new($payload.Length + 1)
    [Array]::Copy($payload, $trailing, $payload.Length)
    $extra = Invoke-Preflight $trailing $direction
    Assert-True (-not $extra.Accepted -and
        $extra.Rejection.Code.ToString() -eq 'TrailingPacketData') `
        'Actual trailing PeerInfo data was not rejected.'

    $truncated = Invoke-Preflight ([byte[]]$payload[0..($payload.Length - 2)]) $direction
    Assert-True (-not $truncated.Accepted) 'A truncated PeerInfo payload was accepted.'
}

# Pin the preflight layout to the target game's actual SendPeerInfo writer.
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    (Join-Path $managed 'assembly_valheim.dll'))
try {
    $send = $definition.MainModule.GetType('ZNet').Methods |
        Where-Object { $_.Name -eq 'SendPeerInfo' -and $_.Parameters.Count -eq 2 } |
        Select-Object -First 1
    Assert-True ($null -ne $send) 'Valheim ZNet.SendPeerInfo(ZRpc,string) is missing.'
    $instructions = @($send.Body.Instructions)
    function Offset-OfCall([string]$TypeName, [string]$MethodName) {
        $instruction = $instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.Name -eq $TypeName -and
            $_.Operand.Name -eq $MethodName
        } | Select-Object -First 1
        if ($null -eq $instruction) { return -1 }
        return $instruction.Offset
    }
    $nameOffset = Offset-OfCall 'PlayerProfile' 'GetName'
    $simulationOffset = Offset-OfCall 'SimulationDistance' 'Serialize'
    $playFabOffset = ($instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'PlayFabUniqueId'
    } | Select-Object -First 1).Offset
    $inviteOffset = ($instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'm_inviteSecretKey'
    } | Select-Object -First 1).Offset
    Assert-True ($nameOffset -ge 0 -and $playFabOffset -gt $nameOffset -and
        $simulationOffset -gt $playFabOffset -and
        $inviteOffset -gt $simulationOffset) `
        'The target game PeerInfo field order no longer matches the bounded decoder.'
} finally {
    $definition.Dispose()
}

Write-Host 'PASS: Valheim 1.0.7 PeerInfo client/server layouts, truncation and trailing-data checks.'
