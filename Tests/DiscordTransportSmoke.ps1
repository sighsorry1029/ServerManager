param(
    [string]$Configuration = 'Debug',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$NewtonsoftPath = (Join-Path $env:USERPROFILE '.nuget\packages\newtonsoft.json\13.0.4\lib\net45\Newtonsoft.Json.dll'),
    [string]$YamlDotNetPath = (Join-Path $env:USERPROFILE '.nuget\packages\yamldotnet\18.1.0\lib\net47\YamlDotNet.dll')
)

$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') {
    throw 'A modern .NET SDK is required for the C# 10 compiler.'
}
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
foreach ($required in @($compiler, $NewtonsoftPath, $YamlDotNetPath, (Join-Path $frameworkPath 'mscorlib.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing offline Discord transport test dependency: $required" }
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-DiscordTransport-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $harness = Join-Path $testDirectory 'DiscordTransportSmoke.exe'
    $compileArgs = @(
        $compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable',
        '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $frameworkPath 'mscorlib.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Core.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Net.Http.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Numerics.dll')),
        "/reference:$NewtonsoftPath",
        "/reference:$YamlDotNetPath",
        ("/resource:" + (Join-Path $ProjectRoot 'translations\English.yml') + ',ServerManager.translations.English.yml'),
        ("/resource:" + (Join-Path $ProjectRoot 'translations\Korean.yml') + ',ServerManager.translations.Korean.yml'),
        (Join-Path $ProjectRoot 'LocalizationManager.cs'),
        (Join-Path $ProjectRoot 'Discord\DiscordSettings.cs'),
        (Join-Path $ProjectRoot 'Discord\DiscordHttp.cs'),
        (Join-Path $ProjectRoot 'Discord\DiscordWebhooks.cs'),
        (Join-Path $ProjectRoot 'Events\EventMessageText.cs'),
        (Join-Path $ProjectRoot 'Tests\DiscordTransportSmoke.cs')
    )
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Discord transport smoke compilation failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath $NewtonsoftPath -Destination (Join-Path $testDirectory 'Newtonsoft.Json.dll')
    Copy-Item -LiteralPath $YamlDotNetPath -Destination (Join-Path $testDirectory 'YamlDotNet.dll')
    & $harness
    if ($LASTEXITCODE -ne 0) { throw "Discord transport smoke failed ($LASTEXITCODE)." }
}
finally {
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    $expectedPrefix = $temporaryRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTestDirectory.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolvedTestDirectory) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolvedTestDirectory) -notmatch '^ServerManager-DiscordTransport-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected Discord transport test directory.'
    }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}
