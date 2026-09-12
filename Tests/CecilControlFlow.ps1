# Normal control-flow reachability with bounded integer/Boolean value tracking.
# Calls, fields and unsupported values stay unknown; this does not model exceptions
# or prove runtime compatibility. Fail visibly if state exploration exceeds its limit.
function Test-CecilReachable($Start, $Target, [object[]]$Blocked = @()) {
    if ($null -eq $Start -or $null -eq $Target) { return $false }
    $pending = [Collections.Generic.Queue[object]]::new()
    $visited = @{}
    $blockedOffsets = @{}
    foreach ($instruction in $Blocked) { if ($null -ne $instruction) { $blockedOffsets[$instruction.Offset] = $true } }
    # Track only integer constants/Boolean locals. All environment results and
    # unsupported operations stay unknown, so their branches remain reachable.
    # This rejects impossible Debug paths such as true -> stloc -> brfalse.
    $pending.Enqueue(@{ Instruction = $Start; Stack = @(); Locals = @{} })
    while ($pending.Count -gt 0) {
        $state = $pending.Dequeue()
        $instruction = $state.Instruction
        if ($blockedOffsets.ContainsKey($instruction.Offset)) { continue }
        $localKey = @($state.Locals.Keys | Sort-Object | ForEach-Object { "$_=$($state.Locals[$_])" }) -join ','
        $key = "$($instruction.Offset)|$($state.Stack -join ',')|$localKey"
        if ($visited.ContainsKey($key)) { continue }
        if ($instruction.Offset -eq $Target.Offset) { return $true }
        $visited[$key] = $true
        if ($visited.Count -gt 20000) {
            throw 'Cecil reachability exceeded its state budget; review the path instead of treating it as unreachable.'
        }
        $flow = $instruction.OpCode.FlowControl.ToString()
        if ($flow -in @('Return', 'Throw')) { continue }
        $code = $instruction.OpCode.Name
        $stack = [Collections.Generic.List[string]]::new()
        foreach ($value in $state.Stack) { $stack.Add($value) }
        $locals = $state.Locals.Clone()
        $popKind = $instruction.OpCode.StackBehaviourPop.ToString()
        $popCount = if ($popKind -eq 'Pop0') { 0 } elseif ($popKind -eq 'Varpop') {
            $instruction.Operand.Parameters.Count + [int]($instruction.Operand.HasThis -and $code -ne 'newobj')
        } else { ($popKind -split '_').Count }
        $popped = @()
        for ($index = 0; $index -lt $popCount; ++$index) {
            if ($stack.Count -gt 0) {
                $popped += $stack[$stack.Count - 1]
                $stack.RemoveAt($stack.Count - 1)
            } else { $popped += '?' }
        }
        $value = '?'
        if ($code -match '^ldc\.i4(?:\.(m1|[0-8]|s))?$') {
            $suffix = $Matches[1]
            $value = if ($suffix -eq 'm1') { '-1' } elseif ($suffix -match '^[0-8]$') {
                $suffix
            } else { [string]$instruction.Operand }
        }
        elseif ($code -match '^(ldloc|stloc)(?:\.([0-3]|s))?$') {
            $operation = $Matches[1]; $suffix = $Matches[2]
            $localIndex = if ($suffix -match '^[0-3]$') { [int]$suffix } else { $instruction.Operand.Index }
            if ($operation -eq 'stloc') {
                if ($popped[0] -eq '?') { $locals.Remove($localIndex) } else { $locals[$localIndex] = $popped[0] }
            } elseif ($locals.ContainsKey($localIndex)) { $value = $locals[$localIndex] }
        }
        elseif ($code -in @('ldloca', 'ldloca.s')) {
            # An address can be passed to an out/ref parameter or stored through.
            $locals.Remove($instruction.Operand.Index)
        }
        elseif ($code -eq 'dup') { $value = $popped[0] }
        elseif ($code -in @('ceq', 'cgt', 'cgt.un', 'clt', 'clt.un') -and '?' -notin $popped) {
            $right = [long]$popped[0]; $left = [long]$popped[1]
            if (-not $code.EndsWith('.un') -or ($left -ge 0 -and $right -ge 0)) {
                $comparison = switch ($code) {
                    'ceq' { $left -eq $right }
                    { $_ -in @('cgt', 'cgt.un') } { $left -gt $right }
                    { $_ -in @('clt', 'clt.un') } { $left -lt $right }
                }
                $value = [string][int]$comparison
            }
        }
        $pushKind = $instruction.OpCode.StackBehaviourPush.ToString()
        $pushCount = if ($pushKind -eq 'Push0') { 0 } elseif ($pushKind -eq 'Varpush') {
            [int]($code -eq 'newobj' -or $instruction.Operand.ReturnType.FullName -ne 'System.Void')
        } else { ($pushKind -split '_').Count }
        for ($index = 0; $index -lt $pushCount; ++$index) { $stack.Add($value) }
        if ($code -in @('leave', 'leave.s')) { $stack.Clear() }
        $successors = @()
        if ($flow -in @('Branch', 'Cond_Branch')) {
            $successors += @($instruction.Operand)
        }
        if ($flow -ne 'Branch') { $successors += $instruction.Next }
        if ($code -in @('brtrue', 'brtrue.s', 'brfalse', 'brfalse.s') -and $popped[0] -ne '?') {
            $takeBranch = ([long]$popped[0] -ne 0) -eq $code.StartsWith('brtrue')
            $successors = @(if ($takeBranch) { $instruction.Operand } else { $instruction.Next })
        }
        foreach ($successor in $successors) {
            if ($null -ne $successor) {
                $pending.Enqueue(@{ Instruction = $successor; Stack = $stack.ToArray(); Locals = $locals })
            }
        }
    }
    return $false
}
