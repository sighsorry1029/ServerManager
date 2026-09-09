param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Configuration = 'Release',
    [string]$YamlDotNetPath = (Join-Path $env:USERPROFILE '.nuget\packages\yamldotnet\18.1.0\lib\net47\YamlDotNet.dll'),
    [string]$CronosPath = (Join-Path $env:USERPROFILE '.nuget\packages\cronos\0.13.0\lib\net45\Cronos.dll'),
    [string]$HarnessSource = (Join-Path $PSScriptRoot 'ServerScheduleHarness.cs')
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdk = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
foreach ($required in @($compiler, $YamlDotNetPath, $CronosPath, (Join-Path $framework 'mscorlib.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Schedule test prerequisite missing: $required" }
}
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-Schedule-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$ownedProcess = $null
try {
    $harness = Join-Path $testDirectory 'ServerScheduleHarness.exe'
    $arguments = @(
        $compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable',
        '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $framework 'mscorlib.dll')),
        ("/reference:" + (Join-Path $framework 'System.dll')),
        ("/reference:" + (Join-Path $framework 'System.Core.dll')),
        "/reference:$YamlDotNetPath", "/reference:$CronosPath",
        (Join-Path $ProjectRoot 'Scheduling\ServerSchedule.cs'),
        (Join-Path $ProjectRoot 'Scheduling\ServerScheduleJournal.cs'),
        $HarnessSource
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Schedule harness compilation failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath $YamlDotNetPath -Destination (Join-Path $testDirectory 'YamlDotNet.dll')
    Copy-Item -LiteralPath $CronosPath -Destination (Join-Path $testDirectory 'Cronos.dll')
    $stdout = Join-Path $testDirectory 'stdout.txt'
    $stderr = Join-Path $testDirectory 'stderr.txt'
    $ownedProcess = Start-Process -FilePath $harness -WorkingDirectory $testDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $ownedProcess.Handle
    $finished = $ownedProcess.WaitForExit(20000)
    if (-not $finished) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -TotalCount 100 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -TotalCount 100 }
    if (-not $finished) { throw 'Schedule probe timed out; only its owned helper was stopped.' }
    if ($ownedProcess.ExitCode -ne 0) { throw "Schedule smoke failed ($($ownedProcess.ExitCode))." }
}
finally {
    if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolved.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolved) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolved) -notmatch '^ServerManager-Schedule-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected schedule test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
