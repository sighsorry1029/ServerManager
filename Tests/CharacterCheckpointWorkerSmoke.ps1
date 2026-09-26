param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [switch]$StaticOnly
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    & (Get-Command pwsh -ErrorAction Stop).Source -NoProfile -File $PSCommandPath @PSBoundParameters
    if ($LASTEXITCODE -ne 0) { throw 'Checkpoint worker fixture failed.' }
    exit 0
}
$workerRoot = Split-Path -Parent $PSScriptRoot
$completionSource = Get-Content -LiteralPath (Join-Path $workerRoot 'Networking/ServerManagerRuntime.cs') -Raw
$completionStart = $completionSource.IndexOf('    private static void RecordCharacterCheckpointOutcome(', [StringComparison]::Ordinal)
$completionEnd = $completionSource.IndexOf('    private static void RegisterCharacterCheckpointFailure(', $completionStart, [StringComparison]::Ordinal)
if ($completionStart -lt 0 -or $completionEnd -le $completionStart) { throw 'Cannot locate production checkpoint completion methods.' }
$completionHarness = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CharacterCheckpointCompletionProbe.cs') -Raw
Add-Type -TypeDefinition $completionHarness.Replace('// SOURCE_LINKED_CHECKPOINT_COMPLETION',
    $completionSource.Substring($completionStart, $completionEnd - $completionStart))
[CharacterCheckpointCompletionProbe]::Verify()
Write-Output 'PASS: source-linked checkpoint publication waits for real outcomes, reports partial failure, and handles coalesced revisions/shutdown.'
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx/core/Mono.Cecil.dll')) | Out-Null
$workerAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $workerRoot "bin/$Configuration/ServerManager.dll"))
try {
    $workerRepository = $workerAssembly.MainModule.Types | Where-Object FullName -eq 'ServerManager.CharacterRepository'
    $workerMethod = $workerRepository.Methods | Where-Object Name -eq 'PersistCheckpointEntryOnWorker'
    if ($null -eq $workerMethod) { throw 'Missing pure checkpoint file worker.' }
    $workerQueue = [Collections.Generic.Queue[object]]::new()
    $workerVisited = [Collections.Generic.HashSet[string]]::new()
    $workerQueue.Enqueue($workerMethod)
    while ($workerQueue.Count -gt 0) {
        $workerCurrent = $workerQueue.Dequeue()
        if (-not $workerVisited.Add($workerCurrent.FullName) -or -not $workerCurrent.HasBody) { continue }
        foreach ($workerInstruction in $workerCurrent.Body.Instructions) {
            if ($workerInstruction.Operand -isnot [Mono.Cecil.MethodReference]) { continue }
            $workerReference = $workerInstruction.Operand
            $workerOwner = $workerReference.DeclaringType.FullName
            if ($workerOwner -match '^(UnityEngine\.|BepInEx\.|Player$|PlayerProfile|Game$|ZNet|ZRpc|ZLog|ZPackage|ServerManager\.(ValheimPlayerProfileCodec|ServerManagerPlugin|ServerEventRuntime))') {
                throw "Checkpoint worker reaches a game/thread-affine boundary: $($workerReference.FullName)"
            }
            if ($workerOwner.StartsWith('ServerManager.', [StringComparison]::Ordinal)) {
                $workerDefinition = $workerReference.Resolve()
                if ($null -ne $workerDefinition -and $workerDefinition.Module -eq $workerAssembly.MainModule) {
                    $workerQueue.Enqueue($workerDefinition)
                }
            }
        }
    }
    Write-Output "PASS: checkpoint file worker's $($workerVisited.Count) production methods have no game/Unity callbacks."
}
finally { $workerAssembly.Dispose() }
if ($StaticOnly) { return }
# Reuse the verified native-profile fixtures and temporary storage lifecycle.
# That suite invokes CharacterCheckpointWorkerProbe against the built plugin;
# it never changes a live world, character, or server configuration.
& (Join-Path $PSScriptRoot 'CharacterShadowCheckpointSmoke.ps1') -Configuration $Configuration -GamePath $GamePath
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Checkpoint worker fixture failed.' }
