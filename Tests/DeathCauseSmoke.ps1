param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkPath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required for the C# 10 compiler.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-DeathCause-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $harness = Join-Path $testDirectory 'DeathCauseSmoke.exe'
    $compileArgs = @(
        $compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable',
        '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $frameworkPath 'mscorlib.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Core.dll')),
        (Join-Path $projectRoot 'Events\ClientEventObservation.cs'),
        (Join-Path $projectRoot 'Events\EventReportProtocol.cs'),
        (Join-Path $projectRoot 'Events\EventMessageText.cs'),
        (Join-Path $projectRoot 'Tests\DeathCauseSmoke.cs')
    )
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Death cause smoke compilation failed ($LASTEXITCODE)." }
    & $harness
    if ($LASTEXITCODE -ne 0) { throw "Death cause smoke failed ($LASTEXITCODE)." }
}
finally {
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    if ((Split-Path -Parent $resolvedTestDirectory) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolvedTestDirectory) -notmatch '^ServerManager-DeathCause-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected death-cause test directory.'
    }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}

# Metadata only: ensure fixture enum values match the actual installed engine.
# No Unity/Valheim classes are instantiated, and no game method executes.
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GamePath 'valheim_Data\Managed\assembly_valheim.dll'))
try {
    $hitType = ($game.MainModule.Types | Where-Object Name -eq 'HitData').NestedTypes | Where-Object Name -eq 'HitType'
    foreach ($entry in @{ Smoke = 9; Freezing = 6; Fall = 3; Drowning = 4; Burning = 5; Poisoned = 7; Tree = 13 }.GetEnumerator()) {
        $field = $hitType.Fields | Where-Object Name -eq $entry.Key
        if ($null -eq $field -or $field.Constant -ne $entry.Value) { throw "Installed HitType does not match the death fixture: $($entry.Key)." }
    }
} finally { $game.Dispose() }
Write-Output 'PASS: installed engine death-cause enum metadata matches the inert fixture.'
