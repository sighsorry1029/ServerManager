param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$ManagedPath = (Join-Path $GamePath 'valheim_Data/Managed')
)
$ErrorActionPreference = 'Stop'
# Run in a fresh Windows PowerShell process. Read the real worker via Harmony;
# do not install patches, invoke Unity or perform any world/backup file I/O.
$bin = Join-Path (Split-Path -Parent $PSScriptRoot) "bin/$Configuration"
$dependencies = @{}
$resolve = [ResolveEventHandler]{ param($sender,$e) return $dependencies[$e.Name] }
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolve)
try {
    foreach ($name in 'Mono.Cecil','MonoMod.Utils','MonoMod.RuntimeDetour','0Harmony','BepInEx') {
        $a = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GamePath "BepInEx/core/$name.dll")))
        $dependencies[$a.FullName] = $a
    }
    foreach ($name in 'UnityEngine.CoreModule','assembly_utils','SoftReferenceableAssets',
        'com.rlabrecque.steamworks.net','Splatform','assembly_valheim') {
        $game = [Reflection.Assembly]::LoadFrom((Join-Path $ManagedPath "$name.dll"))
        $dependencies[$game.FullName] = $game
    }
    $plugin = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $bin 'ServerManager.dll')))
    $patch = $plugin.GetType('ServerManager.VerifiedWorldSaveWorkerPatch',$true).GetMethod('Transpiler',[Reflection.BindingFlags]'NonPublic,Static')
    $worker = $game.GetType('ZNet',$true).GetMethod('SaveWorldThread',[Reflection.BindingFlags]'NonPublic,Instance')
    $gen = [HarmonyLib.PatchProcessor]::CreateILGenerator($worker)
    $reader = [HarmonyLib.PatchProcessor].GetMethods() | Where-Object {
        $_.Name -eq 'GetOriginalInstructions' -and $_.GetParameters().Count -eq 2 -and !$_.GetParameters()[1].ParameterType.IsByRef
    }
    $original = $reader.Invoke($null,[object[]]@($worker,$gen))
    $il = @($patch.Invoke($null,[object[]](,$original)))
    $calls = @($il | Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand.Name -eq 'SetWorldSaveWorkerSuccess' })
    if ($calls.Count -ne 2) { throw 'Expected a commit and a rollback observation.' }
    $yes = [Array]::IndexOf($il,$calls[0])
    $no = [Array]::IndexOf($il,$calls[1])
    if ($il[$yes-3].opcode.Name -ne 'ldc.i4.1' -or $il[$yes-2].operand.DeclaringType.Name -ne 'SaveSystem' -or
        $il[$yes-2].operand.Name -ne 'EndSave' -or $il[$yes-1].opcode.Name -ne 'ldc.i4.1') {
        throw 'The success marker is not on the return edge of EndSave(true).'
    }
    $catchStore = $il[$no-2]
    if ($catchStore.opcode.Name -notlike 'stloc*' -or
        !@($catchStore.blocks | Where-Object { $_.blockType -eq 'BeginCatchBlock' -and $_.catchType -eq [Exception] }).Count -or
        $il[$no-1].opcode.Name -ne 'ldc.i4.0' -or $il[$no+1].operand -cne 'Error saving world! ') {
        throw 'The rollback handler must revoke success before logging or file cleanup can throw.'
    }
    # Removing the four observations must yield exactly the original instructions,
    # including the branches for skipped backups, failed writes, and the finally.
    $vanilla = @($il[0..($yes-2)] + $il[($yes+1)..($no-2)] + $il[($no+1)..($il.Count-1)])
    if ($vanilla.Count -ne $original.Count) { throw 'Unexpected worker instructions.' }
    for ($i=0; $i -lt $vanilla.Count; $i++) {
        if (![object]::ReferenceEquals($vanilla[$i],$original[$i])) { throw "Replaced vanilla instruction $i." }
    }
    # A handler without vanilla's rollback contract must not be mistaken for
    # a known outcome just because its error log still looks the same.
    $rollback = @($original | Select-Object -Skip ($original.IndexOf($catchStore) + 1) |
        Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand.DeclaringType.Name -eq 'SaveSystem' -and $_.operand.Name -eq 'EndSave' })
    if ($rollback.Count -ne 1) { throw 'Unexpected native rollback contract.' }
    $rollback[0].opcode = [Reflection.Emit.OpCodes]::Nop
    $rollback[0].operand = $null
    try {
        $null = $patch.Invoke($null,[object[]](,$original))
        throw 'Accepted an unverified rollback handler.'
    } catch {
        if ($_.Exception.GetBaseException().Message -notlike '*Refusing to apply the checkpoint patch*') { throw }
    }

    # Exercise the actual markers on a bound attempt in this thread, without
    # enqueueing it: normal primary failure, success (including no backup), then
    # a caught failure after commit.
    $runtime = $plugin.GetType('ServerManager.ServerManagerRuntime',$true)
    $active = $runtime.GetField('_activeWorldSaveWorkerAttempt','NonPublic,Static')
    $prop = $active.FieldType.GetProperty('PrimaryWorldSaveSucceeded',[Reflection.BindingFlags]'NonPublic,Instance')
    $ctor = $active.FieldType.GetConstructor('NonPublic,Instance',$null,[type[]]@([string]),$null)
    $attempt = $ctor.Invoke(@('test'))
    try {
        $active.SetValue($null,$attempt)
        if ($prop.GetValue($attempt)) { throw 'Ordinary failed saves would be committed.' }
        foreach ($offset in @($yes,$no)) {
            $expected = $offset -eq $yes
            $il[$offset].operand.Invoke($null,[object[]]@($il[$offset-1].opcode.Name -eq 'ldc.i4.1'))
            if ($prop.GetValue($attempt) -ne $expected) { throw "Wrong checkpoint result: expected $expected." }
        }
    } finally { $active.SetValue($null,$null) }
    Write-Output "PASS: original $($game.ManifestModule.ModuleVersionId) commit/rollback boundaries and checkpoint state; no live save performed."
} finally { [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolve) }
