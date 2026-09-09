param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
$frameworkPath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required for the offline compiler.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
foreach ($required in @($pluginPath, $compiler, (Join-Path $frameworkPath 'mscorlib.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing optional publication test prerequisite: $required" }
}
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Find-Method($type, [string]$name) {
    $method = $type.Methods | Where-Object Name -eq $name | Select-Object -First 1
    Assert-True ($null -ne $method) "Missing optional publication integration method: $name"
    return $method
}
function Calls($method, [string]$name) {
    return @($method.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $name
    })
}
function Type-Methods($type) {
    $type.Methods
    foreach ($nested in $type.NestedTypes) { Type-Methods $nested }
}

# Inspect actual game API signatures and compiled lifecycle wiring without ever
# initializing Steam or loading a world. Test delegates below own all side effects.
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
$steam = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managedRoot 'com.rlabrecque.steamworks.net.dll'))
try {
    $gameServer = $steam.MainModule.Types | Where-Object FullName -eq 'Steamworks.SteamGameServer'
    $set = Find-Method $gameServer 'SetKeyValue'
    Assert-True ($set.IsStatic -and $set.ReturnType.FullName -eq 'System.Void' -and
        $set.Parameters.Count -eq 2 -and $set.Parameters[0].ParameterType.FullName -eq 'System.String' -and
        $set.Parameters[1].ParameterType.FullName -eq 'System.String') 'Installed Steam SetKeyValue signature changed.'
    $api = $steam.MainModule.Types | Where-Object FullName -eq 'Steamworks.GameServer'
    $pipe = Find-Method $api 'GetHSteamPipe'
    Assert-True ($pipe.IsStatic -and $pipe.Parameters.Count -eq 0 -and
        $pipe.ReturnType.FullName -eq 'Steamworks.HSteamPipe') 'Installed GameServer readiness signature changed.'
    $matchmaking = $steam.MainModule.Types | Where-Object FullName -eq 'Steamworks.SteamMatchmaking'
    $lobbySet = Find-Method $matchmaking 'SetLobbyData'
    Assert-True ($lobbySet.IsStatic -and $lobbySet.ReturnType.FullName -eq 'System.Boolean' -and
        $lobbySet.Parameters.Count -eq 3 -and $lobbySet.Parameters[0].ParameterType.FullName -eq 'Steamworks.CSteamID' -and
        $lobbySet.Parameters[1].ParameterType.FullName -eq 'System.String' -and
        $lobbySet.Parameters[2].ParameterType.FullName -eq 'System.String') 'Installed SetLobbyData signature changed.'
    $owner = Find-Method $matchmaking 'GetLobbyOwner'
    Assert-True ($owner.IsStatic -and $owner.ReturnType.FullName -eq 'Steamworks.CSteamID' -and
        $owner.Parameters.Count -eq 1 -and $owner.Parameters[0].ParameterType.FullName -eq 'Steamworks.CSteamID') 'Installed lobby ownership signature changed.'
    $constants = $steam.MainModule.Types | Where-Object FullName -eq 'Steamworks.Constants'
    $codec = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.OptionalModCatalog'
    $keyLimit = ($constants.Fields | Where-Object Name -eq 'k_nMaxLobbyKeyLength').Constant
    $valueLimit = ($constants.Fields | Where-Object Name -eq 'k_cubChatMetadataMax').Constant
    $headerKey = ($codec.Fields | Where-Object Name -eq 'HeaderKey').Constant
    $codecLimit = ($codec.Fields | Where-Object Name -eq 'MaximumRuleValueBytes').Constant
    Assert-True ($headerKey.Length -lt $keyLimit -and $codecLimit -lt $valueLimit) 'Codec buffers no longer fit installed Steam lobby metadata limits.'
    $runtime = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerManagerRuntime'
    foreach ($name in @('Initialize', 'CleanupFailedInitialization', 'Shutdown', 'BeforeNetworkShutdown', 'AfterNetworkShutdown')) {
        Assert-True ((Calls (Find-Method $runtime $name) 'StopOptionalModPublication').Count -eq 1) "$name lost catalog lifecycle cleanup."
    }
    Assert-True ((Calls (Find-Method $runtime 'BeforeNetworkStart') 'BeginOptionalModPublication').Count -eq 1) 'Network startup lost catalog binding.'
    Assert-True ((Calls (Find-Method $runtime 'Tick') 'TickOptionalModPublication').Count -eq 1) 'Main-thread Tick lost catalog publication.'
    Assert-True ((Calls (Find-Method $runtime 'BeginOptionalModPublication') 'Begin').Count -eq 2) 'Network startup must bind both managed publishers.'
    Assert-True ((Calls (Find-Method $runtime 'StopOptionalModPublication') 'Stop').Count -eq 2) 'Network/plugin shutdown must stop both publishers without native cleanup.'
    $tick = Find-Method $runtime 'TickOptionalModPublication'
    foreach ($name in @('IsServer', 'IsDedicated', 'get_CurrentPolicy', 'GetTimestamp', 'Tick')) {
        Assert-True ((Calls $tick $name).Count -eq 1) "Publication lost runtime guard/snapshot call: $name"
    }
    $service = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerIntegrityService'
    $current = Find-Method $service 'get_CurrentPolicy'
    Assert-True ((Calls $current 'get_Current').Count -eq 1) 'Publication no longer consumes the actual active policy store snapshot.'
    $allCalls = @($current.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
    Assert-True ($allCalls.Count -eq 1) 'CurrentPolicy unexpectedly rescans or mutates instead of returning the active snapshot.'
    $transport = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.OptionalModLobbyPublicationTransport'
    Assert-True ($null -ne $transport) 'Dedicated and listen publication transports were not separated.'
    $transportMethods = @(Type-Methods $transport)
    foreach ($name in @('get_Initialized', 'GetSteamServerLobby', 'GetSteamID', 'GetLobbyOwner', 'SetLobbyData')) {
        $matches = @($transportMethods | ForEach-Object { Calls $_ $name })
        Assert-True ($matches.Count -eq 1) "Lobby transport lost verified API/lifecycle seam: $name"
    }
    $unexpected = @($transportMethods | ForEach-Object { $_.Body.Instructions } | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        ($_.Operand.DeclaringType.FullName -match '^Steamworks\.(GameServer|SteamGameServer|SteamAPI)$' -or
        $_.Operand.Name -in @('CreateLobby', 'JoinLobby', 'LeaveLobby', 'RequestLobbyList', 'SetLobbyOwner', 'SetLobbyGameServer'))
    })
    Assert-True ($unexpected.Count -eq 0) 'Lobby publication must never initialize Steam, touch GameServer, or change lobby discovery/ownership/joining.'
    $publicationSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'Networking\OptionalModPublication.cs'))
    Assert-True (-not [regex]::IsMatch($publicationSource, 'ClearAllKeyValues|SetGameTags|SetGameData|Task\.Run|\.Scan\(')) 'Catalog publishing introduced unrelated metadata mutation or worker/folder scanning.'
    Assert-True ($publicationSource.Contains('dedicated ? _optionalModPublication : _optionalModLobbyPublication')) 'Runtime no longer chooses exactly one publication transport for its server role.'
    Write-Host 'Optional catalog compiled lifecycle, separate transports, lobby bounds and actual Steam API signature checks passed.'
}
finally { $definition.Dispose(); $steam.Dispose() }

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-OptionalPublication-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $harness = Join-Path $testDirectory 'OptionalModPublicationSmoke.exe'
    $compileArgs = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable',
        '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $frameworkPath 'mscorlib.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Core.dll')),
        (Join-Path $projectRoot 'Tests\OptionalModPublicationSmoke.cs'))
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Optional publication harness compilation failed ($LASTEXITCODE)." }
    & $harness $pluginPath $GamePath (Join-Path $testDirectory 'policy')
    if ($LASTEXITCODE -ne 0) { throw "Optional publication harness failed ($LASTEXITCODE)." }
}
finally {
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if ((Split-Path -Parent $resolved) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolved) -notmatch '^ServerManager-OptionalPublication-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected optional publication test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
