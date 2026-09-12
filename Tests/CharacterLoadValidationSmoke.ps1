param([string]$Configuration = 'Debug', [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    & (Get-Command pwsh -ErrorAction Stop).Source -NoProfile -File $PSCommandPath -Configuration $Configuration -GamePath $GamePath
    if ($LASTEXITCODE -ne 0) { throw 'Character load validation failed.' }
    exit 0
}
$root = Split-Path -Parent $PSScriptRoot
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx/core/Mono.Cecil.dll')) | Out-Null
$dll = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root "bin/$Configuration/ServerManager.dll"))
function Method([string]$type, [string]$name) { $dll.MainModule.GetType("ServerManager.$type").Methods | Where-Object Name -eq $name | Select-Object -First 1 }
function Call($method, [string]$name) {
    $call = $method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $name } | Select-Object -First 1
    if ($null -eq $call) { throw "Missing $name in $($method.FullName)." }
    return $call
}
function Assert([bool]$value, [string]$reason) { if (-not $value) { throw $reason } }
try {
    $prefix = Method 'ManagedCharacterPlayerLoadPatch' 'Prefix'
    $finalizer = Method 'ManagedCharacterPlayerLoadPatch' 'Finalizer'
    Assert ($null -eq $dll.MainModule.GetType('ServerManager.CharacterLoadValidation')) 'Content comparison must not remain in the deployed DLL.'
    Assert ((Call $prefix 'ShouldValidatePlayerLoad').Offset -lt (Call $prefix 'BeforeLoad').Offset) 'Scope the managed load before adapter initialization.'
    foreach ($method in @($prefix, $finalizer)) {
        Assert (@($method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -in @('GetItemPrefab', 'GetInventory', 'GetSkills', 'ReadPlayerLoadSections', 'Validate', 'RequireAvailablePrefabs') }).Count -eq 0) 'Load guards must not revalidate restored content or the prefab catalog.'
        $null = Call $method 'RejectUnsafePlayerLoad'
        $null = Call $method 'IsFatal'
    }
    $null = Call $finalizer 'AfterLoad'
    $null = Call $finalizer 'ShouldValidatePlayerLoad'
    $cleanup = Call $finalizer 'AfterPlayerLoad'
    Assert (@($finalizer.Body.ExceptionHandlers | Where-Object { $_.HandlerType.ToString() -eq 'Finally' -and $_.HandlerStart.Offset -le $cleanup.Offset -and ($null -eq $_.HandlerEnd -or $cleanup.Offset -lt $_.HandlerEnd.Offset) }).Count -eq 1) 'Load suppression must always unwind.'
    foreach ($type in @('UnsafeCharacterProfileSavePatch', 'UnsafeCharacterCapturePatch', 'ManagedCharacterPlayerSavePatch')) {
        $null = Call (Method $type 'Prefix') 'get_CharacterLoadSaveSuppressed'
    }
    $reject = Method 'ServerManagerRuntime' 'RejectUnsafePlayerLoad'
    $latch = $reject.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -eq '_unsafePlayerLoadGame' } | Select-Object -First 1
    Assert ($null -ne $latch -and $latch.Offset -lt (Call $reject 'Close').Offset -and $latch.Offset -lt (Call $reject 'FailClient').Offset) 'Latch saves before either disconnect path.'
    $null = Call (Method 'ServerManagerRuntime' 'ShouldValidatePlayerLoad') 'get_CharacterLoadSaveBlocked'
    $null = Call (Method 'ServerManagerRuntime' 'get_CharacterLoadSaveSuppressed') 'get_IsPlayerLoadInProgress'
    $reset = Method 'ServerManagerRuntime' 'ResetLocalHostLifecycle'
    Assert (@($reset.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -eq '_unsafePlayerLoadGame' }).Count -eq 0) 'Host teardown must not clear the unsafe old Game latch.'
    Write-Host 'PASS: Character load/save patch ordering and lifecycle metadata.'
} finally { $dll.Dispose() }
