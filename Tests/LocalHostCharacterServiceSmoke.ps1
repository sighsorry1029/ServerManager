param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null }
    catch {
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        Assert-True ($failure.Message -like $Pattern) (
            "Expected '$Pattern', got '$($failure.GetType().Name): $($failure.Message)'.")
        return
    }
    throw "Expected failure '$Pattern'."
}

$allInstance = [Reflection.BindingFlags]::Instance -bor
    [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
function Invoke-Hidden([object]$Instance, [string]$Name, [object[]]$Arguments = @()) {
    $method = $Instance.GetType().GetMethods($script:allInstance) |
        Where-Object { $_.Name -eq $Name -and $_.GetParameters().Count -eq $Arguments.Count } |
        Select-Object -First 1
    Assert-True ($null -ne $method) "Missing method $Name."
    $converted = [object[]]::new($Arguments.Count)
    $parameters = $method.GetParameters()
    for ($index = 0; $index -lt $Arguments.Count; ++$index) {
        $type = $parameters[$index].ParameterType
        if ($type.IsByRef) { $type = $type.GetElementType() }
        $converted[$index] = if ($null -eq $Arguments[$index]) { $null } else {
            [Management.Automation.LanguagePrimitives]::ConvertTo($Arguments[$index], $type)
        }
    }
    $result = $method.Invoke($Instance, $converted)
    for ($index = 0; $index -lt $Arguments.Count; ++$index) {
        if ($parameters[$index].ParameterType.IsByRef) { $Arguments[$index] = $converted[$index] }
    }
    return ,$result
}

function New-Instance([string]$Name, [object[]]$Arguments = @()) {
    $type = $script:plugin.GetType("ServerManager.$Name", $true)
    $ctor = $type.GetConstructors($script:allInstance) |
        Where-Object { $_.GetParameters().Count -eq $Arguments.Count } |
        Select-Object -First 1
    Assert-True ($null -ne $ctor) "Missing constructor $Name."
    $converted = [object[]]::new($Arguments.Count)
    $parameters = $ctor.GetParameters()
    for ($index = 0; $index -lt $Arguments.Count; ++$index) {
        $converted[$index] = [Management.Automation.LanguagePrimitives]::ConvertTo(
            $Arguments[$index], $parameters[$index].ParameterType)
    }
    return $ctor.Invoke($converted)
}

function Get-Hidden([object]$Instance, [string]$Name) {
    return ,($Instance.GetType().GetProperty($Name, $script:allInstance).GetValue($Instance))
}

function Test-Bytes([byte[]]$Left, [byte[]]$Right) {
    return [Convert]::ToBase64String($Left) -ceq [Convert]::ToBase64String($Right)
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$binaryRoot = Join-Path $projectRoot "bin\$Configuration"
$managedRoot = Join-Path $GamePath "valheim_Data\Managed"
$pluginPath = Join-Path $binaryRoot "ServerManager.dll"
Assert-True (Test-Path -LiteralPath $pluginPath) "Build ServerManager first."
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll")) | Out-Null
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((
    Join-Path $GamePath "BepInEx\core\BepInEx.dll"))) | Out-Null
foreach ($name in @("netstandard.dll", "com.rlabrecque.steamworks.net.dll", "Splatform.dll")) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($managedRoot)
$resolver.AddSearchDirectory((Join-Path $GamePath "BepInEx\core"))
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver

function Load-TestDependency([string]$Path, [scriptblock]$Patch) {
    $definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path, $script:reader)
    $stream = [IO.MemoryStream]::new()
    try {
        & $Patch $definition
        $definition.Write($stream)
        return [Reflection.Assembly]::Load($stream.ToArray())
    }
    finally { $stream.Dispose(); $definition.Dispose() }
}

# No game files are changed. Match the native storage fixture: avoid
# Unity-native Object initialization and UID generation in this headless process.
Load-TestDependency (Join-Path $binaryRoot "UnityEngine.CoreModule.dll") {
    param($definition)
    $type = $definition.MainModule.Types | Where-Object FullName -eq "UnityEngine.Object"
    $method = $type.Methods | Where-Object Name -eq ".cctor" | Select-Object -First 1
    Assert-True ($null -ne $method -and $method.HasBody) "Unity Object initializer changed."
    $method.Body.Instructions.Clear()
    $method.Body.ExceptionHandlers.Clear()
    $method.Body.Variables.Clear()
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
} | Out-Null
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $binaryRoot "UnityEngine.dll"))) | Out-Null
Load-TestDependency (Join-Path $managedRoot "assembly_utils.dll") {
    param($definition)
    $type = $definition.MainModule.Types | Where-Object FullName -eq "Utils"
    $method = $type.Methods | Where-Object {
        $_.Name -eq "GenerateUID" -and $_.Parameters.Count -eq 0
    } | Select-Object -First 1
    Assert-True ($null -ne $method -and $method.HasBody) "GenerateUID changed."
    $method.Body.Instructions.Clear()
    $method.Body.ExceptionHandlers.Clear()
    $method.Body.Variables.Clear()
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create(
        [Mono.Cecil.Cil.OpCodes]::Ldc_I8, [long]76561198012345678))
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
} | Out-Null
$game = Load-TestDependency (Join-Path $managedRoot "assembly_valheim.dll") {
    param($definition)
    $version = $definition.MainModule.Types | Where-Object FullName -eq "Version"
    $field = $version.Fields | Where-Object Name -eq "m_playerVersion"
    Assert-True ($null -ne $field -and $field.HasConstant) "PlayerProfile version marker changed."
    # Test the explicitly supported v43 schema, not compatibility with another game version.
    $field.Constant = [int]43
}
# CoreCLR isolates byte-loaded assemblies; resolve only matching images that
# this fixture has already loaded, preserving the in-memory native-call shims.
$fixtureAssemblyResolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    foreach ($loaded in [AppDomain]::CurrentDomain.GetAssemblies()) {
        if ($loaded.FullName -eq $eventArgs.Name) { return $loaded }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($fixtureAssemblyResolver)
# Only this lifecycle fixture replaces the Unity-bound starter factory with a
# deterministic, materialized empty-inventory profile. Repository/session/origin,
# validation, reload and checkpoint code remain the actual compiled production
# methods. CharacterStarterItemsSmoke separately executes the real builder source.
# This modified image exists only in memory; no plugin or game file is rewritten.
$plugin = Load-TestDependency $pluginPath {
    param($definition)
    $type = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ValheimPlayerProfileCodec'
    $method = $type.Methods | Where-Object Name -eq 'CreateInitialProfileBytes' | Select-Object -First 1
    Assert-True ($null -ne $method -and $method.Parameters.Count -eq 2) 'Starter factory test boundary changed.'
    $factoryType = $definition.MainModule.ImportReference([Func[string,byte[]]])
    $factory = [Mono.Cecil.FieldDefinition]::new('__TestInitialProfileFactory',
        [Enum]::Parse([Mono.Cecil.FieldAttributes], 'Public, Static'), $factoryType)
    $type.Fields.Add($factory)
    $invoke = $definition.MainModule.ImportReference([Func[string,byte[]]].GetMethod('Invoke'))
    $method.Body.Instructions.Clear()
    $method.Body.ExceptionHandlers.Clear()
    $method.Body.Variables.Clear()
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldsfld, $factory))
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $invoke))
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
}
$resolver.Dispose()
$envelopeType = $plugin.GetType("ServerManager.CharacterEnvelope", $true)
$kindType = $plugin.GetType("ServerManager.CharacterEnvelopeKind", $true)
$createEnvelope = $envelopeType.GetMethod("Create")

function New-Request([object]$Identity, [Guid]$SessionId, [long]$Revision,
    [long]$BaseRevision, [byte[]]$Payload, [string]$Kind = "SaveRequest") {
    return $script:createEnvelope.Invoke($null, [object[]]@(
        [Enum]::Parse($script:kindType, $Kind), $Revision, $BaseRevision,
        $SessionId, $Identity, [DateTime]::UtcNow, [int]43, $Payload))
}

function New-ProfilePayload([string]$Name, [long]$PlayerId, [string]$Seed,
    [single]$MaximumHealth = 25, [single]$Health = 25,
    [single]$MaximumStamina = 50, [single]$Stamina = 50,
    [single]$MaximumEitr = 0, [single]$Eitr = 0, [string]$ItemPrefab = "",
    [bool]$UsedCheats = $false) {
    $inner = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($inner)
    try {
        $writer.Write([int]29)
        foreach ($value in @($MaximumHealth, $Health, $MaximumStamina, [single]0)) { $writer.Write($value) }
        $writer.Write(""); $writer.Write([single]0)
        $writer.Write([int]106)
        $writer.Write([int]([bool]$ItemPrefab))
        if ($ItemPrefab) {
            $writer.Write($ItemPrefab); $writer.Write([int]1); $writer.Write([single]0)
            $writer.Write([int]0); $writer.Write([int]0); $writer.Write($false)
            $writer.Write([int]1); $writer.Write([int]0); $writer.Write([long]0)
            $writer.Write(""); $writer.Write([int]0); $writer.Write([int]0); $writer.Write($false)
        }
        for ($index = 0; $index -lt 8; ++$index) { $writer.Write([int]0) }
        $writer.Write(""); $writer.Write("")
        for ($index = 0; $index -lt 6; ++$index) { $writer.Write([single]0) }
        $writer.Write([int]0); $writer.Write([int]0)
        $writer.Write([int]2); $writer.Write([int]0); $writer.Write([int]0)
        $writer.Write($Stamina); $writer.Write($MaximumEitr); $writer.Write($Eitr)
        $writer.Flush()
        [byte[]]$innerBytes = $inner.ToArray()
    }
    finally { $writer.Dispose(); $inner.Dispose() }
    $outer = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($outer)
    try {
        $writer.Write([int]43); $writer.Write([int]105)
        for ($index = 0; $index -lt 105; ++$index) { $writer.Write([single]0) }
        $writer.Write($false); $writer.Write([int]0)
        $writer.Write($Name); $writer.Write($PlayerId); $writer.Write($Seed)
        $writer.Write($UsedCheats); $writer.Write([long]0)
        for ($index = 0; $index -lt 6; ++$index) { $writer.Write([int]0) }
        $writer.Write($true); $writer.Write([int]$innerBytes.Length); $writer.Write($innerBytes)
        $writer.Flush()
        return ,($outer.ToArray())
    }
    finally { $writer.Dispose(); $outer.Dispose() }
}

$initialProfileFixture = [Func[string,byte[]]] {
    param($name)
    return [byte[]](New-ProfilePayload $name ([long]76561198012345678) 'initial-fixture')
}
$plugin.GetType('ServerManager.ValheimPlayerProfileCodec').GetField('__TestInitialProfileFactory').SetValue($null, $initialProfileFixture)

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("smlocal-" + [Guid]::NewGuid().ToString("N"))
$service = $null
$keys = $null
try {
    $options = New-Instance "CharacterStorageOptions"
    $options.SaveRequestBurstCapacity = 100
    $layout = New-Instance "CharacterStorageLayout" @($testRoot)
    $keys = New-Instance "CharacterStorageKeyProvider" @($layout)
    $codec = New-Instance "CharacterEnvelopeCodec" @($options)
    $profiles = New-Instance "ValheimPlayerProfileCodec" @($options)
    $mode = [Enum]::Parse($plugin.GetType("ServerManager.CharacterSemanticPolicyMode"), "Enforce")
    $policy = New-Instance "CharacterSemanticPolicy" @(
        $mode, "ForbiddenSword", [single]1000, [single]1000,
        [single]1000, [single]100, [single]1000)
    $evaluator = New-Instance "CharacterSemanticEvaluator" @($policy)
    $script:fixturePolicyAdmin = $false
    $fixtureAdminResolverType = [Func``2].MakeGenericType(
        $plugin.GetType('ServerManager.CharacterIdentity'), [bool])
    $fixtureAdminResolver = [Management.Automation.LanguagePrimitives]::ConvertTo(
        [scriptblock]{ param($account) return $script:fixturePolicyAdmin }, $fixtureAdminResolverType)
    $validator = New-Instance "CharacterSemanticRevisionValidator" @($profiles, $evaluator, $fixtureAdminResolver)
    $storedMode = [Enum]::Parse($plugin.GetType("ServerManager.CharacterSemanticPolicyMode"), "Observe")
    $storedPolicy = New-Instance "CharacterSemanticPolicy" @(
        $storedMode, "ForbiddenSword", [single]1000, [single]1000,
        [single]1000, [single]100, [single]1000)
    $storedEvaluator = New-Instance "CharacterSemanticEvaluator" @($storedPolicy)
    $storedValidator = New-Instance "CharacterSemanticRevisionValidator" @($profiles, $storedEvaluator)
    $repository = New-Instance "CharacterRepository" @($layout, $options, $profiles, $validator, $null)
    $identityResolver = New-Instance "CharacterPeerIdentityResolver"
    $service = New-Instance "CharacterSnapshotService" @(
        $options, $identityResolver, $keys, $codec, $profiles, $repository, $storedValidator)
    $identity = New-Instance "CharacterIdentity" @("steamworks:76561198000000001", "HostHero")
    # Cheat usage is preserved by serialization and inventory materialization;
    # only the authoritative incoming policy may grant the admin exemption.
    [byte[]]$flaggedBytes = New-ProfilePayload 'HostHero' 76561198012345678 'cheat-fixture' -UsedCheats $true
    $profileCodecType = $plugin.GetType('ServerManager.ValheimPlayerProfileCodec')
    $extractValidated = $profileCodecType.GetMethods($allInstance) | Where-Object {
        $_.Name -eq 'ExtractValidatedSnapshot' -and $_.GetParameters().Count -eq 2
    } | Select-Object -First 1
    $deserializeProfile = $profileCodecType.GetMethod('DeserializeProfileFromBytes')
    $serializeProfile = $profileCodecType.GetMethod('SerializeProfileToBytes')
    $flaggedValidated = $extractValidated.Invoke($profiles, [object[]]@($identity, $flaggedBytes))
    Assert-True ((Get-Hidden $flaggedValidated 'SemanticSnapshot').UsedCheats) 'Outer cheat flag was not extracted.'
    $fileSourceType = $deserializeProfile.GetParameters()[2].ParameterType
    $localSource = [Enum]::Parse($fileSourceType, 'Local')
    $flaggedProfile = $deserializeProfile.Invoke($profiles, [object[]]@($flaggedBytes, $null, $localSource))
    Assert-True $flaggedProfile.m_usedCheats 'Profile decoding cleared the cheat flag.'
    [byte[]]$flaggedRoundTrip = $serializeProfile.Invoke($profiles, [object[]]@($flaggedProfile))
    Assert-True ((Get-Hidden ($extractValidated.Invoke($profiles, [object[]]@($identity, $flaggedRoundTrip))) 'SemanticSnapshot').UsedCheats) `
        'Profile serialization rejected or cleared the cheat flag.'
    [byte[]]$emptyInventory = @([BitConverter]::GetBytes([int]106) + [BitConverter]::GetBytes([int]0))
    $spliceArguments = [object[]]@($identity, $flaggedRoundTrip, $emptyInventory, $null)
    [byte[]]$flaggedSplice = $profileCodecType.GetMethod('ReplaceInventorySnapshot', $allInstance).Invoke($profiles, $spliceArguments)
    Assert-True ((Get-Hidden $spliceArguments[3] 'SemanticSnapshot').UsedCheats -and
        $deserializeProfile.Invoke($profiles, [object[]]@($flaggedSplice, $null, $localSource)).m_usedCheats) `
        'Inventory fast path cleared the retained outer cheat flag.'
    $emptyProfileForFlag = $profileCodecType.GetMethod('CreateEmptyProfile',
        [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, [object[]]@('HostHero'))
    $emptyProfileForFlag.m_usedCheats = $true
    [byte[]]$flaggedEmpty = $serializeProfile.Invoke($profiles, [object[]]@($emptyProfileForFlag))
    $emptyProfileForFlag.m_usedCheats = $false
    [byte[]]$plainEmpty = $serializeProfile.Invoke($profiles, [object[]]@($emptyProfileForFlag))
    $flaggedEmptyState = Get-Hidden ($extractValidated.Invoke($profiles, [object[]]@($identity, $flaggedEmpty))) 'SemanticSnapshot'
    $plainEmptyState = Get-Hidden ($extractValidated.Invoke($profiles, [object[]]@($identity, $plainEmpty))) 'SemanticSnapshot'
    Assert-True ($flaggedEmptyState.UsedCheats -and -not $plainEmptyState.UsedCheats -and
        -not $flaggedEmptyState.HasPlayerData -and -not $plainEmptyState.HasPlayerData) `
        'Cheat metadata mutated the shared empty semantic snapshot.'
    $otherIdentity = New-Instance "CharacterIdentity" @("steamworks:76561198000000002", "GuestHero")
    $storageKey = $keys.DeriveStorageKey($identity)
    $profilePath = $layout.GetProfilePath($storageKey)
    $pendingPath = Invoke-Hidden $layout "GetPendingProfilePath" @($storageKey)
    $invalidIdentity = New-Instance "CharacterIdentity" @("discord:123", "HostHero")
    Assert-Throws { Invoke-Hidden $service "OpenOrCreateLocalHostSession" @($invalidIdentity) } "*canonical individual Steam*"
    $opened = Invoke-Hidden $service "OpenOrCreateLocalHostSession" @($identity)
    $sessionId = $opened.Snapshot.SessionId
    $lookup = [object[]]@($sessionId, $null)
    Assert-True (Invoke-Hidden $service "TryGetLocalHostSession" $lookup) "Host session missing."
    $session = $lookup[1]
    Assert-True ($null -eq $session.Rpc -and (Get-Hidden $session "IsLocalHost")) "Host used a fake RPC."
    Assert-True ($opened.PendingInitialCommit -and -not (Test-Path -LiteralPath $profilePath)) "Open wrote a primary before finalization."
    Assert-True ($opened.Snapshot.RequiresFreshLocalCharacter) "New profile origin must survive materialized START ITEMS."
    $wireRoundTrip = $codec.Decode($codec.Encode($opened.Snapshot))
    Assert-True ($wireRoundTrip.RequiresFreshLocalCharacter) "Envelope codec lost the authoritative origin marker."
    Assert-Throws { Invoke-Hidden $service "OpenOrCreateLocalHostSession" @($otherIdentity) } "*already open*"
    Assert-True ($null -eq $service.GetType().GetMethod('ImportServerCharactersInbox', $allInstance)) 'The removed import entry point is still exposed.'
    Assert-Throws { Invoke-Hidden $service "GetLocalHostSnapshot" @($sessionId) } "*not active*"
    $early = New-Request $identity $sessionId 2 1 $opened.Snapshot.GetPayloadCopy()
    Assert-True (-not (Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $early)).Accepted) "Pending initial save accepted."
    Assert-True (Invoke-Hidden $service "FinalizePendingLocalHostSnapshot" @($sessionId)) "Initial host snapshot not finalized."
    Assert-True (-not (Invoke-Hidden $service "FinalizePendingLocalHostSnapshot" @($sessionId))) "Initial finalize was not idempotent."
    Assert-True (-not (Test-Path -LiteralPath $profilePath) -and
        (Test-Path -LiteralPath $pendingPath)) `
        "Initial host final/pending paths did not preserve the fresh-origin boundary."
    $initialStored = Invoke-Hidden $repository "Load" @($identity, $storageKey)
    Assert-True ($initialStored.Envelope.RequiresFreshLocalCharacter) "Initial disk commit lost the origin marker."
    $initialLatest = Invoke-Hidden $service "GetLocalHostSnapshot" @($sessionId)
    Assert-True ($initialLatest.RequiresFreshLocalCharacter) "Host snapshot projection lost the origin marker."
    [byte[]]$emptyProfile = $plugin.GetType('ServerManager.ValheimPlayerProfileCodec').GetMethod(
        'CreateEmptyProfileBytes').Invoke($profiles, [object[]]@($identity.CharacterName))
    $unmaterialized = New-Request $identity $sessionId 2 1 $emptyProfile
    Assert-True (-not (Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $unmaterialized)).Accepted) "Empty save must not clear initial-origin metadata."
    $prematureInventory = New-Request $identity $sessionId 2 1 ([byte[]]@(106,0,0,0,0,0,0,0)) "InventorySaveRequest"
    $prematureResult = Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $prematureInventory)
    Assert-True (-not $prematureResult.Accepted -and $prematureResult.Error -like "*accepted full save*") "Inventory-only save must not skip the first full baseline."

    # Exercise the shared remote core with an inert RPC only as a test transport
    # key; production remote admission must still resolve a real authenticated peer.
    $rpc = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($game.GetType("ZRpc", $true))
    Assert-Throws { Invoke-Hidden $service "OpenOrCreateSessionCore" @($identity, $rpc) } "*already has an active session*"
    Assert-Throws { $service.OpenOrCreateServerSession($null) } "*peer*"

    [byte[]]$payload = New-ProfilePayload "HostHero" $session.PlayerId "host-live-2" `
        -MaximumHealth 1001 -MaximumStamina 1002 -MaximumEitr 1003
    $request = New-Request $identity $sessionId 2 1 $payload
    # The first full profile must not become an authoritative final .fch if RAM
    # publication cannot be admitted. This exercises the durable-action commit
    # barrier without allocating hundreds of MiB in the headless fixture.
    $retainedBytesField = $service.GetType().GetField(
        '_retainedSnapshotPayloadBytes', $allInstance)
    $retainedBytesBeforeCapacityProbe = [long]$retainedBytesField.GetValue($service)
    try {
        $retainedBytesField.SetValue($service, [long](300 * 1024 * 1024))
        Assert-Throws {
            Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $request)
        } "*capacity*"
        Assert-True ($session.CurrentRevision -eq 1 -and
            -not (Test-Path -LiteralPath $profilePath) -and
            (Test-Path -LiteralPath $pendingPath)) `
            "A failed RAM-capacity preflight published an unacknowledged final .fch."
    }
    finally {
        $retainedBytesField.SetValue($service, $retainedBytesBeforeCapacityProbe)
    }
    $accepted = Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $request)
    Assert-True ($accepted.Accepted -and $session.CurrentRevision -eq 2) (
        "Valid host save failed to advance revision. accepted=" + $accepted.Accepted +
        ", revision=" + $session.CurrentRevision + ", error=" + $accepted.Error)
    Assert-True ((Test-Path -LiteralPath $profilePath) -and
        -not (Test-Path -LiteralPath $pendingPath) -and
        @(Get-ChildItem -LiteralPath $layout.GetAccountDirectory($storageKey) -File -Filter "$storageKey.*.fch" -ErrorAction SilentlyContinue).Count -eq 0) `
        "The first full save was ACKed without a durable pending-to-final promotion, or backed up the genesis."
    Assert-True ($accepted.StatLimitFindings.Count -eq 3 -and
        $accepted.SemanticObservations.Count -eq 0 -and
        $accepted.StatLimitFindings[0].Code -ceq "maximum_health" -and
        $accepted.StatLimitFindings[0].Value -eq 1001 -and
        $accepted.StatLimitFindings[0].Limit -eq 1000) `
        "An accepted over-cap save lost typed findings or double-reported semantic observations."
    $latest = Invoke-Hidden $service "GetLocalHostSnapshot" @($sessionId)
    Assert-True (-not $latest.RequiresFreshLocalCharacter) "Accepted full Player data did not clear initial-origin metadata."
    Assert-True ($latest.Kind.ToString() -eq "Snapshot" -and $latest.SessionId -eq $sessionId -and
        $latest.Revision -eq 2 -and $latest.BaseRevision -eq 2 -and
        (Test-Bytes $latest.GetPayloadCopy() $payload)) "Host snapshot is not the full acknowledged current generation."
    $disk = Invoke-Hidden $repository "Load" @($identity, $storageKey)
    Assert-True ($disk.Envelope.Revision -eq 1 -and
        -not $disk.Envelope.RequiresFreshLocalCharacter -and
        (Test-Bytes $disk.Envelope.GetPayloadCopy() $payload)) `
        "The first full ACK did not read back as an established vanilla .fch baseline."
    $health = Invoke-Hidden $service "GetOpenSessionSaveHealth"
    Assert-True ($health.Count -eq 1 -and (Get-Hidden $health[0] "SessionId") -eq $sessionId) "Host missing from save health."
    $warnings = Invoke-Hidden $service "ClaimLongUnsavedWarnings" @([TimeSpan]::FromTicks(1))
    Assert-True ($warnings.Count -eq 1) "Host missing from long-unsaved warnings."

    $stale = Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $request)
    Assert-True (-not $stale.Accepted -and $session.CurrentRevision -eq 2) "Stale host revision accepted."
    $wrongId = New-Request $identity $sessionId 3 2 (New-ProfilePayload "HostHero" ($session.PlayerId + 1) "wrong")
    Assert-True (-not (Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $wrongId)).Accepted) "Changed PlayerID accepted."
    $wrongIdentity = New-Request $otherIdentity $sessionId 3 2 $payload
    Assert-True (-not (Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $wrongIdentity)).Accepted) "Foreign identity accepted."
    $wrongSession = New-Request $identity ([Guid]::NewGuid()) 3 2 $payload
    Assert-True (-not (Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $wrongSession)).Accepted) "Foreign envelope session accepted."
    $latest = Invoke-Hidden $service "GetLocalHostSnapshot" @($sessionId)
    Assert-True ($latest.Revision -eq 2 -and (Test-Bytes $latest.GetPayloadCopy() $payload)) "Rejected save changed the acknowledged snapshot."

    foreach ($invalidStats in @(
        @{ MaximumHealth = [single]::NaN },
        @{ MaximumStamina = [single]::PositiveInfinity },
        @{ MaximumEitr = [single]-1 },
        @{ Health = [single]26 },
        @{ Stamina = [single]51 },
        @{ Eitr = [single]1 })) {
        [byte[]]$invalidPayload = New-ProfilePayload "HostHero" $session.PlayerId "invalid-stats" @invalidStats
        $invalidRequest = New-Request $identity $sessionId 3 2 $invalidPayload
        $invalidResult = Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $invalidRequest)
        Assert-True (-not $invalidResult.Accepted -and $session.CurrentRevision -eq 2) `
            "Allowing numeric-cap exceedances weakened structural stat validation."
    }

    # Inventory requests reuse the same revision path and expose full materialized profiles.
    [byte[]]$inventory = @([BitConverter]::GetBytes([int]106) + [BitConverter]::GetBytes([int]0))
    $inventoryRequest = New-Request $identity $sessionId 3 2 $inventory "InventorySaveRequest"
    $inventoryAccepted = Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $inventoryRequest)
    Assert-True ($inventoryAccepted.Accepted -and $inventoryAccepted.StatLimitFindings.Count -eq 0 -and
        $inventoryAccepted.SemanticObservations.Count -eq 0) `
        "Host inventory request rejected or manufactured fresh stat evidence from retained profile maxima."
    $latest = Invoke-Hidden $service "GetLocalHostSnapshot" @($sessionId)
    Assert-True ($latest.Revision -eq 3 -and $latest.PayloadLength -gt $inventory.Length -and
        (Test-Bytes $latest.GetPayloadCopy() $payload)) "Inventory ACK did not expose the full canonical host profile."

    # A distinct second full revision after the synchronous first-full
    # promotion must checkpoint against the promoted durable payload, not the
    # obsolete pending genesis.
    [byte[]]$laterPayload = New-ProfilePayload "HostHero" $session.PlayerId "host-live-4" `
        -MaximumHealth 1001 -MaximumStamina 1002 -MaximumEitr 1003
    $later = New-Request $identity $sessionId 4 3 $laterPayload
    Assert-True (Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $later)).Accepted `
        "Later host shadow rejected after first-full promotion."

    $checkpoint = Invoke-Hidden $service "BeginCheckpoint"
    $checkpointEntries = Get-Hidden $checkpoint "Entries"
    Assert-True ($checkpointEntries.Count -eq 1) "Host missing from world checkpoint."
    Invoke-Hidden $service "CommitCheckpointEntry" @($checkpoint, $checkpointEntries[0]) | Out-Null
    $disk = Invoke-Hidden $repository "Load" @($identity, $storageKey)
    Assert-True ($disk.Envelope.Revision -eq 1 -and
        (Test-Bytes $disk.Envelope.GetPayloadCopy() $laterPayload)) `
        "The post-promotion checkpoint did not use the promoted durable base or reset its cold revision baseline."
    $backups = @(Get-ChildItem -LiteralPath $layout.GetAccountDirectory($storageKey) -File -Filter "$storageKey.*.fch")
    Assert-True ($backups.Count -ge 1) "Host checkpoint did not retain its prior backup."

    Invoke-Hidden $service "CloseLocalHostSession" @($sessionId) | Out-Null
    Assert-Throws { Invoke-Hidden $service "GetLocalHostSnapshot" @($sessionId) } "*No matching*"
    Assert-Throws { Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $later) } "*No matching*"
    Assert-Throws { Invoke-Hidden $service "FinalizePendingLocalHostSnapshot" @($sessionId) } "*No matching*"
    $reopened = Invoke-Hidden $service "OpenOrCreateLocalHostSession" @($identity)
    $newSessionId = $reopened.Snapshot.SessionId
    Assert-True ($newSessionId -ne $sessionId -and $reopened.Snapshot.Revision -eq 4 -and
        (Test-Bytes $reopened.Snapshot.GetPayloadCopy() $laterPayload)) "Host reopen lost the retained shadow or reused its generation."
    Assert-True ($reopened.StatLimitFindings.Count -eq 3 -and $reopened.SemanticObservations.Count -eq 0) `
        "Stored Observe did not expose separate numeric findings for the retained snapshot."
    Assert-Throws { Invoke-Hidden $service "GetLocalHostSnapshot" @($sessionId) } "*No matching*"
    Assert-Throws { Invoke-Hidden $service "HandleLocalHostSaveRequest" @($sessionId, $later) } "*No matching*"
    $rebound = Invoke-Hidden $service "GetLocalHostSnapshot" @($newSessionId)
    Assert-True ($rebound.SessionId -eq $newSessionId -and $rebound.Revision -eq 4) "Retained snapshot was not rebound to the replacement session."
    Invoke-Hidden $service "CloseLocalHostSession" @($sessionId) | Out-Null
    $lookup = [object[]]@($newSessionId, $null)
    Assert-True (Invoke-Hidden $service "TryGetLocalHostSession" $lookup) "Stale close retired the replacement host."
    Invoke-Hidden $service "CloseLocalHostSession" @($newSessionId) | Out-Null

    $remote = Invoke-Hidden $service "OpenOrCreateSessionCore" @($identity, $rpc)
    Assert-True ($remote.StatLimitFindings.Count -eq 3 -and $remote.SemanticObservations.Count -eq 0) `
        "Remote stored-session open lost numeric findings or duplicated them into semantic observations."
    Assert-Throws { Invoke-Hidden $service "OpenOrCreateLocalHostSession" @($identity) } "*already has an active session*"
    $service.CloseServerSession($rpc)

    # A separate identity receives a fresh limiter; the in-process path must not bypass it.
    $options.SaveRequestBurstCapacity = 1
    $limited = Invoke-Hidden $service "OpenOrCreateLocalHostSession" @($otherIdentity)
    $limitedId = $limited.Snapshot.SessionId
    Invoke-Hidden $service "FinalizePendingLocalHostSnapshot" @($limitedId) | Out-Null
    $limitedLookup = [object[]]@($limitedId, $null)
    Assert-True (Invoke-Hidden $service "TryGetLocalHostSession" $limitedLookup) "Rate-limited host missing."
    $limitedPayload = New-ProfilePayload "GuestHero" $limitedLookup[1].PlayerId "rate-limit"
    $allowed = New-Request $otherIdentity $limitedId 2 1 $limitedPayload
    Assert-True (Invoke-Hidden $service "HandleLocalHostSaveRequest" @($limitedId, $allowed)).Accepted "First rate-limited host save rejected."
    $excess = New-Request $otherIdentity $limitedId 3 2 $limitedPayload
    $rateRejected = Invoke-Hidden $service "HandleLocalHostSaveRequest" @($limitedId, $excess)
    Assert-True (-not $rateRejected.Accepted -and $rateRejected.Error -like "*rate limit*") "Host bypassed the shared save rate limit."
    Invoke-Hidden $service "CloseLocalHostSession" @($limitedId) | Out-Null

    $final = Invoke-Hidden $service "OpenOrCreateLocalHostSession" @($identity)
    $lookup = [object[]]@($final.Snapshot.SessionId, $null)
    Assert-True (Invoke-Hidden $service "TryGetLocalHostSession" $lookup) "Final host missing."
    $finalSession = $lookup[1]
    # A reload atomically changes future policy, not retained authoritative data.
    $stateNames = @('_liveSnapshots', '_activeStorageLeases', '_skillObservationWindows', '_saveRateLimiters')
    $retainedState = @{}
    foreach ($stateName in $stateNames) {
        $retainedState[$stateName] = $service.GetType().GetField($stateName, $allInstance).GetValue($service)
    }
    $windowField = $finalSession.GetType().GetField('_skillObservationWindow', $allInstance)
    $retainedWindow = $windowField.GetValue($finalSession)
    $settingsType = $plugin.GetType('ServerManager.ServerSettings', $true)
    $reloadSettings = $settingsType.GetMethod('Parse').Invoke($null, [object[]]@(
        "serverSettings:`n  maxCharactersPerAccount: 1`n  backupsPerProfile: 2`nforbiddenItems: [ReloadBlocked]`nstatCaps:`n  health: 40`nstartItems:`n  - Wood, 20`n"))
    Invoke-Hidden $service 'ApplyServerSettings' @($reloadSettings) | Out-Null
    Assert-True ($options.MaxCharactersPerAccount -eq 1 -and $options.MaxBackups -eq 2) 'Reload did not update quota and future backup retention.'
    foreach ($stateName in $stateNames) {
        Assert-True ([object]::ReferenceEquals($retainedState[$stateName],
            $service.GetType().GetField($stateName, $allInstance).GetValue($service))) "Reload replaced retained state $stateName."
    }
    Assert-True ([object]::ReferenceEquals($retainedWindow, $windowField.GetValue($finalSession))) 'Reload reset the skill observation window.'
    Assert-True ((Invoke-Hidden $service 'TryGetLocalHostSession' $lookup) -and
        [object]::ReferenceEquals($lookup[1], $finalSession)) 'Reload replaced the open session or lease.'
    $afterReload = Invoke-Hidden $service 'GetLocalHostSnapshot' @($final.Snapshot.SessionId)
    Assert-True ($afterReload.Revision -eq 4 -and -not $afterReload.RequiresFreshLocalCharacter -and
        (Test-Bytes $afterReload.GetPayloadCopy() $laterPayload)) 'Reload changed existing inventory/profile bytes or regranted START ITEMS.'
    Assert-Throws { Invoke-Hidden $service 'OpenOrCreateSessionCore' @($identity, $rpc) } '*already has an active session*'
    $quotaIdentity = New-Instance 'CharacterIdentity' @($identity.AccountId, 'QuotaBlocked')
    Assert-Throws { Invoke-Hidden $service 'OpenOrCreateSessionCore' @($quotaIdentity, $rpc) } '*profile quota*'
    $blockedPayload = New-ProfilePayload 'HostHero' $finalSession.PlayerId 'reload-forbidden' -ItemPrefab 'ReloadBlocked'
    $blockedRequest = New-Request $identity $final.Snapshot.SessionId 5 4 $blockedPayload
    $blockedResult = Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($final.Snapshot.SessionId, $blockedRequest)
    Assert-True (-not $blockedResult.Accepted -and $blockedResult.Error -like '*forbidden*ReloadBlocked*') 'Reloaded forbidden-item policy was not applied to incoming saves.'
    $reloadedRequest = New-Request $identity $final.Snapshot.SessionId 5 4 $laterPayload
    $reloadedResult = Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($final.Snapshot.SessionId, $reloadedRequest)
    Assert-True ($reloadedResult.Accepted -and $reloadedResult.StatLimitFindings[0].Limit -eq 40) 'Reloaded numeric policy did not govern future saves.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($final.Snapshot.SessionId) | Out-Null
    $afterReloadReopen = Invoke-Hidden $service 'OpenOrCreateLocalHostSession' @($identity)
    Assert-True ($afterReloadReopen.Snapshot.Revision -eq 5 -and
        (Test-Bytes $afterReloadReopen.Snapshot.GetPayloadCopy() $laterPayload) -and
        -not $afterReloadReopen.Snapshot.RequiresFreshLocalCharacter -and
        $afterReloadReopen.StatLimitFindings[0].Limit -eq 40) 'Reconnect after reload lost RAM state, regranted items, or retained stale stored policy.'
    $lookup = [object[]]@($afterReloadReopen.Snapshot.SessionId, $null)
    Assert-True (Invoke-Hidden $service 'TryGetLocalHostSession' $lookup) 'Reloaded reconnect session is missing.'
    $finalSession = $lookup[1]
    [byte[]]$adminPayload = New-ProfilePayload 'HostHero' $finalSession.PlayerId 'admin-roundtrip' -UsedCheats $true
    $adminRequest = New-Request $identity $afterReloadReopen.Snapshot.SessionId 6 5 $adminPayload
    $deniedFlag = Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($afterReloadReopen.Snapshot.SessionId, $adminRequest)
    Assert-True (-not $deniedFlag.Accepted -and $deniedFlag.Error -like '*used_cheats*' -and
        $finalSession.CurrentRevision -eq 5) 'Non-admin cheat flag changed the live revision.'
    $script:fixturePolicyAdmin = $true
    $acceptedFlag = Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($afterReloadReopen.Snapshot.SessionId, $adminRequest)
    Assert-True ($acceptedFlag.Accepted -and $finalSession.CurrentRevision -eq 6 -and
        $acceptedFlag.SemanticObservations[0] -like '*admin_bypass:used_cheats*') `
        'Verified admin cheat flag did not pass the real local-host repository path.'
    $acceptedAudit = Get-Hidden (Get-Hidden $acceptedFlag 'AuditFindings') 'AuditObservations'
    Assert-True ($acceptedAudit.Count -eq 1 -and (Get-Hidden $acceptedAudit[0] 'ReasonCode') -eq 'used_cheats') `
        'Accepted repository/service result lost its generated audit classification.'
    $adminInventory = New-Request $identity $afterReloadReopen.Snapshot.SessionId 7 6 $emptyInventory 'InventorySaveRequest'
    Assert-True (Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($afterReloadReopen.Snapshot.SessionId, $adminInventory)).Accepted `
        'Admin inventory fast path rejected the retained flagged full profile.'
    $adminCheckpoint = Invoke-Hidden $service 'BeginCheckpoint'
    foreach ($entry in (Get-Hidden $adminCheckpoint 'Entries')) {
        Invoke-Hidden $service 'CommitCheckpointEntry' @($adminCheckpoint, $entry) | Out-Null
    }
    $adminDisk = Invoke-Hidden $repository 'Load' @($identity, $storageKey)
    Assert-True ($adminDisk.Envelope.Revision -eq 1 -and
        $deserializeProfile.Invoke($profiles, [object[]]@($adminDisk.Envelope.GetPayloadCopy(), $null, $localSource)).m_usedCheats) `
        'Admin checkpoint cleared the cheat flag or failed to expose a cold revision-1 disk baseline.'
    Invoke-Hidden $service 'CloseLocalHostSession' @($afterReloadReopen.Snapshot.SessionId) | Out-Null
    $script:fixturePolicyAdmin = $false
    $adminReopen = Invoke-Hidden $service 'OpenOrCreateLocalHostSession' @($identity)
    Assert-True ($adminReopen.Snapshot.Revision -eq 7 -and
        $adminReopen.SemanticObservations[0] -like '*would_reject:used_cheats*') `
        'Stored Observe could not reopen a previously accepted admin profile.'
    $storedAudit = Get-Hidden (Get-Hidden $adminReopen 'AuditFindings') 'AuditObservations'
    Assert-True ($storedAudit.Count -eq 1 -and (Get-Hidden $storedAudit[0] 'ReasonCode') -eq 'stored_policy_violation') `
        'Stored reopen lost its generated Observe classification.'
    $revokedRequest = New-Request $identity $adminReopen.Snapshot.SessionId 8 7 $adminPayload
    Assert-True (-not (Invoke-Hidden $service 'HandleLocalHostSaveRequest' @($adminReopen.Snapshot.SessionId, $revokedRequest)).Accepted) `
        'Revoked administrator retained the incoming cheat-flag exemption.'
    $lookup = [object[]]@($adminReopen.Snapshot.SessionId, $null)
    Assert-True (Invoke-Hidden $service 'TryGetLocalHostSession' $lookup) 'Final admin test session missing.'
    $finalSession = $lookup[1]
    $service.Dispose()
    Assert-True (Get-Hidden $finalSession "IsClosed") "Dispose did not close the host."
    Assert-True (-not (Invoke-Hidden $service "TryGetLocalHostSession" $lookup)) "Disposed host lookup succeeded."
    Assert-Throws { Invoke-Hidden $service "OpenOrCreateLocalHostSession" @($identity) } "*disposed*"
    $replacementKeys = New-Instance "CharacterStorageKeyProvider" @($layout)
    $replacementKeys.Dispose()
    Write-Host "Local host character service smoke passed (real repository; headless Unity dependency shims)."
}
finally {
    if ($null -ne $service) { $service.Dispose() }
    elseif ($null -ne $keys) { $keys.Dispose() }
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($fixtureAssemblyResolver)
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-True ($resolvedRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot).StartsWith("smlocal-", [StringComparison]::Ordinal)) "Unsafe test cleanup target."
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
