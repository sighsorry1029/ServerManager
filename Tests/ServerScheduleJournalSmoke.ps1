param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'ServerScheduleSmoke.ps1') -ProjectRoot $ProjectRoot -Configuration $Configuration -HarnessSource (Join-Path $PSScriptRoot 'ServerScheduleJournalSmoke.cs')
