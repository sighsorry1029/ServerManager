param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Configuration = 'Release',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$UpgradeWorldPath = (Join-Path $env:APPDATA 'com.kesomannen.gale\valheim\profiles\modtest\BepInEx\plugins\JereKuusela-Upgrade_World\UpgradeWorld.dll')
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdk = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$directory = Join-Path $temporaryRoot ('ServerManager-UpgradeWorld-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$process = $null
try {
    $harness = Join-Path $directory 'UpgradeWorldScheduleSmoke.exe'
    $arguments = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable', '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $framework 'mscorlib.dll')),
        ("/reference:" + (Join-Path $framework 'System.dll')),
        ("/reference:" + (Join-Path $framework 'System.Core.dll')),
        (Join-Path $ProjectRoot 'Scheduling\UpgradeWorldScheduleBridge.cs'),
        (Join-Path $ProjectRoot 'Tests\UpgradeWorldScheduleSmoke.cs'))
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Upgrade World bridge harness compilation failed ($LASTEXITCODE)." }
    $stdout = Join-Path $directory 'stdout.txt'
    $stderr = Join-Path $directory 'stderr.txt'
    $process = Start-Process -FilePath $harness -WorkingDirectory $directory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $process.Handle
    $finished = $process.WaitForExit(20000)
    if (-not $finished) { $process.Kill(); $null = $process.WaitForExit(5000) }
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -TotalCount 100 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -TotalCount 100 }
    if (-not $finished -or $process.ExitCode -ne 0) { throw 'Upgrade World bridge behavior tests failed.' }

    if (Test-Path -LiteralPath $UpgradeWorldPath) {
        $cecil = Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll'
        $null = [Reflection.Assembly]::LoadFrom($cecil)
        $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($UpgradeWorldPath)
        try {
            function Require-Type([string]$name) {
                $type = @($assembly.MainModule.Types | Where-Object FullName -eq "UpgradeWorld.$name")
                if ($type.Count -ne 1) { throw "Upgrade World contract type missing: $name" }
                return $type[0]
            }
            function Require-Method([string]$type, [string]$name, [string]$returns, [string[]]$parameters, [bool]$static) {
                $matches = @((Require-Type $type).Methods | Where-Object {
                    $_.Name -eq $name -and $_.ReturnType.FullName -eq $returns -and $_.IsStatic -eq $static -and
                    (@($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -eq ($parameters -join '|')
                })
                if ($matches.Count -ne 1) { throw "Upgrade World contract method mismatch: $type.$name" }
                return $matches[0]
            }
            $plugin = Require-Type 'UpgradeWorld'
            $attribute = @($plugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
            if ($attribute.Count -ne 1 -or $attribute[0].ConstructorArguments[0].Value -ne 'upgrade_world' -or $attribute[0].ConstructorArguments[2].Value -ne '1.80') {
                throw 'Upgrade World reference is not reviewed plugin 1.80.'
            }
            $null = Require-Method 'Executor' 'AddOperation' 'System.Void' @('UpgradeWorld.ExecutedOperation', 'System.Boolean') $true
            $null = Require-Method 'Executor' 'GetOperations' 'System.Collections.Generic.List`1<UpgradeWorld.ExecutedOperation>' @() $true
            $null = Require-Method 'Executor' 'StopExecution' 'System.Void' @() $true
            $null = Require-Method 'ExecutedOperation' 'Execute' 'System.Collections.IEnumerator' @('System.Diagnostics.Stopwatch') $false
            $null = Require-Method 'ExecutedOperation' 'Init' 'System.Boolean' @('System.Boolean') $false
            $getInfo = Require-Method 'ExecutedOperation' 'GetInfo' 'System.String' @() $false
            if (@($getInfo.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'OnInit' }).Count -ne 1) {
                throw 'Upgrade World inspection/reinitialization contract changed.'
            }
            $null = Require-Method 'Settings' 'IsRoot' 'System.Boolean' @('System.String') $true
            $null = Require-Method 'RegenerateLocations' 'ExecuteLocation' 'System.Boolean' @('Vector2i', 'ZoneSystem/LocationInstance') $false
            $null = Require-Method 'SpawnLocations' 'ExecuteLocation' 'System.Boolean' @('Vector2i', 'ZoneSystem/LocationInstance') $false
            $null = Require-Method 'Helper' 'Print' 'System.Void' @('Terminal', 'ZRpc', 'System.String') $true
            $operationNames = @('ResetChests', 'DistributeLocations', 'SpawnLocations', 'RemoveLocations', 'RegenerateLocations',
                'EditObjects', 'RefreshObjects', 'RemoveObjects', 'SwapObjects', 'TempleVersion', 'AddVegetation',
                'RemoveVegetation', 'ResetVegetation', 'ResetZones', 'WorldVersion', 'Generate', 'RestoreZones', 'Print')
            foreach ($operationName in $operationNames) {
                foreach ($methodName in @('OnInit', 'OnStart', 'OnExecute', 'OnEnd')) {
                    $current = Require-Type $operationName
                    $found = $null
                    while ($null -ne $current) {
                        $found = @($current.Methods | Where-Object { $_.Name -eq $methodName -and -not $_.IsStatic })
                        if ($found.Count -gt 0) { break }
                        if ($null -eq $current.BaseType -or -not $current.BaseType.FullName.StartsWith('UpgradeWorld.')) { break }
                        $current = Require-Type ($current.BaseType.FullName.Substring('UpgradeWorld.'.Length))
                    }
                    $returnType = if ($methodName -eq 'OnExecute') { 'System.Collections.IEnumerator' } elseif ($methodName -eq 'OnInit') { 'System.String' } else { 'System.Void' }
                    $parameters = if ($methodName -eq 'OnExecute') { 'System.Diagnostics.Stopwatch' } else { '' }
                    if ($found.Count -ne 1 -or $found[0].IsAbstract -or -not $found[0].HasBody -or $found[0].ReturnType.FullName -ne $returnType -or
                        (@($found[0].Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -ne $parameters) {
                        throw "Unobservable Upgrade World lifecycle: $operationName.$methodName"
                    }
                }
            }
            $game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GamePath 'valheim_Data\Managed\assembly_valheim.dll'))
            try {
                $zone = @($game.MainModule.Types | Where-Object FullName -eq 'ZoneSystem')[0]
                $location = @($zone.NestedTypes | Where-Object Name -eq 'LocationInstance')[0]
                $placed = @($location.Fields | Where-Object { $_.Name -eq 'm_placed' -and $_.FieldType.FullName -eq 'System.Boolean' -and -not $_.IsStatic })
                if ($placed.Count -ne 1) { throw 'Valheim location placement observation contract is unavailable.' }
            }
            finally { $game.Dispose() }
            foreach ($field in @(@('Executor', 'executionCoroutine', 'UnityEngine.Coroutine', $true), @('ExecutedOperation', 'Failed', 'System.Int32', $false),
                    @('SavingCommands', 'SavingDisabled', 'System.Boolean', $true), @('ServerExecution', 'User', 'ZRpc', $true),
                    @('ExecutedEntityOperation', 'TotalCount', 'System.Int32', $false))) {
                $found = @((Require-Type $field[0]).Fields | Where-Object { $_.Name -eq $field[1] -and $_.FieldType.FullName -eq $field[2] -and $_.IsStatic -eq $field[3] })
                if ($found.Count -ne 1) { throw "Upgrade World field mismatch: $($field[0]).$($field[1])" }
            }
            foreach ($clean in @('CleanDuplicates', 'CleanLocations', 'CleanObjects', 'CleanChests', 'CleanStands', 'CleanDungeons', 'CleanSpawns', 'CleanHealth')) {
                $parameters = if ($clean -eq 'CleanDuplicates') { @('Terminal', 'System.Boolean', 'System.Boolean') } else { @('Terminal', 'ZDO[]', 'System.Boolean', 'System.Boolean') }
                $null = Require-Method $clean '.ctor' 'System.Void' $parameters $false
            }
            $null = Require-Method 'FixLocations' '.ctor' 'System.Void' @('Terminal', 'UpgradeWorld.FiltererParameters') $false
            $null = Require-Method 'SwapLocations' '.ctor' 'System.Void' @('Terminal', 'System.Collections.Generic.IEnumerable`1<System.String>', 'UpgradeWorld.DataParameters') $false
            $null = Require-Method 'RegisterLocation' '.ctor' 'System.Void' @('Terminal', 'System.String', 'UnityEngine.Vector3') $false
            $null = Require-Method 'ChangeTime' '.ctor' 'System.Void' @('Terminal', 'System.Double') $false
            $null = Require-Method 'SetTime' '.ctor' 'System.Void' @('Terminal', 'System.Double') $false
            Write-Host 'PASS: actual Upgrade World 1.80 DLL plugin, 18 operation lifecycles, executor, permission, empty-selection and 13 immediate-constructor contracts (metadata only; DLL not executed).'
        }
        finally { $assembly.Dispose() }
    }
    else { Write-Warning 'Upgrade World DLL not present: actual dependency metadata probe skipped; source-linked bridge tests passed.' }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }
    if ($null -ne $process) { $process.Dispose() }
    $resolved = [IO.Path]::GetFullPath($directory)
    if ((Split-Path -Parent $resolved) -ne $temporaryRoot -or (Split-Path -Leaf $resolved) -notmatch '^ServerManager-UpgradeWorld-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected Upgrade World test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
