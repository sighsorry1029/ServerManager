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

function Write-CommonPeerInfo([IO.BinaryWriter]$Writer,
    [string]$PlayerName = 'NewCharacter', [string]$PlayFabId = 'PlayFab_76561198000000000') {
    $Writer.Write([long]123456789)
    $Writer.Write('1.0.20')
    $Writer.Write([uint32]41)
    $Writer.Write([single]1); $Writer.Write([single]2); $Writer.Write([single]3)
    $Writer.Write($PlayerName)
    $Writer.Write($PlayFabId)
    $Writer.Write([int]3); $Writer.Write([int]2); $Writer.Write($false)
}

function New-PeerInfo(
    [bool]$ReceivingOnServer,
    [string]$Password = 'password-hash',
    [string]$InviteSecret = 'invite-secret',
    [string]$PlayerName = 'NewCharacter',
    [string]$PlayFabId = 'PlayFab_76561198000000000',
    [string]$WorldName = 'Dedicated World',
    [string]$SeedName = 'seed-name',
    [int]$TicketLength = 6) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
    try {
        Write-CommonPeerInfo $writer $PlayerName $PlayFabId
        if ($ReceivingOnServer) {
            $writer.Write($Password)
            $writer.Write($InviteSecret)
            $writer.Write($TicketLength)
            if ($TicketLength -gt 0) { $writer.Write([byte[]]::new($TicketLength)) }
        } else {
            $writer.Write($WorldName)
            $writer.Write([int]987654321)
            $writer.Write($SeedName)
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
$credentialsMethod = $type.GetMethod('TryReadServerCredentials',
    [Reflection.BindingFlags]'Static, NonPublic')
Assert-True ($null -ne $credentialsMethod) 'The bounded credential reader is missing.'

function Invoke-Preflight([byte[]]$Payload, [bool]$ReceivingOnServer) {
    $arguments = [object[]]@($Payload, 0, $Payload.Length, $ReceivingOnServer, $null, 0)
    $accepted = [bool]$method.Invoke($null, $arguments)
    return [pscustomobject]@{
        Accepted = $accepted; Rejection = $arguments[4]; ExtensionBytes = $arguments[5]
    }
}

function New-Envelope([byte[]]$Payload, [int]$DeclaredLength = $Payload.Length) {
    $body = [byte[]]::new($Payload.Length + 4)
    [Array]::Copy([BitConverter]::GetBytes($DeclaredLength), 0, $body, 0, 4)
    [Array]::Copy($Payload, 0, $body, 4, $Payload.Length)
    return ,$body
}

function Invoke-EnvelopePreflight([byte[]]$Body, [bool]$ReceivingOnServer,
    [int]$MaximumBytes = (512 * 1024)) {
    $arguments = [object[]]@($Body, 0, $Body.Length, $ReceivingOnServer,
        $MaximumBytes, $null, 0)
    $accepted = [bool]$envelopeMethod.Invoke($null, $arguments)
    return [pscustomobject]@{
        Accepted = $accepted; Rejection = $arguments[5]; ExtensionBytes = $arguments[6]
    }
}

function Add-Extension([byte[]]$Payload, [int]$Count) {
    $extended = [byte[]]::new($Payload.Length + $Count)
    [Array]::Copy($Payload, $extended, $Payload.Length)
    for ($index = $Payload.Length; $index -lt $extended.Length; ++$index) {
        $extended[$index] = [byte]($index % 256)
    }
    return ,$extended
}

foreach ($direction in @($true, $false)) {
    $payload = [byte[]](New-PeerInfo $direction)
    $valid = Invoke-Preflight $payload $direction
    Assert-True ($valid.Accepted -and $null -eq $valid.Rejection -and
        $valid.ExtensionBytes -eq 0) 'A complete Valheim 1.0.20 PeerInfo payload was rejected.'
    $body = New-Envelope $payload
    $validEnvelope = Invoke-EnvelopePreflight $body $direction
    Assert-True ($validEnvelope.Accepted -and $null -eq $validEnvelope.Rejection -and
        $validEnvelope.ExtensionBytes -eq 0) 'A complete PeerInfo RPC envelope was rejected.'

    foreach ($count in @(1, 29, 4096)) {
        $extended = Add-Extension $payload $count
        $before = [Convert]::ToBase64String($extended)
        $extra = Invoke-Preflight $extended $direction
        $extendedBody = New-Envelope $extended
        $envelopeBefore = [Convert]::ToBase64String($extendedBody)
        $extraEnvelope = Invoke-EnvelopePreflight $extendedBody $direction
        foreach ($result in @($extra, $extraEnvelope)) {
            if ($direction) {
                Assert-True (-not $result.Accepted -and $result.ExtensionBytes -eq 0 -and
                    $result.Rejection.Code.ToString() -eq 'TrailingPacketData') `
                    'Untrusted client-to-server trailing data was accepted.'
            } else {
                Assert-True ($result.Accepted -and $null -eq $result.Rejection -and
                    $result.ExtensionBytes -eq $count) `
                    'A bounded server-to-client extension was rejected or miscounted.'
            }
        }
        Assert-True ($before -ceq [Convert]::ToBase64String($extended) -and
            $envelopeBefore -ceq [Convert]::ToBase64String($extendedBody)) `
            'Preflight changed the original wire bytes.'
    }

    # Every incomplete vanilla prefix must fail, even in the tolerant direction.
    for ($length = 0; $length -lt $payload.Length; ++$length) {
        $prefix = [byte[]]::new($length)
        [Array]::Copy($payload, $prefix, $length)
        foreach ($result in @((Invoke-Preflight $prefix $direction),
            (Invoke-EnvelopePreflight (New-Envelope $prefix) $direction))) {
            Assert-True (-not $result.Accepted -and $result.ExtensionBytes -eq 0) `
                "An incomplete vanilla prefix of $length bytes was accepted."
        }
    }
    for ($length = 0; $length -lt 4; ++$length) {
        Assert-True (-not (Invoke-EnvelopePreflight ([byte[]]::new($length)) $direction).Accepted) `
            'A missing/truncated inner length prefix was accepted.'
    }
    foreach ($declared in @(-1, ($payload.Length - 1), ($payload.Length + 1), 524289)) {
        Assert-True (-not (Invoke-EnvelopePreflight (New-Envelope $payload $declared) $direction).Accepted) `
            'A negative, oversized, or mismatched inner length was accepted.'
    }
    Assert-True (Invoke-EnvelopePreflight $body $direction $payload.Length).Accepted `
        'The configured exact payload size boundary was rejected.'
    $limited = Invoke-EnvelopePreflight $body $direction ($payload.Length - 1)
    Assert-True (-not $limited.Accepted -and $limited.Rejection.Code.ToString() -eq 'PayloadTooLarge') `
        'The configured lower PeerInfo size limit was bypassed.'
    $oversized = Invoke-EnvelopePreflight (New-Envelope (Add-Extension $payload (524289 - $payload.Length))) $direction
    Assert-True (-not $oversized.Accepted -and $oversized.Rejection.Code.ToString() -eq 'PayloadTooLarge') `
        'The 512 KiB PeerInfo limit was bypassed.'

    foreach ($field in @('PlayerName', 'PlayFabId') + $(if ($direction) {
        @('Password', 'InviteSecret')
    } else { @('WorldName', 'SeedName') })) {
        $options = @{ ReceivingOnServer = $direction }
        $options[$field] = 'x' * 4096
        Assert-True (Invoke-Preflight (New-PeerInfo @options) $direction).Accepted `
            "The exact string boundary was rejected for $field."
        $options[$field] = 'x' * 4097
        Assert-True (-not (Invoke-Preflight (New-PeerInfo @options) $direction).Accepted) `
            "An oversized string was accepted for $field."
    }
    # Corrupt the version string's 7-bit encoded length, not its decoded contents.
    foreach ($lengthBytes in @([byte[]](255, 255, 255, 255, 15), [byte[]](128, 128, 128, 128, 128))) {
        $malformed = [byte[]]$payload.Clone()
        [Array]::Copy($lengthBytes, 0, $malformed, 8, $lengthBytes.Length)
        Assert-True (-not (Invoke-Preflight $malformed $direction).Accepted) `
            'A negative/overflowing 7-bit string length was accepted.'
    }
}

Assert-True (Invoke-Preflight (New-PeerInfo $true -TicketLength 65536) $true).Accepted `
    'The exact 64 KiB Steam ticket boundary was rejected.'
foreach ($ticketLength in @(-1, 65537)) {
    Assert-True (-not (Invoke-Preflight (New-PeerInfo $true -TicketLength $ticketLength) $true).Accepted) `
        'A negative or oversized Steam ticket length was accepted.'
}
$atHardLimit = Add-Extension (New-PeerInfo $false) (524288 - (New-PeerInfo $false).Length)
Assert-True (Invoke-EnvelopePreflight (New-Envelope $atHardLimit) $false).Accepted `
    'A server extension at exactly 512 KiB was rejected.'

# Exercise the real raw-transport dispatch boundary with an inert vanilla handler.
# Only its validator delegate is supplied by this fixture; it calls the production
# envelope parser. No game loop, network socket, Harmony patch or publicized DLL is used.
$rawType = [ServerManager.RawProtocolRpcTransport]
$registerPreflight = $rawType.GetMethod('RegisterPreflight', [Reflection.BindingFlags]'Static, NonPublic')
$beforeHandle = $rawType.GetMethod('BeforeHandlePackage', [Reflection.BindingFlags]'Static, NonPublic')
$validatorType = $type.Assembly.GetType('ServerManager.RawProtocolPreflightHandler', $true)
$debugField = [ZRpc].GetField('m_DEBUG', [Reflection.BindingFlags]'Static, NonPublic')
$previousDebug = $debugField.GetValue($null)
Add-Type -ReferencedAssemblies @(
    (Join-Path $managed 'assembly_valheim.dll'),
    (Join-Path $managed 'assembly_utils.dll'),
    (Join-Path $root "bin\$Configuration\ServerManager.dll")
) -TypeDefinition @'
using System;
using System.Reflection;
using ServerManager;
public static class PeerInfoRawPreflightFixture {
    public static bool ReceivingOnServer;
    public static ProtocolRejection Rejection;
    public static readonly MethodInfo Envelope = typeof(BoundedPeerInfoRpcTransport)
        .GetMethod("TryPreflightPeerInfoEnvelope", BindingFlags.Static | BindingFlags.NonPublic);
    public static bool Validate(ZRpc rpc, byte[] source, int offset, int length,
        out ProtocolRejection rejection) {
        object[] arguments = { source, offset, length, ReceivingOnServer, 512 * 1024, null, 0 };
        bool accepted = (bool)Envelope.Invoke(null, arguments);
        rejection = (ProtocolRejection)arguments[5];
        return accepted;
    }
    public static void Reject(ZRpc rpc, ProtocolRejection rejection) { Rejection = rejection; }
}
'@
try {
    foreach ($direction in @($true, $false)) {
        $rpc = [ZRpc]::new($null)
        $rpc.Register('PeerInfo', [ZRpc+RpcMethod+Method]{ param($ignored) })
        [PeerInfoRawPreflightFixture]::ReceivingOnServer = $direction
        $validator = [Delegate]::CreateDelegate($validatorType,
            [PeerInfoRawPreflightFixture].GetMethod('Validate'))
        $errorHandler = [Delegate]::CreateDelegate([ServerManager.RawProtocolTransportErrorHandler],
            [PeerInfoRawPreflightFixture].GetMethod('Reject'))
        $registerPreflight.Invoke($null, [object[]]@($rpc, 'PeerInfo',
            [int](512 * 1024 + 4), $validator, $errorHandler)) | Out-Null
        try {
            foreach ($debug in @($false, $true)) {
                $debugField.SetValue($null, $debug)
                foreach ($extensionLength in @(0, 1, 29)) {
                    $inner = Add-Extension (New-PeerInfo $direction) $extensionLength
                    $stream = [IO.MemoryStream]::new()
                    $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
                    try {
                        $writer.Write([byte[]](31, 32, 33)) # nonzero initial package cursor
                        $writer.Write([StringExtensionMethods]::GetStableHashCode('PeerInfo'))
                        if ($debug) { $writer.Write('PeerInfo') }
                        $writer.Write((New-Envelope $inner))
                        $writer.Flush()
                        $wire = $stream.ToArray()
                    } finally { $writer.Dispose(); $stream.Dispose() }
                    $packet = [ZPackage]::new($wire)
                    $packet.SetPos(3)
                    $before = [Convert]::ToBase64String($wire)
                    [PeerInfoRawPreflightFixture]::Rejection = $null
                    $passToVanilla = [bool]$beforeHandle.Invoke($null, [object[]]@($rpc, $packet))
                    $expected = -not $direction -or $extensionLength -eq 0
                    Assert-True ($passToVanilla -eq $expected) `
                        'Raw dispatch did not preserve the directional extension policy.'
                    if ($expected) {
                        Assert-True ($null -eq [PeerInfoRawPreflightFixture]::Rejection) `
                            'Raw dispatch reported an error for an accepted packet.'
                    } else {
                        Assert-True ([PeerInfoRawPreflightFixture]::Rejection.Code.ToString() -eq 'TrailingPacketData') `
                            'Raw dispatch lost the client extension rejection reason.'
                    }
                    Assert-True ($packet.GetPos() -eq 3 -and
                        [Convert]::ToBase64String($packet.GetArray()) -ceq $before) `
                        'Raw preflight changed the original package bytes or cursor.'
                }
            }
        } finally { $rawType::Unregister($rpc, 'PeerInfo') }
    }
} finally { $debugField.SetValue($null, $previousDebug) }

$serverBytes = [byte[]](New-PeerInfo $true)
$serverPackage = [Activator]::CreateInstance([ZPackage], [object[]]@(,$serverBytes))
$credentialArguments = [object[]]@($serverPackage, $null, $null)
Assert-True ([bool]$credentialsMethod.Invoke($null, $credentialArguments) -and
    $credentialArguments[1] -ceq 'password-hash' -and
    $credentialArguments[2] -ceq 'invite-secret' -and
    $serverPackage.GetPos() -eq 0) `
    'The bounded credential reader changed the packet or decoded the wrong fields.'
$badBytes = [byte[]](1, 2, 3)
$badPackage = [Activator]::CreateInstance([ZPackage], [object[]]@(,$badBytes))
$badCredentialArguments = [object[]]@($badPackage, $null, $null)
Assert-True (-not [bool]$credentialsMethod.Invoke($null, $badCredentialArguments) -and
    $badCredentialArguments[1] -ceq '' -and $badCredentialArguments[2] -ceq '') `
    'A malformed credential packet produced a password classification.'

$pluginAssembly = [AppDomain]::CurrentDomain.GetAssemblies() |
    Where-Object { $_.GetName().Name -eq 'ServerManager' } |
    Select-Object -First 1
$runtimeType = $pluginAssembly.GetType('ServerManager.ServerManagerRuntime', $true)
$wrongPasswordMethod = $runtimeType.GetMethod('IsPlainWrongServerPassword',
    [Reflection.BindingFlags]'Static, NonPublic')
$passwordField = [ZNet].GetField('m_serverPassword',
    [Reflection.BindingFlags]'Static, NonPublic')
Assert-True ($null -ne $wrongPasswordMethod -and $null -ne $passwordField) `
    'The pre-Steam password classification boundary is missing.'
$previousPassword = $passwordField.GetValue($null)
try {
    $passwordField.SetValue($null, 'expected-hash')
    function Is-PlainWrongPassword([string]$Password, [string]$InviteSecret) {
        $bytes = [byte[]](New-PeerInfo $true $Password $InviteSecret)
        $packet = [Activator]::CreateInstance([ZPackage], [object[]]@(,$bytes))
        return [bool]$wrongPasswordMethod.Invoke($null, [object[]]@($packet))
    }
    Assert-True (-not (Is-PlainWrongPassword 'expected-hash' '')) `
        'The correct password was classified as an early rejection.'
    Assert-True (Is-PlainWrongPassword 'wrong-hash' '') `
        'A plain wrong password would still enter Steam authentication.'
    Assert-True (-not (Is-PlainWrongPassword 'wrong-hash' 'invite-secret')) `
        'A possible vanilla invite-secret bypass was preempted.'
    Assert-True (-not [bool]$wrongPasswordMethod.Invoke(
        $null, [object[]]@($badPackage))) `
        'A malformed packet was misclassified as an ordinary password error.'
} finally {
    $passwordField.SetValue($null, $previousPassword)
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

Write-Host 'PASS: Valheim 1.0.20/network 41 PeerInfo, bounded server extensions, strict client packets, raw dispatch and credentials.'
