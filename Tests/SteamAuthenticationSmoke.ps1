param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$PluginPath = '',
    [switch]$FixtureOnly,
    [switch]$DependenciesLoaded
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $projectRoot "bin\$Configuration"
if ([string]::IsNullOrWhiteSpace($PluginPath)) { $PluginPath = Join-Path $buildRoot 'ServerManager.dll' }
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
if (-not $DependenciesLoaded) {
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll', 'UnityEngine.dll',
    'assembly_utils.dll', 'SoftReferenceableAssets.dll', 'com.rlabrecque.steamworks.net.dll',
    'Splatform.dll', 'assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $buildRoot $name))) | Out-Null
}

# Replace native/process inputs and outbound I/O only. Factories retain the
# production-created delegate and the real Callback<T>.IsGameServer property.
# Calling Emit therefore enters the actual callback capture and processor IL.
Add-Type -ReferencedAssemblies @((Join-Path $managedRoot 'assembly_valheim.dll'),
    (Join-Path $managedRoot 'assembly_utils.dll'),
    (Join-Path $managedRoot 'com.rlabrecque.steamworks.net.dll')) -TypeDefinition @'
using System;
using System.Reflection;
using System.Runtime.Serialization;
using Steamworks;
public static class SteamAuthenticationEnvironment {
    public static object Server, Peer, PeerRpc, RawSocket, RegisteredPeers;
    public static ulong SteamId;
    public static bool Dedicated, Connected = true, ThrowFactory;
    public static long Now;
    public static int ClientFactories, ServerFactories, Disposals, Completed, Rejected, Disconnected;
    public static string RejectionCode, UnregisteredReason;
    public static object CompletedRpc, RejectedRpc;
    public static Callback<ValidateAuthTicketResponse_t> Registration;
    public static Callback<ValidateAuthTicketResponse_t>.DispatchDelegate Handler;
    public static Action<object> OnComplete;
    public static void Reset() {
        Server = null; Peer = null; PeerRpc = null; RawSocket = null; RegisteredPeers = null; SteamId = 0;
        Dedicated = false; Connected = true; ThrowFactory = false; Now = 0;
        ClientFactories = ServerFactories = Disposals = Completed = Rejected = Disconnected = 0;
        RejectionCode = UnregisteredReason = null; CompletedRpc = RejectedRpc = null;
        Registration = null; Handler = null; OnComplete = null;
    }
    private static Callback<ValidateAuthTicketResponse_t> Create(
        Callback<ValidateAuthTicketResponse_t>.DispatchDelegate handler, bool gameServer) {
        if (gameServer) ++ServerFactories; else ++ClientFactories;
        if (ThrowFactory) throw new InvalidOperationException("Fixture callback registration failure.");
        var callback = (Callback<ValidateAuthTicketResponse_t>)FormatterServices.GetUninitializedObject(
            typeof(Callback<ValidateAuthTicketResponse_t>));
        GC.SuppressFinalize(callback);
        typeof(Callback<ValidateAuthTicketResponse_t>).GetField("m_bGameServer",
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(callback, gameServer);
        Registration = callback; Handler = handler;
        return callback;
    }
    public static Callback<ValidateAuthTicketResponse_t> CreateClient(
        Callback<ValidateAuthTicketResponse_t>.DispatchDelegate handler) { return Create(handler, false); }
    public static Callback<ValidateAuthTicketResponse_t> CreateServer(
        Callback<ValidateAuthTicketResponse_t>.DispatchDelegate handler) { return Create(handler, true); }
    public static void DisposeCallback(object callback) {
        if (!ReferenceEquals(callback, Registration)) throw new InvalidOperationException("Disposed another callback.");
        ++Disposals; Registration = null; Handler = null;
    }
    public static void Emit(EAuthSessionResponse response) {
        if (Handler == null) throw new InvalidOperationException("No callback was registered.");
        Handler(new ValidateAuthTicketResponse_t { m_SteamID = new CSteamID(SteamId),
            m_OwnerSteamID = new CSteamID(SteamId), m_eAuthSessionResponse = response });
    }
    public static long Timestamp() { return Now; }
    public static bool IsNull(object left, object right) { return ReferenceEquals(left, right); }
    public static bool IsServer(object server) { return ReferenceEquals(server, Server); }
    public static bool IsDedicated(object server) { return Dedicated; }
    public static object GetServer() { return Server; }
    public static object GetPeers(object server) { return RegisteredPeers; }
    public static object FindPeer(object server, object rpc) {
        return Peer != null && ReferenceEquals(PeerRpc, rpc) ? Peer : null;
    }
    public static bool IsConnected(object socket) {
        if (!ReferenceEquals(socket, RawSocket)) throw new InvalidOperationException("Unreserved native socket.");
        return Connected;
    }
    public static CSteamID GetPeerID(object socket) {
        if (!ReferenceEquals(socket, RawSocket)) throw new InvalidOperationException("Unreserved native identity.");
        return new CSteamID(SteamId);
    }
    public static string GetEndPointString(object socket) { return "fixture-endpoint"; }
    public static string GetHostName(object socket) {
        if (!ReferenceEquals(socket, RawSocket)) throw new InvalidOperationException("A wrapper supplied character identity.");
        return SteamId.ToString();
    }
    public static void Complete(object server, object rpc) {
        ++Completed; CompletedRpc = rpc;
        if (OnComplete != null) OnComplete(rpc);
    }
    public static void Reject(object rpc, object rejection) {
        ++Rejected; RejectedRpc = rpc;
        RejectionCode = rejection == null ? null : rejection.GetType().GetProperty("Code").GetValue(rejection, null).ToString();
    }
    public static void Disconnect(object rpc) { ++Disconnected; }
    public static void RecordUnregistered(object rpc, object peer, string reason, string detail) { UnregisteredReason = reason; }
    public static void DisposePeer(object peer) { ++Disconnected; }
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
function Get-Property($Object, [string]$Name) {
    return $Object.GetType().GetProperty($Name, $script:instance).GetValue($Object, $null)
}

$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($PluginPath)
$memory = [IO.MemoryStream]::new()
try {
    $definition.Name.Name = 'SteamAuthenticationFixture_' + [Guid]::NewGuid().ToString('N')
    $module = $definition.MainModule
    $runtimeIL = $module.GetType($runtimeName)
    $initializer = $runtimeIL.Methods | Where-Object Name -eq '.cctor'
    $initializer.Body.Instructions.Clear(); $initializer.Body.ExceptionHandlers.Clear(); $initializer.Body.Variables.Clear()
    $initializer.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
    foreach ($field in $runtimeIL.Fields) { $field.IsInitOnly = $false }

    # Auth logic stays intact. Only the transfer/rejection/audit/socket-close
    # boundaries are replaced so tests cannot send packets or touch save files.
    foreach ($boundary in @(
        @{ Name = 'CompleteAuthenticatedPeer'; Probe = 'Complete'; Arguments = @(0, 1) },
        @{ Name = 'SendServerRejection'; Probe = 'Reject'; Arguments = @(0, 1) },
        @{ Name = 'DisconnectServerPeer'; Probe = 'Disconnect'; Arguments = @(0) },
        @{ Name = 'RecordUnregisteredConnectionRejection'; Probe = 'RecordUnregistered'; Arguments = @(0, 1, 2, 3) },
        @{ Name = 'DisposeUnregisteredPeer'; Probe = 'DisposePeer'; Arguments = @(0) }
    )) {
        $method = $runtimeIL.Methods | Where-Object Name -eq $boundary.Name
        Assert-True ($null -ne $method -and $method.ReturnType.FullName -eq 'System.Void') "Missing I/O boundary $($boundary.Name)."
        $method.Body.Instructions.Clear(); $method.Body.ExceptionHandlers.Clear(); $method.Body.Variables.Clear()
        foreach ($argument in $boundary.Arguments) {
            $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create(
                [Mono.Cecil.Cil.OpCodes]::Ldarg, $method.Parameters[$argument]))
        }
        $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call,
            $module.ImportReference([SteamAuthenticationEnvironment].GetMethod($boundary.Probe))))
        $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
    }

    foreach ($method in @($runtimeIL.Methods | Where-Object HasBody) +
        @($module.GetType('ServerManager.ServerPeerResolver').Methods | Where-Object HasBody) +
        @($module.GetType('ServerManager.CharacterPeerIdentityResolver').Methods | Where-Object HasBody)) {
        foreach ($instruction in @($method.Body.Instructions)) {
            if ($instruction.Operand -isnot [Mono.Cecil.MethodReference]) { continue }
            $call = $instruction.Operand
            $replacement = $null
            if ($call.DeclaringType.FullName.StartsWith('Steamworks.Callback`1<')) {
                if ($call.Name -eq 'Create') { $replacement = 'CreateClient' }
                elseif ($call.Name -eq 'CreateGameServer') { $replacement = 'CreateServer' }
                elseif ($call.Name -eq 'Dispose') { $replacement = 'DisposeCallback' }
            } elseif ($call.DeclaringType.FullName -eq 'ZSteamSocket' -and
                $call.Name -in @('IsConnected', 'GetPeerID', 'GetEndPointString')) {
                $replacement = $call.Name
            } elseif ($call.DeclaringType.FullName -eq 'ZNet') {
                if ($call.Name -in @('IsServer', 'IsDedicated', 'GetPeers')) { $replacement = $call.Name }
                elseif ($call.Name -eq 'get_instance') { $replacement = 'GetServer' }
            } elseif ($call.DeclaringType.FullName -eq 'ISocket' -and $call.Name -eq 'GetHostName') {
                $replacement = 'GetHostName'
            } elseif ($call.DeclaringType.FullName -eq 'UnityEngine.Object' -and $call.Name -eq 'op_Equality') {
                $replacement = 'IsNull'
            } elseif ($call.DeclaringType.FullName -eq 'ServerManager.ValheimPrivateAccess' -and $call.Name -eq 'FindPeer') {
                $replacement = 'FindPeer'
            } elseif ($call.DeclaringType.FullName -eq 'System.Diagnostics.Stopwatch' -and $call.Name -eq 'GetTimestamp') {
                $replacement = 'Timestamp'
            }
            if ($null -ne $replacement) {
                $instruction.OpCode = [Mono.Cecil.Cil.OpCodes]::Call
                $instruction.Operand = $module.ImportReference([SteamAuthenticationEnvironment].GetMethod($replacement))
                if ($replacement -in @('FindPeer', 'GetServer', 'GetPeers')) {
                    $method.Body.GetILProcessor().InsertAfter($instruction,
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Castclass, $call.ReturnType))
                }
            }
        }
    }
    $definition.Write($memory)
    $assembly = [Reflection.Assembly]::Load($memory.ToArray())
}
finally { $memory.Dispose(); $definition.Dispose() }

$runtime = $assembly.GetType($runtimeName, $true)
$gateType = $assembly.GetType('ServerManager.BufferedWorldSocket', $true)
$stateType = $assembly.GetType($runtimeName + '+PeerInfoPatchState', $true)
$limitsType = $assembly.GetType('ServerManager.ConnectionProtocolLimits', $true)
$coordinatorType = $assembly.GetType('ServerManager.ConnectionSessionCoordinator', $true)
$assembly.GetType('ServerManager.ServerManagerPlugin', $true).GetProperty('Log', $static).SetValue(
    $null, [BepInEx.Logging.ManualLogSource]::new('SteamAuthenticationSmoke'), $null)
function Set-RuntimeField([string]$Name, $Value) { $script:runtime.GetField($Name, $script:static).SetValue($null, $Value) }
function Get-RuntimeField([string]$Name) { return $script:runtime.GetField($Name, $script:static).GetValue($null) }
function Invoke-Runtime([string]$Name, [object[]]$Arguments = @()) {
    return $script:runtime.GetMethod($Name, $script:static).Invoke($null, $Arguments)
}
function Ensure-Callback {
    $ensure = $script:runtime.GetMethod('EnsureSteamAuthenticationCallback', $script:static)
    # Supporting the old signature lets the saved baseline fail on observed
    # factory behavior rather than merely on a renamed/private method shape.
    $arguments = if ($ensure.GetParameters().Count -eq 0) { @() } else { @($script:server) }
    return $ensure.Invoke($null, [object[]]$arguments)
}
function Reset-Fixture([bool]$Dedicated = $true) {
    if ($script:fixtureInitialized) { Invoke-Runtime 'DisposeSteamAuthenticationCallback' | Out-Null }
    [SteamAuthenticationEnvironment]::Reset()
    [SteamAuthenticationEnvironment]::Dedicated = $Dedicated
    $script:server = New-Uninitialized ([ZNet])
    [SteamAuthenticationEnvironment]::Server = $script:server
    [ZNet].GetField('m_onlineBackend', $script:static).SetValue($null, [Enum]::Parse([OnlineBackendType], 'Steamworks'))
    Set-RuntimeField 'SteamAuthenticationGate' ([object]::new())
    foreach ($name in @('SteamAuthenticationsByRpc', 'SteamAuthenticationsById', 'RetiredSteamIds',
        'QuarantinedIncompleteSteamIds', 'SteamAuthenticationCallbacks', 'WorldBuffers')) {
        $field = $script:runtime.GetField($name, $script:static)
        $field.SetValue($null, [Activator]::CreateInstance($field.FieldType))
    }
    Set-RuntimeField '_initialized' $true
    Set-RuntimeField '_shuttingDown' $false
    Set-RuntimeField '_steamAuthenticationCallback' $null
    Set-RuntimeField '_steamAuthenticationCallbackUnavailable' $false
    Set-RuntimeField '_steamAuthenticationGenerationCapacityExhausted' $false
    Set-RuntimeField '_queuedSteamAuthenticationCallbackCount' 0
    Set-RuntimeField 'RetiredSteamIdQuietTicks' ([long](2 * [Diagnostics.Stopwatch]::Frequency))
    $limits = $script:limitsType.GetConstructors()[0].Invoke([object[]]@(524288, 262144, 262144, 512,
        [TimeSpan]::FromSeconds(120), [TimeSpan]::FromSeconds(120)))
    Set-RuntimeField '_connectionLimits' $limits
    Set-RuntimeField '_coordinator' ($script:coordinatorType.GetConstructors()[0].Invoke([object[]]@($limits, $null, $null)))
    $script:fixtureInitialized = $true
}
function New-Connection([uint64]$SteamId = 76561198000000001) {
    $script:rpc = New-Uninitialized ([ZRpc])
    $script:peer = New-Uninitialized ([ZNetPeer])
    $script:rawSocket = New-Uninitialized ([ZSteamSocket])
    $script:peer.m_rpc = $script:rpc; $script:peer.m_socket = $script:rawSocket
    [ZRpc].GetField('m_socket', $script:instance).SetValue($script:rpc, $script:rawSocket)
    [SteamAuthenticationEnvironment]::Peer = $script:peer
    [SteamAuthenticationEnvironment]::PeerRpc = $script:rpc
    $registeredPeers = [Collections.Generic.List[ZNetPeer]]::new()
    $registeredPeers.Add($script:peer)
    [SteamAuthenticationEnvironment]::RegisteredPeers = $registeredPeers
    [SteamAuthenticationEnvironment]::RawSocket = $script:rawSocket
    [SteamAuthenticationEnvironment]::SteamId = $SteamId
    $script:steamId = [Steamworks.CSteamID]::new($SteamId)
    return Invoke-Runtime 'TryReserveSteamAuthentication' @($script:server, $script:peer, $script:rpc)
}
function Enter-PeerInfo {
    $arguments = [object[]]@($script:server, $script:rpc, $null, $null)
    Assert-True (Invoke-Runtime 'TryEnterSteamPeerInfo' $arguments) 'A reserved connection could not enter vanilla PeerInfo.'
    $script:attempt = $arguments[2]
    $script:gate = $script:gateType.GetConstructors($script:instance)[0].Invoke([object[]]@($script:rawSocket, 1048576))
    (Get-RuntimeField 'WorldBuffers').Add($script:rpc, $script:gate)
    $script:peerInfoState = [Activator]::CreateInstance($script:stateType, $true)
    Set-Property $script:peerInfoState 'IsServerPeerInfo' $true
    Set-Property $script:peerInfoState 'Buffer' $script:gate
    Set-Property $script:peerInfoState 'SteamAuthentication' $script:attempt
}
function Begin-Ticket {
    Invoke-Runtime 'BeforeVanillaSteamTicketVerification' @($script:steamId) | Out-Null
}
function Finish-PeerInfo([bool]$Accepted = $true) {
    Invoke-Runtime 'AfterVanillaSteamTicketVerification' @($script:steamId, $Accepted) | Out-Null
    # Vanilla's successful PeerInfo populates these fields. Its native ticket
    # call is represented by the actual production before/after hooks above.
    $script:peer.m_uid = 42; $script:peer.m_playerName = 'FixtureProfile'
    Invoke-Runtime 'AfterServerPeerInfo' @($script:server, $script:rpc, $script:peerInfoState) | Out-Null
}
function Prepare-Join {
    Assert-True (New-Connection) 'Could not reserve the connection.'
    Enter-PeerInfo
    Begin-Ticket
    Finish-PeerInfo
    Assert-True ((Get-Property $script:attempt 'Phase').ToString() -eq 'VanillaAccepted') 'Synchronous vanilla acceptance did not wait for final Steam authentication.'
    Assert-True ([SteamAuthenticationEnvironment]::Completed -eq 0) 'Character handling started before the final Steam callback.'
}
function Assert-Activated([string]$Context) {
    Assert-True ((Get-Property $script:attempt 'Phase').ToString() -eq 'Active' -and
        (Get-Property $script:attempt 'EnqueuedCallbackCount') -eq 1 -and (Get-Property $script:attempt 'ProcessedCallbackCount') -eq 1 -and
        [SteamAuthenticationEnvironment]::Completed -eq 1 -and
        [object]::ReferenceEquals([SteamAuthenticationEnvironment]::CompletedRpc, $script:rpc) -and
        [SteamAuthenticationEnvironment]::Rejected -eq 0) $Context
}
function Assert-Denied([string]$Code, [string]$Context) {
    Assert-True ((Get-Property $script:attempt 'Phase').ToString() -eq 'Rejected' -and
        [SteamAuthenticationEnvironment]::Completed -eq 0 -and
        [SteamAuthenticationEnvironment]::Rejected -eq 1 -and
        [SteamAuthenticationEnvironment]::RejectionCode -eq $Code) $Context
}

# Character-session integration tests reuse the native-input substitutions and
# actual auth transition helpers without executing these standalone scenarios.
if ($FixtureOnly) { return }

# This is the regression's first behavior check: 0.24.29 registers Create on
# the client pipe even when the server uses the game-server auth-ticket API.
Reset-Fixture $true
Assert-True (Ensure-Callback) 'Dedicated callback registration failed.'
Assert-True ([SteamAuthenticationEnvironment]::ServerFactories -eq 1 -and
    [SteamAuthenticationEnvironment]::ClientFactories -eq 0 -and
    [SteamAuthenticationEnvironment]::Registration.IsGameServer) 'Dedicated authentication registered the client callback pipe instead of CreateGameServer.'
$registered = Get-RuntimeField '_steamAuthenticationCallback'
Assert-True (Ensure-Callback) 'Same-channel callback reuse failed.'
Assert-True ([object]::ReferenceEquals($registered, (Get-RuntimeField '_steamAuthenticationCallback')) -and
    [SteamAuthenticationEnvironment]::ServerFactories -eq 1) 'Same-channel reuse registered an additional callback.'
[SteamAuthenticationEnvironment]::Dedicated = $false
Assert-True (-not (Ensure-Callback)) 'A game-server callback was reused for a listen server.'
Assert-True ([object]::ReferenceEquals($registered, (Get-RuntimeField '_steamAuthenticationCallback')) -and
    [SteamAuthenticationEnvironment]::ClientFactories -eq 0) 'Channel mismatch replaced a live registration.'
Invoke-Runtime 'DisposeSteamAuthenticationCallback' | Out-Null
Assert-True ([SteamAuthenticationEnvironment]::Disposals -eq 1 -and
    $null -eq (Get-RuntimeField '_steamAuthenticationCallback') -and
    -not (Get-RuntimeField '_steamAuthenticationCallbackUnavailable')) 'Callback disposal did not clear registration/failure state.'
Assert-True (Ensure-Callback) 'Listen callback registration did not recover after disposal.'
Assert-True ([SteamAuthenticationEnvironment]::ClientFactories -eq 1 -and
    -not [SteamAuthenticationEnvironment]::Registration.IsGameServer) 'Listen authentication did not register the client callback pipe.'
Invoke-Runtime 'DisposeSteamAuthenticationCallback' | Out-Null
Invoke-Runtime 'DisposeSteamAuthenticationCallback' | Out-Null
Assert-True ([SteamAuthenticationEnvironment]::Disposals -eq 2) 'Repeated callback disposal was not idempotent.'

Reset-Fixture
[SteamAuthenticationEnvironment]::ThrowFactory = $true
Assert-True (-not (Ensure-Callback)) 'Native callback registration failure was accepted.'
[SteamAuthenticationEnvironment]::ThrowFactory = $false
Assert-True (-not (Ensure-Callback) -and [SteamAuthenticationEnvironment]::ServerFactories -eq 1) 'Unavailable callback registration retried without lifecycle reset.'
Invoke-Runtime 'DisposeSteamAuthenticationCallback' | Out-Null
Assert-True (Ensure-Callback) 'Callback registration failure did not recover after lifecycle reset.'

foreach ($dedicated in @($true, $false)) {
    Reset-Fixture $dedicated
    Prepare-Join
    [SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
    Assert-True ((Get-Property $attempt 'Phase').ToString() -eq 'VanillaAccepted' -and
        [SteamAuthenticationEnvironment]::Completed -eq 0) 'Callback capture itself released a character before Tick processing.'
    Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
    Assert-Activated "Valid callback did not activate the reserved session (dedicated=$dedicated)."
    Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
    Assert-True ([SteamAuthenticationEnvironment]::Completed -eq 1) 'A later Tick repeated character admission.'
}

Reset-Fixture
Assert-True (New-Connection) 'Early-callback connection reservation failed.'
Enter-PeerInfo
Begin-Ticket
[SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
Assert-True ((Get-Property $attempt 'Phase').ToString() -eq 'PeerInfoPending' -and
    [SteamAuthenticationEnvironment]::Completed -eq 0) 'A callback arriving during BeginAuth bypassed vanilla completion.'
Finish-PeerInfo
Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
Assert-Activated 'A valid callback captured before the PeerInfo postfix was lost.'

Reset-Fixture
Prepare-Join
Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
Assert-True ((Get-Property $attempt 'Phase').ToString() -eq 'VanillaAccepted' -and
    [SteamAuthenticationEnvironment]::Completed -eq 0) 'Missing callback was treated as authentication success.'
[SteamAuthenticationEnvironment]::Now = (Get-Property $attempt 'DeadlineTimestamp') + 1
Invoke-Runtime 'ExpireSteamAuthentications' | Out-Null
Assert-Denied 'HandshakeTimedOut' 'Missing callback did not time out without admitting the character.'

Reset-Fixture
Prepare-Join
[SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseAuthTicketInvalid)
Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
Assert-Denied 'PeerInfoAuthenticationIncomplete' 'A negative final callback admitted a character.'

Reset-Fixture
Assert-True (New-Connection) 'Stale-callback connection reservation failed.'
[SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
Enter-PeerInfo
Begin-Ticket
Finish-PeerInfo
Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
Assert-Denied 'PeerInfoAuthenticationIncomplete' 'An OK captured before BeginAuth authenticated a later attempt.'

Reset-Fixture
Prepare-Join
[SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
[SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
Assert-Denied 'DuplicateMessage' 'Duplicate final callbacks admitted a character.'

Reset-Fixture
Prepare-Join
[SteamAuthenticationEnvironment]::Now = (Get-Property $attempt 'DeadlineTimestamp') + 1
[SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
Assert-Denied 'HandshakeTimedOut' 'A late final callback bypassed the deadline.'

Reset-Fixture
Prepare-Join
[SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
Invoke-Runtime 'RemoveSteamAuthentication' @($rpc) | Out-Null
Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
Assert-True ([SteamAuthenticationEnvironment]::Completed -eq 0 -and
    (Get-RuntimeField '_queuedSteamAuthenticationCallbackCount') -eq 0) 'A queued callback survived removal of its connection generation.'
Assert-True (-not (New-Connection) -and
    [SteamAuthenticationEnvironment]::UnregisteredReason -eq 'steam_identity_quarantined') 'An incomplete old callback generation authenticated a reconnect.'

Reset-Fixture
Prepare-Join
[SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
Invoke-Runtime 'DisposeSteamAuthenticationCallback' | Out-Null
Assert-True ((Get-RuntimeField 'SteamAuthenticationsByRpc').Count -eq 0 -and
    (Get-RuntimeField 'SteamAuthenticationsById').Count -eq 0 -and
    (Get-RuntimeField '_queuedSteamAuthenticationCallbackCount') -eq 0 -and
    [SteamAuthenticationEnvironment]::Completed -eq 0) 'Callback disposal left a pending authentication or queued completion.'

Write-Host "Steam callback registration and authentication lifecycle smoke tests passed ($script:checks checks)."
