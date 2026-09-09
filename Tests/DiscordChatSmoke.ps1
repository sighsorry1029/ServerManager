param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Configuration = 'Debug',
    [string]$YamlDotNetPath = (Join-Path $env:USERPROFILE '.nuget\packages\yamldotnet\18.1.0\lib\net47\YamlDotNet.dll'),
    [string]$JsonPath = (Join-Path $env:USERPROFILE '.nuget\packages\newtonsoft.json\13.0.4\lib\net45\Newtonsoft.Json.dll')
)
$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-DiscordChat-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$ownedProcess = $null
try {
    $harness = Join-Path $testDirectory 'DiscordChatSmoke.exe'
    $compileArgs = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable', '/target:exe', "/out:$harness")
    foreach ($name in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Net.Http.dll')) {
        $compileArgs += '/reference:' + (Join-Path $frameworkPath $name)
    }
    $compileArgs += @("/reference:$YamlDotNetPath", "/reference:$JsonPath",
        (Join-Path $ProjectRoot 'Discord\DiscordSettings.cs'),
        (Join-Path $ProjectRoot 'Discord\DiscordCommands.cs'),
        (Join-Path $ProjectRoot 'Events\IntegrationApi.cs'),
        (Join-Path $ProjectRoot 'Tests\DiscordChatSmoke.cs'))
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Discord chat harness compilation failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath $YamlDotNetPath -Destination (Join-Path $testDirectory 'YamlDotNet.dll')
    Copy-Item -LiteralPath $JsonPath -Destination (Join-Path $testDirectory 'Newtonsoft.Json.dll')
    $stdout = Join-Path $testDirectory 'stdout.txt'
    $stderr = Join-Path $testDirectory 'stderr.txt'
    $ownedProcess = Start-Process -FilePath $harness -WorkingDirectory $testDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $ownedProcess.Handle
    $finished = $ownedProcess.WaitForExit(20000)
    if (-not $finished) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -TotalCount 100 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -TotalCount 100 }
    if (-not $finished) { throw 'Discord chat probe timed out; only its owned helper was stopped.' }
    if ($ownedProcess.ExitCode -ne 0) { throw "Discord chat smoke failed ($($ownedProcess.ExitCode))." }
}
finally {
    if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { $ownedProcess.Kill(); $null = $ownedProcess.WaitForExit(5000) }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolvedTestDirectory.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolvedTestDirectory) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolvedTestDirectory) -notmatch '^ServerManager-DiscordChat-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected Discord chat test directory.'
    }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}
