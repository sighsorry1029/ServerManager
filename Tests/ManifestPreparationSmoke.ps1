param(
    [string]$Configuration = 'Debug',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required for the offline C# compiler.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$pluginPath = Join-Path $ProjectRoot "bin\$Configuration\ServerManager.dll"
foreach ($required in @($compiler, $pluginPath, (Join-Path $frameworkPath 'mscorlib.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing manifest test prerequisite: $required" }
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Find-Method($type, [string]$name) {
    $method = @($type.Methods | Where-Object Name -eq $name | Select-Object -First 1)
    Assert-True ($method.Count -eq 1) "Missing manifest integration method: $name"
    return $method[0]
}
function Find-Call($method, [string]$name) {
    return $method.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $name
    } | Select-Object -First 1
}
function Assert-Calls($method, [string]$name) {
    Assert-True ($null -ne (Find-Call $method $name)) "$($method.Name) no longer calls $name."
}
function Get-NestedTypes($type) {
    $type
    foreach ($nested in $type.NestedTypes) { Get-NestedTypes $nested }
}

# Read IL only: none of these lifecycle/RPC methods execute outside Unity.
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
try {
    $runtime = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerManagerRuntime'
    $scanner = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.PluginManifestScanner'
    Assert-True ($null -ne $runtime -and $null -ne $scanner) 'Missing manifest runtime/scanner types.'
    $networkStart = Find-Method $runtime 'BeforeNetworkStart'
    Assert-Calls $networkStart 'BeginClientManifestPreparation'
    Assert-Calls $networkStart 'IsServer'
    $startPatch = $definition.MainModule.Types | Where-Object Name -eq 'SteamworksBackendStartupPatch'
    Assert-Calls (Find-Method $startPatch 'Prefix') 'BeforeNetworkStart'
    Assert-Calls (Find-Method $runtime 'BeforeNewConnection') 'CreateClientConnection'
    Assert-Calls (Find-Method $runtime 'GetOrCreateClient') 'CreateClientConnection'
    $create = Find-Method $runtime 'CreateClientConnection'
    Assert-Calls $create 'CancelClientManifestPreparation'
    Assert-Calls $create 'BeginCurrent'
    Assert-Calls $create 'set_Network'
    $clientConnection = $runtime.NestedTypes | Where-Object Name -eq 'ClientConnection'
    Assert-True (@($clientConnection.Properties | Where-Object { $_.Name -eq 'Network' -and $_.PropertyType.FullName -eq 'ZNet' }).Count -eq 1) 'Network owner is missing from ClientConnection or was added to an unrelated nested type.'
    $pendingClear = Find-Method $runtime 'CancelPendingManifestPreparation'
    $clientClear = Find-Method $runtime 'CancelClientManifestPreparation'
    Assert-Calls $pendingClear 'Dispose'
    Assert-Calls $clientClear 'Dispose'
    foreach ($name in @('Shutdown', 'BeforeNetworkShutdown', 'AfterNetworkShutdown', 'CleanupPeer', 'FailClient')) {
        Assert-Calls (Find-Method $runtime $name) 'CancelClientManifestPreparation'
    }
    foreach ($name in @('Shutdown', 'BeforeNetworkShutdown', 'AfterNetworkShutdown', 'BeginClientManifestPreparation')) {
        Assert-Calls (Find-Method $runtime $name) 'CancelPendingManifestPreparation'
    }
    $shutdownPatch = $definition.MainModule.Types | Where-Object Name -eq 'NetworkShutdownCleanupPatch'
    Assert-Calls (Find-Method $shutdownPatch 'Prefix') 'BeforeNetworkShutdown'
    Assert-Calls (Find-Method $shutdownPatch 'Finalizer') 'AfterNetworkShutdown'
    $afterShutdown = Find-Method $runtime 'AfterNetworkShutdown'
    $shutdownExceptionUse = $afterShutdown.Body.Instructions | Where-Object {
        $_.OpCode.Code.ToString() -eq 'Ldarg_2' -or
        ($_.OpCode.Code.ToString() -in @('Ldarg', 'Ldarg_S') -and $_.Operand.Name -eq 'shutdownException')
    } | Select-Object -First 1
    Assert-True ($null -ne $shutdownExceptionUse -and
        (Find-Call $afterShutdown 'CancelPendingManifestPreparation').Offset -lt $shutdownExceptionUse.Offset -and
        (Find-Call $afterShutdown 'CancelClientManifestPreparation').Offset -lt $shutdownExceptionUse.Offset) 'Failed StopAll can return before cancelling its pending or active manifest work.'

    $challenge = Find-Method $runtime 'HandleClientChallenge'
    Assert-True ($null -eq (Find-Call $challenge 'BuildCurrent')) 'Challenge handling returned to synchronous file hashing.'
    Assert-Calls $challenge 'get_MaximumManifestBytes'
    Assert-Calls $challenge 'set_ManifestResponseLimits'
    Assert-Calls $challenge 'CancelClientManifestPreparation'
    Assert-Calls $challenge 'SendClientManifestResponse'
    $process = Find-Method $runtime 'ProcessClientManifestPreparation'
    foreach ($name in @('get_Failed', 'get_ChallengeReceived', 'get_ManifestResponseSent', 'get_DeadlineTimestamp', 'GetTimestamp', 'IsConnected', 'TryGetResult', 'TryEncode', 'SendClientManifestResponse')) {
        Assert-Calls $process $name
    }
    $take = Find-Call $process 'TryGetResult'
    Assert-True ((Find-Call $process 'get_DeadlineTimestamp').Offset -lt $take.Offset) 'Completed hash is consumed before the handshake deadline guard.'
    Assert-True ((Find-Call $process 'IsConnected').Offset -lt $take.Offset) 'Completed hash is consumed before checking the connected socket.'
    Assert-True ($take.Offset -lt (Find-Call $process 'MatchesCurrentPlugins').Offset -and
        (Find-Call $process 'MatchesCurrentPlugins').Offset -lt (Find-Call $process 'TryEncode').Offset) 'Completed manifest skips the main-thread registry freshness guard before encoding.'
    $tick = Find-Method $runtime 'Tick'
    Assert-True ((Find-Call $tick 'get_DeadlineTimestamp').Offset -lt (Find-Call $tick 'ProcessClientManifestPreparation').Offset) 'Tick processes hashes before its existing handshake timeout.'
    $send = Find-Method $runtime 'SendClientManifestResponse'
    Assert-True ((Find-Call $send 'SendProtocolOrThrow').Offset -lt (Find-Call $send 'set_ManifestResponseSent').Offset) 'Manifest response is marked sent before transport success.'
    $ack = Find-Method $runtime 'HandleClientManifestAccepted'
    Assert-True ((Find-Call $ack 'get_ManifestResponseSent').Offset -lt (Find-Call $ack 'set_ManifestAccepted').Offset) 'A server ACK can bypass the outstanding asynchronous preparation.'
    foreach ($method in @($challenge, $process, $send, $networkStart, $create, $pendingClear, $clientClear)) {
        $blockingCalls = @($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName.StartsWith('System.Threading.Tasks.Task') -and
            $_.Operand.Name -in @('Wait', 'WaitAll', 'WaitAny', 'get_Result', 'GetAwaiter')
        })
        Assert-True ($blockingCalls.Count -eq 0) "$($method.Name) blocks the main thread on a worker task."
    }

    $record = $scanner.NestedTypes | Where-Object Name -eq 'PluginRecord'
    Assert-True ($null -ne $record -and @($record.Fields | Where-Object { -not $_.IsInitOnly -or $_.FieldType.FullName -ne 'System.String' }).Count -eq 0) 'Worker plugin records retain mutable or Unity/BepInEx object state.'
    $build = Find-Method $scanner 'BuildSnapshot'
    $hash = Find-Method $scanner 'TryHashPluginFile'
    Assert-Calls $hash 'ThrowIfCancellationRequested'
    Assert-Calls $hash 'Read'
    Assert-Calls $hash 'TransformBlock'
    Assert-Calls $hash 'TransformFinalBlock'
    Assert-True ($null -eq (Find-Call $hash 'ReadAllBytes')) 'The worker now allocates entire DLLs instead of streaming bounded chunks.'
    foreach ($method in @($build, $hash)) {
        $unsafeCalls = @($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -match '^(UnityEngine\.|BepInEx\.|ZRpc$|ZNet$|ServerManager\.ServerManagerRuntime$)'
        })
        Assert-True ($unsafeCalls.Count -eq 0) "$($method.Name) touches live plugin/Unity/RPC state on the worker."
    }
    $preparation = $scanner.NestedTypes | Where-Object Name -eq 'Preparation'
    Assert-Calls (Find-Method $preparation 'Dispose') 'Cancel'
    $tryResult = Find-Method $preparation 'TryGetResult'
    Assert-True ((Find-Call $tryResult 'get_IsCompleted').Offset -lt (Find-Call $tryResult 'GetResult').Offset) 'TryGetResult waits on an incomplete task.'
    $asyncMethods = @(Get-NestedTypes $scanner | ForEach-Object { $_.Methods } | Where-Object { $_.HasBody -and $null -ne (Find-Call $_ 'WaitAsync') })
    Assert-True ($asyncMethods.Count -eq 1) 'Asynchronous manifest workers lost their shared serial I/O gate.'
    Assert-Calls $asyncMethods[0] 'Release'
    Assert-Calls $asyncMethods[0] 'BuildSnapshot'

    $runtimeSource = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'Networking\ServerManagerRuntime.cs'))
    Assert-True ($runtimeSource.Contains('if (_initialized && !_shuttingDown && !znet.IsServer())')) 'Early prewarm lost its initialized client-only guard.'
    Assert-True ($runtimeSource.Contains('ReferenceEquals(_manifestPreparationNetwork, network)')) 'Pending preparations can cross network instances.'
    Assert-True ($runtimeSource.Contains('if (!ReferenceEquals(_client, session) || session.Failed ||')) 'Stale asynchronous completion can affect a replaced client session.'
    Assert-True ($runtimeSource.Contains('ReferenceEquals(_client.Network, znet)')) 'Shutdown cancellation lost the active network owner guard.'
    Assert-True ($runtimeSource.Contains('if (_client?.Network != null && !ReferenceEquals(_client.Network, znet)) return;')) 'Delayed teardown from an older network can clear the new client session.'
    $scannerSource = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'Integrity\PluginManifestScanner.cs'))
    Assert-True (-not [regex]::IsMatch($scannerSource, 'LastWriteTime|CreationTime|WriteAllBytes|WriteAllText|Assembly\.Load')) 'Preparation introduced metadata-cache reuse, disk cache writes, or assembly reloading.'
    Write-Host 'Manifest preparation runtime wiring/worker boundary checks passed.'
}
finally { $definition.Dispose() }

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-ManifestPreparation-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$ownedProcess = $null
try {
    $harness = Join-Path $testDirectory 'ManifestPreparationSmoke.exe'
    $compileArgs = @(
        $compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable',
        '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $frameworkPath 'mscorlib.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Core.dll')),
        (Join-Path $ProjectRoot 'Tests\ManifestPreparationSmoke.cs')
    )
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Manifest preparation harness compilation failed ($LASTEXITCODE)." }
    $stdout = Join-Path $testDirectory 'stdout.txt'
    $stderr = Join-Path $testDirectory 'stderr.txt'
    $dataRoot = Join-Path $testDirectory 'data'
    $arguments = '"' + $pluginPath + '" "' + $GamePath + '" "' + $dataRoot + '"'
    $ownedProcess = Start-Process -FilePath $harness -ArgumentList $arguments -WorkingDirectory $testDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $ownedProcess.Handle
    $finished = $ownedProcess.WaitForExit(45000)
    if (-not $finished) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -TotalCount 100 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -TotalCount 100 }
    if (-not $finished) { throw 'Manifest preparation smoke timed out; only its owned test process was stopped.' }
    if ($ownedProcess.ExitCode -ne 0) { throw "Manifest preparation smoke failed ($($ownedProcess.ExitCode))." }
}
finally {
    if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolved.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolved) -ne $temporaryRoot -or (Split-Path -Leaf $resolved) -notmatch '^ServerManager-ManifestPreparation-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected manifest test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
