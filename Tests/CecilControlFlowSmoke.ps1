param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
. (Join-Path $PSScriptRoot 'CecilControlFlow.ps1')
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    ++$script:assertions
    if (-not $Condition) { throw $Message }
}

# Serialize each tiny fixture in memory so Cecil assigns real, unique offsets.
# The graph deliberately has both paths; only the propagated value rules one out.
foreach ($shape in @('short-local', 'long-local', 'negated', 'unknown-call', 'ref-local', 'unsupported')) {
    foreach ($constant in @(0, 1)) {
        $module = [Mono.Cecil.ModuleDefinition]::CreateModule('FlowFixture', [Mono.Cecil.ModuleKind]::Dll)
        $type = [Mono.Cecil.TypeDefinition]::new('', 'Fixture', [Mono.Cecil.TypeAttributes]::Public, $module.TypeSystem.Object)
        $module.Types.Add($type)
        $method = [Mono.Cecil.MethodDefinition]::new('Probe', [Mono.Cecil.MethodAttributes]'Public,Static', $module.TypeSystem.Void)
        $type.Methods.Add($method)
        $method.Body.InitLocals = $true
        for ($index = 0; $index -lt 5; ++$index) {
            $method.Body.Variables.Add([Mono.Cecil.Cil.VariableDefinition]::new($module.TypeSystem.Int32))
        }
        $variable = $method.Body.Variables[4]
        $il = $method.Body.GetILProcessor()
        $target = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Nop)
        $done = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret)
        $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldc_I4, [int]$constant)
        if ($shape -eq 'negated') {
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ceq)
        }
        if ($shape -eq 'unsupported') {
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Xor)
        }
        if ($shape -eq 'unknown-call') {
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Pop)
            $unknown = [Mono.Cecil.MethodDefinition]::new('Unknown', [Mono.Cecil.MethodAttributes]'Public,Static', $module.TypeSystem.Boolean)
            $type.Methods.Add($unknown)
            $unknown.Body.GetILProcessor().Emit([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0)
            $unknown.Body.GetILProcessor().Emit([Mono.Cecil.Cil.OpCodes]::Ret)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Call, $unknown)
        }
        if ($shape -eq 'short-local') {
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Stloc_0)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Nop)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldloc_0)
        }
        else {
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Stloc_S, $variable)
            if ($shape -eq 'ref-local') {
                $unknown = [Mono.Cecil.MethodDefinition]::new('UnknownRef', [Mono.Cecil.MethodAttributes]'Public,Static', $module.TypeSystem.Void)
                $unknown.Parameters.Add([Mono.Cecil.ParameterDefinition]::new([Mono.Cecil.ByReferenceType]::new($module.TypeSystem.Int32)))
                $type.Methods.Add($unknown)
                $unknown.Body.GetILProcessor().Emit([Mono.Cecil.Cil.OpCodes]::Ret)
                $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldloca_S, $variable)
                $il.Emit([Mono.Cecil.Cil.OpCodes]::Call, $unknown)
            }
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldloc_S, $variable)
        }
        $branchCode = if ($shape -eq 'short-local') { [Mono.Cecil.Cil.OpCodes]::Brtrue_S } else { [Mono.Cecil.Cil.OpCodes]::Brtrue }
        $il.Emit($branchCode, $target)
        $il.Emit([Mono.Cecil.Cil.OpCodes]::Br, $done)
        $il.Append($target)
        $il.Append($done)
        $targetIndex = $method.Body.Instructions.IndexOf($target)
        $stream = [IO.MemoryStream]::new()
        $loaded = $null
        try {
            $module.Write($stream)
            $stream.Position = 0
            $loaded = [Mono.Cecil.ModuleDefinition]::ReadModule($stream)
            $probe = $loaded.GetType('Fixture').Methods | Where-Object Name -eq 'Probe' | Select-Object -First 1
            $effect = $probe.Body.Instructions[$targetIndex]
            $expected = if ($shape -in @('unknown-call', 'ref-local', 'unsupported')) { $true }
                elseif ($shape -eq 'negated') { $constant -eq 0 } else { $constant -ne 0 }
            Assert-True ((Test-CecilReachable $probe.Body.Instructions[0] $effect) -eq $expected) `
                "Incorrect $shape reachability for constant $constant."
            Assert-True (-not (Test-CecilReachable $probe.Body.Instructions[0] $effect @($effect))) `
                'Blocked instructions must remain excluded even when their path is otherwise reachable.'
        }
        finally {
            if ($null -ne $loaded) { $loaded.Dispose() }
            $stream.Dispose()
            $module.Dispose()
        }
    }
}

# Mutation controls use the three real compiled guard paths that failed in Debug.
# Redirect the rejecting edge to the allowing edge, then require rejection of that
# broken contract. Only in-memory Cecil operands change; no DLL is written/run.
$pluginPath = Join-Path (Split-Path -Parent $PSScriptRoot) "bin\$Configuration\ServerManager.dll"
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
try {
    $runtime = $assembly.MainModule.GetType('ServerManager.ServerManagerRuntime')
    $validator = $runtime.NestedTypes | Where-Object Name -eq 'RuntimeManifestValidator' | Select-Object -First 1
    foreach ($case in @(
        @{ Type = $runtime; Method = 'RefreshServerPlayerLimitAdvertisement'; Guard = 'IsServer'; Effect = 'SetMaxPlayerCount' },
        @{ Type = $runtime; Method = 'RestoreClientProfile'; Guard = 'get_ReadyAcknowledgementSent'; Effect = 'SetGamePlayerProfile' },
        @{ Type = $validator; Method = 'HandleLibraryUpdate'; Guard = 'TryGetSnapshot'; Effect = 'ValidateLibraryUpdate' }
    )) {
        $method = $case.Type.Methods | Where-Object Name -eq $case.Method | Select-Object -First 1
        $calls = @($method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
        $guard = $calls | Where-Object { $_.Operand.Name -eq $case.Guard } | Select-Object -First 1
        $effect = $calls | Where-Object { $_.Operand.Name -eq $case.Effect } | Select-Object -First 1
        Assert-True ($null -ne $guard -and $null -ne $effect) "Missing mutation target $($case.Method)."
        $branch = $guard.Next
        while ($null -ne $branch -and $branch.Offset -lt $effect.Offset -and
            $branch.OpCode.FlowControl.ToString() -ne 'Cond_Branch') { $branch = $branch.Next }
        Assert-True ($null -ne $branch -and $branch.Operand -is [Mono.Cecil.Cil.Instruction]) 'Missing guard branch.'
        $contract = {
            -not (Test-CecilReachable $method.Body.Instructions[0] $effect @($branch)) -and
            -not (Test-CecilReachable $branch.Operand $effect) -and
            (Test-CecilReachable $branch.Next $effect)
        }
        Assert-True (& $contract) "Unmodified $($case.Method) guard is not preserved."
        $originalTarget = $branch.Operand
        try {
            $branch.Operand = $branch.Next
            Assert-True (-not (& $contract)) "A bypassed $($case.Method) guard escaped the test."
        }
        finally { $branch.Operand = $originalTarget }
        Assert-True (& $contract) "Mutation restoration changed $($case.Method)."
    }
}
finally { $assembly.Dispose() }
Write-Output "Cecil control flow passed: Boolean/local propagation, conservative unknowns and three compiled guard mutations ($script:assertions assertions)."
