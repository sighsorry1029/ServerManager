param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$PluginPath = ''
)
$ErrorActionPreference = 'Stop'
$requestedPluginPath = $PluginPath

# Reuse the existing native profile/file builders and headless dependencies,
# then execute the compiled auth state machine from the callback fixture.
. (Join-Path $PSScriptRoot 'BackupOnlySmoke.ps1') -Configuration $Configuration -GamePath $GamePath -FixtureOnly
. (Join-Path $PSScriptRoot 'SteamAuthenticationSmoke.ps1') -Configuration $Configuration -GamePath $GamePath `
    -PluginPath $requestedPluginPath -FixtureOnly -DependenciesLoaded
$plugin = $assembly

Add-Type -ReferencedAssemblies @((Join-Path $managedRoot 'assembly_valheim.dll'),
    (Join-Path $managedRoot 'assembly_utils.dll')) -TypeDefinition @'
using System;
public sealed class CharacterIdentityForeignSocket : ISocket {
    public readonly ISocket Inner;
    public static int HostNameReads;
    public CharacterIdentityForeignSocket(ISocket inner) { Inner = inner; }
    public bool IsConnected() { return true; }
    public void Send(ZPackage package) { throw new InvalidOperationException("Unexpected network send."); }
    public ZPackage Recv() { return null; }
    public int GetSendQueueSize() { return 0; }
    public int GetCurrentSendRate() { return 0; }
    public bool IsHost() { return false; }
    public void Dispose() { }
    public bool GotNewData() { return false; }
    public void Close() { }
    public string GetEndPointString() { return "spoofed-wrapper-endpoint"; }
    public void GetAndResetStats(out int sent, out int received) { sent = received = 0; }
    public void GetConnectionQuality(out float local, out float remote, out int ping, out float sent, out float received)
    { local = remote = sent = received = 0; ping = 0; }
    public ISocket Accept() { return null; }
    public int GetHostPort() { return 0; }
    public bool Flush() { return true; }
    public string GetHostName() { ++HostNameReads; return "76561198000000999"; }
    public void VersionMatch() { }
}
'@

$sessionTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('sm-auth-character-' + [Guid]::NewGuid().ToString('N'))
$characterFixtures = [Collections.Generic.List[object]]::new()
function New-CharacterFixture([string]$Name) {
    $options = New-Instance 'CharacterStorageOptions'
    $layout = New-Instance 'CharacterStorageLayout' @((Join-Path $script:sessionTestRoot $Name))
    $keys = New-Instance 'CharacterStorageKeyProvider' @($layout)
    $codec = New-Instance 'CharacterEnvelopeCodec' @($options)
    $profiles = New-Instance 'ValheimPlayerProfileCodec' @($options)
    $mode = [Enum]::Parse($script:plugin.GetType('ServerManager.CharacterSemanticPolicyMode'), 'Observe')
    $policy = New-Instance 'CharacterSemanticPolicy' @($mode, '', [single]800, [single]800,
        [single]500, [single]100, [single]1000)
    $validator = New-Instance 'CharacterSemanticRevisionValidator' @($profiles,
        (New-Instance 'CharacterSemanticEvaluator' @($policy)))
    $repository = New-Instance 'CharacterRepository' @($layout, $options, $profiles, $validator, $null)
    $identityResolver = New-Instance 'CharacterPeerIdentityResolver'
    $service = New-Instance 'CharacterSnapshotService' @($options,
        $identityResolver, $keys, $codec, $profiles, $repository, $validator)
    $fixture = [pscustomobject]@{ Layout = $layout; Keys = $keys; Codec = $codec;
        Repository = $repository; Service = $service; IdentityResolver = $identityResolver }
    $script:characterFixtures.Add($fixture)
    return $fixture
}
function Write-NativeCharacter($Fixture, [byte[]]$Payload) {
    $identity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000001', 'FixtureProfile')
    $path = $Fixture.Layout.GetProfilePath($Fixture.Keys.DeriveStorageKey($identity))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    $hash = [Security.Cryptography.SHA512]::Create()
    try { [byte[]]$digest = $hash.ComputeHash($Payload) } finally { $hash.Dispose() }
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([int]$Payload.Length); $writer.Write($Payload)
        $writer.Write([int]$digest.Length); $writer.Write($digest); $writer.Flush()
        [IO.File]::WriteAllBytes($path, $stream.ToArray())
    } finally { $writer.Dispose(); $stream.Dispose() }
    return $path
}
function Select-CharacterSocket([string]$Kind) {
    $socket = switch ($Kind) {
        'raw' { $script:rawSocket }
        'owned' { $script:gate }
        'foreign' { [CharacterIdentityForeignSocket]::new($script:rawSocket) }
        'nested' { [CharacterIdentityForeignSocket]::new([CharacterIdentityForeignSocket]::new($script:gate)) }
        default { throw "Unknown socket fixture $Kind." }
    }
    $script:peer.m_socket = $socket
    [ZRpc].GetField('m_socket', $script:instance).SetValue($script:rpc, $socket)
}
function Get-CharacterFileCount($Fixture) {
    if (-not [IO.Directory]::Exists($Fixture.Layout.RootDirectory)) { return 0 }
    return [IO.Directory]::GetFiles($Fixture.Layout.RootDirectory, '*', [IO.SearchOption]::AllDirectories).Length
}
function Assert-NoCharacterState($Fixture, [int]$InitialFiles, [string]$Context) {
    foreach ($name in @('_serverSessions', '_activeStorageLeases', '_preparedBackupCaptures')) {
        $collection = $Fixture.Service.GetType().GetField($name, $script:allInstance).GetValue($Fixture.Service)
        Assert-True ($collection.Count -eq 0) "$Context allocated $name before identity validation."
    }
    Assert-True ((Get-CharacterFileCount $Fixture) -eq $InitialFiles) "$Context created character files before identity validation."
}
function Assert-CharacterRejected($Fixture, $RequestedPeer, [string]$Context) {
    $initialFiles = Get-CharacterFileCount $Fixture
    foreach ($backup in @($false, $true)) {
        $failure = $null
        try {
            if ($backup) { $Fixture.Service.OpenBackupServerSession($RequestedPeer, $script:storedPayload) | Out-Null }
            else { $Fixture.Service.OpenOrCreateServerSession($RequestedPeer) | Out-Null }
        } catch {
            $failure = $_.Exception
            while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        }
        Assert-True ($null -ne $failure -and $failure.GetType().Name -eq 'CharacterProtocolException' -and
            $failure.Message -eq 'ServerManager character identities require the current final-authenticated Steam connection.') `
            "$Context was not rejected by the shared final-authentication identity boundary (backup=$backup): $failure"
        Assert-NoCharacterState $Fixture $initialFiles "$Context (backup=$backup)"
    }
}

try {
    [byte[]]$storedPayload = New-ProfilePayload 'FixtureProfile' 7101 'stored-character'
    [byte[]]$localPayload = New-ProfilePayload 'FixtureProfile' 7101 'captured-local-character'
    foreach ($backup in @($false, $true)) {
        foreach ($kind in @('raw', 'owned', 'foreign', 'nested')) {
            Reset-Fixture
            Prepare-Join
            Select-CharacterSocket $kind
            $openingFixture = New-CharacterFixture ("positive-$backup-$kind")
            $nativePath = Write-NativeCharacter $openingFixture $storedPayload
            [byte[]]$nativeBefore = [IO.File]::ReadAllBytes($nativePath)
            $backupMode = $backup
            $opened = $null
            $resolvedCharacter = $null
            [SteamAuthenticationEnvironment]::OnComplete = [Action[object]] {
                param($completedRpc)
                Assert-True ([object]::ReferenceEquals($completedRpc, $script:rpc)) 'Completion selected another RPC.'
                $script:resolvedCharacter = $script:openingFixture.IdentityResolver.ResolveServerPeer($script:peer)
                if ($script:backupMode) {
                    $script:opened = $script:openingFixture.Service.OpenBackupServerSession($script:peer, $script:localPayload)
                } else {
                    $script:opened = $script:openingFixture.Service.OpenOrCreateServerSession($script:peer)
                }
            }
            # The real callback processor activates the reservation and calls
            # its existing completion boundary. That boundary now invokes the
            # real character resolver, service, repository and native codecs.
            [SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
            Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
            Assert-Activated "Authentication did not reach character opening ($kind, backup=$backup)."
            Assert-True ($null -ne $opened -and
                $resolvedCharacter.AccountId -ceq 'steamworks:76561198000000001' -and
                $resolvedCharacter.CharacterName -ceq 'FixtureProfile' -and
                $opened.Snapshot.AccountId -ceq $resolvedCharacter.AccountId -and
                $opened.Snapshot.CharacterName -ceq $resolvedCharacter.CharacterName) `
                "Character opening changed the authenticated account/name ($kind, backup=$backup)."
            $expectedPayload = if ($backup) { $localPayload } else { $storedPayload }
            Assert-True (Test-Bytes $opened.Snapshot.GetPayloadCopy() $expectedPayload) "The actual native profile payload changed ($kind, backup=$backup)."
            $decoded = $openingFixture.Codec.FromZPackage($opened.NetworkPackage)
            Assert-True ($decoded.SessionId -eq $opened.Snapshot.SessionId -and
                (Test-Bytes $decoded.GetPayloadCopy() $expectedPayload)) 'Character network payload did not round-trip through the real envelope codec.'
            $sessionArguments = [object[]]@($rpc, $null)
            Assert-True (Invoke-Hidden $openingFixture.Service 'TryGetServerSession' $sessionArguments) 'Character service failed to register the authenticated RPC.'
            Assert-True ($sessionArguments[1].Identity.AccountId -ceq $resolvedCharacter.AccountId -and
                (Get-Hidden $sessionArguments[1] 'BackupOnly') -eq $backup) 'The registered character session has the wrong identity or admission mode.'
            Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($nativePath)) $nativeBefore) 'Opening a session rewrote the existing native character file.'
            Assert-True ([CharacterIdentityForeignSocket]::HostNameReads -eq 0) 'A spoofed socket-wrapper host name supplied character ownership.'
            $openingFixture.Service.CloseServerSession($rpc)
        }
    }

    foreach ($case in @('missing-callback', 'different-peer', 'different-rpc', 'disconnected-original',
        'changed-original-id', 'missing-reservation', 'missing-world-gate')) {
        Reset-Fixture
        Prepare-Join
        Select-CharacterSocket 'nested'
        if ($case -ne 'missing-callback') {
            [SteamAuthenticationEnvironment]::Emit([Steamworks.EAuthSessionResponse]::k_EAuthSessionResponseOK)
            Invoke-Runtime 'ProcessSteamAuthenticationCallbacks' | Out-Null
            Assert-Activated "Negative-case setup failed for $case."
        }
        $requestedPeer = $peer
        switch ($case) {
            'different-peer' {
                $requestedPeer = New-Uninitialized ([ZNetPeer])
                $requestedPeer.m_rpc = $rpc; $requestedPeer.m_uid = 42
                $requestedPeer.m_playerName = 'FixtureProfile'; $requestedPeer.m_socket = $peer.m_socket
            }
            'different-rpc' { $peer.m_rpc = New-Uninitialized ([ZRpc]) }
            'disconnected-original' { [SteamAuthenticationEnvironment]::Connected = $false }
            'changed-original-id' { [SteamAuthenticationEnvironment]::SteamId = [uint64]76561198000000999 }
            'missing-reservation' { (Get-RuntimeField 'SteamAuthenticationsByRpc').Remove($rpc) | Out-Null }
            'missing-world-gate' { (Get-RuntimeField 'WorldBuffers').Remove($rpc) | Out-Null }
        }
        $rejectedFixture = New-CharacterFixture ("negative-$case")
        Assert-CharacterRejected $rejectedFixture $requestedPeer $case
    }
    Write-Host "Authenticated character session smoke passed ($script:checks checks; managed auth callbacks, real resolver, native files, service and payloads)."
}
finally {
    [SteamAuthenticationEnvironment]::OnComplete = $null
    foreach ($fixture in $characterFixtures) { $fixture.Service.Dispose(); $fixture.Keys.Dispose() }
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($fixtureAssemblyResolver)
    $resolvedSessionRoot = [IO.Path]::GetFullPath($sessionTestRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedSessionRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the owned temporary fixture directory.'
    }
    if ([IO.Directory]::Exists($resolvedSessionRoot)) { Remove-Item -LiteralPath $resolvedSessionRoot -Recurse -Force }
}
