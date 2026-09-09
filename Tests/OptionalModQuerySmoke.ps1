param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $powerShell7 = Get-Command pwsh.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    & $powerShell7.Source -NoProfile -File $PSCommandPath -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Optional-mod query smoke tests failed with exit code $LASTEXITCODE." }
    return
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$querySource = Join-Path $projectRoot 'Networking\OptionalModQuery.cs'
$fixtureSource = Join-Path $PSScriptRoot 'Fixtures\OptionalModQueryFixture.cs'
# Compile the production state machine against inert wrappers. No game process,
# Steam initialization, server socket, DNS lookup, or live configuration is used.
Add-Type -Path @($querySource, $fixtureSource) -CompilerOptions '/nullable:annotations' -WarningAction SilentlyContinue
$assertions = [ServerManager.OptionalModQuerySmoke]::Run()
Write-Host "Optional-mod query smoke tests passed ($assertions assertions; no network calls)."
