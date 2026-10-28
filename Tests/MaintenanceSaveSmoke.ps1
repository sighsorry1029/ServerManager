param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$OriginalGameAssembly,
    [string]$OriginalUtilsAssembly
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this source-linked test using PowerShell 7.' }
$project = Split-Path -Parent $PSScriptRoot
if (-not $OriginalGameAssembly) { $OriginalGameAssembly = Join-Path $GamePath 'valheim_Data\Managed\assembly_valheim.dll' }
if (-not $OriginalUtilsAssembly) { $OriginalUtilsAssembly = Join-Path $GamePath 'valheim_Data\Managed\assembly_utils.dll' }
$source = Get-Content -LiteralPath (Join-Path $project 'Events/ServerEventRuntime.cs') -Raw
function Read-Region([string]$Start, [string]$End) {
    $first = $source.IndexOf($Start, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "Production source marker missing: $Start" }
    $last = $source.IndexOf($End, $first + $Start.Length, [StringComparison]::Ordinal)
    if ($last -le $first) { throw "Production source end marker missing: $End" }
    $source.Substring($first, $last - $first)
}
$queue = Read-Region '        internal static Task<ServerManagerCommandResult> EnqueueCommand(' '        private static void SetCommandAdmission('
$execution = Read-Region '        private static ServerManagerCommandResult ExecuteCommand(' '        // Called synchronously on Unity'
$queued = Read-Region '        private sealed class QueuedCommand' '    internal sealed class EventLogWriter'
# This small data-holder has no braces in string literals/comments. Preserve its
# balanced class block without its enclosing runtime/namespace closing braces.
$balance = 0
for ($index = $queued.IndexOf('{'); $index -lt $queued.Length; ++$index) {
    if ($queued[$index] -eq '{') { ++$balance }
    if ($queued[$index] -eq '}') { --$balance; if ($balance -eq 0) { $queued = $queued.Substring(0, $index + 1); break } }
}
$harness = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MaintenanceSaveSmoke.cs') -Raw
$harness = $harness.Replace('// SOURCE_LINKED_QUEUED_COMMAND', $queued).
    Replace('// SOURCE_LINKED_QUEUE', $queue).
    Replace('// SOURCE_LINKED_EXECUTION', $execution)
Add-Type -TypeDefinition $harness -WarningAction SilentlyContinue
[MaintenanceSaveSmoke]::Verify()

# Inspect the untouched game's access metadata and the real call path; neither
# publicization nor fixture mutation of a game DLL is used for these checks.
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx/core/Mono.Cecil.dll')) | Out-Null
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($OriginalGameAssembly)
$utils = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($OriginalUtilsAssembly)
function Check([bool]$Condition, [string]$Reason) { if (-not $Condition) { throw $Reason } }
try {
    $znet = $game.MainModule.GetType('ZNet')
    $save = @($znet.Methods | Where-Object { $_.Name -eq 'Save' -and $_.Parameters.Count -eq 3 })
    Check ($save.Count -eq 1 -and $save[0].IsPublic -and -not $save[0].IsStatic -and
        @($save[0].Parameters | Where-Object { $_.ParameterType.FullName -ne 'System.Boolean' }).Count -eq 0) 'Original ZNet.Save(bool,bool,bool) public contract changed.'
    $calls = @($save[0].Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
    foreach ($needed in @('HasSessionFlag', 'SkipSaving', 'SaveOtherPlayerProfiles', 'SaveWorld')) {
        Check ($calls -contains $needed) "Original Save no longer contains required boundary $needed."
    }
    foreach ($forbidden in @('HardSaveBlock', 'RPC_Save', 'SavePlayerProfile', 'EnoughDiskSpaceAvailable')) {
        Check ($calls -notcontains $forbidden) "Dedicated entry unexpectedly reaches $forbidden."
    }
    Check (($znet.Fields | Where-Object Name -eq 'm_loadError').IsPublic) 'Original load-error guard is not public.'
    Check (($game.MainModule.GetType('World').Fields | Where-Object Name -eq 'm_fileSource').IsPublic) 'Original world file source is not public.'
    foreach ($spec in @(@('ZNet','GetWorldIfIsHost'), @('World','GetSavePaths'), @('ZoneSystem','SkipSaving'), @('DungeonDB','SkipSaving'), @('SaveSystem','HasSessionFlag'))) {
        Check (@($game.MainModule.GetType($spec[0]).Methods | Where-Object { $_.Name -eq $spec[1] -and $_.IsPublic }).Count -eq 1) "Original public boundary missing: $($spec -join '.')."
    }
    $hardBlock = $znet.Methods | Where-Object Name -eq 'HardSaveBlock'
    $platform = @($hardBlock.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_DistributionPlatform' })
    Check ($platform.Count -eq 1 -and $platform[0].Next.Operand.Name -eq 'get_Platform') 'Original reported null-platform boundary changed; inspect before updating the fixture.'
    $rpc = $znet.Methods | Where-Object Name -eq 'RPC_Save'
    Check (@($rpc.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'HardSaveBlock' }).Count -eq 1) 'Original RPC_Save no longer reaches HardSaveBlock.'
    $files = $utils.MainModule.GetType('FileHelpers')
    Check (@($files.Methods | Where-Object { $_.Name -eq 'GetFileSizes' -and $_.IsPublic -and $_.Parameters[0].ParameterType.FullName -like 'System.Collections.Generic.List*' }).Count -eq 1) 'Original world-size helper contract changed.'
    $disk = $files.Methods | Where-Object Name -eq 'CheckDiskSpace'
    $constants = @($disk.Body.Instructions | ForEach-Object { if ($_.Operand -is [int]) { $_.Operand } })
    Check ($constants -contains 104857600 -and $constants -contains 2097152) 'Original disk reserve baseline changed; inspect dedicated guard.'
    foreach ($forbidden in @('BeforeWorldSnapshotPrepared(', 'MarkWorldSaveWorkerSucceeded(', 'OnWorldSaveCheckpointCompleted(')) {
        Check (-not $execution.Contains($forbidden)) "Maintenance dispatch must not synthesize $forbidden."
    }
    Write-Output 'PASS: original public game signatures, save/block call paths, disk baseline and absence of synthetic completions.'
    Write-Output ('Original game SHA256: ' + (Get-FileHash -Algorithm SHA256 -LiteralPath $OriginalGameAssembly).Hash)
    Write-Output ('Original utils SHA256: ' + (Get-FileHash -Algorithm SHA256 -LiteralPath $OriginalUtilsAssembly).Hash)
}
finally { $game.Dispose(); $utils.Dispose() }
