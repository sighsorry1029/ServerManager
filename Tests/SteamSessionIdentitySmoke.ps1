param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll', 'UnityEngine.dll', 'assembly_utils.dll',
    'SoftReferenceableAssets.dll', 'com.rlabrecque.steamworks.net.dll', 'Splatform.dll', 'assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (Split-Path -Parent $pluginPath) $name))) | Out-Null
}
Add-Type -ReferencedAssemblies @((Join-Path $managedRoot 'assembly_valheim.dll'), (Join-Path $managedRoot 'assembly_utils.dll'),
    (Join-Path $managedRoot 'com.rlabrecque.steamworks.net.dll')) -TypeDefinition @'
using System;
using Steamworks;
public static class SessionIdentityEnvironment {
    public static object Peer, RawSocket;
    public static bool Server = true, Connected = true, ThrowNative, ThrowEndpoint;
    public static ulong SteamId;
    public static int HostNameReads, EndpointReads, PeerIdReads;
    public static object FindPeer(object server, object rpc) { return Peer; }
    public static bool IsNull(object left, object right) { return ReferenceEquals(left, right); }
    public static bool IsServer(object server) { return Server; }
    public static bool IsConnected(object socket) {
        if (!ReferenceEquals(socket, RawSocket)) throw new InvalidOperationException("Untrusted socket used for liveness.");
        return Connected;
    }
    public static CSteamID GetPeerID(object socket) {
        if (!ReferenceEquals(socket, RawSocket)) throw new InvalidOperationException("Untrusted socket used for identity.");
        ++PeerIdReads;
        if (ThrowNative) throw new InvalidOperationException("Native fixture unavailable.");
        return new CSteamID(SteamId);
    }
    public static string GetEndPointString(object socket) {
        if (!ReferenceEquals(socket, RawSocket)) throw new InvalidOperationException("Untrusted socket used for endpoint.");
        if (ThrowEndpoint) throw new InvalidOperationException("Logging endpoint unavailable.");
        ++EndpointReads; return "raw-endpoint";
    }
}
public sealed class ForeignIdentitySocket : ISocket {
    public readonly ISocket Inner;
    public ForeignIdentitySocket(ISocket inner = null) { Inner = inner; }
    public bool IsConnected() { return Inner == null || Inner.IsConnected(); }
    public void Send(ZPackage package) { if (Inner != null) Inner.Send(package); }
    public ZPackage Recv() { return Inner == null ? null : Inner.Recv(); }
    public int GetSendQueueSize() { return 0; }
    public int GetCurrentSendRate() { return 0; }
    public bool IsHost() { return false; }
    public void Dispose() { }
    public bool GotNewData() { return false; }
    public void Close() { }
    public string GetEndPointString() { throw new InvalidOperationException("Do not trust the wrapper endpoint."); }
    public void GetAndResetStats(out int sent, out int received) { sent = received = 0; }
    public void GetConnectionQuality(out float local, out float remote, out int ping, out float sent, out float received)
    { local = remote = sent = received = 0; ping = 0; }
    public ISocket Accept() { return null; }
    public int GetHostPort() { return 0; }
    public bool Flush() { return true; }
    public string GetHostName() { ++SessionIdentityEnvironment.HostNameReads; return "76561198000000999"; }
    public void VersionMatch() { }
}
'@
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$runtimeName = 'ServerManager.ServerManagerRuntime'
$script:checks = 0
function Assert-True([bool]$Condition, [string]$Message) {
    ++$script:checks
    if (-not $Condition) { throw $Message }
}
function New-Uninitialized([Type]$Type) {
    $value = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($Type)
    [GC]::SuppressFinalize($value)
    return $value
}
function Set-Property($Object, [string]$Name, $Value) {
    $Object.GetType().GetProperty($Name, $script:instance).SetValue($Object, $Value, $null)
}

# Execute the actual reservation/phase/ref checks. Only process/native inputs
# are replaced in an in-memory fixture; no game, Steam session or server starts.
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
$memory = [IO.MemoryStream]::new()
try {
    $definition.Name.Name = 'SessionIdentityFixture_' + [Guid]::NewGuid().ToString('N')
    $module = $definition.MainModule
    $runtimeIL = $module.GetType($runtimeName)
    $helperIL = $runtimeIL.Methods | Where-Object Name -eq 'TryGetSteamConnection'
    $activeIL = $runtimeIL.Methods | Where-Object Name -eq 'TryResolveActiveDetectionPeer'
    $resolverIL = $module.GetType('ServerManager.ServerPeerResolver')
    Assert-True ($null -ne $helperIL -and $helperIL.HasBody) 'The reserved Steam identity boundary is missing.'
    $helperCalls = @($helperIL.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
    foreach ($name in @('TryResolvePeer', 'IsCurrentSteamAuthenticationLocked', 'IsConnected', 'GetPeerID', 'IsValid')) {
        Assert-True (@($helperCalls | Where-Object { $_.Operand.Name -eq $name }).Count -gt 0) "Steam identity boundary lost $name."
    }
    foreach ($method in @($helperIL, $activeIL) + @($resolverIL.Methods | Where-Object Name -eq 'TryResolve')) {
        Assert-True (@($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -in @('GetHostName', 'GetSocket', 'get_Original')
        }).Count -eq 0 -and @($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'm_socket'
        }).Count -eq 0) 'Later identity validation again pins or unwraps a replaceable outer socket.'
    }
    $initial = $runtimeIL.Methods | Where-Object Name -eq 'TryReserveSteamAuthentication'
    Assert-True (@($initial.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'isinst' -and $_.Operand.FullName -eq 'ZSteamSocket'
    }).Count -gt 0 -and @($initial.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'GetSocket'
    }).Count -gt 0) 'Initial reservation no longer requires the raw connection-bound Steam socket.'
    $initializer = $runtimeIL.Methods | Where-Object Name -eq '.cctor'
    $initializer.Body.Instructions.Clear(); $initializer.Body.ExceptionHandlers.Clear(); $initializer.Body.Variables.Clear()
    $initializer.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
    foreach ($field in $runtimeIL.Fields) { $field.IsInitOnly = $false }
    foreach ($method in @($helperIL, $activeIL) + @($resolverIL.Methods | Where-Object HasBody)) {
        foreach ($instruction in @($method.Body.Instructions)) {
            if ($instruction.Operand -isnot [Mono.Cecil.MethodReference]) { continue }
            $call = $instruction.Operand
            $replacement = $null
            $castType = $null
            if ($call.DeclaringType.FullName -eq 'ZSteamSocket' -and $call.Name -in @('IsConnected', 'GetPeerID', 'GetEndPointString')) {
                $replacement = $call.Name
            } elseif ($call.DeclaringType.FullName -eq 'ZNet' -and $call.Name -eq 'IsServer') {
                $replacement = 'IsServer'
            } elseif ($call.DeclaringType.FullName -eq 'UnityEngine.Object' -and $call.Name -eq 'op_Equality') {
                $replacement = 'IsNull'
            } elseif ($call.DeclaringType.FullName -eq 'ServerManager.ValheimPrivateAccess' -and $call.Name -eq 'FindPeer') {
                $replacement = 'FindPeer'; $castType = [ZNetPeer]
            }
            if ($null -eq $replacement) { continue }
            $instruction.OpCode = [Mono.Cecil.Cil.OpCodes]::Call
            $instruction.Operand = $module.ImportReference([SessionIdentityEnvironment].GetMethod($replacement))
            if ($null -ne $castType) {
                $method.Body.GetILProcessor().InsertAfter($instruction, [Mono.Cecil.Cil.Instruction]::Create(
                    [Mono.Cecil.Cil.OpCodes]::Castclass, $module.ImportReference($castType)))
            }
        }
    }
    $definition.Write($memory)
    $assembly = [Reflection.Assembly]::Load($memory.ToArray())
}
finally { $memory.Dispose(); $definition.Dispose() }
$runtime = $assembly.GetType($runtimeName, $true)
$attemptType = $assembly.GetType($runtimeName + '+SteamAuthenticationAttempt', $true)
$phaseType = $assembly.GetType($runtimeName + '+SteamAuthenticationPhase', $true)
$resolver = $assembly.GetType('ServerManager.ServerPeerResolver', $true)
$gateType = $assembly.GetType('ServerManager.BufferedWorldSocket', $true)
$server = New-Uninitialized ([ZNet])
$rpc = New-Uninitialized ([ZRpc])
$peer = New-Uninitialized ([ZNetPeer])
$rawSocket = New-Uninitialized ([ZSteamSocket])
$peer.m_rpc = $rpc; $peer.m_uid = 42; $peer.m_playerName = 'SelectedProfile'
$peer.m_socket = [ForeignIdentitySocket]::new($rawSocket)
[ZRpc].GetField('m_socket', $instance).SetValue($rpc, [ForeignIdentitySocket]::new([ForeignIdentitySocket]::new($rawSocket)))
[SessionIdentityEnvironment]::Peer = $peer
[SessionIdentityEnvironment]::RawSocket = $rawSocket
[SessionIdentityEnvironment]::SteamId = [uint64]76561198000000001
[ZNet].GetField('m_onlineBackend', $static).SetValue($null, [Enum]::Parse([OnlineBackendType], 'Steamworks'))
$steamId = [Steamworks.CSteamID]::new([SessionIdentityEnvironment]::SteamId)
$attempt = $attemptType.GetConstructors($instance)[0].Invoke([object[]]@($peer, $rpc, $rawSocket, $steamId, [long]::MaxValue))
$runtime.GetField('SteamAuthenticationGate', $static).SetValue($null, [object]::new())
$rpcField = $runtime.GetField('SteamAuthenticationsByRpc', $static)
$idField = $runtime.GetField('SteamAuthenticationsById', $static)
$byRpc = [Activator]::CreateInstance($rpcField.FieldType)
$byId = [Activator]::CreateInstance($idField.FieldType)
$rpcField.SetValue($null, $byRpc); $idField.SetValue($null, $byId)
$byRpc.Add($rpc, $attempt); $byId.Add([SessionIdentityEnvironment]::SteamId, $attempt)
$worldField = $runtime.GetField('WorldBuffers', $static)
$world = [Activator]::CreateInstance($worldField.FieldType)
$gate = $gateType.GetConstructors($instance)[0].Invoke([object[]]@([ForeignIdentitySocket]::new($rawSocket), 1048576))
$world.Add($rpc, $gate); $worldField.SetValue($null, $world)
$helper = $runtime.GetMethod('TryGetSteamConnection', $static)
$active = $runtime.GetMethod('TryResolveActiveDetectionPeer', $static)
$resolve = $resolver.GetMethod('TryResolve', $static)
function Invoke-Identity($Method, $RequestedRpc = $script:rpc) {
    $arguments = [object[]]@($script:server, $RequestedRpc, $null, $null)
    $success = $Method.Invoke($null, $arguments)
    return [pscustomobject]@{ Success = $success; Value = $arguments[2]; Rejection = $arguments[3] }
}
function Set-Phase([string]$Phase) { Set-Property $script:attempt 'Phase' ([Enum]::Parse($script:phaseType, $Phase)) }

Set-Phase 'Reserved'
Assert-True ((Invoke-Identity $helper).Success) 'A current initial reservation cannot supply the early handshake identity.'
$resolved = Invoke-Identity $resolve
Assert-True ($resolved.Success -and $resolved.Value.HostId -ceq '76561198000000001' -and
    $resolved.Value.Endpoint -ceq 'raw-endpoint' -and $resolved.Value.PlayerName -ceq 'SelectedProfile' -and
    [SessionIdentityEnvironment]::HostNameReads -eq 0) 'A foreign wrapper changed the reservation-derived identity or endpoint.'
[SessionIdentityEnvironment]::ThrowEndpoint = $true
$resolved = Invoke-Identity $resolve
Assert-True ($resolved.Success -and $resolved.Value.HostId -ceq '76561198000000001' -and
    $resolved.Value.Endpoint -ceq '' -and [SessionIdentityEnvironment]::HostNameReads -eq 0) 'A logging-only endpoint failure replaced or rejected the trusted Steam identity.'
[SessionIdentityEnvironment]::ThrowEndpoint = $false
Assert-True (-not (Invoke-Identity $active).Success) 'Reserved identity was treated as final active authentication.'
Set-Phase 'Active'
Set-Property $attempt 'EnqueuedCallbackCount' 1
Set-Property $attempt 'ProcessedCallbackCount' 1
foreach ($name in @('BeginAuthInvocationObserved', 'BeginAuthResultRecorded', 'BeginAuthImmediateAccepted')) { Set-Property $attempt $name $true }
Set-Property $attempt 'LatestResponse' ([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
Assert-True ((Invoke-Identity $active).Success) 'An authenticated session rejected harmless peer/RPC outer wrappers.'
$peer.m_socket = [ForeignIdentitySocket]::new([ForeignIdentitySocket]::new($gate))
[ZRpc].GetField('m_socket', $instance).SetValue($rpc, [ForeignIdentitySocket]::new($gate))
Assert-True ((Invoke-Identity $active).Success -and [SessionIdentityEnvironment]::HostNameReads -eq 0) 'Replacing outer wrappers invalidated a pinned active Steam session.'

foreach ($case in @(
    @{ Name = 'EnqueuedCallbackCount'; Invalid = 0; Valid = 1 },
    @{ Name = 'EnqueuedCallbackCount'; Invalid = 2; Valid = 1 },
    @{ Name = 'ProcessedCallbackCount'; Invalid = 0; Valid = 1 },
    @{ Name = 'ProcessedCallbackCount'; Invalid = 2; Valid = 1 },
    @{ Name = 'CallbackOverflowed'; Invalid = $true; Valid = $false },
    @{ Name = 'StaleCallbackCaptured'; Invalid = $true; Valid = $false },
    @{ Name = 'LateCallbackCaptured'; Invalid = $true; Valid = $false },
    @{ Name = 'BeginAuthInvocationObserved'; Invalid = $false; Valid = $true },
    @{ Name = 'BeginAuthResultRecorded'; Invalid = $false; Valid = $true },
    @{ Name = 'BeginAuthImmediateAccepted'; Invalid = $false; Valid = $true },
    @{ Name = 'BeginAuthExecutionFaulted'; Invalid = $true; Valid = $false },
    @{ Name = 'DuplicateBeginAuthInvocation'; Invalid = $true; Valid = $false },
    @{ Name = 'LatestResponse'; Invalid = [Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseAuthTicketInvalid; Valid = [Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK }
)) {
    Set-Property $attempt $case.Name $case.Invalid
    Assert-True (-not (Invoke-Identity $active).Success) ('Invalid final callback state was accepted: ' + $case.Name)
    Set-Property $attempt $case.Name $case.Valid
}
foreach ($phase in @('Reserved', 'PeerInfoPending', 'VanillaAccepted', 'Rejected')) {
    Set-Phase $phase
    Assert-True (-not (Invoke-Identity $active).Success) "Non-active phase was accepted by the final verifier: $phase"
}
Set-Phase 'Rejected'
Assert-True (-not (Invoke-Identity $helper).Success) 'A rejected reservation still supplied a trusted connection identity.'
Set-Phase 'Active'
$peer.m_socket = [ForeignIdentitySocket]::new()
[ZRpc].GetField('m_socket', $instance).SetValue($rpc, [ForeignIdentitySocket]::new())
Assert-True ($peer.m_socket.IsConnected() -and $rpc.GetSocket().IsConnected()) 'Disconnected-raw fixture requires connected-looking outer sockets.'
[SessionIdentityEnvironment]::Connected = $false
Assert-True (-not (Invoke-Identity $helper).Success -and -not (Invoke-Identity $active).Success) 'A disconnected pinned Steam socket was accepted.'
[SessionIdentityEnvironment]::Connected = $true
$peer.m_socket = [ForeignIdentitySocket]::new([ForeignIdentitySocket]::new($gate))
[ZRpc].GetField('m_socket', $instance).SetValue($rpc, [ForeignIdentitySocket]::new($gate))
foreach ($changedId in @([uint64]0, [uint64]76561198000000999)) {
    [SessionIdentityEnvironment]::SteamId = $changedId
    Assert-True (-not (Invoke-Identity $helper).Success -and -not (Invoke-Identity $resolve).Success) 'A changed/invalid underlying Steam ID replaced the reservation.'
}
[SessionIdentityEnvironment]::SteamId = $steamId.m_SteamID
[SessionIdentityEnvironment]::ThrowNative = $true
Assert-True (-not (Invoke-Identity $helper).Success) 'Native socket identity failure did not fail closed.'
[SessionIdentityEnvironment]::ThrowNative = $false
Assert-True (-not (Invoke-Identity $helper (New-Uninitialized ([ZRpc]))).Success) 'An unrelated RPC borrowed another connection reservation.'
$foreignPeer = New-Uninitialized ([ZNetPeer]); $foreignPeer.m_rpc = $rpc
[SessionIdentityEnvironment]::Peer = $foreignPeer
Assert-True (-not (Invoke-Identity $helper).Success) 'A replacement registered peer borrowed the old reservation.'
[SessionIdentityEnvironment]::Peer = $peer
$peer.m_rpc = New-Uninitialized ([ZRpc])
Assert-True (-not (Invoke-Identity $helper).Success) 'A registered peer with a different RPC was accepted.'
$peer.m_rpc = $rpc
$byId.Remove($steamId.m_SteamID) | Out-Null
Assert-True (-not (Invoke-Identity $helper).Success) 'A reservation missing the Steam-ID index was accepted.'
$otherAttempt = $attemptType.GetConstructors($instance)[0].Invoke([object[]]@($peer, $rpc, $rawSocket, $steamId, [long]::MaxValue))
$byId.Add($steamId.m_SteamID, $otherAttempt)
Assert-True (-not (Invoke-Identity $helper).Success) 'A superseded by-ID reservation was accepted by the old RPC generation.'
$byId[$steamId.m_SteamID] = $attempt
$byRpc[$rpc] = $otherAttempt
Assert-True (-not (Invoke-Identity $helper).Success) 'A superseded by-RPC reservation was accepted by the old Steam-ID generation.'
$byRpc[$rpc] = $attempt
$byRpc.Remove($rpc) | Out-Null
Assert-True (-not (Invoke-Identity $helper).Success) 'A missing by-RPC reservation was accepted.'
$byRpc.Add($rpc, $attempt)
$world.Remove($rpc) | Out-Null
Assert-True (-not (Invoke-Identity $active).Success) 'An active session without its owned world gate was accepted.'
$world.Add($rpc, $gate)
foreach ($property in @('Overflowed', 'InboundViolation')) {
    Set-Property $gate $property $true
    Assert-True (-not (Invoke-Identity $active).Success) "An unhealthy owned world gate was accepted: $property"
    Set-Property $gate $property $false
}
$quarantinedGate = $gateType.GetConstructors($instance)[0].Invoke([object[]]@([ForeignIdentitySocket]::new($rawSocket), 1048576))
$gateType.GetMethod('Discard', $instance).Invoke($quarantinedGate, @()) | Out-Null
$world[$rpc] = $quarantinedGate
Assert-True (-not (Invoke-Identity $active).Success) 'An active session with a quarantined owned world gate was accepted.'
$world[$rpc] = $gate
[SessionIdentityEnvironment]::Server = $false
Assert-True (-not (Invoke-Identity $helper).Success) 'A non-server process resolved an authoritative account identity.'
[SessionIdentityEnvironment]::Server = $true
foreach ($backend in @('PlayFab', 'None')) {
    if (-not ([Enum]::GetNames([OnlineBackendType]) -contains $backend)) { continue }
    [ZNet].GetField('m_onlineBackend', $static).SetValue($null, [Enum]::Parse([OnlineBackendType], $backend))
    Assert-True (-not (Invoke-Identity $helper).Success) "A non-Steam backend supplied a Steam identity: $backend"
}
[ZNet].GetField('m_onlineBackend', $static).SetValue($null, [Enum]::Parse([OnlineBackendType], 'Steamworks'))
$peer.m_uid = 0
Assert-True (-not (Invoke-Identity $active).Success) 'A peer without vanilla admission progress was treated as active.'
$peer.m_uid = 42; $peer.m_playerName = ''
Assert-True (-not (Invoke-Identity $active).Success) 'A peer without a selected profile name was treated as active.'
$peer.m_playerName = 'SelectedProfile'
Assert-True ((Invoke-Identity $active).Success -and [SessionIdentityEnvironment]::HostNameReads -eq 0) 'Restored valid state did not recover without trusting wrapper host names.'
Write-Host "Reservation-bound Steam identity smoke tests passed ($script:checks checks)."
