param(
    [string]$Configuration = 'Debug', # Standard smoke-runner argument; fixture compiles production source.
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdk = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$directory = Join-Path $temporaryRoot ('ServerManager-SeedPermissions-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$process = $null
try {
    # These identities select trusted TEST TOOLS matching the installed stack;
    # no game/mod runtime permission depends on binary identity.
    foreach ($tool in @(
        @('harmonyx\2.9.0\lib\net35\0Harmony.dll', '0Harmony.dll'),
        @('monomod.runtimedetour\22.1.29.1\lib\net35\MonoMod.RuntimeDetour.dll', 'MonoMod.RuntimeDetour.dll'),
        @('monomod.utils\22.1.29.1\lib\net35\MonoMod.Utils.dll', 'MonoMod.Utils.dll')
    )) {
        $restored = Join-Path $packageRoot $tool[0]
        $installed = Join-Path (Join-Path $GamePath 'BepInEx\core') $tool[1]
        if ((Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash) {
            throw "Real-Harmony fixture tool does not match the installed stack: $($tool[1])"
        }
        Copy-Item -LiteralPath $restored -Destination (Join-Path $directory $tool[1])
    }
    Copy-Item -LiteralPath (Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll') -Destination $directory
    $harness = Join-Path $directory 'SeedCommandPermissionsSmoke.exe'
    $arguments = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable', '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $framework 'mscorlib.dll')),
        ("/reference:" + (Join-Path $framework 'System.dll')),
        ("/reference:" + (Join-Path $framework 'System.Core.dll')),
        ("/reference:" + (Join-Path $directory '0Harmony.dll')),
        (Join-Path $ProjectRoot 'Networking\ClientCommandPermissions.cs'),
        (Join-Path $ProjectRoot 'Tests\SeedCommandPermissionsSmoke.cs'))
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Seed permissions harness compilation failed ($LASTEXITCODE)." }
    $stdout = Join-Path $directory 'stdout.txt'
    $stderr = Join-Path $directory 'stderr.txt'
    $process = Start-Process -FilePath $harness -WorkingDirectory $directory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $process.Handle
    $finished = $process.WaitForExit(20000)
    if (-not $finished) { $process.Kill(); $null = $process.WaitForExit(5000) }
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -TotalCount 100 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -TotalCount 100 }
    if (-not $finished) { throw 'Seed permissions fixture timed out; only its owned helper was stopped.' }
    if ($process.ExitCode -ne 0) { throw "Seed permissions fixture failed ($($process.ExitCode))." }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }
    if ($null -ne $process) { $process.Dispose() }
    $resolvedDirectory = [IO.Path]::GetFullPath($directory)
    if (-not $resolvedDirectory.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolvedDirectory) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolvedDirectory) -notmatch '^ServerManager-SeedPermissions-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected seed permissions test directory.'
    }
    Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
}
