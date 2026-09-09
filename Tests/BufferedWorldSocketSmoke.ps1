param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
$references = @('UnityEngine.CoreModule.dll', 'UnityEngine.dll', 'assembly_utils.dll', 'assembly_valheim.dll') |
    ForEach-Object { Join-Path $managedRoot $_ }
foreach ($path in $references) { [Reflection.Assembly]::LoadFrom($path) | Out-Null }
Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.Collections.Generic;
public sealed class DirectRpcSocketFixture : ISocket
{
    public readonly Queue<ZPackage> Incoming = new Queue<ZPackage>();
    public readonly List<ZPackage> Sent = new List<ZPackage>();
    public bool IsConnected() { return true; }
    public void Send(ZPackage value) { Sent.Add(value); }
    public ZPackage Recv() { return Incoming.Count == 0 ? null : Incoming.Dequeue(); }
    public int GetSendQueueSize() { return 0; }
    public int GetCurrentSendRate() { return 0; }
    public bool IsHost() { return false; }
    public void Dispose() { }
    public bool GotNewData() { return Incoming.Count != 0; }
    public void Close() { }
    public string GetEndPointString() { return "offline-fixture"; }
    public void GetAndResetStats(out int sent, out int received) { sent = received = 0; }
    public void GetConnectionQuality(out float local, out float remote, out int ping, out float sent, out float received)
    { local = remote = sent = received = 0; ping = 0; }
    public ISocket Accept() { return null; }
    public int GetHostPort() { return 0; }
    public bool Flush() { return true; }
    public string GetHostName() { return "offline-fixture"; }
    public void VersionMatch() { }
    public static ZPackage Packet(string name)
    {
        byte[] bytes = new byte[8];
        Buffer.BlockCopy(BitConverter.GetBytes(name == null ? 0 : StringExtensionMethods.GetStableHashCode(name)), 0, bytes, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(12345), 0, bytes, 4, 4);
        ZPackage value = new ZPackage(bytes);
        value.SetPos(4);
        return value;
    }
}
'@
$plugin = [Reflection.Assembly]::LoadFrom($pluginPath)
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$gateType = $plugin.GetType('ServerManager.BufferedWorldSocket', $true)
$runtimeType = $plugin.GetType('ServerManager.ServerManagerRuntime', $true)
$gateConstructor = $gateType.GetConstructors($instance)[0]
$script:checks = 0
function Assert-True([bool]$Condition, [string]$Message) {
    ++$script:checks
    if (-not $Condition) { throw $Message }
}
function New-Fixture([int]$MaximumBytes = 1048576) {
    $original = [DirectRpcSocketFixture]::new()
    $gate = $script:gateConstructor.Invoke([object[]]@($original, $MaximumBytes))
    return [pscustomobject]@{ Original = $original; Gate = $gate }
}
function Invoke-Gate($Fixture, [string]$Method, [object[]]$Arguments = @()) {
    return $script:gateType.GetMethod($Method, $script:instance).Invoke($Fixture.Gate, $Arguments)
}
function Gate-Property($Fixture, [string]$Name) {
    return $script:gateType.GetProperty($Name, $script:instance).GetValue($Fixture.Gate, $null)
}
function Release-Gate($Fixture) {
    $arguments = [object[]]@($null)
    Assert-True ([bool]$script:gateType.GetMethod('TryRelease', $script:instance).Invoke($Fixture.Gate, $arguments)) `
        ('Ready release failed: ' + $arguments[0])
}
function Rpc-Name([string]$Field) {
    return [string]$script:runtimeType.GetField($Field, $script:static).GetRawConstantValue()
}
function Packet([string]$Name) { return [DirectRpcSocketFixture]::Packet($Name) }
$protocol = Rpc-Name 'ProtocolRpcName'
$detection = Rpc-Name 'DetectionRpcName'
$adminEntitlement = Rpc-Name 'AdminEntitlementRpcName'
$adminCommand = Rpc-Name 'AdminCommandRpcName'
$eventReport = Rpc-Name 'EventReportRpcName'
$eventDisplay = Rpc-Name 'EventDisplayRpcName'
$modRpcs = @('RPC_Jotunn_ReceiveVersionData', 'ServerSync VersionCheck', 'org.bepinex.plugins.example ConfigSync',
    'Jotunn_CustomRPC', 'Jotunn_RPC_InitialSync', 'ServerSyncVersionCheck', 'ServerSync', 'tests.future.direct.sync')
$unsafeRpcs = @('SavePlayerProfile', 'RoutedRPC', 'ZDOData', 'RefPos', 'CharacterID', 'ServerSyncedPlayerData',
    'PlayerList', 'AdminList', 'RemotePrint', 'NetTime', 'Kick', 'Ban', 'Unban', 'RPC_RemoteCommand', 'Save',
    'PrintBanned', $adminEntitlement, $adminCommand)
$globalBudgetFields = @('_globalHeldInboundBytes', '_globalHeldOutboundBytes', '_globalHeldOutboundPackages')
$initialBudgets = @{}
foreach ($field in $globalBudgetFields) { $initialBudgets[$field] = $gateType.GetField($field, $static).GetValue($null) }

# Unknown direct RPCs are delivered immediately in both initial-login phases.
# The same object/position assertions guard against hidden queue-copy work.
foreach ($peerInfoAdmitted in @($false, $true)) {
    $fixture = New-Fixture 8
    try {
        if ($peerInfoAdmitted) { $null = Invoke-Gate $fixture 'MarkPeerInfoAdmitted' }
        foreach ($name in $modRpcs) {
            $outbound = Packet $name
            $null = Invoke-Gate $fixture 'Send' @($outbound)
            Assert-True ([object]::ReferenceEquals($outbound, $fixture.Original.Sent[$fixture.Original.Sent.Count - 1]) -and
                $outbound.GetPos() -eq 4 -and (Gate-Property $fixture 'BufferedBytes') -eq 0) `
                "Initial outbound mod RPC was delayed/copied or consumed: $name"
            $inbound = Packet $name
            $fixture.Original.Incoming.Enqueue($inbound)
            $received = Invoke-Gate $fixture 'Recv'
            Assert-True ([object]::ReferenceEquals($inbound, $received) -and $received.GetPos() -eq 4 -and
                (Gate-Property $fixture 'BufferedBytes') -eq 0) "Initial inbound mod RPC was delayed/copied: $name"
        }
        Assert-True (-not (Gate-Property $fixture 'Overflowed') -and -not (Gate-Property $fixture 'InboundViolation')) `
            'Allowed mod RPCs consumed the one-packet held-traffic budget or caused a violation.'
    }
    finally { $null = Invoke-Gate $fixture 'Dispose' }
}

$fixture = New-Fixture
try {
    $jotunnVersion = Packet 'RPC_Jotunn_ReceiveVersionData'
    $clientHandshake = Packet 'ClientHandshake'
    $peerInfo = Packet 'PeerInfo'
    foreach ($value in @($jotunnVersion, $clientHandshake, $peerInfo)) { $null = Invoke-Gate $fixture 'Send' @($value) }
    Assert-True ($fixture.Original.Sent.Count -eq 2 -and
        [object]::ReferenceEquals($jotunnVersion, $fixture.Original.Sent[0]) -and
        [object]::ReferenceEquals($clientHandshake, $fixture.Original.Sent[1]) -and
        (Gate-Property $fixture 'BufferedBytes') -eq $peerInfo.Size()) `
        'Jotunn version delivery was reordered behind ClientHandshake or released vanilla PeerInfo too early.'
    $null = Invoke-Gate $fixture 'MarkPeerInfoAdmitted'
    Release-Gate $fixture
    Assert-True ($fixture.Original.Sent.Count -eq 3 -and $fixture.Original.Sent[2].GetPos() -eq 4) `
        'Vanilla PeerInfo was lost or overtook the earlier mod/vanilla handshakes.'
}
finally { $null = Invoke-Gate $fixture 'Dispose' }

foreach ($name in @($detection, $eventReport, $eventDisplay, 'ServerHandshake')) {
    $fixture = New-Fixture
    try {
        $value = Packet $name
        $null = Invoke-Gate $fixture 'Send' @($value)
        Assert-True ($fixture.Original.Sent.Count -eq 0 -and (Gate-Property $fixture 'BufferedBytes') -eq $value.Size()) `
            "Own event/control outbound ordering was weakened: $name"
    }
    finally { $null = Invoke-Gate $fixture 'Dispose' }
}

# Core world/control and ServerManager administrative RPCs retain their gate.
foreach ($name in $unsafeRpcs) {
    foreach ($peerInfoAdmitted in @($false, $true)) {
        $fixture = New-Fixture
        try {
            if ($peerInfoAdmitted) { $null = Invoke-Gate $fixture 'MarkPeerInfoAdmitted' }
            $fixture.Original.Incoming.Enqueue((Packet $name))
            Assert-True ($null -eq (Invoke-Gate $fixture 'Recv') -and (Gate-Property $fixture 'InboundViolation')) `
                "Unsafe inbound RPC passed during initial login: $name"
            $releaseArguments = [object[]]@($null)
            Assert-True (-not $gateType.GetMethod('TryRelease', $instance).Invoke($fixture.Gate, $releaseArguments)) `
                'Unsafe inbound traffic no longer prevents readiness.'
        }
        finally { $null = Invoke-Gate $fixture 'Dispose' }
    }
    $fixture = New-Fixture
    try {
        $outbound = Packet $name
        $null = Invoke-Gate $fixture 'Send' @($outbound)
        Assert-True ($fixture.Original.Sent.Count -eq 0 -and (Gate-Property $fixture 'BufferedBytes') -eq $outbound.Size()) `
            "Core/administrative outbound RPC bypassed character readiness: $name"
        $null = Invoke-Gate $fixture 'MarkPeerInfoAdmitted'
        Release-Gate $fixture
        Assert-True ($fixture.Original.Sent.Count -eq 1 -and $fixture.Original.Sent[0].GetPos() -eq 4 -and
            (Gate-Property $fixture 'BufferedBytes') -eq 0) 'Held core outbound traffic was lost or replayed incorrectly.'
    }
    finally { $null = Invoke-Gate $fixture 'Dispose' }
}

$fixture = New-Fixture
try {
    $handshake = Packet 'ServerHandshake'
    $fixture.Original.Incoming.Enqueue($handshake)
    Assert-True ([object]::ReferenceEquals($handshake, (Invoke-Gate $fixture 'Recv'))) 'The first server handshake was not admitted.'
    $fixture.Original.Incoming.Enqueue((Packet 'ServerHandshake'))
    Assert-True ($null -eq (Invoke-Gate $fixture 'Recv') -and (Gate-Property $fixture 'InboundViolation')) 'A duplicate server handshake passed.'
}
finally { $null = Invoke-Gate $fixture 'Dispose' }
foreach ($name in @('PeerInfo', 'ServerHandshake')) {
    $fixture = New-Fixture
    try {
        $null = Invoke-Gate $fixture 'MarkPeerInfoAdmitted'
        $fixture.Original.Incoming.Enqueue((Packet $name))
        Assert-True ($null -eq (Invoke-Gate $fixture 'Recv') -and (Gate-Property $fixture 'InboundViolation')) `
            "A repeated post-PeerInfo handshake/control RPC passed: $name"
    }
    finally { $null = Invoke-Gate $fixture 'Dispose' }
}

# Own event traffic and wrong-direction vanilla controls keep existing replay
# order, while third-party direct handshakes need not wait behind them.
foreach ($name in @($eventReport, $eventDisplay, 'ClientHandshake', 'Error', 'Kicked')) {
    $fixture = New-Fixture
    try {
        $held = Packet $name
        $fixture.Original.Incoming.Enqueue($held)
        Assert-True ($null -eq (Invoke-Gate $fixture 'Recv') -and (Gate-Property $fixture 'BufferedBytes') -eq $held.Size()) `
            "Previously held inbound control/event traffic was released early: $name"
        $direct = Packet $modRpcs[0]
        $fixture.Original.Incoming.Enqueue($direct)
        Assert-True ([object]::ReferenceEquals($direct, (Invoke-Gate $fixture 'Recv'))) 'Held own traffic blocked a later third-party handshake.'
        $null = Invoke-Gate $fixture 'MarkPeerInfoAdmitted'
        Release-Gate $fixture
        $replayed = Invoke-Gate $fixture 'Recv'
        Assert-True ($null -ne $replayed -and $replayed.GetPos() -eq 4 -and
            [Convert]::ToBase64String($replayed.GetArray()) -ceq [Convert]::ToBase64String($held.GetArray()) -and
            (Gate-Property $fixture 'BufferedBytes') -eq 0 -and $null -eq (Invoke-Gate $fixture 'Recv')) `
            'Retained inbound control/event replay was lost, altered or duplicated.'
    }
    finally { $null = Invoke-Gate $fixture 'Dispose' }
}

$fixture = New-Fixture 8
try {
    $fixture.Original.Incoming.Enqueue((Packet $eventReport))
    $fixture.Original.Incoming.Enqueue((Packet $eventReport))
    Assert-True ($null -eq (Invoke-Gate $fixture 'Recv') -and (Gate-Property $fixture 'Overflowed') -and
        (Gate-Property $fixture 'BufferedBytes') -eq 0) 'Still-held own events lost their bounded overflow cleanup.'
}
finally { $null = Invoke-Gate $fixture 'Dispose' }
$fixture = New-Fixture
try {
    $malformed = [ZPackage]::new([byte[]]@(1, 2, 3))
    $null = Invoke-Gate $fixture 'Send' @($malformed)
    Assert-True ($fixture.Original.Sent.Count -eq 0) 'A malformed short outbound frame was treated as an unknown direct RPC.'
    $fixture.Original.Incoming.Enqueue($malformed)
    Assert-True ($null -eq (Invoke-Gate $fixture 'Recv') -and (Gate-Property $fixture 'InboundViolation')) `
        'A malformed short inbound frame was treated as an unknown direct RPC.'
}
finally { $null = Invoke-Gate $fixture 'Dispose' }

$fixture = New-Fixture
try {
    $null = Invoke-Gate $fixture 'MarkPeerInfoAdmitted'
    Release-Gate $fixture
    foreach ($name in $modRpcs) {
        $value = Packet $name
        $null = Invoke-Gate $fixture 'Send' @($value)
        $fixture.Original.Incoming.Enqueue($value)
        Assert-True ([object]::ReferenceEquals($value, (Invoke-Gate $fixture 'Recv'))) 'Ordinary Ready traffic became restricted.'
    }
    Assert-True ([bool](Invoke-Gate $fixture 'TryRestrictInboundToDetection')) 'Detection-only fixture could not engage.'
    foreach ($name in @($modRpcs + $protocol + 'ZDOData')) { $fixture.Original.Incoming.Enqueue((Packet $name)) }
    $detectionValue = Packet $detection
    $fixture.Original.Incoming.Enqueue($detectionValue)
    Assert-True ([object]::ReferenceEquals($detectionValue, (Invoke-Gate $fixture 'Recv')) -and $fixture.Original.Incoming.Count -eq 0) `
        'Detection-only mode now admits mod/protocol/gameplay traffic.'
    $null = Invoke-Gate $fixture 'ReleaseDetectionOnlyInbound'
    $restored = Packet $modRpcs[0]
    $fixture.Original.Incoming.Enqueue($restored)
    Assert-True ([object]::ReferenceEquals($restored, (Invoke-Gate $fixture 'Recv'))) 'Leaving detection-only mode did not restore Ready compatibility.'
}
finally { $null = Invoke-Gate $fixture 'Dispose' }

$fixture = New-Fixture
try {
    $null = Invoke-Gate $fixture 'MarkPeerInfoAdmitted'
    Release-Gate $fixture
    Assert-True ([bool](Invoke-Gate $fixture 'TryRestrictOutboundForFinalSave')) 'Final-save outbound drain could not engage.'
    foreach ($name in @($modRpcs + 'ZDOData' + $eventDisplay + $adminEntitlement)) {
        $null = Invoke-Gate $fixture 'Send' @((Packet $name))
    }
    Assert-True ($fixture.Original.Sent.Count -eq 0 -and (Gate-Property $fixture 'BufferedBytes') -eq 0) `
        'Final-save outbound gating reused the broad pre-ready compatibility policy.'
    foreach ($name in @($protocol, 'Disconnect', 'Error', 'Kicked')) {
        $null = Invoke-Gate $fixture 'Send' @((Packet $name))
    }
    Assert-True ($fixture.Original.Sent.Count -eq 4) 'Final-save outbound control traffic was blocked.'
    $preFinalFragment = Packet $modRpcs[0]
    $fixture.Original.Incoming.Enqueue($preFinalFragment)
    Assert-True ([object]::ReferenceEquals($preFinalFragment, (Invoke-Gate $fixture 'Recv'))) `
        'Phase-one final drain discarded client traffic before the ordered final-save fragment.'
    Assert-True ([bool](Invoke-Gate $fixture 'TryCompleteFinalSaveRestriction')) 'Final-save inbound barrier could not engage.'
    foreach ($name in @($modRpcs + 'ZDOData' + $eventReport + $adminCommand)) { $fixture.Original.Incoming.Enqueue((Packet $name)) }
    $finalControl = Packet $protocol
    $fixture.Original.Incoming.Enqueue($finalControl)
    Assert-True ([object]::ReferenceEquals($finalControl, (Invoke-Gate $fixture 'Recv')) -and $fixture.Original.Incoming.Count -eq 0) `
        'Final-save inbound barrier now admits third-party RPCs or gameplay.'
    $finalDetection = Packet $detection
    $fixture.Original.Incoming.Enqueue($finalDetection)
    Assert-True ([object]::ReferenceEquals($finalDetection, (Invoke-Gate $fixture 'Recv'))) 'Final-save detection traffic was lost.'
}
finally { $null = Invoke-Gate $fixture 'Dispose' }

$fixture = New-Fixture
try {
    $null = Invoke-Gate $fixture 'Discard'
    for ($index = 0; $index -lt 40; ++$index) { $fixture.Original.Incoming.Enqueue((Packet $modRpcs[0])) }
    Assert-True ($null -eq (Invoke-Gate $fixture 'Recv') -and $fixture.Original.Incoming.Count -eq 8) `
        'Quarantine admits mod traffic or lost its bounded per-poll discard budget.'
    Assert-True ($null -eq (Invoke-Gate $fixture 'Recv') -and $fixture.Original.Incoming.Count -eq 0) 'Quarantine did not finish discarding queued mod traffic.'
    $null = Invoke-Gate $fixture 'Send' @((Packet $modRpcs[0]))
    Assert-True ($fixture.Original.Sent.Count -eq 0) 'Quarantine transmits third-party RPCs.'
}
finally { $null = Invoke-Gate $fixture 'Dispose' }

foreach ($field in $globalBudgetFields) {
    Assert-True ($gateType.GetField($field, $static).GetValue($null) -eq $initialBudgets[$field]) "Socket gate leaked its global budget: $field"
}
Write-Host "Direct RPC socket compatibility smoke tests passed ($script:checks checks)."
