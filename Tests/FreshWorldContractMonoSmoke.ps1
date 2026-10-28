param(
    [Parameter(Mandatory = $true)][string]$FreshWorldPath,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [ValidateRange(10, 60)][int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
$embedRoot = Join-Path $GamePath 'MonoBleedingEdge\EmbedRuntime'
$configRoot = Join-Path $GamePath 'MonoBleedingEdge\etc'
$referenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$hostSource = Join-Path $ProjectRoot 'Tests\DiscordMonoHost.cs'
$probeSource = Join-Path $ProjectRoot 'Tests\FreshWorldContractMonoProbe.cs'
foreach ($required in @($compiler, $FreshWorldPath, $hostSource, $probeSource,
    (Join-Path $managedRoot 'mscorlib.dll'), (Join-Path $referenceRoot 'mscorlib.dll'),
    (Join-Path $embedRoot 'mono-2.0-bdwgc.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "FreshWorld Mono prerequisite missing: $required" }
}
$FreshWorldPath = [IO.Path]::GetFullPath($FreshWorldPath)
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-FreshWorldMono-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$ownedProcess = $null
try {
    $hostPath = Join-Path $testDirectory 'DiscordMonoHost.exe'
    $probePath = Join-Path $testDirectory 'FreshWorldContractMonoProbe.dll'
    $common = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0')
    $references = @('mscorlib.dll', 'System.dll', 'System.Core.dll') | ForEach-Object { '/reference:' + (Join-Path $referenceRoot $_) }
    & dotnet @common @references '/target:exe' '/platform:x64' "/out:$hostPath" $hostSource
    if ($LASTEXITCODE -ne 0) { throw 'FreshWorld Mono helper compilation failed.' }
    & dotnet @common @references '/target:library' "/out:$probePath" $probeSource
    if ($LASTEXITCODE -ne 0) { throw 'FreshWorld Mono probe compilation failed.' }
    # Only the actual merged provider DLL is isolated; do not manufacture dependency
    # shims or initialize either plugin if the original contract cannot load.
    $isolatedPlugin = Join-Path $testDirectory 'FreshWorld.dll'
    Copy-Item -LiteralPath $FreshWorldPath -Destination $isolatedPlugin
    $sourceHash = (Get-FileHash -LiteralPath $FreshWorldPath -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $isolatedPlugin -Algorithm SHA256).Hash -ne $sourceHash) { throw 'FreshWorld isolated DLL copy hash mismatch.' }
    Write-Output "FreshWorld SHA-256: $sourceHash"
    $stdout = Join-Path $testDirectory 'stdout.txt'
    $stderr = Join-Path $testDirectory 'stderr.txt'
    $arguments = @($embedRoot, $managedRoot, $configRoot, $probePath, $isolatedPlugin) | ForEach-Object { '"' + $_ + '"' }
    $ownedProcess = Start-Process -FilePath $hostPath -ArgumentList $arguments -WorkingDirectory $testDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $ownedProcess.Handle
    $finished = $ownedProcess.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
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
    if (-not $finished) { throw "FreshWorld Mono probe exceeded ${TimeoutSeconds}s; only its owned helper was stopped." }
    if ($ownedProcess.ExitCode -ne 0) { throw "Actual Mono FreshWorld contract probe failed (child exit $($ownedProcess.ExitCode))." }
}
finally {
    if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolved.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolved) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolved) -notmatch '^ServerManager-FreshWorldMono-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected FreshWorld Mono test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
