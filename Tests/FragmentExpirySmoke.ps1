param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"

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

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$gameAssemblyPath = Join-Path $GamePath `
    "valheim_Data\Managed\assembly_valheim.dll"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"

Assert-True (Test-Path -LiteralPath $pluginPath) `
    "Build ServerManager before running this smoke test."
Assert-True (Test-Path -LiteralPath $gameAssemblyPath) `
    "The Valheim runtime assembly was not found."
Assert-True (Test-Path -LiteralPath $cecilPath) `
    "Mono.Cecil from BepInEx was not found."

[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
$pluginDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $pluginPath)
$runtimeDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ServerManagerRuntime" |
    Select-Object -First 1
Assert-True ($null -ne $runtimeDefinition) `
    "ServerManagerRuntime was not found."

$tickDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "Tick" |
    Select-Object -First 1
$processExpiryDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "ProcessExpiredFragmentAssemblies" |
    Select-Object -First 1
Assert-True ($null -ne $tickDefinition -and
    $null -ne $processExpiryDefinition) `
    "The fragment-expiry runtime pump is missing."

$tickExpiryCalls = @(
    $tickDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerRuntime" -and
            $_.Operand.Name -eq "ProcessExpiredFragmentAssemblies"
        })
Assert-True ($tickExpiryCalls.Count -eq 1) `
    "Runtime Tick no longer pumps fragment expiry exactly once."

$expiryCalls = @(
    $processExpiryDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$cleanupCalls = @(
    $expiryCalls |
        Where-Object {
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.BoundedFragmentReassembler" -and
            $_.Operand.Name -eq "CleanupExpired"
        })
$serverRejectCall = $expiryCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $_.Operand.Name -eq "SendServerRejection"
    } |
    Select-Object -First 1
$clientFailCall = $expiryCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $_.Operand.Name -eq "FailClient"
    } |
    Select-Object -First 1
$peerReads = @(
    $expiryCalls |
        Where-Object {
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ExpiredFragmentAssembly" -and
            $_.Operand.Name -eq "get_PeerRpc"
        })
$diagnosticReads = @(
    $expiryCalls |
        Where-Object {
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ExpiredFragmentAssembly" -and
            $_.Operand.Name -eq "get_MessageIdDiagnostic"
        })
Assert-True (
    $cleanupCalls.Count -eq 2 -and
    $null -ne $serverRejectCall -and
    $null -ne $clientFailCall -and
    $cleanupCalls[0].Offset -lt $serverRejectCall.Offset -and
    $serverRejectCall.Offset -lt $cleanupCalls[1].Offset -and
    $cleanupCalls[1].Offset -lt $clientFailCall.Offset -and
    $peerReads.Count -ge 3 -and
    $diagnosticReads.Count -eq 2) `
    "Expired server/client assemblies are no longer routed to terminal handlers."

$gameAssembly = [Reflection.Assembly]::LoadFrom($gameAssemblyPath)
$pluginAssembly = [Reflection.Assembly]::LoadFrom($pluginPath)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;

public sealed class MutableProtocolClockProxy : RealProxy
{
    private readonly Type _interfaceType;

    public MutableProtocolClockProxy(Type interfaceType, long frequency)
        : base(interfaceType)
    {
        if (frequency <= 0)
        {
            throw new ArgumentOutOfRangeException("frequency");
        }

        Frequency = frequency;
        _interfaceType = interfaceType;
    }

    public long Timestamp { get; set; }

    public long Frequency { get; private set; }

    public object Clock
    {
        get { return GetTransparentProxy(); }
    }

    public override IMessage Invoke(IMessage message)
    {
        IMethodCallMessage call = (IMethodCallMessage)message;
        object result;
        switch (call.MethodName)
        {
            case "GetTimestamp":
                result = Timestamp;
                break;
            case "get_Frequency":
                result = Frequency;
                break;
            case "GetType":
                result = _interfaceType;
                break;
            case "ToString":
                result = _interfaceType.FullName;
                break;
            case "GetHashCode":
                result = GetHashCode();
                break;
            case "Equals":
                result = ReferenceEquals(Clock, call.Args[0]);
                break;
            default:
                throw new MissingMethodException(call.MethodName);
        }

        return new ReturnMessage(
            result,
            null,
            0,
            call.LogicalCallContext,
            call);
    }
}

public static class ExpiryResultProbe
{
    public static bool RejectsMutation(object result)
    {
        System.Collections.IList list = result as System.Collections.IList;
        if (list == null || !list.IsReadOnly) return false;
        int count = list.Count;
        try { list.Add(null); return false; }
        catch (NotSupportedException) { }
        try { list.Clear(); return false; }
        catch (NotSupportedException) { }
        return list.Count == count;
    }

    public static long MeasureIdleAllocations(Func<object> fragments, Func<object> sessions)
    {
        System.Reflection.MethodInfo counter = typeof(GC).GetMethod(
            "GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
        if (counter == null) return -1;
        Func<long> allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), counter);
        // Use production clocks and direct delegates: reflection/proxy/PowerShell
        // allocations must not be counted as transport housekeeping work.
        for (int index = 0; index < 512; ++index) { fragments(); sessions(); }
        allocated();
        long before = allocated();
        for (int index = 0; index < 4096; ++index) { fragments(); sessions(); }
        return allocated() - before;
    }

    public static bool ThrowsIfDisposed(Func<object> operation)
    {
        try { operation(); return false; }
        catch (ObjectDisposedException) { return true; }
    }
}
"@

$connectionLimitsType = $pluginAssembly.GetType(
    "ServerManager.ConnectionProtocolLimits",
    $true)
$fragmentLimitsType = $pluginAssembly.GetType(
    "ServerManager.FragmentTransportLimits",
    $true)
$clockType = $pluginAssembly.GetType(
    "ServerManager.IProtocolClock",
    $true)
$codecType = $pluginAssembly.GetType(
    "ServerManager.BoundedFragmentCodec",
    $true)
$reassemblerType = $pluginAssembly.GetType(
    "ServerManager.BoundedFragmentReassembler",
    $true)
$protocolCodecType = $pluginAssembly.GetType(
    "ServerManager.ProtocolPacketCodec",
    $true)
$zRpcType = $gameAssembly.GetType("ZRpc", $true)

$connectionArguments = [object[]]::new(6)
$connectionArguments[0] = 512 * 1024
$connectionArguments[1] = 256 * 1024
$connectionArguments[2] = 256 * 1024
$connectionArguments[3] = 512
$connectionLimits = $connectionLimitsType.GetConstructors()[0].Invoke(
    $connectionArguments)

$fragmentArguments = [object[]]::new(8)
$fragmentArguments[0] = 4
$fragmentArguments[1] = 4
$fragmentArguments[2] = 16
$fragmentArguments[3] = 16
$fragmentArguments[4] = 4
$fragmentArguments[5] = 1
$fragmentArguments[6] = 64
$fragmentArguments[7] = [TimeSpan]::FromSeconds(5)
$fragmentLimits = $fragmentLimitsType.GetConstructors()[0].Invoke(
    $fragmentArguments)

$createTransfer = $codecType.GetMethod(
    "CreateCharacterTransfer",
    [Reflection.BindingFlags]"Static,Public")
$decodeProtocol = $protocolCodecType.GetMethods(
    [Reflection.BindingFlags]"Static,Public,NonPublic") |
    Where-Object {
        $_.Name -eq "TryDecode" -and
        $_.GetParameters().Count -eq 4
    } |
    Select-Object -First 1
$acceptPackage = $reassemblerType.GetMethod("AcceptPackage")
$cleanupExpired = $reassemblerType.GetMethod("CleanupExpired")
$removePeer = $reassemblerType.GetMethod("RemovePeer")
$disposeReassembler = $reassemblerType.GetMethod("Dispose")
$reassemblerConstructor = $reassemblerType.GetConstructors()[0]
Assert-True (
    $null -ne $createTransfer -and
    $null -ne $decodeProtocol -and
    $null -ne $acceptPackage -and
    $null -ne $cleanupExpired -and
    $null -ne $removePeer -and
    $null -ne $disposeReassembler) `
    "The fragment transport behavior seam changed."

$sessionId = [byte[]]::new(16)
$nonce = [byte[]]::new(32)
for ($index = 0; $index -lt $sessionId.Length; ++$index) {
    $sessionId[$index] = [byte]($index + 1)
}
for ($index = 0; $index -lt $nonce.Length; ++$index) {
    $nonce[$index] = [byte](255 - $index)
}

function New-Peer {
    return [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
        $script:zRpcType)
}

function New-PartialTransfer {
    param([byte]$Marker)

    $payload = [byte[]]::new(8)
    for ($index = 0; $index -lt $payload.Length; ++$index) {
        $payload[$index] = [byte]($Marker + $index)
    }

    $transfer = $script:createTransfer.Invoke(
        $null,
        [object[]]@(
            $script:sessionId,
            $script:nonce,
            $payload,
            $false,
            $script:connectionLimits,
            $script:fragmentLimits))
    Assert-True ($transfer.Packets.Count -eq 2) `
        "The fragment fixture did not create a partial two-packet transfer."

    $decodeArguments = [object[]]::new(4)
    $decodeArguments[0] = $transfer.Packets[0]
    $decodeArguments[1] = $script:connectionLimits
    $decoded = [bool]$script:decodeProtocol.Invoke(
        $null,
        $decodeArguments)
    Assert-True $decoded "A valid first fragment could not be decoded."

    return [pscustomobject]@{
        Plan = $transfer
        Packet = $decodeArguments[2]
    }
}

function Add-PartialAssembly {
    param(
        [object]$Reassembler,
        [object]$Peer,
        [byte]$Marker
    )

    $transfer = New-PartialTransfer $Marker
    $accepted = $script:acceptPackage.Invoke(
        $Reassembler,
        [object[]]@(
            $Peer,
            $script:sessionId,
            $script:nonce,
            $transfer.Packet))
    Assert-True ($accepted.Status.ToString() -eq "InProgress") `
        "A valid first fragment did not start an assembly."
    return $transfer
}

$script:zRpcType = $zRpcType
$script:createTransfer = $createTransfer
$script:decodeProtocol = $decodeProtocol
$script:acceptPackage = $acceptPackage
$script:sessionId = $sessionId
$script:nonce = $nonce
$script:connectionLimits = $connectionLimits
$script:fragmentLimits = $fragmentLimits

$compressionFragmentArguments = [object[]]::new(8)
$compressionFragmentArguments[0] = 1024
$compressionFragmentArguments[1] = 8
$compressionFragmentArguments[2] = 8 * 1024
$compressionFragmentArguments[3] = 8 * 1024
$compressionFragmentArguments[4] = 4
$compressionFragmentArguments[5] = 1
$compressionFragmentArguments[6] = 32 * 1024
$compressionFragmentArguments[7] = [TimeSpan]::FromSeconds(5)
$compressionLimits = $fragmentLimitsType.GetConstructors()[0].Invoke(
    $compressionFragmentArguments)
$compressiblePayload = [byte[]]::new(4 * 1024)
for ($index = 0; $index -lt $compressiblePayload.Length; ++$index) {
    $compressiblePayload[$index] = 0x41
}

$compressedTransfer = $createTransfer.Invoke(
    $null,
    [object[]]@(
        $sessionId,
        $nonce,
        $compressiblePayload,
        $true,
        $connectionLimits,
        $compressionLimits))
Assert-True (
    $compressedTransfer.Encoding.ToString() -eq "GZip" -and
    $compressedTransfer.EncodedLength -lt $compressedTransfer.DecodedLength -and
    $compressedTransfer.DecodedLength -eq $compressiblePayload.Length -and
    $compressedTransfer.Packets.Count -eq 1) `
    "A compressible character save did not produce one bounded GZip transfer."

$compressionClock = [MutableProtocolClockProxy]::new($clockType, 1)
$compressionClock.Timestamp = 0
$compressionReassembler = $reassemblerConstructor.Invoke(
    [object[]]@(
        $compressionLimits,
        $compressionClock.Clock,
        $true,
        $null))
try {
    $decodeArguments = [object[]]::new(4)
    $decodeArguments[0] = $compressedTransfer.Packets[0]
    $decodeArguments[1] = $connectionLimits
    Assert-True ([bool]$decodeProtocol.Invoke($null, $decodeArguments)) `
        "The compressed character fragment could not be decoded."
    $completedCompression = $acceptPackage.Invoke(
        $compressionReassembler,
        [object[]]@(
            (New-Peer),
            $sessionId,
            $nonce,
            $decodeArguments[2]))
    Assert-True (
        $completedCompression.Status.ToString() -eq "Completed" -and
        (Test-ByteArrayEqual `
            ([byte[]]$completedCompression.Payload) `
            $compressiblePayload)) `
        "A bounded GZip character save did not round-trip exactly."
}
finally {
    $disposeReassembler.Invoke(
        $compressionReassembler,
        [object[]]@())
}

$tightFragmentArguments = [object[]]::new(8)
$tightFragmentArguments[0] = 1024
$tightFragmentArguments[1] = 8
$tightFragmentArguments[2] = 8 * 1024
$tightFragmentArguments[3] = 1024
$tightFragmentArguments[4] = 4
$tightFragmentArguments[5] = 1
$tightFragmentArguments[6] = 32 * 1024
$tightFragmentArguments[7] = [TimeSpan]::FromSeconds(5)
$tightLimits = $fragmentLimitsType.GetConstructors()[0].Invoke(
    $tightFragmentArguments)
$bombDecodeArguments = [object[]]::new(4)
$bombDecodeArguments[0] = $compressedTransfer.Packets[0]
$bombDecodeArguments[1] = $connectionLimits
Assert-True ([bool]$decodeProtocol.Invoke($null, $bombDecodeArguments)) `
    "The compression-limit fixture could not be decoded."
$bombPacket = $bombDecodeArguments[2]
$decodedLengthOffset = 2 + $compressedTransfer.MessageId.Length + (3 * 4)
$declaredLength = [BitConverter]::GetBytes([int]1024)
[Array]::Copy(
    $declaredLength,
    0,
    $bombPacket.Payload,
    $decodedLengthOffset,
    $declaredLength.Length)

$tightClock = [MutableProtocolClockProxy]::new($clockType, 1)
$tightClock.Timestamp = 0
$tightReassembler = $reassemblerConstructor.Invoke(
    [object[]]@($tightLimits, $tightClock.Clock, $true, $null))
try {
    $bombResult = $acceptPackage.Invoke(
        $tightReassembler,
        [object[]]@(
            (New-Peer),
            $sessionId,
            $nonce,
            $bombPacket))
    Assert-True (
        $bombResult.Status.ToString() -eq "Rejected" -and
        $null -ne $bombResult.Rejection -and
        $bombResult.Rejection.Code.ToString() -eq
            "DecompressedPayloadTooLarge") `
        "A compressed character payload that expanded past its declared bound was not rejected."
}
finally {
    $disposeReassembler.Invoke($tightReassembler, [object[]]@())
}

$clockProxy = [MutableProtocolClockProxy]::new($clockType, 1)
$clockProxy.Timestamp = 100
$reassembler = $reassemblerConstructor.Invoke(
    [object[]]@($fragmentLimits, $clockProxy.Clock, $true, $null))
try {
    $emptyFragments = $cleanupExpired.Invoke($reassembler, [object[]]@())
    Assert-True ([ExpiryResultProbe]::RejectsMutation($emptyFragments) -and
        [object]::ReferenceEquals($emptyFragments,
            $cleanupExpired.Invoke($reassembler, [object[]]@()))) `
        "Idle fragment expiry did not reuse an immutable empty result."
    $peer = New-Peer
    $partial = Add-PartialAssembly $reassembler $peer 10
    $clockProxy.Timestamp = 105
    $atDeadline = $cleanupExpired.Invoke($reassembler, [object[]]@())
    Assert-True ($atDeadline.Count -eq 0 -and
        [object]::ReferenceEquals($atDeadline, $emptyFragments)) `
        "A fragment assembly expired at its exact deadline."

    $clockProxy.Timestamp = 106
    $afterDeadline = $cleanupExpired.Invoke($reassembler, [object[]]@())
    Assert-True ($afterDeadline.Count -eq 1 -and
        [ExpiryResultProbe]::RejectsMutation($afterDeadline)) `
        "An expired fragment assembly was silently dropped."
    $expired = $afterDeadline[0]
    $expectedDiagnostic = [Convert]::ToBase64String(
        [byte[]]$partial.Plan.MessageId)
    Assert-True (
        [object]::ReferenceEquals($expired.PeerRpc, $peer) -and
        $expired.MessageIdDiagnostic -eq $expectedDiagnostic -and
        $expired.MessageIdDiagnostic.Length -eq 24 -and
        -not $expired.MessageIdDiagnostic.Contains("`r") -and
        -not $expired.MessageIdDiagnostic.Contains("`n")) `
        "Fragment expiry did not retain the exact peer and bounded diagnostic."
    Assert-True (
        [object]::ReferenceEquals($emptyFragments,
            $cleanupExpired.Invoke($reassembler, [object[]]@())) -and
        $afterDeadline.Count -eq 1) `
        "A fragment expiry diagnostic was delivered more than once."
}
finally {
    $disposeReassembler.Invoke($reassembler, [object[]]@())
}

$suppressionClock = [MutableProtocolClockProxy]::new($clockType, 1)
$suppressionClock.Timestamp = 0
$suppressionReassembler = $reassemblerConstructor.Invoke(
    [object[]]@(
        $fragmentLimits,
        $suppressionClock.Clock,
        $true,
        $null))
try {
    $removedPeer = New-Peer
    $activePeer = New-Peer
    Add-PartialAssembly $suppressionReassembler $removedPeer 30 | Out-Null
    $suppressionClock.Timestamp = 6
    Add-PartialAssembly $suppressionReassembler $activePeer 50 | Out-Null

    $removedCount = [int]$removePeer.Invoke(
        $suppressionReassembler,
        [object[]]@($removedPeer))
    Assert-True ($removedCount -eq 0) `
        "A queued-only peer expiry unexpectedly retained an active assembly."
    Assert-True (
        $cleanupExpired.Invoke(
            $suppressionReassembler,
            [object[]]@()).Count -eq 0) `
        "RemovePeer did not suppress the peer's queued expiry diagnostic."
    Assert-True (
        [int]$removePeer.Invoke(
            $suppressionReassembler,
            [object[]]@($activePeer)) -eq 1) `
        "The independent active peer assembly was not retained."
}
finally {
    $disposeReassembler.Invoke(
        $suppressionReassembler,
        [object[]]@())
}

$boundedClock = [MutableProtocolClockProxy]::new($clockType, 1)
$boundedClock.Timestamp = 0
$boundedReassembler = $reassemblerConstructor.Invoke(
    [object[]]@($fragmentLimits, $boundedClock.Clock, $true, $null))
try {
    for ($index = 0; $index -lt 4; ++$index) {
        Add-PartialAssembly `
            $boundedReassembler `
            (New-Peer) `
            ([byte](70 + $index * 10)) | Out-Null
    }

    $boundedClock.Timestamp = 6
    $boundedExpirations = $cleanupExpired.Invoke(
        $boundedReassembler,
        [object[]]@())
    Assert-True ($boundedExpirations.Count -eq 4) `
        "The bounded expiry queue lost an admitted assembly diagnostic."
    foreach ($boundedExpiry in $boundedExpirations) {
        Assert-True (
            $boundedExpiry.MessageIdDiagnostic.Length -eq 24) `
            "An expiry diagnostic exceeded its fixed identifier size."
    }
    Assert-True (
        $cleanupExpired.Invoke(
            $boundedReassembler,
            [object[]]@()).Count -eq 0) `
        "The bounded expiry queue retained drained diagnostics."
}
finally {
    $disposeReassembler.Invoke($boundedReassembler, [object[]]@())
}

# A full queue of old diagnostics must drain before newly expired assemblies
# are discovered. One call can therefore return both bounded generations.
$queuedClock = [MutableProtocolClockProxy]::new($clockType, 1)
$queuedReassembler = $reassemblerConstructor.Invoke(
    [object[]]@($fragmentLimits, $queuedClock.Clock, $true, $null))
try {
    for ($index = 0; $index -lt 4; ++$index) {
        Add-PartialAssembly $queuedReassembler (New-Peer) ([byte](20 + $index * 10)) | Out-Null
    }
    $queuedClock.Timestamp = 6
    $newlyExpiredPeer = New-Peer
    Add-PartialAssembly $queuedReassembler $newlyExpiredPeer 70 | Out-Null
    $queuedClock.Timestamp = 12
    $bothGenerations = $cleanupExpired.Invoke($queuedReassembler, [object[]]@())
    Assert-True ($bothGenerations.Count -eq 5 -and
        [object]::ReferenceEquals($bothGenerations[4].PeerRpc, $newlyExpiredPeer) -and
        [ExpiryResultProbe]::RejectsMutation($bothGenerations) -and
        [object]::ReferenceEquals($emptyFragments,
            $cleanupExpired.Invoke($queuedReassembler, [object[]]@()))) `
        "Lazy expiry results changed pending-first order, bounded queue draining or one-time delivery."
}
finally {
    $disposeReassembler.Invoke($queuedReassembler, [object[]]@())
}

# Production fixes the connection ceiling at 120 seconds, independently of
# the 30-second lifetime of one partial character transfer. Seed sessions at
# the coordinator boundary so this test never needs a live Unity/Steam host.
$instanceFlags = [Reflection.BindingFlags]"Instance,Public,NonPublic"
$runtimeInitializer = $runtimeDefinition.Methods |
    Where-Object Name -eq ".cctor" | Select-Object -First 1
function Get-FixedRuntimeTimeout {
    param([string]$Name, [int]$ExpectedSeconds)

    $store = $runtimeInitializer.Body.Instructions |
        Where-Object {
            $_.OpCode.Name -eq "stsfld" -and
            $_.Operand -is [Mono.Cecil.FieldReference] -and
            $_.Operand.Name -eq $Name
        } | Select-Object -First 1
    Assert-True ($null -ne $store -and
        $store.Previous.Operand -is [Mono.Cecil.MethodReference] -and
        $store.Previous.Operand.Name -eq "FromSeconds" -and
        [double]$store.Previous.Previous.Operand -eq $ExpectedSeconds) `
        "The fixed runtime timeout $Name is not $ExpectedSeconds seconds."
    return [TimeSpan]::FromSeconds([double]$store.Previous.Previous.Operand)
}
$handshakeTimeout = Get-FixedRuntimeTimeout "ConnectionHandshakeTimeout" 120
$fragmentTimeout = Get-FixedRuntimeTimeout "CharacterFragmentAssemblyTimeout" 30
$fixedConnectionArguments = [object[]]$connectionArguments.Clone()
$fixedConnectionArguments[4] = $handshakeTimeout
$fixedConnectionArguments[5] = $handshakeTimeout
$fixedConnectionLimits = $connectionLimitsType.GetConstructors()[0].Invoke(
    $fixedConnectionArguments)
$fixedFragmentArguments = [object[]]$fragmentArguments.Clone()
$fixedFragmentArguments[7] = $fragmentTimeout
$fixedFragmentLimits = $fragmentLimitsType.GetConstructors()[0].Invoke(
    $fixedFragmentArguments)
Assert-True ($fixedConnectionLimits.PhaseTimeout.TotalSeconds -eq 120 -and
    $fixedConnectionLimits.OverallHandshakeTimeout.TotalSeconds -eq 120 -and
    $fixedFragmentLimits.AssemblyTimeout.TotalSeconds -eq 30) `
    "The behavior fixture did not retain the separate handshake/fragment limits."

$coordinatorType = $pluginAssembly.GetType(
    "ServerManager.ConnectionSessionCoordinator", $true)
$coordinatorSessionType = $coordinatorType.GetNestedType("Session", $instanceFlags)
$connectionStateType = $pluginAssembly.GetType(
    "ServerManager.ConnectionSessionState", $true)
$packetKindType = $pluginAssembly.GetType("ServerManager.ProtocolPacketKind", $true)
$packetType = $pluginAssembly.GetType("ServerManager.ProtocolPacket", $true)
$fixedClock = [MutableProtocolClockProxy]::new($clockType, 1)
$fixedClock.Timestamp = 0
$coordinator = $coordinatorType.GetConstructors()[0].Invoke(
    [object[]]@($fixedConnectionLimits, $fixedFragmentLimits, $fixedClock.Clock))
$sessionDictionary = $coordinatorType.GetField("sessions", $instanceFlags).GetValue(
    $coordinator)
$refreshDeadline = $coordinatorType.GetMethod("RefreshPhaseDeadline", $instanceFlags)
$validateIncoming = $coordinatorType.GetMethod("ValidateIncomingLocked", $instanceFlags)
$expireHandshake = $coordinatorType.GetMethod("ExpireTimedOutSessions")
$getConnectionSnapshot = $coordinatorType.GetMethod("TryGetSnapshot")
$isReadyAndAuthenticated = $coordinatorType.GetMethod("IsReadyAndAuthenticated", $instanceFlags)

function Add-HandshakeFixture {
    param([string]$StateName)

    $session = [Activator]::CreateInstance($coordinatorSessionType, $true)
    $peer = New-Peer
    $fields = @{
        Rpc = $peer
        State = [Enum]::Parse($connectionStateType, $StateName)
        SessionId = [byte[]]$sessionId.Clone()
        Nonce = [byte[]]$nonce.Clone()
        LastSequence = [uint32]4
        CreatedTimestamp = [long]0
        AbsoluteDeadline = [long]120
        ServerCharactersEnabled = $true
        CharacterMessageId = [byte[]]$sessionId.Clone()
    }
    foreach ($fieldName in $fields.Keys) {
        $coordinatorSessionType.GetField($fieldName, $instanceFlags).SetValue(
            $session, $fields[$fieldName])
    }
    $refreshDeadline.Invoke($coordinator, [object[]]@($session, [long]0)) | Out-Null
    $sessionDictionary.GetType().GetMethod("Add").Invoke(
        $sessionDictionary, [object[]]@($peer, $session)) | Out-Null
    return [pscustomobject]@{ Peer = $peer; Session = $session }
}

$fixedReassembler = $reassemblerConstructor.Invoke(
    [object[]]@($fixedFragmentLimits, $fixedClock.Clock, $true, $null))
try {
    $emptySessions = $expireHandshake.Invoke($coordinator, [object[]]@())
    Assert-True ([ExpiryResultProbe]::RejectsMutation($emptySessions) -and
        [object]::ReferenceEquals($emptySessions,
            $expireHandshake.Invoke($coordinator, [object[]]@()))) `
        "Idle handshake expiry did not reuse an immutable empty result."
    $waitingSessions = @()
    foreach ($stateName in @("Connected", "Challenged", "ManifestValidated", "CharacterSent")) {
        $fixture = Add-HandshakeFixture $stateName
        $waitingSessions += $fixture
        Assert-True (-not $isReadyAndAuthenticated.Invoke($coordinator, [object[]]@($fixture.Peer))) `
            "The allocation-free readiness query admitted $stateName."
        Assert-True ([long]$coordinatorSessionType.GetField(
            "PhaseDeadline", $instanceFlags).GetValue($fixture.Session) -eq 120) `
            "$stateName did not receive the finite 120-second deadline."
        $refreshDeadline.Invoke($coordinator, [object[]]@($fixture.Session, [long]100)) |
            Out-Null
        Assert-True ([long]$coordinatorSessionType.GetField(
            "PhaseDeadline", $instanceFlags).GetValue($fixture.Session) -eq 120) `
            "Refreshing $stateName incorrectly extended the overall handshake ceiling."
    }

    $quickSession = Add-HandshakeFixture "CharacterSent"
    $readyKind = [Enum]::Parse($packetKindType, "ReadyAck")
    $readyPacket = $packetType.GetConstructors()[0].Invoke([object[]]@(
        $readyKind, [uint32]5, $sessionId, $nonce, $sessionId))
    $quickValidation = $validateIncoming.Invoke($coordinator, [object[]]@(
        $quickSession.Peer, $readyPacket,
        [Enum]::Parse($connectionStateType, "CharacterSent"), $readyKind, [uint32]5))
    Assert-True ($quickValidation.Succeeded -and $fixedClock.Timestamp -eq 0) `
        "A valid ReadyAck cannot validate immediately; 120 seconds must be a ceiling, not a required wait."
    # Ready is a separately seeded terminal-success fixture. The compiled ACK
    # transition below verifies that normal acceptance enters this state.
    $coordinatorSessionType.GetField("State", $instanceFlags).SetValue(
        $quickSession.Session, [Enum]::Parse($connectionStateType, "Ready"))
    Assert-True (-not $isReadyAndAuthenticated.Invoke($coordinator, [object[]]@($quickSession.Peer))) `
        "Ready without final PeerInfo authentication passed the policy polling gate."
    $coordinatorSessionType.GetField("PeerInfoAuthenticated", $instanceFlags).SetValue(
        $quickSession.Session, $true)
    Assert-True ($isReadyAndAuthenticated.Invoke($coordinator, [object[]]@($quickSession.Peer)) -and
        -not $isReadyAndAuthenticated.Invoke($coordinator, [object[]]@($null)) -and
        -not $isReadyAndAuthenticated.Invoke($coordinator, [object[]]@((New-Peer)))) `
        "The readiness query confused an authenticated Ready session with a missing session."

    $quickPeer = New-Peer
    $quickTransfer = Add-PartialAssembly $fixedReassembler $quickPeer 110
    $decodeQuickArguments = [object[]]@(
        $quickTransfer.Plan.Packets[1], $fixedConnectionLimits, $null, $null)
    Assert-True ([bool]$decodeProtocol.Invoke($null, $decodeQuickArguments)) `
        "The small immediate character transfer did not decode."
    $quickResult = $acceptPackage.Invoke($fixedReassembler, [object[]]@(
        $quickPeer, $sessionId, $nonce, $decodeQuickArguments[2]))
    Assert-True ($quickResult.Status.ToString() -eq "Completed" -and
        $fixedClock.Timestamp -eq 0) `
        "A small complete character transfer waited for a timeout instead of completing immediately."

    $partialPeer = $waitingSessions[0].Peer
    Add-PartialAssembly $fixedReassembler $partialPeer 130 | Out-Null
    $fixedClock.Timestamp = 30
    Assert-True ($cleanupExpired.Invoke($fixedReassembler, [object[]]@()).Count -eq 0) `
        "The production fragment lifetime expired before its exact 30-second boundary."
    $fixedClock.Timestamp = 31
    $partialExpired = $cleanupExpired.Invoke($fixedReassembler, [object[]]@())
    Assert-True ($partialExpired.Count -eq 1 -and
        [object]::ReferenceEquals($partialExpired[0].PeerRpc, $partialPeer) -and
        $expireHandshake.Invoke($coordinator, [object[]]@()).Count -eq 0) `
        "A stalled fragment must expire after 30 seconds even while the 120-second handshake is still open."

    $fixedClock.Timestamp = 120
    Assert-True ([object]::ReferenceEquals($emptySessions,
        $expireHandshake.Invoke($coordinator, [object[]]@()))) `
        "Handshake expiry became premature at the exact 120-second boundary."
    $expectedExpiryOrder = @($sessionDictionary.Values | Where-Object {
        $coordinatorSessionType.GetField("State", $instanceFlags).GetValue($_).ToString() -ne "Ready"
    } | ForEach-Object { $coordinatorSessionType.GetField("Rpc", $instanceFlags).GetValue($_) })
    $fixedClock.Timestamp = 121
    $expiredHandshakes = $expireHandshake.Invoke($coordinator, [object[]]@())
    Assert-True ($expiredHandshakes.Count -eq 4 -and
        [ExpiryResultProbe]::RejectsMutation($expiredHandshakes)) `
        "One or more pre-Ready phases escaped the finite overall handshake timeout."
    for ($index = 0; $index -lt $expectedExpiryOrder.Count; ++$index) {
        Assert-True ([object]::ReferenceEquals($expiredHandshakes[$index].Rpc,
            $expectedExpiryOrder[$index])) "Lazy handshake expiry changed result order."
    }
    foreach ($expiredHandshake in $expiredHandshakes) {
        Assert-True ($expiredHandshake.State.ToString() -eq "Rejected" -and
            $expiredHandshake.Rejection.Code.ToString() -eq "HandshakeTimedOut") `
            "An expired handshake did not fail closed with its timeout reason."
    }
    $readySnapshotArguments = [object[]]@($quickSession.Peer, $null)
    Assert-True ([bool]$getConnectionSnapshot.Invoke($coordinator, $readySnapshotArguments) -and
        $readySnapshotArguments[1].State.ToString() -eq "Ready" -and
        $null -eq $readySnapshotArguments[1].Rejection -and
        [object]::ReferenceEquals($emptySessions,
            $expireHandshake.Invoke($coordinator, [object[]]@()))) `
        "A Ready connection was timed out or terminal timeout diagnostics were delivered twice."
    Assert-True ($isReadyAndAuthenticated.Invoke($coordinator, [object[]]@($quickSession.Peer))) `
        "Policy polling expired a Ready connection after the handshake deadline."

    # The fast query must still observe expiry; moving a generation check ahead
    # of all coordinator access would silently lose this side effect.
    $lateSession = Add-HandshakeFixture "Challenged"
    Assert-True (-not $isReadyAndAuthenticated.Invoke($coordinator, [object[]]@($lateSession.Peer)) -and
        $coordinatorSessionType.GetField("State", $instanceFlags).GetValue($lateSession.Session).ToString() -eq "Rejected" -and
        $coordinatorSessionType.GetField("Rejection", $instanceFlags).GetValue($lateSession.Session).Code.ToString() -eq "HandshakeTimedOut") `
        "Policy polling stopped observing an expired handshake."
    $coordinatorType.GetMethod("RemoveSession").Invoke($coordinator, [object[]]@($quickSession.Peer)) | Out-Null
    Assert-True (-not $isReadyAndAuthenticated.Invoke($coordinator, [object[]]@($quickSession.Peer))) `
        "Policy polling retained readiness after disconnect cleanup."
}
finally {
    $disposeReassembler.Invoke($fixedReassembler, [object[]]@())
    $coordinatorType.GetMethod("Dispose").Invoke($coordinator, [object[]]@())
}

$idleReassembler = $reassemblerConstructor.Invoke(
    [object[]]@($fragmentLimits, $null, $true, $null))
$idleCoordinator = $coordinatorType.GetConstructors()[0].Invoke(
    [object[]]@($fixedConnectionLimits, $fixedFragmentLimits, $null))
$idleFragments = [Delegate]::CreateDelegate([Func[object]], $idleReassembler, $cleanupExpired)
$idleSessions = [Delegate]::CreateDelegate([Func[object]], $idleCoordinator, $expireHandshake)
try {
    Assert-True ([object]::ReferenceEquals($emptyFragments, $idleFragments.Invoke()) -and
        [object]::ReferenceEquals($emptySessions, $idleSessions.Invoke())) `
        "Empty expiry results were not safely shared across instances."
    $allocatedBytes = [ExpiryResultProbe]::MeasureIdleAllocations($idleFragments, $idleSessions)
    if ($allocatedBytes -lt 0) {
        Write-Output "SKIP: This runtime cannot measure per-thread idle expiry allocations."
    }
    else {
        Assert-True ($allocatedBytes -eq 0) `
            "Idle expiry allocated $allocatedBytes bytes across 4096 production-clock polls."
        Write-Output "Idle expiry allocation check passed: 0 bytes across 4096 paired polls."
    }
}
finally {
    $disposeReassembler.Invoke($idleReassembler, [object[]]@())
    $coordinatorType.GetMethod("Dispose").Invoke($idleCoordinator, [object[]]@())
}
Assert-True ([ExpiryResultProbe]::ThrowsIfDisposed($idleFragments) -and
    [ExpiryResultProbe]::ThrowsIfDisposed($idleSessions)) `
    "The cached empty result bypassed disposal validation."

$coordinatorDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ConnectionSessionCoordinator"
$acceptReadyDefinition = $coordinatorDefinition.Methods |
    Where-Object Name -eq "AcceptReadyAck" | Select-Object -First 1
$ackValidation = $acceptReadyDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "ValidateIncomingLocked"
    } | Select-Object -First 1
$readyStateStore = $acceptReadyDefinition.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "stfld" -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.Name -eq "State" -and
        $_.Previous.OpCode.Name -eq "ldc.i4.4"
    } | Select-Object -First 1
$ackDeadlineReads = @($acceptReadyDefinition.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "ldfld" -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.Name -match "Deadline|CreatedTimestamp"
    })
$ackDelayCalls = @($acceptReadyDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -match "^(Sleep|Delay|Wait|GetTimestamp)$"
    })
Assert-True ($null -ne $ackValidation -and $null -ne $readyStateStore -and
    $ackValidation.Offset -lt $readyStateStore.Offset -and
    $ackDeadlineReads.Count -eq 0 -and $ackDelayCalls.Count -eq 0) `
    "AcceptReadyAck no longer enters Ready immediately after valid input or introduced a minimum wait."

Write-Output (
    "Fixed 120-second handshake / 30-second fragment expiry, immediate ready validation and small transfer completion, Ready exemption, bounded GZip round-trip and expansion rejection, fragment expiry peer " +
    "identity, exact deadline, bounded diagnostic, RemovePeer suppression, " +
    "pending-first queue draining, immutable cached results, idle allocations, disposal checks, " +
    "queue bound, and runtime terminal-routing smoke tests passed.")
