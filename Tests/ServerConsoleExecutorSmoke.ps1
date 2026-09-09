param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-ConsoleExecutor-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$ownedProcess = $null
try {
    $harness = Join-Path $testDirectory 'ServerConsoleExecutorSmoke.exe'
    $compileArgs = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable', '/target:exe', "/out:$harness")
    foreach ($name in @('mscorlib.dll', 'System.dll', 'System.Core.dll')) {
        $compileArgs += '/reference:' + (Join-Path $frameworkPath $name)
    }
    $compileArgs += @((Join-Path $ProjectRoot 'Commands\ServerConsoleExecutor.cs'),
        (Join-Path $ProjectRoot 'Discord\DiscordRconCapture.cs'),
        (Join-Path $ProjectRoot 'Tests\ServerConsoleExecutorSmoke.cs'))
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Shared console harness compilation failed ($LASTEXITCODE)." }
    $stdout = Join-Path $testDirectory 'stdout.txt'
    $stderr = Join-Path $testDirectory 'stderr.txt'
    $ownedProcess = Start-Process -FilePath $harness -WorkingDirectory $testDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $ownedProcess.Handle
    $finished = $ownedProcess.WaitForExit(20000)
    if (-not $finished) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -TotalCount 100 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -TotalCount 100 }
    if (-not $finished) { throw 'Shared console probe timed out; only its owned helper was stopped.' }
    if ($ownedProcess.ExitCode -ne 0) { throw "Shared console smoke failed ($($ownedProcess.ExitCode))." }
}
finally {
    if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolvedTestDirectory.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolvedTestDirectory) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolvedTestDirectory) -notmatch '^ServerManager-ConsoleExecutor-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected shared console test directory.'
    }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}
