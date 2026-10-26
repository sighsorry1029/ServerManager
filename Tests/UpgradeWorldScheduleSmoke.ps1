param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$UpgradeWorldPath = (Join-Path $env:APPDATA 'com.kesomannen.gale\valheim\profiles\modtest\BepInEx\plugins\JereKuusela-Upgrade_World\UpgradeWorld.dll'),
    [string]$CecilPath = '',
    [switch]$MetadataOnly,
    # Test a retained incompatible reference without accepting an arbitrary failure.
    [string]$ExpectedUnsupportedMember = ''
)
$ErrorActionPreference = 'Stop'
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdk = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$directory = Join-Path $temporaryRoot ('ServerManager-UpgradeWorld-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$process = $null
try {
    if (-not $MetadataOnly) {
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

    foreach ($variant in @('UW_BAD_CLEAN', 'UW_BAD_CORE')) {
        $variantHarness = Join-Path $directory ($variant + '.exe')
        $variantArguments = @($arguments | Where-Object { -not $_.StartsWith('/out:', [StringComparison]::OrdinalIgnoreCase) })
        & dotnet @variantArguments ("/define:" + $variant) ("/out:" + $variantHarness)
        if ($LASTEXITCODE -ne 0) { throw "Upgrade World capability fixture compilation failed: $variant" }
        $process.Dispose()
        $process = Start-Process -FilePath $variantHarness -WorkingDirectory $directory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        $null = $process.Handle
        $finished = $process.WaitForExit(20000)
        if (-not $finished) { $process.Kill(); $null = $process.WaitForExit(5000) }
        Get-Content -LiteralPath $stdout -TotalCount 100
        Get-Content -LiteralPath $stderr -TotalCount 100
        if (-not $finished -or $process.ExitCode -ne 0) { throw "Upgrade World capability fixture failed: $variant" }
    }

    # A small real-Harmony probe complements the fixture's intentional fake patcher.
    # Use trusted restored tools whose bytes match this installed BepInEx stack;
    # this identity check identifies the TEST TOOL, not an Upgrade World allowlist.
    $harmonyTools = @(
        @('harmonyx\2.9.0\lib\net35\0Harmony.dll', '0Harmony.dll'),
        @('monomod.runtimedetour\22.1.29.1\lib\net35\MonoMod.RuntimeDetour.dll', 'MonoMod.RuntimeDetour.dll'),
        @('monomod.utils\22.1.29.1\lib\net35\MonoMod.Utils.dll', 'MonoMod.Utils.dll')
    )
    foreach ($tool in $harmonyTools) {
        $restored = Join-Path $packageRoot $tool[0]
        $installed = Join-Path (Join-Path $GamePath 'BepInEx\core') $tool[1]
        if (-not (Test-Path -LiteralPath $restored) -or -not (Test-Path -LiteralPath $installed)) {
            throw "Real-Harmony test tool missing: $restored or $installed"
        }
        if ((Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash) {
            throw "Real-Harmony test tool does not match the installed stack: $($tool[1]). Select matching trusted tools before claiming runtime injection coverage."
        }
        Copy-Item -LiteralPath $restored -Destination (Join-Path $directory $tool[1])
    }
    # MonoMod's installed build depends on Cecil 0.10.4's ABI. Keep its actual
    # dependency beside the managed probe; do not bind this runtime to 0.11.x.
    Copy-Item -LiteralPath (Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll') -Destination $directory
    $realHarness = Join-Path $directory 'UpgradeWorldScheduleHarmonySmoke.exe'
    & dotnet $compiler /nologo /noconfig /nostdlib+ /langversion:10.0 /nullable:enable /target:exe "/out:$realHarness" `
        ("/reference:" + (Join-Path $framework 'mscorlib.dll')) `
        ("/reference:" + (Join-Path $framework 'System.dll')) `
        ("/reference:" + (Join-Path $framework 'System.Core.dll')) `
        ("/reference:" + (Join-Path $directory '0Harmony.dll')) `
        (Join-Path $ProjectRoot 'Tests\UpgradeWorldScheduleHarmonySmoke.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Real-Harmony lifecycle probe compilation failed.' }
    $process.Dispose()
    $process = Start-Process -FilePath $realHarness -WorkingDirectory $directory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $process.Handle
    $finished = $process.WaitForExit(20000)
    if (-not $finished) { $process.Kill(); $null = $process.WaitForExit(5000) }
    Get-Content -LiteralPath $stdout -TotalCount 100
    Get-Content -LiteralPath $stderr -TotalCount 100
    if (-not $finished -or $process.ExitCode -ne 0) { throw 'Real-Harmony lifecycle probe failed.' }
    }

    if (Test-Path -LiteralPath $UpgradeWorldPath) {
        # Prefer the restored .NET Framework tool. A downloaded game-side copy can
        # carry Windows zone metadata; do not unblock or change that user file.
        $cecil = $CecilPath
        if (-not $cecil) {
            $restoredCecil = Join-Path $packageRoot 'mono.cecil\0.11.6\lib\net40\Mono.Cecil.dll'
            $cecil = if (Test-Path -LiteralPath $restoredCecil) { $restoredCecil } else { Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll' }
        }
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
                if ($matches.Count -ne 1) {
                    $actual = @((Require-Type $type).Methods | Where-Object Name -eq $name | ForEach-Object FullName) -join '; '
                    throw "Upgrade World contract method mismatch: $type.$name; expected $returns ($($parameters -join ', ')); actual: $actual"
                }
                if ($matches[0].IsAbstract -or -not $matches[0].HasBody) { throw "Upgrade World contract body missing: $type.$name" }
                return $matches[0]
            }
            function Require-Call($method, [string]$declaringType, [string]$name) {
                $calls = @($method.Body.Instructions | Where-Object {
                    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $declaringType -and $_.Operand.Name -eq $name
                })
                if ($calls.Count -eq 0) { throw "Upgrade World behavior probe failed: $($method.FullName) no longer calls $declaringType.$name" }
            }
            function Get-IteratorMoveNext([string]$owner, [string]$name) {
                $iterators = @((Require-Type $owner).NestedTypes | Where-Object { $_.Name.StartsWith('<' + $name + '>d__', [StringComparison]::Ordinal) })
                if ($iterators.Count -ne 1) { throw "Upgrade World behavior probe cannot identify iterator: $owner.$name" }
                $methods = @($iterators[0].Methods | Where-Object { $_.Name -eq 'MoveNext' -and $_.HasBody })
                if ($methods.Count -ne 1) { throw "Upgrade World iterator MoveNext missing: $owner.$name" }
                return $methods[0]
            }
            function Get-CommandHandlers([string]$owner) {
                # Follow the delegates actually registered by the command rather
                # than selecting a compiler-generated method by its unstable name.
                $constructor = Require-Method $owner '.ctor' 'System.Void' @() $false
                Require-Call $constructor 'UpgradeWorld.Helper' 'Command'
                $handlers = @($constructor.Body.Instructions | Where-Object {
                    $_.OpCode.Code.ToString() -eq 'Ldftn' -and $_.Operand -is [Mono.Cecil.MethodReference] -and
                    $_.Operand.ReturnType.FullName -eq 'System.Void' -and
                    (@($_.Operand.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -eq 'Terminal/ConsoleEventArgs'
                } | ForEach-Object { $_.Operand.Resolve() })
                if ($handlers.Count -eq 0) { throw "Upgrade World command delegate missing: $owner" }
                return $handlers
            }
            function Require-OperationConstructors($method, [string[]]$expected) {
                $actual = @($method.Body.Instructions | Where-Object {
                    $_.OpCode.Code.ToString() -eq 'Newobj' -and $_.Operand -is [Mono.Cecil.MethodReference] -and
                    $_.Operand.DeclaringType.Namespace -eq 'UpgradeWorld' -and
                    $_.Operand.DeclaringType.Name -in $observedOperationNames
                } | ForEach-Object { $_.Operand.DeclaringType.Name })
                if (($actual -join '|') -ne ($expected -join '|')) {
                    throw "Upgrade World command operation sequence changed: $($method.FullName); expected $($expected -join ', '); actual $($actual -join ', ')"
                }
            }
            $plugin = Require-Type 'UpgradeWorld'
            $attribute = @($plugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
            if ($attribute.Count -ne 1 -or $attribute[0].ConstructorArguments[0].Value -ne 'upgrade_world') {
                throw 'Upgrade World reference plugin identity is missing.'
            }
            Write-Host ("Reference identity (not an allowlist): Upgrade World {0}; MVID {1}; SHA256 {2}" -f
                $attribute[0].ConstructorArguments[2].Value, $assembly.MainModule.Mvid, (Get-FileHash -LiteralPath $UpgradeWorldPath -Algorithm SHA256).Hash)
            # The runtime supports the reviewed Init() / ExecutionFailed contract,
            # not a version range. Retained Init(bool) references fail by member.
            $init = Require-Method 'ExecutedOperation' 'Init' 'System.Boolean' @() $false
            $failure = Require-Method 'ExecutedOperation' 'get_ExecutionFailed' 'System.Boolean' @() $false
            if (-not $failure.IsPublic) { throw 'Upgrade World failure observation getter is not public.' }
            $add = Require-Method 'Executor' 'AddOperation' 'System.Void' @('UpgradeWorld.ExecutedOperation', 'System.Boolean') $true
            $null = Require-Method 'Executor' 'GetOperations' 'System.Collections.Generic.List`1<UpgradeWorld.ExecutedOperation>' @() $true
            $null = Require-Method 'Executor' 'StopExecution' 'System.Void' @() $true
            $null = Require-Method 'ExecutedOperation' 'Execute' 'System.Collections.IEnumerator' @('System.Diagnostics.Stopwatch') $false
            $getInfo = Require-Method 'ExecutedOperation' 'GetInfo' 'System.String' @() $false
            if (@($getInfo.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'OnInit' }).Count -ne 0) {
                throw 'Upgrade World inspection unexpectedly reinitializes operations.'
            }
            Require-Call $init 'UpgradeWorld.ExecutedOperation' 'OnInit'
            Require-Call $add 'UpgradeWorld.ExecutedOperation' 'Init'
            Require-Call $add 'UpgradeWorld.Settings' 'get_AutoStart'
            Require-Call $add 'UpgradeWorld.Executor' 'StartExecution'
            $reportFailure = Require-Method 'ExecutedOperation' 'ReportFailure' 'System.Void' @('System.Exception') $false
            Require-Call $reportFailure 'UpgradeWorld.ExecutedOperation' 'set_ExecutionFailed'
            $execute = Get-IteratorMoveNext 'ExecutedOperation' 'Execute'
            foreach ($name in @('OnStart', 'OnExecute', 'OnEnd', 'ReportFailure')) { Require-Call $execute 'UpgradeWorld.ExecutedOperation' $name }
            Require-Call $execute 'UpgradeWorld.BaseOperation' 'PrintPins'
            Require-Call $execute 'System.Collections.IEnumerator' 'MoveNext'
            Require-Call $execute 'System.Collections.IEnumerator' 'get_Current'
            Require-Call $execute 'System.IDisposable' 'Dispose'
            if (@($execute.Body.ExceptionHandlers | Where-Object { $_.HandlerType.ToString() -eq 'Catch' }).Count -eq 0) {
                throw 'Upgrade World Execute failure handling contract changed.'
            }
            $executor = Get-IteratorMoveNext 'Executor' 'ExecuteCoroutine'
            Require-Call $executor 'UpgradeWorld.ExecutedOperation' 'get_ExecutionFailed'
            # These are bounded structural evidence checks, not semantic equivalence
            # proofs. Actual operation/exception/save lifecycle tests remain needed.
            $null = Require-Method 'Settings' 'IsRoot' 'System.Boolean' @('System.String') $true
            $null = Require-Method 'RegenerateLocations' 'ExecuteLocation' 'System.Boolean' @('Vector2s', 'ZoneSystem/LocationInstance') $false
            $null = Require-Method 'SpawnLocations' 'ExecuteLocation' 'System.Boolean' @('Vector2s', 'ZoneSystem/LocationInstance') $false
            $null = Require-Method 'Helper' 'Print' 'System.Void' @('Terminal', 'ZRpc', 'System.String') $true
            $operationNames = @('ResetChests', 'DistributeLocations', 'SpawnLocations', 'RemoveLocations', 'RegenerateLocations',
                'EditObjects', 'RefreshObjects', 'RemoveObjects', 'SwapObjects', 'TempleVersion', 'AddVegetation',
                'RemoveVegetation', 'ResetVegetation', 'ResetZones', 'WorldVersion', 'Generate', 'RestoreZones', 'Print', 'QueuedOperation')
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
            $cleanNames = @('CleanDuplicates', 'CleanLocations', 'CleanObjects', 'CleanChests', 'CleanStands', 'CleanDungeons', 'CleanSpawns', 'CleanHealth')
            foreach ($clean in $cleanNames) {
                $parameters = if ($clean -eq 'CleanDuplicates') { @('Terminal', 'System.Nullable`1<Minimap/PinType>', 'System.Boolean', 'UpgradeWorld.FiltererParameters') } else { @('Terminal', 'ZDO[]', 'System.Nullable`1<Minimap/PinType>', 'System.Boolean') }
                $null = Require-Method $clean '.ctor' 'System.Void' $parameters $false
            }
            $null = Require-Method 'FixLocations' '.ctor' 'System.Void' @('Terminal', 'UpgradeWorld.FiltererParameters') $false
            $null = Require-Method 'SwapLocations' '.ctor' 'System.Void' @('Terminal', 'System.Collections.Generic.IEnumerable`1<System.String>', 'UpgradeWorld.DataParameters') $false
            $null = Require-Method 'RegisterLocation' '.ctor' 'System.Void' @('Terminal', 'System.String', 'UnityEngine.Vector3') $false
            $null = Require-Method 'ChangeTime' '.ctor' 'System.Void' @('Terminal', 'System.Double') $false
            $null = Require-Method 'SetTime' '.ctor' 'System.Void' @('Terminal', 'System.Double') $false
            $observedOperationNames = $operationNames + $cleanNames + @('FixLocations', 'SwapLocations', 'RegisterLocation', 'ChangeTime', 'SetTime')

            # Cleanup constructors are synchronous, but their COMMANDS are not:
            # the current DLL queues an Action. A constructor-only fixture would
            # miss both delayed execution and cancellation before that Action.
            $null = Require-Method 'QueuedOperation' '.ctor' 'System.Void' @('Terminal', 'System.Nullable`1<Minimap/PinType>', 'System.String', 'System.Action') $false
            $queuedExecute = Get-IteratorMoveNext 'QueuedOperation' 'OnExecute'
            Require-Call $queuedExecute 'System.Action' 'Invoke'
            $actionCalls = @($queuedExecute.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'System.Action' -and $_.Operand.Name -eq 'Invoke'
            })
            $actionFinally = @($queuedExecute.Body.ExceptionHandlers | Where-Object {
                $_.HandlerType.ToString() -eq 'Finally' -and $actionCalls.Count -eq 1 -and
                $actionCalls[0].Offset -ge $_.TryStart.Offset -and $actionCalls[0].Offset -lt $_.TryEnd.Offset
            })
            if ($actionFinally.Count -ne 1) { throw 'Upgrade World queued Action is no longer protected by its cleanup finally.' }
            $pinClears = @($queuedExecute.Body.Instructions | Where-Object {
                $_.Offset -ge $actionFinally[0].HandlerStart.Offset -and $_.Offset -lt $actionFinally[0].HandlerEnd.Offset -and
                $_.OpCode.Code.ToString() -eq 'Stsfld' -and $_.Operand -is [Mono.Cecil.FieldReference] -and
                $_.Operand.DeclaringType.FullName -eq 'UpgradeWorld.BaseOperation' -and $_.Operand.Name -eq 'SharedPins' -and
                $_.Previous.OpCode.Code.ToString() -eq 'Ldnull'
            })
            if ($pinClears.Count -ne 1) { throw 'Upgrade World queued Action no longer clears SharedPins in its finally.' }
            foreach ($commandType in @('WorldCleanCommand') + @($cleanNames | ForEach-Object { $_ + 'Command' })) {
                $handlers = @(Get-CommandHandlers $commandType)
                if ($handlers.Count -ne 1) { throw "Upgrade World cleanup command delegate count changed: $commandType" }
                $handler = $handlers[0]
                Require-OperationConstructors $handler @('QueuedOperation')
                Require-Call $handler 'UpgradeWorld.Executor' 'AddOperation'
                $actions = @($handler.Body.Instructions | Where-Object {
                    $_.OpCode.Code.ToString() -eq 'Ldftn' -and $_.Operand -is [Mono.Cecil.MethodReference] -and
                    $_.Operand.ReturnType.FullName -eq 'System.Void' -and $_.Operand.Parameters.Count -eq 0
                } | ForEach-Object { $_.Operand.Resolve() })
                if ($actions.Count -ne 1) { throw "Upgrade World cleanup queued Action changed: $commandType" }
                $expected = if ($commandType -eq 'WorldCleanCommand') { $cleanNames } else { @($commandType.Substring(0, $commandType.Length - 'Command'.Length)) }
                Require-OperationConstructors $actions[0] $expected
                if (@($actions[0].Body.Instructions | Where-Object {
                    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'UpgradeWorld.Executor' -and $_.Operand.Name -eq 'AddOperation'
                }).Count -ne 0) { throw "Upgrade World cleanup Action now creates nested queued work: $commandType" }
            }

            $locationsAddHandlers = @(Get-CommandHandlers 'LocationsAddCommand')
            if ($locationsAddHandlers.Count -ne 1) { throw 'Upgrade World locations_add command delegate count changed.' }
            $locationsAdd = $locationsAddHandlers[0]
            Require-OperationConstructors $locationsAdd @('DistributeLocations', 'SpawnLocations')
            Require-Call $locationsAdd 'System.Nullable`1<System.Boolean>' 'GetValueOrDefault'
            Require-Call $locationsAdd 'System.Nullable`1<System.Boolean>' 'get_HasValue'
            $addCalls = @($locationsAdd.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'UpgradeWorld.Executor' -and $_.Operand.Name -eq 'AddOperation'
            })
            if ($addCalls.Count -ne 2) { throw 'Upgrade World locations_add enqueue sites changed.' }
            $spawnGuards = @($locationsAdd.Body.Instructions | Where-Object {
                $_.Offset -gt $addCalls[0].Offset -and $_.Offset -lt $addCalls[1].Offset -and
                $_.OpCode.FlowControl.ToString() -eq 'Cond_Branch' -and $_.Operand -is [Mono.Cecil.Cil.Instruction] -and
                $_.Operand.Offset -gt $addCalls[1].Offset
            })
            if ($spawnGuards.Count -ne 1) { throw 'Upgrade World locations_add no longer conditionally skips SpawnLocations.' }
            $exploredFields = @($locationsAdd.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.DeclaringType.FullName -eq 'UpgradeWorld.FiltererParameters' -and
                $_.Operand.Name -eq 'Explored' -and $_.Operand.FieldType.FullName -eq 'System.Nullable`1<System.Boolean>'
            })
            if ($exploredFields.Count -eq 0) { throw 'Upgrade World locations_add explored condition changed.' }

            foreach ($direct in @(@('LocationsFixCommand', 'FixLocations', 1), @('LocationsSwapCommand', 'SwapLocations', 1),
                    @('LocationRegisterCommand', 'RegisterLocation', 1), @('TimeChangeCommand', 'ChangeTime', 2), @('TimeSetCommand', 'SetTime', 2))) {
                $handlers = @(Get-CommandHandlers $direct[0])
                if ($handlers.Count -ne $direct[2]) { throw "Upgrade World immediate command delegate count changed: $($direct[0])" }
                foreach ($handler in $handlers) {
                    Require-OperationConstructors $handler @($direct[1])
                    if (@($handler.Body.Instructions | Where-Object {
                        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'UpgradeWorld.Executor' -and $_.Operand.Name -eq 'AddOperation'
                    }).Count -ne 0) { throw "Upgrade World immediate command became queued: $($direct[0])" }
                }
            }
            if ($ExpectedUnsupportedMember) { throw "Expected unsupported contract '$ExpectedUnsupportedMember', but the reference passed." }
            Write-Host 'PASS: actual Upgrade World DLL current Init()/ExecutionFailed lifecycle, 19 queued-operation types, 13 synchronous constructors, queued cleanup delegates and optional locations_add spawn (metadata/selected IL only; plugin not executed; no version/MVID/hash gate).'
        }
        catch {
            if ($ExpectedUnsupportedMember -and $_.Exception.Message.StartsWith('Upgrade World contract method mismatch: ' + $ExpectedUnsupportedMember + ';', [StringComparison]::Ordinal)) {
                Write-Host ("PASS: incompatible reference rejected by required member, not version: " + $_.Exception.Message)
            }
            else { throw }
        }
        finally { $assembly.Dispose() }
    }
    elseif ($MetadataOnly -or $ExpectedUnsupportedMember) { throw "Upgrade World reference not found: $UpgradeWorldPath" }
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
