param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$YamlDotNetPath = (Join-Path $env:USERPROFILE '.nuget\packages\yamldotnet\18.1.0\lib\net47\YamlDotNet.dll'),
    [string]$CronosPath = (Join-Path $env:USERPROFILE '.nuget\packages\cronos\0.13.0\lib\net45\Cronos.dll')
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdk = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
foreach ($required in @($compiler, $YamlDotNetPath, $CronosPath, (Join-Path $framework 'mscorlib.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Schedule runtime test prerequisite missing: $required" }
}
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-ScheduleRuntime-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$ownedProcess = $null
try {
    $harness = Join-Path $testDirectory 'ServerScheduleRuntimeSmoke.exe'
    $arguments = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable', '/target:exe', "/out:$harness")
    foreach ($name in @('mscorlib.dll', 'System.dll', 'System.Core.dll')) {
        $arguments += '/reference:' + (Join-Path $framework $name)
    }
    $arguments += @("/reference:$YamlDotNetPath", "/reference:$CronosPath",
        (Join-Path $ProjectRoot 'Scheduling\ServerSchedule.cs'),
        (Join-Path $ProjectRoot 'Scheduling\ServerScheduleJournal.cs'),
        (Join-Path $ProjectRoot 'Scheduling\ServerScheduleRuntime.cs'),
        (Join-Path $ProjectRoot 'Tests\ServerScheduleRuntimeSmoke.cs'))
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Schedule runtime harness compilation failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath $YamlDotNetPath -Destination (Join-Path $testDirectory 'YamlDotNet.dll')
    Copy-Item -LiteralPath $CronosPath -Destination (Join-Path $testDirectory 'Cronos.dll')
    $reparseTarget = Join-Path $testDirectory 'reparse-target'
    $reparseRoot = Join-Path $testDirectory 'reparse-root'
    New-Item -ItemType Directory -Path $reparseTarget | Out-Null
    try { New-Item -ItemType Junction -Path $reparseRoot -Target $reparseTarget -ErrorAction Stop | Out-Null }
    catch { Write-Warning 'Optional directory reparse fixture unavailable; file byte/UTF8 safety still runs.' }
    $stdout = Join-Path $testDirectory 'stdout.txt'
    $stderr = Join-Path $testDirectory 'stderr.txt'
    $ownedProcess = Start-Process -FilePath $harness -WorkingDirectory $testDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $ownedProcess.Handle
    $finished = $ownedProcess.WaitForExit(60000)
    if (-not $finished) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -TotalCount 100 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -TotalCount 100 }
    if (-not $finished) { throw 'Schedule runtime probe timed out; only its owned helper was stopped.' }
    if ($ownedProcess.ExitCode -ne 0) { throw "Schedule runtime smoke failed ($($ownedProcess.ExitCode))." }
}
finally {
    if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolved.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolved) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolved) -notmatch '^ServerManager-ScheduleRuntime-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected schedule runtime test directory.'
    }
    # Both junction and its target are exact children of this validated owned
    # fixture. Remove the junction itself first, without traversing its target.
    $ownedJunction = Join-Path $resolved 'reparse-root'
    if (Test-Path -LiteralPath $ownedJunction) { Remove-Item -LiteralPath $ownedJunction -Force }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
