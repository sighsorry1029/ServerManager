param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$Configuration = 'Debug',
    [ValidateRange(10, 60)][int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
$embedRoot = Join-Path $GamePath 'MonoBleedingEdge\EmbedRuntime'
$configRoot = Join-Path $GamePath 'MonoBleedingEdge\etc'
$pluginPath = Join-Path $ProjectRoot "bin\$Configuration\ServerManager.dll"
$referenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
foreach ($required in @($compiler, $pluginPath, (Join-Path $managedRoot 'mscorlib.dll'), (Join-Path $embedRoot 'mono-2.0-bdwgc.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Mono probe prerequisite missing: $required" }
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testDirectory = Join-Path $temporaryRoot ('ServerManager-Mono-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$ownedProcess = $null
try {
    $hostPath = Join-Path $testDirectory 'DiscordMonoHost.exe'
    $probePath = Join-Path $testDirectory 'DiscordMonoProbe.dll'
    $common = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0')
    $references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Net.Http.dll') | ForEach-Object { '/reference:' + (Join-Path $referenceRoot $_) }
    & dotnet @common @references '/target:exe' '/platform:x64' "/out:$hostPath" (Join-Path $ProjectRoot 'Tests\DiscordMonoHost.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Mono helper compilation failed.' }
    & dotnet @common @references '/target:library' "/out:$probePath" (Join-Path $ProjectRoot 'Tests\DiscordMonoProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Mono probe compilation failed.' }
    # Deliberately copy only the bundled plugin, not Newtonsoft/YamlDotNet or other output dependencies.
    $isolatedPlugin = Join-Path $testDirectory 'ServerManager.dll'
    Copy-Item -LiteralPath $pluginPath -Destination $isolatedPlugin
    $stdout = Join-Path $testDirectory 'stdout.txt'
    $stderr = Join-Path $testDirectory 'stderr.txt'
    $arguments = @($embedRoot, $managedRoot, $configRoot, $probePath, $isolatedPlugin) | ForEach-Object { '"' + $_ + '"' }
    $ownedProcess = Start-Process -FilePath $hostPath -ArgumentList $arguments -WorkingDirectory $testDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    # Windows PowerShell 5.1 can lose ExitCode for a short-lived Start-Process
    # result unless its native process handle is acquired before it exits.
    $null = $ownedProcess.Handle
    $finished = $ownedProcess.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) {
        $ownedProcess.Kill()
        $null = $ownedProcess.WaitForExit(5000)
    }
    foreach ($outputPath in @($stdout, $stderr)) {
        if (-not (Test-Path -LiteralPath $outputPath)) { continue }
        $preview = @(Get-Content -LiteralPath $outputPath -TotalCount 161)
        if ($preview.Count -le 160) { $preview }
        else {
            $preview | Select-Object -First 120
            Write-Output '[Probe output shortened; final 40 lines follow.]'
            Get-Content -LiteralPath $outputPath -Tail 40
        }
    }
    if (-not $finished) { throw "Mono probe exceeded ${TimeoutSeconds}s; only its owned helper process was stopped." }
    if ($ownedProcess.ExitCode -ne 0) { throw "Actual Mono Discord probe failed (child exit $($ownedProcess.ExitCode))." }
}
finally {
    if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolvedTestDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedTestDirectory) -notmatch '^ServerManager-Mono-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected Mono test directory.'
    }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}
