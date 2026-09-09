param(
    [string]$Configuration = 'Debug',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$NewtonsoftPath = (Join-Path $env:USERPROFILE '.nuget\packages\newtonsoft.json\13.0.4\lib\net45\Newtonsoft.Json.dll')
)

$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required for the C# 10 compiler.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
foreach ($required in @($compiler, $NewtonsoftPath, (Join-Path $frameworkPath 'mscorlib.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing offline Gateway test dependency: $required" }
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testDirectory = Join-Path $temporaryRoot ('ServerManager-Gateway-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $harness = Join-Path $testDirectory 'DiscordGatewayHarness.exe'
    $compileArgs = @(
        $compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable',
        '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $frameworkPath 'mscorlib.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Core.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Net.Http.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Numerics.dll')),
        "/reference:$NewtonsoftPath",
        (Join-Path $ProjectRoot 'Discord\DiscordGateway.cs'),
        (Join-Path $ProjectRoot 'Tests\DiscordGatewayHarness.cs')
    )
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Gateway harness compilation failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath $NewtonsoftPath -Destination (Join-Path $testDirectory 'Newtonsoft.Json.dll')
    & $harness
    if ($LASTEXITCODE -ne 0) { throw "Gateway smoke test failed ($LASTEXITCODE)." }
}
finally {
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolvedTestDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedTestDirectory) -notmatch '^ServerManager-Gateway-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected Gateway test directory.'
    }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}
