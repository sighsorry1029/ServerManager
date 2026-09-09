param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw $Message }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
Assert-True (Test-Path -LiteralPath $pluginPath) 'Build ServerManager before running this smoke test.'
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll', 'UnityEngine.dll',
    'assembly_utils.dll', 'SoftReferenceableAssets.dll', 'com.rlabrecque.steamworks.net.dll',
    'Splatform.dll', 'assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (Split-Path -Parent $pluginPath) $name))) | Out-Null
}
$probeReferences = @((Join-Path $managedRoot 'com.rlabrecque.steamworks.net.dll'))
if ($PSVersionTable.PSEdition -eq 'Core') {
    $probeReferences += @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
}
Add-Type -ReferencedAssemblies $probeReferences -TypeDefinition @'
using System;
using Steamworks;
public static class CharacterCreationEnvironment {
    public static object Network, Game, Profile, Peer, LastServer, LastRpc;
    public static bool Server, Dedicated, Admin, ResolveAllowed;
    public static bool ThrowSteam, ThrowProfile, ThrowResolve, ThrowAdmin, ThrowFatal;
    public static int Backend, AdminReads, ResolveReads;
    public static ulong SteamId;
    public static string ProfileName, AdminHostId;
    public static object GetNetwork() { return Network; }
    public static object GetGame() { return Game; }
    public static object GetProfile(object game) {
        if (ThrowProfile) throw new InvalidOperationException("fixture profile unavailable");
        return Profile;
    }
    public static string GetProfileName(object profile) { return ProfileName; }
    public static bool IsNull(object left, object right) { return ReferenceEquals(left, right); }
    public static bool IsServer(object server) { return Server; }
    public static bool IsDedicated(object server) { return Dedicated; }
    public static CSteamID GetSteamID() {
        if (ThrowFatal) throw new OutOfMemoryException("fixture fatal failure");
        if (ThrowSteam) throw new InvalidOperationException("fixture Steam unavailable");
        return new CSteamID(SteamId);
    }
    public static bool Resolve(object server, object rpc) {
        ResolveReads++; LastServer = server; LastRpc = rpc;
        if (ThrowResolve) throw new InvalidOperationException("fixture peer unavailable");
        return ResolveAllowed;
    }
    public static object GetPeer() { return Peer; }
    public static bool IsAdmin(object server, string hostId) {
        AdminReads++; AdminHostId = hostId;
        if (ThrowAdmin) throw new InvalidOperationException("fixture admin list unavailable");
        return Admin;
    }
    public static bool AlwaysAdmin(object server, object peer) { return true; }
}
'@

$allStatic = [Reflection.BindingFlags]'Static,Public,NonPublic'
$allInstance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$runtimeName = 'ServerManager.ServerManagerRuntime'
function Clear-CreationBody($Method) {
    $Method.Body.Instructions.Clear()
    $Method.Body.ExceptionHandlers.Clear()
    $Method.Body.Variables.Clear()
}
function Add-CreationInstruction($Method, $Opcode, $Operand = $null) {
    $instruction = if ($null -eq $Operand) { [Mono.Cecil.Cil.Instruction]::Create($Opcode) }
        else { [Mono.Cecil.Cil.Instruction]::Create($Opcode, $Operand) }
    $Method.Body.Instructions.Add($instruction)
}
function New-CreationFixture([string]$Mutation = '') {
    $definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
    $stream = [IO.MemoryStream]::new()
    try {
        $definition.Name.Name = 'CharacterCreationFixture_' + [Guid]::NewGuid().ToString('N')
        $module = $definition.MainModule
        $runtime = $module.Types | Where-Object FullName -eq $runtimeName
        $guard = $runtime.Methods | Where-Object Name -eq 'IsCharacterCreationAdmin'
        Assert-True ($null -ne $guard -and $guard.HasBody) 'The actual creation resolver is missing.'
        $calls = @($guard.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
        foreach ($binding in @('ServerManager.CharacterSteamIdentity::TryParseCanonicalAccountId',
            'Steamworks.SteamUser::GetSteamID', 'ServerManager.ValheimPrivateAccess::GetGamePlayerProfile',
            'ServerManager.CharacterNamePolicy::NormalizeAndValidate',
            'ServerManager.ServerManagerRuntime::TryResolveActiveDetectionPeer',
            'ServerManager.ServerManagerRuntime::IsCurrentServerAdmin',
            'ServerManager.IntegrityCanonical::IsFatal')) {
            Assert-True (@($calls | Where-Object {
                ($_.Operand.DeclaringType.FullName + '::' + $_.Operand.Name) -eq $binding
            }).Count -gt 0) "The production resolver lost required binding $binding."
        }
        Assert-True (@($calls | Where-Object {
            $_.Operand.Name -in @('TryGetServerSession', 'IsCharacterPolicyAdmin')
        }).Count -eq 0) 'Creation depends on an existing character session or broader policy exemption.'
        Assert-True (@($guard.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq '_serverCharacterService'
        }).Count -eq 0) 'The initial creation resolver depends on the character service.'

        # Retain the real resolver, canonical parser, dictionary lookup, phase,
        # identity/name comparisons and exception filter. Only environment
        # boundaries are inert. Clear the runtime initializer so no Steam,
        # Unity, socket, storage service or live plugin runtime is started.
        $initializer = $runtime.Methods | Where-Object Name -eq '.cctor'
        Clear-CreationBody $initializer
        Add-CreationInstruction $initializer ([Mono.Cecil.Cil.OpCodes]::Ret)
        foreach ($field in $runtime.Fields) { $field.IsInitOnly = $false }
        foreach ($instruction in @($guard.Body.Instructions)) {
            if ($instruction.Operand -is [Mono.Cecil.MethodReference]) {
                $call = $instruction.Operand
                $replacement = $null
                $castType = $null
                if ($call.DeclaringType.FullName -eq 'ZNet') {
                    $replacement = switch ($call.Name) {
                        'get_instance' { $castType = [ZNet]; 'GetNetwork' }
                        'IsServer' { 'IsServer' }
                        'IsDedicated' { 'IsDedicated' }
                    }
                } elseif ($call.DeclaringType.FullName -eq 'Game' -and $call.Name -eq 'get_instance') {
                    $replacement = 'GetGame'; $castType = [Game]
                } elseif ($call.DeclaringType.FullName -eq 'ServerManager.ValheimPrivateAccess' -and
                    $call.Name -eq 'GetGamePlayerProfile') {
                    $replacement = 'GetProfile'; $castType = [PlayerProfile]
                } elseif ($call.DeclaringType.FullName -eq 'PlayerProfile' -and $call.Name -eq 'GetName') {
                    $replacement = 'GetProfileName'
                } elseif ($call.DeclaringType.FullName -eq 'Steamworks.SteamUser' -and $call.Name -eq 'GetSteamID') {
                    $replacement = 'GetSteamID'
                } elseif ($call.DeclaringType.FullName -eq 'UnityEngine.Object' -and $call.Name -eq 'op_Equality') {
                    $replacement = 'IsNull'
                }
                if ($replacement) {
                    $instruction.OpCode = [Mono.Cecil.Cil.OpCodes]::Call
                    $instruction.Operand = $module.ImportReference([CharacterCreationEnvironment].GetMethod($replacement))
                    if ($null -ne $castType) {
                        $guard.Body.GetILProcessor().InsertAfter($instruction,
                            [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Castclass,
                                $module.ImportReference($castType)))
                    }
                }
            } elseif ($instruction.Operand -is [Mono.Cecil.FieldReference] -and
                $instruction.Operand.Name -eq 'm_onlineBackend') {
                $instruction.Operand = $module.ImportReference([CharacterCreationEnvironment].GetField('Backend'))
            }
        }

        # The socket/auth verifier is a boundary, not a second implementation of
        # the quota decision. Assert its original security bindings, then supply
        # controlled success/failure and a real ServerPeerIdentity to the guard.
        $resolve = $runtime.Methods | Where-Object Name -eq 'TryResolveActiveDetectionPeer'
        foreach ($name in @('TryResolve', 'IsCurrentSteamAuthenticationLocked')) {
            Assert-True (@($resolve.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $name
            }).Count -gt 0) "The live peer verifier lost $name."
        }
        $connection = $runtime.Methods | Where-Object Name -eq 'TryGetSteamConnection'
        $identityResolver = ($module.Types | Where-Object FullName -eq 'ServerManager.ServerPeerResolver').Methods |
            Where-Object Name -eq 'TryResolve'
        Assert-True (@($identityResolver.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'TryGetSteamConnection'
        }).Count -eq 1) 'The live peer verifier no longer uses the reservation-derived identity boundary.'
        foreach ($name in @('TryResolvePeer', 'IsCurrentSteamAuthenticationLocked', 'IsConnected', 'GetPeerID', 'IsValid')) {
            Assert-True (@($connection.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $name
            }).Count -gt 0) "The connection-bound identity verifier lost $name."
        }
        Clear-CreationBody $resolve
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Ldarg_2)
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Call) ($module.ImportReference([CharacterCreationEnvironment].GetMethod('GetPeer')))
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Castclass) ($module.Types | Where-Object FullName -eq 'ServerManager.ServerPeerIdentity')
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Stind_Ref)
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Ldarg_3)
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Ldnull)
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Stind_Ref)
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Ldarg_0)
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Ldarg_1)
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Call) ($module.ImportReference([CharacterCreationEnvironment].GetMethod('Resolve')))
        Add-CreationInstruction $resolve ([Mono.Cecil.Cil.OpCodes]::Ret)

        $admin = $runtime.Methods | Where-Object Name -eq 'IsCurrentServerAdmin'
        Assert-True (@($admin.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq 'ZNet' -and $_.Operand.Name -eq 'IsAdmin'
        }).Count -eq 1) 'Remote privilege no longer uses the live server admin list.'
        # Do not execute this helper's plugin logging on injected failures. The
        # unchanged outer resolver exception filter must still deny the lookup.
        Clear-CreationBody $admin
        Add-CreationInstruction $admin ([Mono.Cecil.Cil.OpCodes]::Ldarg_0)
        Add-CreationInstruction $admin ([Mono.Cecil.Cil.OpCodes]::Ldarg_1)
        $peerType = $module.Types | Where-Object FullName -eq 'ServerManager.ServerPeerIdentity'
        Add-CreationInstruction $admin ([Mono.Cecil.Cil.OpCodes]::Callvirt) ($peerType.Methods | Where-Object Name -eq 'get_HostId')
        Add-CreationInstruction $admin ([Mono.Cecil.Cil.OpCodes]::Call) ($module.ImportReference([CharacterCreationEnvironment].GetMethod('IsAdmin')))
        Add-CreationInstruction $admin ([Mono.Cecil.Cil.OpCodes]::Ret)

        if ($Mutation -eq 'IgnoreHostFailure') {
            $read = @($guard.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq '_localHostStartupFailed'
            })
            Assert-True ($read.Count -eq 1) 'Host mutation did not locate the real failure guard.'
            $read[0].OpCode = [Mono.Cecil.Cil.OpCodes]::Ldc_I4_0
            $read[0].Operand = $null
        } elseif ($Mutation -eq 'IgnoreRemoteAdmin') {
            $read = @($guard.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'IsCurrentServerAdmin'
            })
            Assert-True ($read.Count -eq 1) 'Remote mutation did not locate the real admin guard.'
            $read[0].Operand = $module.ImportReference([CharacterCreationEnvironment].GetMethod('AlwaysAdmin'))
        }
        $definition.Write($stream)
        return [Reflection.Assembly]::Load($stream.ToArray())
    } finally { $stream.Dispose(); $definition.Dispose() }
}
function Set-CreationField($Target, [string]$Name, $Value) {
    if ($Target -is [Type]) { $Target.GetField($Name, $allStatic).SetValue($null, $Value) }
    else { $Target.GetType().GetField($Name, $allInstance).SetValue($Target, $Value) }
}
function New-CreationIdentity($Assembly, [string]$Account = 'steamworks:76561198000000000', [string]$Name = 'QuotaTest') {
    return [Activator]::CreateInstance($Assembly.GetType('ServerManager.CharacterIdentity'), [object[]]@($Account, $Name))
}
function New-CreationState($Assembly) {
    $runtime = $Assembly.GetType($runtimeName, $true)
    $authField = $runtime.GetField('SteamAuthenticationsById', $allStatic)
    $auth = [Activator]::CreateInstance($authField.FieldType)
    Set-CreationField $runtime 'SteamAuthenticationsById' $auth
    Set-CreationField $runtime 'SteamAuthenticationGate' ([object]::new())
    return [pscustomobject]@{
        Assembly = $Assembly; Runtime = $runtime; Auth = $auth
        Resolver = $runtime.GetMethod('IsCharacterCreationAdmin', $allStatic)
        Host = New-CreationIdentity $Assembly
        Remote = New-CreationIdentity $Assembly 'steamworks:76561198000000001'
    }
}
$network = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([ZNet])
$otherNetwork = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([ZNet])
$game = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([Game])
$profile = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([PlayerProfile])
$rpc = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([ZRpc])
function Reset-CreationState($State) {
    [CharacterCreationEnvironment]::Network = $network
    [CharacterCreationEnvironment]::Game = $game
    [CharacterCreationEnvironment]::Profile = $profile
    [CharacterCreationEnvironment]::ProfileName = 'QuotaTest'
    [CharacterCreationEnvironment]::Server = $true
    [CharacterCreationEnvironment]::Dedicated = $false
    [CharacterCreationEnvironment]::Backend = [int][OnlineBackendType]::Steamworks
    [CharacterCreationEnvironment]::SteamId = 76561198000000000
    [CharacterCreationEnvironment]::Admin = $false
    [CharacterCreationEnvironment]::ResolveAllowed = $true
    [CharacterCreationEnvironment]::AdminReads = 0
    [CharacterCreationEnvironment]::ResolveReads = 0
    [CharacterCreationEnvironment]::ThrowSteam = $false
    [CharacterCreationEnvironment]::ThrowProfile = $false
    [CharacterCreationEnvironment]::ThrowResolve = $false
    [CharacterCreationEnvironment]::ThrowAdmin = $false
    [CharacterCreationEnvironment]::ThrowFatal = $false
    [CharacterCreationEnvironment]::Peer = $null
    $State.Auth.Clear()
    Set-CreationField $State.Runtime '_initialized' $true
    Set-CreationField $State.Runtime '_shuttingDown' $false
    Set-CreationField $State.Runtime '_localHostRequested' $true
    Set-CreationField $State.Runtime '_localHostStartupFailed' $false
    Set-CreationField $State.Runtime '_localHostNetwork' $network
}
function Set-CreationRemote($State, [string]$Phase = 'Active', [string]$HostId = '76561198000000001', [string]$Name = 'QuotaTest') {
    $attemptType = $State.Assembly.GetType($runtimeName + '+SteamAuthenticationAttempt', $true)
    $attempt = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($attemptType)
    Set-CreationField $attempt '<Rpc>k__BackingField' $rpc
    $phaseProperty = $attemptType.GetProperty('Phase', $allInstance)
    $phaseProperty.SetValue($attempt, [Enum]::Parse($phaseProperty.PropertyType, $Phase), $null)
    $State.Auth.Clear()
    $State.Auth.Add([uint64]76561198000000001, $attempt)
    $peer = [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
        $State.Assembly.GetType('ServerManager.ServerPeerIdentity', $true))
    Set-CreationField $peer '<HostId>k__BackingField' $HostId
    Set-CreationField $peer '<PlayerName>k__BackingField' $Name
    [CharacterCreationEnvironment]::Peer = $peer
}
function Invoke-Creation($State, $Identity) { return $State.Resolver.Invoke($null, [object[]]@($Identity)) }

$state = New-CreationState (New-CreationFixture)
Reset-CreationState $state
$hostCases = 0
foreach ($initialized in @($false, $true)) { foreach ($shutdown in @($false, $true)) {
foreach ($server in @($false, $true)) { foreach ($dedicated in @($false, $true)) {
foreach ($backend in [Enum]::GetValues([OnlineBackendType])) { foreach ($requested in @($false, $true)) {
foreach ($failed in @($false, $true)) { foreach ($sameNetwork in @($false, $true)) {
foreach ($admin in @($false, $true)) {
    Set-CreationField $state.Runtime '_initialized' $initialized
    Set-CreationField $state.Runtime '_shuttingDown' $shutdown
    Set-CreationField $state.Runtime '_localHostRequested' $requested
    Set-CreationField $state.Runtime '_localHostStartupFailed' $failed
    Set-CreationField $state.Runtime '_localHostNetwork' $(if ($sameNetwork) { $network } else { $otherNetwork })
    [CharacterCreationEnvironment]::Server = $server
    [CharacterCreationEnvironment]::Dedicated = $dedicated
    [CharacterCreationEnvironment]::Backend = [int]$backend
    [CharacterCreationEnvironment]::Admin = $admin
    [CharacterCreationEnvironment]::AdminReads = 0
    $expected = $initialized -and -not $shutdown -and $server -and -not $dedicated -and
        $backend -eq [OnlineBackendType]::Steamworks -and $requested -and -not $failed -and $sameNetwork
    Assert-True ((Invoke-Creation $state $state.Host) -eq $expected) (
        "Host mismatch: initialized=$initialized shutdown=$shutdown server=$server dedicated=$dedicated " +
        "backend=$backend requested=$requested failed=$failed sameNetwork=$sameNetwork admin=$admin")
    Assert-True ([CharacterCreationEnvironment]::AdminReads -eq 0) 'Host quota logic consulted an admin-list fallback.'
    $hostCases++
}}}}}}}}}

Reset-CreationState $state
Assert-True (Invoke-Creation $state $state.Host) 'The unlisted actual host was not exempt before character-session activation.'
Assert-True ($null -eq $state.Runtime.GetField('_serverCharacterService', $allStatic).GetValue($null)) `
    'The fixture unexpectedly constructed a character service.'
Assert-True (-not (Invoke-Creation $state $null)) 'Null identity received a creation exemption.'
foreach ($account in @('steamworks:0', 'steamworks:076561198000000000', 'Steamworks:76561198000000000', 'steamworks:76561198000000000 ')) {
    Assert-True (-not (Invoke-Creation $state (New-CreationIdentity $state.Assembly $account))) "Noncanonical account $account received an exemption."
}
foreach ($missing in @('Network', 'Game', 'Profile')) {
    Reset-CreationState $state
    [CharacterCreationEnvironment].GetField($missing).SetValue($null, $null)
    Assert-True (-not (Invoke-Creation $state $state.Host)) "Missing $missing granted host exemption."
}
foreach ($name in @('DifferentCharacter', 'quotatest', '', '../invalid')) {
    Reset-CreationState $state
    [CharacterCreationEnvironment]::ProfileName = $name
    Assert-True (-not (Invoke-Creation $state $state.Host)) "Mismatched or invalid selected name '$name' granted host exemption."
}
Reset-CreationState $state
[CharacterCreationEnvironment]::SteamId = 76561198000000002
Assert-True (-not (Invoke-Creation $state $state.Host)) 'An unrelated local Steam account received the host exemption.'
foreach ($failure in @('ThrowSteam', 'ThrowProfile')) {
    Reset-CreationState $state
    [CharacterCreationEnvironment].GetField($failure).SetValue($null, $true)
    Assert-True (-not (Invoke-Creation $state $state.Host)) "$failure did not fail closed."
}

$remoteCases = 0
foreach ($phase in @('Missing', 'Reserved', 'PeerInfoPending', 'VanillaAccepted', 'Active', 'Rejected')) {
foreach ($resolved in @($false, $true)) { foreach ($sameHost in @($false, $true)) {
foreach ($sameName in @($false, $true)) { foreach ($admin in @($false, $true)) {
    Reset-CreationState $state
    if ($phase -ne 'Missing') {
        Set-CreationRemote $state $phase $(if ($sameHost) { '76561198000000001' } else { '76561198000000002' }) `
            $(if ($sameName) { 'QuotaTest' } else { 'AnotherCharacter' })
    }
    [CharacterCreationEnvironment]::ResolveAllowed = $resolved
    [CharacterCreationEnvironment]::Admin = $admin
    $reachesAdmin = $phase -eq 'Active' -and $resolved -and $sameHost -and $sameName
    Assert-True ((Invoke-Creation $state $state.Remote) -eq ($reachesAdmin -and $admin)) `
        "Remote mismatch: phase=$phase resolved=$resolved sameHost=$sameHost sameName=$sameName admin=$admin"
    Assert-True ([CharacterCreationEnvironment]::ResolveReads -eq [int]($phase -eq 'Active')) `
        'Missing/inactive authentication bypassed the actual dictionary/phase gate.'
    Assert-True ([CharacterCreationEnvironment]::AdminReads -eq [int]$reachesAdmin) `
        'Remote admin status was consulted before live identity matched.'
    if ($reachesAdmin) {
        Assert-True ([CharacterCreationEnvironment]::AdminHostId -ceq '76561198000000001' -and
            [object]::ReferenceEquals([CharacterCreationEnvironment]::LastServer, $network) -and
            [object]::ReferenceEquals([CharacterCreationEnvironment]::LastRpc, $rpc)) `
            'Remote lookup did not use the connection-bound server, RPC and Steam identity.'
    }
    $remoteCases++
}}}}}

Reset-CreationState $state
Set-CreationRemote $state
[CharacterCreationEnvironment]::Dedicated = $true
[CharacterCreationEnvironment]::Admin = $true
Assert-True (Invoke-Creation $state $state.Remote) 'A dedicated-server authenticated admin could not create before character-session activation.'
[CharacterCreationEnvironment]::Admin = $false
Assert-True (-not (Invoke-Creation $state $state.Remote)) 'Revoked remote admin status remained cached.'
[CharacterCreationEnvironment]::Admin = $true
Assert-True (Invoke-Creation $state $state.Remote) 'A refreshed remote admin grant was not observed.'
foreach ($failure in @('ThrowResolve', 'ThrowAdmin')) {
    Reset-CreationState $state
    Set-CreationRemote $state
    [CharacterCreationEnvironment]::Admin = $true
    [CharacterCreationEnvironment].GetField($failure).SetValue($null, $true)
    Assert-True (-not (Invoke-Creation $state $state.Remote)) "$failure granted a remote exemption."
}
Reset-CreationState $state
Set-CreationRemote $state 'Active' '76561198000000001' '../invalid'
[CharacterCreationEnvironment]::Admin = $true
Assert-True (-not (Invoke-Creation $state $state.Remote)) 'An invalid peer name granted a remote exemption.'
Reset-CreationState $state
[CharacterCreationEnvironment]::ThrowFatal = $true
$fatalPropagated = $false
try { Invoke-Creation $state $state.Host | Out-Null }
catch {
    $exception = $_.Exception
    while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
    $fatalPropagated = $exception -is [OutOfMemoryException]
}
Assert-True $fatalPropagated 'The creation resolver swallowed a fatal process exception.'

# Mutation controls show that these tests execute the production branch guards,
# not a separately rewritten decision with the same expected answers.
$brokenHost = New-CreationState (New-CreationFixture 'IgnoreHostFailure')
Reset-CreationState $state
Set-CreationField $state.Runtime '_localHostStartupFailed' $true
Assert-True (-not (Invoke-Creation $state $state.Host)) 'Production accepted failed host startup.'
Reset-CreationState $brokenHost
Set-CreationField $brokenHost.Runtime '_localHostStartupFailed' $true
Assert-True (Invoke-Creation $brokenHost $brokenHost.Host) 'Removing the host failure guard did not alter the result.'
$brokenRemote = New-CreationState (New-CreationFixture 'IgnoreRemoteAdmin')
Reset-CreationState $state
Set-CreationRemote $state
Assert-True (-not (Invoke-Creation $state $state.Remote)) 'Production accepted an unlisted remote peer.'
Reset-CreationState $brokenRemote
Set-CreationRemote $brokenRemote
Assert-True (Invoke-Creation $brokenRemote $brokenRemote.Remote) 'Removing the remote admin guard did not alter the result.'

Write-Host "Character creation administrator smoke passed: $script:assertions assertions, $hostCases host cases, $remoteCases remote cases, two guard mutations."
