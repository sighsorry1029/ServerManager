param(
    [string]$Configuration = 'Debug',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$pluginPath = Join-Path $ProjectRoot "bin\$Configuration\ServerManager.dll"
$cecilPath = Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-DependencyManifest-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$ownedProcess = $null
try {
    $harness = Join-Path $testDirectory 'DependencyManifestSmoke.exe'
    $compileArgs = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable', '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $frameworkPath 'mscorlib.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Core.dll')),
        ("/reference:" + $cecilPath), (Join-Path $ProjectRoot 'Tests\DependencyManifestSmoke.cs'))
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Dependency harness compilation failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath $cecilPath -Destination (Join-Path $testDirectory 'Mono.Cecil.dll')
    $stdout = Join-Path $testDirectory 'stdout.txt'
    $stderr = Join-Path $testDirectory 'stderr.txt'
    $arguments = '"' + $pluginPath + '" "' + $GamePath + '" "' + (Join-Path $testDirectory 'data') + '"'
    $ownedProcess = Start-Process -FilePath $harness -ArgumentList $arguments -WorkingDirectory $testDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $ownedProcess.Handle
    $finished = $ownedProcess.WaitForExit(45000)
    if (-not $finished) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -TotalCount 100 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -TotalCount 100 }
    if (-not $finished) { throw 'Dependency test timed out; only its owned process was stopped.' }
    if ($ownedProcess.ExitCode -ne 0) { throw "Dependency test failed ($($ownedProcess.ExitCode))." }
}
finally {
    if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolved.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolved) -ne $temporaryRoot -or (Split-Path -Leaf $resolved) -notmatch '^ServerManager-DependencyManifest-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected dependency test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
