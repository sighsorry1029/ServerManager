param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-CecilSuccessors {
    param($Instruction)

    if ($null -eq $Instruction) {
        return @()
    }

    $flowControl = $Instruction.OpCode.FlowControl.ToString()
    if ($flowControl -eq "Branch") {
        return @($Instruction.Operand)
    }

    if ($flowControl -eq "Cond_Branch") {
        $successors = @()
        if ($Instruction.Operand -is [System.Collections.IEnumerable] -and
            $Instruction.Operand -isnot [string]) {
            $successors += @($Instruction.Operand)
        }
        else {
            $successors += $Instruction.Operand
        }

        if ($null -ne $Instruction.Next) {
            $successors += $Instruction.Next
        }

        return $successors
    }

    if ($flowControl -eq "Return" -or $flowControl -eq "Throw") {
        return @()
    }

    if ($null -eq $Instruction.Next) {
        return @()
    }

    return @($Instruction.Next)
}

. (Join-Path $PSScriptRoot 'CecilControlFlow.ps1')

function Get-FirstConditionalBranch {
    param(
        $Start,
        $Before
    )

    $current = $Start.Next
    while ($null -ne $current -and
           ($null -eq $Before -or $current.Offset -lt $Before.Offset)) {
        if ($current.OpCode.FlowControl.ToString() -eq "Cond_Branch") {
            return $current
        }

        $current = $current.Next
    }

    return $null
}

function Get-CecilIntConstant {
    param($Instruction)
    if ($null -eq $Instruction) { return $null }
    switch ($Instruction.OpCode.Name) {
        'ldc.i4.m1' { return -1 }
        'ldc.i4.0' { return 0 }
        'ldc.i4.1' { return 1 }
        'ldc.i4.2' { return 2 }
        'ldc.i4.3' { return 3 }
        'ldc.i4.4' { return 4 }
        'ldc.i4.5' { return 5 }
        'ldc.i4.6' { return 6 }
        'ldc.i4.7' { return 7 }
        'ldc.i4.8' { return 8 }
        'ldc.i4' { return [int]$Instruction.Operand }
        'ldc.i4.s' { return [int]$Instruction.Operand }
        default { return $null }
    }
}

function New-ZPackage {
    param([byte[]]$Bytes)

    $arguments = [object[]]::new(1)
    $arguments[0] = $Bytes
    return $script:zPackageConstructor.Invoke($arguments)
}

function New-RoutedEnvelopeBytes {
    param(
        [int]$DeclaredParameterLength,
        [byte[]]$ParameterBytes = [byte[]]::new(0),
        [byte[]]$Prefix = [byte[]]::new(0)
    )

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write($Prefix)
        $writer.Write([long]11)       # message ID
        $writer.Write([long]22)       # claimed sender peer ID
        $writer.Write([long]33)       # target peer ID
        $writer.Write([long]0)        # ZDOID user ID
        $writer.Write([uint32]0)      # ZDOID object ID (None)
        $writer.Write([int]0)         # unknown method hash
        $writer.Write($DeclaredParameterLength)
        $writer.Write($ParameterBytes)
        $writer.Flush()
        return $stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Invoke-RoutedDamageInspection {
    param($Package)

    $arguments = [object[]]::new(2)
    $arguments[0] = $Package
    $arguments[1] = $null
    $result = $script:inspectRoutedDamage.Invoke($null, $arguments)
    return [pscustomobject]@{
        Result = $result
        Observation = $arguments[1]
    }
}

function Decode-DetectionReport {
    param($Package)

    $arguments = [object[]]::new(3)
    $arguments[0] = $Package
    $succeeded = $script:decodeDetection.Invoke($null, $arguments)
    return [pscustomobject]@{
        Succeeded = $succeeded
        Report = $arguments[1]
        Rejection = $arguments[2]
    }
}

function Dictionary-ContainsKey {
    param(
        $Dictionary,
        [string]$Key
    )

    $arguments = [object[]]::new(1)
    $arguments[0] = $Key
    return $Dictionary.GetType().GetMethod("ContainsKey").Invoke(
        $Dictionary,
        $arguments)
}

function Decode-ProtocolPacket {
    param(
        $Package,
        $Limits
    )

    $arguments = [object[]]::new(4)
    $arguments[0] = $Package
    $arguments[1] = $Limits
    $succeeded = $script:decodeProtocol.Invoke($null, $arguments)
    return [pscustomobject]@{
        Succeeded = $succeeded
        Packet = $arguments[2]
        Rejection = $arguments[3]
    }
}

function Decode-ChallengeOptions {
    param($Packet)

    $arguments = [object[]]::new(3)
    $arguments[0] = $Packet
    $succeeded = $script:decodeChallenge.Invoke($null, $arguments)
    return [pscustomobject]@{
        Succeeded = $succeeded
        Options = $arguments[1]
        Rejection = $arguments[2]
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$gameAssemblyPath = Join-Path $GamePath `
    "valheim_Data\Managed\assembly_valheim.dll"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"

Assert-True (Test-Path -LiteralPath $pluginPath) `
    "Build ServerManager before running this smoke test."
Assert-True (Test-Path -LiteralPath $gameAssemblyPath) `
    "The Valheim runtime assembly was not found."
Assert-True (Test-Path -LiteralPath $cecilPath) `
    "Mono.Cecil from BepInEx was not found."

$managedAssemblyRoot = Join-Path $GamePath "valheim_Data\Managed"
foreach ($unityDependency in @(
    "UnityEngine.CoreModule.dll",
    "UnityEngine.PhysicsModule.dll",
    "UnityEngine.dll",
    "assembly_utils.dll",
    "SoftReferenceableAssets.dll",
    "com.rlabrecque.steamworks.net.dll",
    "Splatform.dll")) {
    [Reflection.Assembly]::LoadFrom((Join-Path `
        $managedAssemblyRoot `
        $unityDependency)) | Out-Null
}
$gameAssembly = [Reflection.Assembly]::LoadFrom($gameAssemblyPath)
$pluginAssembly = [Reflection.Assembly]::LoadFrom($pluginPath)
$staticNonPublic = [Reflection.BindingFlags]"Static,NonPublic"
$instanceNonPublic = [Reflection.BindingFlags]"Instance,NonPublic"

# The installed Valheim assemblies contain default interface metadata supported
# by Unity Mono and modern .NET, but not by Windows PowerShell 5.1's CLR. Run
# the executable reflection probe where the host can load that metadata; the
# separate Cecil access smoke always validates the exact installed schema.
if ($PSVersionTable.PSEdition -eq "Core") {
    $privateAccessType = $pluginAssembly.GetType(
        "ServerManager.ValheimPrivateAccess",
        $true)
    $validatePrivateAccess = $privateAccessType.GetMethod(
        "ValidateRequiredMembers",
        $staticNonPublic)
    Assert-True ($null -ne $validatePrivateAccess) `
        "The Valheim private-access compatibility boundary is missing."
    try {
        $validatePrivateAccess.Invoke($null, [object[]]@())
    }
    catch {
        $messages = [Collections.Generic.List[string]]::new()
        $current = $_.Exception
        while ($null -ne $current) {
            $messages.Add(
                $current.GetType().FullName + ": " + $current.Message)
            $current = $current.InnerException
        }

        throw "Valheim private-access compatibility validation failed: " +
            ($messages -join " -> ")
    }
}

[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
$gameDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $gameAssemblyPath)
$terminalDefinition = $gameDefinition.MainModule.Types |
    Where-Object FullName -eq "Terminal"
$tryRunDefinition = $terminalDefinition.Methods |
    Where-Object {
        $_.Name -eq "TryRunCommand" -and
        $_.ReturnType.FullName -eq "System.Void" -and
        $_.Parameters.Count -eq 3 -and
        $_.Parameters[0].ParameterType.FullName -eq "System.String" -and
        $_.Parameters[1].ParameterType.FullName -eq "System.Boolean" -and
        $_.Parameters[2].ParameterType.FullName -eq "System.Boolean"
    }
Assert-True ($null -ne $tryRunDefinition) `
    "Terminal.TryRunCommand(string,bool,bool) changed."

$consoleCommandDefinition = $terminalDefinition.NestedTypes |
    Where-Object Name -eq "ConsoleCommand"
$isCheatDefinition = $consoleCommandDefinition.Fields |
    Where-Object {
        $_.Name -eq "IsCheat" -and
        $_.FieldType.FullName -eq "System.Boolean"
    }
Assert-True ($null -ne $isCheatDefinition) `
    "Terminal.ConsoleCommand.IsCheat changed."

$zNetDefinition = $gameDefinition.MainModule.Types |
    Where-Object FullName -eq "ZNet"
$internalKickDefinition = $zNetDefinition.Methods |
    Where-Object {
        $_.Name -eq "InternalKick" -and
        $_.Parameters.Count -eq 1 -and
        $_.Parameters[0].ParameterType.FullName -eq "ZNetPeer"
    }
$bannedListDefinition = $zNetDefinition.Fields |
    Where-Object {
        $_.Name -eq "m_bannedList" -and
        $_.FieldType.FullName -eq "SyncedList"
    }
$adminListDefinition = $zNetDefinition.Fields |
    Where-Object {
        $_.Name -eq "m_adminList" -and
        $_.FieldType.FullName -eq "SyncedList"
    }
$isAdminDefinition = $zNetDefinition.Methods |
    Where-Object {
        $_.Name -eq "IsAdmin" -and
        $_.ReturnType.FullName -eq "System.Boolean" -and
        $_.Parameters.Count -eq 1 -and
        $_.Parameters[0].ParameterType.FullName -eq "System.String"
    }
Assert-True ($null -ne $internalKickDefinition) `
    "ZNet.InternalKick(ZNetPeer) changed."
Assert-True ($null -ne $bannedListDefinition) `
    "ZNet.m_bannedList changed."
Assert-True ($null -ne $adminListDefinition) `
    "ZNet.m_adminList changed."
Assert-True ($null -ne $isAdminDefinition) `
    "ZNet.IsAdmin(string):bool changed."

$playerDefinition = $gameDefinition.MainModule.Types |
    Where-Object FullName -eq "Player"
$carryGetterDefinition = $playerDefinition.Methods |
    Where-Object {
        $_.Name -eq "GetMaxCarryWeight" -and
        $_.ReturnType.FullName -eq "System.Single" -and
        $_.Parameters.Count -eq 0
    }
$baseCarryFieldDefinition = $playerDefinition.Fields |
    Where-Object {
        $_.Name -eq "m_maxCarryWeight" -and
        $_.FieldType.FullName -eq "System.Single"
    }
Assert-True (
    $null -ne $carryGetterDefinition -and
    $null -ne $baseCarryFieldDefinition) `
    "Player carry-weight API changed."

foreach ($damageTargetName in @(
    "Character",
    "WearNTear",
    "MineRock5",
    "Destructible",
    "TreeLog",
    "TreeBase")) {
    $damageTargetDefinition = $gameDefinition.MainModule.Types |
        Where-Object FullName -eq $damageTargetName
    $damageDefinition = $damageTargetDefinition.Methods |
        Where-Object {
            $_.Name -eq "Damage" -and
            $_.ReturnType.FullName -eq "System.Void" -and
            $_.Parameters.Count -eq 1 -and
            $_.Parameters[0].ParameterType.FullName -eq "HitData"
        }
    Assert-True ($null -ne $damageDefinition) `
        "$damageTargetName.Damage(HitData) changed."
}

$routedRpcDefinition = $gameDefinition.MainModule.Types |
    Where-Object FullName -eq "ZRoutedRpc"
$routedReceiveDefinition = $routedRpcDefinition.Methods |
    Where-Object {
        $_.Name -eq "RPC_RoutedRPC" -and
        $_.ReturnType.FullName -eq "System.Void" -and
        $_.Parameters.Count -eq 2 -and
        $_.Parameters[0].ParameterType.FullName -eq "ZRpc" -and
        $_.Parameters[1].ParameterType.FullName -eq "ZPackage"
    }
Assert-True ($null -ne $routedReceiveDefinition) `
    "ZRoutedRpc.RPC_RoutedRPC(ZRpc,ZPackage) changed."

$pluginDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $pluginPath)
$guardDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.CheatCommandGuard"
$guardPrefixDefinition = $guardDefinition.Methods |
    Where-Object Name -eq "Prefix"
$guardPrefixCalls = $guardPrefixDefinition.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] }
$enqueueCall = $guardPrefixCalls |
    Where-Object {
        $_.Operand.Name -eq "TryEnqueue" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.CheatCommandGuard"
    } |
    Select-Object -First 1
$entitlementCall = $guardPrefixCalls |
    Where-Object {
        $_.Operand.Name -eq "HasActiveAdminEntitlement" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.CheatCommandGuard"
    } |
    Select-Object -First 1
Assert-True ($null -ne $enqueueCall -and $null -ne $entitlementCall) `
    "The admin command-report path changed."
Assert-True ($enqueueCall.Offset -lt $entitlementCall.Offset) `
    "Admin commands can bypass reporting before server revalidation."

$runtimeDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ServerManagerRuntime"
$dataRootDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ServerDataRoot"
$integrityServiceDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ServerIntegrityService"
$pluginTypeDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ServerManagerPlugin"
Assert-True (
    $null -ne $runtimeDefinition -and
    $null -ne $dataRootDefinition -and
    $null -ne $integrityServiceDefinition -and
    $null -ne $pluginTypeDefinition) `
    "The persistent server-data root types are missing."

$removedDetectionToggleProperties = @(
    $pluginTypeDefinition.Properties |
        Where-Object {
            $_.Name -eq "EnableCheatEngineDetection" -or
            $_.Name -eq "EnableExternalToolDetection" -or
            $_.Name -eq "EnableValheimToolerDetection" -or
            $_.Name -eq "EnforceCarryWeightLimit"
        })
$removedDetectionResponseProperties = @(
    $pluginTypeDefinition.Properties |
        Where-Object {
            $_.Name -in @("CheatEngineDetectionAction", "ExternalToolDetectionAction",
                "ValheimToolerDetectionAction", "RepeatedCheatCommandAction",
                "AllowAdminCheatCommands", "CarryWeightLimitAction", "MaximumDamageAction")
        })
$settingsTypeDefinition = $pluginDefinition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerSettings'
$requiredDetectionResponseProperties = @(
    $settingsTypeDefinition.Properties | Where-Object {
        $_.Name -in @("CheatDetectionResponse", "StatLimitResponse") -and
        $_.PropertyType.FullName -eq 'ServerManager.DetectionAction'
    })
Assert-True (
    $removedDetectionToggleProperties.Count -eq 0 -and
    $removedDetectionResponseProperties.Count -eq 0 -and
    $requiredDetectionResponseProperties.Count -eq 2) `
    "Detection and stat policies no longer expose exactly two shared responses without enable/admin toggles."

$defaultEnforceModPolicyField = $pluginTypeDefinition.Fields |
    Where-Object Name -eq "DefaultEnforceModPolicy" |
    Select-Object -First 1
$policyWatcherNotifyFiltersField = $integrityServiceDefinition.Fields |
    Where-Object Name -eq "PolicyWatcherNotifyFilters" |
    Select-Object -First 1
Assert-True (
    $null -ne $defaultEnforceModPolicyField -and
    $defaultEnforceModPolicyField.HasConstant -and
    [bool]$defaultEnforceModPolicyField.Constant -and
    $null -ne $policyWatcherNotifyFiltersField -and
    $policyWatcherNotifyFiltersField.HasConstant -and
    (([int]$policyWatcherNotifyFiltersField.Constant -band 2) -eq 2)) `
    "Strict mod-policy defaults or directory-change watcher coverage changed."
foreach ($fixedSetting in @(
    "EnforceModPolicy", "EnableServerCharacters", "MaximumManifestBytes",
    "HandshakeTimeoutSeconds", "MaximumCharacterBytes")) {
    Assert-True (@($pluginTypeDefinition.Properties |
        Where-Object Name -eq $fixedSetting).Count -eq 0) `
        "Fixed admission policy retained cfg property $fixedSetting."
    foreach ($runtimeMethod in @($runtimeDefinition.Methods | Where-Object HasBody)) {
        Assert-True (@($runtimeMethod.Body.Instructions |
            Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and
                $_.Operand.DeclaringType.FullName -eq $pluginTypeDefinition.FullName -and
                $_.Operand.Name -eq "get_$fixedSetting"
            }).Count -eq 0) `
            "$($runtimeMethod.Name) still reads the removed $fixedSetting cfg."
    }
}
$fixedRuntimeSource = [IO.File]::ReadAllText((
    Join-Path $projectRoot "Networking\ServerManagerRuntime.cs"))
Assert-True ([Text.RegularExpressions.Regex]::IsMatch(
    $fixedRuntimeSource,
    'ProtocolChallengeOptions challengeOptions = new\(\s*' +
    'enforceManifest: ServerManagerPlugin\.DefaultEnforceModPolicy,\s*' +
    'serverCharactersEnabled: true,\s*maximumManifestBytes:\s*' +
    'ConnectionProtocolLimits\.AbsoluteMaxManifestBytes,')) `
    "Every new server challenge must require hash validation and managed characters under the protocol manifest cap."
$policyChallengeConstructors = @($runtimeDefinition.Methods.Body.Instructions |
    Where-Object { $_.OpCode.Name -eq 'newobj' -and
        $_.Operand.DeclaringType.FullName -eq 'ServerManager.ProtocolChallengeOptions' })
Assert-True ($policyChallengeConstructors.Count -eq 1 -and
    $policyChallengeConstructors[0].Operand.Parameters.Count -eq 16) `
    "The server must construct one explicit challenge policy."
$challengeArgumentInstructions = [Collections.Generic.List[object]]::new()
$previousArgument = $policyChallengeConstructors[0].Previous
while ($null -ne $previousArgument -and
    -not ($previousArgument.Operand -is [Mono.Cecil.MethodReference] -and
        $previousArgument.Operand.Name -eq 'get_MaximumDamage')) {
    $previousArgument = $previousArgument.Previous
}
Assert-True ($null -ne $previousArgument) 'The challenge lost its maximum-damage argument.'
while ($null -ne $previousArgument -and $challengeArgumentInstructions.Count -lt 17) {
    if ($previousArgument.OpCode.Name -ne 'nop') {
        $challengeArgumentInstructions.Insert(0, $previousArgument)
    }
    $previousArgument = $previousArgument.Previous
}
# The two float YAML arguments each read CurrentServerSettings plus its getter;
# all other arguments are constants directly at this compiled constructor call.
foreach ($argumentSpec in @(
    @{ Index = 0; Value = 1; Name = 'enforceManifest' },
    @{ Index = 1; Value = 1; Name = 'serverCharactersEnabled' },
    @{ Index = 2; Value = 1048576; Name = 'maximumManifestBytes' },
    @{ Index = 3; Value = 1; Name = 'detectCheatEngine' },
    @{ Index = 4; Value = 1; Name = 'detectExternalTools' },
    @{ Index = 5; Value = 1; Name = 'detectGenericProcessNames' },
    @{ Index = 6; Value = 1; Name = 'detectValheimTooler' },
    @{ Index = 7; Value = 1; Name = 'monitorCheatCommands' },
    @{ Index = 8; Value = 1; Name = 'blockCheatCommands' },
    @{ Index = 9; Value = 1; Name = 'allowAdminCheatCommands' },
    @{ Index = 10; Value = 30; Name = 'processScanIntervalSeconds' },
    @{ Index = 11; Value = 1; Name = 'enforceCarryWeightLimit' },
    @{ Index = 14; Value = 1; Name = 'enforceMaximumDamageLimit' })) {
    Assert-True ((Get-CecilIntConstant $challengeArgumentInstructions[$argumentSpec.Index]) -eq $argumentSpec.Value) `
        "The compiled server challenge no longer fixes $($argumentSpec.Name) to $($argumentSpec.Value)."
}
Assert-True ($challengeArgumentInstructions[12].Operand.Name -eq 'get_CurrentServerSettings' -and
    $challengeArgumentInstructions[13].Operand.Name -eq 'get_MaximumCarryWeight' -and
    $challengeArgumentInstructions[15].Operand.Name -eq 'get_CurrentServerSettings' -and
    $challengeArgumentInstructions[16].Operand.Name -eq 'get_MaximumDamage') `
    "The challenge lost independent server-configured carry and damage caps."
$fixedWorldBufferField = $runtimeDefinition.Fields |
    Where-Object Name -eq "MaximumBufferedWorldBytes" |
    Select-Object -First 1
Assert-True ($null -ne $fixedWorldBufferField -and $fixedWorldBufferField.IsLiteral -and
    [long]$fixedWorldBufferField.Constant -eq 33554432) `
    "The fixed pre-ready world transmission buffer is no longer bounded at 32 MiB."

$fixedInitialize = $runtimeDefinition.Methods |
    Where-Object Name -eq "Initialize" | Select-Object -First 1
$fixedStaticConstructor = $runtimeDefinition.Methods |
    Where-Object Name -eq ".cctor" | Select-Object -First 1
foreach ($timeoutSpec in @(
    @{ Name = "ConnectionHandshakeTimeout"; Seconds = 120 },
    @{ Name = "CharacterFragmentAssemblyTimeout"; Seconds = 30 })) {
    $timeoutField = $runtimeDefinition.Fields |
        Where-Object Name -eq $timeoutSpec.Name | Select-Object -First 1
    $timeoutStore = $fixedStaticConstructor.Body.Instructions |
        Where-Object {
            $_.OpCode.Name -eq "stsfld" -and
            $_.Operand -is [Mono.Cecil.FieldReference] -and
            $_.Operand.Name -eq $timeoutSpec.Name
        } | Select-Object -First 1
    $timeoutReads = @($fixedInitialize.Body.Instructions |
        Where-Object {
            $_.OpCode.Name -eq "ldsfld" -and
            $_.Operand -is [Mono.Cecil.FieldReference] -and
            $_.Operand.Name -eq $timeoutSpec.Name
        })
    Assert-True ($null -ne $timeoutField -and $timeoutField.IsStatic -and
        $timeoutField.IsInitOnly -and $null -ne $timeoutStore -and
        $timeoutStore.Previous.Operand -is [Mono.Cecil.MethodReference] -and
        $timeoutStore.Previous.Operand.FullName -eq
            "System.TimeSpan System.TimeSpan::FromSeconds(System.Double)" -and
        [double]$timeoutStore.Previous.Previous.Operand -eq $timeoutSpec.Seconds -and
        $timeoutReads.Count -eq 2) `
        "The fixed $($timeoutSpec.Seconds)-second $($timeoutSpec.Name) value or its two runtime consumers changed."
}
Assert-True ([Text.RegularExpressions.Regex]::IsMatch(
    $fixedRuntimeSource,
    'phaseTimeout:\s*ConnectionHandshakeTimeout,\s*' +
    'overallHandshakeTimeout:\s*ConnectionHandshakeTimeout\)') -and
    [Text.RegularExpressions.Regex]::Matches(
        $fixedRuntimeSource,
        'assemblyTimeout:\s*CharacterFragmentAssemblyTimeout\)').Count -eq 2) `
    "Handshake phase/overall deadlines must use 120 seconds, while both fragment directions expire at 30 seconds."

$clientConnectionDefinition = $runtimeDefinition.NestedTypes |
    Where-Object Name -eq "ClientConnection" | Select-Object -First 1
foreach ($deadlineConsumer in @(
    ($clientConnectionDefinition.Methods | Where-Object Name -eq ".ctor" |
        Select-Object -First 1),
    ($runtimeDefinition.Methods | Where-Object Name -eq "TryReserveSteamAuthentication" |
        Select-Object -First 1))) {
    Assert-True (@($deadlineConsumer.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq "ServerManager.ConnectionProtocolLimits" -and
            $_.Operand.Name -eq "get_OverallHandshakeTimeout"
        }).Count -eq 1) `
        "$($deadlineConsumer.FullName) stopped using the bounded overall handshake deadline."
}

$storageOptionsDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.CharacterStorageOptions"
$defaultPayloadField = $storageOptionsDefinition.Fields |
    Where-Object Name -eq "DefaultMaxPayloadBytes"
$metadataAllowanceField = $storageOptionsDefinition.Fields |
    Where-Object Name -eq "DefaultMaxEnvelopeOverheadBytes"
Assert-True ($defaultPayloadField.HasConstant -and
    [int]$defaultPayloadField.Constant -eq 10 * 1024 * 1024 -and
    $metadataAllowanceField.HasConstant -and
    [int]$metadataAllowanceField.Constant -eq 16 * 1024) `
    "The server raw-character limit or separate envelope metadata allowance changed."
foreach ($requiredBoundWiring in @(
    'int maximumCharacterBytes = CharacterStorageOptions.DefaultMaxPayloadBytes;',
    'int maximumNetworkCharacterBytes = 32 * 1024 * 1024;',
    'MaxPayloadBytes = maximumCharacterBytes,',
    'MaxEnvelopeBytes = serverEnvelopeLimit,',
    'MaxPayloadBytes = maximumNetworkCharacterBytes,',
    'MaxEnvelopeBytes = networkEnvelopeLimit,',
    'maxEncodedMessageBytes: serverEnvelopeLimit,',
    'maxDecodedMessageBytes: serverEnvelopeLimit,',
    'maxEncodedMessageBytes: networkEnvelopeLimit,',
    'maxDecodedMessageBytes: networkEnvelopeLimit,')) {
    Assert-True ($fixedRuntimeSource.Contains($requiredBoundWiring)) `
        "The server 10 MiB / client 32 MiB separation lost required wiring: $requiredBoundWiring"
}
Assert-True ([Text.RegularExpressions.Regex]::IsMatch(
    $fixedRuntimeSource,
    'int serverEnvelopeLimit = checked\(\s*maximumCharacterBytes\s*\+\s*' +
    'CharacterStorageOptions\.DefaultMaxEnvelopeOverheadBytes\)') -and
    [Text.RegularExpressions.Regex]::IsMatch(
    $fixedRuntimeSource,
    'int networkEnvelopeLimit = checked\(\s*maximumNetworkCharacterBytes\s*\+\s*' +
    'CharacterStorageOptions\.DefaultMaxEnvelopeOverheadBytes\)') -and
    [Text.RegularExpressions.Regex]::Matches(
    $fixedRuntimeSource,
    'int (?:serverReservedBytes|reservedBytes) = \(int\)Math\.Min\(\s*' +
    '128L \* 1024L \* 1024L,\s*Math\.Max\(').Count -eq 2) `
    "Character envelopes lost their metadata allowance or either fragment reservation pool became unbounded."

$eventRuntimeDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.Events.ServerEventRuntime"
$eventInitializeDefinition = $eventRuntimeDefinition.Methods |
    Where-Object Name -eq "Initialize"
$eventShutdownDefinition = $eventRuntimeDefinition.Methods |
    Where-Object Name -eq "Shutdown"
$eventInitializeStopCall = $eventInitializeDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "TryStopDispatcher" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.Events.ServerEventRuntime"
    } |
    Select-Object -First 1
$eventInitializeCatch = $eventInitializeDefinition.Body.ExceptionHandlers |
    Where-Object {
        $_.HandlerType.ToString() -eq "Catch" -and
        $null -ne $eventInitializeStopCall -and
        $eventInitializeStopCall.Offset -ge $_.HandlerStart.Offset -and
        ($null -eq $_.HandlerEnd -or
         $eventInitializeStopCall.Offset -lt $_.HandlerEnd.Offset)
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $eventInitializeCatch -and
    $null -ne $eventInitializeStopCall) `
    "Event dispatcher startup no longer rolls back from initialization failure."

$eventShutdownCalls = @(
    $eventShutdownDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$eventShutdownStepCalls = @(
    $eventShutdownCalls |
        Where-Object {
            $_.Operand.Name -eq "RunShutdownStep" -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.Events.ServerEventRuntime"
        })
$eventShutdownStopCalls = @(
    $eventShutdownCalls |
        Where-Object {
            $_.Operand.Name -eq "TryStopDispatcher" -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.Events.ServerEventRuntime"
        })
$eventShutdownFinally = $eventShutdownDefinition.Body.ExceptionHandlers |
    Where-Object {
        $handler = $_
        $handler.HandlerType.ToString() -eq "Finally" -and
        @($eventShutdownStopCalls |
            Where-Object {
                $_.Offset -ge $handler.HandlerStart.Offset -and
                ($null -eq $handler.HandlerEnd -or
                 $_.Offset -lt $handler.HandlerEnd.Offset)
            }).Count -gt 0
    } |
    Select-Object -First 1
Assert-True (
    $eventShutdownStepCalls.Count -ge 6 -and
    $eventShutdownStopCalls.Count -ge 2 -and
    $null -ne $eventShutdownFinally) `
    "Event shutdown no longer guarantees dispatcher cleanup after cleanup faults."

$runtimeInitializeDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "Initialize"
$runtimeInitializeCleanupCall = $runtimeInitializeDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "CleanupFailedInitialization" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$runtimeInitializeCatch = $runtimeInitializeDefinition.Body.ExceptionHandlers |
    Where-Object {
        $_.HandlerType.ToString() -eq "Catch" -and
        $null -ne $runtimeInitializeCleanupCall -and
        $runtimeInitializeCleanupCall.Offset -ge $_.HandlerStart.Offset -and
        ($null -eq $_.HandlerEnd -or
         $runtimeInitializeCleanupCall.Offset -lt $_.HandlerEnd.Offset)
    } |
    Select-Object -First 1
$failedInitializationCleanupDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "CleanupFailedInitialization"
$failedInitializationCleanupCalls = @(
    $failedInitializationCleanupDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq "RunInitializationCleanup"
        })
Assert-True (
    $null -ne $runtimeInitializeCatch -and
    $null -ne $runtimeInitializeCleanupCall -and
    $failedInitializationCleanupCalls.Count -ge 7) `
    "Partial ServerManager runtime initialization is no longer rolled back."

$pluginAwakeDefinition = $pluginTypeDefinition.Methods |
    Where-Object Name -eq "Awake"
$pluginAwakeRuntimeCalls = @(
    $pluginAwakeDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerRuntime"
        })
$pluginRuntimeInitializeCall = $pluginAwakeRuntimeCalls |
    Where-Object { $_.Operand.Name -eq "Initialize" } |
    Select-Object -First 1
$pluginRuntimeShutdownCall = $pluginAwakeRuntimeCalls |
    Where-Object { $_.Operand.Name -eq "Shutdown" } |
    Select-Object -First 1
$pluginAwakeRollbackCatch = $pluginAwakeDefinition.Body.ExceptionHandlers |
    Where-Object {
        $_.HandlerType.ToString() -eq "Catch" -and
        $null -ne $pluginRuntimeShutdownCall -and
        $pluginRuntimeShutdownCall.Offset -ge $_.HandlerStart.Offset -and
        ($null -eq $_.HandlerEnd -or
         $pluginRuntimeShutdownCall.Offset -lt $_.HandlerEnd.Offset)
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $pluginRuntimeInitializeCall -and
    $null -ne $pluginRuntimeShutdownCall -and
    $null -ne $pluginAwakeRollbackCatch -and
    $pluginRuntimeInitializeCall.Offset -lt $pluginRuntimeShutdownCall.Offset) `
    "Plugin patch/event-hook failure no longer shuts down an initialized runtime."

$detectionStateDefinition = $runtimeDefinition.NestedTypes |
    Where-Object Name -eq "ServerDetectionState"
$stateResponseProperties = @($detectionStateDefinition.Properties |
    Where-Object { $_.PropertyType.FullName -eq "ServerManager.DetectionAction" })
Assert-True ($stateResponseProperties.Count -eq 2 -and
    $stateResponseProperties.Name -contains "CheatDetectionResponse" -and
    $stateResponseProperties.Name -contains "StatLimitResponse") `
    "A detection session must pin only the common cheat and stat responses."
$stateConstructor = $detectionStateDefinition.Methods |
    Where-Object Name -eq ".ctor" | Select-Object -First 1
Assert-True ($stateConstructor.Parameters.Count -eq 5 -and
    @($stateConstructor.Parameters | Where-Object {
        $_.ParameterType.FullName -eq "ServerManager.DetectionAction"
    }).Count -eq 2) "The detection session retains independent legacy action constructor parameters."
$pendingEvidenceProperty = $detectionStateDefinition.Properties |
    Where-Object Name -eq "PendingKickEvidence"
$applyDetectionActionDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "ApplyDetectionAction"
$pendingKickDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "ProcessPendingDetectionKicks"
$terminalDetectionDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "ExecuteTerminalDetectionAction"
$clientSecurityMessage =
    "The server security policy ended this connection. " +
    "If you believe this is an error, contact a server administrator."
$clientSecurityMessageField = $runtimeDefinition.Fields |
    Where-Object Name -eq "ClientSecurityPolicyRejectionMessage"
$detectionRejectionDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.DetectionRejectionMessage"
$applyDetectionCalls = @(
    $applyDetectionActionDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$applyTerminalCall = $applyDetectionCalls |
    Where-Object { $_.Operand.Name -eq "ExecuteTerminalDetectionAction" } |
    Select-Object -First 1
$pendingKickCalls = @(
    $pendingKickDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$pendingTerminalCall = $pendingKickCalls |
    Where-Object { $_.Operand.Name -eq "ExecuteTerminalDetectionAction" } |
    Select-Object -First 1
$terminalInstructions = @($terminalDetectionDefinition.Body.Instructions)
$persistBanCall = $terminalInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "Add" -and
        $_.Operand.DeclaringType.FullName -eq "SyncedList"
    } |
    Select-Object -First 1
$terminalMessageLiteral = $terminalInstructions |
    Where-Object {
        $_.OpCode.Name -eq "ldstr" -and
        $_.Operand -ceq $clientSecurityMessage
    } |
    Select-Object -First 1
$terminalRejectionCall = $terminalInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "SendServerRejection" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$terminalKickCall = $terminalInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "InternalKick" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ValheimPrivateAccess"
    } |
    Select-Object -First 1
Assert-True (
    $null -eq $pendingEvidenceProperty -and
    $null -eq $detectionRejectionDefinition -and
    $null -ne $clientSecurityMessageField -and
    $clientSecurityMessageField.HasConstant -and
    $clientSecurityMessageField.Constant -ceq $clientSecurityMessage -and
    $null -ne $applyTerminalCall -and
    $null -ne $pendingTerminalCall -and
    $null -ne $persistBanCall -and
    $null -ne $terminalMessageLiteral -and
    $null -ne $terminalRejectionCall -and
    $null -ne $terminalKickCall -and
    $persistBanCall.Offset -lt $terminalRejectionCall.Offset -and
    $terminalMessageLiteral.Offset -lt $terminalRejectionCall.Offset -and
    $terminalRejectionCall.Offset -lt $terminalKickCall.Offset) `
    "Terminal detection lost its generic client message, Ban-persistence ordering, or exact-peer kick."

foreach ($pendingRejectionMethodName in @(
    "HandleServerFinalSaveBegin",
    "HandleServerCharacterFragment")) {
    $pendingRejectionMethod = $runtimeDefinition.Methods |
        Where-Object Name -eq $pendingRejectionMethodName
    $pendingRejectionInstructions = @(
        $pendingRejectionMethod.Body.Instructions)
    $pendingMessageLiteral = $pendingRejectionInstructions |
        Where-Object {
            $_.OpCode.Name -eq "ldstr" -and
            $_.Operand -ceq $clientSecurityMessage
        } |
        Select-Object -First 1
    $pendingRejectionCall = $pendingRejectionInstructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq "SendServerRejection" -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerRuntime" -and
            $null -ne $pendingMessageLiteral -and
            $_.Offset -gt $pendingMessageLiteral.Offset
        } |
        Select-Object -First 1
    Assert-True (
        $null -ne $pendingMessageLiteral -and
        $null -ne $pendingRejectionCall -and
        (Test-CecilReachable `
            -Start $pendingMessageLiteral `
            -Target $pendingRejectionCall)) `
        ("$pendingRejectionMethodName no longer uses the fixed " +
         "client-safe security-policy rejection.")
}

$rejectDetectionProtocolDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "RejectDetectionProtocol"
$rejectDetectionInstructions = @(
    $rejectDetectionProtocolDefinition.Body.Instructions)
$rejectDetailLoads = @(
    $rejectDetectionInstructions |
        Where-Object { $_.OpCode.Name -eq "ldarg.1" })
$rejectDetailSanitizeCall = $rejectDetectionInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "SanitizeRemoteRejectMessage" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$rejectDetailLogCall = $rejectDetectionInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "LogWarning"
    } |
    Select-Object -First 1
$rejectProtocolMessageLiteral = $rejectDetectionInstructions |
    Where-Object {
        $_.OpCode.Name -eq "ldstr" -and
        $_.Operand -ceq $clientSecurityMessage
    } |
    Select-Object -First 1
$rejectProtocolSendCall = $rejectDetectionInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "SendServerRejection" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
Assert-True (
    $rejectDetailLoads.Count -eq 1 -and
    $null -ne $rejectDetailSanitizeCall -and
    $null -ne $rejectDetailLogCall -and
    $null -ne $rejectProtocolMessageLiteral -and
    $null -ne $rejectProtocolSendCall -and
    $rejectDetailLoads[0].Offset -lt $rejectDetailSanitizeCall.Offset -and
    $rejectDetailSanitizeCall.Offset -lt $rejectDetailLogCall.Offset -and
    $rejectDetailLogCall.Offset -lt $rejectProtocolMessageLiteral.Offset -and
    $rejectProtocolMessageLiteral.Offset -lt $rejectProtocolSendCall.Offset) `
    "Detection-protocol details are no longer server-log-only or the wire rejection is not generic."

$handleDetectionReportDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "HandleServerDetectionReport"
$handleDetectionReportInstructions = @(
    $handleDetectionReportDefinition.Body.Instructions)
$decodeDetectionCall = $handleDetectionReportInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "TryDecode" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.DetectionReportCodec"
    } |
    Select-Object -First 1
$decodeErrorMessageCall = $handleDetectionReportInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "get_SafeMessage" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ProtocolRejection" -and
        $null -ne $decodeDetectionCall -and
        $_.Offset -gt $decodeDetectionCall.Offset
    } |
    Select-Object -First 1
$decodeErrorRejectCall = $handleDetectionReportInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "RejectDetectionProtocol" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $null -ne $decodeErrorMessageCall -and
        $_.Offset -gt $decodeErrorMessageCall.Offset
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $decodeDetectionCall -and
    $null -ne $decodeErrorMessageCall -and
    $null -ne $decodeErrorRejectCall -and
    (Test-CecilReachable `
        -Start $decodeDetectionCall `
        -Target $decodeErrorRejectCall)) `
    "Malformed detection reports no longer route their detail through the generic protocol rejection."

$beforeNewConnectionDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "BeforeNewConnection"
$beforeNewConnectionInstructions = @(
    $beforeNewConnectionDefinition.Body.Instructions)
foreach ($securityRpcName in @(
    "sighsorry.ServerManager.Detection.v1",
    "sighsorry.ServerManager.AdminEntitlement.v1")) {
    $securityRpcLiteral = $beforeNewConnectionInstructions |
        Where-Object {
            $_.OpCode.Name -eq "ldstr" -and
            $_.Operand -ceq $securityRpcName
        } |
        Select-Object -First 1
    $securityErrorHandler = $beforeNewConnectionInstructions |
        Where-Object {
            $_.OpCode.Name -eq "ldftn" -and
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq "OnSecurityRawTransportError" -and
            $null -ne $securityRpcLiteral -and
            $_.Offset -gt $securityRpcLiteral.Offset
        } |
        Select-Object -First 1
    $securityRegisterCall = $beforeNewConnectionInstructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq "Register" -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.RawProtocolRpcTransport" -and
            $null -ne $securityErrorHandler -and
            $_.Offset -gt $securityErrorHandler.Offset
        } |
        Select-Object -First 1
    Assert-True (
        $null -ne $securityRpcLiteral -and
        $null -ne $securityErrorHandler -and
        $null -ne $securityRegisterCall -and
        $securityRpcLiteral.Offset -lt $securityErrorHandler.Offset -and
        $securityErrorHandler.Offset -lt $securityRegisterCall.Offset) `
        ("$securityRpcName no longer installs the generic security " +
         "transport-error handler.")
}

$securityTransportErrorDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "OnSecurityRawTransportError"
$securityTransportInstructions = @(
    $securityTransportErrorDefinition.Body.Instructions)
$securityTransportDetailCall = $securityTransportInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "get_SafeMessage" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ProtocolRejection"
    } |
    Select-Object -First 1
$securityTransportServerCall = $securityTransportInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "RejectDetectionProtocol"
    } |
    Select-Object -First 1
$securityTransportMessageLiteral = $securityTransportInstructions |
    Where-Object {
        $_.OpCode.Name -eq "ldstr" -and
        $_.Operand -ceq $clientSecurityMessage
    } |
    Select-Object -First 1
$securityTransportClientCall = $securityTransportInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "FailClient" -and
        $null -ne $securityTransportMessageLiteral -and
        $_.Offset -gt $securityTransportMessageLiteral.Offset
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $securityTransportDetailCall -and
    $null -ne $securityTransportServerCall -and
    $null -ne $securityTransportMessageLiteral -and
    $null -ne $securityTransportClientCall -and
    $securityTransportDetailCall.Offset -lt
        $securityTransportServerCall.Offset -and
    $securityTransportServerCall.Offset -lt
        $securityTransportMessageLiteral.Offset -and
    $securityTransportMessageLiteral.Offset -lt
        $securityTransportClientCall.Offset) `
    "Security transport failures no longer remain generic on both server and client paths."

$decodeRejectDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "DecodeRejectMessage"
$decodeRejectInstructions = @($decodeRejectDefinition.Body.Instructions)
$decodedPayloadCodecCall = $decodeRejectInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "DecodeRejectPayload" -and
        $_.Operand.DeclaringType.FullName -eq "ServerManager.ProtocolPacketCodec"
    } |
    Select-Object -First 1
$cheatDetectedCodeLiteral = $decodeRejectInstructions |
    Where-Object {
        $_.OpCode.Name -eq "ldc.i4" -and
        [int]$_.Operand -eq 1701
    } |
    Select-Object -First 1
$detectionProtocolCodeLiteral = $decodeRejectInstructions |
    Where-Object {
        $_.OpCode.Name -eq "ldc.i4" -and
        [int]$_.Operand -eq 1700
    } |
    Select-Object -First 1
$decodedSecurityMessageLiteral = $decodeRejectInstructions |
    Where-Object {
        $_.OpCode.Name -eq "ldstr" -and
        $_.Operand -ceq $clientSecurityMessage
    } |
    Select-Object -First 1
$ordinaryCodePrefixLiteral = $decodeRejectInstructions |
    Where-Object {
        $_.OpCode.Name -eq "ldstr" -and
        $_.Operand -ceq "ServerManager ["
    } |
    Select-Object -First 1
$ordinaryMessageSanitizeCall = $decodeRejectInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "SanitizeRemoteRejectMessage" -and
        $null -ne $ordinaryCodePrefixLiteral -and
        $_.Offset -gt $ordinaryCodePrefixLiteral.Offset
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $decodedPayloadCodecCall -and
    $null -ne $cheatDetectedCodeLiteral -and
    $null -ne $detectionProtocolCodeLiteral -and
    $null -ne $decodedSecurityMessageLiteral -and
    $null -ne $ordinaryCodePrefixLiteral -and
    $null -ne $ordinaryMessageSanitizeCall -and
    $decodedPayloadCodecCall.Offset -lt $cheatDetectedCodeLiteral.Offset -and
    $cheatDetectedCodeLiteral.Offset -lt
        $decodedSecurityMessageLiteral.Offset -and
    $detectionProtocolCodeLiteral.Offset -lt
        $decodedSecurityMessageLiteral.Offset -and
    -not (Test-CecilReachable `
        -Start $decodedSecurityMessageLiteral `
        -Target $ordinaryCodePrefixLiteral) -and
    $ordinaryCodePrefixLiteral.Offset -lt
        $ordinaryMessageSanitizeCall.Offset) `
    "The client decoder can expose a security reject code prefix or server-supplied detail."

$clientDetectionDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ClientDetectionAgent"
$clientDetectionTickDefinition = $clientDetectionDefinition.Methods |
    Where-Object {
        $_.Name -eq "Tick" -and
        $_.Parameters.Count -eq 1 -and
        $_.Parameters[0].ParameterType.FullName -eq "System.Boolean"
    }
$carryTickDefinition = $clientDetectionDefinition.Methods |
    Where-Object Name -eq "TickCarryWeightDetector"
$runtimeTickDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "Tick"
$runtimeTickCalls = @(
    $runtimeTickDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$readyBeforeDetectionTick = $runtimeTickCalls |
    Where-Object {
        $_.Operand.Name -eq "get_ReadyAcknowledgementSent" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime/ClientConnection"
    } |
    Select-Object -First 1
$clientDetectionTickCall = $runtimeTickCalls |
    Where-Object {
        $_.Operand.Name -eq "Tick" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ClientDetectionAgent"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $clientDetectionTickDefinition -and
    $null -ne $carryTickDefinition -and
    $null -ne $readyBeforeDetectionTick -and
    $null -ne $clientDetectionTickCall -and
    $readyBeforeDetectionTick.Offset -lt $clientDetectionTickCall.Offset) `
    "Carry-weight polling is no longer gated by client Ready ACK state."

$maximumDamagePatchDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.MaximumDamageLimitPatch"
$maximumDamagePrefixDefinition = $maximumDamagePatchDefinition.Methods |
    Where-Object Name -eq "Prefix"
$beforeLocalDamageDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "BeforeLocalPlayerDamage"
$maximumDamagePrefixCall = $maximumDamagePrefixDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "BeforeLocalPlayerDamage" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$routedDamagePatchDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ServerRoutedDamageLimitPatch"
$routedDamagePrefixDefinition = $routedDamagePatchDefinition.Methods |
    Where-Object Name -eq "Prefix"
$routedDamagePrefixCall = $routedDamagePrefixDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "BeforeServerRoutedRpcDamage" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$serverRoutedDamageDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "BeforeServerRoutedRpcDamage"
$serverRoutedDamageMembers = @(
    $serverRoutedDamageDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MemberReference] })
$serverRoutedIdentityCall = $serverRoutedDamageMembers |
    Where-Object {
        $_.Operand.Name -eq "TryResolveActiveDetectionPeer" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$serverRoutedCharacterField = $serverRoutedDamageMembers |
    Where-Object {
        $_.Operand.Name -eq "m_characterID" -and
        $_.Operand.DeclaringType.FullName -eq "ZNetPeer"
    } |
    Select-Object -First 1
$payloadSenderReads = @(
    $serverRoutedDamageMembers |
        Where-Object { $_.Operand.Name -eq "m_senderPeerID" })
$serverRoutedTelemetryCall = $serverRoutedDamageMembers |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "ObserveRoutedDamage" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.PlayerLogging.PlayerActivityRuntime"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $maximumDamagePrefixCall -and
    $maximumDamagePrefixDefinition.Parameters.Count -eq 2 -and
    $maximumDamagePrefixDefinition.Parameters[0].ParameterType.FullName -eq
        "System.Object" -and
    $maximumDamagePrefixDefinition.Parameters[1].ParameterType.FullName -eq
        "HitData" -and
    $beforeLocalDamageDefinition.Parameters.Count -eq 2 -and
    $beforeLocalDamageDefinition.Parameters[0].ParameterType.FullName -eq
        "System.Object" -and
    $beforeLocalDamageDefinition.Parameters[1].ParameterType.FullName -eq
        "HitData" -and
    $null -ne $routedDamagePrefixCall -and
    $null -ne $serverRoutedIdentityCall -and
    $null -ne $serverRoutedCharacterField -and
    $null -ne $serverRoutedTelemetryCall -and
    $serverRoutedTelemetryCall.Operand.Parameters.Count -eq 4 -and
    $serverRoutedTelemetryCall.Operand.Parameters[0].ParameterType.FullName -eq
        "ZRpc" -and
    $serverRoutedTelemetryCall.Operand.Parameters[1].ParameterType.FullName -eq
        "ServerManager.RoutedDamageObservation" -and
    $serverRoutedTelemetryCall.Operand.Parameters[2].ParameterType.FullName -eq
        "System.Boolean" -and
    $serverRoutedTelemetryCall.Operand.Parameters[3].ParameterType.FullName -eq
        "System.Boolean" -and
    $payloadSenderReads.Count -eq 0) `
    "Damage enforcement or target-aware telemetry lost its patched, connection-bound wiring."

$gameplayValidationDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.GameplayLimitValidation"
$routedDamageObservationDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.RoutedDamageObservation"
$inspectRoutedDamageDefinition = $gameplayValidationDefinition.Methods |
    Where-Object Name -eq "InspectRoutedDamage"
$mutableObservationFields = @(
    $routedDamageObservationDefinition.Fields |
        Where-Object { -not $_.IsStatic -and -not $_.IsInitOnly })
Assert-True (
    $null -ne $routedDamageObservationDefinition -and
    $routedDamageObservationDefinition.IsSealed -and
    $mutableObservationFields.Count -eq 0 -and
    $inspectRoutedDamageDefinition.Parameters.Count -eq 2 -and
    $inspectRoutedDamageDefinition.Parameters[1].IsOut -and
    $inspectRoutedDamageDefinition.Parameters[1].ParameterType.FullName -eq
        "ServerManager.RoutedDamageObservation&") `
    "The routed-damage decoder lost its immutable observation result."
$inspectRoutedDamageCalls = @(
    $inspectRoutedDamageDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$unboundedPackageReads = @(
    $inspectRoutedDamageCalls |
        Where-Object {
            $_.Operand.Name -eq "ReadPackage" -and
            $_.Operand.DeclaringType.FullName -eq "ZPackage"
        })
$innerLengthRead = $inspectRoutedDamageCalls |
    Where-Object {
        $_.Operand.Name -eq "ReadInt" -and
        $_.Operand.DeclaringType.FullName -eq "ZPackage"
    } |
    Select-Object -Last 1
$boundedInnerRead = $inspectRoutedDamageCalls |
    Where-Object {
        $_.Operand.Name -eq "ReadByteArray" -and
        $_.Operand.DeclaringType.FullName -eq "ZPackage" -and
        $_.Operand.Parameters.Count -eq 1 -and
        $_.Operand.Parameters[0].ParameterType.FullName -eq "System.Int32"
    } |
    Select-Object -First 1
Assert-True (
    $unboundedPackageReads.Count -eq 0 -and
    $null -ne $innerLengthRead -and
    $null -ne $boundedInnerRead -and
    $innerLengthRead.Offset -lt $boundedInnerRead.Offset) `
    "The routed-damage parser reintroduced an unbounded inner-package allocation."

$resolveDataRootDefinition = $dataRootDefinition.Methods |
    Where-Object Name -eq "ResolveCurrentPath" |
    Select-Object -First 1
$resolveDataRootCalls = @(
    $resolveDataRootDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$getValheimSavePathCall = $resolveDataRootCalls |
    Where-Object {
        $_.Operand.Name -eq "GetSaveDataPath" -and
        $_.Operand.DeclaringType.FullName -eq "Utils"
    } |
    Select-Object -First 1
$earlyPersistentPathCalls = @(
    $resolveDataRootCalls |
        Where-Object {
            $_.Operand.Name -eq "get_persistentDataPath" -and
            $_.Operand.DeclaringType.FullName -eq "UnityEngine.Application"
        })
Assert-True (
    $null -ne $resolveDataRootDefinition -and
    $null -ne $getValheimSavePathCall -and
    $earlyPersistentPathCalls.Count -eq 0) `
    "The server-data root no longer follows Valheim's resolved local save path."

$dataRootInstructions = @(
    $dataRootDefinition.Methods |
        Where-Object HasBody |
        ForEach-Object { $_.Body.Instructions })
$dataRootCalls = @(
    $dataRootInstructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$forbiddenDataRootCalls = @(
    $dataRootCalls |
        Where-Object {
            ($_.Operand.DeclaringType.FullName -eq "BepInEx.Paths" -and
                $_.Operand.Name -eq "get_ConfigPath") -or
            ($_.Operand.DeclaringType.FullName -eq "System.IO.File" -and
                $_.Operand.Name -eq "Copy") -or
            ($_.Operand.DeclaringType.FullName -eq "System.IO.Directory" -and
                $_.Operand.Name -eq "Move")
        })
$obsoleteDataRootMarkerLoads = @(
    $dataRootInstructions |
        Where-Object {
            $_.OpCode.Code.ToString() -eq "Ldstr" -and
            [string]$_.Operand -eq ".data-root-v1"
        })
Assert-True (
    $forbiddenDataRootCalls.Count -eq 0 -and
    $obsoleteDataRootMarkerLoads.Count -eq 0) `
    "ServerDataRoot regained config-root copy/move migration or its obsolete marker."

$pluginAwakeDefinition = $pluginTypeDefinition.Methods |
    Where-Object Name -eq "Awake" |
    Select-Object -First 1
$awakeDirectoryCreationCalls = @(
    $pluginAwakeDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq "CreateDirectory" -and
            $_.Operand.DeclaringType.FullName -eq "System.IO.Directory"
        })
Assert-True ($awakeDirectoryCreationCalls.Count -eq 0) `
    "Plugin.Awake creates server storage before Valheim applies -savedir."

$beforeNetworkStartDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "BeforeNetworkStart" |
    Select-Object -First 1
$beforeNetworkStartCalls = @(
    $beforeNetworkStartDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$bindDataRootCall = $beforeNetworkStartCalls |
    Where-Object {
        $_.Operand.Name -eq "BindAndPrepare" -and
        $_.Operand.DeclaringType.FullName -eq "ServerManager.ServerDataRoot"
    } |
    Select-Object -First 1
# The earlier !IsServer() branch starts client-only manifest preparation.
# Test the nearest server-role check that actually guards server-root binding,
# while retaining the false-branch exclusion checks below.
$serverRoleCall = $beforeNetworkStartCalls |
    Where-Object {
        $null -ne $bindDataRootCall -and
        $_.Offset -lt $bindDataRootCall.Offset -and
        $_.Operand.Name -eq "IsServer" -and
        $_.Operand.DeclaringType.FullName -eq "ZNet"
    } |
    Sort-Object Offset -Descending |
    Select-Object -First 1
$ensureIntegrityServiceCall = $beforeNetworkStartCalls |
    Where-Object {
        $_.Operand.Name -eq "EnsureIntegrityService" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$serverStartedCall = $beforeNetworkStartCalls |
    Where-Object {
        $_.Operand.Name -eq "OnServerStarted" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.Events.ServerEventRuntime"
    } |
    Select-Object -First 1
$serverRoleGuard = if (
    $null -ne $serverRoleCall -and
    $null -ne $bindDataRootCall) {
    $beforeNetworkStartDefinition.Body.Instructions |
        Where-Object {
            $_.Offset -gt $serverRoleCall.Offset -and
            $_.Offset -lt $bindDataRootCall.Offset -and
            $_.OpCode.FlowControl.ToString() -eq "Cond_Branch" -and
            $_.Operand -is [Mono.Cecil.Cil.Instruction]
        } |
        Select-Object -First 1
} else {
    $null
}
$serverRoleFalseTarget = if (
    $null -ne $serverRoleGuard -and
    $serverRoleGuard.Operand -is [Mono.Cecil.Cil.Instruction]) {
    $serverRoleGuard.Operand
} else {
    $null
}
# Debug short-circuit lowering for IsServer() && !alreadyPrepared writes false
# to a temporary and branches after reloading that exact temporary.
if ($null -ne $serverRoleFalseTarget -and
    $serverRoleFalseTarget.OpCode.Name -eq 'ldc.i4.0') {
    $store = $serverRoleFalseTarget.Next
    $load = $store.Next
    $branch = $load.Next
    if ($store.OpCode.Name.StartsWith('stloc') -and
        $load.OpCode.Name -eq $store.OpCode.Name.Replace('stloc', 'ldloc') -and
        $load.Operand -eq $store.Operand -and
        $branch.OpCode.Name.StartsWith('brfalse')) {
        $serverRoleFalseTarget = $branch.Operand
    }
}
Assert-True (
    $null -ne $serverRoleCall -and
    $null -ne $serverRoleFalseTarget -and
    $null -ne $bindDataRootCall -and
    $null -ne $ensureIntegrityServiceCall -and
    $null -ne $serverStartedCall -and
    $serverRoleCall.Offset -lt $bindDataRootCall.Offset -and
    $bindDataRootCall.Offset -lt $ensureIntegrityServiceCall.Offset -and
    $ensureIntegrityServiceCall.Offset -lt $serverStartedCall.Offset -and
    $serverRoleFalseTarget.Offset -gt $serverStartedCall.Offset) `
    "Server startup does not eagerly prepare the mod folders after binding the server-data root."

$integrityServiceConstructor = $integrityServiceDefinition.Methods |
    Where-Object {
        $_.IsConstructor -and
        -not $_.IsStatic -and
        $_.Parameters.Count -eq 3 -and
        $_.Parameters[0].ParameterType.FullName -eq "System.String" -and
        $_.Parameters[1].ParameterType.FullName -eq
            "ServerManager.IntegrityLimits" -and
        $_.Parameters[2].ParameterType.FullName -eq
            "BepInEx.Logging.ManualLogSource"
    } |
    Select-Object -First 1
$integrityConstructorCalls = if ($null -ne $integrityServiceConstructor) {
    @($integrityServiceConstructor.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
} else {
    @()
}
$policyStoreConstruction = $integrityConstructorCalls |
    Where-Object {
        $_.OpCode.Name -eq "newobj" -and
        $_.Operand.Name -eq ".ctor" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.IntegrityPolicyStore" -and
        $_.Operand.Parameters.Count -eq 4 -and
        $_.Operand.Parameters[0].ParameterType.FullName -eq "System.String" -and
        $_.Operand.Parameters[1].ParameterType.FullName -eq
            "ServerManager.IntegrityLimits" -and
        $_.Operand.Parameters[2].ParameterType.FullName -eq
            "ServerManager.ReferencePluginPolicyScanner" -and
        $_.Operand.Parameters[3].ParameterType.FullName -eq
            "ServerManager.IntegrityManifestEntry"
    } |
    Select-Object -First 1
$initialPolicyReloadCall = $integrityConstructorCalls |
    Where-Object {
        $_.Operand.Name -eq "Reload" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerIntegrityService"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $policyStoreConstruction -and
    $null -ne $initialPolicyReloadCall -and
    $policyStoreConstruction.Offset -lt $initialPolicyReloadCall.Offset -and
    @($integrityConstructorCalls | Where-Object { $_.Operand.Name -in @('TryReload', 'PrepareReload', 'PublishReload') }).Count -eq 0) `
    "Integrity-service startup scans DLLs synchronously or does not schedule its initial policy."
$initialWatcherCalls = @($integrityConstructorCalls | Where-Object {
    $_.Operand.Name -in @('CreatePolicyRootWatcher', 'CreatePolicySourceWatcher')
})
Assert-True ($initialWatcherCalls.Count -eq 3 -and
    @($initialWatcherCalls | Where-Object { $_.Offset -gt $initialPolicyReloadCall.Offset }).Count -eq 0) `
    "The first background policy scan is scheduled before all folder watchers are armed."

$integrityTick = $integrityServiceDefinition.Methods | Where-Object Name -eq 'Tick' | Select-Object -First 1
$integrityTickCalls = @($integrityTick.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$integrityReady = $integrityTickCalls | Where-Object { $_.Operand.Name -eq 'get_IsCompleted' } | Select-Object -First 1
$integrityAwaiter = $integrityTickCalls | Where-Object { $_.Operand.Name -eq 'GetAwaiter' } | Select-Object -First 1
$integrityPublish = $integrityTickCalls | Where-Object { $_.Operand.Name -eq 'PublishReload' } | Select-Object -First 1
$integrityRun = $integrityTickCalls | Where-Object {
    $_.Operand.DeclaringType.FullName -eq 'System.Threading.Tasks.Task' -and $_.Operand.Name -eq 'Run'
} | Select-Object -First 1
Assert-True ($null -ne $integrityReady -and $null -ne $integrityAwaiter -and $null -ne $integrityPublish -and
    $null -ne $integrityRun -and $integrityReady.Offset -lt $integrityAwaiter.Offset -and
    $integrityAwaiter.Offset -lt $integrityPublish.Offset -and
    @($integrityTickCalls | Where-Object { $_.Operand.Name -in @('TryReload', 'PrepareReload', 'Wait', 'WaitAll', 'WaitAny', 'get_Result') }).Count -eq 0) `
    "Policy Tick blocks, hashes locally, or consumes a worker before its completion guard."
$integrityMethods = @($integrityServiceDefinition.Methods) + @($integrityServiceDefinition.NestedTypes | ForEach-Object { $_.Methods })
$policyWorkerMethods = @($integrityMethods | Where-Object {
    $_.HasBody -and @($_.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'PrepareReload'
    }).Count -gt 0
})
Assert-True ($policyWorkerMethods.Count -eq 1 -and $policyWorkerMethods[0].DeclaringType -ne $integrityServiceDefinition -and
    @($policyWorkerMethods[0].Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        ($_.Operand.Name -in @('PublishReload', 'LogReload') -or $_.Operand.DeclaringType.FullName.StartsWith('UnityEngine.'))
    }).Count -eq 0) `
    "Background policy work publishes/logs/uses Unity instead of returning an immutable candidate."
$reloadSchedule = $integrityServiceDefinition.Methods | Where-Object Name -eq 'ScheduleReloadLocked' | Select-Object -First 1
Assert-True (@($reloadSchedule.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Cancel'
}).Count -gt 0 -and @($reloadSchedule.Body.Instructions | Where-Object {
    $_.OpCode.Name -eq 'stfld' -and $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq '_reloadTask'
}).Count -eq 0) 'Folder invalidation must cancel but retain the active task until it completes (single flight).'
$reloadDisposeDefinition = $integrityServiceDefinition.Methods | Where-Object Name -eq 'Dispose' | Select-Object -First 1
$reloadDisposeCalls = @($reloadDisposeDefinition.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
Assert-True (@($reloadDisposeCalls | Where-Object { $_.Operand.Name -eq 'Cancel' }).Count -gt 0 -and
    @($reloadDisposeCalls | Where-Object { $_.Operand.Name -eq 'ContinueWith' }).Count -gt 0 -and
    @($reloadDisposeCalls | Where-Object { $_.Operand.Name -in @('Wait', 'WaitAll', 'WaitAny', 'get_Result', 'GetAwaiter') }).Count -eq 0) `
    "Integrity disposal must cancel and observe asynchronously, never wait for disk work."
$abandonedObservation = @($integrityMethods | Where-Object { $_.HasBody } | ForEach-Object { $_.Body.Instructions } | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_Exception' -and
    $_.Operand.DeclaringType.FullName.StartsWith('System.Threading.Tasks.Task')
})
Assert-True ($abandonedObservation.Count -gt 0) 'Abandoned policy worker faults are not observed.'

$referenceScannerDefinition = $pluginDefinition.MainModule.GetType('ServerManager.ReferencePluginPolicyScanner')
$referenceCalls = @($referenceScannerDefinition.Methods | Where-Object HasBody | ForEach-Object { $_.Body.Instructions } |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
Assert-True (@($referenceCalls | Where-Object {
    $_.Operand.Name -eq 'ReadAllBytes' -or $_.Operand.DeclaringType.FullName -eq 'System.IO.MemoryStream'
}).Count -eq 0) 'Reference scanning materializes complete DLL byte arrays instead of streaming.'
$referenceInMemory = @($referenceCalls | Where-Object { $_.Operand.Name -eq 'set_InMemory' })
Assert-True ($referenceInMemory.Count -eq 1 -and (Get-CecilIntConstant $referenceInMemory[0].Previous) -eq 0) `
    'Cecil reference metadata parsing buffers the whole DLL in memory.'
Assert-True (@($referenceScannerDefinition.Fields | Where-Object {
    $_.Name -in @('MaximumReferenceDllBytes', 'MaximumTotalReferenceBytes')
}).Count -eq 0) 'Reference DLL byte-size caps remain after the streaming change.'

$watcherErrorDefinition = $integrityServiceDefinition.Methods |
    Where-Object {
        $_.Name -eq "OnWatcherError" -and
        $_.ReturnType.FullName -eq "System.Void" -and
        $_.Parameters.Count -eq 2 -and
        $_.Parameters[0].ParameterType.FullName -eq "System.Object" -and
        $_.Parameters[1].ParameterType.FullName -eq
            "System.IO.ErrorEventArgs"
    } |
    Select-Object -First 1
$watcherErrorInstructions = if ($null -ne $watcherErrorDefinition) {
    @($watcherErrorDefinition.Body.Instructions)
} else {
    @()
}
$rootWatcherComparisonLoad = $watcherErrorInstructions |
    Where-Object {
        $_.OpCode.Name -eq "ldfld" -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.Name -eq "_rootWatcher" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerIntegrityService" -and
        $null -ne $_.Next -and
        ($_.Next.OpCode.Name -eq "ceq" -or
         $_.Next.OpCode.Name -like "bne.un*" -or
         ($_.Next.Operand -is [Mono.Cecil.MethodReference] -and
          $_.Next.Operand.Name -eq "ReferenceEquals" -and
          $_.Next.Operand.DeclaringType.FullName -eq "System.Object"))
    } |
    Select-Object -First 1
$rootWatcherBranch = if (
    $null -ne $rootWatcherComparisonLoad -and
    $rootWatcherComparisonLoad.Next.OpCode.FlowControl.ToString() -eq
        "Cond_Branch") {
    $rootWatcherComparisonLoad.Next
} elseif ($null -ne $rootWatcherComparisonLoad) {
    Get-FirstConditionalBranch `
        -Start $rootWatcherComparisonLoad `
        -Before $null
} else {
    $null
}
$rootWatcherFalseTarget = if (
    $null -ne $rootWatcherBranch -and
    ($rootWatcherBranch.OpCode.Name -like "brfalse*" -or
     $rootWatcherBranch.OpCode.Name -like "bne.un*") -and
    $rootWatcherBranch.Operand -is [Mono.Cecil.Cil.Instruction]) {
    $rootWatcherBranch.Operand
} else {
    $null
}
$watcherRecoverySchedule = $watcherErrorInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "ScheduleReloadLocked" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerIntegrityService"
    } |
    Select-Object -First 1
$rootWatcherInvalidations = @{}
foreach ($watcherFieldName in @(
    "_requiredWatcher",
    "_optionalWatcher",
    "_rootWatcher")) {
    $rootWatcherInvalidations[$watcherFieldName] =
        $watcherErrorInstructions |
            Where-Object {
                $null -ne $rootWatcherBranch -and
                $null -ne $rootWatcherFalseTarget -and
                $_.Offset -gt $rootWatcherBranch.Offset -and
                $_.Offset -lt $rootWatcherFalseTarget.Offset -and
                $_.OpCode.Name -eq "stfld" -and
                $_.Operand -is [Mono.Cecil.FieldReference] -and
                $_.Operand.Name -eq $watcherFieldName -and
                $_.Operand.DeclaringType.FullName -eq
                    "ServerManager.ServerIntegrityService" -and
                $null -ne $_.Previous -and
                $_.Previous.OpCode.Name -eq "ldnull"
            } |
            Select-Object -First 1
}
Assert-True (
    $null -ne $rootWatcherFalseTarget -and
    $null -ne $watcherRecoverySchedule -and
    $null -ne $rootWatcherInvalidations["_requiredWatcher"] -and
    $null -ne $rootWatcherInvalidations["_optionalWatcher"] -and
    $null -ne $rootWatcherInvalidations["_rootWatcher"] -and
    $rootWatcherInvalidations["_requiredWatcher"].Offset -lt
        $watcherRecoverySchedule.Offset -and
    $rootWatcherInvalidations["_optionalWatcher"].Offset -lt
        $watcherRecoverySchedule.Offset -and
    $rootWatcherInvalidations["_rootWatcher"].Offset -lt
        $watcherRecoverySchedule.Offset) `
    "A root watcher failure no longer invalidates all watcher handles before recovery is scheduled."

$ensureCharacterServiceDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "EnsureServerCharacterService" |
    Select-Object -First 1
$ensureCharacterServiceCalls = @(
    $ensureCharacterServiceDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$legacyStorageMigrationCalls = @($ensureCharacterServiceCalls |
    Where-Object {
        $_.Operand.Name -like "*Migrate*Storage*" -or
        $_.Operand.Name -like "*Legacy*Storage*"
    })
$validateStorageMappingsCall = $ensureCharacterServiceCalls |
    Where-Object {
        $_.Operand.Name -eq "ValidateStorageKeyMappings" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.CharacterRepository"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $validateStorageMappingsCall -and
    $legacyStorageMigrationCalls.Count -eq 0) `
    "The fresh-only character store regained a legacy migration path."

$beforeNetworkShutdownDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "BeforeNetworkShutdown" |
    Select-Object -First 1
$prematureReleaseDataRootCalls = @(
    $beforeNetworkShutdownDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq "Release" -and
            $_.Operand.DeclaringType.FullName -eq "ServerManager.ServerDataRoot"
        })
$afterNetworkShutdownDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "AfterNetworkShutdown" |
    Select-Object -First 1
$releaseDataRootCalls = @(
    $afterNetworkShutdownDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq "Release" -and
            $_.Operand.DeclaringType.FullName -eq "ServerManager.ServerDataRoot"
        })
Assert-True (
    $prematureReleaseDataRootCalls.Count -eq 0 -and
    $releaseDataRootCalls.Count -eq 1) `
    "Server shutdown releases the data root before StopAll joins save workers or not exactly once afterward."

$serverAdminDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "IsCurrentServerAdmin"
$serverAdminCall = $serverAdminDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "IsAdmin" -and
        $_.Operand.DeclaringType.FullName -eq "ZNet"
    } |
    Select-Object -First 1
Assert-True ($null -ne $serverAdminCall) `
    "The server no longer independently revalidates admin status."

$onAdminEntitlementDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "OnAdminEntitlementPacket"
$onAdminEntitlementCalls = @(
    $onAdminEntitlementDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$isServerDirectionCall = $onAdminEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "IsServer" -and
        $_.Operand.DeclaringType.FullName -eq "ZNet"
    } |
    Select-Object -First 1
$rejectClientEntitlementCall = $onAdminEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "RejectDetectionProtocol" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$handleClientEntitlementCall = $onAdminEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "HandleClientAdminEntitlement" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$directionBranch = Get-FirstConditionalBranch `
    $isServerDirectionCall `
    $rejectClientEntitlementCall
$directionClientStart =
    if ($null -ne $directionBranch) {
        $directionBranch.Operand
    }
    else {
        $null
    }
$directionServerStart =
    if ($null -ne $directionBranch) {
        $directionBranch.Next
    }
    else {
        $null
    }
$serverDirectionReachesReject = Test-CecilReachable `
    -Start $directionServerStart `
    -Target $rejectClientEntitlementCall
$serverDirectionReachesClient = Test-CecilReachable `
    -Start $directionServerStart `
    -Target $handleClientEntitlementCall
$clientDirectionReachesHandler = Test-CecilReachable `
    -Start $directionClientStart `
    -Target $handleClientEntitlementCall
$clientDirectionReachesReject = Test-CecilReachable `
    -Start $directionClientStart `
    -Target $rejectClientEntitlementCall
Assert-True (
    $null -ne $isServerDirectionCall -and
    $null -ne $rejectClientEntitlementCall -and
    $null -ne $handleClientEntitlementCall -and
    $null -ne $directionBranch -and
    $serverDirectionReachesReject -and
    -not $serverDirectionReachesClient -and
    $clientDirectionReachesHandler -and
    -not $clientDirectionReachesReject) `
    "The server-to-client entitlement direction guard changed."

$clientEntitlementDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "HandleClientAdminEntitlement"
$clientEntitlementCalls = @(
    $clientEntitlementDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$decodeEntitlementCall = $clientEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "TryDecode" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.AdminCommandEntitlementCodec"
    } |
    Select-Object -First 1
$readyEntitlementCall = $clientEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "get_ReadyAcknowledgementSent"
    } |
    Select-Object -First 1
$readyNonceCall = $clientEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "get_Nonce" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime/ClientConnection"
    } |
    Select-Object -First 1
$fixedTimeEntitlementCalls = @(
    $clientEntitlementCalls |
        Where-Object {
            $_.Operand.Name -eq "FixedTimeEquals" -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ProtocolByteUtil"
        })
$getEntitlementSequenceCall = $clientEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "get_NextAdminEntitlementSequence"
    } |
    Select-Object -First 1
$setEntitlementSequenceCall = $clientEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "set_NextAdminEntitlementSequence"
    } |
    Select-Object -First 1
$applyEntitlementCall = $clientEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "ApplyAdminEntitlement" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.CheatCommandGuard"
    } |
    Select-Object -First 1
$clientEntitlementFailureCalls = @(
    $clientEntitlementCalls |
        Where-Object {
            $_.Operand.Name -eq "FailClient" -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerRuntime"
        })
$detectionSequenceReferences = @(
    $clientEntitlementDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MemberReference] -and
            $_.Operand.Name -like "*NextDetectionSequence*"
        })
Assert-True (
    $null -ne $decodeEntitlementCall -and
    $null -ne $readyEntitlementCall -and
    $fixedTimeEntitlementCalls.Count -eq 2 -and
    $null -ne $getEntitlementSequenceCall -and
    $null -ne $setEntitlementSequenceCall -and
    $null -ne $applyEntitlementCall -and
    $decodeEntitlementCall.Offset -lt $readyEntitlementCall.Offset -and
    $readyEntitlementCall.Offset -lt
        $fixedTimeEntitlementCalls[0].Offset -and
    $fixedTimeEntitlementCalls[1].Offset -lt
        $getEntitlementSequenceCall.Offset -and
    $getEntitlementSequenceCall.Offset -lt
        $setEntitlementSequenceCall.Offset -and
    $setEntitlementSequenceCall.Offset -lt
        $applyEntitlementCall.Offset -and
    $clientEntitlementFailureCalls.Count -ge 5 -and
    $detectionSequenceReferences.Count -eq 0) `
    "The client entitlement validation or sequence isolation changed."

foreach ($clientGuard in @(
    $decodeEntitlementCall,
    $getEntitlementSequenceCall,
    $setEntitlementSequenceCall)) {
    $clientGuardCanBeSkipped = Test-CecilReachable `
        -Start $clientEntitlementDefinition.Body.Instructions[0] `
        -Target $applyEntitlementCall `
        -Blocked @($clientGuard)
    Assert-True (-not $clientGuardCanBeSkipped) `
        ("A client entitlement guard no longer dominates local apply: " +
         $clientGuard.Operand.Name)
}

$readinessBranch = Get-FirstConditionalBranch `
    $readyNonceCall `
    $fixedTimeEntitlementCalls[0]
$readinessTargetReachesApply =
    $null -ne $readinessBranch -and
    (Test-CecilReachable `
        -Start $readinessBranch.Operand `
        -Target $applyEntitlementCall)
$readinessFallthroughReachesApply =
    $null -ne $readinessBranch -and
    (Test-CecilReachable `
        -Start $readinessBranch.Next `
        -Target $applyEntitlementCall)
Assert-True (
    $null -ne $readyEntitlementCall -and
    $null -ne $readyNonceCall -and
    $null -ne $readinessBranch -and
    $readyEntitlementCall.Offset -lt $readinessBranch.Offset -and
    $readinessTargetReachesApply -ne
        $readinessFallthroughReachesApply) `
    "Client readiness no longer gates admin entitlement apply."

$identityFailureCall = $clientEntitlementFailureCalls |
    Where-Object {
        $_.Offset -gt $fixedTimeEntitlementCalls[1].Offset -and
        $_.Offset -lt $getEntitlementSequenceCall.Offset
    } |
    Select-Object -First 1
for ($comparisonIndex = 0;
     $comparisonIndex -lt $fixedTimeEntitlementCalls.Count;
     ++$comparisonIndex) {
    $fixedTimeCall = $fixedTimeEntitlementCalls[$comparisonIndex]
    $fixedTimeBranch = Get-FirstConditionalBranch `
        $fixedTimeCall `
        $applyEntitlementCall
    if ($comparisonIndex -eq 0) {
        Assert-True (
            $null -ne $fixedTimeBranch -and
            $null -ne $identityFailureCall -and
            -not (Test-CecilReachable `
                -Start $fixedTimeBranch.Operand `
                -Target $fixedTimeEntitlementCalls[1]) -and
            (Test-CecilReachable `
                -Start $fixedTimeBranch.Operand `
                -Target $identityFailureCall) -and
            (Test-CecilReachable `
                -Start $fixedTimeBranch.Next `
                -Target $fixedTimeEntitlementCalls[1])) `
            "The session comparison no longer gates nonce validation."
        continue
    }

    $branchTargetReachesApply =
        $null -ne $fixedTimeBranch -and
        (Test-CecilReachable `
            -Start $fixedTimeBranch.Operand `
            -Target $applyEntitlementCall)
    $fallthroughReachesApply =
        $null -ne $fixedTimeBranch -and
        (Test-CecilReachable `
            -Start $fixedTimeBranch.Next `
            -Target $applyEntitlementCall)
    Assert-True (
        $null -ne $fixedTimeBranch -and
        $branchTargetReachesApply -ne $fallthroughReachesApply) `
        ("The nonce comparison no longer gates local apply at IL_" +
         $fixedTimeCall.Offset.ToString("x4") + ".")
}

$refreshEntitlementDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "TryRefreshAdminCommandEntitlement"
$refreshEntitlementCalls = @(
    $refreshEntitlementDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$refreshSnapshotCall = $refreshEntitlementCalls |
    Where-Object { $_.Operand.Name -eq "TryGetSnapshot" } |
    Select-Object -First 1
$refreshReadyCall = $refreshEntitlementCalls |
    Where-Object { $_.Operand.Name -eq "get_State" } |
    Select-Object -First 1
$refreshAuthenticatedCall = $refreshEntitlementCalls |
    Where-Object { $_.Operand.Name -eq "get_PeerInfoAuthenticated" } |
    Select-Object -First 1
$refreshActivePeerCall = $refreshEntitlementCalls |
    Where-Object { $_.Operand.Name -eq "TryResolveActiveDetectionPeer" } |
    Select-Object -First 1
$refreshAdminCall = $refreshEntitlementCalls |
    Where-Object { $_.Operand.Name -eq "IsCurrentServerAdmin" } |
    Select-Object -First 1
$refreshConstructorCall = $refreshEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq ".ctor" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.AdminCommandEntitlement"
    } |
    Select-Object -First 1
$refreshEncodeCall = $refreshEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "Encode" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.AdminCommandEntitlementCodec"
    } |
    Select-Object -First 1
$refreshSendCall = $refreshEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "Send" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.RawProtocolRpcTransport"
    } |
    Select-Object -First 1
$refreshSequenceSetCall = $refreshEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq "set_NextAdminEntitlementSequence"
    } |
    Select-Object -First 1
$refreshTimestampSetCall = $refreshEntitlementCalls |
    Where-Object {
        $_.Operand.Name -eq
            "set_NextAdminEntitlementRefreshTimestamp"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $refreshSnapshotCall -and
    $null -ne $refreshReadyCall -and
    $null -ne $refreshAuthenticatedCall -and
    $null -ne $refreshActivePeerCall -and
    $null -ne $refreshAdminCall -and
    $null -ne $refreshConstructorCall -and
    $null -ne $refreshEncodeCall -and
    $null -ne $refreshSendCall -and
    $null -ne $refreshSequenceSetCall -and
    $null -ne $refreshTimestampSetCall -and
    $refreshSnapshotCall.Offset -lt $refreshReadyCall.Offset -and
    $refreshReadyCall.Offset -lt $refreshAuthenticatedCall.Offset -and
    $refreshAuthenticatedCall.Offset -lt $refreshActivePeerCall.Offset -and
    $refreshActivePeerCall.Offset -lt $refreshAdminCall.Offset -and
    $refreshAdminCall.Offset -lt $refreshConstructorCall.Offset -and
    $refreshConstructorCall.Offset -lt $refreshEncodeCall.Offset -and
    $refreshEncodeCall.Offset -lt $refreshSendCall.Offset -and
    $refreshSendCall.Offset -lt $refreshSequenceSetCall.Offset -and
    $refreshSequenceSetCall.Offset -lt
        $refreshTimestampSetCall.Offset) `
    "The server entitlement trust checks or send ordering changed."

foreach ($serverGrantGuard in @(
    $refreshActivePeerCall,
    $refreshAdminCall)) {
    $serverGuardCanBeSkipped = Test-CecilReachable `
        -Start $refreshEntitlementDefinition.Body.Instructions[0] `
        -Target $refreshConstructorCall `
        -Blocked @($serverGrantGuard)
    Assert-True (-not $serverGuardCanBeSkipped) `
        ("A server entitlement trust check no longer dominates construction: " +
         $serverGrantGuard.Operand.Name)
}
$serverReadinessBranch = Get-FirstConditionalBranch `
    $refreshAuthenticatedCall `
    $refreshActivePeerCall
$serverReadinessTargetReachesActive =
    $null -ne $serverReadinessBranch -and
    (Test-CecilReachable `
        -Start $serverReadinessBranch.Operand `
        -Target $refreshActivePeerCall)
$serverReadinessFallthroughReachesActive =
    $null -ne $serverReadinessBranch -and
    (Test-CecilReachable `
        -Start $serverReadinessBranch.Next `
        -Target $refreshActivePeerCall)
Assert-True (
    $null -ne $serverReadinessBranch -and
    $serverReadinessTargetReachesActive -ne
        $serverReadinessFallthroughReachesActive) `
    "Server Ready/PeerInfo state no longer gates entitlement creation."

foreach ($postSendMutation in @(
    $refreshSequenceSetCall,
    $refreshTimestampSetCall)) {
    $sendCanBeSkipped = Test-CecilReachable `
        -Start $refreshEntitlementDefinition.Body.Instructions[0] `
        -Target $postSendMutation `
        -Blocked @($refreshSendCall)
    Assert-True (-not $sendCanBeSkipped) `
        "Entitlement state can advance without a successful send call."
}
$activePeerBranch = Get-FirstConditionalBranch `
    $refreshActivePeerCall `
    $refreshAdminCall
$activeBranchTargetReachesGrant =
    $null -ne $activePeerBranch -and
    (Test-CecilReachable `
        -Start $activePeerBranch.Operand `
        -Target $refreshConstructorCall)
$activeFallthroughReachesGrant =
    $null -ne $activePeerBranch -and
    (Test-CecilReachable `
        -Start $activePeerBranch.Next `
        -Target $refreshConstructorCall)
Assert-True (
    $null -ne $activePeerBranch -and
    $activeBranchTargetReachesGrant -ne
        $activeFallthroughReachesGrant) `
    "Failed active-peer validation no longer prevents entitlement creation."

$activePeerDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "TryResolveActiveDetectionPeer"
$activePeerMemberNames = @(
    $activePeerDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MemberReference]
        } |
        ForEach-Object { $_.Operand.Name })
foreach ($requiredMember in @(
    "TryResolve",
    "get_HasAuthenticatedIdentity",
    "IsCurrentSteamAuthenticationLocked",
    "get_Phase",
    "Equals",
    "TryGetValue",
    "get_Quarantined",
    "get_Overflowed",
    "get_InboundViolation",
    "get_EnqueuedCallbackCount",
    "get_ProcessedCallbackCount",
    "get_ReachedActive",
    "get_CallbackOverflowed",
    "get_StaleCallbackCaptured",
    "get_LateCallbackCaptured",
    "get_BeginAuthInvocationObserved",
    "get_BeginAuthResultRecorded",
    "get_BeginAuthImmediateAccepted",
    "get_BeginAuthExecutionFaulted",
    "get_DuplicateBeginAuthInvocation",
    "get_LatestResponse")) {
    Assert-True ($activePeerMemberNames -contains $requiredMember) `
        ("The active Steam/RPC identity check lost " +
         $requiredMember + ".")
}

$activePeerFields = @(
    $activePeerDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.FieldReference]
        } |
        ForEach-Object {
            $_.Operand.DeclaringType.FullName + "::" + $_.Operand.Name
        })
$activePeerCalls = @(
    $activePeerDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference]
        } |
        ForEach-Object { $_.Operand.FullName })
Assert-True (
    $activePeerFields -contains
        "ServerManager.ServerManagerRuntime::SteamAuthenticationsByRpc" -and
    $activePeerFields -contains
        "ServerManager.ServerManagerRuntime::WorldBuffers" -and
    ($activePeerCalls -like
        "System.Boolean ServerManager.ServerPeerResolver::TryResolve(*").Count `
        -eq 1 -and
    ($activePeerCalls -contains
        "System.Boolean System.String::Equals(" +
        "System.String,System.String,System.StringComparison)")) `
    "The active peer is no longer tied to final Steam auth and its owned world gate."

$steamConnectionDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "TryGetSteamConnection"
$identityResolverDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ServerPeerResolver"
$identityResolveDefinition = $identityResolverDefinition.Methods |
    Where-Object Name -eq "TryResolve"
Assert-True (@($identityResolveDefinition.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and
    $_.Operand.Name -eq "TryGetSteamConnection"
}).Count -eq 1) "Peer identity no longer comes from the connection-bound Steam reservation."
$steamConnectionMembers = @($steamConnectionDefinition.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MemberReference] } |
    ForEach-Object { $_.Operand.Name })
foreach ($name in @("TryResolvePeer", "IsCurrentSteamAuthenticationLocked", "SteamAuthenticationsByRpc",
    "get_Phase", "get_Peer", "get_Rpc", "get_Socket", "get_SteamId",
    "IsConnected", "GetPeerID", "IsValid")) {
    Assert-True ($steamConnectionMembers -contains $name) "The reserved Steam connection check lost $name."
}
foreach ($method in @($activePeerDefinition, $steamConnectionDefinition, $identityResolveDefinition)) {
    Assert-True (@($method.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -in @("GetSocket", "get_Original", "GetHostName")
    }).Count -eq 0 -and @($method.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq "m_socket"
    }).Count -eq 0) "Later account identity again depends on replaceable outer socket references or host names."
}

$serverReportDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "HandleServerDetectionReport"
$serverReportCalls = @(
    $serverReportDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$reportActivePeerCall = $serverReportCalls |
    Where-Object { $_.Operand.Name -eq "TryResolveActiveDetectionPeer" } |
    Select-Object -First 1
$reportAdminCall = $serverReportCalls |
    Where-Object { $_.Operand.Name -eq "IsCurrentServerAdmin" } |
    Select-Object -First 1
$reportAllowAdminCall = $serverReportCalls |
    Where-Object { $_.Operand.Name -eq "get_AllowAdminCheatCommands" } |
    Select-Object -First 1
$reportCommandLimitCall = $serverReportCalls |
    Where-Object { $_.Operand.Name -eq "TryAdmitCommandReport" } |
    Select-Object -First 1
$cheatCommandLiteral = $serverReportDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [int] -and
        $_.Operand -eq 300 -and
        $_.Offset -lt $reportCommandLimitCall.Offset
    } |
    Select-Object -Last 1
$processEvidenceCall = $serverReportCalls |
    Where-Object { $_.Operand.Name -eq "ProcessDetectionEvidence" } |
    Select-Object -First 1
Assert-True (
    $null -ne $reportActivePeerCall -and
    $null -ne $cheatCommandLiteral -and
    $null -ne $reportCommandLimitCall -and
    $null -ne $reportAdminCall -and
    $null -ne $processEvidenceCall -and
    $reportActivePeerCall.Offset -lt $cheatCommandLiteral.Offset -and
    $cheatCommandLiteral.Offset -lt $reportCommandLimitCall.Offset -and
    $reportCommandLimitCall.Offset -lt $reportAdminCall.Offset -and
    $reportAdminCall.Offset -lt $processEvidenceCall.Offset) `
    "Cheat-command reports are no longer flood-limited before admin bypass."

$commandRateBranch = Get-FirstConditionalBranch `
    $cheatCommandLiteral `
    $reportCommandLimitCall
$rateTargetReachesLimiter = Test-CecilReachable `
    -Start $commandRateBranch.Operand `
    -Target $reportCommandLimitCall
$rateFallthroughReachesLimiter = Test-CecilReachable `
    -Start $commandRateBranch.Next `
    -Target $reportCommandLimitCall
Assert-True (
    $null -ne $commandRateBranch -and
    $rateTargetReachesLimiter -ne $rateFallthroughReachesLimiter) `
    "Non-command evidence no longer bypasses the command flood limiter."

Assert-True (
    $null -ne $reportAllowAdminCall -and
    $reportCommandLimitCall.Offset -lt $reportAllowAdminCall.Offset -and
    $reportAllowAdminCall.Offset -lt $reportAdminCall.Offset) `
    "The bounded command path no longer rechecks the admin policy and identity."

$releaseWorldDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "ReleaseWorld"
$releaseWorldCalls = @(
    $releaseWorldDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$canReleaseWorldCall = $releaseWorldCalls |
    Where-Object { $_.Operand.Name -eq "CanReleaseWorld" } |
    Select-Object -First 1
$releaseBufferCall = $releaseWorldCalls |
    Where-Object {
        $_.Operand.Name -eq "TryRelease" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.BufferedWorldSocket"
    } |
    Select-Object -First 1
$initialEntitlementCall = $releaseWorldCalls |
    Where-Object {
        $_.Operand.Name -eq "TryRefreshAdminCommandEntitlement"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $canReleaseWorldCall -and
    $null -ne $releaseBufferCall -and
    $null -ne $initialEntitlementCall -and
    $canReleaseWorldCall.Offset -lt $releaseBufferCall.Offset -and
    $releaseBufferCall.Offset -lt $initialEntitlementCall.Offset) `
    "The initial admin entitlement no longer follows world-buffer release."

$cleanupPeerDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "CleanupPeer"
$cleanupResetCall = $cleanupPeerDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "Reset" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.CheatCommandGuard"
    } |
    Select-Object -First 1
Assert-True ($null -ne $cleanupResetCall) `
    "Disconnect cleanup no longer revokes local admin entitlement."

$bufferDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.BufferedWorldSocket"

$beginLocalQuiescenceDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "BeginClientGameplayQuiescence"
$beginLocalQuiescenceCalls = @(
    $beginLocalQuiescenceDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$hideInventoryCall = $beginLocalQuiescenceCalls |
    Where-Object {
        $_.Operand.Name -eq "Hide" -and
        $_.Operand.DeclaringType.FullName -eq "InventoryGui"
    } |
    Select-Object -First 1
$hideStoreCall = $beginLocalQuiescenceCalls |
    Where-Object {
        $_.Operand.Name -eq "Hide" -and
        $_.Operand.DeclaringType.FullName -eq "StoreGui"
    } |
    Select-Object -First 1
$disablePlayerCall = $beginLocalQuiescenceCalls |
    Where-Object {
        $_.Operand.Name -eq "set_enabled" -and
        $_.Operand.DeclaringType.FullName -eq
            "UnityEngine.Behaviour"
    } |
    Select-Object -First 1
$freezeBodyCall = $beginLocalQuiescenceCalls |
    Where-Object {
        $_.Operand.Name -eq "set_constraints" -and
        $_.Operand.DeclaringType.FullName -eq
            "UnityEngine.Rigidbody"
    } |
    Select-Object -First 1
$syncFrozenTransformCall = $beginLocalQuiescenceCalls |
    Where-Object {
        $_.Operand.Name -eq "SyncNow" -and
        $_.Operand.DeclaringType.FullName -eq "ZSyncTransform"
    } |
    Select-Object -First 1
$disableSyncTransformCall = $beginLocalQuiescenceCalls |
    Where-Object {
        $_.Operand.Name -eq "set_enabled" -and
        $_.Operand.DeclaringType.FullName -eq
            "UnityEngine.Behaviour" -and
        $_.Offset -gt $syncFrozenTransformCall.Offset
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $hideInventoryCall -and
    $null -ne $hideStoreCall -and
    $null -ne $disablePlayerCall -and
    $null -ne $freezeBodyCall -and
    $null -ne $syncFrozenTransformCall -and
    $null -ne $disableSyncTransformCall -and
    $hideInventoryCall.Offset -lt $disablePlayerCall.Offset -and
    $hideStoreCall.Offset -lt $disablePlayerCall.Offset -and
    $disablePlayerCall.Offset -lt $freezeBodyCall.Offset -and
    $freezeBodyCall.Offset -lt $syncFrozenTransformCall.Offset -and
    $syncFrozenTransformCall.Offset -lt
        $disableSyncTransformCall.Offset) `
    "Local inventory interaction is not closed before final-save quiescence."

$flushWorldDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "TryFlushClientWorldMutations"
$flushWorldCalls = @(
    $flushWorldDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$flushClientObjectsCall = $flushWorldCalls |
    Where-Object {
        $_.Operand.Name -eq "FlushClientObjects" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ValheimPrivateAccess"
    } |
    Select-Object -First 1
$flushDestroyedCall = $flushWorldCalls |
    Where-Object {
        $_.Operand.Name -eq "SendDestroyed" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ValheimPrivateAccess"
    } |
    Select-Object -First 1
$flushWorldQueueReads = @($flushWorldCalls |
    Where-Object {
        ($_.Operand.Name -eq "GetClientChangeQueue" -and
         $_.Operand.DeclaringType.FullName -eq "ZDOMan") -or
        ($_.Operand.Name -eq "GetDestroySendCount" -and
         $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ValheimPrivateAccess")
    })
Assert-True (
    $null -ne $flushClientObjectsCall -and
    $null -ne $flushDestroyedCall -and
    $flushClientObjectsCall.Offset -lt $flushDestroyedCall.Offset -and
    $flushWorldQueueReads.Count -ge 2 -and
    $flushDestroyedCall.Offset -lt $flushWorldQueueReads[0].Offset) `
    "FinalSaveBegin can overtake queued client ZDO or destroy mutations."

$captureReadyExitDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "CaptureDeferredClientExitSnapshot"
$captureReadyExitCalls = @(
    $captureReadyExitDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$capturePlayerDataCall = $captureReadyExitCalls |
    Where-Object {
        $_.Operand.Name -eq "SavePlayerData" -and
        $_.Operand.DeclaringType.FullName -eq "PlayerProfile"
    } |
    Select-Object -First 1
$captureLogoutPointCall = $captureReadyExitCalls |
    Where-Object {
        $_.Operand.Name -eq "SaveLogoutPoint" -and
        $_.Operand.DeclaringType.FullName -eq "PlayerProfile"
    } |
    Select-Object -First 1
$serializeReadyProfileCall = $captureReadyExitCalls |
    Where-Object {
        $_.Operand.Name -eq "SerializeProfileToBytes" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ValheimPlayerProfileCodec"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $capturePlayerDataCall -and
    $null -ne $captureLogoutPointCall -and
    $null -ne $serializeReadyProfileCall -and
    $capturePlayerDataCall.Offset -lt $captureLogoutPointCall.Offset -and
    $captureLogoutPointCall.Offset -lt
        $serializeReadyProfileCall.Offset) `
    "The post-gate final full-profile capture order changed."

$sendFinalBeginDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "TrySendFinalSaveBegin"
$sendFinalBeginCalls = @(
    $sendFinalBeginDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$flushBeforeBeginCall = $sendFinalBeginCalls |
    Where-Object {
        $_.Operand.Name -eq "TryFlushClientWorldMutations"
    } |
    Select-Object -First 1
$createFinalBeginCall = $sendFinalBeginCalls |
    Where-Object {
        $_.Operand.Name -eq "CreateFinalSaveBegin"
    } |
    Select-Object -First 1
$sendFinalBeginCall = $sendFinalBeginCalls |
    Where-Object {
        $_.Operand.Name -eq "SendProtocolOrThrow"
    } |
    Select-Object -First 1
$markFinalBeginSentCall = $sendFinalBeginCalls |
    Where-Object {
        $_.Operand.Name -eq "set_ClientWorldBarrierSent"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $flushBeforeBeginCall -and
    $null -ne $createFinalBeginCall -and
    $null -ne $sendFinalBeginCall -and
    $null -ne $markFinalBeginSentCall -and
    $flushBeforeBeginCall.Offset -lt $createFinalBeginCall.Offset -and
    $createFinalBeginCall.Offset -lt $sendFinalBeginCall.Offset -and
    $sendFinalBeginCall.Offset -lt $markFinalBeginSentCall.Offset) `
    "FinalSaveBegin can overtake the pre-gate client world drain."

$processDeferredExitDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "ProcessDeferredClientExit"
$processDeferredExitCalls = @(
    $processDeferredExitDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$postGateOfferCall = $processDeferredExitCalls |
    Where-Object {
        $_.Operand.Name -eq "OfferClientSave" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$postGateCaptureCall = $processDeferredExitCalls |
    Where-Object {
        $_.Operand.Name -eq "CaptureDeferredClientExitSnapshot" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$settleDeathCall = $processDeferredExitCalls |
    Where-Object {
        $_.Operand.Name -eq "CheckDeath" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ValheimPrivateAccess"
    } |
    Select-Object -First 1
$postReadyFlushCall = $processDeferredExitCalls |
    Where-Object {
        $_.Operand.Name -eq "TryFlushClientWorldMutations" -and
        $_.Offset -gt $settleDeathCall.Offset
    } |
    Select-Object -First 1
$startFinalDispatchCall = $processDeferredExitCalls |
    Where-Object {
        $_.Operand.Name -eq "TryStartNext" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ClientCharacterSavePipeline"
    } |
    Select-Object -First 1
$sendFinalDispatchCall = $processDeferredExitCalls |
    Where-Object {
        $_.Operand.Name -eq "SendClientSave" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime"
    } |
    Select-Object -First 1
$postGateRecaptureCalls = @(
    $processDeferredExitCalls |
        Where-Object {
            $_.Operand.Name -eq "SavePlayerProfile" -and
            $_.Operand.DeclaringType.FullName -eq "Game"
        })
$postBarrierInventoryGuard = $processDeferredExitCalls |
    Where-Object {
        $_.Operand.Name -eq
            "get_InventoryChangedAfterQuiescence"
    } |
    Select-Object -Last 1
$resetPostBarrierInventoryGuard = $processDeferredExitCalls |
    Where-Object {
        $_.Operand.Name -eq
            "set_InventoryChangedAfterQuiescence"
    } |
    Select-Object -Last 1
Assert-True (
    $null -ne $settleDeathCall -and
    $null -ne $postReadyFlushCall -and
    $null -ne $postGateCaptureCall -and
    $null -ne $postGateOfferCall -and
    $null -ne $startFinalDispatchCall -and
    $null -ne $sendFinalDispatchCall -and
    $null -ne $resetPostBarrierInventoryGuard -and
    $null -ne $postBarrierInventoryGuard -and
    $settleDeathCall.Offset -lt $postReadyFlushCall.Offset -and
    $postReadyFlushCall.Offset -lt
        $resetPostBarrierInventoryGuard.Offset -and
    $resetPostBarrierInventoryGuard.Offset -lt
        $postGateCaptureCall.Offset -and
    $postGateCaptureCall.Offset -lt
        $postBarrierInventoryGuard.Offset -and
    $postBarrierInventoryGuard.Offset -lt $postGateOfferCall.Offset -and
    $postGateOfferCall.Offset -lt $startFinalDispatchCall.Offset -and
    $startFinalDispatchCall.Offset -lt $sendFinalDispatchCall.Offset -and
    $postGateRecaptureCalls.Count -eq 0) `
    "The final-save Ready path no longer settles, drains, captures, and dispatches atomically."
$afterInventoryDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "AfterInventoryChanged"
$postBarrierInventoryMarker = $afterInventoryDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq
            "set_InventoryChangedAfterQuiescence"
    } |
    Select-Object -First 1
$clientFinalReadyDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "HandleClientFinalSaveReady"
$clientReadyBarrierGuard = $clientFinalReadyDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "get_ClientWorldBarrierSent"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $postBarrierInventoryMarker -and
    $null -ne $clientReadyBarrierGuard) `
    "The post-barrier inventory mutation or unsolicited Ready guard is missing."

$finalSaveBeginDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "HandleServerFinalSaveBegin"
$finalSaveBeginCalls = @(
    $finalSaveBeginDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$discardPreGateAssemblyCall = $finalSaveBeginCalls |
    Where-Object {
        $_.Operand.Name -eq "RemovePeer" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.BoundedFragmentReassembler"
    } |
    Select-Object -First 1
$restrictFinalSaveCall = $finalSaveBeginCalls |
    Where-Object {
        $_.Operand.Name -eq "TryRestrictOutboundForFinalSave" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.BufferedWorldSocket"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $discardPreGateAssemblyCall -and
    $null -ne $restrictFinalSaveCall -and
    $discardPreGateAssemblyCall.Offset -lt
        $restrictFinalSaveCall.Offset) `
    "A partial pre-gate character assembly can survive final-save isolation."

$serverFragmentDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "HandleServerCharacterFragment"
$serverFragmentCalls = @(
    $serverFragmentDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$completeInboundBarrierCall = $serverFragmentCalls |
    Where-Object {
        $_.Operand.Name -eq "TryCompleteFinalSaveRestriction" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.BufferedWorldSocket"
    } |
    Select-Object -First 1
$acceptFinalFragmentCall = $serverFragmentCalls |
    Where-Object {
        $_.Operand.Name -eq "AcceptPackage" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.BoundedFragmentReassembler"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $completeInboundBarrierCall -and
    $null -ne $acceptFinalFragmentCall -and
    $completeInboundBarrierCall.Offset -lt
        $acceptFinalFragmentCall.Offset) `
    "The final snapshot can be overtaken by later inbound gameplay."

$admitSaveDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "AdmitClientSaveAssembly"
$admitSaveCalls = @(
    $admitSaveDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$finalSaveAdmissionRead = $admitSaveCalls |
    Where-Object {
        $_.Operand.Name -eq "get_SaveAssemblyAdmitted"
    } |
    Select-Object -First 1
$finalSaveAdmissionWrite = $admitSaveCalls |
    Where-Object {
        $_.Operand.Name -eq "set_SaveAssemblyAdmitted"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $finalSaveAdmissionRead -and
    $null -ne $finalSaveAdmissionWrite -and
    $finalSaveAdmissionRead.Offset -lt
        $finalSaveAdmissionWrite.Offset) `
    "The final-save gate no longer limits the peer to one save assembly."

$saveAdmissionWindowDefinition = $runtimeDefinition.NestedTypes |
    Where-Object Name -eq "SaveAdmissionWindow" |
    Select-Object -First 1
$saveByteAdmissionDefinition = $runtimeDefinition.NestedTypes |
    Where-Object Name -eq "SaveByteAdmission" |
    Select-Object -First 1
$admissionQueueProperty = $saveAdmissionWindowDefinition.Properties |
    Where-Object Name -eq "Admissions" |
    Select-Object -First 1
$admissionBytesProperty = $saveAdmissionWindowDefinition.Properties |
    Where-Object Name -eq "DecodedBytes" |
    Select-Object -First 1
$admissionByteCountProperty = $saveByteAdmissionDefinition.Properties |
    Where-Object Name -eq "ByteCount" |
    Select-Object -First 1
$decodedByteReads = @(
    $admitSaveCalls |
        Where-Object {
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerRuntime/SaveAdmissionWindow" -and
            $_.Operand.Name -eq "get_DecodedBytes"
        })
$decodedByteWrites = @(
    $admitSaveCalls |
        Where-Object {
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerRuntime/SaveAdmissionWindow" -and
            $_.Operand.Name -eq "set_DecodedBytes"
        })
$admissionQueueReads = @(
    $admitSaveCalls |
        Where-Object {
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerRuntime/SaveAdmissionWindow" -and
            $_.Operand.Name -eq "get_Admissions"
        })
$saveByteAdmissionCreate = $admitSaveDefinition.Body.Instructions |
    Where-Object {
        $_.OpCode.Code.ToString() -eq "Newobj" -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime/SaveByteAdmission" -and
        $_.Operand.Parameters.Count -eq 2 -and
        $_.Operand.Parameters[0].ParameterType.FullName -eq "System.Int64" -and
        $_.Operand.Parameters[1].ParameterType.FullName -eq "System.Int32"
    } |
    Select-Object -First 1
$maxEnvelopeReads = @(
    $admitSaveCalls |
        Where-Object {
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.CharacterStorageOptions" -and
            $_.Operand.Name -eq "get_MaxEnvelopeBytes"
        })
Assert-True (
    $null -ne $saveAdmissionWindowDefinition -and
    $null -ne $saveByteAdmissionDefinition -and
    $null -ne $admissionQueueProperty -and
    $admissionQueueProperty.PropertyType.FullName -eq
        'System.Collections.Generic.Queue`1<ServerManager.ServerManagerRuntime/SaveByteAdmission>' -and
    $null -ne $admissionBytesProperty -and
    $admissionBytesProperty.PropertyType.FullName -eq "System.Int64" -and
    $null -ne $admissionByteCountProperty -and
    $admissionByteCountProperty.PropertyType.FullName -eq "System.Int32" -and
    $decodedByteReads.Count -ge 3 -and
    $decodedByteWrites.Count -ge 2 -and
    $admissionQueueReads.Count -ge 4 -and
    $null -ne $saveByteAdmissionCreate -and
    $maxEnvelopeReads.Count -ge 2) `
    ("Per-character save admission no longer tracks both count and decoded " +
        "bytes against the envelope-derived budget. window=" +
        ($null -ne $saveAdmissionWindowDefinition) +
        " admission=" + ($null -ne $saveByteAdmissionDefinition) +
        " queueType=" + $admissionQueueProperty.PropertyType.FullName +
        " bytesType=" + $admissionBytesProperty.PropertyType.FullName +
        " byteCountType=" + $admissionByteCountProperty.PropertyType.FullName +
        " decodedReads=" + $decodedByteReads.Count +
        " decodedWrites=" + $decodedByteWrites.Count +
        " queueReads=" + $admissionQueueReads.Count +
        " admissionCtor=" + ($null -ne $saveByteAdmissionCreate) +
        " maxEnvelopeReads=" + $maxEnvelopeReads.Count)

$resumeExitDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "ResumeDeferredClientExit"
$resumeExitCalls = @(
    $resumeExitDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$confirmLogoutCall = $resumeExitCalls |
    Where-Object {
        $_.Operand.Name -eq "IsShuttingDown" -and
        $_.Operand.DeclaringType.FullName -eq "Game"
    } |
    Select-Object -First 1
$failedLogoutDisconnectCall = $resumeExitCalls |
    Where-Object {
        $_.Operand.Name -eq "FailClient" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime" -and
        $_.Offset -gt $confirmLogoutCall.Offset
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $confirmLogoutCall -and
    $null -ne $failedLogoutDisconnectCall) `
    "A vetoed resumed logout can leave a final-save-only session active."

$bufferSendDefinition = $bufferDefinition.Methods |
    Where-Object {
        $_.Name -eq "Send" -and
        $_.Parameters.Count -eq 1 -and
        $_.Parameters[0].ParameterType.FullName -eq "ZPackage"
    }
$bufferSendFinalModeLoads = @(
    $bufferSendDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.FieldReference] -and
            $_.Operand.Name -eq "_finalSaveOutboundRestricted"
        })
$bufferSendAllowCalls = @(
    $bufferSendDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq "ShouldPassOutbound"
        })
$bufferSendInitialAllowCalls = @(
    $bufferSendDefinition.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq "ShouldPassBeforeReady"
        })
$bufferRecvDefinition = $bufferDefinition.Methods |
    Where-Object {
        $_.Name -eq "Recv" -and
        $_.Parameters.Count -eq 0
    }
$bufferRecvFinalModeCall = $bufferRecvDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "ReceiveFinalSaveOnlyInbound"
    } |
    Select-Object -First 1
$bufferRecvInboundModeLoad = $bufferRecvDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.Name -eq "_finalSaveInboundRestricted"
    } |
    Select-Object -First 1
Assert-True (
    $bufferSendFinalModeLoads.Count -ge 2 -and
    $bufferSendAllowCalls.Count -ge 2 -and
    $bufferSendInitialAllowCalls.Count -eq 1 -and
    $null -ne $bufferRecvInboundModeLoad -and
    $null -ne $bufferRecvFinalModeCall) `
    "The two-phase final-save socket restrictions are incomplete."
$thirdPartySocketOverrides = @($bufferDefinition.Methods | Where-Object HasBody |
    ForEach-Object { $_.Body.Instructions } | Where-Object {
        $_.OpCode.Name -eq 'ldstr' -and
        [string]$_.Operand -match 'Jotunn|^ServerSync(?:$|VersionCheck| )|tests\.future|org\.bepinex\.plugins\.example'
    })
Assert-True ($thirdPartySocketOverrides.Count -eq 0) `
    'Direct-RPC compatibility must not depend on a hardcoded third-party mod allowlist.'

$bufferInitializer = $bufferDefinition.Methods |
    Where-Object Name -eq ".cctor"
$adminUnsafeLoad = $bufferInitializer.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "ldsfld" -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.Name -eq "AdminEntitlementMethodHash"
    } |
    Select-Object -Last 1
$unsafeSetStore = $bufferInitializer.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "stsfld" -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.Name -eq "UnsafeInboundMethodHashes"
    } |
    Select-Object -First 1
$unsafeSetConstructor = $bufferInitializer.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "newobj" -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq
            'System.Collections.Generic.HashSet`1<System.Int32>' -and
        $_.Offset -lt $unsafeSetStore.Offset
    } |
    Select-Object -Last 1
$unsafeAdminAdd = $bufferInitializer.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "Add" -and
        $_.Operand.DeclaringType.FullName -eq
            'System.Collections.Generic.HashSet`1<System.Int32>' -and
        $_.Offset -gt $adminUnsafeLoad.Offset -and
        $_.Offset -lt $unsafeSetStore.Offset
    } |
    Select-Object -First 1
$unsafeRemovals = @(
    $bufferInitializer.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq "Remove" -and
            $_.Offset -gt $unsafeSetConstructor.Offset -and
            $_.Offset -lt $unsafeSetStore.Offset
        })
$prePeerInfoSetStore = $bufferInitializer.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "stsfld" -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.Name -eq "PrePeerInfoInboundMethodHashes"
    } |
    Select-Object -First 1
$preReadySetStore = $bufferInitializer.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "stsfld" -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.Name -eq "PreReadyInboundMethodHashes"
    } |
    Select-Object -First 1
$prePeerInfoSetConstructor = $bufferInitializer.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "newobj" -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq
            'System.Collections.Generic.HashSet`1<System.Int32>' -and
        $_.Offset -lt $prePeerInfoSetStore.Offset
    } |
    Select-Object -Last 1
$preReadySetConstructor = $bufferInitializer.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "newobj" -and
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq
            'System.Collections.Generic.HashSet`1<System.Int32>' -and
        $_.Offset -lt $preReadySetStore.Offset
    } |
    Select-Object -Last 1
$prePeerInfoAdminLoads = @(
    $bufferInitializer.Body.Instructions |
        Where-Object {
            $_.OpCode.Name -eq "ldsfld" -and
            $_.Operand -is [Mono.Cecil.FieldReference] -and
            $_.Operand.Name -eq "AdminEntitlementMethodHash" -and
            $_.Offset -gt $prePeerInfoSetConstructor.Offset -and
            $_.Offset -lt $prePeerInfoSetStore.Offset
        })
$preReadyAdminLoads = @(
    $bufferInitializer.Body.Instructions |
        Where-Object {
            $_.OpCode.Name -eq "ldsfld" -and
            $_.Operand -is [Mono.Cecil.FieldReference] -and
            $_.Operand.Name -eq "AdminEntitlementMethodHash" -and
            $_.Offset -gt $preReadySetConstructor.Offset -and
            $_.Offset -lt $preReadySetStore.Offset
        })
Assert-True (
    $null -ne $adminUnsafeLoad -and
    $null -ne $unsafeSetStore -and
    $null -ne $unsafeSetConstructor -and
    $null -ne $unsafeAdminAdd -and
    $null -ne $prePeerInfoSetConstructor -and
    $null -ne $preReadySetConstructor -and
    $unsafeSetConstructor.Offset -lt $adminUnsafeLoad.Offset -and
    $adminUnsafeLoad.Offset -lt $unsafeAdminAdd.Offset -and
    $unsafeAdminAdd.Offset -lt $unsafeSetStore.Offset -and
    $unsafeRemovals.Count -eq 0 -and
    $prePeerInfoAdminLoads.Count -eq 0 -and
    $preReadyAdminLoads.Count -eq 0) `
    ("Pre-ready client entitlement packets are no longer unsafe inbound: " +
     "ctor=" + $unsafeSetConstructor.Offset +
     ", load=" + $adminUnsafeLoad.Offset +
     ", add=" + $unsafeAdminAdd.Offset +
     ", store=" + $unsafeSetStore.Offset +
     ", removes=" + $unsafeRemovals.Count +
     ", prePeer=" + $prePeerInfoAdminLoads.Count +
     ", preReady=" + $preReadyAdminLoads.Count + ".")

$commandMaximumField = $runtimeDefinition.Fields |
    Where-Object Name -eq "MaximumCommandReportsPerWindow"
$commandWindowField = $runtimeDefinition.Fields |
    Where-Object Name -eq "CommandReportWindowSeconds"
$adminGrantField = $runtimeDefinition.Fields |
    Where-Object Name -eq "AdminEntitlementGrantMilliseconds"
$legacyLifetimeCapFields = @(
    $runtimeDefinition.Fields |
        Where-Object {
            $_.Name -eq "MaximumDetectionReportsPerSession" -or
            $_.Name -eq "MaximumCheatCommandReportsPerSession"
        })
$legacyLifetimeCounterFields = @(
    $detectionStateDefinition.Fields |
        Where-Object {
            $_.Name -like "*ReportCount*" -or
            $_.Name -like "*CheatCommandReportCount*"
        })
$commandLimitDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "TryAdmitCommandReport"
$commandLimitCall = $commandLimitDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq "TryAdmit" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.DetectionRateLimiter"
    } |
    Select-Object -First 1
$commandWindowLiteral = $commandLimitDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -eq 10 -and
        $_.Offset -lt $commandLimitCall.Offset
    } |
    Select-Object -First 1
$stopwatchFrequencyLoad = $commandLimitDefinition.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "ldsfld" -and
        $_.Operand -is [Mono.Cecil.FieldReference] -and
        $_.Operand.FullName -eq
            "System.Int64 System.Diagnostics.Stopwatch::Frequency"
    } |
    Select-Object -First 1
$commandWindowMultiply = $commandLimitDefinition.Body.Instructions |
    Where-Object { $_.OpCode.Name -eq "mul.ovf" } |
    Select-Object -First 1
$commandMaximumLiteral = $commandLimitDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -eq 64 -and
        $_.Offset -lt $commandLimitCall.Offset
    } |
    Select-Object -First 1
$adminGrantLiteral = $refreshEntitlementDefinition.Body.Instructions |
    Where-Object {
        $_.Operand -eq 5000 -and
        $_.Offset -gt $refreshAdminCall.Offset -and
        $_.Offset -lt $refreshConstructorCall.Offset
    } |
    Select-Object -Last 1
$adminRevokeLiteral = $refreshEntitlementDefinition.Body.Instructions |
    Where-Object {
        $_.OpCode.Name -eq "ldc.i4.0" -and
        $_.Offset -gt $refreshAdminCall.Offset -and
        $_.Offset -lt $refreshConstructorCall.Offset
    } |
    Select-Object -Last 1
$grantLiteralReachesConstructor = Test-CecilReachable `
    -Start $adminGrantLiteral `
    -Target $refreshConstructorCall
$revokeLiteralReachesConstructor = Test-CecilReachable `
    -Start $adminRevokeLiteral `
    -Target $refreshConstructorCall
Assert-True (
    $commandMaximumField.HasConstant -and
    $commandMaximumField.Constant -eq 64 -and
    $commandWindowField.HasConstant -and
    $commandWindowField.Constant -eq 10 -and
    $legacyLifetimeCapFields.Count -eq 0 -and
    $legacyLifetimeCounterFields.Count -eq 0 -and
    $adminGrantField.HasConstant -and
    $adminGrantField.Constant -eq 5000 -and
    $null -ne $commandWindowLiteral -and
    $null -ne $stopwatchFrequencyLoad -and
    $null -ne $commandWindowMultiply -and
    $null -ne $commandMaximumLiteral -and
    $null -ne $commandLimitCall -and
    $commandWindowLiteral.Offset -lt $stopwatchFrequencyLoad.Offset -and
    $stopwatchFrequencyLoad.Offset -lt $commandWindowMultiply.Offset -and
    $commandWindowMultiply.Offset -lt $commandMaximumLiteral.Offset -and
    $commandMaximumLiteral.Offset -lt $commandLimitCall.Offset -and
    $null -ne $adminGrantLiteral -and
    $null -ne $adminRevokeLiteral -and
    $grantLiteralReachesConstructor -and
    $revokeLiteralReachesConstructor) `
    "The runtime admin TTL or all-command 64-per-10-second limit changed."

$zPackageType = $gameAssembly.GetType("ZPackage", $true)
$script:zPackageConstructor = $zPackageType.GetConstructor(
    [Type[]]@([byte[]]))
$zPackageGetArray = $zPackageType.GetMethod(
    "GetArray",
    [Type[]]@())
Assert-True ($null -ne $script:zPackageConstructor) `
    "ZPackage(byte[]) was unavailable."

$reportType = $pluginAssembly.GetType(
    "ServerManager.DetectionReport",
    $true)
$codecType = $pluginAssembly.GetType(
    "ServerManager.DetectionReportCodec",
    $true)
$evidenceType = $pluginAssembly.GetType(
    "ServerManager.DetectionEvidence",
    $true)

$reportConstructor = $reportType.GetConstructors($instanceNonPublic)[0]
$encodeDetection = $codecType.GetMethod("Encode", $staticNonPublic)
$script:decodeDetection = $codecType.GetMethod(
    "TryDecode",
    $staticNonPublic)

$sessionId = [byte[]]::new(16)
$nonce = [byte[]]::new(32)
for ($index = 0; $index -lt $sessionId.Length; ++$index) {
    $sessionId[$index] = [byte]($index + 1)
}

for ($index = 0; $index -lt $nonce.Length; ++$index) {
    $nonce[$index] = [byte](255 - $index)
}

$reportArguments = [object[]]::new(6)
$reportArguments[0] = $sessionId
$reportArguments[1] = $nonce
$reportArguments[2] = [uint32]1
$reportArguments[3] = [Enum]::Parse(
    $evidenceType,
    "CheatCommand")
$reportArguments[4] = "devcommands"
$reportArguments[5] = [uint32]1
$report = $reportConstructor.Invoke($reportArguments)
$package = $encodeDetection.Invoke($null, [object[]]@($report))

$decoded = Decode-DetectionReport $package
Assert-True $decoded.Succeeded "A valid detection report was rejected."

$sequence = $reportType.GetProperty(
    "Sequence",
    $instanceNonPublic).GetValue($decoded.Report, $null)
$detail = $reportType.GetProperty(
    "Detail",
    $instanceNonPublic).GetValue($decoded.Report, $null)
Assert-True ($sequence -eq 1) "The detection sequence did not round-trip."
Assert-True ($detail -eq "devcommands") `
    "The bounded command token did not round-trip."

$maximumDetail = ("a" * 48) -join ""
$reportArguments[4] = $maximumDetail
$maximumReport = $reportConstructor.Invoke($reportArguments)
$maximumPackage = $encodeDetection.Invoke(
    $null,
    [object[]]@($maximumReport))
$maximumBytes = [byte[]]$zPackageGetArray.Invoke(
    $maximumPackage,
    $null)
Assert-True ($maximumBytes.Length -eq 113) `
    "The maximum bounded detection report size changed."
Assert-True ((Decode-DetectionReport $maximumPackage).Succeeded) `
    "A valid 48-byte command token was rejected."

$reportArguments[4] = ("a" * 49) -join ""
$longDetailRejected = $false
try {
    $null = $reportConstructor.Invoke($reportArguments)
}
catch {
    $longDetailRejected =
        $_.Exception.Message -like "*bounded command token*"
}

Assert-True $longDetailRejected `
    "A 49-byte command token was accepted."

$reportArguments[3] = [Enum]::Parse(
    $evidenceType,
    "CheatEngineProcess")
$reportArguments[4] = "unexpected"
$nonCommandDetailRejected = $false
try {
    $null = $reportConstructor.Invoke($reportArguments)
}
catch {
    $nonCommandDetailRejected =
        $_.Exception.Message -like "*Only command reports*"
}

Assert-True $nonCommandDetailRejected `
    "A process report accepted command detail."

$injectedModuleEvidence = [Enum]::Parse(
    $evidenceType,
    "CheatEngineInjectedModule")
$moduleReportArguments = [object[]]$reportArguments.Clone()
$moduleReportArguments[3] = $injectedModuleEvidence
$moduleReportArguments[4] = ""
$moduleReport = $reportConstructor.Invoke($moduleReportArguments)
$modulePackage = $encodeDetection.Invoke(
    $null,
    [object[]]@($moduleReport))
$decodedModule = Decode-DetectionReport $modulePackage
Assert-True $decodedModule.Succeeded `
    "A fixed Cheat Engine module report was rejected."
$decodedModuleEvidence = $reportType.GetProperty(
    "Evidence",
    $instanceNonPublic).GetValue($decodedModule.Report, $null)
$decodedModuleDetail = $reportType.GetProperty(
    "Detail",
    $instanceNonPublic).GetValue($decodedModule.Report, $null)
Assert-True (
    $decodedModuleEvidence.ToString() -eq
        "CheatEngineInjectedModule" -and
    $decodedModuleDetail.Length -eq 0) `
    "The module report was not a fixed, detail-free evidence value."

$evidenceCatalogType = $pluginAssembly.GetType("ServerManager.DetectionEvidenceCatalog", $true)
$isClientReportable = $evidenceCatalogType.GetMethod("IsClientReportable", $staticNonPublic)
$isSnapshotStatLimit = $evidenceCatalogType.GetMethod("IsSnapshotStatLimit", $staticNonPublic)
foreach ($serverEvidenceSpec in @(
    @{ Name = 'MalformedGameplayTraffic'; Value = 402; Snapshot = $false },
    @{ Name = 'MaximumHealthLimitExceeded'; Value = 403; Snapshot = $true },
    @{ Name = 'MaximumStaminaLimitExceeded'; Value = 404; Snapshot = $true },
    @{ Name = 'MaximumEitrLimitExceeded'; Value = 405; Snapshot = $true })) {
    $serverEvidence = [Enum]::Parse($evidenceType, $serverEvidenceSpec.Name)
    Assert-True ([int]$serverEvidence -eq $serverEvidenceSpec.Value) "Server-only evidence numbering changed for $($serverEvidenceSpec.Name)."
    Assert-True (-not $isClientReportable.Invoke($null, [object[]]@($serverEvidence))) "A client may claim $serverEvidence."
    Assert-True ($isSnapshotStatLimit.Invoke($null, [object[]]@($serverEvidence)) -eq $serverEvidenceSpec.Snapshot) "Snapshot stat evidence classification changed for $serverEvidence."
    $serverReportArguments = [object[]]$moduleReportArguments.Clone()
    $serverReportArguments[3] = $serverEvidence
    $serverReport = $reportConstructor.Invoke($serverReportArguments)
    $serverEncodeRejected = $false
    try { $null = $encodeDetection.Invoke($null, [object[]]@($serverReport)) }
    catch {
        $errorCause = $_.Exception
        while ($null -ne $errorCause.InnerException) { $errorCause = $errorCause.InnerException }
        $serverEncodeRejected = $errorCause -is [ArgumentOutOfRangeException]
    }
    Assert-True $serverEncodeRejected "The client codec encoded server-only evidence $serverEvidence."
    [byte[]]$serverOnlyEvidenceBytes = $zPackageGetArray.Invoke($modulePackage, $null).Clone()
    $serverOnlyEvidenceBytes[62] = [byte]($serverEvidenceSpec.Value -band 255)
    $serverOnlyEvidenceBytes[63] = [byte]($serverEvidenceSpec.Value -shr 8)
    $serverDecode = Decode-DetectionReport (New-ZPackage $serverOnlyEvidenceBytes)
    Assert-True (-not $serverDecode.Succeeded -and $null -ne $serverDecode.Rejection -and
        $serverDecode.Rejection.SafeMessage -eq 'The detection evidence code was invalid.') `
        "The wire codec accepted server-only evidence $serverEvidence."
}

$moduleReportArguments[4] = "speedhack-x86_64.dll"
$rawModuleDetailRejected = $false
try {
    $null = $reportConstructor.Invoke($moduleReportArguments)
}
catch {
    $rawModuleDetailRejected = $true
}

Assert-True $rawModuleDetailRejected `
    "A module report exposed a raw module name as detail."

foreach ($gameplayEvidenceName in @(
    "CarryWeightLimitExceeded",
    "MaximumDamageLimitExceeded")) {
    $gameplayReportArguments = [object[]]$reportArguments.Clone()
    $gameplayReportArguments[3] = [Enum]::Parse(
        $evidenceType,
        $gameplayEvidenceName)
    $gameplayReportArguments[4] = ""
    $gameplayReport = $reportConstructor.Invoke(
        $gameplayReportArguments)
    $gameplayPackage = $encodeDetection.Invoke(
        $null,
        [object[]]@($gameplayReport))
    $decodedGameplay = Decode-DetectionReport $gameplayPackage
    Assert-True ($decodedGameplay.Succeeded -and
        $reportType.GetProperty(
            "Evidence",
            $instanceNonPublic).GetValue(
                $decodedGameplay.Report,
                $null).ToString() -eq $gameplayEvidenceName) `
        "A detail-free gameplay-limit report did not round-trip."
}

$reportArguments[3] = [Enum]::Parse(
    $evidenceType,
    "CheatCommand")
$reportArguments[4] = "devcommands"

$encoded = [byte[]]$zPackageGetArray.Invoke($package, $null)

$zeroSequence = [byte[]]$encoded.Clone()
$zeroSequence[54] = 0
$zeroSequence[55] = 0
$zeroSequence[56] = 0
$zeroSequence[57] = 0
Assert-True (-not (Decode-DetectionReport (
    New-ZPackage $zeroSequence)).Succeeded) `
    "A zero detection sequence was accepted."

$unknownEvidence = [byte[]]$encoded.Clone()
$unknownEvidence[62] = 255
$unknownEvidence[63] = 127
Assert-True (-not (Decode-DetectionReport (
    New-ZPackage $unknownEvidence)).Succeeded) `
    "An unknown evidence code was accepted."

$trailing = [byte[]]::new($encoded.Length + 1)
[Array]::Copy($encoded, $trailing, $encoded.Length)
Assert-True (-not (Decode-DetectionReport (
    New-ZPackage $trailing)).Succeeded) `
    "Trailing detection bytes were accepted."

$oversize = [byte[]]::new(114)
Assert-True (-not (Decode-DetectionReport (
    New-ZPackage $oversize)).Succeeded) `
    "An oversized detection report was accepted."

$adminEntitlementType = $pluginAssembly.GetType(
    "ServerManager.AdminCommandEntitlement",
    $true)
$adminEntitlementCodecType = $pluginAssembly.GetType(
    "ServerManager.AdminCommandEntitlementCodec",
    $true)
$adminEntitlementConstructor =
    $adminEntitlementType.GetConstructors($instanceNonPublic)[0]
$encodeAdminEntitlement = $adminEntitlementCodecType.GetMethod(
    "Encode",
    $staticNonPublic)
$decodeAdminEntitlement = $adminEntitlementCodecType.GetMethod(
    "TryDecode",
    $staticNonPublic)
$adminEntitlementArguments = [object[]]::new(5)
$adminEntitlementArguments[0] = $sessionId
$adminEntitlementArguments[1] = $nonce
$adminEntitlementArguments[2] = [uint32]1
$adminEntitlementArguments[3] = $true
$adminEntitlementArguments[4] = 5000
$adminEntitlement = $adminEntitlementConstructor.Invoke(
    $adminEntitlementArguments)
$adminEntitlementPackage = $encodeAdminEntitlement.Invoke(
    $null,
    [object[]]@($adminEntitlement))
$decodeAdminArguments = [object[]]::new(3)
$decodeAdminArguments[0] = $adminEntitlementPackage
Assert-True ($decodeAdminEntitlement.Invoke(
    $null,
    $decodeAdminArguments)) `
    "A valid admin command entitlement was rejected."
$decodedAdminEntitlement = $decodeAdminArguments[1]
$adminGrantedProperty = $adminEntitlementType.GetProperty(
    "Granted",
    $instanceNonPublic)
$adminSequenceProperty = $adminEntitlementType.GetProperty(
    "Sequence",
    $instanceNonPublic)
$adminLifetimeProperty = $adminEntitlementType.GetProperty(
    "ValidForMilliseconds",
    $instanceNonPublic)
Assert-True ($adminGrantedProperty.GetValue(
    $decodedAdminEntitlement,
    $null)) `
    "The admin command grant did not round-trip."
Assert-True ($adminSequenceProperty.GetValue(
    $decodedAdminEntitlement,
    $null) -eq 1) `
    "The admin command entitlement sequence did not round-trip."
Assert-True ($adminLifetimeProperty.GetValue(
    $decodedAdminEntitlement,
    $null) -eq 5000) `
    "The admin command entitlement lifetime did not round-trip."

$adminEntitlementBytes = [byte[]]$zPackageGetArray.Invoke(
    $adminEntitlementPackage,
    $null)
$adminFixedPacketBytes = $adminEntitlementCodecType.GetField(
    "FixedPacketBytes",
    $staticNonPublic).GetRawConstantValue()
Assert-True (
    $adminFixedPacketBytes -eq 61 -and
    $adminEntitlementBytes.Length -eq $adminFixedPacketBytes) `
    "The admin entitlement packet is no longer exactly 61 bytes."

$adminGrantOffset =
    4 + 2 + 16 + 32 + 4
$adminLifetimeOffset = $adminGrantOffset + 1
$adminSequenceOffset =
    4 + 2 + 16 + 32

$invalidAdminMagic = [byte[]]$adminEntitlementBytes.Clone()
$invalidAdminMagic[0] = [byte]($invalidAdminMagic[0] -bxor 255)
$decodeAdminArguments = [object[]]::new(3)
$decodeAdminArguments[0] = New-ZPackage $invalidAdminMagic
Assert-True (-not $decodeAdminEntitlement.Invoke(
    $null,
    $decodeAdminArguments)) `
    "An invalid admin entitlement magic value was accepted."

$invalidAdminWireVersion = [byte[]]$adminEntitlementBytes.Clone()
$invalidAdminWireVersion[4] = 2
$invalidAdminWireVersion[5] = 0
$decodeAdminArguments = [object[]]::new(3)
$decodeAdminArguments[0] = New-ZPackage $invalidAdminWireVersion
Assert-True (-not $decodeAdminEntitlement.Invoke(
    $null,
    $decodeAdminArguments)) `
    "An invalid admin entitlement wire version was accepted."

$zeroAdminSequence = [byte[]]$adminEntitlementBytes.Clone()
for ($offset = 0; $offset -lt 4; ++$offset) {
    $zeroAdminSequence[$adminSequenceOffset + $offset] = 0
}
$decodeAdminArguments = [object[]]::new(3)
$decodeAdminArguments[0] = New-ZPackage $zeroAdminSequence
Assert-True (-not $decodeAdminEntitlement.Invoke(
    $null,
    $decodeAdminArguments)) `
    "A zero admin entitlement sequence was accepted."

$invalidAdminGrant = [byte[]]$adminEntitlementBytes.Clone()
$invalidAdminGrant[$adminGrantOffset] = 2
$decodeAdminArguments = [object[]]::new(3)
$decodeAdminArguments[0] = New-ZPackage $invalidAdminGrant
Assert-True (-not $decodeAdminEntitlement.Invoke(
    $null,
    $decodeAdminArguments)) `
    "An invalid admin entitlement grant flag was accepted."

$shortAdminGrant = [byte[]]$adminEntitlementBytes.Clone()
$shortAdminGrant[$adminLifetimeOffset] = 231
$shortAdminGrant[$adminLifetimeOffset + 1] = 3
$decodeAdminArguments = [object[]]::new(3)
$decodeAdminArguments[0] = New-ZPackage $shortAdminGrant
Assert-True (-not $decodeAdminEntitlement.Invoke(
    $null,
    $decodeAdminArguments)) `
    "A 999 ms admin entitlement was accepted."

$longAdminGrant = [byte[]]$adminEntitlementBytes.Clone()
$longAdminGrant[$adminLifetimeOffset] = 17
$longAdminGrant[$adminLifetimeOffset + 1] = 39
$decodeAdminArguments = [object[]]::new(3)
$decodeAdminArguments[0] = New-ZPackage $longAdminGrant
Assert-True (-not $decodeAdminEntitlement.Invoke(
    $null,
    $decodeAdminArguments)) `
    "A 10001 ms admin entitlement was accepted."

$invalidAdminRevoke = [byte[]]$adminEntitlementBytes.Clone()
$invalidAdminRevoke[$adminGrantOffset] = 0
$decodeAdminArguments = [object[]]::new(3)
$decodeAdminArguments[0] = New-ZPackage $invalidAdminRevoke
Assert-True (-not $decodeAdminEntitlement.Invoke(
    $null,
    $decodeAdminArguments)) `
    "A revocation with a nonzero lifetime was accepted."

$truncatedAdminEntitlement =
    [byte[]]::new($adminEntitlementBytes.Length - 1)
[Array]::Copy(
    $adminEntitlementBytes,
    $truncatedAdminEntitlement,
    $truncatedAdminEntitlement.Length)
$decodeAdminArguments = [object[]]::new(3)
$decodeAdminArguments[0] = New-ZPackage $truncatedAdminEntitlement
Assert-True (-not $decodeAdminEntitlement.Invoke(
    $null,
    $decodeAdminArguments)) `
    "A truncated admin entitlement was accepted."

$trailingAdminEntitlement =
    [byte[]]::new($adminEntitlementBytes.Length + 1)
[Array]::Copy(
    $adminEntitlementBytes,
    $trailingAdminEntitlement,
    $adminEntitlementBytes.Length)
$decodeAdminArguments = [object[]]::new(3)
$decodeAdminArguments[0] = New-ZPackage $trailingAdminEntitlement
Assert-True (-not $decodeAdminEntitlement.Invoke(
    $null,
    $decodeAdminArguments)) `
    "A trailing admin entitlement byte was accepted."

$limitsType = $pluginAssembly.GetType(
    "ServerManager.ConnectionProtocolLimits",
    $true)
$challengeType = $pluginAssembly.GetType(
    "ServerManager.ProtocolChallengeOptions",
    $true)
Assert-True ($null -eq $pluginAssembly.GetType(
    "ServerManager.ClientDetectionPolicy",
    $false)) `
    "The duplicate client detection policy DTO still exists."
$protocolCodecType = $pluginAssembly.GetType(
    "ServerManager.ProtocolPacketCodec",
    $true)
$allStatic = [Reflection.BindingFlags]"Static,Public,NonPublic"
$protocolWireVersion = $protocolCodecType.GetField(
    "WireVersion",
    [Reflection.BindingFlags]"Static,Public").GetRawConstantValue()
Assert-True ($protocolWireVersion -eq 21) `
    "The main protocol wire version was not bumped to 21 for scoped library manifest challenges and updates."

$limitsArguments = [object[]]::new(6)
$limitsArguments[0] = 512 * 1024
$limitsArguments[1] = 256 * 1024
$limitsArguments[2] = 256 * 1024
$limitsArguments[3] = 512
$limits = $limitsType.GetConstructors()[0].Invoke($limitsArguments)

$challengeArguments = [object[]]::new(16)
$challengeArguments[0] = $true
$challengeArguments[1] = $true
$challengeArguments[2] = 256 * 1024
$challengeArguments[3] = $true
$challengeArguments[4] = $true
$challengeArguments[5] = $true
$challengeArguments[6] = $true
$challengeArguments[7] = $true
$challengeArguments[8] = $true
$challengeArguments[9] = $true
$challengeArguments[10] = 45
$challengeArguments[11] = $true
$challengeArguments[12] = [single]5000
$challengeArguments[13] = $true
$challengeArguments[14] = [single]55000
$challenge = $challengeType.GetConstructors()[0].Invoke(
    $challengeArguments)

$createChallenge = $protocolCodecType.GetMethod(
    "CreateChallenge",
    $allStatic)
$script:decodeProtocol = $protocolCodecType.GetMethod(
    "TryDecode",
    $allStatic)
$script:decodeChallenge = $protocolCodecType.GetMethod(
    "TryDecodeChallengeOptions",
    $allStatic)

$createArguments = [object[]]::new(4)
$createArguments[0] = $sessionId
$createArguments[1] = $nonce
$createArguments[2] = $challenge
$createArguments[3] = $limits
$challengePackage = $createChallenge.Invoke($null, $createArguments)

$decodedPacket = Decode-ProtocolPacket $challengePackage $limits
Assert-True $decodedPacket.Succeeded `
    "A valid wire-v21 challenge packet was rejected."
$decodedChallenge = Decode-ChallengeOptions $decodedPacket.Packet
Assert-True $decodedChallenge.Succeeded `
    "Valid wire-v21 challenge options were rejected."

$createFinalSaveBegin = $protocolCodecType.GetMethod(
    "CreateFinalSaveBegin",
    $allStatic)
$createFinalSaveReady = $protocolCodecType.GetMethod(
    "CreateFinalSaveReady",
    $allStatic)
Assert-True ($null -ne $createFinalSaveBegin -and
    $null -ne $createFinalSaveReady) `
    "The wire-v21 final-save gate packet factories were missing."
$finalControlArguments = [object[]]@($sessionId, $nonce, $limits)
$finalBeginPacket = Decode-ProtocolPacket (
    $createFinalSaveBegin.Invoke($null, $finalControlArguments)) $limits
Assert-True ($finalBeginPacket.Succeeded -and
    $finalBeginPacket.Packet.Kind.ToString() -eq "FinalSaveBegin" -and
    $finalBeginPacket.Packet.Sequence -eq 6 -and
    $finalBeginPacket.Packet.Payload.Length -eq 0) `
    "The final-save begin control packet did not round-trip."
$finalReadyPacket = Decode-ProtocolPacket (
    $createFinalSaveReady.Invoke($null, $finalControlArguments)) $limits
Assert-True ($finalReadyPacket.Succeeded -and
    $finalReadyPacket.Packet.Kind.ToString() -eq "FinalSaveReady" -and
    $finalReadyPacket.Packet.Sequence -eq 7 -and
    $finalReadyPacket.Packet.Payload.Length -eq 0) `
    "The final-save ready control packet did not round-trip."

$genericProperty = $challengeType.GetProperty(
    "DetectGenericProcessNames")
$blockProperty = $challengeType.GetProperty("BlockCheatCommands")
$adminBypassProperty = $challengeType.GetProperty(
    "AllowAdminCheatCommands")
$intervalProperty = $challengeType.GetProperty(
    "ProcessScanIntervalSeconds")
$carryEnabledProperty = $challengeType.GetProperty(
    "EnforceCarryWeightLimit")
$carryLimitProperty = $challengeType.GetProperty(
    "MaximumCarryWeight")
$damageEnabledProperty = $challengeType.GetProperty(
    "EnforceMaximumDamageLimit")
$damageLimitProperty = $challengeType.GetProperty(
    "MaximumDamage")
Assert-True ($genericProperty.GetValue(
    $decodedChallenge.Options,
    $null)) "The generic-name policy flag did not round-trip."
Assert-True ($blockProperty.GetValue(
    $decodedChallenge.Options,
    $null)) "The command-blocking policy flag did not round-trip."
Assert-True ($adminBypassProperty.GetValue(
    $decodedChallenge.Options,
    $null)) "The admin command-bypass policy flag did not round-trip."
Assert-True ($intervalProperty.GetValue(
    $decodedChallenge.Options,
    $null) -eq 45) "The process interval did not round-trip."
Assert-True ($carryEnabledProperty.GetValue(
    $decodedChallenge.Options,
    $null)) "The carry-weight policy flag did not round-trip."
Assert-True ($carryLimitProperty.GetValue(
    $decodedChallenge.Options,
    $null) -eq [single]5000) "The carry-weight limit did not round-trip."
Assert-True ($damageEnabledProperty.GetValue(
    $decodedChallenge.Options,
    $null)) "The maximum-damage policy flag did not round-trip."
Assert-True ($damageLimitProperty.GetValue(
    $decodedChallenge.Options,
    $null) -eq [single]55000) "The maximum-damage limit did not round-trip."

$rateLimiterType = $pluginAssembly.GetType(
    "ServerManager.DetectionRateLimiter",
    $true)
$tryAdmitReport = $rateLimiterType.GetMethod(
    "TryAdmit",
    $staticNonPublic)
$commandReportWindow = [System.Collections.Generic.Queue[long]]::new()
for ($attempt = 0; $attempt -lt 64; ++$attempt) {
    Assert-True ($tryAdmitReport.Invoke(
        $null,
        [object[]]@(
            $commandReportWindow,
            [long]1000,
            [long]100,
            64))) `
        "The command rolling window filled too early."
}
Assert-True (-not $tryAdmitReport.Invoke(
    $null,
    [object[]]@(
        $commandReportWindow,
        [long]1000,
        [long]100,
        64))) `
    "The command rolling flood cap accepted a 65th report."
Assert-True ($tryAdmitReport.Invoke(
    $null,
    [object[]]@(
        $commandReportWindow,
        [long]1101,
        [long]100,
        64))) `
    "The command rolling window did not expire old reports."

$challengeBytes = [byte[]]$zPackageGetArray.Invoke(
    $challengePackage,
    $null)
$previousWireBytes = [byte[]]$challengeBytes.Clone()
$previousWireBytes[4] = 14
$previousWireBytes[5] = 0
Assert-True (-not (Decode-ProtocolPacket (
    New-ZPackage $previousWireBytes) $limits).Succeeded) `
    "A previous wire-v14 peer with private/normal server chat support was accepted."
$wireV6Bytes = [byte[]]$challengeBytes.Clone()
$wireV6Bytes[4] = 6
$wireV6Bytes[5] = 0
Assert-True (-not (Decode-ProtocolPacket (
    New-ZPackage $wireV6Bytes) $limits).Succeeded) `
    "A legacy wire-v6 packet without gameplay limits was accepted."

$fixedHeaderBytes = $protocolCodecType.GetField(
    "FixedHeaderBytes",
    [Reflection.BindingFlags]"Static,Public").GetRawConstantValue()
$unknownFlags = [byte[]]$challengeBytes.Clone()
$unknownFlags[$fixedHeaderBytes + 1] = [byte](
    $unknownFlags[$fixedHeaderBytes + 1] -bor 128)
$unknownPacket = Decode-ProtocolPacket (
    New-ZPackage $unknownFlags) $limits
Assert-True $unknownPacket.Succeeded `
    "The main envelope rejected a well-formed reserved flag test."
Assert-True (-not (Decode-ChallengeOptions (
    $unknownPacket.Packet)).Succeeded) `
    "An unknown reserved challenge flag was accepted."

$invalidPolicyBytes = [byte[]]$challengeBytes.Clone()
$invalidPolicyBytes[$fixedHeaderBytes] = [byte](
    $invalidPolicyBytes[$fixedHeaderBytes] -band 0xDF)
$invalidPolicyPacket = Decode-ProtocolPacket (
    New-ZPackage $invalidPolicyBytes) $limits
Assert-True $invalidPolicyPacket.Succeeded `
    "The main envelope rejected a malformed policy-combination test."
Assert-True (-not (Decode-ChallengeOptions (
    $invalidPolicyPacket.Packet)).Succeeded) `
    "A wire challenge enabled command blocking without monitoring."

$nanCarryBytes = [byte[]]$challengeBytes.Clone()
$carryFloatOffset = $fixedHeaderBytes + 8
$nanCarryBytes[$carryFloatOffset] = 0
$nanCarryBytes[$carryFloatOffset + 1] = 0
$nanCarryBytes[$carryFloatOffset + 2] = 0xC0
$nanCarryBytes[$carryFloatOffset + 3] = 0x7F
$nanCarryPacket = Decode-ProtocolPacket (
    New-ZPackage $nanCarryBytes) $limits
Assert-True ($nanCarryPacket.Succeeded -and
    -not (Decode-ChallengeOptions $nanCarryPacket.Packet).Succeeded) `
    "A wire challenge containing a NaN carry-weight limit was accepted."

$infiniteDamageBytes = [byte[]]$challengeBytes.Clone()
$damageFloatOffset = $fixedHeaderBytes + 12
$infiniteDamageBytes[$damageFloatOffset] = 0
$infiniteDamageBytes[$damageFloatOffset + 1] = 0
$infiniteDamageBytes[$damageFloatOffset + 2] = 0x80
$infiniteDamageBytes[$damageFloatOffset + 3] = 0x7F
$infiniteDamagePacket = Decode-ProtocolPacket (
    New-ZPackage $infiniteDamageBytes) $limits
Assert-True ($infiniteDamagePacket.Succeeded -and
    -not (Decode-ChallengeOptions $infiniteDamagePacket.Packet).Succeeded) `
    "A wire challenge containing an infinite maximum-damage limit was accepted."

$invalidChallengeArguments = [object[]]$challengeArguments.Clone()
$invalidChallengeArguments[7] = $false
$invalidChallengeArguments[8] = $true
$invalidCombinationRejected = $false
try {
    $null = $challengeType.GetConstructors()[0].Invoke(
        $invalidChallengeArguments)
}
catch {
    $invalidCombinationRejected =
        $_.Exception.Message -like "*requires command monitoring*"
}

Assert-True $invalidCombinationRejected `
    "Command blocking without monitoring was accepted."

foreach ($invalidCarryLimit in @(
    [single]0,
    [single]::NaN,
    [single]::PositiveInfinity,
    [single]1000001)) {
    $invalidLimitArguments = [object[]]$challengeArguments.Clone()
    $invalidLimitArguments[12] = $invalidCarryLimit
    $invalidLimitRejected = $false
    try {
        $null = $challengeType.GetConstructors()[0].Invoke(
            $invalidLimitArguments)
    }
    catch {
        $invalidLimitRejected = $true
    }

    Assert-True $invalidLimitRejected `
        "An invalid carry-weight challenge limit was accepted."
}

foreach ($invalidDamageLimit in @(
    [single]0,
    [single]::NaN,
    [single]::NegativeInfinity,
    [single]1000000100)) {
    $invalidLimitArguments = [object[]]$challengeArguments.Clone()
    $invalidLimitArguments[14] = $invalidDamageLimit
    $invalidLimitRejected = $false
    try {
        $null = $challengeType.GetConstructors()[0].Invoke(
            $invalidLimitArguments)
    }
    catch {
        $invalidLimitRejected = $true
    }

    Assert-True $invalidLimitRejected `
        "An invalid maximum-damage challenge limit was accepted."
}

$gameplayLimitType = $pluginAssembly.GetType(
    "ServerManager.GameplayLimitValidation",
    $true)
$exceedsCarryWeight = $gameplayLimitType.GetMethod(
    "ExceedsCarryWeight",
    $staticNonPublic)
$exceedsDamageComponents = $gameplayLimitType.GetMethod(
    "ExceedsDamageComponents",
    $staticNonPublic)
$script:inspectRoutedDamage = $gameplayLimitType.GetMethod(
    "InspectRoutedDamage",
    $staticNonPublic)
$routedDamageObservationType = $pluginAssembly.GetType(
    "ServerManager.RoutedDamageObservation",
    $true)
$observationProperties = @(
    $routedDamageObservationType.GetProperties($instanceNonPublic))
Assert-True (
    $routedDamageObservationType.IsSealed -and
    @($observationProperties | Where-Object CanWrite).Count -eq 0 -and
    @($observationProperties | Where-Object Name -eq "Hit").Count -eq 1 -and
    @($observationProperties | Where-Object Name -eq "Target").Count -eq 1 -and
    @($observationProperties | Where-Object Name -eq "TargetPrefabHash").Count -eq 1 -and
    @($observationProperties | Where-Object Name -eq "TargetPrefabName").Count -eq 1 -and
    @($observationProperties | Where-Object Name -eq "TargetKind").Count -eq 1) `
    "Routed-damage decoding no longer returns immutable target metadata."
Assert-True (-not $exceedsCarryWeight.Invoke(
    $null,
    [object[]]@([single]5000, [single]300, [single]5000))) `
    "A carry weight equal to the configured ceiling was rejected."
Assert-True ($exceedsCarryWeight.Invoke(
    $null,
    [object[]]@([single]5000.5, [single]300, [single]5000))) `
    "An effective carry weight above the ceiling was accepted."
Assert-True ($exceedsCarryWeight.Invoke(
    $null,
    [object[]]@([single]300, [single]5001, [single]5000))) `
    "A base carry weight above the ceiling was accepted."
Assert-True ($exceedsCarryWeight.Invoke(
    $null,
    [object[]]@([single]::NaN, [single]300, [single]5000))) `
    "A non-finite carry weight was accepted."

$zeroDamageComponents = @(
    [single]0, [single]0, [single]0, [single]0, [single]0,
    [single]0, [single]0, [single]0, [single]0, [single]0)
Assert-True (-not $exceedsDamageComponents.Invoke(
    $null,
    [object[]]@([single]55000, [single]1, [single]55000) +
        $zeroDamageComponents)) `
    "Damage equal to the configured ceiling was rejected."
Assert-True ($exceedsDamageComponents.Invoke(
    $null,
    [object[]]@([single]55000, [single]1, [single]55000.5) +
        $zeroDamageComponents)) `
    "Damage above the configured ceiling was accepted."
Assert-True ($exceedsDamageComponents.Invoke(
    $null,
    [object[]]@([single]55000, [single]1, [single]::PositiveInfinity) +
        $zeroDamageComponents)) `
    "Non-finite damage was accepted."
Assert-True ($exceedsDamageComponents.Invoke(
    $null,
    [object[]]@([single]55000, [single]1, [single]60000, [single]-5000) +
        $zeroDamageComponents[1..9])) `
    "A negative damage component was allowed to cancel oversized damage."
Assert-True ($exceedsDamageComponents.Invoke(
    $null,
    [object[]]@([single]55000, [single]6, [single]10000) +
        $zeroDamageComponents)) `
    "A declared backstab multiplier bypassed the maximum potential damage."
Assert-True ($exceedsDamageComponents.Invoke(
    $null,
    [object[]]@([single]55000, [single]::PositiveInfinity, [single]1) +
        $zeroDamageComponents)) `
    "A non-finite backstab multiplier was accepted."

$routedPrefix = [byte[]]@(0xA1, 0xB2, 0xC3)
$unknownParameters = [byte[]]@(1, 2, 3, 4)
$validUnknownPackage = New-ZPackage (New-RoutedEnvelopeBytes `
    -DeclaredParameterLength $unknownParameters.Length `
    -ParameterBytes $unknownParameters `
    -Prefix $routedPrefix)
$validUnknownPackage.SetPos($routedPrefix.Length)
$validUnknownStart = $validUnknownPackage.GetPos()
$validUnknownInspection = Invoke-RoutedDamageInspection $validUnknownPackage
Assert-True (
    $validUnknownInspection.Result.ToString() -eq "NotDamage" -and
    $null -eq $validUnknownInspection.Observation) `
    "A valid unknown routed envelope was classified as damage or malformed."
Assert-True ($validUnknownPackage.GetPos() -eq $validUnknownStart) `
    "Routed inspection did not restore the package position after success."

foreach ($malformedRoutedCase in @(
    [pscustomobject]@{
        Name = "negative inner length"
        Bytes = New-RoutedEnvelopeBytes `
            -DeclaredParameterLength -1 `
            -Prefix $routedPrefix
    },
    [pscustomobject]@{
        Name = "maximum inner length"
        Bytes = New-RoutedEnvelopeBytes `
            -DeclaredParameterLength ([int]::MaxValue) `
            -Prefix $routedPrefix
    },
    [pscustomobject]@{
        Name = "mismatched inner length"
        Bytes = New-RoutedEnvelopeBytes `
            -DeclaredParameterLength 3 `
            -ParameterBytes ([byte[]]@(7, 8)) `
            -Prefix $routedPrefix
    },
    [pscustomobject]@{
        Name = "truncated routed header"
        Bytes = [byte[]]@(
            $routedPrefix + [byte[]]::new(26))
    })) {
    $malformedPackage = New-ZPackage $malformedRoutedCase.Bytes
    $malformedPackage.SetPos($routedPrefix.Length)
    $malformedStart = $malformedPackage.GetPos()
    $malformedInspection = Invoke-RoutedDamageInspection $malformedPackage
    Assert-True (
        $malformedInspection.Result.ToString() -eq "MalformedRoutedRpc" -and
        $null -eq $malformedInspection.Observation) `
        ("A {0} was not rejected as malformed routed framing." -f `
            $malformedRoutedCase.Name)
    Assert-True ($malformedPackage.GetPos() -eq $malformedStart) `
        ("Routed inspection did not restore the package position for {0}." -f `
            $malformedRoutedCase.Name)
}

$processType = $pluginAssembly.GetType(
    "ServerManager.ExternalProcessDetector",
    $true)
$moduleNameMatcher = $processType.GetMethod(
    "IsCheatEngineNativeModuleName",
    $staticNonPublic)
$expectedModuleNames = @(
    "speedhack-i386.dll",
    "speedhack-x86_64.dll",
    "vehdebug-i386.dll",
    "vehdebug-x86_64.dll",
    "MonoDataCollector32.dll",
    "MonoDataCollector64.dll"
)
Assert-True ($null -ne $moduleNameMatcher) `
    "The exact Cheat Engine native-module matcher was missing."
foreach ($moduleName in $expectedModuleNames) {
    Assert-True ($moduleNameMatcher.Invoke(
        $null,
        [object[]]@($moduleName))) `
        ("The expected Cheat Engine module was missing: " + $moduleName)
    Assert-True ($moduleNameMatcher.Invoke(
        $null,
        [object[]]@($moduleName.ToUpperInvariant()))) `
        ("Native-module matching was not case-insensitive: " + $moduleName)
}

foreach ($rejectedModuleName in @(
    "",
    "speedhack-x86_64",
    "speedhack-x86_64.exe",
    "speedhack-x86_64.dll.bak",
    "myspeedhack-x86_64.dll",
    "speedhack-x86_64-helper.dll",
    " speedhack-x86_64.dll",
    "speedhack-x86_64.dll ",
    "C:\tools\speedhack-x86_64.dll",
    ".\speedhack-x86_64.dll",
    "speedhack-x86_64.dll:stream",
    "vehdebug.dll",
    "MonoDataCollector.dll",
    "dbk64.dll",
    "luaclient-x86_64.dll"
)) {
    Assert-True (-not $moduleNameMatcher.Invoke(
        $null,
        [object[]]@($rejectedModuleName))) `
        ("A non-exact native-module name matched: " +
         $rejectedModuleName)
}
Assert-True (-not $moduleNameMatcher.Invoke(
    $null,
    [object[]]@($null))) `
    "A null native-module name matched."

$moduleNamesField = $processType.GetField(
    "CheatEngineNativeModuleNames",
    $staticNonPublic)
$moduleNames = $moduleNamesField.GetValue($null)
Assert-True ($moduleNames.Count -eq $expectedModuleNames.Count) `
    "The fixed native-module catalog gained an unreviewed signature."

$catalogType = $pluginAssembly.GetType(
    "ServerManager.DetectionEvidenceCatalog",
    $true)
$isCheatEngineEvidence = $catalogType.GetMethod(
    "IsCheatEngine",
    $staticNonPublic)
$isStickyEvidence = $catalogType.GetMethod(
    "IsSticky",
    $staticNonPublic)
Assert-True ($isCheatEngineEvidence.Invoke(
    $null,
    [object[]]@($injectedModuleEvidence))) `
    "Injected-module evidence was not classified as Cheat Engine evidence."
Assert-True ($isStickyEvidence.Invoke(
    $null,
    [object[]]@($injectedModuleEvidence))) `
    "Injected-module evidence was not sticky."

$isExpectedEvidenceDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "IsExpectedDetectionEvidence"
$isExpectedEvidenceCalls = @(
    $isExpectedEvidenceDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$expectedCheatEngineCall = $isExpectedEvidenceCalls |
    Where-Object {
        $_.Operand.Name -eq "IsCheatEngine" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.DetectionEvidenceCatalog"
    } |
    Select-Object -First 1
$expectedPolicyGetter = $isExpectedEvidenceCalls |
    Where-Object {
        $_.Operand.Name -eq "get_Policy" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime/ServerDetectionState" -and
        $_.Offset -gt $expectedCheatEngineCall.Offset
    } |
    Select-Object -First 1
$expectedCheatEnginePolicyCall = $isExpectedEvidenceCalls |
    Where-Object {
        $_.Operand.Name -eq "get_DetectCheatEngine" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ProtocolChallengeOptions" -and
        $_.Offset -gt $expectedPolicyGetter.Offset
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $expectedCheatEngineCall -and
    $null -ne $expectedPolicyGetter -and
    $null -ne $expectedCheatEnginePolicyCall -and
    $expectedCheatEngineCall.Offset -lt
        $expectedPolicyGetter.Offset -and
    $expectedPolicyGetter.Offset -lt
        $expectedCheatEnginePolicyCall.Offset) `
    "Cheat Engine evidence was not gated by DetectCheatEngine."

$expectedCarryPolicyCall = $isExpectedEvidenceCalls |
    Where-Object {
        $_.Operand.Name -eq "get_EnforceCarryWeightLimit" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ProtocolChallengeOptions"
    } |
    Select-Object -First 1
$expectedDamagePolicyCall = $isExpectedEvidenceCalls |
    Where-Object {
        $_.Operand.Name -eq "get_EnforceMaximumDamageLimit" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ProtocolChallengeOptions"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $expectedCarryPolicyCall -and
    $null -ne $expectedDamagePolicyCall) `
    "Gameplay-limit evidence was not gated by the pinned session policy."

$externalProcessDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.ExternalProcessDetector"
$scanDefinition = $externalProcessDefinition.Methods |
    Where-Object Name -eq "Scan"
$moduleScanDefinition = $externalProcessDefinition.Methods |
    Where-Object Name -eq "TryScanCurrentProcessNativeModules"
$processNameScanDefinition = $externalProcessDefinition.Methods |
    Where-Object Name -eq "TryScanProcessNames"
$scanCalls = @(
    $scanDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$detectCheatEngineCall = $scanCalls |
    Where-Object {
        $_.Operand.Name -eq "get_DetectCheatEngine" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ExternalProcessScanPolicy"
    } |
    Select-Object -First 1
$currentModuleScanCall = $scanCalls |
    Where-Object {
        $_.Operand.Name -eq "TryScanCurrentProcessNativeModules" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ExternalProcessDetector"
    } |
    Select-Object -First 1
$modulePolicyBranch = Get-FirstConditionalBranch `
    $detectCheatEngineCall `
    $currentModuleScanCall
$policyTargetReachesModule =
    $null -ne $modulePolicyBranch -and
    (Test-CecilReachable `
        -Start $modulePolicyBranch.Operand `
        -Target $currentModuleScanCall)
$policyFallthroughReachesModule =
    $null -ne $modulePolicyBranch -and
    (Test-CecilReachable `
        -Start $modulePolicyBranch.Next `
        -Target $currentModuleScanCall)
Assert-True (
    $null -ne $detectCheatEngineCall -and
    $null -ne $currentModuleScanCall -and
    $null -ne $modulePolicyBranch -and
    $policyTargetReachesModule -ne $policyFallthroughReachesModule) `
    "The current-process module scan was not gated by DetectCheatEngine."

$moduleScanCalls = @(
    $moduleScanDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$processNameScanCalls = @(
    $processNameScanDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
Assert-True (
    ($moduleScanCalls |
        Where-Object {
            $_.Operand.Name -eq "GetCurrentProcess" -and
            $_.Operand.DeclaringType.FullName -eq
                "System.Diagnostics.Process"
        }).Count -eq 1 -and
    ($moduleScanCalls |
        Where-Object {
            $_.Operand.Name -eq "get_Modules" -and
            $_.Operand.DeclaringType.FullName -eq
                "System.Diagnostics.Process"
        }).Count -eq 1 -and
    ($moduleScanCalls |
        Where-Object { $_.Operand.Name -eq "GetProcesses" }).Count -eq 0) `
    "Native modules were not restricted to the current Valheim process."
Assert-True (
    ($processNameScanCalls |
        Where-Object {
            $_.Operand.Name -eq "GetProcesses" -and
            $_.Operand.DeclaringType.FullName -eq
                "System.Diagnostics.Process"
        }).Count -eq 1 -and
    ($processNameScanCalls |
        Where-Object { $_.Operand.Name -eq "get_Modules" }).Count -eq 0) `
    "The system-wide process-name pass began enumerating process modules."

$detectorMemberNames = @(
    $externalProcessDefinition.Methods.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MemberReference] } |
        ForEach-Object { $_.Operand.Name })
foreach ($forbiddenObservation in @(
    "get_MainWindowTitle",
    "get_MainWindowHandle",
    "EnumWindows",
    "GetClassName",
    "GetClassNameW",
    "CreateToolhelp32Snapshot",
    "Module32First",
    "Module32Next",
    "NtQuerySystemInformation"
)) {
    Assert-True ($detectorMemberNames -notcontains $forbiddenObservation) `
        ("An out-of-scope window, driver, or all-process module API was " +
         "introduced: " + $forbiddenObservation)
}
Assert-True (($externalProcessDefinition.Methods |
    Where-Object IsPInvokeImpl).Count -eq 0) `
    "The phase-one detector introduced native window or driver P/Invoke."

$processEvidenceDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "ProcessDetectionEvidence"
$processEvidenceCalls = @(
    $processEvidenceDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$processGameplayCall = $processEvidenceCalls |
    Where-Object {
        $_.Operand.Name -eq "IsGameplayLimit" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.DetectionEvidenceCatalog"
    } |
    Select-Object -First 1
$cheatResponseGetter = $processEvidenceCalls |
    Where-Object {
        $_.Operand.Name -eq "get_CheatDetectionResponse" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime/ServerDetectionState"
    } |
    Select-Object -First 1
$statResponseGetter = $processEvidenceCalls |
    Where-Object {
        $_.Operand.Name -eq "get_StatLimitResponse" -and
        $_.Operand.DeclaringType.FullName -eq
            "ServerManager.ServerManagerRuntime/ServerDetectionState"
    } | Select-Object -First 1
$liveActionConfigCalls = @(
    $processEvidenceCalls |
        Where-Object {
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerPlugin"
        })
$adminCallsInEvidenceAction = @(
    $processEvidenceCalls |
        Where-Object {
            $_.Operand.Name -eq "IsCurrentServerAdmin" -or
            $_.Operand.Name -eq "get_AllowAdminCheatCommands"
        })
Assert-True (
    $null -ne $processGameplayCall -and
    $null -ne $cheatResponseGetter -and
    $null -ne $statResponseGetter -and
    $processGameplayCall.Offset -lt $cheatResponseGetter.Offset -and
    $processGameplayCall.Offset -lt $statResponseGetter.Offset -and
    $liveActionConfigCalls.Count -eq 0 -and
    $adminCallsInEvidenceAction.Count -eq 0) `
    "Detection evidence no longer uses its two pinned response paths before centralized admin revalidation."

$responseSelectionBranch = Get-FirstConditionalBranch $processGameplayCall $null
$invalidGameplayConstant = $processEvidenceDefinition.Body.Instructions |
    Where-Object { (Get-CecilIntConstant $_) -eq 406 -and
        $_.Offset -gt $processGameplayCall.Offset -and $_.Offset -lt $cheatResponseGetter.Offset } |
    Select-Object -First 1
$invalidGameplayBranch = Get-FirstConditionalBranch $invalidGameplayConstant $cheatResponseGetter
Assert-True ($null -ne $responseSelectionBranch -and
    $responseSelectionBranch.OpCode.Name -like 'brtrue*' -and
    (Test-CecilReachable $responseSelectionBranch.Operand $statResponseGetter) -and
    -not (Test-CecilReachable $responseSelectionBranch.Operand $cheatResponseGetter) -and
    (Test-CecilReachable $responseSelectionBranch.Next $cheatResponseGetter) -and
    $null -ne $invalidGameplayConstant -and $null -ne $invalidGameplayBranch -and
    (Test-CecilReachable $responseSelectionBranch.Next $invalidGameplayConstant) -and
    ((Test-CecilReachable $invalidGameplayBranch.Operand $statResponseGetter) -xor
     (Test-CecilReachable $invalidGameplayBranch.Next $statResponseGetter)) -and
    ((Test-CecilReachable $invalidGameplayBranch.Operand $cheatResponseGetter) -xor
     (Test-CecilReachable $invalidGameplayBranch.Next $cheatResponseGetter))) `
    "Numeric limits and invalid gameplay values must select StatLimitResponse while other detectors select CheatDetectionResponse."
Assert-True (@($processEvidenceCalls | Where-Object {
    $_.Operand.Name -in @('IsCheatEngine', 'IsExternalTool', 'IsValheimTooler')
}).Count -eq 0) "The unified detector response retained per-tool action branches."

$applyEvidenceCall = $processEvidenceCalls |
    Where-Object { $_.Operand.Name -eq 'ApplyDetectionAction' } | Select-Object -First 1
foreach ($logOnlyReason in @('detector diagnostic', 'low-confidence observation')) {
    $reasonLiteral = $processEvidenceDefinition.Body.Instructions |
        Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $logOnlyReason } |
        Select-Object -First 1
    $observationLogCall = $processEvidenceCalls |
        Where-Object { $_.Operand.Name -eq 'LogDetection' -and $_.Offset -gt $reasonLiteral.Offset } |
        Select-Object -First 1
    Assert-True ($null -ne $reasonLiteral -and (Get-CecilIntConstant $reasonLiteral.Previous) -eq 1 -and
        $null -ne $observationLogCall -and
        -not (Test-CecilReachable $observationLogCall.Next $applyEvidenceCall)) `
        "Log-only evidence $logOnlyReason can reach a terminal response."
}

$commandProcessDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq 'ProcessCheatCommandReport' | Select-Object -First 1
$commandProcessCalls = @($commandProcessDefinition.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$attemptLogCalls = @($commandProcessCalls | Where-Object { $_.Operand.Name -eq 'LogDetection' })
$attemptActionCalls = @($commandProcessCalls | Where-Object { $_.Operand.Name -eq 'ApplyDetectionAction' })
$commandResponseGetter = $commandProcessCalls |
    Where-Object { $_.Operand.Name -eq 'get_CheatDetectionResponse' } | Select-Object -First 1
Assert-True ($attemptLogCalls.Count -eq 1 -and $attemptActionCalls.Count -eq 1 -and
    $null -ne $commandResponseGetter -and
    -not (Test-CecilReachable $attemptLogCalls[0].Next $attemptActionCalls[0]) -and
    -not (Test-CecilReachable $attemptActionCalls[0].Next $attemptLogCalls[0])) `
    "A repeated command can produce both the ordinary attempt log and a duplicate threshold log."
foreach ($requiredCommandMember in @('Enqueue', 'Dequeue', 'get_CheatCommandThreshold',
    'get_CheatCommandWindowSeconds', 'get_CheatCommandThresholdActionApplied',
    'set_CheatCommandThresholdActionApplied')) {
    Assert-True ($commandProcessCalls.Operand.Name -contains $requiredCommandMember) `
        "The bounded once-per-session command threshold lost $requiredCommandMember."
}

$normalizeResponseDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq 'NormalizeDetectionAction' | Select-Object -First 1
$normalizeInstructions = @($normalizeResponseDefinition.Body.Instructions)
$normalizeComparisons = @($normalizeInstructions | Where-Object {
    $_.OpCode.Name -eq 'ldarg.0' -and $null -ne (Get-CecilIntConstant $_.Next)
})
Assert-True ($normalizeComparisons.Count -eq 3) `
    "Response normalization must explicitly accept only three nonzero actions."
for ($responseIndex = 0; $responseIndex -lt 3; $responseIndex++) {
    Assert-True ((Get-CecilIntConstant $normalizeComparisons[$responseIndex].Next) -eq ($responseIndex + 1) -and
        $normalizeComparisons[$responseIndex].Next.Next.OpCode.Name -match '^(beq|ceq|bne\.un)') `
        "Runtime response normalization no longer compares only Log/Kick/Ban."
}
$normalizeLogError = $normalizeInstructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'LogError' } |
    Select-Object -First 1
$normalizeFallbackConstants = @($normalizeInstructions | Where-Object {
    $_.Offset -gt $normalizeLogError.Offset -and $null -ne (Get-CecilIntConstant $_)
})
Assert-True ($null -ne $normalizeLogError -and $normalizeFallbackConstants.Count -eq 1 -and
    (Get-CecilIntConstant $normalizeFallbackConstants[0]) -eq 1) `
    "Off and undefined runtime response values must fall back to Log, never disable detection."
$applyNormalize = $applyDetectionCalls | Where-Object { $_.Operand.Name -eq 'NormalizeDetectionAction' } |
    Select-Object -First 1
$applyLog = $applyDetectionCalls | Where-Object { $_.Operand.Name -eq 'LogDetection' } |
    Select-Object -First 1
Assert-True ($null -ne $applyNormalize -and $null -ne $applyLog -and
    $applyNormalize.Offset -lt $applyLog.Offset -and
    -not (Test-CecilReachable -Start $applyDetectionActionDefinition.Body.Instructions[0] `
        -Target $applyLog -Blocked @($applyNormalize))) `
    "Every response must normalize invalid/Off values before logging and enforcement."

$statAuditDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq 'RecordCharacterStatLimits' | Select-Object -First 1
Assert-True ($null -ne $statAuditDefinition -and $statAuditDefinition.Parameters.Count -eq 6 -and
    $statAuditDefinition.Parameters[5].Name -eq 'applyResponse' -and
    $statAuditDefinition.Parameters[5].HasConstant -and
    -not [bool]$statAuditDefinition.Parameters[5].Constant) `
    "Snapshot stat observations must default to no response."
$statAuditInstructions = @($statAuditDefinition.Body.Instructions)
$statAuditCalls = @($statAuditInstructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$statApplyCall = $statAuditCalls | Where-Object { $_.Operand.Name -eq 'ApplyDetectionAction' } |
    Select-Object -First 1
$statPinnedResponse = $statAuditCalls | Where-Object { $_.Operand.Name -eq 'CurrentNumericLimitResponse' } |
    Select-Object -First 1
$statIdentityComparisons = @($statAuditCalls | Where-Object {
    $_.Operand.DeclaringType.FullName -eq 'System.String' -and $_.Operand.Name -eq 'Equals'
})
$statPlayerIdentityStores = @($statAuditInstructions | Where-Object {
    $_.OpCode.Name -like 'stloc*' -and $_.Previous.OpCode.Name -ne 'ldnull' -and
    (($_.Operand -is [Mono.Cecil.Cil.VariableDefinition] -and
        $_.Operand.VariableType.FullName -eq 'ServerManager.ServerPeerIdentity') -or
     ($_.OpCode.Name -match '^stloc\.[0-3]$' -and
        $statAuditDefinition.Body.Variables[[int]$_.OpCode.Name.Substring(6)].VariableType.FullName -eq 'ServerManager.ServerPeerIdentity'))
})
Assert-True ($null -ne $statApplyCall -and $null -ne $statPinnedResponse -and
    $statPinnedResponse.Offset -lt $statApplyCall.Offset -and
    $statIdentityComparisons.Count -eq 2 -and $statPlayerIdentityStores.Count -eq 1) `
    "Snapshot stat action lost its pinned response or uniquely authenticated identity assignment."
$statIdentityStore = $statPlayerIdentityStores[0]
$statGuardCalls = @($statAuditCalls | Where-Object {
    $_.Offset -lt $statIdentityStore.Offset -and
    ($_.Operand.Name -in @('IsServer', 'TryGetSnapshot', 'get_State',
        'get_PeerInfoAuthenticated', 'TryResolveActiveDetectionPeer') -or
     ($_.Operand.Name -eq 'Equals' -and $_.Operand.DeclaringType.FullName -eq 'System.String'))
})
Assert-True ($statGuardCalls.Count -eq 7) `
    "Snapshot stat action must check server role, Ready/authenticated session, live Steam peer, account, and character."
foreach ($guardCall in $statGuardCalls) {
    $guardBranch = Get-FirstConditionalBranch $guardCall $statIdentityStore
    $failedTarget = $guardBranch.Operand
    # Debug short-circuit predicates converge on false in a local, then reload
    # that local before jumping over the only non-null identity assignment.
    if ($null -ne $failedTarget -and $failedTarget.OpCode.Name -eq 'ldc.i4.0') {
        $falseStore = $failedTarget.Next
        $falseLoad = $falseStore.Next
        $falseBranch = $falseLoad.Next
        if ($falseStore.OpCode.Name -like 'stloc*' -and
            $falseLoad.OpCode.Name -eq $falseStore.OpCode.Name.Replace('stloc', 'ldloc') -and
            $falseLoad.Operand -eq $falseStore.Operand -and $falseBranch.OpCode.Name -like 'brfalse*') {
            $failedTarget = $falseBranch.Operand
        }
    }
    Assert-True ($null -ne $guardBranch -and $guardBranch.OpCode.Name -match '^(brfalse|bne\.un)' -and
        $null -ne $failedTarget -and $failedTarget.Offset -gt $statIdentityStore.Offset) `
        "Failed $($guardCall.Operand.Name) can populate an enforceable snapshot-stat identity."
}
$statStrings = @($statAuditInstructions | Where-Object { $_.OpCode.Name -eq 'ldstr' } | ForEach-Object Operand)
Assert-True ($statStrings -contains 'steamworks:' -and
    @($statAuditCalls | Where-Object { $_.Operand.Name -eq 'get_HostId' }).Count -eq 1 -and
    @($statAuditCalls | Where-Object { $_.Operand.Name -eq 'get_PlayerName' }).Count -eq 1 -and
    @($statAuditCalls | Where-Object { $_.Operand.Name -eq 'NormalizeAndValidate' -and
        $_.Operand.DeclaringType.FullName -eq 'ServerManager.CharacterNamePolicy' }).Count -eq 1 -and
    @($statAuditCalls | Where-Object { $_.Operand.Name -eq 'IsCurrentServerAdmin' }).Count -eq 0) `
    "Snapshot-stat identity must use socket Steam64 and canonical character matching before centralized admin revalidation."
$statReadyCall = $statGuardCalls | Where-Object { $_.Operand.Name -eq 'get_State' } | Select-Object -First 1
Assert-True ((Get-CecilIntConstant $statReadyCall.Next) -eq 4) `
    "Snapshot-stat response requires the actual Ready session state."
$statGetServer = $statAuditCalls | Where-Object {
    $_.Operand.Name -eq 'get_instance' -and $_.Operand.DeclaringType.FullName -eq 'ZNet'
} | Select-Object -First 1
$statApplyFlagLoad = $statAuditInstructions | Where-Object {
    $_.OpCode.Name -like 'ldarg*' -and $_.Operand -is [Mono.Cecil.ParameterDefinition] -and
    $_.Operand.Name -eq 'applyResponse'
} | Select-Object -First 1
$statApplyFlagBranch = Get-FirstConditionalBranch $statApplyFlagLoad $statGetServer
$statSourceLiteral = $statAuditInstructions | Where-Object {
    $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'snapshot_received'
} | Select-Object -First 1
$statSourceBranch = Get-FirstConditionalBranch $statSourceLiteral $statGetServer
Assert-True ($null -ne $statGetServer -and $null -ne $statApplyFlagBranch -and $null -ne $statSourceBranch -and
    $statApplyFlagBranch.OpCode.Name -like 'brfalse*' -and $statSourceBranch.OpCode.Name -like 'brfalse*' -and
    -not (Test-CecilReachable $statApplyFlagBranch.Operand $statGetServer) -and
    -not (Test-CecilReachable $statSourceBranch.Operand $statGetServer)) `
    "Snapshot-stat response must require an explicit applyResponse flag and snapshot_received source before using the live server."
$statResponseCatch = $statAuditDefinition.Body.ExceptionHandlers | Where-Object {
    $_.HandlerType.ToString() -eq 'Filter' -and $statApplyCall.Offset -ge $_.TryStart.Offset -and
    $statApplyCall.Offset -lt $_.TryEnd.Offset
} | Select-Object -First 1
Assert-True ($null -ne $statResponseCatch -and
    @($statAuditInstructions | Where-Object {
        $_.Offset -ge $statResponseCatch.HandlerStart.Offset -and $_.Offset -lt $statResponseCatch.HandlerEnd.Offset -and
        $_.OpCode.Name -in @('throw', 'rethrow')
    }).Count -eq 0 -and $statStrings -contains 'processing_failed') `
    "Optional post-ACK stat processing failures must be locally audited without escaping into save rejection."
foreach ($statMapping in @(
    @{ Code = 'maximum_health'; Evidence = 403 },
    @{ Code = 'maximum_stamina'; Evidence = 404 },
    @{ Code = 'maximum_eitr'; Evidence = 405 })) {
    $codeLiteral = $statAuditInstructions | Where-Object {
        $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $statMapping.Code
    } | Select-Object -First 1
    $mappingBranch = Get-FirstConditionalBranch $codeLiteral $statApplyCall
    Assert-True ($null -ne $codeLiteral -and $null -ne $mappingBranch -and
        $mappingBranch.OpCode.Name -like 'brtrue*' -and
        (Get-CecilIntConstant $mappingBranch.Operand) -eq $statMapping.Evidence) `
        "Typed snapshot stat $($statMapping.Code) no longer maps to its dedicated server-only evidence."
}

$incomingSaveDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq 'HandleServerCharacterFragment' | Select-Object -First 1
$incomingSaveCalls = @($incomingSaveDefinition.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$saveAdmissionCall = $incomingSaveCalls | Where-Object { $_.Operand.Name -eq 'HandleSaveRequest' } |
    Select-Object -First 1
$saveAckSend = $incomingSaveCalls | Where-Object { $_.Operand.Name -eq 'SendCharacterPayload' } |
    Select-Object -First 1
$incomingStatCall = $incomingSaveCalls | Where-Object { $_.Operand.Name -eq 'RecordCharacterStatLimits' } |
    Select-Object -First 1
Assert-True ($null -ne $saveAdmissionCall -and $null -ne $saveAckSend -and $null -ne $incomingStatCall -and
    $saveAdmissionCall.Offset -lt $saveAckSend.Offset -and $saveAckSend.Offset -lt $incomingStatCall.Offset -and
    $incomingStatCall.Previous.Operand.Name -eq 'get_Accepted' -and
    -not (Test-CecilReachable -Start $incomingSaveDefinition.Body.Instructions[0] -Target $incomingStatCall -Blocked @($saveAdmissionCall)) -and
    -not (Test-CecilReachable -Start $incomingSaveDefinition.Body.Instructions[0] -Target $incomingStatCall -Blocked @($saveAckSend))) `
    "Snapshot stat response must follow admission and ACK sending, with enforcement gated by Accepted."
$hostStatDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq 'ServerManager.LocalHostCharacterRuntime' | Select-Object -First 1
$observationOnlyStatCalls = @(@($runtimeDefinition.Methods | Where-Object {
    $_.Name -notin @('HandleServerCharacterFragment', 'HandleServerPacket')
}).Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RecordCharacterStatLimits' })
$hostStatCalls = @($hostStatDefinition.Methods.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RecordCharacterStatLimits' })
Assert-True ($observationOnlyStatCalls.Count -eq 1 -and $hostStatCalls.Count -eq 2) `
    "Stored remote and stored/incoming host stat observation call sites changed."
foreach ($observationCall in @($observationOnlyStatCalls) + @($hostStatCalls)) {
    Assert-True ((Get-CecilIntConstant $observationCall.Previous) -eq 0) `
        "Stored or listen-host stat observation can request a remote sanction."
}
$backupReadyHandler = $runtimeDefinition.Methods | Where-Object Name -eq 'HandleServerPacket' | Select-Object -First 1
$backupReadyCalls = @($backupReadyHandler.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$backupReadyStat = @($backupReadyCalls | Where-Object { $_.Operand.Name -eq 'RecordCharacterStatLimits' })
$backupReadyFinalize = $backupReadyCalls | Where-Object { $_.Operand.Name -eq 'FinalizePendingInitialSnapshot' } | Select-Object -First 1
$backupReadyRelease = $backupReadyCalls | Where-Object { $_.Operand.Name -eq 'ReleaseWorld' } | Select-Object -First 1
Assert-True ($backupReadyStat.Count -eq 1 -and
    (Get-CecilIntConstant $backupReadyStat[0].Previous) -eq 1 -and
    $backupReadyFinalize.Offset -lt $backupReadyStat[0].Offset -and
    $backupReadyStat[0].Offset -lt $backupReadyRelease.Offset -and
    -not (Test-CecilReachable -Start $backupReadyHandler.Body.Instructions[0] -Target $backupReadyStat[0] -Blocked @($backupReadyFinalize))) `
    'Initial backup stat response must follow capture finalization and precede world release.'
$backupCaptureHandler = $runtimeDefinition.Methods | Where-Object Name -eq 'HandleServerBackupCapture' | Select-Object -First 1
Assert-True ($null -ne $backupCaptureHandler -and @($backupCaptureHandler.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RecordCharacterStatLimits'
}).Count -eq 0) 'An uncommitted capture cannot trigger numeric-stat sanctions.'
$securityLogDefinition = $runtimeDefinition.Methods | Where-Object Name -eq 'LogDetection' | Select-Object -First 1
$securityResponseDefinition = $runtimeDefinition.Methods | Where-Object Name -eq 'RecordDetectionResponse' | Select-Object -First 1
$consoleDetectionCalls = @($securityLogDefinition.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -in @('LogInfo', 'LogWarning')
})
Assert-True ($consoleDetectionCalls.Count -eq 2) "Detection console diagnostics disappeared."
foreach ($consoleDetectionCall in $consoleDetectionCalls) {
    Assert-True (@($securityLogDefinition.Body.ExceptionHandlers | Where-Object {
        $_.HandlerType.ToString() -eq 'Filter' -and $consoleDetectionCall.Offset -ge $_.TryStart.Offset -and
        $consoleDetectionCall.Offset -lt $_.TryEnd.Offset
    }).Count -eq 1) "A throwing console log listener can interrupt detection enforcement."
}
foreach ($hookSpec in @(
    @{ Definition = $securityLogDefinition; Kind = 'security.detection' },
    @{ Definition = $securityResponseDefinition; Kind = 'security.response' })) {
    Assert-True (@($hookSpec.Definition.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $hookSpec.Kind
    }).Count -eq 1 -and @($hookSpec.Definition.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RecordSecurityEvent'
    }).Count -ge 1) "The runtime lost its local audit hook for $($hookSpec.Kind)."
}
Assert-True (@($incomingSaveDefinition.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RecordCharacterSaveRejected'
}).Count -eq 1) 'The incoming save path lost its typed rejection audit hook.'
$terminalOutcomeStrings = @($terminalInstructions | Where-Object { $_.OpCode.Name -eq 'ldstr' } | ForEach-Object Operand)
foreach ($terminalOutcome in @('banlist_add_returned', 'banlist_add_failed',
    'kick_call_returned', 'disconnect_fallback_scheduled')) {
    Assert-True ($terminalOutcomeStrings -contains $terminalOutcome) `
        "Terminal audit lost its qualified $terminalOutcome outcome."
}
$routedTerminalCall = $serverRoutedDamageDefinition.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'ExecuteTerminalDetectionAction'
} | Select-Object -First 1
$routedTerminalEvidence = $serverRoutedDamageDefinition.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'set_TerminalEvidence' -and
    $_.Offset -lt $routedTerminalCall.Offset
} | Select-Object -First 1
$routedTerminalSource = $serverRoutedDamageDefinition.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'set_TerminalSource' -and
    $_.Offset -lt $routedTerminalCall.Offset
} | Select-Object -First 1
$routedMalformedLog = $serverRoutedDamageDefinition.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'LogDetection' -and
    $_.Offset -lt $routedTerminalCall.Offset
} | Select-Object -First 1
Assert-True ($null -ne $routedTerminalCall -and (Get-CecilIntConstant $routedTerminalCall.Previous) -eq 2 -and
    $null -ne $routedTerminalEvidence -and (Get-CecilIntConstant $routedTerminalEvidence.Previous) -eq 402 -and
    $null -ne $routedTerminalSource -and $routedTerminalSource.Previous.Operand -eq 'server_observed' -and
    $null -ne $routedMalformedLog) `
    "Malformed routed framing lost its fixed Kick or server-observed audit attribution."

$cheatEngineNames = $processType.GetField(
    "CheatEngineNames",
    $staticNonPublic).GetValue($null)
$externalNames = $processType.GetField(
    "ExternalToolNames",
    $staticNonPublic).GetValue($null)
$genericNames = $processType.GetField(
    "GenericProcessNames",
    $staticNonPublic).GetValue($null)

Assert-True (Dictionary-ContainsKey $cheatEngineNames "cheatengine") `
    "The exact Cheat Engine process name was missing."
Assert-True (-not (Dictionary-ContainsKey `
    $cheatEngineNames "cheatengine-helper")) `
    "A Cheat Engine superstring matched."
Assert-True (Dictionary-ContainsKey $externalNames "SMI_GUI") `
    "Case-insensitive smi_gui matching failed."
Assert-True (-not (Dictionary-ContainsKey $externalNames "smi")) `
    "Bare smi matched without its metadata condition."
Assert-True (Dictionary-ContainsKey $genericNames "trainer") `
    "The exact generic trainer observation was missing."
Assert-True (-not (Dictionary-ContainsKey $genericNames "trainer64")) `
    "A generic-name superstring matched."
Assert-True (-not (Dictionary-ContainsKey $genericNames "injector")) `
    "The generic inject name used substring matching."

$metadataMatcher = $processType.GetMethod(
    "IsSharpMonoInjectorProductValue",
    $staticNonPublic)
Assert-True ($metadataMatcher.Invoke(
    $null,
    [object[]]@("SharpMonoInjector.Console"))) `
    "The exact SharpMonoInjector metadata value did not match."
Assert-True (-not $metadataMatcher.Invoke(
    $null,
    [object[]]@("SharpMonoInjector"))) `
    "A SharpMonoInjector metadata substring matched."
Assert-True (-not $metadataMatcher.Invoke(
    $null,
    [object[]]@(" SharpMonoInjector.Console "))) `
    "Whitespace was trimmed from strict injector metadata."

$normalizeProcessName = $processType.GetMethod(
    "NormalizeProcessName",
    $staticNonPublic)
Assert-True ($normalizeProcessName.Invoke(
    $null,
    [object[]]@("trainer.exe")) -eq "trainer") `
    "The executable extension was not normalized."
Assert-True ($normalizeProcessName.Invoke(
    $null,
    [object[]]@(" trainer.exe ")) -eq " trainer.exe ") `
    "Whitespace was trimmed from a strict process name."

$toolerInspectorType = $pluginAssembly.GetType(
    "ServerManager.ValheimToolerAssemblyInspector",
    $true)
$detectionEvidenceType = $pluginAssembly.GetType(
    "ServerManager.DetectionEvidence",
    $true)
$detectionEvidenceNames = [Enum]::GetNames($detectionEvidenceType)
Assert-True (
    $detectionEvidenceNames -contains "ValheimToolerDetected" -and
    $detectionEvidenceNames -notcontains "ValheimToolerAssembly" -and
    $detectionEvidenceNames -notcontains "ValheimToolerNamespace") `
    "ValheimTooler evidence was not consolidated to one wire value."
$inspectTooler = $toolerInspectorType.GetMethod(
    "Inspect",
    $staticNonPublic)
$toolerResultType = $pluginAssembly.GetType(
    "ServerManager.ValheimToolerInspection",
    $true)
$toolerDetectedProperty = $toolerResultType.GetProperty(
    "Detected",
    $instanceNonPublic)

$benignResult = $inspectTooler.Invoke(
    $null,
    [object[]]@($pluginAssembly))
Assert-True (-not $toolerDetectedProperty.GetValue(
    $benignResult,
    $null)) "A benign assembly matched ValheimTooler."

$toolerAssemblyName = New-Object Reflection.AssemblyName(
    "ValheimTooler")
$toolerAssembly = if ($PSVersionTable.PSEdition -eq "Core") {
    [Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly(
        $toolerAssemblyName, [Reflection.Emit.AssemblyBuilderAccess]::Run)
} else {
    [AppDomain]::CurrentDomain.DefineDynamicAssembly(
        $toolerAssemblyName, [Reflection.Emit.AssemblyBuilderAccess]::Run)
}
$toolerModule = $toolerAssembly.DefineDynamicModule("main")
$null = $toolerModule.DefineType(
    "Neutral.Probe",
    [Reflection.TypeAttributes]::Public).CreateType()
$toolerAssemblyResult = $inspectTooler.Invoke(
    $null,
    [object[]]@($toolerAssembly))
Assert-True ($toolerDetectedProperty.GetValue(
    $toolerAssemblyResult,
    $null)) "The exact ValheimTooler assembly name did not match."

$namespaceAssemblyName = New-Object Reflection.AssemblyName(
    "NamespaceProbe")
$namespaceAssembly = if ($PSVersionTable.PSEdition -eq "Core") {
    [Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly(
        $namespaceAssemblyName, [Reflection.Emit.AssemblyBuilderAccess]::Run)
} else {
    [AppDomain]::CurrentDomain.DefineDynamicAssembly(
        $namespaceAssemblyName, [Reflection.Emit.AssemblyBuilderAccess]::Run)
}
$namespaceModule = $namespaceAssembly.DefineDynamicModule("main")
$null = $namespaceModule.DefineType(
    "ValheimTooler.Probe",
    [Reflection.TypeAttributes]::Public).CreateType()
$toolerNamespaceResult = $inspectTooler.Invoke(
    $null,
    [object[]]@($namespaceAssembly))
Assert-True ($toolerDetectedProperty.GetValue(
    $toolerNamespaceResult,
    $null)) "The ValheimTooler namespace prefix did not match."

$commandGuardType = $pluginAssembly.GetType(
    "ServerManager.CheatCommandGuard",
    $true)
$sanitizeCommandDetail = $commandGuardType.GetMethod(
    "SanitizeDetail",
    $staticNonPublic)
$validateCommandDetail = $codecType.GetMethod(
    "IsValidDetail",
    $staticNonPublic,
    $null,
    [Type[]]@([string]),
    $null)
# The guard and wire retain the same bounded ASCII grammar; invalid input
# becomes an empty report detail, not a normalized or partially copied token.
$commandDetailCases = @('', 'devcommands', 'a0_-z9', ('a' * 48), ('a' * 49),
    'DEVCOMMANDS', 'devcommands 1', "god`t", "god`n", 'a.b', '한글', 'ａ')
$commandDetailCases += 0..127 | ForEach-Object { [string][char]$_ }
foreach ($commandDetail in $commandDetailCases) {
    $expectedValid = $commandDetail.Length -le 48 -and
        $commandDetail -cmatch '\A[a-z0-9_-]*\z'
    $expectedDetail = if ($expectedValid) { $commandDetail } else { '' }
    Assert-True ($validateCommandDetail.Invoke(
        $null, [object[]]@($commandDetail)) -eq $expectedValid) `
        "The detection wire command-detail grammar or 48-character limit changed."
    Assert-True ($sanitizeCommandDetail.Invoke(
        $null, [object[]]@($commandDetail)) -ceq $expectedDetail) `
        "The command guard did not preserve a valid detail or clear an invalid detail."
}
$enqueueCommand = $commandGuardType.GetMethod(
    "TryEnqueue",
    $staticNonPublic)
$dequeueCommand = $commandGuardType.GetMethod(
    "TryDequeue",
    $staticNonPublic)
$resetCommand = $commandGuardType.GetMethod(
    "Reset",
    $staticNonPublic)
$configureCommand = $commandGuardType.GetMethod(
    "Configure",
    $staticNonPublic)
$applyAdminEntitlement = $commandGuardType.GetMethod(
    "ApplyAdminEntitlement",
    $staticNonPublic)
$hasActiveAdminEntitlement = $commandGuardType.GetMethod(
    "HasActiveAdminEntitlement",
    $staticNonPublic)
$adminEntitlementExpiryField = $commandGuardType.GetField(
    "_adminEntitlementExpiryTimestamp",
    $staticNonPublic)
$shouldAllowCommand = $commandGuardType.GetMethod(
    "ShouldAllowExecution",
    $staticNonPublic)
$resetCommand.Invoke($null, $null)

Assert-True (-not $shouldAllowCommand.Invoke(
    $null,
    [object[]]@($true, $false, $true, $true))) `
    "An admin command without a queued report bypassed local blocking."
Assert-True ($shouldAllowCommand.Invoke(
    $null,
    [object[]]@($true, $true, $true, $true))) `
    "A reported admin command did not bypass local blocking."
Assert-True (-not $shouldAllowCommand.Invoke(
    $null,
    [object[]]@($true, $true, $false, $true))) `
    "A disabled admin bypass allowed a blocked command."
Assert-True ($shouldAllowCommand.Invoke(
    $null,
    [object[]]@($false, $false, $false, $false))) `
    "A command was blocked while command blocking was disabled."

$configureCommand.Invoke(
    $null,
    [object[]]@($true, $true, $true))
Assert-True (-not $hasActiveAdminEntitlement.Invoke(
    $null,
    $null)) "Admin execution was granted without a server entitlement."
$applyAdminEntitlement.Invoke(
    $null,
    [object[]]@($true, 1000))
Assert-True ($hasActiveAdminEntitlement.Invoke(
    $null,
    $null)) "A valid server admin entitlement was not activated."
$adminEntitlementExpiryField.SetValue(
    $null,
    [Diagnostics.Stopwatch]::GetTimestamp() - 1)
Assert-True (-not $hasActiveAdminEntitlement.Invoke(
    $null,
    $null)) "An expired server admin entitlement remained active."
$applyAdminEntitlement.Invoke(
    $null,
    [object[]]@($true, 1000))
$applyAdminEntitlement.Invoke(
    $null,
    [object[]]@($false, 0))
Assert-True (-not $hasActiveAdminEntitlement.Invoke(
    $null,
    $null)) "A server admin revocation did not clear local execution."
$configureCommand.Invoke(
    $null,
    [object[]]@($true, $true, $false))
$applyAdminEntitlement.Invoke(
    $null,
    [object[]]@($true, 1000))
Assert-True (-not $hasActiveAdminEntitlement.Invoke(
    $null,
    $null)) "A disabled policy accepted a server admin entitlement."
$configureCommand.Invoke(
    $null,
    [object[]]@($true, $true, $true))

for ($attempt = 0; $attempt -lt 3; ++$attempt) {
    Assert-True ($enqueueCommand.Invoke(
        $null,
        [object[]]@("devcommands"))) `
        "A command report could not be queued."
}

for ($attempt = 0; $attempt -lt 3; ++$attempt) {
    $dequeueArguments = [object[]]::new(1)
    Assert-True ($dequeueCommand.Invoke(
        $null,
        $dequeueArguments)) `
        "Repeated command attempts were incorrectly de-duplicated."
}

$emptyDequeueArguments = [object[]]::new(1)
Assert-True (-not $dequeueCommand.Invoke(
    $null,
    $emptyDequeueArguments)) `
    "The command signal queue contained an unexpected report."

for ($attempt = 0; $attempt -lt 8; ++$attempt) {
    Assert-True ($enqueueCommand.Invoke(
        $null,
        [object[]]@("devcommands"))) `
        "The bounded command queue filled too early."
}
Assert-True (-not $enqueueCommand.Invoke(
    $null,
    [object[]]@("devcommands"))) `
    "A ninth command report was accepted by the bounded queue."

$boundedCount = 0
while ($true) {
    $dequeueArguments = [object[]]::new(1)
    if (-not $dequeueCommand.Invoke($null, $dequeueArguments)) {
        break
    }

    ++$boundedCount
}

Assert-True ($boundedCount -eq 8) `
    "The command signal queue was not bounded to eight reports."
$resetCommand.Invoke($null, $null)

$detectionLogType = $pluginAssembly.GetType(
    "ServerManager.DetectionLog",
    $true)
$quoteLogValue = $detectionLogType.GetMethod(
    "QuoteValue",
    $staticNonPublic)
$quotedLogValue = $quoteLogValue.Invoke(
    $null,
    [object[]]@("name`r`n action=Ban `"quoted`"", 64))
Assert-True ($quotedLogValue.StartsWith('"') -and
    $quotedLogValue.EndsWith('"')) `
    "The detection log value was not quoted."
Assert-True (-not $quotedLogValue.Contains("`r") -and
    -not $quotedLogValue.Contains("`n")) `
    "A detection log value retained a line break."
Assert-True ($quotedLogValue.Contains('\"')) `
    "A quote in a detection log value was not escaped."

$eventReportType = $pluginAssembly.GetType(
    "ServerManager.Events.EventClientReport",
    $true)
$eventReportKindType = $pluginAssembly.GetType(
    "ServerManager.Events.EventClientReportKind",
    $true)
$eventReportCodecType = $pluginAssembly.GetType(
    "ServerManager.Events.EventClientReportCodec",
    $true)
$eventReport = [Activator]::CreateInstance($eventReportType, $true)
$eventInstance = [Reflection.BindingFlags]"Instance,NonPublic"
$eventReportType.GetProperty("SessionId", $eventInstance).SetValue(
    $eventReport,
    $sessionId)
$eventReportType.GetProperty("Nonce", $eventInstance).SetValue(
    $eventReport,
    $nonce)
$eventReportType.GetProperty("Sequence", $eventInstance).SetValue(
    $eventReport,
    [uint32]1)
$eventReportType.GetProperty("Kind", $eventInstance).SetValue(
    $eventReport,
    [Enum]::Parse($eventReportKindType, "Shout"))
$eventReportType.GetProperty("Text", $eventInstance).SetValue(
    $eventReport,
    "bounded shout")
$encodeEventReport = $eventReportCodecType.GetMethod(
    "Encode",
    $staticNonPublic)
$decodeEventReport = $eventReportCodecType.GetMethod(
    "TryDecode",
    $staticNonPublic)
$eventPackage = $encodeEventReport.Invoke(
    $null,
    [object[]]@($eventReport))
Assert-True ($eventPackage.Size() -le 4096) `
    "The client event report exceeded its hard packet bound."
$decodeEventArguments = [object[]]::new(3)
$decodeEventArguments[0] = New-ZPackage $eventPackage.GetArray()
Assert-True ($decodeEventReport.Invoke($null, $decodeEventArguments)) `
    "A valid session-bound event report was rejected."
$decodedEvent = $decodeEventArguments[1]
Assert-True (
    $eventReportType.GetProperty("Sequence", $eventInstance).GetValue(
        $decodedEvent) -eq 1 -and
    $eventReportType.GetProperty("Text", $eventInstance).GetValue(
        $decodedEvent) -eq "bounded shout") `
    "The event report did not round-trip exactly."
$eventBytes = $eventPackage.GetArray()
$trailingEvent = [byte[]]::new($eventBytes.Length + 1)
[Array]::Copy($eventBytes, $trailingEvent, $eventBytes.Length)
$decodeEventArguments = [object[]]::new(3)
$decodeEventArguments[0] = New-ZPackage $trailingEvent
Assert-True (-not $decodeEventReport.Invoke(
    $null,
    $decodeEventArguments)) `
    "An event report with trailing bytes was accepted."

$integrationApiType = $pluginAssembly.GetType(
    "ServerManager.Events.ServerManagerIntegrationApi",
    $true)
Assert-True (
    $integrationApiType.GetField(
        "ApiVersion",
        [Reflection.BindingFlags]"Static,Public").GetRawConstantValue() -eq
        "1.3.0") `
    "The ServerManager integration API version was not 1.3.0."
$saveOperationSnapshotType = $pluginAssembly.GetType(
    "ServerManager.Events.ServerManagerSaveOperationSnapshot",
    $true)
$partialCommitScopeType = $pluginAssembly.GetType(
    "ServerManager.Events.ServerManagerCharacterCommitScope",
    $true)
Assert-True (
    $null -ne $saveOperationSnapshotType.GetProperty(
        "CapturedCharacterCount") -and
    $null -ne $saveOperationSnapshotType.GetProperty(
        "PersistedCharacterCount") -and
    $null -ne $saveOperationSnapshotType.GetProperty(
        "PendingCharacterCount") -and
    [int][Enum]::Parse(
        $partialCommitScopeType,
        "PartialRetainedShadowsAtCutoff") -eq 2) `
    "Integration API 1.3.0 no longer exposes partial checkpoint scope and captured/persisted/pending character counts."
foreach ($methodName in @(
    "GetStatus",
    "GetPlayers",
    "RequestWorldSaveAsync",
    "AnnounceAsync",
    "KickAsync",
    "BanAsync",
    "UnbanAsync")) {
    Assert-True ($null -ne $integrationApiType.GetMethod($methodName)) `
        "The public ServerManager integration API is missing $methodName."
}

$integrationApiDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq `
        "ServerManager.Events.ServerManagerIntegrationApi" |
    Select-Object -First 1
$publishEventDefinition = $integrationApiDefinition.Methods |
    Where-Object Name -eq "Publish" |
    Select-Object -First 1
$publishEventCalls = @(
    $publishEventDefinition.Body.Instructions |
        Where-Object { $_.OpCode.Code -in @("Call", "Callvirt") } |
        ForEach-Object Operand)
Assert-True ($publishEventCalls.Name -contains "TryAdd") `
    "The integration publisher no longer uses a bounded nonblocking queue."

$eventRuntimeDefinition = $pluginDefinition.MainModule.Types |
    Where-Object FullName -eq "ServerManager.Events.ServerEventRuntime" |
    Select-Object -First 1
$authenticatedDeathEvent = $eventRuntimeDefinition.Events |
    Where-Object Name -eq "AuthenticatedPlayerDeathPublished" |
    Select-Object -First 1
$publishDeathDefinition = $eventRuntimeDefinition.Methods |
    Where-Object Name -eq "PublishDeath" |
    Select-Object -First 1
$publishDeathCalls = @(
    $publishDeathDefinition.Body.Instructions |
        Where-Object { $_.OpCode.Code -in @("Call", "Callvirt") } |
        ForEach-Object Operand)
$notifyDeathDefinition = $eventRuntimeDefinition.Methods |
    Where-Object Name -eq "NotifyAuthenticatedPlayerDeath" |
    Select-Object -First 1
$notifyDeathFields = @(
    $notifyDeathDefinition.Body.Instructions |
        Where-Object { $_.OpCode.Code -in @("Ldfld", "Ldsfld") } |
        ForEach-Object Operand)
$publishDefinition = $eventRuntimeDefinition.Methods |
    Where-Object Name -eq "Publish" |
    Select-Object -First 1
$onEventPlayerReadyDefinition = $eventRuntimeDefinition.Methods |
    Where-Object Name -eq "OnPlayerReady" |
    Select-Object -First 1
$readyStateStores = @(
    $onEventPlayerReadyDefinition.Body.Instructions |
        Where-Object { $_.OpCode.Code -eq "Stfld" } |
        ForEach-Object Operand)
Assert-True (
    $null -ne $authenticatedDeathEvent -and
    $authenticatedDeathEvent.EventType.FullName -eq
        'System.Action`2<ZRpc,ServerManager.Events.ServerManagerEvent>' -and
    $publishDefinition.ReturnType.FullName -eq
        "ServerManager.Events.ServerManagerEvent" -and
    ($publishDeathCalls.Name -contains "Publish") -and
    ($publishDeathCalls.Name -contains "NotifyAuthenticatedPlayerDeath") -and
    ($notifyDeathFields.Name -contains "Rpc") -and
    ($notifyDeathFields.Name -contains "TransportId") -and
    ($notifyDeathFields.Name -contains "AccountId") -and
    ($readyStateStores.Name -contains "Rpc")) `
    "Authenticated death fan-out no longer preserves the published event or the live ready-session identity."
$publishDeathStrings = @(
    $publishDeathDefinition.Body.Instructions |
        Where-Object { $_.OpCode.Code -eq "Ldstr" } |
        ForEach-Object Operand)
Assert-True ($publishDeathStrings -contains "client_reported") `
    "Authenticated death fan-out no longer preserves client-reported reliability."
$executeModerationDefinition = $eventRuntimeDefinition.Methods |
    Where-Object Name -eq "ExecuteModeration" |
    Select-Object -First 1
$executeModerationCalls = @(
    $executeModerationDefinition.Body.Instructions |
        Where-Object { $_.OpCode.Code -in @("Call", "Callvirt") } |
        ForEach-Object Operand)
Assert-True (-not ($executeModerationCalls | Where-Object {
    $_.DeclaringType.FullName -eq "ZNet" -and
    $_.Name -in @("Ban", "Unban")
})) `
    "Public moderation can again reinterpret a Steam64 ID as a player name."
$banListMutationDefinition = $eventRuntimeDefinition.Methods |
    Where-Object Name -eq "TrySetBanListEntry" |
    Select-Object -First 1
$banListMutationCalls = @(
    $banListMutationDefinition.Body.Instructions |
        Where-Object { $_.OpCode.Code -in @("Call", "Callvirt") } |
        ForEach-Object Operand)
Assert-True (
    ($executeModerationCalls | Where-Object {
        $_.DeclaringType.FullName -eq
            "ServerManager.Events.ServerEventRuntime" -and
        $_.Name -eq "TrySetBanListEntry"
    }).Count -ge 2 -and
    ($banListMutationCalls | Where-Object {
        $_.DeclaringType.FullName -eq "SyncedList" -and $_.Name -eq "Add"
    }).Count -ge 1 -and
    ($banListMutationCalls | Where-Object {
        $_.DeclaringType.FullName -eq "SyncedList" -and $_.Name -eq "Remove"
    }).Count -ge 1 -and
    ($executeModerationCalls | Where-Object {
        $_.DeclaringType.FullName -eq "ZNet" -and $_.Name -eq "Disconnect"
    }).Count -ge 1) `
    "Public moderation no longer uses literal ban-list IDs and the resolved peer."

$exactTargetMatchDefinition = $eventRuntimeDefinition.Methods |
    Where-Object Name -eq "IsExactModerationTargetMatch" |
    Select-Object -First 1
$runtimeType = $pluginAssembly.GetType(
    "ServerManager.Events.ServerEventRuntime",
    $true)
$exactTargetMatch = $runtimeType.GetMethod(
    "IsExactModerationTargetMatch",
    $staticNonPublic)
$serverNameReader = $runtimeType.GetMethod(
    "ServerName",
    $staticNonPublic)
$victimSteamId = "76561198000000001"
$attackerSteamId = "76561198000000002"
Assert-True ($null -ne $exactTargetMatchDefinition -and
             $null -ne $exactTargetMatch -and
             $null -ne $serverNameReader -and
             $serverNameReader.Invoke($null, [object[]]@()) -is [string]) `
    "The exact moderation target matcher is missing."
Assert-True (-not $exactTargetMatch.Invoke(
    $null,
    [object[]]@(
        $victimSteamId,
        "steamworks:$attackerSteamId",
        $attackerSteamId,
        $victimSteamId))) `
    "A numeric player name can shadow another account's Steam64 ID."
Assert-True ($exactTargetMatch.Invoke(
    $null,
    [object[]]@(
        $victimSteamId,
        "steamworks:$victimSteamId",
        $victimSteamId,
        "unrelated"))) `
    "An exact connected Steam64 target was not recognized."

$eventPacketDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq "OnEventReportPacket" |
    Select-Object -First 1
$eventPacketCalls = @(
    $eventPacketDefinition.Body.Instructions |
        Where-Object { $_.OpCode.Code -in @("Call", "Callvirt") } |
        ForEach-Object Operand)
Assert-True (
    (@($eventPacketCalls | Where-Object Name -eq "FixedTimeEquals").Count -ge 2) -and
    ($eventPacketCalls.Name -contains "TryResolveActiveDetectionPeer") -and
    ($eventPacketCalls.Name -contains "TryProcessClientReport")) `
    "Client events are no longer bound to session ID, nonce, and the live peer."
Assert-True ($null -ne $eventRuntimeDefinition) `
    "The concrete ServerManager event module was not compiled."

# Administrator mod exceptions are only provisional until final Steam auth.
# Inspect the compiled call graph, not merely names present in source comments.
$adminManifestType = $runtimeDefinition.NestedTypes |
    Where-Object Name -eq 'RuntimeManifestValidator' | Select-Object -First 1
$adminAdmission = $adminManifestType.Methods |
    Where-Object Name -eq 'Validate' | Select-Object -First 1
$adminAdmissionCalls = @($adminAdmission.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$provisionalValidation = $adminAdmissionCalls |
    Where-Object { $_.Operand.Name -eq 'ValidateForAdmission' } | Select-Object -First 1
Assert-True ($null -ne $provisionalValidation -and
    @($adminAdmissionCalls | Where-Object { $_.Operand.Name -eq 'IsCurrentServerAdmin' }).Count -eq 0 -and
    $provisionalValidation.Previous.Previous.OpCode.Code.ToString() -eq 'Ldc_I4_1' -and
    @($adminAdmissionCalls | Where-Object { $_.Operand.Name -in @(
        'ConfirmAdminManifest', 'OpenOrCreateServerSession', 'RecordSecurityEvent', 'RecordAdminExemptions') }).Count -eq 0) `
    'Pre-auth mod validation looked up/granted/audited administrator status instead of deferring final review.'
Assert-True (@($adminAdmission.Body.Instructions | Where-Object {
    (Get-CecilIntConstant $_) -eq 64 }).Count -ge 1) `
    'Pending admin manifests lost their fixed authentication-reservation bound.'

$adminConfirmation = $adminManifestType.Methods |
    Where-Object Name -eq 'ConfirmAuthenticated' | Select-Object -First 1
$adminConfirmationCalls = @($adminConfirmation.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$finalAdminLookup = $adminConfirmationCalls |
    Where-Object { $_.Operand.Name -eq 'IsCurrentServerAdmin' } | Select-Object -First 1
$finalPolicyCheck = $adminConfirmationCalls |
    Where-Object { $_.Operand.Name -eq 'ConfirmAdminManifest' } | Select-Object -First 1
$adminAudit = $adminConfirmationCalls |
    Where-Object { $_.Operand.Name -eq 'RecordAdminExemptions' } | Select-Object -First 1
$adminPendingConsumption = $adminConfirmationCalls |
    Where-Object { $_.Operand.Name -eq 'Remove' -and $_.Operand.DeclaringType.FullName.StartsWith('System.Collections.Generic.Dictionary`2<ZRpc,') } |
    Select-Object -First 1
Assert-True ($null -ne $finalAdminLookup -and $null -ne $finalPolicyCheck -and
    $null -ne $adminAudit -and $finalAdminLookup.Offset -lt $finalPolicyCheck.Offset -and
    $finalPolicyCheck.Offset -lt $adminAudit.Offset -and
    $null -ne $adminPendingConsumption -and $adminPendingConsumption.Offset -lt $finalPolicyCheck.Offset -and
    @($adminConfirmationCalls | Where-Object { $_.Operand.Name -eq 'RemovePeer' }).Count -eq 0 -and
    @($adminConfirmationCalls | Where-Object { $_.Operand.Name -eq 'get_HostId' }).Count -ge 1) `
    'Final mod exemption lost current admin membership, connection identity binding, one-shot consumption, or post-check audit.'
Assert-True (@($adminConfirmation.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq '_libraryChecks' }).Count -eq 0) `
    'Consuming a provisional administrator manifest removed the ready-session library monitor.'
$adminAuditHelper = $adminManifestType.Methods | Where-Object Name -eq 'RecordAdminExemptions' | Select-Object -First 1
$adminAuditHelperCalls = @($adminAuditHelper.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
Assert-True (@($adminAuditHelperCalls | Where-Object { $_.Operand.Name -eq 'RecordSecurityEvent' }).Count -eq 1 -and
    @($adminAuditHelperCalls | Where-Object { $_.Operand.Name -eq 'Take' }).Count -eq 1 -and
    @($adminAuditHelperCalls | Where-Object { $_.Operand.Name -eq 'get_HostId' }).Count -eq 1 -and
    @($adminAuditHelper.Body.Instructions | Where-Object { (Get-CecilIntConstant $_) -eq 20 }).Count -eq 1) `
    'Shared administrator exemption auditing lost its account binding, fixed record bound or event sink.'

$authenticatedCompletion = $runtimeDefinition.Methods |
    Where-Object Name -eq 'CompleteAuthenticatedPeer' | Select-Object -First 1
$completionCalls = @($authenticatedCompletion.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$characterOpen = $completionCalls |
    Where-Object { $_.Operand.Name -eq 'OpenOrCreateServerSession' } | Select-Object -First 1
$previousGateOffset = -1
foreach ($gateName in @('ConfirmPeerInfoAuthenticated', 'TryResolveActiveDetectionPeer', 'ConfirmAuthenticated')) {
    $gate = $completionCalls | Where-Object { $_.Operand.Name -eq $gateName } | Select-Object -First 1
    Assert-True ($null -ne $gate -and $null -ne $characterOpen -and
        $gate.Offset -gt $previousGateOffset -and $gate.Offset -lt $characterOpen.Offset -and
        -not (Test-CecilReachable $authenticatedCompletion.Body.Instructions[0] $characterOpen @($gate))) `
        "Character data can be opened without ordered authentication/admin gate $gateName."
    $previousGateOffset = $gate.Offset
}
$confirmationCall = $completionCalls |
    Where-Object { $_.Operand.Name -eq 'ConfirmAuthenticated' } | Select-Object -First 1
$confirmationBranch = Get-FirstConditionalBranch $confirmationCall $characterOpen
Assert-True ($null -ne $confirmationBranch -and
    ((Test-CecilReachable $confirmationBranch.Operand $characterOpen) -xor
     (Test-CecilReachable $confirmationBranch.Next $characterOpen))) `
    'Both outcomes of the final mod-policy decision can reach character creation.'

$characterAdminDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq 'IsCharacterPolicyAdmin' | Select-Object -First 1
$characterAdminCalls = @($characterAdminDefinition.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
    ForEach-Object { $_.Operand.Name })
foreach ($requiredCall in @('TryParseCanonicalAccountId', 'IsServer', 'IsDedicated',
    'GetSteamID', 'IsAdmin', 'TryResolveActiveDetectionPeer', 'TryGetServerSession',
    'EqualsIdentity', 'IsCurrentServerAdmin')) {
    Assert-True ($characterAdminCalls -contains $requiredCall) `
        "Incoming character admin exemption lost live identity/membership check $requiredCall."
}
foreach ($cleanupName in @('SendServerRejection', 'CleanupPeer', 'Shutdown', 'AfterNetworkShutdown')) {
    $cleanupDefinition = $runtimeDefinition.Methods |
        Where-Object Name -eq $cleanupName | Select-Object -First 1
    $cleanupCalls = @($cleanupDefinition.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq $adminManifestType.FullName -and
            $_.Operand.Name -in @('RemovePeer', 'Clear') })
    Assert-True ($cleanupCalls.Count -ge 1) "Pending admin manifests survive $cleanupName."
}

# Live policy control is a separate authenticated exchange, never a second challenge.
$createPolicyUpdate = $protocolCodecType.GetMethod('CreatePolicyUpdate', $allStatic)
$createPolicyAck = $protocolCodecType.GetMethod('CreatePolicyAck', $allStatic)
$policyUpdate = Decode-ProtocolPacket ($createPolicyUpdate.Invoke($null,
    [object[]]@($sessionId, $nonce, [uint32]2, [single]2000, [single]9999, $limits))) $limits
Assert-True ($policyUpdate.Succeeded -and $policyUpdate.Packet.Kind.ToString() -eq 'PolicyUpdate' -and
    $policyUpdate.Packet.Sequence -eq 2 -and $policyUpdate.Packet.Payload.Length -eq 8 -and
    [BitConverter]::ToSingle($policyUpdate.Packet.Payload, 0) -eq [single]2000 -and
    [BitConverter]::ToSingle($policyUpdate.Packet.Payload, 4) -eq [single]9999) 'Live policy limits did not round-trip.'
$policyAck = Decode-ProtocolPacket ($createPolicyAck.Invoke($null,
    [object[]]@($sessionId, $nonce, [uint32]2, $limits))) $limits
Assert-True ($policyAck.Succeeded -and $policyAck.Packet.Kind.ToString() -eq 'PolicyAck' -and
    $policyAck.Packet.Sequence -eq 2 -and $policyAck.Packet.Payload.Length -eq 0) 'Live policy ACK did not round-trip.'
$generationArguments = [object[]]$reportArguments.Clone()
$generationArguments[3] = [Enum]::Parse($evidenceType, 'CarryWeightLimitExceeded')
$generationArguments[4] = ''
$generationArguments[5] = [uint32]7
$generationReport = $reportConstructor.Invoke($generationArguments)
$generationRoundTrip = Decode-DetectionReport ($encodeDetection.Invoke($null, [object[]]@($generationReport)))
Assert-True ($generationRoundTrip.Succeeded -and $reportType.GetProperty('PolicyGeneration',
    $instanceNonPublic).GetValue($generationRoundTrip.Report, $null) -eq 7) 'Observation generation was not preserved on the wire.'
$zeroGeneration = [byte[]]$encoded.Clone()
for ($index = 58; $index -lt 62; ++$index) { $zeroGeneration[$index] = 0 }
Assert-True (-not (Decode-DetectionReport (New-ZPackage $zeroGeneration)).Succeeded) 'A zero observation generation was accepted.'

# A real detector keeps queued old-generation observations and sticky history;
# only a genuinely new observation can produce evidence for the new generation.
$reloadDetectorType = $pluginAssembly.GetType('ServerManager.ClientDetectionAgent', $true)
$reloadDetector = [Activator]::CreateInstance($reloadDetectorType, $true)
$quietArguments = [object[]]$challengeArguments.Clone()
foreach ($index in @(3,4,5,6)) { $quietArguments[$index] = $false }
$quietPolicy = $challengeType.GetConstructors()[0].Invoke($quietArguments)
$null = $reloadDetectorType.GetMethod('Configure', $instanceNonPublic).Invoke($reloadDetector, [object[]]@($quietPolicy))
$emitReloadSignal = $reloadDetectorType.GetMethod('Emit', $instanceNonPublic)
$reloadEvidence = [Enum]::Parse($evidenceType, 'CarryWeightLimitExceeded')
$null = $emitReloadSignal.Invoke($reloadDetector, [object[]]@($reloadEvidence, [uint32]0))
$updateDetector = $reloadDetectorType.GetMethod('UpdateGameplayLimits', $instanceNonPublic)
$null = $updateDetector.Invoke($reloadDetector, [object[]]@([uint32]2, [single]2000, [single]9999))
$null = $emitReloadSignal.Invoke($reloadDetector, [object[]]@($reloadEvidence, [uint32]0))
$null = $emitReloadSignal.Invoke($reloadDetector, [object[]]@($reloadEvidence, [uint32]0))
$dequeueReloadSignal = $reloadDetectorType.GetMethod('TryDequeue', $instanceNonPublic)
$reloadSignalType = $pluginAssembly.GetType('ServerManager.ClientDetectionSignal', $true)
foreach ($expectedGeneration in @(1,2)) {
    $signalArguments = [object[]]@($null)
    Assert-True ($dequeueReloadSignal.Invoke($reloadDetector, $signalArguments)) 'Reload discarded a queued observation.'
    Assert-True ($reloadSignalType.GetProperty('PolicyGeneration', $instanceNonPublic).GetValue(
        $signalArguments[0], $null) -eq $expectedGeneration) 'Reload retagged old evidence or suppressed a fresh observation.'
}
$emptySignalArguments = [object[]]@($null)
Assert-True (-not $dequeueReloadSignal.Invoke($reloadDetector, $emptySignalArguments)) 'A same-generation sticky observation was duplicated.'
$invalidLimitRejected = $false
try { $null = $updateDetector.Invoke($reloadDetector, [object[]]@([uint32]3, [single]::NaN, [single]9999)) }
catch { $invalidLimitRejected = $true }
Assert-True ($invalidLimitRejected -and $reloadDetectorType.GetField('_policyGeneration',
    $instanceNonPublic).GetValue($reloadDetector) -eq 2) 'Invalid reload partially changed detector generation.'
$null = $reloadDetectorType.GetMethod('Reset', $instanceNonPublic).Invoke($reloadDetector, $null)

$settingsRuntimeSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'Networking\ServerManagerRuntime.Settings.cs'))
foreach ($requiredToken in @('ServerDataRoot.ActivePath', 'IsStartItemsCatalogReady', 'ValidateStartItems',
    '_serverCharacterService?.ApplyServerSettings(settings)', 'PolicyAcknowledgementDeadline',
    'TryResolveActiveDetectionPeer', 'FixedTimeEquals', 'UpdateGameplayLimits', 'UpdatePolicyGeneration')) {
    Assert-True ($settingsRuntimeSource.Contains($requiredToken)) "Live settings lost safety integration $requiredToken."
}
foreach ($forbiddenReset in @('ClientDetection.Configure(', 'ClientDetection.Reset(',
    'CheatCommandGuard.Configure(', 'ServerDetectionStates.Clear(', 'DispatchChallenge(')) {
    Assert-True (-not $settingsRuntimeSource.Contains($forbiddenReset)) "Reload reset active security state through $forbiddenReset."
}
$transitionStateType = $pluginAssembly.GetType('ServerManager.ServerManagerRuntime+ServerDetectionState', $true)
$transitionState = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($transitionStateType)
$transitionActionType = $pluginAssembly.GetType('ServerManager.DetectionAction', $true)
$transitionStateType.GetProperty('StatLimitResponse', $instanceNonPublic).SetValue($transitionState,
    [Enum]::Parse($transitionActionType, 'Ban'), $null)
$transitionStateType.GetProperty('PolicyGeneration', $instanceNonPublic).SetValue($transitionState, [uint32]2, $null)
$transitionStateType.GetProperty('AcknowledgedPolicyGeneration', $instanceNonPublic).SetValue($transitionState, [uint32]1, $null)
$policyRuntimeType = $pluginAssembly.GetType('ServerManager.ServerManagerRuntime', $true)
$numericResponseMethod = $policyRuntimeType.GetMethod('CurrentNumericLimitResponse', $staticNonPublic)
Assert-True ($numericResponseMethod.Invoke($null, [object[]]@($transitionState)).ToString() -eq 'Log') `
    'Unacknowledged numeric policy can sanction a hit/profile sent under old advertised limits.'
$transitionStateType.GetProperty('AcknowledgedPolicyGeneration', $instanceNonPublic).SetValue($transitionState, [uint32]2, $null)
Assert-True ($numericResponseMethod.Invoke($null, [object[]]@($transitionState)).ToString() -eq 'Ban') `
    'Acknowledged numeric policy did not restore its configured response.'
foreach ($retryToken in @('DeferServerOpenForSettings', 'ResumeSettingsDeferredServerOpen',
    '_settingsDeferredOpenGeneration != _settingsLifecycleGeneration',
    'ReferenceEquals(deferred, ZNet.instance)', 'ReferenceEquals(deferred, _preparedServerNetwork)',
    'ReferenceEquals(_settingsDeferredOpenGame, Game.instance)',
    'ServerCharacterStorageStartupState.Failed', 'ValheimPrivateAccess.GetOpenServer()',
    'if (_currentServerSettings == null) return;')) {
    Assert-True ($settingsRuntimeSource.Contains($retryToken)) "Deferred listener retry lost guard $retryToken."
}
Assert-True ([Text.RegularExpressions.Regex]::IsMatch($settingsRuntimeSource,
    '_settingsDeferredOpenNetwork = null;\s*_settingsDeferredOpenGame = null;\s*deferred\.OpenServer\(\);')) `
    'The deferred listener retry must consume its latch before invoking the ordinary patched OpenServer entry.'
$retrySource = $settingsRuntimeSource.Substring($settingsRuntimeSource.IndexOf('private static void ResumeSettingsDeferredServerOpen()'))
$retrySource = $retrySource.Substring(0, $retrySource.IndexOf('private static bool ApplyServerSettings'))
Assert-True (-not $retrySource.Contains('SetOpenServer(true)') -and -not $retrySource.Contains('EnsureServerCharacterService(')) `
    'Deferred listener retry bypasses normal admission gates.'
$staleObservationOffset = $fixedRuntimeSource.IndexOf('if (report.PolicyGeneration < state.PolicyGeneration)')
$processObservationOffset = $fixedRuntimeSource.IndexOf('ProcessDetectionEvidence(', $staleObservationOffset)
Assert-True ($staleObservationOffset -ge 0 -and $processObservationOffset -gt $staleObservationOffset -and
    $fixedRuntimeSource.Substring($staleObservationOffset, $processObservationOffset - $staleObservationOffset).Contains(
        'LogDetection(identity, report, DetectionAction.Log, "stale_policy_observation")')) 'Old observations can inherit new terminal responses.'

# Managed dependencies extend the existing admission manifest and have a narrow,
# authenticated late-load update path; they must not create a second admission gate.
$libraryMergeStart = $fixedRuntimeSource.IndexOf('private static void ProcessClientManifestPreparation(')
$libraryMergeEnd = $fixedRuntimeSource.IndexOf('private static void SendClientManifestResponse(', $libraryMergeStart)
Assert-True ($libraryMergeStart -ge 0 -and $libraryMergeEnd -gt $libraryMergeStart) 'Missing initial plugin/library manifest preparation.'
$libraryMergeSource = $fixedRuntimeSource.Substring($libraryMergeStart, $libraryMergeEnd - $libraryMergeStart)
foreach ($libraryMergeToken in @(
    'if (!preparation.TryGetResult(out IntegrityManifestBuildResult build)) return;',
    'if (!libraries.TryGetResult(out IntegrityManifestBuildResult libraryBuild)) return;',
    'if (!libraries.MatchesCurrentAssemblies())',
    'manifest.Entries.Concat(libraryBuild.Manifest.Entries)',
    'manifest, session.ManifestResponseLimits',
    'session.LibraryBaseline = session.LibraryPreparation;',
    'SendClientManifestResponse(session, encoded.Payload)')) {
    Assert-True ($libraryMergeSource.Contains($libraryMergeToken)) "Initial library merge lost $libraryMergeToken."
}
Assert-True ($libraryMergeSource.IndexOf('manifest.Entries.Concat') -lt $libraryMergeSource.IndexOf('IntegrityManifestCodec.TryEncode') -and
    $libraryMergeSource.IndexOf('IntegrityManifestCodec.TryEncode') -lt $libraryMergeSource.IndexOf('SendClientManifestResponse')) `
    'Library entries were not included in the same bounded manifest before admission response.'

$libraryUpdateStart = $fixedRuntimeSource.IndexOf('internal void HandleLibraryUpdate(')
$libraryUpdateEnd = $fixedRuntimeSource.IndexOf('public ManifestValidationDecision Validate(', $libraryUpdateStart)
Assert-True ($libraryUpdateStart -ge 0 -and $libraryUpdateEnd -gt $libraryUpdateStart) 'Missing authenticated library-update handler.'
$libraryUpdateSource = $fixedRuntimeSource.Substring($libraryUpdateStart, $libraryUpdateEnd - $libraryUpdateStart)
foreach ($libraryGuard in @(
    'session.State != ConnectionSessionState.Ready', '!session.PeerInfoAuthenticated',
    'ProtocolByteUtil.FixedTimeEquals(session.SessionId, packet.SessionId)',
    'ProtocolByteUtil.FixedTimeEquals(session.Nonce, packet.Nonce)',
    '_libraryChecks.TryGetValue(rpc, out LibraryCheck check)',
    'check.LastSequence == uint.MaxValue', 'packet.Sequence != check.LastSequence + 1',
    'Stopwatch.GetTimestamp() - check.LastUpdateTimestamp < Stopwatch.Frequency / 2',
    'TryResolveActiveDetectionPeer(server, rpc',
    'identity, check.Policy, packet.Payload, IsCurrentServerAdmin(server, identity)',
    'if (!decision.Accepted) SendServerRejection(rpc, decision.Rejection)',
    'else RecordAdminExemptions(identity, exemptions)')) {
    Assert-True ($libraryUpdateSource.Contains($libraryGuard)) "Library update lost guard $libraryGuard."
}
Assert-True ($libraryUpdateSource.IndexOf('check.LastSequence = packet.Sequence') -gt $libraryUpdateSource.IndexOf('TryResolveActiveDetectionPeer') -and
    $libraryUpdateSource.IndexOf('check.LastSequence = packet.Sequence') -lt $libraryUpdateSource.IndexOf('ValidateLibraryUpdate(')) `
    'Library sequence state must advance only after session and peer authentication, before policy validation.'

$libraryValidatorDefinition = $runtimeDefinition.NestedTypes | Where-Object Name -eq 'RuntimeManifestValidator' | Select-Object -First 1
$libraryUpdateDefinition = $libraryValidatorDefinition.Methods | Where-Object Name -eq 'HandleLibraryUpdate' | Select-Object -First 1
Assert-True ($null -ne $libraryUpdateDefinition -and $libraryUpdateDefinition.HasBody) 'No compiled library update handler.'
$libraryUpdateCalls = @($libraryUpdateDefinition.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$libraryValidationCall = $libraryUpdateCalls | Where-Object { $_.Operand.Name -eq 'ValidateLibraryUpdate' } | Select-Object -First 1
$libraryAuthorizationCall = $libraryUpdateCalls | Where-Object { $_.Operand.Name -eq 'IsCurrentServerAdmin' } | Select-Object -First 1
Assert-True ($null -ne $libraryValidationCall -and $null -ne $libraryAuthorizationCall -and
    $libraryAuthorizationCall.Offset -lt $libraryValidationCall.Offset) `
    'Late library admin exceptions no longer use the server-current authorization result.'
$libraryExemptionAuditCall = $libraryUpdateCalls | Where-Object { $_.Operand.Name -eq 'RecordAdminExemptions' } | Select-Object -First 1
Assert-True ($null -ne $libraryExemptionAuditCall -and
    $libraryValidationCall.Offset -lt $libraryExemptionAuditCall.Offset -and
    -not (Test-CecilReachable $libraryUpdateDefinition.Body.Instructions[0] $libraryExemptionAuditCall @($libraryValidationCall))) `
    'Late administrator library exemptions can be audited before validation, or no longer reach the shared audit path.'
$libraryDecisionBranch = Get-FirstConditionalBranch $libraryValidationCall $libraryExemptionAuditCall
Assert-True ($null -ne $libraryDecisionBranch -and
    ((Test-CecilReachable $libraryDecisionBranch.Operand $libraryExemptionAuditCall) -xor
     (Test-CecilReachable $libraryDecisionBranch.Next $libraryExemptionAuditCall))) `
    'Both accepted and rejected late-library updates can reach administrator exemption auditing.'
$libraryRequiredGuardCalls = @($libraryUpdateCalls | Where-Object { $_.Operand.Name -in @(
    'TryGetSnapshot', 'get_State', 'get_PeerInfoAuthenticated', 'FixedTimeEquals', 'TryResolveActiveDetectionPeer') })
Assert-True ($libraryRequiredGuardCalls.Count -eq 6) 'Compiled session/authentication/library update guard set changed.'
foreach ($libraryGuardCall in $libraryRequiredGuardCalls) {
    Assert-True (-not (Test-CecilReachable $libraryUpdateDefinition.Body.Instructions[0] $libraryValidationCall @($libraryGuardCall))) `
        "Library policy validation can bypass compiled guard $($libraryGuardCall.Operand.Name)."
    $libraryGuardBranch = Get-FirstConditionalBranch $libraryGuardCall $libraryValidationCall
    Assert-True ($null -ne $libraryGuardBranch) "Library guard $($libraryGuardCall.Operand.Name) has no rejecting branch."
    $libraryGuardBranchesReachingValidation = @((Get-CecilSuccessors $libraryGuardBranch) | Where-Object {
        Test-CecilReachable $_ $libraryValidationCall
    })
    Assert-True ($libraryGuardBranchesReachingValidation.Count -eq 1) `
        "Both outcomes of library guard $($libraryGuardCall.Operand.Name) can validate or the valid path is unreachable."
}

$libraryClientStart = $fixedRuntimeSource.IndexOf('private static void ProcessClientLibraryUpdates(')
$libraryClientEnd = $fixedRuntimeSource.IndexOf('private static void HandleClientManifestAccepted(', $libraryClientStart)
Assert-True ($libraryClientStart -ge 0 -and $libraryClientEnd -gt $libraryClientStart) 'Missing bounded client library-update path.'
$libraryClientSource = $fixedRuntimeSource.Substring($libraryClientStart, $libraryClientEnd - $libraryClientStart)
foreach ($libraryClientGuard in @(
    '!ReferenceEquals(_client, session)', 'session.Failed', '!session.ReadyAcknowledgementSent',
    'session.LibraryKeys.Length == 0', 'session.LibraryBaseline == null',
    'if (now < session.NextLibraryCheckTimestamp) return;',
    'session.NextLibraryCheckTimestamp = now + Stopwatch.Frequency;',
    'session.LibraryBaseline.MatchesCurrentAssemblies()',
    'if (!pending.TryGetResult(out IntegrityManifestBuildResult result)) return;',
    'if (!pending.MatchesCurrentAssemblies())', 'session.NextLibraryUpdateSequence == 0',
    'ProtocolPacketKind.LibraryManifestUpdate, session.NextLibraryUpdateSequence',
    'session.SessionId!, session.Nonce!, encoded.Payload', 'session.NextLibraryUpdateSequence++')) {
    Assert-True ($libraryClientSource.Contains($libraryClientGuard)) "Client late-library check lost $libraryClientGuard."
}
Assert-True ($libraryClientSource.IndexOf('NextLibraryCheckTimestamp = now + Stopwatch.Frequency') -lt
    $libraryClientSource.IndexOf('LibraryBaseline.MatchesCurrentAssemblies()')) 'Loaded assembly polling is no longer paced before discovery.'
foreach ($forbiddenInlineLibraryWork in @('File.ReadAllBytes', 'Directory.GetFiles', 'SHA256.Create', 'ModuleDefinition.ReadModule')) {
    Assert-True (-not $libraryClientSource.Contains($forbiddenInlineLibraryWork)) "Client update performs main-thread $forbiddenInlineLibraryWork."
}

$libraryPinStart = $fixedRuntimeSource.IndexOf('if (decision.Accepted && _libraryChecks.TryGetValue(')
$libraryPinEnd = $fixedRuntimeSource.IndexOf('if (decision.Accepted && pendingManifest != null)', $libraryPinStart)
Assert-True ($libraryPinStart -ge 0 -and $libraryPinEnd -gt $libraryPinStart) 'Library policy pin is not tied to successful admission.'
$libraryPinSource = $fixedRuntimeSource.Substring($libraryPinStart, $libraryPinEnd - $libraryPinStart)
Assert-True ($libraryPinSource.Contains('CaptureLibraryPolicy()') -and
    $libraryPinSource.Contains('libraries.Policy.Rules.Select(rule => rule.PluginGuid)') -and
    $libraryPinSource.Contains('accepted.Rules.Where(rule => requested.Contains(rule.PluginGuid))')) `
    'An admission-time policy reload can add unrequested library identities to the late-update contract.'
Assert-True ([Text.RegularExpressions.Regex]::IsMatch($fixedRuntimeSource,
    'internal void RemovePeer\(ZRpc rpc\)\s*\{[^}]*_libraryChecks\.Remove\(rpc\);') -and
    [Text.RegularExpressions.Regex]::IsMatch($fixedRuntimeSource,
    'internal void Clear\(\)\s*\{[^}]*_libraryChecks\.Clear\(\);')) `
    'Per-connection library validation state survives peer/global cleanup.'
$libraryCleanupStart = $fixedRuntimeSource.IndexOf('private static void CancelClientManifestPreparation(')
$libraryCleanupEnd = $fixedRuntimeSource.IndexOf('private static ServerIntegrityService EnsureIntegrityService()', $libraryCleanupStart)
Assert-True ($libraryCleanupStart -ge 0 -and $libraryCleanupEnd -gt $libraryCleanupStart) 'Missing client manifest cleanup.'
$libraryCleanupSource = $fixedRuntimeSource.Substring($libraryCleanupStart, $libraryCleanupEnd - $libraryCleanupStart)
foreach ($libraryCleanup in @('session.LibraryPreparation?.Dispose();', 'session.LibraryPreparation = null;',
    'session.LibraryBaseline?.Dispose();', 'session.LibraryBaseline = null;', 'session.LibraryKeys = Array.Empty<string>();')) {
    Assert-True ($libraryCleanupSource.Contains($libraryCleanup)) "Library worker lifecycle cleanup lost $libraryCleanup."
}

Write-Output ("Fixed 120-second/30-second timeout wiring and separate 10 MiB server/32 MiB client character bounds, unified responses, server-only snapshot stat evidence, post-ACK guarded stat sanctions/audit, anti-cheat wire-v21, authenticated hot policy updates, generation-bound reports, generic security rejection, gameplay limits, first-join/two-phase final-save gate, report-codec, exact-name/module, assembly, " +
    "admin-entitlement, event-integration, flood-cap, and command-queue smoke tests passed.")
