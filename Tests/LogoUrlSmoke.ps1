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
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing logo test prerequisite: $required" }
}
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-LogoUrl-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$ownedProcess = $null
try {
    $harness = Join-Path $testDirectory 'LogoUrlSmoke.exe'
    $compileArgs = @(
        $compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable',
        '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $frameworkPath 'mscorlib.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Core.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Net.Http.dll')),
        (Join-Path $ProjectRoot 'Tests\LogoUrlSmoke.cs')
    )
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Logo URL harness compilation failed ($LASTEXITCODE)." }
    $stdout = Join-Path $testDirectory 'stdout.txt'
    $stderr = Join-Path $testDirectory 'stderr.txt'
    $dataRoot = Join-Path $testDirectory 'data'
    $arguments = '"' + $pluginPath + '" "' + $GamePath + '" "' + $dataRoot + '"'
    $ownedProcess = Start-Process -FilePath $harness -ArgumentList $arguments -WorkingDirectory $testDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $ownedProcess.Handle
    $finished = $ownedProcess.WaitForExit(60000)
    if (-not $finished) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -TotalCount 100 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -TotalCount 100 }
    if (-not $finished) { throw 'Logo URL smoke timed out; only its owned test process was stopped.' }
    if ($ownedProcess.ExitCode -ne 0) { throw "Logo URL smoke failed ($($ownedProcess.ExitCode))." }
}
finally {
    if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolved.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolved) -ne $temporaryRoot -or (Split-Path -Leaf $resolved) -notmatch '^ServerManager-LogoUrl-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected logo test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
