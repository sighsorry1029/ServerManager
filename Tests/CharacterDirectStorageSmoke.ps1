param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string[]]$NativeSamplePaths = @()
)

$ErrorActionPreference = 'Stop'
# Reuse only the native dependency shims/reflection/profile builders. No capture
# scenario runs here; production storage methods operate on owned temp files.
. (Join-Path $PSScriptRoot 'BackupOnlySmoke.ps1') -Configuration $Configuration -GamePath $GamePath -FixtureOnly

$directRoot = Join-Path ([IO.Path]::GetTempPath()) ('smdirect-' + [Guid]::NewGuid().ToString('N'))
$fixtures = [Collections.Generic.List[object]]::new()
function New-DirectFixture([string]$Name, [object]$CreationAdminResolver = $null) {
    $options = New-Instance 'CharacterStorageOptions'
    $options.SaveRequestBurstCapacity = 100
    $layout = New-Instance 'CharacterStorageLayout' @((Join-Path $script:directRoot $Name))
    $keys = New-Instance 'CharacterStorageKeyProvider' @($layout)
    $codec = New-Instance 'CharacterEnvelopeCodec' @($options)
    $profiles = New-Instance 'ValheimPlayerProfileCodec' @($options)
    $mode = [Enum]::Parse($script:plugin.GetType('ServerManager.CharacterSemanticPolicyMode'), 'Observe')
    $policy = New-Instance 'CharacterSemanticPolicy' @($mode, '', [single]800, [single]800,
        [single]500, [single]100, [single]1000)
    $validator = New-Instance 'CharacterSemanticRevisionValidator' @($profiles,
        (New-Instance 'CharacterSemanticEvaluator' @($policy)))
    $repository = New-Instance 'CharacterRepository' @($layout, $options, $profiles, $validator, $CreationAdminResolver)
    $service = New-Instance 'CharacterSnapshotService' @($options,
        (New-Instance 'CharacterPeerIdentityResolver'), $keys, $codec, $profiles, $repository, $validator)
    $fixture = [pscustomobject]@{ Options = $options; Layout = $layout; Keys = $keys;
        Codec = $codec; Profiles = $profiles; Repository = $repository; Service = $service }
    $script:fixtures.Add($fixture)
    return $fixture
}
function New-NativeFch([byte[]]$Payload) {
    # Independent Valheim/ServerCharacters wrapper fixture: two length-prefixed
    # byte arrays containing the raw PlayerProfile and its SHA-512 checksum.
    $sha = [Security.Cryptography.SHA512]::Create()
    try { [byte[]]$digest = $sha.ComputeHash($Payload) }
    finally { $sha.Dispose() }
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([int]$Payload.Length); $writer.Write($Payload)
        $writer.Write([int]$digest.Length); $writer.Write($digest); $writer.Flush()
        return ,$stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}
function Write-DirectCharacter($Fixture, $Identity, [byte[]]$Payload) {
    $key = $Fixture.Keys.DeriveStorageKey($Identity)
    $path = $Fixture.Layout.GetProfilePath($key)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllBytes($path, (New-NativeFch $Payload))
    return [pscustomobject]@{ Identity = $Identity; Key = $key; Path = $path; Payload = $Payload }
}
function Validate-DirectStorage($Fixture) {
    Invoke-Hidden $Fixture.Repository 'ValidateStorageKeyMappings' @($Fixture.Keys) | Out-Null
}
function Write-DirectBackup($Fixture, $Identity, [byte[]]$Payload, [string]$Stamp = '2026-09-04_23-04-27') {
    $key = $Fixture.Keys.DeriveStorageKey($Identity)
    $directory = $Fixture.Layout.GetAccountDirectory($key)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $path = Join-Path $directory ($key + '.' + $Stamp + '.fch')
    [IO.File]::WriteAllBytes($path, (New-NativeFch $Payload))
    return $path
}
function Assert-RejectedFile([string]$Name, [string]$Filename, [byte[]]$Bytes) {
    $fixture = New-DirectFixture $Name
    $accountDirectory = Join-Path $fixture.Layout.RootDirectory '76561198000000001'
    [IO.Directory]::CreateDirectory($accountDirectory) | Out-Null
    $path = Join-Path $accountDirectory $Filename
    [IO.File]::WriteAllBytes($path, $Bytes)
    try { Assert-Throws { Validate-DirectStorage $fixture } }
    catch { throw "Native storage rejection case $Name ($Filename) failed: $($_.Exception.Message)" }
    Assert-True ((Test-Path -LiteralPath $path) -and
        (Test-Bytes ([IO.File]::ReadAllBytes($path)) $Bytes)) 'Rejected native storage was rewritten, moved, or removed.'
}
function Prepare-QuotaCharacter($Fixture, $Identity, [long]$PlayerId) {
    $script:quotaFactoryPayload = New-ProfilePayload $Identity.CharacterName $PlayerId 'quota-new-character'
    $factory = [Func[byte[]]] {
        ++$script:quotaFactoryCalls
        return ,$script:quotaFactoryPayload
    }
    return Invoke-Hidden $Fixture.Repository 'PrepareInitialSnapshot' @(
        $Identity, $Fixture.Keys.DeriveStorageKey($Identity), [Guid]::NewGuid(), $factory, [int]46)
}
function Finalize-QuotaCharacter($Fixture, $Identity, $Prepared) {
    return Invoke-Hidden $Fixture.Repository 'FinalizePreparedInitialSnapshot' @(
        $Identity, $Fixture.Keys.DeriveStorageKey($Identity), (Get-Hidden $Prepared 'Envelope'))
}

# A native managed thread can invoke reflection without a PowerShell runspace.
# This fixture coordinates only public reflection/monitor primitives and never
# changes the repository lock or filesystem implementation under test.
Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;
public sealed class DirectStorageReflectionWorker
{
    private readonly object target;
    private readonly MethodInfo method;
    private readonly object[] arguments;
    public readonly ManualResetEvent Started = new ManualResetEvent(false);
    public readonly ManualResetEvent Completed = new ManualResetEvent(false);
    public object Result;
    public Exception Error;
    public DirectStorageReflectionWorker(object target, MethodInfo method, object[] arguments)
    { this.target = target; this.method = method; this.arguments = arguments; }
    public void Start()
    {
        Thread worker = new Thread(delegate() {
            Started.Set();
            try { Result = method.Invoke(target, arguments); }
            catch (Exception error) { Error = error; }
            finally { Completed.Set(); }
        });
        worker.IsBackground = true;
        worker.Start();
    }
}
'@

try {
    # Inspect actual startup wiring and the removed public surface, not a text
    # stand-in. Native profile validation must gate both listener and host start.
    $definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
    try {
        $runtime = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerManagerRuntime'
        $ready = $runtime.Methods | Where-Object Name -eq 'EnsureServerCharacterStorageReady'
        Assert-True ($null -ne $ready -and $ready.ReturnType.FullName -eq 'System.Boolean') 'Native storage readiness gate is missing.'
        foreach ($entryName in @('BeforeServerOpen', 'BeforeLocalHostGameplay')) {
            $entry = $runtime.Methods | Where-Object Name -eq $entryName
            Assert-True ($null -ne $entry -and @($entry.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'EnsureServerCharacterStorageReady'
            }).Count -eq 1) 'A startup entry point skipped native storage validation.'
        }
        Assert-True (@($definition.MainModule.Types | Where-Object {
            $_.Name -eq 'ServerCharactersMigration'
        }).Count -eq 0 -and @($runtime.Methods | Where-Object {
            $_.Name -match 'ServerCharactersMigration|ImportStatus'
        }).Count -eq 0) 'The removed import implementation or runtime entry points remain.'
        $pluginDefinition = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerManagerPlugin'
        Assert-True (@($pluginDefinition.Properties | Where-Object Name -eq 'CharacterImportRoot').Count -eq 0) 'The removed import root remains exposed.'
        $repositoryDefinition = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.CharacterRepository'
        $creationResolver = $runtime.Methods | Where-Object Name -eq 'IsCharacterCreationAdmin'
        Assert-True ($null -ne $creationResolver) 'Missing creation-quota authority resolver.'
        $creationCalls = @($creationResolver.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
        foreach ($requiredCall in @('TryParseCanonicalAccountId', 'IsServer', 'IsDedicated', 'GetSteamID',
            'GetGamePlayerProfile', 'NormalizeAndValidate', 'TryResolveActiveDetectionPeer', 'IsCurrentServerAdmin')) {
            Assert-True (@($creationCalls | Where-Object { $_.Operand.Name -eq $requiredCall }).Count -gt 0) "Creation authority lost its $requiredCall check."
        }
        Assert-True (@($creationCalls | Where-Object { $_.Operand.Name -in @('IsCharacterPolicyAdmin', 'TryGetServerSession', 'IsAdmin') }).Count -eq 0) 'Creation quota reused broader cheat-policy bypass, required an already-created character session, or removed the explicit actual-host exemption.'
        $quotaExemption = $repositoryDefinition.Methods | Where-Object Name -eq 'HasCharacterCreationQuotaExemption'
        Assert-True (@($quotaExemption.Body.ExceptionHandlers | Where-Object { $_.HandlerType.ToString() -eq 'Filter' }).Count -eq 1) 'Creation admin lookup failure is not safely reduced to no exemption.'
        foreach ($quotaGate in @(
            @{ Type = $repositoryDefinition; Name = 'LoadOrPrepareInitialCore'; Arity = 7 },
            @{ Type = $repositoryDefinition; Name = 'FinalizePreparedInitialSnapshot'; Arity = 4 },
            @{ Type = ($definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.CharacterSnapshotService'); Name = 'ValidateCaptureReservations'; Arity = 3 })) {
            $quotaMethod = $quotaGate.Type.Methods | Where-Object { $_.Name -eq $quotaGate.Name -and $_.Parameters.Count -eq $quotaGate.Arity }
            Assert-True ($null -ne $quotaMethod -and @($quotaMethod.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'HasCharacterCreationQuotaExemption'
            }).Count -eq 1) "Creation quota exemption is missing or duplicated at $($quotaGate.Name)."
        }
        $quotaRuntimeSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Networking/ServerManagerRuntime.cs') -Raw
        $quotaResolverStart = $quotaRuntimeSource.IndexOf('private static bool IsCharacterCreationAdmin(', [StringComparison]::Ordinal)
        $quotaResolverEnd = $quotaRuntimeSource.IndexOf('private static bool IsCharacterPolicyAdmin(', $quotaResolverStart, [StringComparison]::Ordinal)
        Assert-True ($quotaResolverStart -ge 0 -and $quotaResolverEnd -gt $quotaResolverStart) 'Could not isolate creation-quota authentication guards.'
        $quotaResolverSource = $quotaRuntimeSource.Substring($quotaResolverStart, $quotaResolverEnd - $quotaResolverStart)
        foreach ($requiredGuard in @('_initialized', '_shuttingDown', 'OnlineBackendType.Steamworks',
            'SteamUser.GetSteamID().m_SteamID == steamId', '_localHostRequested && !_localHostStartupFailed',
            'ReferenceEquals(_localHostNetwork, server)', 'profile != null', 'profile.GetName()',
            'attempt.Phase != SteamAuthenticationPhase.Active', 'peer.HostId', 'peer.PlayerName')) {
            Assert-True ($quotaResolverSource.Contains($requiredGuard)) "Creation-quota authority lost guard $requiredGuard."
        }
        foreach ($lockedSeam in @(
            @{ Name = 'Load'; Arity = 3 },
            @{ Name = 'PersistCheckpointEntry'; Arity = 2 },
            @{ Name = 'PromotePendingSnapshot'; Arity = 4 })) {
            $lockedMethod = $repositoryDefinition.Methods | Where-Object {
                $_.Name -eq $lockedSeam.Name -and $_.Parameters.Count -eq $lockedSeam.Arity
            }
            $calls = @($lockedMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
            $accountGateCall = @($calls | Where-Object { $_.Operand.Name -eq 'GetAccountLock' })
            $firstMonitor = $calls | Where-Object { $_.Operand.DeclaringType.FullName -eq 'System.Threading.Monitor' -and $_.Operand.Name -eq 'Enter' } | Select-Object -First 1
            $profileGateCall = $calls | Where-Object { $_.Operand.Name -eq 'GetOrAdd' } | Select-Object -First 1
            Assert-True ($accountGateCall.Count -eq 1 -and $null -ne $firstMonitor -and $null -ne $profileGateCall -and
                $accountGateCall[0].Offset -lt $firstMonitor.Offset -and $firstMonitor.Offset -lt $profileGateCall.Offset) `
                ('Shared-account scan/write synchronization lost account-before-profile lock order at ' + $lockedSeam.Name + '.')
        }
        $adminList = $repositoryDefinition.Methods | Where-Object { $_.Name -eq 'GetAdminStoredIdentities' -and $_.Parameters.Count -eq 2 }
        $adminCalls = @($adminList.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
        $adminAccountGate = $adminCalls | Where-Object { $_.Operand.Name -eq 'GetAccountLock' } | Select-Object -First 1
        $adminFirstMonitor = $adminCalls | Where-Object { $_.Operand.DeclaringType.FullName -eq 'System.Threading.Monitor' -and $_.Operand.Name -eq 'Enter' } | Select-Object -First 1
        $adminScan = $adminCalls | Where-Object { $_.Operand.Name -eq 'EnumerateAccountFiles' } | Select-Object -First 1
        Assert-True ($null -ne $adminAccountGate -and $null -ne $adminFirstMonitor -and $null -ne $adminScan -and
            $adminAccountGate.Offset -lt $adminFirstMonitor.Offset -and $adminFirstMonitor.Offset -lt $adminScan.Offset) 'Admin identity enumeration scans transient account files outside the account lock.'
    }
    finally { $definition.Dispose() }

    $pathFixture = New-DirectFixture 'path-getter-purity'
    $pathIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000020', 'PathHero')
    $pathKey = $pathFixture.Keys.DeriveStorageKey($pathIdentity)
    $accountPath = $pathFixture.Layout.GetAccountDirectory($pathKey)
    Assert-True ($accountPath -ceq $pathFixture.Layout.GetAccountDirectoryForAccount($pathIdentity.AccountId) -and
        [IO.Path]::GetDirectoryName($pathFixture.Layout.GetProfilePath($pathKey)) -ceq $accountPath -and
        [IO.Path]::GetDirectoryName((Invoke-Hidden $pathFixture.Layout 'GetPendingProfilePath' @($pathKey))) -ceq $accountPath -and
        -not (Test-Path -LiteralPath $accountPath)) 'Path getters created storage directories or disagreed on the account boundary.'
    Assert-True ($null -eq $plugin.GetType('ServerManager.CharacterStorageLayout').GetProperty('BackupsDirectory') -and
        $null -eq $plugin.GetType('ServerManager.CharacterStorageLayout').GetMethod('GetBackupDirectory')) 'The removed separate backup tree remains exposed.'

    $fixture = New-DirectFixture 'native-baseline'
    $identity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000001', 'DisplayHero')
    [byte[]]$raw = New-ProfilePayload 'DisplayHero' 101 'copied-native-fch'
    $direct = Write-DirectCharacter $fixture $identity $raw
    Assert-True ($direct.Key -ceq 'Steam_76561198000000001_displayhero' -and
        [IO.Path]::GetFileName($direct.Path) -ceq 'Steam_76561198000000001_displayhero.fch' -and
        [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($direct.Path)) -ceq '76561198000000001' -and
        $identity.CharacterName -ceq 'DisplayHero') 'Canonical storage changed the embedded/display spelling, account folder, or lowercase filename.'
    [byte[]]$originalFch = [IO.File]::ReadAllBytes($direct.Path)
    Validate-DirectStorage $fixture
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($direct.Path)) $originalFch) 'Startup changed a valid directly placed native file.'

    # The generated guide is non-authoritative: refresh only this file, leaving
    # character data untouched, and do not rewrite an already current guide.
    $guidePath = Join-Path $fixture.Layout.RootDirectory 'HowToRestore.txt'
    Invoke-Hidden $fixture.Repository 'EnsureRestoreInstructions' | Out-Null
    $guideBytes = [IO.File]::ReadAllBytes($guidePath)
    $guide = [Text.UTF8Encoding]::new($false, $true).GetString($guideBytes)
    Assert-True ($guide.StartsWith('ServerManager - Character Restore') -and
        $guide -notmatch '[^\x00-\x7F]') 'The restore guide is not clean English UTF-8 without a BOM.'
    foreach ($instruction in @('serverSettings.loadServerCharacterOnJoin',
        'to true in ServerManager.yml before the player reconnects.',
        'WorldCharacterCheckpointCompleted ... pending=0',
        'sm:characterbackups mrdot', 'sm:characterrestore mrdot <backupId>',
        '/rcon command:save', '/characterrestore player:mrdot backup_id:<backupId>',
        'server MUST be stopped', 'outside the active folder',
        'same account, character and Player ID', 'Keep .fch and the original backup.',
        'there is no import folder', 'Confirm the owner', 'not the world',
        'A timeout does NOT prove', 'restore_unconfirmed', 'logs/events-audit.log')) {
        Assert-True ($guide.Contains($instruction)) "Restore guide omitted an essential procedure or warning: $instruction"
    }
    $guideIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000001', 'MyHero')
    $guideExample = $fixture.Layout.GetProfilePath($fixture.Keys.DeriveStorageKey($guideIdentity))
    Assert-True ($guide.Contains('characters/76561198000000001/' + [IO.Path]::GetFileName($guideExample))) `
        'The native .fch guide example disagrees with the real filename policy.'
    [IO.File]::SetLastWriteTimeUtc($guidePath, [DateTime]::new(2001, 1, 1, 0, 0, 0, [DateTimeKind]::Utc))
    $guideWriteTime = [IO.File]::GetLastWriteTimeUtc($guidePath)
    Invoke-Hidden $fixture.Repository 'EnsureRestoreInstructions' | Out-Null
    Assert-True ([IO.File]::GetLastWriteTimeUtc($guidePath) -eq $guideWriteTime) 'An unchanged restore guide was rewritten.'
    [IO.File]::WriteAllText($guidePath, 'Previous restore instructions.', [Text.UTF8Encoding]::new($false))
    Invoke-Hidden $fixture.Repository 'EnsureRestoreInstructions' | Out-Null
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($guidePath)) $guideBytes) 'An outdated restore guide was not refreshed.'
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($direct.Path)) $originalFch) 'Restore guide refresh changed character data.'
    Validate-DirectStorage $fixture

    $stored = Invoke-Hidden $fixture.Repository 'Load' @($identity, $direct.Key)
    Assert-True ($stored.Envelope.Revision -eq 1 -and $stored.Envelope.BaseRevision -eq 0 -and
        $stored.Envelope.CharacterName -ceq 'DisplayHero' -and
        -not $stored.Envelope.RequiresFreshLocalCharacter -and
        (Test-Bytes $stored.Envelope.GetPayloadCopy() $raw)) 'Direct .fch did not load as an established cold baseline with its original name.'
    $storedIdentities = Invoke-Hidden $fixture.Repository 'GetAdminStoredIdentities' @($fixture.Keys)
    Assert-True ($storedIdentities.Count -eq 1 -and $storedIdentities[0].CharacterName -ceq 'DisplayHero') 'Offline enumeration reconstructed a lowercased display name from the filename.'
    $records = Invoke-Hidden $fixture.Service 'GetAdminCharacters'
    Assert-True ($records.Count -eq 1 -and (Get-Hidden $records[0] 'CharacterName') -ceq 'DisplayHero' -and
        (Get-Hidden $records[0] 'PlayerId') -eq 101) 'Admin listing lost the original name or PlayerID.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.Layout.RootDirectory 'import')) -and
        -not (Test-Path -LiteralPath (Join-Path $fixture.Layout.RootDirectory '.import-complete-v1'))) 'Direct startup created import artifacts.'

    # Storage keys use invariant casing even on machines with Turkish casing
    # rules. Korean names and names containing underscores remain readable.
    $priorCulture = [Threading.Thread]::CurrentThread.CurrentCulture
    try {
        foreach ($culture in @('en-US', 'tr-TR', 'ko-KR')) {
            [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($culture)
            foreach ($name in @('IHero', 'Viking_Name', ([string][char]0xd55c + [char]0xae00 + 'Hero'))) {
                $named = New-Instance 'CharacterIdentity' @($identity.AccountId, $name)
                Assert-True ($fixture.Keys.DeriveStorageKey($named) -ceq
                    ('Steam_76561198000000001_' + $name.ToLowerInvariant()) -and
                    $named.CharacterName -ceq $name) 'Storage naming depended on culture or modified display text.'
            }
        }
    }
    finally { [Threading.Thread]::CurrentThread.CurrentCulture = $priorCulture }
    $markerFixture = New-DirectFixture 'literal-old-backup-marker'
    $markerIdentity = New-Instance 'CharacterIdentity' @($identity.AccountId, 'Hero_backup_auto-Label')
    [byte[]]$markerRaw = New-ProfilePayload 'Hero_backup_auto-Label' 102 'literal-name-marker'
    $markerDirect = Write-DirectCharacter $markerFixture $markerIdentity $markerRaw
    Validate-DirectStorage $markerFixture
    $markerStored = Invoke-Hidden $markerFixture.Repository 'Load' @($markerIdentity, $markerDirect.Key)
    Assert-True ($markerStored.Envelope.CharacterName -ceq $markerIdentity.CharacterName -and
        (Test-Bytes $markerStored.Envelope.GetPayloadCopy() $markerRaw)) 'A primary name containing the obsolete backup marker was misclassified as a backup.'
    $caseAlias = New-Instance 'CharacterIdentity' @($identity.AccountId, 'displayhero')
    Assert-True ($fixture.Keys.DeriveStorageKey($caseAlias) -ceq $direct.Key) 'Case-only names bypassed the common storage key.'
    Assert-Throws { Invoke-Hidden $fixture.Repository 'Load' @($caseAlias, $direct.Key) }
    Assert-Throws { New-Instance 'CharacterStorageKeyProvider' @($fixture.Layout) }

    # Cross-check the production native codec against independent framing and
    # corrupt each boundary. A valid checksum cannot hide malformed profiles.
    $nativeCodec = $plugin.GetType('ServerManager.VanillaCharacterFileCodec')
    $encodeNative = $nativeCodec.GetMethod('Encode', $allStatic)
    $decodeNative = $nativeCodec.GetMethods($allStatic) | Where-Object {
        $_.Name -eq 'Decode' -and $_.GetParameters()[0].ParameterType -eq [byte[]]
    }
    Assert-True ((Test-Bytes $encodeNative.Invoke($null, @($raw, $fixture.Options.MaxPayloadBytes)) $originalFch) -and
        (Test-Bytes $decodeNative.Invoke($null, @($originalFch, $fixture.Options.MaxPayloadBytes)) $raw)) 'Native wrapper was not byte-compatible with Valheim/ServerCharacters framing.'
    $corrupt = [Collections.Generic.List[byte[]]]::new()
    [byte[]]$badHash = $originalFch.Clone(); $badHash[$badHash.Length - 1] = $badHash[$badHash.Length - 1] -bxor 1
    $corrupt.Add($badHash)
    $corrupt.Add([byte[]]($originalFch + [byte]0))
    $corrupt.Add([byte[]]$originalFch[0..($originalFch.Length - 2)])
    foreach ($length in @(-1, 0, ($raw.Length + 1), [int]::MaxValue)) {
        [byte[]]$invalid = $originalFch.Clone()
        [Array]::Copy([BitConverter]::GetBytes([int]$length), 0, $invalid, 0, 4)
        $corrupt.Add($invalid)
    }
    foreach ($length in @(0, 63, 65)) {
        [byte[]]$invalid = $originalFch.Clone()
        [Array]::Copy([BitConverter]::GetBytes([int]$length), 0, $invalid, (4 + $raw.Length), 4)
        $corrupt.Add($invalid)
    }
    $index = 0
    foreach ($bytes in $corrupt) {
        Assert-Throws { $decodeNative.Invoke($null, @($bytes, $fixture.Options.MaxPayloadBytes)) }
        Assert-RejectedFile ('wrapper-' + $index++) ([IO.Path]::GetFileName($direct.Path)) $bytes
    }
    foreach ($badFile in @('Steam_76561198000000001_DisplayHero.fch',
        'steam_76561198000000001_displayhero.fch', 'Steam_076561198000000001_displayhero.fch',
        'Steam_0_displayhero.fch', 'Steam_76561198000000001_displayhero.FCH',
        'Steam_76561198000000001_otherhero.fch', 'old-profile.character')) {
        Assert-RejectedFile ('filename-' + $index++) $badFile $originalFch
    }
    [byte[]]$badVersion = $raw.Clone(); [Array]::Copy([BitConverter]::GetBytes([int]44), 0, $badVersion, 0, 4)
    foreach ($badPayload in @($badVersion,
        (New-ProfilePayload 'DisplayHero' 0 'zero-player'),
        (New-ProfilePayload 'OtherHero' 101 'wrong-name'))) {
        Assert-RejectedFile ('profile-' + $index++) ([IO.Path]::GetFileName($direct.Path)) (New-NativeFch $badPayload)
    }
    $malformedProfile = New-DirectFixture 'malformed-player-payload'
    $malformedDirect = Write-DirectCharacter $malformedProfile $identity ([byte[]]($raw + [byte]0))
    # Startup checks wrapper/identity mappings; full Player-data structure is
    # checked before character session admission, once its policy is available.
    Validate-DirectStorage $malformedProfile
    Assert-Throws { Invoke-Hidden $malformedProfile.Service 'OpenOrCreateLocalHostSession' @($identity) }
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($malformedDirect.Path)) (New-NativeFch $malformedDirect.Payload)) 'Rejected malformed Player payload was modified.'
    $duplicate = New-DirectFixture 'duplicate-player-id'
    $first = Write-DirectCharacter $duplicate $identity $raw
    $otherIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000002', 'OtherHero')
    $second = Write-DirectCharacter $duplicate $otherIdentity (New-ProfilePayload 'OtherHero' 101 'duplicate-player')
    Assert-Throws { Validate-DirectStorage $duplicate } '*PlayerProfile ID*'
    Assert-True ((Test-Bytes ([IO.File]::ReadAllBytes($first.Path)) (New-NativeFch $first.Payload)) -and
        (Test-Bytes ([IO.File]::ReadAllBytes($second.Path)) (New-NativeFch $second.Payload))) 'Duplicate-ID rejection modified either profile.'

    # Only canonical Steam64 account directories may own profile files. No
    # legacy root alias, foreign-account filename, nested folder, or unknown
    # child is silently skipped while opening the character store.
    $badTrees = @(
        @{ Path = 'Steam_76561198000000001_displayhero.fch'; Directory = $false },
        @{ Path = 'unexpected.txt'; Directory = $false },
        @{ Path = 'backups'; Directory = $true },
        @{ Path = 'not-a-steam-account'; Directory = $true },
        @{ Path = '076561198000000001'; Directory = $true },
        @{ Path = '76561198000000001\nested'; Directory = $true },
        @{ Path = '76561198000000001\unexpected.txt'; Directory = $false },
        @{ Path = '76561198000000002\Steam_76561198000000001_displayhero.fch'; Directory = $false },
        @{ Path = '76561198000000002\Steam_76561198000000001_displayhero.fch.pending'; Directory = $false },
        @{ Path = '76561198000000002\Steam_76561198000000001_displayhero.2026-09-04_23-04-27.fch'; Directory = $false },
        @{ Path = '76561198000000002\.Steam_76561198000000001_displayhero.0123456789abcdef0123456789abcdef'; Directory = $false },
        @{ Path = '76561198000000001\Steam_76561198000000001_displayhero.2026-13-04_23-04-27.fch'; Directory = $false },
        @{ Path = '76561198000000001\.Steam_76561198000000001_displayhero.0123456789ABCDEF0123456789ABCDEF'; Directory = $false })
    $badTreeIndex = 0
    foreach ($badTree in $badTrees) {
        $treeFixture = New-DirectFixture ('invalid-account-tree-' + $badTreeIndex++)
        $badPath = Join-Path $treeFixture.Layout.RootDirectory $badTree.Path
        if ($badTree.Directory) { [IO.Directory]::CreateDirectory($badPath) | Out-Null }
        else {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($badPath)) | Out-Null
            [IO.File]::WriteAllBytes($badPath, $originalFch)
        }
        Assert-Throws { Validate-DirectStorage $treeFixture }
        Assert-True (Test-Path -LiteralPath $badPath) 'Rejected account-tree data was removed or migrated.'
        if (-not $badTree.Directory) {
            Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($badPath)) $originalFch) 'Rejected account-tree file bytes were changed.'
        }
    }
    $sameAccountDuplicate = New-DirectFixture 'same-account-duplicate-id'
    Write-DirectCharacter $sameAccountDuplicate $identity $raw | Out-Null
    $sameAccountAlias = New-Instance 'CharacterIdentity' @($identity.AccountId, 'SameAccountAlias')
    Write-DirectCharacter $sameAccountDuplicate $sameAccountAlias (New-ProfilePayload 'SameAccountAlias' 101 'same-account-duplicate') | Out-Null
    Assert-Throws { Validate-DirectStorage $sameAccountDuplicate } '*PlayerProfile ID*'

    # Junctions are Windows reparse points without requiring symlink privilege.
    # Check both account roots and nested account children; unlink the owned
    # junction explicitly before recursive fixture cleanup can see it.
    $linkTargetFixture = New-DirectFixture 'link-target'
    $linkTargetCharacter = Write-DirectCharacter $linkTargetFixture $identity $raw
    foreach ($nestedLink in @($false, $true)) {
        $linkFixture = New-DirectFixture ('reparse-' + $nestedLink)
        $linkAccount = Join-Path $linkFixture.Layout.RootDirectory '76561198000000001'
        $linkPath = $linkAccount
        if ($nestedLink) {
            [IO.Directory]::CreateDirectory($linkAccount) | Out-Null
            $linkPath = Join-Path $linkAccount 'linked-child'
        }
        $linkTarget = [IO.Path]::GetDirectoryName($linkTargetCharacter.Path)
        New-Item -ItemType Junction -Path $linkPath -Target $linkTarget | Out-Null
        try {
            Assert-True (([IO.File]::GetAttributes($linkPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'Link fixture did not create a reparse point.'
            Assert-Throws { Validate-DirectStorage $linkFixture }
            Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($linkTargetCharacter.Path)) (New-NativeFch $raw)) 'Rejecting a linked account mutated its target.'
        }
        finally { [IO.Directory]::Delete($linkPath) }
    }

    # Recovery remains per character in a shared account directory. Orphan
    # backups are kept for manual recovery, but neither create characters nor
    # consume the primary quota. Pending primaries still consume a profile slot.
    $quotaFixture = New-DirectFixture 'primary-only-quota'
    $quotaIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000004', 'QuotaHero')
    [byte[]]$quotaRaw = New-ProfilePayload 'QuotaHero' 401 'quota-primary'
    $quotaDirect = Write-DirectCharacter $quotaFixture $quotaIdentity $quotaRaw
    Write-DirectBackup $quotaFixture $quotaIdentity $quotaRaw | Out-Null
    $orphanIdentity = New-Instance 'CharacterIdentity' @($quotaIdentity.AccountId, 'OrphanHero')
    [byte[]]$orphanRaw = New-ProfilePayload 'OrphanHero' 402 'orphan-backup'
    $orphanPath = Write-DirectBackup $quotaFixture $orphanIdentity $orphanRaw
    Validate-DirectStorage $quotaFixture
    $quotaStored = Invoke-Hidden $quotaFixture.Repository 'GetAdminStoredIdentities' @($quotaFixture.Keys)
    Assert-True ($quotaStored.Count -eq 1 -and $quotaStored[0].CharacterName -ceq 'QuotaHero') 'Backup files were counted as primary characters.'
    $quotaSettings = $plugin.GetType('ServerManager.ServerSettings').GetMethod('Parse').Invoke($null,
        @("serverSettings:`n  loadServerCharacterOnJoin: false`n  maxCharactersPerAccount: 2`n"))
    Invoke-Hidden $quotaFixture.Service 'ApplyServerSettings' @($quotaSettings) | Out-Null
    $orphanCapture = Invoke-Hidden $quotaFixture.Service 'OpenBackupLocalHostSession' @($orphanIdentity, $orphanRaw)
    try {
        Assert-Throws { Invoke-Hidden $quotaFixture.Service 'FinalizePendingLocalHostSnapshot' @($orphanCapture.Snapshot.SessionId) } '*backup*'
        Assert-True (-not (Test-Path -LiteralPath $quotaFixture.Layout.GetProfilePath($quotaFixture.Keys.DeriveStorageKey($orphanIdentity)))) 'An orphan backup was silently replaced by a newly created primary.'
    }
    finally { Invoke-Hidden $quotaFixture.Service 'CloseLocalHostSession' @($orphanCapture.Snapshot.SessionId) | Out-Null }
    $newQuotaIdentity = New-Instance 'CharacterIdentity' @($quotaIdentity.AccountId, 'QuotaSecond')
    $newQuota = Invoke-Hidden $quotaFixture.Service 'OpenBackupLocalHostSession' @($newQuotaIdentity,
        (New-ProfilePayload 'QuotaSecond' 403 'quota-second'))
    Assert-True $newQuota.PendingInitialCommit 'Backups incorrectly exhausted the account primary quota.'
    Invoke-Hidden $quotaFixture.Service 'FinalizePendingLocalHostSnapshot' @($newQuota.Snapshot.SessionId) | Out-Null
    Invoke-Hidden $quotaFixture.Service 'CloseLocalHostSession' @($newQuota.Snapshot.SessionId) | Out-Null
    $thirdQuotaIdentity = New-Instance 'CharacterIdentity' @($quotaIdentity.AccountId, 'QuotaThird')
    Assert-Throws { Invoke-Hidden $quotaFixture.Service 'OpenBackupLocalHostSession' @($thirdQuotaIdentity,
        (New-ProfilePayload 'QuotaThird' 404 'quota-third')) } '*quota*'
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($orphanPath)) (New-NativeFch $orphanRaw)) 'Quota handling consumed or rewrote orphan recovery data.'

    # Creation limits count existing and pending characters, but never select
    # or delete an already stored character after the operator lowers a limit.
    # Authentication is a separately injected boundary here; the actual server
    # resolver is checked in the IL guards, not faked with a client admin claim.
    $script:quotaAdminAllowed = $false
    $script:quotaAdminThrows = $false
    $script:quotaFactoryCalls = 0
    $quotaAdminType = [Func``2].MakeGenericType($plugin.GetType('ServerManager.CharacterIdentity'), [bool])
    $quotaAdmin = [Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock] {
        param($candidate)
        if ($script:quotaAdminThrows) { throw 'Fixture admin lookup unavailable.' }
        return $script:quotaAdminAllowed
    }, $quotaAdminType)
    $creationFixture = New-DirectFixture 'character-creation-default-and-admin' $quotaAdmin
    Assert-True ($creationFixture.Options.MaxCharactersPerAccount -eq 3) 'Storage-level default must match the YAML three-character limit.'
    $creationIdentities = @()
    foreach ($index in 1..3) {
        $creationIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000070', ('Character' + $index))
        $preparedCreation = Prepare-QuotaCharacter $creationFixture $creationIdentity (7000 + $index)
        Assert-True (Get-Hidden $preparedCreation 'PendingInitialCommit') 'New character did not require its ordinary final admission.'
        Finalize-QuotaCharacter $creationFixture $creationIdentity $preparedCreation | Out-Null
        $creationIdentities += $creationIdentity
    }
    Assert-True ((Invoke-Hidden $creationFixture.Repository 'CountProfilesForAccount' @($creationIdentities[0].AccountId)) -eq 3) 'Three admitted pending/primary characters were not counted exactly once.'
    $fourthIdentity = New-Instance 'CharacterIdentity' @($creationIdentities[0].AccountId, 'Character4')
    $callsBeforeQuota = $script:quotaFactoryCalls
    Assert-Throws { Prepare-QuotaCharacter $creationFixture $fourthIdentity 7004 } '*quota*'
    Assert-True ($script:quotaFactoryCalls -eq $callsBeforeQuota) 'Non-admin over-quota admission invoked the initial character factory.'
    Assert-True (-not (Invoke-Hidden $creationFixture.Repository 'HasCharacterCreationQuotaExemption' @($fourthIdentity))) 'A false callback unexpectedly grants an exemption.'
    Assert-True (-not (Invoke-Hidden $quotaFixture.Repository 'HasCharacterCreationQuotaExemption' @($fourthIdentity))) 'A missing callback grants an exemption.'

    $loweredQuota = $plugin.GetType('ServerManager.ServerSettings').GetMethod('Parse').Invoke($null,
        @("serverSettings:`n  maxCharactersPerAccount: 1`n"))
    Invoke-Hidden $creationFixture.Service 'ApplyServerSettings' @($loweredQuota) | Out-Null
    foreach ($existingIdentity in $creationIdentities) {
        $existingOpen = Invoke-Hidden $creationFixture.Service 'OpenOrCreateLocalHostSession' @($existingIdentity)
        Assert-True ($existingOpen.Snapshot.CharacterName -ceq $existingIdentity.CharacterName) 'Lowering three to one selected only the first joining character or rewrote another character identity.'
        Invoke-Hidden $creationFixture.Service 'CloseLocalHostSession' @($existingOpen.Snapshot.SessionId) | Out-Null
    }
    Assert-Throws { Invoke-Hidden $creationFixture.Service 'OpenOrCreateLocalHostSession' @($fourthIdentity) } '*quota*'
    Assert-True ((Invoke-Hidden $creationFixture.Repository 'CountProfilesForAccount' @($creationIdentities[0].AccountId)) -eq 3) 'Quota reduction removed stored characters.'
    $restoredQuota = $plugin.GetType('ServerManager.ServerSettings').GetMethod('Parse').Invoke($null,
        @("serverSettings:`n  maxCharactersPerAccount: 3`n"))
    Invoke-Hidden $creationFixture.Service 'ApplyServerSettings' @($restoredQuota) | Out-Null

    $script:quotaAdminAllowed = $true
    $preparedFourth = Prepare-QuotaCharacter $creationFixture $fourthIdentity 7004
    Assert-True (Get-Hidden $preparedFourth 'PendingInitialCommit') 'The authenticated admin callback did not exempt an over-quota preparation.'
    $script:quotaAdminAllowed = $false
    Assert-Throws { Finalize-QuotaCharacter $creationFixture $fourthIdentity $preparedFourth } '*quota*'
    $fourthKey = $creationFixture.Keys.DeriveStorageKey($fourthIdentity)
    Assert-True (-not (Test-Path -LiteralPath $creationFixture.Layout.GetProfilePath($fourthKey)) -and
        -not (Test-Path -LiteralPath (Invoke-Hidden $creationFixture.Layout 'GetPendingProfilePath' @($fourthKey)))) 'Revoked admin entitlement persisted an over-quota primary or pending primary.'
    $script:quotaAdminAllowed = $true
    $script:quotaAdminThrows = $true
    Assert-Throws { Finalize-QuotaCharacter $creationFixture $fourthIdentity $preparedFourth } '*quota*'
    $script:quotaAdminThrows = $false
    Finalize-QuotaCharacter $creationFixture $fourthIdentity $preparedFourth | Out-Null
    Assert-True ((Invoke-Hidden $creationFixture.Repository 'CountProfilesForAccount' @($fourthIdentity.AccountId)) -eq 4) 'Admin exemption did not reach the independently rechecked final commit.'
    $fifthIdentity = New-Instance 'CharacterIdentity' @($fourthIdentity.AccountId, 'Character5')
    $script:quotaAdminThrows = $true
    Assert-Throws { Prepare-QuotaCharacter $creationFixture $fifthIdentity 7005 } '*quota*'
    Assert-True (-not (Invoke-Hidden $creationFixture.Repository 'HasCharacterCreationQuotaExemption' @($fifthIdentity))) 'Admin lookup exceptions fail open.'
    $script:quotaAdminThrows = $false
    $script:quotaAdminAllowed = $false

    # Backup-only admission uses reservation counts, including not-yet-ready
    # remote peers; the same count exemption must apply without disabling ID
    # ownership checks or treating an unverified direct call as a host exemption.
    $captureQuotaFixture = New-DirectFixture 'backup-creation-admin' $quotaAdmin
    $captureOwner = 'steamworks:76561198000000071'
    foreach ($index in 1..3) {
        $captureIdentity = New-Instance 'CharacterIdentity' @($captureOwner, ('Capture' + $index))
        Write-DirectCharacter $captureQuotaFixture $captureIdentity (New-ProfilePayload $captureIdentity.CharacterName (7100 + $index) 'existing-capture') | Out-Null
    }
    $captureFourth = New-Instance 'CharacterIdentity' @($captureOwner, 'Capture4')
    [byte[]]$captureFourthBytes = New-ProfilePayload 'Capture4' 7104 'admin-capture'
    Assert-Throws { Invoke-Hidden $captureQuotaFixture.Service 'OpenBackupLocalHostSession' @($captureFourth, $captureFourthBytes) } '*quota*'
    $script:quotaAdminAllowed = $true
    $capturePrepared = Invoke-Hidden $captureQuotaFixture.Service 'OpenBackupLocalHostSession' @($captureFourth, $captureFourthBytes)
    $script:quotaAdminAllowed = $false
    Assert-Throws { Invoke-Hidden $captureQuotaFixture.Service 'FinalizePendingLocalHostSnapshot' @($capturePrepared.Snapshot.SessionId) } '*quota*'
    Invoke-Hidden $captureQuotaFixture.Service 'CloseLocalHostSession' @($capturePrepared.Snapshot.SessionId) | Out-Null
    Assert-True ((Invoke-Hidden $captureQuotaFixture.Repository 'CountProfilesForAccount' @($captureOwner)) -eq 3) 'Revoked backup admission wrote an extra character.'
    $script:quotaAdminAllowed = $true
    $capturePrepared = Invoke-Hidden $captureQuotaFixture.Service 'OpenBackupLocalHostSession' @($captureFourth, $captureFourthBytes)
    Invoke-Hidden $captureQuotaFixture.Service 'FinalizePendingLocalHostSnapshot' @($capturePrepared.Snapshot.SessionId) | Out-Null
    Invoke-Hidden $captureQuotaFixture.Service 'CloseLocalHostSession' @($capturePrepared.Snapshot.SessionId) | Out-Null
    Assert-True ((Invoke-Hidden $captureQuotaFixture.Repository 'CountProfilesForAccount' @($captureOwner)) -eq 4) 'Admin exemption did not apply through backup-only local-host admission and commit.'
    $captureAlias = New-Instance 'CharacterIdentity' @($captureOwner, 'CaptureAlias')
    Assert-Throws { Invoke-Hidden $captureQuotaFixture.Service 'OpenBackupLocalHostSession' @($captureAlias,
        (New-ProfilePayload 'CaptureAlias' 7104 'duplicate-id')) } '*PlayerProfile ID*'
    $captureFifth = New-Instance 'CharacterIdentity' @($captureOwner, 'Capture5')
    $script:quotaAdminThrows = $true
    Assert-Throws { Invoke-Hidden $captureQuotaFixture.Service 'OpenBackupLocalHostSession' @($captureFifth,
        (New-ProfilePayload 'Capture5' 7105 'callback-failure')) } '*quota*'
    $script:quotaAdminThrows = $false
    $script:quotaAdminAllowed = $false

    $reservedFixture = New-DirectFixture 'connecting-character-reservations' $quotaAdmin
    $reservationOwner = 'steamworks:76561198000000072'
    foreach ($index in 1..2) {
        $reservedIdentity = New-Instance 'CharacterIdentity' @($reservationOwner, ('Reserved' + $index))
        Write-DirectCharacter $reservedFixture $reservedIdentity (New-ProfilePayload $reservedIdentity.CharacterName (7200 + $index) 'reserved-existing') | Out-Null
    }
    $reservationIdentity = New-Instance 'CharacterIdentity' @($reservationOwner, 'Reserved3')
    $reservedRpc = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($game.GetType('ZRpc', $true))
    $reservation = Invoke-Hidden $reservedFixture.Service 'OpenBackupSessionCore' @($reservationIdentity, $reservedRpc,
        (New-ProfilePayload 'Reserved3' 7203 'remote-connecting'))
    $reservationFourth = New-Instance 'CharacterIdentity' @($reservationOwner, 'Reserved4')
    Assert-Throws { Invoke-Hidden $reservedFixture.Service 'OpenBackupLocalHostSession' @($reservationFourth,
        (New-ProfilePayload 'Reserved4' 7204 'host-connecting')) } '*quota*'
    $script:quotaAdminAllowed = $true
    $reservedAdmin = Invoke-Hidden $reservedFixture.Service 'OpenBackupLocalHostSession' @($reservationFourth,
        (New-ProfilePayload 'Reserved4' 7204 'host-admin'))
    Invoke-Hidden $reservedFixture.Service 'CloseLocalHostSession' @($reservedAdmin.Snapshot.SessionId) | Out-Null
    $reservedFixture.Service.CloseServerSession($reservedRpc)
    $script:quotaAdminAllowed = $false

    $pendingFixture = New-DirectFixture 'pending-recovery'
    $pendingIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000005', 'PendingHero')
    $pendingKey = $pendingFixture.Keys.DeriveStorageKey($pendingIdentity)
    $pendingPath = Invoke-Hidden $pendingFixture.Layout 'GetPendingProfilePath' @($pendingKey)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($pendingPath)) | Out-Null
    [byte[]]$pendingRaw = New-ProfilePayload 'PendingHero' 501 'pending-baseline' -HasPlayerData $false
    [IO.File]::WriteAllBytes($pendingPath, (New-NativeFch $pendingRaw))
    Validate-DirectStorage $pendingFixture
    $pendingStored = Invoke-Hidden $pendingFixture.Repository 'Load' @($pendingIdentity, $pendingKey)
    Assert-True ($pendingStored.Envelope.RequiresFreshLocalCharacter -and
        (Test-Bytes $pendingStored.Envelope.GetPayloadCopy() $pendingRaw)) 'Nested pending primary lost its unmaterialized origin.'
    $pendingSettings = $plugin.GetType('ServerManager.ServerSettings').GetMethod('Parse').Invoke($null,
        @("serverSettings:`n  loadServerCharacterOnJoin: false`n  maxCharactersPerAccount: 1`n"))
    Invoke-Hidden $pendingFixture.Service 'ApplyServerSettings' @($pendingSettings) | Out-Null
    $pendingQuotaIdentity = New-Instance 'CharacterIdentity' @($pendingIdentity.AccountId, 'PendingQuotaAlias')
    Assert-Throws { Invoke-Hidden $pendingFixture.Service 'OpenBackupLocalHostSession' @($pendingQuotaIdentity,
        (New-ProfilePayload 'PendingQuotaAlias' 502 'pending-counts-for-quota')) } '*quota*'
    $pendingTemporaryPath = Join-Path ([IO.Path]::GetDirectoryName($pendingPath)) ('.' + $pendingKey + '.0123456789abcdef0123456789abcdef')
    [IO.File]::WriteAllBytes($pendingTemporaryPath, [byte[]]@(1, 2, 3))
    Validate-DirectStorage $pendingFixture
    Assert-True (-not (Test-Path -LiteralPath $pendingTemporaryPath) -and
        (Test-Bytes ([IO.File]::ReadAllBytes($pendingPath)) (New-NativeFch $pendingRaw))) 'Managed temporary cleanup failed beside a valid pending primary or changed its bytes.'
    [byte[]]$completedRaw = New-ProfilePayload 'PendingHero' 501 'completed-promotion'
    $pendingBackupPath = Write-DirectBackup $pendingFixture $pendingIdentity $completedRaw
    Assert-Throws { Validate-DirectStorage $pendingFixture } '*pending*backups*'
    Assert-True ((Test-Path -LiteralPath $pendingPath) -and (Test-Path -LiteralPath $pendingBackupPath)) 'Invalid pending/backup recovery pair was consumed.'
    [IO.File]::Delete($pendingBackupPath)
    $completedDirect = Write-DirectCharacter $pendingFixture $pendingIdentity $completedRaw
    Validate-DirectStorage $pendingFixture
    Assert-True (-not (Test-Path -LiteralPath $pendingPath) -and
        (Test-Bytes ([IO.File]::ReadAllBytes($completedDirect.Path)) (New-NativeFch $completedRaw))) 'Completed final/pending crash residue was not safely resolved within the account.'

    $temporaryFixture = New-DirectFixture 'temporary-recovery'
    $temporaryDirect = Write-DirectCharacter $temporaryFixture $identity $raw
    $temporaryPath = Join-Path $temporaryFixture.Layout.GetAccountDirectory($temporaryDirect.Key) `
        ('.' + $temporaryDirect.Key + '.0123456789abcdef0123456789abcdef')
    [byte[]]$unfinishedBytes = @(1, 2, 3, 4)
    [IO.File]::WriteAllBytes($temporaryPath, $unfinishedBytes)
    $invalidSiblingPath = Join-Path $temporaryFixture.Layout.GetAccountDirectory($temporaryDirect.Key) 'unknown-child'
    [IO.File]::WriteAllBytes($invalidSiblingPath, $unfinishedBytes)
    Assert-Throws { Validate-DirectStorage $temporaryFixture }
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($temporaryPath)) $unfinishedBytes) 'Startup partially cleaned managed temporary data before all account files passed validation.'
    [IO.File]::Delete($invalidSiblingPath)
    Validate-DirectStorage $temporaryFixture
    Assert-True (-not (Test-Path -LiteralPath $temporaryPath) -and
        (Test-Bytes ([IO.File]::ReadAllBytes($temporaryDirect.Path)) (New-NativeFch $raw))) 'Completed managed temporary cleanup changed the canonical primary.'
    $orphanTemporary = New-DirectFixture 'orphan-temporary'
    $orphanTemporaryAccount = $orphanTemporary.Layout.GetAccountDirectory($direct.Key)
    [IO.Directory]::CreateDirectory($orphanTemporaryAccount) | Out-Null
    $orphanTemporaryPath = Join-Path $orphanTemporaryAccount ('.' + $direct.Key + '.0123456789abcdef0123456789abcdef')
    [IO.File]::WriteAllBytes($orphanTemporaryPath, $unfinishedBytes)
    Assert-Throws { Validate-DirectStorage $orphanTemporary } '*temporary*without*primary*'
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($orphanTemporaryPath)) $unfinishedBytes) 'An orphan managed temporary file was discarded instead of retained for manual recovery.'

    # Hold one account's real lock while loading a sibling character from a
    # native thread. The sibling must wait; a different lock stripe must remain
    # independently readable. This deterministically exercises the race guard
    # without racing the filesystem or relying on arbitrary save timing.
    $lockFixture = New-DirectFixture 'account-lock-coordination'
    $lockIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000040', 'LockHero')
    $siblingLockIdentity = New-Instance 'CharacterIdentity' @($lockIdentity.AccountId, 'SiblingLockHero')
    Write-DirectCharacter $lockFixture $lockIdentity (New-ProfilePayload 'LockHero' 4001 'account-lock') | Out-Null
    $siblingLockDirect = Write-DirectCharacter $lockFixture $siblingLockIdentity (New-ProfilePayload 'SiblingLockHero' 4002 'sibling-lock')
    $repositoryType = $plugin.GetType('ServerManager.CharacterRepository')
    $getAccountLock = $repositoryType.GetMethod('GetAccountLock', $allStatic)
    $heldAccountGate = $getAccountLock.Invoke($null, [object[]]@($lockIdentity.AccountId))
    $differentLockIdentity = $null
    foreach ($candidateAccountNumber in 50..80) {
        $candidateAccount = 'steamworks:' + ([long]76561198000000000 + $candidateAccountNumber).ToString([Globalization.CultureInfo]::InvariantCulture)
        if (-not [object]::ReferenceEquals($heldAccountGate, $getAccountLock.Invoke($null, [object[]]@($candidateAccount)))) {
            $differentLockIdentity = New-Instance 'CharacterIdentity' @($candidateAccount, 'DifferentLockHero')
            break
        }
    }
    Assert-True ($null -ne $differentLockIdentity) 'Could not choose an independent account-lock stripe for concurrency verification.'
    $differentLockDirect = Write-DirectCharacter $lockFixture $differentLockIdentity (New-ProfilePayload 'DifferentLockHero' 4003 'different-lock')
    $loadMethod = $repositoryType.GetMethods($allInstance) | Where-Object { $_.Name -eq 'Load' -and $_.GetParameters().Count -eq 2 }
    $sameAccountWorker = [DirectStorageReflectionWorker]::new($lockFixture.Repository, $loadMethod, [object[]]@($siblingLockIdentity, $siblingLockDirect.Key))
    $otherAccountWorker = [DirectStorageReflectionWorker]::new($lockFixture.Repository, $loadMethod, [object[]]@($differentLockIdentity, $differentLockDirect.Key))
    [Threading.Monitor]::Enter($heldAccountGate)
    try {
        $sameAccountWorker.Start()
        Assert-True ($sameAccountWorker.Started.WaitOne(5000)) 'Same-account read worker did not start.'
        Assert-True (-not $sameAccountWorker.Completed.WaitOne(100)) 'A sibling character read bypassed the held account lock.'
        $otherAccountWorker.Start()
        Assert-True ($otherAccountWorker.Completed.WaitOne(5000) -and $null -eq $otherAccountWorker.Error) 'An unrelated account read was blocked by another account lock or failed.'
        Assert-True (Test-Bytes $otherAccountWorker.Result.Envelope.GetPayloadCopy() $differentLockDirect.Payload) 'Independent account read returned another character snapshot.'
    }
    finally { [Threading.Monitor]::Exit($heldAccountGate) }
    Assert-True ($sameAccountWorker.Completed.WaitOne(5000) -and $null -eq $sameAccountWorker.Error -and
        (Test-Bytes $sameAccountWorker.Result.Envelope.GetPayloadCopy() $siblingLockDirect.Payload)) 'A sibling read did not resume with exact bytes after releasing the account lock.'
    foreach ($worker in @($sameAccountWorker, $otherAccountWorker)) { $worker.Started.Dispose(); $worker.Completed.Dispose() }

    # Direct native characters participate in the existing revision/cutoff
    # pipeline. A cutoff must persist its own bytes and retain newer RAM state.
    $opened = Invoke-Hidden $fixture.Service 'OpenOrCreateLocalHostSession' @($identity)
    Assert-True (-not $opened.PendingInitialCommit -and
        $opened.Snapshot.CharacterName -ceq 'DisplayHero' -and
        (Test-Bytes $opened.Snapshot.GetPayloadCopy() $raw)) 'Direct native character was treated as a new or imported profile.'
    [byte[]]$raw2 = New-ProfilePayload 'DisplayHero' 101 'cutoff-2'
    [byte[]]$raw3 = New-ProfilePayload 'DisplayHero' 101 'retained-3'
    $save2 = New-Request $identity $opened.Snapshot.SessionId 2 1 $raw2
    Assert-True (Invoke-Hidden $fixture.Service 'HandleLocalHostSaveRequest' @($opened.Snapshot.SessionId, $save2)).Accepted 'Direct profile could not accept a normal save.'
    $cutoff = Invoke-Hidden $fixture.Service 'BeginCheckpoint'
    $entries = Get-Hidden $cutoff 'Entries'
    $save3 = New-Request $identity $opened.Snapshot.SessionId 3 2 $raw3
    Assert-True (Invoke-Hidden $fixture.Service 'HandleLocalHostSaveRequest' @($opened.Snapshot.SessionId, $save3)).Accepted 'Post-cutoff direct profile save rejected.'
    Invoke-Hidden $fixture.Service 'CloseLocalHostSession' @($opened.Snapshot.SessionId) | Out-Null
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($direct.Path)) $originalFch) 'Gameplay RAM save prematurely changed the direct primary.'
    Invoke-Hidden $fixture.Service 'CommitCheckpointEntry' @($cutoff, $entries[0]) | Out-Null
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($direct.Path)) (New-NativeFch $raw2)) 'Cutoff did not persist its native profile generation.'
    $reopened = Invoke-Hidden $fixture.Service 'OpenOrCreateLocalHostSession' @($identity)
    Assert-True ($reopened.Snapshot.Revision -eq 3 -and
        (Test-Bytes $reopened.Snapshot.GetPayloadCopy() $raw3)) 'Checkpoint completion overwrote newer retained RAM.'
    Invoke-Hidden $fixture.Service 'CloseLocalHostSession' @($reopened.Snapshot.SessionId) | Out-Null
    $backupDirectory = $fixture.Layout.GetAccountDirectory($direct.Key)
    $files = @(Get-ChildItem -LiteralPath $backupDirectory -File | Where-Object {
        $_.Name.StartsWith($direct.Key + '.', [StringComparison]::Ordinal) -and $_.Name -cne ($direct.Key + '.fch')
    })
    Assert-True ($files.Count -eq 1 -and $files[0].Name -cmatch
        ('^' + [Regex]::Escape($direct.Key) + '\.\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(?:\.\d{2,4})?\.fch$') -and
        (Test-Bytes ([IO.File]::ReadAllBytes($files[0].FullName)) $originalFch)) 'Checkpoint backup did not use the native canonical name and original bytes.'
    $backups = Invoke-Hidden $fixture.Repository 'GetAdminBackups' @($identity, $direct.Key)
    $backupId = Get-Hidden $backups[0] 'BackupId'
    $readBackup = Invoke-Hidden $fixture.Repository 'ReadAdminBackup' @($identity, $direct.Key, $backupId)
    Assert-True ($readBackup.CharacterName -ceq 'DisplayHero' -and
        (Test-Bytes $readBackup.GetPayloadCopy() $raw)) 'Backup load changed original display metadata.'
    $pendingCutoff = Invoke-Hidden $fixture.Service 'BeginCheckpoint'
    $blockedRestore = Invoke-Hidden $fixture.Service 'RestoreAdminBackup' @($identity.AccountId, $identity.CharacterName, $backupId)
    Assert-True (-not $blockedRestore.Success -and $blockedRestore.Code -eq 'checkpoint_pending' -and
        (Test-Bytes ([IO.File]::ReadAllBytes($direct.Path)) (New-NativeFch $raw2))) 'Restore invalidated a registered checkpoint generation.'
    Invoke-Hidden $fixture.Service 'DiscardCheckpoint' @($pendingCutoff) | Out-Null
    $shadowRestore = Invoke-Hidden $fixture.Service 'RestoreAdminBackup' @($identity.AccountId, $identity.CharacterName, $backupId)
    Assert-True (-not $shadowRestore.Success -and $shadowRestore.Code -eq 'shadow_pending') 'Discarding a cutoff bypassed the pending-RAM restore guard.'
    $finalCutoff = Invoke-Hidden $fixture.Service 'BeginCheckpoint'
    foreach ($entry in (Get-Hidden $finalCutoff 'Entries')) {
        Invoke-Hidden $fixture.Service 'CommitCheckpointEntry' @($finalCutoff, $entry) | Out-Null
    }
    $restored = Invoke-Hidden $fixture.Service 'RestoreAdminBackup' @($identity.AccountId, $identity.CharacterName, $backupId)
    Assert-True ($restored.Success -and (Test-Bytes ([IO.File]::ReadAllBytes($direct.Path)) $originalFch)) 'Native backup restore failed after checkpoint release.'
    $restoredOpen = Invoke-Hidden $fixture.Service 'OpenOrCreateLocalHostSession' @($identity)
    Assert-True ($restoredOpen.Snapshot.Revision -eq 1 -and $restoredOpen.Snapshot.CharacterName -ceq 'DisplayHero' -and
        (Test-Bytes $restoredOpen.Snapshot.GetPayloadCopy() $raw)) 'Restore did not retire stale RAM or retain original display name.'
    Invoke-Hidden $fixture.Service 'CloseLocalHostSession' @($restoredOpen.Snapshot.SessionId) | Out-Null
    Validate-DirectStorage $fixture
    Assert-True (@(Get-ChildItem -LiteralPath $fixture.Layout.GetAccountDirectory($direct.Key) -File | Where-Object {
        $_.Name -ceq ($direct.Key + '.fch')
    }).Count -eq 1) 'Native operations created a second case-variant primary.'

    # The native schema stores mod-owned scalar values verbatim. Neither the
    # cold-load parser nor inventory-only materialization owns vanilla catalog
    # rules, normalization, or repairs to those values.
    $modFixture = New-DirectFixture 'mod-raw-roundtrip'
    $modIdentity = New-Instance 'CharacterIdentity' @('steamworks:76561198000000003', 'ModHero')
    $modSkills = @(
        @{ Id = [int]::MinValue; Level = [single]::MinValue; Accumulator = [single]::MaxValue },
        @{ Id = -700123; Level = [single]-1.5; Accumulator = [single]-2500000000 },
        @{ Id = 0; Level = [single]125.25; Accumulator = [single]2000000000 },
        @{ Id = 1; Level = [single]150; Accumulator = [single]-3 },
        @{ Id = 999; Level = [single]101; Accumulator = [single]::MinValue },
        @{ Id = 1980891425; Level = [single]::MaxValue; Accumulator = [single]123.5 },
        @{ Id = [int]::MaxValue; Level = [single]-42; Accumulator = [single]0 })
    $modItems = @(
        @{ Prefab = 'Mod_UnregisteredWeapon'; Stack = 65535; Quality = 0;
            Variant = [int]::MinValue; Durability = [single]-21474836; X = 0; Y = 0;
            Custom = [ordered]@{ 'mod:data' = '{"quality":0,"raw":true}'; 'owner' = 'ModHero' } },
        @{ Prefab = 'Mod_UnregisteredArmor'; Stack = 65534; Quality = 65519;
            Variant = [int]::MaxValue; Durability = [single]21474836; X = 255; Y = 255; WorldLevel = 255 },
        @{ Prefab = 'Mod_UnregisteredUtility'; Quality = 65535; Variant = 40000;
            Durability = [single]-1; X = 254; Y = 255; WorldLevel = 1 })
    [byte[]]$modRaw = New-ProfilePayload 'ModHero' 303 'mod-owned-values' -SkillValues $modSkills -InventoryItems $modItems
    $modDirect = Write-DirectCharacter $modFixture $modIdentity $modRaw
    Validate-DirectStorage $modFixture
    $extract = $plugin.GetType('ServerManager.ValheimPlayerProfileCodec').GetMethods($allInstance) | Where-Object {
        $_.Name -eq 'ExtractValidatedSnapshot' -and $_.GetParameters()[0].ParameterType.Name -eq 'CharacterIdentity'
    }
    $modValidated = $extract.Invoke($modFixture.Profiles, [object[]]@($modIdentity, $modRaw))
    $modSemantic = Get-Hidden $modValidated 'SemanticSnapshot'
    Assert-True ($modSemantic.Skills.Count -eq $modSkills.Count -and $modSemantic.Items.Count -eq $modItems.Count) 'The parser dropped custom skill IDs or unknown prefabs.'
    foreach ($expectedSkill in $modSkills) {
        $actualSkill = $modSemantic.Skills[[int]$expectedSkill.Id]
        Assert-True ($actualSkill.SkillType -eq $expectedSkill.Id -and $actualSkill.Level -eq $expectedSkill.Level -and
            $actualSkill.Accumulator -eq $expectedSkill.Accumulator) 'Mod skill scalar values were clamped, normalized, or remapped.'
    }
    for ($itemIndex = 0; $itemIndex -lt $modItems.Count; ++$itemIndex) {
        Assert-True ($modSemantic.Items[$itemIndex].PrefabHash -eq [Valheim107Fixture]::Hash($modItems[$itemIndex].Prefab) -and
            $modSemantic.Items[$itemIndex].Quality -eq $modItems[$itemIndex].Quality) 'Unknown item prefab or raw quality was normalized.'
    }
    $modOpened = Invoke-Hidden $modFixture.Service 'OpenOrCreateLocalHostSession' @($modIdentity)
    Assert-True ((Test-Bytes $modOpened.Snapshot.GetPayloadCopy() $modRaw) -and -not $modOpened.PendingInitialCommit) 'Cold session admission changed mod data or created a replacement character.'
    $modReplacementItems = @($modItems + @{ Prefab = 'Mod_AddedWithoutObjectDB'; Quality = 0;
        Variant = -9; Durability = [single]987654321; X = 17; Y = 31 })
    [byte[]]$modInventory = New-InventoryPayload $modReplacementItems
    [byte[]]$expectedMerged = New-ProfilePayload 'ModHero' 303 'mod-owned-values' -SkillValues $modSkills -InventoryItems $modReplacementItems
    $mergeArguments = [object[]]@($modIdentity, $modRaw, $modInventory, $null)
    $replaceInventory = $plugin.GetType('ServerManager.ValheimPlayerProfileCodec').GetMethod('ReplaceInventorySnapshot', $allInstance)
    [byte[]]$merged = $replaceInventory.Invoke($modFixture.Profiles, $mergeArguments)
    Assert-True ((Test-Bytes $merged $expectedMerged) -and $null -ne $mergeArguments[3]) 'Inventory fast-path altered unselected mod skill, metadata, or scalar bytes.'
    $modInventoryRequest = New-Request $modIdentity $modOpened.Snapshot.SessionId 2 1 $modInventory 'InventorySaveRequest'
    Assert-True (Invoke-Hidden $modFixture.Service 'HandleLocalHostSaveRequest' @($modOpened.Snapshot.SessionId, $modInventoryRequest)).Accepted 'The normal inventory-save service path rejected mod-owned values.'
    $modCurrent = Invoke-Hidden $modFixture.Service 'GetLocalHostSnapshot' @($modOpened.Snapshot.SessionId)
    Assert-True (Test-Bytes $modCurrent.GetPayloadCopy() $expectedMerged) 'Inventory-save acceptance failed to preserve exact full-profile bytes.'
    Invoke-Hidden $modFixture.Service 'CloseLocalHostSession' @($modOpened.Snapshot.SessionId) | Out-Null
    $invalidAdminEdit = Invoke-Hidden $modFixture.Service 'ApplyOfflineSkillAdmin' @($modIdentity.AccountId, $modIdentity.CharacterName, 'set', 'Swords', [single]101)
    Assert-True (-not $invalidAdminEdit.Success) 'Accepting raw stored skill values also relaxed explicit administrator command input limits.'
    $adminEdit = Invoke-Hidden $modFixture.Service 'ApplyOfflineSkillAdmin' @($modIdentity.AccountId, $modIdentity.CharacterName, 'set', 'Swords', [single]42)
    Assert-True ($adminEdit.Success -and $adminEdit.Code -eq 'staged_in_ram') 'An offline selected-skill edit rejected unrelated mod values.'
    $editedSkills = @($modSkills | ForEach-Object {
        if ($_.Id -eq 1) { @{ Id = 1; Level = [single]42; Accumulator = [single]0 } } else { $_ }
    } | Sort-Object { [int]$_.Id })
    [byte[]]$expectedEdited = New-ProfilePayload 'ModHero' 303 'mod-owned-values' -SkillValues $editedSkills -InventoryItems $modReplacementItems
    $modEditedOpen = Invoke-Hidden $modFixture.Service 'OpenOrCreateLocalHostSession' @($modIdentity)
    Assert-True ($modEditedOpen.Snapshot.Revision -eq 3 -and (Test-Bytes $modEditedOpen.Snapshot.GetPayloadCopy() $expectedEdited)) 'Offline skill editing changed unselected custom skills or unrelated item bytes.'
    Invoke-Hidden $modFixture.Service 'CloseLocalHostSession' @($modEditedOpen.Snapshot.SessionId) | Out-Null
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($modDirect.Path)) (New-NativeFch $modRaw)) 'Mod inventory or administrator RAM edits bypassed world checkpoint persistence.'
    $modCheckpoint = Invoke-Hidden $modFixture.Service 'BeginCheckpoint'
    foreach ($entry in (Get-Hidden $modCheckpoint 'Entries')) {
        Invoke-Hidden $modFixture.Service 'CommitCheckpointEntry' @($modCheckpoint, $entry) | Out-Null
    }
    Assert-True (Test-Bytes ([IO.File]::ReadAllBytes($modDirect.Path)) (New-NativeFch $expectedEdited)) 'Checkpoint serialization changed mod values after an explicit selected-skill edit.'

    # Relaxed catalog/value policy must not weaken the structural contract.
    foreach ($invalidSkills in @(
        @(@{ Id = 1980891425; Level = 1; Accumulator = 0 }, @{ Id = 1980891425; Level = 2; Accumulator = 0 }),
        @(@{ Id = -3; Level = [single]::NaN; Accumulator = 0 }),
        @(@{ Id = -3; Level = [single]::PositiveInfinity; Accumulator = 0 }),
        @(@{ Id = -3; Level = 1; Accumulator = [single]::NegativeInfinity }))) {
        [byte[]]$invalidRaw = New-ProfilePayload 'ModHero' 303 'invalid-mod-skill' -SkillValues $invalidSkills
        Assert-Throws { $extract.Invoke($modFixture.Profiles, [object[]]@($modIdentity, $invalidRaw)) }
    }
    # Durability is now an int and positions/world level are bytes on disk;
    # negative coordinates and NaN are no longer representable wire values.
    foreach ($invalidItems in @(
        @(@{ Prefab = 'Mod_Item'; Stack = 0 }),
        @(@{ Prefab = '' }),
        @(@{ Prefab = 'Mod_One'; X = 4; Y = 7 }, @{ Prefab = 'Mod_Two'; X = 4; Y = 7 }))) {
        [byte[]]$invalidInventory = New-InventoryPayload $invalidItems
        $validateInventory = $plugin.GetType('ServerManager.ValheimPlayerProfileCodec').GetMethod('ValidateInventorySnapshot', $allInstance)
        Assert-Throws { $validateInventory.Invoke($modFixture.Profiles, [object[]]@($invalidInventory)) }
        [byte[]]$invalidRaw = New-ProfilePayload 'ModHero' 303 'invalid-mod-item' -InventoryItems $invalidItems
        Assert-Throws { $extract.Invoke($modFixture.Profiles, [object[]]@($modIdentity, $invalidRaw)) }
    }

    # Activity formatting must retain extreme raw values and unknown numeric
    # IDs without int32 overflow or a fallback to an unrelated vanilla skill.
    $activityType = $plugin.GetType('ServerManager.PlayerLogging.PlayerActivityRuntime', $true)
    $integerSkillLevel = $activityType.GetMethod('IntegerSkillLevel', $allStatic)
    $getSkillName = $activityType.GetMethod('GetSkillName', $allStatic)
    foreach ($rawLevel in @([single]::MaxValue, [single]::MinValue, [single]-1.5)) {
        $activitySkill = New-Instance 'CharacterSemanticSkillState' @([int]1980891425, $rawLevel, [single]0)
        $floored = $integerSkillLevel.Invoke($null, [object[]]@($activitySkill))
        Assert-True ($floored -is [double] -and $floored -eq [Math]::Floor([double]$rawLevel) -and
            -not [double]::IsInfinity($floored) -and -not [double]::IsNaN($floored)) 'Activity logging overflowed or clamped a finite raw mod skill level.'
    }
    foreach ($customId in @(1980891425, [int]::MinValue)) {
        Assert-True ($getSkillName.Invoke($null, [object[]]@($customId)) -ceq
            $customId.ToString([Globalization.CultureInfo]::InvariantCulture)) 'Activity logging lost an unknown numeric skill ID.'
    }

    # Optional operator-provided examples are read only. All repository work
    # uses temporary copies; default suite execution has no external fixtures.
    $sampleIndex = 0
    foreach ($sourcePath in $NativeSamplePaths) {
        [byte[]]$sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
        $sampleFixture = New-DirectFixture ('sample-' + $sampleIndex++)
        $sourceFilename = [IO.Path]::GetFileName($sourcePath)
        Assert-True ($sourceFilename -cmatch '^Steam_(\d+)_') 'Native sample filename does not expose a Steam account.'
        $sampleAccountDirectory = Join-Path $sampleFixture.Layout.RootDirectory $Matches[1]
        [IO.Directory]::CreateDirectory($sampleAccountDirectory) | Out-Null
        $samplePath = Join-Path $sampleAccountDirectory $sourceFilename
        [IO.File]::WriteAllBytes($samplePath, $sourceBytes)
        Validate-DirectStorage $sampleFixture
        $sampleIdentities = Invoke-Hidden $sampleFixture.Repository 'GetAdminStoredIdentities' @($sampleFixture.Keys)
        Assert-True ($sampleIdentities.Count -eq 1) 'A direct native sample did not enumerate exactly once.'
        $sampleIdentity = $sampleIdentities[0]
        $sampleKey = $sampleFixture.Keys.DeriveStorageKey($sampleIdentity)
        $sampleStored = Invoke-Hidden $sampleFixture.Repository 'Load' @($sampleIdentity, $sampleKey)
        Assert-True ($sampleStored.Envelope.CharacterName -ceq $sampleIdentity.CharacterName -and
            (Test-Bytes (New-NativeFch $sampleStored.Envelope.GetPayloadCopy()) $sourceBytes) -and
            (Test-Bytes ([IO.File]::ReadAllBytes($sourcePath)) $sourceBytes)) 'Direct native sample changed display metadata, native payload, or source bytes.'
        Write-Host ('Native sample storage mapping and byte-preserving load passed: ' + [IO.Path]::GetFileName($sourcePath))
        $sampleRecords = Invoke-Hidden $sampleFixture.Service 'GetAdminCharacters'
        Assert-True ($sampleRecords.Count -eq 1 -and
            (Get-Hidden $sampleRecords[0] 'CharacterName') -ceq $sampleIdentity.CharacterName) 'Native sample lost its display name during full-profile validation.'
        $sampleOpened = Invoke-Hidden $sampleFixture.Service 'OpenOrCreateLocalHostSession' @($sampleIdentity)
        Assert-True (-not $sampleOpened.PendingInitialCommit -and $sampleOpened.Snapshot.CharacterName -ceq $sampleIdentity.CharacterName -and
            (Test-Bytes (New-NativeFch $sampleOpened.Snapshot.GetPayloadCopy()) $sourceBytes)) 'Native sample could not enter a full character session with its original raw bytes.'
        Invoke-Hidden $sampleFixture.Service 'CloseLocalHostSession' @($sampleOpened.Snapshot.SessionId) | Out-Null
        Write-Host ('Native sample full-profile validation and byte-identical session passed: ' + [IO.Path]::GetFileName($sourcePath))
        Assert-True ((Test-Bytes ([IO.File]::ReadAllBytes($sourcePath)) $sourceBytes) -and
            (Test-Bytes ([IO.File]::ReadAllBytes($samplePath)) $sourceBytes)) 'Sample validation changed original or temporary native bytes.'
    }

    Write-Host "Direct native character storage smoke passed ($script:assertions assertions; real storage, startup validation, checkpoints and restore)."
}
finally {
    foreach ($fixture in $fixtures) { $fixture.Service.Dispose() }
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($fixtureAssemblyResolver)
    $resolvedRoot = [IO.Path]::GetFullPath($directRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-True ($resolvedRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot).StartsWith('smdirect-', [StringComparison]::Ordinal)) 'Unsafe test cleanup target.'
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
