param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$steamPath = Join-Path $GamePath 'valheim_Data\Managed\com.rlabrecque.steamworks.net.dll'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-OptionalLobby-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$ownedProcess = $null
try {
    $harness = Join-Path $testDirectory 'OptionalModLobbyQuerySmoke.exe'
    $compileArgs = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable', '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $framework 'mscorlib.dll')),
        ("/reference:" + (Join-Path $framework 'System.dll')),
        ("/reference:" + (Join-Path $framework 'System.Core.dll')),
        "/reference:$steamPath",
        (Join-Path $ProjectRoot 'Integrity\IntegrityModels.cs'),
        (Join-Path $ProjectRoot 'Integrity\OptionalModCatalog.cs'),
        (Join-Path $ProjectRoot 'Networking\OptionalModLobbyQuery.cs'),
        (Join-Path $ProjectRoot 'Tests\OptionalModLobbyQuerySmoke.cs'))
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Optional lobby harness compilation failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath $steamPath -Destination (Join-Path $testDirectory 'com.rlabrecque.steamworks.net.dll')
    $stdout = Join-Path $testDirectory 'stdout.txt'
    $stderr = Join-Path $testDirectory 'stderr.txt'
    $ownedProcess = Start-Process -FilePath $harness -WorkingDirectory $testDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $ownedProcess.Handle
    $finished = $ownedProcess.WaitForExit(30000)
    if (-not $finished) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -TotalCount 100 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -TotalCount 100 }
    if (-not $finished) { throw 'Optional lobby test timed out; only its owned helper was stopped.' }
    if ($ownedProcess.ExitCode -ne 0) { throw "Optional lobby test failed ($($ownedProcess.ExitCode))." }
}
finally {
    if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolved.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolved) -ne $temporaryRoot -or (Split-Path -Leaf $resolved) -notmatch '^ServerManager-OptionalLobby-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected optional lobby test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
