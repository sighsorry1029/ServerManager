param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"
# Load the hashing cmdlet before loading the game reference assemblies.
Import-Module (Join-Path $PSHOME "Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1") -ErrorAction Stop

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-DiagnosticCode {
    param(
        [object[]]$Diagnostics,
        [string]$Code
    )

    foreach ($diagnostic in $Diagnostics) {
        if ($null -ne $diagnostic -and $diagnostic.Code -eq $Code) {
            return $true
        }
    }

    return $false
}

function Assert-DiagnosticCode {
    param(
        [object[]]$Diagnostics,
        [string]$Code,
        [string]$Message
    )

    Assert-True (Test-DiagnosticCode $Diagnostics $Code) $Message
}

function New-GenericList {
    param(
        [Type]$ElementType,
        [object[]]$Values
    )

    $listType = [Collections.Generic.List``1].MakeGenericType($ElementType)
    $list = [Activator]::CreateInstance($listType)
    $add = $listType.GetMethod("Add")
    foreach ($value in $Values) {
        $add.Invoke($list, [object[]]@($value)) | Out-Null
    }

    return ,$list
}

function Get-HiddenProperty {
    param(
        [object]$Instance,
        [string]$Name
    )

    $property = $Instance.GetType().GetProperty($Name, $script:instanceAll)
    Assert-True ($null -ne $property) `
        "Expected property '$Name' was not found on $($Instance.GetType().FullName)."
    return $property.GetValue($Instance, $null)
}

function Get-ExactConstructor {
    param(
        [Type]$Type,
        [Reflection.BindingFlags]$BindingFlags,
        [Type[]]$ParameterTypes
    )

    foreach ($constructor in $Type.GetConstructors($BindingFlags)) {
        $parameters = @($constructor.GetParameters())
        if ($parameters.Count -ne $ParameterTypes.Count) {
            continue
        }

        $matches = $true
        for ($index = 0; $index -lt $parameters.Count; $index++) {
            if ($parameters[$index].ParameterType -ne $ParameterTypes[$index]) {
                $matches = $false
                break
            }
        }

        if ($matches) {
            return $constructor
        }
    }

    return $null
}

function New-ManifestEntry {
    param(
        [string]$Guid,
        [string]$Name,
        [string]$Hash
    )

    return $script:manifestEntryConstructor.Invoke(
        [object[]]@($Guid, $Name, $Hash))
}

function New-Manifest {
    param([object[]]$Entries = @())

    $entryList = New-GenericList $script:manifestEntryType $Entries
    $arguments = [object[]]::new(1)
    $arguments[0] = $entryList
    return $script:manifestConstructor.Invoke($arguments)
}

function New-PolicyRule {
    param(
        [string]$Guid,
        [string]$Name,
        [string]$Requirement,
        [string[]]$Hashes = @()
    )

    $requirementValue = [Enum]::Parse(
        $script:requirementType,
        $Requirement)
    $arguments = [object[]]::new(4)
    $arguments[0] = $Guid
    $arguments[1] = $Name
    $arguments[2] = $requirementValue
    $arguments[3] = [string[]]$Hashes
    return $script:policyRuleConstructor.Invoke($arguments)
}

function New-Policy {
    param([object[]]$Rules = @())

    $ruleList = New-GenericList $script:policyRuleType $Rules
    $arguments = [object[]]::new(2)
    $arguments[0] = [long]7
    $arguments[1] = $ruleList
    return $script:policyConstructor.Invoke($arguments)
}

function Invoke-Validation {
    param(
        [object]$Policy,
        [object]$Manifest,
        [bool]$AllowAdminExceptions = $false
    )

    if ($PSBoundParameters.ContainsKey("AllowAdminExceptions")) {
        return $script:validateManifestWithAdminExceptions.Invoke(
            $null,
            [object[]]@($Policy, $Manifest, $AllowAdminExceptions))
    }

    return $script:validateManifest.Invoke(
        $null,
        [object[]]@($Policy, $Manifest))
}

function Invoke-DecodedValidation {
    param(
        [object]$Policy,
        [object]$DecodedManifest,
        [bool]$AllowAdminExceptions = $false
    )

    if ($PSBoundParameters.ContainsKey("AllowAdminExceptions")) {
        return $script:validateDecodedWithAdminExceptions.Invoke(
            $null,
            [object[]]@($Policy, $DecodedManifest, $AllowAdminExceptions))
    }

    return $script:validateDecoded.Invoke(
        $null,
        [object[]]@($Policy, $DecodedManifest))
}

function Assert-ValidationResult {
    param(
        [object]$Result,
        [string[]]$RejectingDiagnostics,
        [string[]]$ExemptedDiagnostics,
        [string]$Context
    )

    Assert-True ($Result.Allowed -eq ($RejectingDiagnostics.Count -eq 0)) `
        "$Context returned the wrong admission decision."
    $actualRejecting = @($Result.Diagnostics | ForEach-Object {
        $_.Code + "|" + $_.PluginGuid
    })
    $actualExempted = @($Result.ExemptedDiagnostics | ForEach-Object {
        $_.Code + "|" + $_.PluginGuid
    })
    Assert-True (
        (($actualRejecting | Sort-Object) -join ",") -ceq
        (($RejectingDiagnostics | Sort-Object) -join ",")) `
        "$Context changed the rejecting discrepancy codes or GUIDs."
    Assert-True (
        (($actualExempted | Sort-Object) -join ",") -ceq
        (($ExemptedDiagnostics | Sort-Object) -join ",")) `
        "$Context changed the exempted audit discrepancy codes or GUIDs."
    Assert-True (
        ([Collections.IList]$Result.Diagnostics).IsReadOnly -and
        ([Collections.IList]$Result.ExemptedDiagnostics).IsReadOnly) `
        "$Context exposed mutable validation diagnostics."
}

function Invoke-ServiceAdmission {
    param(
        [object]$Service,
        [byte[]]$Payload,
        [bool]$AllowAuthenticatedAdminReview
    )

    $arguments = [object[]]@($null, $Payload, $AllowAuthenticatedAdminReview, $null)
    $decision = $script:validateForAdmission.Invoke($Service, $arguments)
    return [pscustomobject]@{
        Decision = $decision
        PendingManifest = $arguments[3]
    }
}

function Invoke-ServiceConfirmation {
    param(
        [object]$Service,
        [object]$Manifest,
        [bool]$AuthenticatedAdmin
    )

    $arguments = [object[]]@($null, $Manifest, $AuthenticatedAdmin, $null)
    $decision = $script:confirmAdminManifest.Invoke($Service, $arguments)
    return [pscustomobject]@{
        Decision = $decision
        ExemptedDiagnostics = $arguments[3]
    }
}

function Invoke-ClientRejectionMessage {
    param(
        [object]$Policy,
        [object]$Manifest,
        [object]$Diagnostics
    )

    return $script:buildClientRejectionMessage.Invoke(
        $null,
        [object[]]@($Policy, $Manifest, $Diagnostics))
}

function Invoke-Encode {
    param(
        [object]$Manifest,
        [object]$Limits = $null
    )

    if ($null -eq $Limits) {
        return $script:encodeDefault.Invoke(
            $null,
            [object[]]@($Manifest))
    }

    return $script:encodeWithLimits.Invoke(
        $null,
        [object[]]@($Manifest, $Limits))
}

function Invoke-Decode {
    param(
        [byte[]]$Payload,
        [object]$Limits = $null
    )

    $arguments = [object[]]::new($(if ($null -eq $Limits) { 1 } else { 2 }))
    $arguments[0] = $Payload
    if ($null -ne $Limits) {
        $arguments[1] = $Limits
        return $script:decodeWithLimits.Invoke($null, $arguments)
    }

    return $script:decodeDefault.Invoke($null, $arguments)
}

function New-Limits {
    param(
        [int]$MaxPayloadBytes = 262144,
        [int]$MaxPluginCount = 1024,
        [int]$MaxGuidUtf8Bytes = 256,
        [int]$MaxNameUtf8Bytes = 512
    )

    return $script:limitsConstructor.Invoke(
        [object[]]@(
            $MaxPayloadBytes,
            $MaxPluginCount,
            $MaxGuidUtf8Bytes,
            $MaxNameUtf8Bytes))
}

function Join-DuplicateEntryPayload {
    param([byte[]]$OneEntryPayload)

    $entryLength = $OneEntryPayload.Length - 12
    $payload = [byte[]]::new(12 + (2 * $entryLength))
    [Buffer]::BlockCopy($OneEntryPayload, 0, $payload, 0, 12)
    [Buffer]::BlockCopy([BitConverter]::GetBytes(2), 0, $payload, 8, 4)
    [Buffer]::BlockCopy(
        $OneEntryPayload,
        12,
        $payload,
        12,
        $entryLength)
    [Buffer]::BlockCopy(
        $OneEntryPayload,
        12,
        $payload,
        12 + $entryLength,
        $entryLength)
    return ,$payload
}

function New-ReferenceScanner {
    param(
        [string]$Root,
        [object]$Limits
    )

    return $script:scannerConstructor.Invoke(
        [object[]]@($Root, $Limits))
}

function Invoke-ReferenceScan {
    param([object]$Scanner)

    $script:scannerEnsureDirectories.Invoke($Scanner, [object[]]@()) | Out-Null
    return $script:scannerScan.Invoke($Scanner, [object[]]@([Threading.CancellationToken]::None))
}

function New-ManagedReferenceFixture {
    param(
        [string]$Path,
        [string]$AssemblyName,
        [string]$Version = "1.0.0.0",
        [string]$PluginGuid = ""
    )

    $identity = [Mono.Cecil.AssemblyNameDefinition]::new($AssemblyName, [Version]$Version)
    $assembly = [Mono.Cecil.AssemblyDefinition]::CreateAssembly(
        $identity, ($AssemblyName + ".dll"), [Mono.Cecil.ModuleKind]::Dll)
    try {
        if (-not [string]::IsNullOrEmpty($PluginGuid)) {
            $module = $assembly.MainModule
            $type = [Mono.Cecil.TypeDefinition]::new(
                "IntegrityFixtures", "Plugin", [Mono.Cecil.TypeAttributes]::Public, $module.TypeSystem.Object)
            $module.Types.Add($type)
            $constructor = [BepInEx.BepInPlugin].GetConstructor([Type[]]@([string], [string], [string]))
            $attribute = [Mono.Cecil.CustomAttribute]::new($module.ImportReference($constructor))
            foreach ($value in @($PluginGuid, "Reserved GUID fixture", "1.0.0")) {
                $attribute.ConstructorArguments.Add(
                    [Mono.Cecil.CustomAttributeArgument]::new($module.TypeSystem.String, [string]$value))
            }
            $type.CustomAttributes.Add($attribute)
        }
        $assembly.Write($Path)
    }
    finally { $assembly.Dispose() }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"
$bepInExPath = Join-Path $GamePath "BepInEx\core\BepInEx.dll"
Assert-True (Test-Path -LiteralPath $pluginPath) `
    "Build ServerManager before running this smoke test."
Assert-True (Test-Path -LiteralPath $cecilPath) `
    "Mono.Cecil.dll was not found under the configured Valheim game path."
Assert-True (Test-Path -LiteralPath $bepInExPath) `
    "BepInEx.dll was not found under the configured Valheim game path."

# Reference-folder scanning executes Cecil code and reads BepInPlugin metadata.
# Load those exact build-time dependencies before loading the merged plugin.
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($bepInExPath)) | Out-Null
[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
$plugin = [Reflection.Assembly]::LoadFrom($pluginPath)

$instanceAll = [Reflection.BindingFlags]::Instance -bor
    [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic
$staticAll = [Reflection.BindingFlags]::Static -bor
    [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic

$requirementType = $plugin.GetType("ServerManager.IntegrityRequirement", $true)
$limitsType = $plugin.GetType("ServerManager.IntegrityLimits", $true)
$manifestEntryType = $plugin.GetType(
    "ServerManager.IntegrityManifestEntry",
    $true)
$manifestType = $plugin.GetType("ServerManager.IntegrityManifest", $true)
$decodedManifestType = $plugin.GetType(
    "ServerManager.IntegrityManifestDecodeResult",
    $true)
$diagnosticType = $plugin.GetType("ServerManager.IntegrityDiagnostic", $true)
$policyRuleType = $plugin.GetType("ServerManager.IntegrityPolicyRule", $true)
$policyType = $plugin.GetType("ServerManager.IntegrityPolicySnapshot", $true)
$validatorType = $plugin.GetType("ServerManager.IntegrityValidator", $true)
$codecType = $plugin.GetType("ServerManager.IntegrityManifestCodec", $true)
$policyStoreType = $plugin.GetType("ServerManager.IntegrityPolicyStore", $true)
$scannerType = $plugin.GetType(
    "ServerManager.ReferencePluginPolicyScanner",
    $true)
$integrityServiceType = $plugin.GetType(
    "ServerManager.ServerIntegrityService",
    $true)

$manifestEntryEnumerableType =
    [Collections.Generic.IEnumerable``1].MakeGenericType($manifestEntryType)
$policyRuleEnumerableType =
    [Collections.Generic.IEnumerable``1].MakeGenericType($policyRuleType)
$stringEnumerableType =
    [Collections.Generic.IEnumerable``1].MakeGenericType([string])

$limitsConstructor = Get-ExactConstructor `
    -Type $limitsType `
    -BindingFlags $instanceAll `
    -ParameterTypes ([Type[]]@(
        [int], [int], [int], [int]))
$manifestEntryConstructor = Get-ExactConstructor `
    -Type $manifestEntryType `
    -BindingFlags $instanceAll `
    -ParameterTypes ([Type[]]@(
        [string], [string], [string]))
$manifestConstructor = Get-ExactConstructor `
    -Type $manifestType `
    -BindingFlags $instanceAll `
    -ParameterTypes ([Type[]]@($manifestEntryEnumerableType))
$diagnosticConstructor = Get-ExactConstructor `
    -Type $diagnosticType `
    -BindingFlags $instanceAll `
    -ParameterTypes ([Type[]]@([string], [string], [string]))
$policyRuleConstructor = Get-ExactConstructor `
    -Type $policyRuleType `
    -BindingFlags $instanceAll `
    -ParameterTypes ([Type[]]@(
        [string],
        [string],
        $requirementType,
        $stringEnumerableType))
$policyConstructor = Get-ExactConstructor `
    -Type $policyType `
    -BindingFlags $instanceAll `
    -ParameterTypes ([Type[]]@(
        [long],
        $policyRuleEnumerableType))
$validateManifest = $validatorType.GetMethods($staticAll) |
    Where-Object {
        $_.Name -eq "Validate" -and
        $_.GetParameters().Count -eq 2 -and
        $_.GetParameters()[1].ParameterType -eq $manifestType
    } |
    Select-Object -First 1
$validateManifestWithAdminExceptions = $validatorType.GetMethods($staticAll) |
    Where-Object {
        $_.Name -eq "Validate" -and
        $_.GetParameters().Count -eq 3 -and
        $_.GetParameters()[1].ParameterType -eq $manifestType -and
        $_.GetParameters()[2].ParameterType -eq [bool]
    } |
    Select-Object -First 1
$validateDecoded = $validatorType.GetMethods($staticAll) |
    Where-Object {
        $_.Name -eq "Validate" -and
        $_.GetParameters().Count -eq 2 -and
        $_.GetParameters()[1].ParameterType -eq $decodedManifestType
    } |
    Select-Object -First 1
$validateDecodedWithAdminExceptions = $validatorType.GetMethods($staticAll) |
    Where-Object {
        $_.Name -eq "Validate" -and
        $_.GetParameters().Count -eq 3 -and
        $_.GetParameters()[1].ParameterType -eq $decodedManifestType -and
        $_.GetParameters()[2].ParameterType -eq [bool]
    } |
    Select-Object -First 1
$encodeDefault = $codecType.GetMethods($staticAll) |
    Where-Object { $_.Name -eq "TryEncode" -and $_.GetParameters().Count -eq 1 } |
    Select-Object -First 1
$encodeWithLimits = $codecType.GetMethods($staticAll) |
    Where-Object { $_.Name -eq "TryEncode" -and $_.GetParameters().Count -eq 2 } |
    Select-Object -First 1
$decodeDefault = $codecType.GetMethods($staticAll) |
    Where-Object { $_.Name -eq "TryDecode" -and $_.GetParameters().Count -eq 1 } |
    Select-Object -First 1
$decodeWithLimits = $codecType.GetMethods($staticAll) |
    Where-Object { $_.Name -eq "TryDecode" -and $_.GetParameters().Count -eq 2 } |
    Select-Object -First 1
$policyStoreConstructor = Get-ExactConstructor `
    -Type $policyStoreType `
    -BindingFlags $instanceAll `
    -ParameterTypes ([Type[]]@([string], $limitsType))
$policyStoreWithSelfConstructor = Get-ExactConstructor `
    -Type $policyStoreType `
    -BindingFlags $instanceAll `
    -ParameterTypes ([Type[]]@(
        [string],
        $limitsType,
        $scannerType,
        $manifestEntryType))
$scannerConstructor = Get-ExactConstructor `
    -Type $scannerType `
    -BindingFlags $instanceAll `
    -ParameterTypes ([Type[]]@([string], $limitsType))
$scannerEnsureDirectories = $scannerType.GetMethod(
    "EnsureDirectories",
    $instanceAll)
$scannerScan = $scannerType.GetMethod("Scan", $instanceAll)
$buildClientRejectionMessage = $integrityServiceType.GetMethod(
    "BuildClientRejectionMessage",
    $staticAll)
$validateForAdmission = $integrityServiceType.GetMethod(
    "ValidateForAdmission",
    $instanceAll)
$confirmAdminManifest = $integrityServiceType.GetMethod(
    "ConfirmAdminManifest",
    $instanceAll)
$validateService = $integrityServiceType.GetMethod("Validate", $instanceAll)
$isRelevantPolicyPath = $integrityServiceType.GetMethods($staticAll) |
    Where-Object {
        $_.Name -eq "IsRelevantPolicyPath" -and
        $_.GetParameters().Count -eq 3
    } |
    Select-Object -First 1
$cecilAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
$pluginDefinition = $cecilAssembly.MainModule.GetType(
    "ServerManager.ServerManagerPlugin")
$integrityServiceDefinition = $cecilAssembly.MainModule.GetType(
    "ServerManager.ServerIntegrityService")
$defaultEnforceModPolicyField = @($pluginDefinition.Fields) |
    Where-Object Name -eq "DefaultEnforceModPolicy" |
    Select-Object -First 1
$policyWatcherNotifyFiltersField = @($integrityServiceDefinition.Fields) |
    Where-Object Name -eq "PolicyWatcherNotifyFilters" |
    Select-Object -First 1
$manifestWireSchemaField = $codecType.GetField(
    "WireSchemaVersion",
    $staticAll)

Assert-True (
    $null -ne $limitsConstructor -and
    $null -ne $manifestEntryConstructor -and
    $null -ne $manifestConstructor -and
    $null -ne $diagnosticConstructor -and
    $null -ne $policyRuleConstructor -and
    $null -ne $policyConstructor -and
    $null -ne $validateManifest -and
    $null -ne $validateManifestWithAdminExceptions -and
    $null -ne $validateDecoded -and
    $null -ne $validateDecodedWithAdminExceptions -and
    $null -ne $encodeDefault -and
    $null -ne $encodeWithLimits -and
    $null -ne $decodeDefault -and
    $null -ne $decodeWithLimits -and
    $null -ne $policyStoreConstructor -and
    $null -ne $policyStoreWithSelfConstructor -and
    $null -ne $scannerConstructor -and
    $null -ne $scannerEnsureDirectories -and
    $null -ne $scannerScan -and
    $null -ne $buildClientRejectionMessage -and
    $null -ne $validateForAdmission -and
    $null -ne $confirmAdminManifest -and
    $null -ne $validateService -and
    $null -ne $isRelevantPolicyPath -and
    $null -ne $defaultEnforceModPolicyField -and
    $null -ne $policyWatcherNotifyFiltersField -and
    $null -ne $manifestWireSchemaField) `
    "One or more Integrity characterization seams are missing."

Assert-True (
    $manifestWireSchemaField.IsLiteral -and
    $manifestWireSchemaField.GetRawConstantValue() -eq 2) `
    "The version-free exact-hash manifest is not protected by schema 2."

Assert-True (
    $defaultEnforceModPolicyField.IsLiteral -and
    $defaultEnforceModPolicyField.HasConstant -and
    [bool]$defaultEnforceModPolicyField.Constant) `
    "Mandatory mod-policy enforcement is no longer a stable true constant."
Assert-True (@($pluginDefinition.Properties |
    Where-Object Name -eq "EnforceModPolicy").Count -eq 0) `
    "Mandatory mod-policy enforcement retained a configurable bypass."
$validateServiceDefinition = $integrityServiceDefinition.Methods |
    Where-Object Name -eq "Validate" |
    Select-Object -First 1
$validateFacadeCalls = @($validateServiceDefinition.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$admissionFacadeCall = @($validateFacadeCalls | Where-Object {
    $_.Operand.DeclaringType.FullName -eq "ServerManager.ServerIntegrityService" -and
    $_.Operand.Name -eq "ValidateForAdmission"
})
Assert-True (
    $admissionFacadeCall.Count -eq 1 -and
    $admissionFacadeCall[0].Previous.Previous.OpCode.Code.ToString() -eq "Ldc_I4_0" -and
    @($validateFacadeCalls | Where-Object {
        $_.Operand.DeclaringType.FullName -eq "ServerManager.ManifestValidationDecision" -and
        $_.Operand.Name -eq "Accept"
    }).Count -eq 0) `
    "The compatibility manifest validator must delegate with administrator exceptions disabled."
$admissionServiceDefinition = $integrityServiceDefinition.Methods |
    Where-Object Name -eq "ValidateForAdmission" |
    Select-Object -First 1
Assert-True ($null -ne $admissionServiceDefinition) `
    "The provisional administrator admission validator is missing."
$validateServiceCalls = @($admissionServiceDefinition.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
$strictValidationCall = $validateServiceCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq "ServerManager.IntegrityValidator" -and
        $_.Operand.Name -eq "Validate" -and
        $_.Operand.Parameters.Count -eq 2
    } |
    Select-Object -First 1
$adminValidationCall = $validateServiceCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq "ServerManager.IntegrityValidator" -and
        $_.Operand.Name -eq "Validate" -and
        $_.Operand.Parameters.Count -eq 3
    } |
    Select-Object -First 1
$createValidationDecisionCall = $validateServiceCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq "ServerManager.ServerIntegrityService" -and
        $_.Operand.Name -eq "CreateValidationDecision"
    } |
    Select-Object -First 1
$acceptManifestCalls = @($validateServiceCalls |
    Where-Object {
        $_.Operand.DeclaringType.FullName -eq "ServerManager.ManifestValidationDecision" -and
        $_.Operand.Name -eq "Accept"
    })
Assert-True (
    $null -ne $strictValidationCall -and
    $null -ne $adminValidationCall -and
    $null -ne $createValidationDecisionCall -and
    $acceptManifestCalls.Count -eq 1 -and
    $strictValidationCall.Offset -lt $adminValidationCall.Offset -and
    $adminValidationCall.Offset -lt $acceptManifestCalls[0].Offset -and
    $strictValidationCall.Offset -lt $createValidationDecisionCall.Offset -and
    @($validateServiceCalls |
        Where-Object { $_.Operand.Name -eq "get_EnforceModPolicy" }).Count -eq 0) `
    "Manifest admission must run strict validation before bounded authenticated-admin review or final decision."

$policyWatcherNotifyFilters = [int]$policyWatcherNotifyFiltersField.Constant
$directoryNameNotifyFilter = [int][IO.NotifyFilters]::DirectoryName
Assert-True (
    $policyWatcherNotifyFiltersField.IsLiteral -and
    $policyWatcherNotifyFiltersField.HasConstant -and
    (($policyWatcherNotifyFilters -band $directoryNameNotifyFilter) -eq
        $directoryNameNotifyFilter)) `
    "The policy watcher no longer observes required/optional directory changes."

$allowedHash = "a" * 64
$otherHash = "b" * 64
$requiredGuid = "example.required"
$optionalGuid = "example.optional"
$unlistedGuid = "example.unlisted"
$requiredEntry = New-ManifestEntry `
    $requiredGuid "Required Plugin" $allowedHash
$optionalEntry = New-ManifestEntry `
    $optionalGuid "Optional Plugin" $allowedHash
$unlistedEntry = New-ManifestEntry `
    $unlistedGuid "Unlisted Plugin" $otherHash
$wrongHashEntry = New-ManifestEntry `
    $requiredGuid "Required Plugin" $otherHash
$optionalWrongHashEntry = New-ManifestEntry `
    $optionalGuid "Client Supplied Optional Name" $otherHash

$requiredRule = New-PolicyRule `
    $requiredGuid "Required Plugin" "Required" @($allowedHash)
$optionalRule = New-PolicyRule `
    $optionalGuid "Optional Plugin" "Optional" @($allowedHash)
$strictPolicy = New-Policy @($requiredRule, $optionalRule)

$matching = Invoke-Validation `
    $strictPolicy (New-Manifest @($requiredEntry, $optionalEntry))
Assert-True $matching.Allowed `
    "A matching required and optional manifest was rejected."

$requiredMissing = Invoke-Validation $strictPolicy (New-Manifest @())
Assert-True (-not $requiredMissing.Allowed) `
    "A missing required plugin was accepted."
Assert-DiagnosticCode `
    @($requiredMissing.Diagnostics) `
    "validation.required_plugin_missing" `
    "The missing-required diagnostic code changed."

$optionalMissing = Invoke-Validation `
    $strictPolicy (New-Manifest @($requiredEntry))
Assert-True $optionalMissing.Allowed `
    "A missing optional plugin was rejected."

$unlistedPresent = Invoke-Validation `
    $strictPolicy (New-Manifest @($requiredEntry, $unlistedEntry))
Assert-True (-not $unlistedPresent.Allowed) `
    "The strict folder policy accepted an unlisted plugin."
Assert-DiagnosticCode `
    @($unlistedPresent.Diagnostics) `
    "validation.unlisted_plugin_present" `
    "The unlisted-plugin diagnostic code changed."
$hashMismatch = Invoke-Validation `
    $strictPolicy (New-Manifest @($wrongHashEntry))
Assert-True (-not $hashMismatch.Allowed) `
    "A required plugin with an unlisted SHA-256 was accepted."
Assert-DiagnosticCode `
    @($hashMismatch.Diagnostics) `
    "validation.hash_not_allowed" `
    "The hash-mismatch diagnostic code changed."

$missingRequiredDiagnostic = "validation.required_plugin_missing|" + $requiredGuid
$wrongRequiredDiagnostic = "validation.hash_not_allowed|" + $requiredGuid
$wrongOptionalDiagnostic = "validation.hash_not_allowed|" + $optionalGuid
$unlistedDiagnostic = "validation.unlisted_plugin_present|" + $unlistedGuid
$adminPolicyCases = @(
    @{
        Name = "matching required and optional"
        Entries = @($requiredEntry, $optionalEntry)
        Rejecting = @()
        Exemptible = @()
    },
    @{
        Name = "optional absent"
        Entries = @($requiredEntry)
        Rejecting = @()
        Exemptible = @()
    },
    @{
        Name = "required absent"
        Entries = @($optionalEntry)
        Rejecting = @($missingRequiredDiagnostic)
        Exemptible = @()
    },
    @{
        Name = "required wrong hash"
        Entries = @($wrongHashEntry, $optionalEntry)
        Rejecting = @($wrongRequiredDiagnostic)
        Exemptible = @()
    },
    @{
        Name = "optional wrong hash"
        Entries = @($requiredEntry, $optionalWrongHashEntry)
        Rejecting = @()
        Exemptible = @($wrongOptionalDiagnostic)
    },
    @{
        Name = "additional plugin"
        Entries = @($requiredEntry, $unlistedEntry)
        Rejecting = @()
        Exemptible = @($unlistedDiagnostic)
    },
    @{
        Name = "both administrator exceptions"
        Entries = @($requiredEntry, $optionalWrongHashEntry, $unlistedEntry)
        Rejecting = @()
        Exemptible = @($wrongOptionalDiagnostic, $unlistedDiagnostic)
    },
    @{
        Name = "required absent despite administrator exceptions"
        Entries = @($optionalWrongHashEntry, $unlistedEntry)
        Rejecting = @($missingRequiredDiagnostic)
        Exemptible = @($wrongOptionalDiagnostic, $unlistedDiagnostic)
    },
    @{
        Name = "required wrong hash despite administrator exceptions"
        Entries = @($wrongHashEntry, $optionalWrongHashEntry, $unlistedEntry)
        Rejecting = @($wrongRequiredDiagnostic)
        Exemptible = @($wrongOptionalDiagnostic, $unlistedDiagnostic)
    }
)
foreach ($policyCase in $adminPolicyCases) {
    $caseManifest = New-Manifest $policyCase.Entries
    $caseEncoded = Invoke-Encode $caseManifest
    Assert-True $caseEncoded.Success "The administrator matrix fixture did not encode."
    $caseDecoded = Invoke-Decode ([byte[]]$caseEncoded.Payload)
    Assert-True $caseDecoded.Success "The administrator matrix fixture did not decode."
    $strictDiagnostics = @($policyCase.Rejecting) + @($policyCase.Exemptible)
    Assert-ValidationResult `
        (Invoke-Validation $strictPolicy $caseManifest) `
        $strictDiagnostics @() ("Strict default: " + $policyCase.Name)
    Assert-ValidationResult `
        (Invoke-DecodedValidation $strictPolicy $caseDecoded) `
        $strictDiagnostics @() ("Strict decoded default: " + $policyCase.Name)
    foreach ($allowAdminExceptions in @($false, $true)) {
        $expectedRejecting = $strictDiagnostics
        $expectedExempted = @()
        if ($allowAdminExceptions) {
            $expectedRejecting = @($policyCase.Rejecting)
            $expectedExempted = @($policyCase.Exemptible)
        }

        $context = $policyCase.Name + "; administrator=" + $allowAdminExceptions
        Assert-ValidationResult `
            (Invoke-Validation $strictPolicy $caseManifest $allowAdminExceptions) `
            $expectedRejecting $expectedExempted $context
        Assert-ValidationResult `
            (Invoke-DecodedValidation $strictPolicy $caseDecoded $allowAdminExceptions) `
            $expectedRejecting $expectedExempted ("Decoded: " + $context)
    }
}

foreach ($allowAdminExceptions in @($false, $true)) {
    Assert-ValidationResult `
        (Invoke-Validation $null (New-Manifest @($requiredEntry)) $allowAdminExceptions) `
        @("policy.unavailable|") @() "Unavailable policy"
    Assert-ValidationResult `
        (Invoke-Validation $strictPolicy $null $allowAdminExceptions) `
        @("manifest.unavailable|") @() "Unavailable manifest"
    Assert-ValidationResult `
        (Invoke-DecodedValidation $strictPolicy $null $allowAdminExceptions) `
        @("manifest.unavailable|") @() "Unavailable decoded manifest"
}

$missingGuid = "example.missing"
$missingRule = New-PolicyRule `
    $missingGuid "Missing Required" "Required" @($allowedHash)
$actionablePolicy = New-Policy @($missingRule, $optionalRule)
$markupUnlistedEntry = New-ManifestEntry `
    $unlistedGuid "<size=200>Fake</size>" $otherHash
$actionableManifest = New-Manifest `
    @($optionalWrongHashEntry, $markupUnlistedEntry)
$actionableValidation = Invoke-Validation $actionablePolicy $actionableManifest
$actionableMessage = Invoke-ClientRejectionMessage `
    $actionablePolicy `
    $actionableManifest `
    $actionableValidation.Diagnostics

Assert-True ($actionableMessage.Contains("Install required: Missing Required.")) `
    "The client rejection omitted the required plugin install action."
Assert-True ($actionableMessage.Contains("_size_200_Fake__size_")) `
    "The client rejection did not retain a safe, recognizable unlisted plugin name."
Assert-True ($actionableMessage.Contains("Update or reinstall: Optional Plugin.")) `
    "The client rejection omitted the hash-mismatch repair action."
Assert-True (-not $actionableMessage.Contains("<")) `
    "The client rejection retained attacker-controlled rich-text markup."
Assert-True (-not $actionableMessage.Contains(">")) `
    "The client rejection retained attacker-controlled rich-text markup."
Assert-True (-not $actionableMessage.Contains($allowedHash)) `
    "The client rejection exposed an expected server SHA-256."
Assert-True (-not $actionableMessage.Contains($otherHash)) `
    "The client rejection exposed the reported client SHA-256."
Assert-True (-not $actionableMessage.Contains($missingGuid)) `
    "The client rejection exposed a GUID when a policy display name was available."
Assert-True (
    [Text.Encoding]::UTF8.GetByteCount($actionableMessage) -le 480) `
    "The actionable client rejection exceeded its UTF-8 byte bound."

$adminActionableValidation = Invoke-Validation `
    $actionablePolicy $actionableManifest $true
$adminActionableMessage = Invoke-ClientRejectionMessage `
    $actionablePolicy `
    $actionableManifest `
    $adminActionableValidation.Diagnostics
Assert-True (
    -not $adminActionableValidation.Allowed -and
    $adminActionableValidation.Diagnostics.Count -eq 1 -and
    $adminActionableValidation.ExemptedDiagnostics.Count -eq 2 -and
    $adminActionableMessage.Contains("Install required: Missing Required.") -and
    -not $adminActionableMessage.Contains("Remove not allowed:") -and
    -not $adminActionableMessage.Contains("Update or reinstall:")) `
    "An administrator rejection mixed waived optional/additional mods with the required repair."

$manyUnlistedEntries = @()
for ($index = 1; $index -le 5; $index++) {
    $manyUnlistedEntries += New-ManifestEntry `
        ("example.extra" + $index) `
        ("Extra Plugin " + $index) `
        $otherHash
}
$manyUnlistedManifest = New-Manifest $manyUnlistedEntries
$manyUnlistedValidation = Invoke-Validation $strictPolicy $manyUnlistedManifest
$manyUnlistedMessage = Invoke-ClientRejectionMessage `
    $strictPolicy `
    $manyUnlistedManifest `
    $manyUnlistedValidation.Diagnostics
Assert-True ($manyUnlistedMessage.Contains("(+3 more)")) `
    "The client rejection did not summarize excess plugin names."
Assert-True (
    [Text.Encoding]::UTF8.GetByteCount($manyUnlistedMessage) -le 480) `
    "A summarized client rejection exceeded its UTF-8 byte bound."

$privateDiagnostic = $diagnosticConstructor.Invoke(
    [object[]]@(
        "wire.malformed",
        ("C:\server\private\required\Secret.dll expected " + $allowedHash),
        ""))
$privateDiagnostics = New-GenericList $diagnosticType @($privateDiagnostic)
$fallbackMessage = Invoke-ClientRejectionMessage `
    $strictPolicy `
    $null `
    $privateDiagnostics
Assert-True ($fallbackMessage.Contains("Reference: wire.malformed.")) `
    "The generic rejection omitted its stable troubleshooting code."
Assert-True (-not $fallbackMessage.Contains("C:\server")) `
    "The generic rejection copied a diagnostic filesystem path."
Assert-True (-not $fallbackMessage.Contains("private")) `
    "The generic rejection copied a diagnostic secret."
Assert-True (-not $fallbackMessage.Contains($allowedHash)) `
    "The generic rejection copied an expected server SHA-256."
Assert-True (
    [Text.Encoding]::UTF8.GetByteCount($fallbackMessage) -le 480) `
    "The generic client rejection exceeded its UTF-8 byte bound."

$unnamedGuid = "private.internal.plugin.guid"
$unnamedDiagnostic = $diagnosticConstructor.Invoke(
    [object[]]@(
        "validation.required_plugin_missing",
        "Internal diagnostic text must remain server-only.",
        $unnamedGuid))
$unnamedDiagnostics = New-GenericList $diagnosticType @($unnamedDiagnostic)
$unnamedMessage = Invoke-ClientRejectionMessage `
    $strictPolicy `
    $null `
    $unnamedDiagnostics
Assert-True ($unnamedMessage.Contains("Install required: unknown plugin.")) `
    "A missing display name did not use the safe client fallback."
Assert-True (-not $unnamedMessage.Contains($unnamedGuid)) `
    "The safe client fallback exposed an internal plugin GUID."

$duplicateHashDiagnostics = New-GenericList `
    $diagnosticType `
    @($hashMismatch.Diagnostics[0], $hashMismatch.Diagnostics[0])
$duplicateHashMessage = Invoke-ClientRejectionMessage `
    $strictPolicy `
    (New-Manifest @($wrongHashEntry)) `
    $duplicateHashDiagnostics
$duplicateNameMatches = [Text.RegularExpressions.Regex]::Matches(
    $duplicateHashMessage,
    [Text.RegularExpressions.Regex]::Escape("Required Plugin"))
Assert-True ($duplicateNameMatches.Count -eq 1) `
    "Duplicate diagnostics for one GUID repeated the plugin name in one action."

$koreanRequiredPrefix = -join @(
    [char]0xD544, [char]0xC218, [char]0xBAA8, [char]0xB4DC)
$koreanRemovePrefix = -join @(
    [char]0xAE08, [char]0xC9C0, [char]0xBAA8, [char]0xB4DC)
$koreanUpdatePrefix = -join @(
    [char]0xC5C5, [char]0xB370, [char]0xC774, [char]0xD2B8,
    [char]0xBAA8, [char]0xB4DC)
$longKoreanRequiredName =
    $koreanRequiredPrefix + (([string][char]0xAC00) * 140)
$longKoreanRemoveName =
    $koreanRemovePrefix + (([string][char]0xB098) * 140)
$longKoreanUpdateName =
    $koreanUpdatePrefix + (([string][char]0xB2E4) * 138)
$multiByteRules = @(
    (New-PolicyRule "example.kr.required1" $longKoreanRequiredName "Required" @($allowedHash)),
    (New-PolicyRule "example.kr.required2" $longKoreanRequiredName "Required" @($allowedHash)),
    (New-PolicyRule "example.kr.update1" $longKoreanUpdateName "Optional" @($allowedHash)),
    (New-PolicyRule "example.kr.update2" $longKoreanUpdateName "Optional" @($allowedHash))
)
$multiBytePolicy = New-Policy $multiByteRules
$multiByteManifest = New-Manifest @(
    (New-ManifestEntry "example.kr.unlisted1" $longKoreanRemoveName $otherHash),
    (New-ManifestEntry "example.kr.unlisted2" $longKoreanRemoveName $otherHash),
    (New-ManifestEntry "example.kr.update1" "Client Update 1" $otherHash),
    (New-ManifestEntry "example.kr.update2" "Client Update 2" $otherHash)
)
$multiByteValidation = Invoke-Validation $multiBytePolicy $multiByteManifest
$multiByteMessage = Invoke-ClientRejectionMessage `
    $multiBytePolicy `
    $multiByteManifest `
    $multiByteValidation.Diagnostics
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$multiByteWireBytes = $strictUtf8.GetBytes($multiByteMessage)
Assert-True ($multiByteWireBytes.Length -le 480) `
    "A multi-byte client rejection exceeded its 480-byte UTF-8 bound."
Assert-True (
    $strictUtf8.GetString($multiByteWireBytes) -ceq $multiByteMessage) `
    "A multi-byte client rejection was not valid round-trip UTF-8."
Assert-True ($multiByteMessage.Contains(
    "Install required: " + $koreanRequiredPrefix)) `
    "The bounded rejection lost its Korean required-plugin label."
Assert-True ($multiByteMessage.Contains(
    "Remove not allowed: " + $koreanRemovePrefix)) `
    "The bounded rejection lost its Korean unlisted-plugin label."
Assert-True ($multiByteMessage.Contains(
    "Update or reinstall: " + $koreanUpdatePrefix)) `
    "The bounded rejection lost its Korean hash-mismatch label."
Assert-True ($multiByteMessage.EndsWith(
    "Restart Valheim after changing mods, then try again.")) `
    "The bounded multi-byte rejection truncated its recovery instruction."

$encoded = Invoke-Encode (New-Manifest @($requiredEntry))
Assert-True $encoded.Success "A valid manifest did not encode."
$validPayload = [byte[]]$encoded.Payload
$decoded = Invoke-Decode $validPayload
Assert-True $decoded.Success "A valid canonical manifest did not decode."
Assert-True ($decoded.Manifest.Entries.Count -eq 1) `
    "The decoded manifest entry count changed."

$invalidMagic = [byte[]]$validPayload.Clone()
$invalidMagic[0] = 0
$invalidMagicResult = Invoke-Decode $invalidMagic
Assert-DiagnosticCode `
    @($invalidMagicResult.Diagnostics) `
    "wire.invalid_magic" `
    "An invalid wire marker was not rejected deterministically."

$truncated = [byte[]]$validPayload[0..($validPayload.Length - 2)]
$truncatedResult = Invoke-Decode $truncated
Assert-DiagnosticCode `
    @($truncatedResult.Diagnostics) `
    "wire.malformed" `
    "A truncated manifest was not classified as malformed."

$trailing = [byte[]]::new($validPayload.Length + 1)
[Buffer]::BlockCopy(
    $validPayload,
    0,
    $trailing,
    0,
    $validPayload.Length)
$trailing[$trailing.Length - 1] = 127
$trailingResult = Invoke-Decode $trailing
Assert-DiagnosticCode `
    @($trailingResult.Diagnostics) `
    "wire.trailing_data" `
    "Trailing wire data was not rejected."

$duplicatePayload = Join-DuplicateEntryPayload $validPayload
$duplicateResult = Invoke-Decode $duplicatePayload
Assert-DiagnosticCode `
    @($duplicateResult.Diagnostics) `
    "manifest.duplicate_guid" `
    "Duplicate manifest GUIDs were not rejected."

$smallPayloadLimits = New-Limits -MaxPayloadBytes 64
$payloadBoundsResult = Invoke-Decode $validPayload $smallPayloadLimits
Assert-DiagnosticCode `
    @($payloadBoundsResult.Diagnostics) `
    "wire.payload_too_large" `
    "The manifest payload byte limit was not enforced."

$onePluginLimits = New-Limits -MaxPluginCount 1
$countBoundsResult = Invoke-Decode $duplicatePayload $onePluginLimits
Assert-DiagnosticCode `
    @($countBoundsResult.Diagnostics) `
    "manifest.too_many_entries" `
    "The manifest plugin-count limit was not enforced before decoding entries."

$unsupportedSchema = [byte[]]$validPayload.Clone()
[Buffer]::BlockCopy([BitConverter]::GetBytes(1), 0, $unsupportedSchema, 4, 4)
$unsupportedSchemaResult = Invoke-Decode $unsupportedSchema
Assert-DiagnosticCode `
    @($unsupportedSchemaResult.Diagnostics) `
    "wire.unsupported_schema" `
    "An unsupported manifest schema was not rejected."

$nonCanonicalGuid = [byte[]]$validPayload.Clone()
$nonCanonicalGuid[16] = [byte][char]'E'
$nonCanonicalGuidResult = Invoke-Decode $nonCanonicalGuid
Assert-DiagnosticCode `
    @($nonCanonicalGuidResult.Diagnostics) `
    "wire.non_canonical" `
    "A noncanonical manifest GUID was not rejected."

$invalidUtf8Guid = [byte[]]$validPayload.Clone()
$invalidUtf8Guid[16] = 255
$invalidUtf8GuidResult = Invoke-Decode $invalidUtf8Guid
Assert-DiagnosticCode `
    @($invalidUtf8GuidResult.Diagnostics) `
    "wire.malformed" `
    "Invalid UTF-8 in a manifest GUID was not rejected."

$smallGuidLimits = New-Limits -MaxGuidUtf8Bytes 1
$guidBoundsResult = Invoke-Decode $validPayload $smallGuidLimits
Assert-DiagnosticCode `
    @($guidBoundsResult.Diagnostics) `
    "wire.malformed" `
    "The manifest GUID byte limit was not enforced."
$smallNameLimits = New-Limits -MaxNameUtf8Bytes 1
$nameBoundsResult = Invoke-Decode $validPayload $smallNameLimits
Assert-DiagnosticCode `
    @($nameBoundsResult.Diagnostics) `
    "wire.malformed" `
    "The manifest display-name byte limit was not enforced."

$failedDecodeCases = @(
    (Invoke-Decode $null),
    (Invoke-Decode ([byte[]]@())),
    $invalidMagicResult,
    $truncatedResult,
    $trailingResult,
    $duplicateResult,
    $payloadBoundsResult,
    $countBoundsResult,
    $unsupportedSchemaResult,
    $nonCanonicalGuidResult,
    $invalidUtf8GuidResult,
    $guidBoundsResult,
    $nameBoundsResult
)
foreach ($failedDecode in $failedDecodeCases) {
    Assert-True (-not $failedDecode.Success -and $null -eq $failedDecode.Manifest) `
        "An invalid manifest decode returned a usable manifest."
    $expectedDiagnostics = @($failedDecode.Diagnostics | ForEach-Object {
        $_.Code + "|" + $_.PluginGuid
    })
    Assert-ValidationResult `
        (Invoke-DecodedValidation $strictPolicy $failedDecode) `
        $expectedDiagnostics @() "Strict invalid decoded manifest"
    foreach ($allowAdminExceptions in @($false, $true)) {
        $failedValidation = Invoke-DecodedValidation `
            $strictPolicy $failedDecode $allowAdminExceptions
        Assert-ValidationResult `
            $failedValidation $expectedDiagnostics @() `
            ("Invalid decoded manifest; administrator=" + $allowAdminExceptions)
        Assert-True (
            [object]::ReferenceEquals(
                $failedDecode.Diagnostics[0],
                $failedValidation.Diagnostics[0])) `
            "Validation did not preserve the original bounded-decode diagnostic."
    }
}

$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("ServerManager-IntegritySmoke-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

try {
    # Exercise real service admission/confirmation methods without creating
    # Unity-owned Chainloader state or native FileSystemWatchers. Only the
    # service's required validation dependencies are supplied to this fixture.
    $servicePolicyRoot = Join-Path $testRoot "service-admin-policy"
    $serviceLimits = New-Limits
    $serviceStore = $policyStoreConstructor.Invoke(
        [object[]]@([string]$servicePolicyRoot, $serviceLimits))
    $currentPolicyField = $policyStoreType.GetField("_current", $instanceAll)
    $currentPolicyField.SetValue($serviceStore, $strictPolicy)
    $serviceFixture = [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
        $integrityServiceType)
    $serviceLogField = $integrityServiceType.GetField("_log", $instanceAll)
    $serviceLog = $serviceLogField.FieldType.GetConstructor([Type[]]@([string])).Invoke(
        [object[]]@("Integrity administrator smoke"))
    $serviceLogField.SetValue($serviceFixture, $serviceLog)
    $serviceLimitsField = $integrityServiceType.GetField("_limits", $instanceAll)
    $serviceLimitsField.SetValue($serviceFixture, $serviceLimits)
    $integrityServiceType.GetField("_policyStore", $instanceAll).SetValue(
        $serviceFixture,
        $serviceStore)
    try {
        foreach ($policyCase in $adminPolicyCases) {
            $caseManifest = New-Manifest $policyCase.Entries
            $casePayload = [byte[]](Invoke-Encode $caseManifest).Payload
            $strictAccepted =
                $policyCase.Rejecting.Count -eq 0 -and
                $policyCase.Exemptible.Count -eq 0
            $strictDecision = $validateService.Invoke(
                $serviceFixture,
                [object[]]@($null, $casePayload))
            Assert-True ($strictDecision.Accepted -eq $strictAccepted) `
                ("The service compatibility validator was not strict: " + $policyCase.Name)
            foreach ($allowAuthenticatedAdminReview in @($false, $true)) {
                $admission = Invoke-ServiceAdmission `
                    $serviceFixture $casePayload $allowAuthenticatedAdminReview
                $expectedAccepted = $strictAccepted -or
                    ($allowAuthenticatedAdminReview -and $policyCase.Rejecting.Count -eq 0)
                $expectedPending = $expectedAccepted -and -not $strictAccepted
                Assert-True (
                    $admission.Decision.Accepted -eq $expectedAccepted -and
                    ($null -ne $admission.PendingManifest) -eq $expectedPending) `
                    ("The service provisional admission decision/pending manifest changed: " +
                        $policyCase.Name + "; authenticated review=" +
                        $allowAuthenticatedAdminReview)
                if ($expectedPending) {
                    $confirmed = Invoke-ServiceConfirmation `
                        $serviceFixture $admission.PendingManifest $true
                    Assert-True (
                        $confirmed.Decision.Accepted -and
                        $confirmed.ExemptedDiagnostics.Count -eq $policyCase.Exemptible.Count) `
                        "Final authenticated administrator confirmation lost its audit discrepancies."
                    $revoked = Invoke-ServiceConfirmation `
                        $serviceFixture $admission.PendingManifest $false
                    Assert-True (
                        -not $revoked.Decision.Accepted -and
                        $revoked.ExemptedDiagnostics.Count -eq 0) `
                        "Revoked administrator status retained a provisional mod exception."
                }
            }
        }

        foreach ($badPayload in @($invalidMagic, $truncated, $duplicatePayload)) {
            $badAdmission = Invoke-ServiceAdmission $serviceFixture $badPayload $true
            Assert-True (
                -not $badAdmission.Decision.Accepted -and
                $null -eq $badAdmission.PendingManifest) `
                "Authenticated administrator review deferred a malformed manifest in the service."
        }
        $serviceLimitsField.SetValue($serviceFixture, $smallPayloadLimits)
        $oversizedAdmission = Invoke-ServiceAdmission $serviceFixture $validPayload $true
        Assert-True (
            -not $oversizedAdmission.Decision.Accepted -and
            $null -eq $oversizedAdmission.PendingManifest) `
            "Authenticated administrator review deferred the service manifest byte limit."
        $serviceLimitsField.SetValue($serviceFixture, $serviceLimits)

        $pendingPayload = [byte[]](Invoke-Encode (
            New-Manifest @($requiredEntry, $optionalWrongHashEntry, $unlistedEntry))).Payload
        $pendingAdmission = Invoke-ServiceAdmission $serviceFixture $pendingPayload $true
        Assert-True (
            $pendingAdmission.Decision.Accepted -and
            $null -ne $pendingAdmission.PendingManifest) `
            "The policy-reload fixture did not start with a provisional administrator manifest."
        Copy-Item -LiteralPath $pluginPath `
            -Destination (Join-Path $servicePolicyRoot "required\ServerManager.dll")
        $serviceReload = $serviceStore.TryReload()
        Assert-True $serviceReload.Success `
            "The service policy-reload fixture did not publish its new required rule."
        $changedPolicyConfirmation = Invoke-ServiceConfirmation `
            $serviceFixture $pendingAdmission.PendingManifest $true
        Assert-True (
            -not $changedPolicyConfirmation.Decision.Accepted -and
            $changedPolicyConfirmation.ExemptedDiagnostics.Count -eq 0) `
            "Final administrator confirmation used a stale policy after a required-folder reload."

        $currentPolicyField.SetValue($serviceStore, $null)
        $unavailableConfirmation = Invoke-ServiceConfirmation `
            $serviceFixture $pendingAdmission.PendingManifest $true
        $unavailableAdmission = Invoke-ServiceAdmission `
            $serviceFixture $pendingPayload $true
        Assert-True (
            -not $unavailableConfirmation.Decision.Accepted -and
            $unavailableConfirmation.ExemptedDiagnostics.Count -eq 0 -and
            -not $unavailableAdmission.Decision.Accepted -and
            $null -eq $unavailableAdmission.PendingManifest) `
            "Administrator admission or confirmation bypassed an unavailable server policy."
        $integrityServiceType.GetField("_reloadPending", $instanceAll).SetValue($serviceFixture, $true)
        $startupAdmission = Invoke-ServiceAdmission $serviceFixture $pendingPayload $true
        Assert-True (-not $startupAdmission.Decision.Accepted -and $null -eq $startupAdmission.PendingManifest) `
            "Asynchronous policy startup admitted a player before the first valid publication."
    }
    finally {
        $serviceLog.Dispose()
    }

    $flatPolicyRoot = Join-Path $testRoot "flat-policy-inputs"
    $flatPolicyPath = Join-Path $flatPolicyRoot "mod-policy.yml"
    $flatRequiredRoot = Join-Path $flatPolicyRoot "required"
    $flatOptionalRoot = Join-Path $flatPolicyRoot "optional"
    $relevanceArguments = [object[]]::new(3)
    $relevanceArguments[1] = [IO.Path]::GetFullPath($flatRequiredRoot)
    $relevanceArguments[2] = [IO.Path]::GetFullPath($flatOptionalRoot)

    $relevantPaths = @(
        $flatRequiredRoot,
        (Join-Path $flatRequiredRoot "nested\Required.dll"),
        $flatOptionalRoot,
        (Join-Path $flatOptionalRoot "nested\Optional.dll")
    )
    foreach ($relevantPath in $relevantPaths) {
        $relevanceArguments[0] = [string]$relevantPath
        Assert-True ([bool]$isRelevantPolicyPath.Invoke(
                $null,
                $relevanceArguments)) `
            "A required/optional folder input was excluded from watcher relevance."
    }

    $irrelevantPaths = @(
        $flatPolicyRoot,
        (Join-Path $flatPolicyRoot "characters\76561198000000001\Steam_76561198000000001_profile.fch"),
        (Join-Path $flatPolicyRoot "logs\events-audit.log"),
        (Join-Path $flatPolicyRoot "characters\76561198000000001\Steam_76561198000000001_profile.2026-09-05_11-22-33.fch"),
        $flatPolicyPath,
        (Join-Path $flatPolicyRoot "mod-policy.yml.bak"),
        (Join-Path $flatPolicyRoot "required-old\Legacy.dll"),
        (Join-Path $flatPolicyRoot "optional-copy\Legacy.dll"),
        (Join-Path $flatPolicyRoot "policy-sources\required\Legacy.dll"),
        (Join-Path $flatRequiredRoot "..\characters\76561198000000001\Steam_76561198000000001_profile.fch")
    )
    foreach ($irrelevantPath in $irrelevantPaths) {
        $relevanceArguments[0] = [string]$irrelevantPath
        Assert-True (-not [bool]$isRelevantPolicyPath.Invoke(
                $null,
                $relevanceArguments)) `
            "A non-policy ServerManager path can trigger a policy reload."
    }

    $policyRoot = Join-Path $testRoot "folder-policy-store"
    [IO.Directory]::CreateDirectory($policyRoot) | Out-Null
    $legacyPolicyPath = Join-Path $policyRoot "mod-policy.yml"
    $legacyPolicyText = @"
schema_version: 1
allow_unlisted_plugins: true
plugins: []
"@
    [IO.File]::WriteAllText(
        $legacyPolicyPath,
        $legacyPolicyText,
        [Text.UTF8Encoding]::new($false))
    $legacyPolicyTimestamp = [DateTime]::new(
        2024,
        1,
        2,
        3,
        4,
        5,
        [DateTimeKind]::Utc)
    [IO.File]::SetLastWriteTimeUtc(
        $legacyPolicyPath,
        $legacyPolicyTimestamp)
    $legacyPolicyBytesBefore = [Convert]::ToBase64String(
        [IO.File]::ReadAllBytes($legacyPolicyPath))

    $legacySourceRequiredRoot = Join-Path `
        $policyRoot `
        "policy-sources\required"
    [IO.Directory]::CreateDirectory($legacySourceRequiredRoot) | Out-Null
    $legacySourcePath = Join-Path `
        $legacySourceRequiredRoot `
        "IgnoredBroken.dll"
    [IO.File]::WriteAllBytes(
        $legacySourcePath,
        [byte[]]@(1, 2, 3, 4))

    $policyStoreArguments = [object[]]::new(2)
    $policyStoreArguments[0] = [string]$policyRoot
    $policyStoreArguments[1] = New-Limits
    $policyStore = $policyStoreConstructor.Invoke($policyStoreArguments)
    Copy-Item `
        -LiteralPath $pluginPath `
        -Destination (Join-Path $policyRoot "required\ServerManager.dll")
    $firstReload = $policyStore.TryReload()
    Assert-True $firstReload.Success `
        ("A valid required-folder policy did not load: " +
            (($firstReload.Diagnostics | ForEach-Object {
                $_.Code + ': ' + $_.Message
            }) -join '; '))
    $firstSnapshot = $policyStore.Current
    Assert-True ($null -ne $firstSnapshot) `
        "The successful policy reload did not publish a snapshot."
    $firstRules = @($firstSnapshot.Rules)
    Assert-True (
        $firstRules.Count -eq 1 -and
        $firstRules[0].PluginGuid -eq "sighsorry.servermanager" -and
        $firstRules[0].Requirement.ToString() -eq "Required") `
        "A legacy YAML rule or open-policy setting affected the folder-only snapshot."
    $legacyOpenPolicyDecision = Invoke-Validation `
        $firstSnapshot `
        (New-Manifest @($unlistedEntry))
    Assert-True (-not $legacyOpenPolicyDecision.Allowed) `
        "A legacy allow_unlisted_plugins value reopened the strict folder policy."
    Assert-DiagnosticCode `
        @($legacyOpenPolicyDecision.Diagnostics) `
        "validation.unlisted_plugin_present" `
        "The strict folder policy no longer rejects an unlisted plugin."
    Assert-True (
        (Test-Path -LiteralPath $legacyPolicyPath -PathType Leaf) -and
        [Convert]::ToBase64String(
            [IO.File]::ReadAllBytes($legacyPolicyPath)) -eq
            $legacyPolicyBytesBefore -and
        [IO.File]::GetLastWriteTimeUtc($legacyPolicyPath) -eq
            $legacyPolicyTimestamp) `
        "A pre-existing mod-policy.yml was read, rewritten, or removed."
    Assert-True (Test-Path -LiteralPath $legacySourcePath -PathType Leaf) `
        "The legacy policy-sources input was consumed or removed."

    $rollingReferencePath = Join-Path `
        $policyRoot `
        "required\ServerManager-rolling.dll"
    $baseReferenceBytes = [IO.File]::ReadAllBytes($pluginPath)
    $rollingReferenceBytes = [byte[]]::new($baseReferenceBytes.Length + 1)
    [Buffer]::BlockCopy(
        $baseReferenceBytes,
        0,
        $rollingReferenceBytes,
        0,
        $baseReferenceBytes.Length)
    $rollingReferenceBytes[$rollingReferenceBytes.Length - 1] = 1
    [IO.File]::WriteAllBytes($rollingReferencePath, $rollingReferenceBytes)
    $rollingReload = $policyStore.TryReload()
    Assert-True $rollingReload.Success `
        "Two same-role hashes for an ordinary plugin GUID were rejected."
    $rollingRule = @($policyStore.Current.Rules) |
        Where-Object PluginGuid -eq "sighsorry.servermanager" |
        Select-Object -First 1
    $baseReferenceHash = (Get-FileHash `
            -LiteralPath $pluginPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    $rollingReferenceHash = (Get-FileHash `
            -LiteralPath $rollingReferencePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True (
        $null -ne $rollingRule -and
        @($rollingRule.AllowedSha256).Count -eq 2 -and
        @($rollingRule.AllowedSha256) -contains $baseReferenceHash -and
        @($rollingRule.AllowedSha256) -contains $rollingReferenceHash) `
        "Same-role rolling-update hashes were not merged deterministically."

    Remove-Item -LiteralPath $rollingReferencePath -Force
    $singleHashReload = $policyStore.TryReload()
    $singleHashRule = @($policyStore.Current.Rules) |
        Where-Object PluginGuid -eq "sighsorry.servermanager" |
        Select-Object -First 1
    Assert-True (
        $singleHashReload.Success -and
        $null -ne $singleHashRule -and
        @($singleHashRule.AllowedSha256).Count -eq 1 -and
        @($singleHashRule.AllowedSha256) -contains $baseReferenceHash) `
        "Removing one rolling-update DLL did not remove only its allowed hash."
    $snapshotBeforeInvalidReload = $policyStore.Current

    $brokenReferencePath = Join-Path $policyRoot "optional\Broken.dll"
    [IO.File]::WriteAllBytes($brokenReferencePath, [byte[]]@(1, 2, 3, 4))
    $invalidReload = $policyStore.TryReload()
    Assert-True (-not $invalidReload.Success) `
        "A malformed reference DLL was accepted."
    Assert-True $invalidReload.KeptPreviousSnapshot `
        "An invalid reload did not report last-known-good retention."
    Assert-True (
        [object]::ReferenceEquals(
            $snapshotBeforeInvalidReload,
            $policyStore.Current)) `
        "An invalid folder reload replaced the active policy snapshot."
    Assert-True (
        [object]::ReferenceEquals(
            $snapshotBeforeInvalidReload,
            $invalidReload.ActiveSnapshot)) `
        "The invalid reload result did not expose the retained snapshot."
    Assert-DiagnosticCode `
        @($invalidReload.Diagnostics) `
        "policy.source.invalid_assembly" `
        "A malformed reference DLL did not produce the stable policy diagnostic."

    Remove-Item -LiteralPath $brokenReferencePath -Force
    $recoveredReload = $policyStore.TryReload()
    Assert-True $recoveredReload.Success `
        "The folder policy did not recover after removing an invalid DLL."
    Assert-True (
        $policyStore.Current.Generation -gt
            $snapshotBeforeInvalidReload.Generation) `
        "A recovered folder policy did not publish a later generation."

    # Preparing a candidate is worker-only work: publication and its generation
    # change happen only when the owner explicitly accepts the finished result.
    $prepareReload = $policyStoreType.GetMethod("PrepareReload", $instanceAll)
    $publishReload = $policyStoreType.GetMethod("PublishReload", $instanceAll)
    Assert-True ($null -ne $prepareReload -and $null -ne $publishReload) `
        "Policy preparation/publication seams are missing."
    $beforePreparation = $policyStore.Current
    $prepared = $prepareReload.Invoke($policyStore, [object[]]@([Threading.CancellationToken]::None))
    Assert-True ([object]::ReferenceEquals($beforePreparation, $policyStore.Current)) `
        "Preparing a reload published before the owning service accepted it."
    $preparedPublished = $publishReload.Invoke($policyStore, [object[]]@($prepared))
    Assert-True ($preparedPublished.Success -and
        $policyStore.Current.Generation -eq $beforePreparation.Generation + 1) `
        "Publishing a prepared reload did not increment exactly one generation."
    $beforeCancelledPreparation = $policyStore.Current
    $prepareCancellation = [Threading.CancellationTokenSource]::new()
    try {
        $prepareCancellation.Cancel()
        $preparationCancelled = $false
        try { $null = $prepareReload.Invoke($policyStore, [object[]]@($prepareCancellation.Token)) }
        catch { $preparationCancelled = $_.Exception.GetBaseException() -is [OperationCanceledException] }
        Assert-True ($preparationCancelled -and
            [object]::ReferenceEquals($beforeCancelledPreparation, $policyStore.Current)) `
            "Cancelled policy preparation changed the last-known-good snapshot."
    }
    finally { $prepareCancellation.Dispose() }

    # Execute real completion/invalidation logic with controlled TaskCompletion-
    # Sources. No Unity runtime, native watcher callbacks or timing races needed.
    $reloadFixture = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($integrityServiceType)
    $reloadLog = $serviceLogField.FieldType.GetConstructor([Type[]]@([string])).Invoke(
        [object[]]@("Integrity reload smoke"))
    $reloadFields = @{}
    foreach ($reloadFieldName in @("_log", "_limits", "_policyStore", "_reloadGate", "_reloadTask",
        "_reloadCancellation", "_reloadRequestVersion", "_runningReloadVersion", "_reloadPending",
        "_reloadDueTimestamp", "_reloadRetryCount", "_disposed")) {
        $reloadFields[$reloadFieldName] = $integrityServiceType.GetField($reloadFieldName, $instanceAll)
        Assert-True ($null -ne $reloadFields[$reloadFieldName]) "Missing reload state: $reloadFieldName"
    }
    $reloadFields["_log"].SetValue($reloadFixture, $reloadLog)
    $reloadFields["_limits"].SetValue($reloadFixture, (New-Limits))
    $reloadFields["_policyStore"].SetValue($reloadFixture, $policyStore)
    $reloadFields["_reloadGate"].SetValue($reloadFixture, [object]::new())
    $reloadTick = $integrityServiceType.GetMethod("Tick", $instanceAll)
    $reloadRequest = $integrityServiceType.GetMethod("Reload", $instanceAll)
    $reloadDispose = $integrityServiceType.GetMethod("Dispose", $instanceAll)
    $completionType = [Threading.Tasks.TaskCompletionSource``1].MakeGenericType($prepareReload.ReturnType)
    try {
        $pendingWorker = [Activator]::CreateInstance($completionType)
        $pendingCancellation = [Threading.CancellationTokenSource]::new()
        $pendingToken = $pendingCancellation.Token
        $reloadFields["_reloadTask"].SetValue($reloadFixture, $pendingWorker.Task)
        $reloadFields["_reloadCancellation"].SetValue($reloadFixture, $pendingCancellation)
        $reloadFields["_runningReloadVersion"].SetValue($reloadFixture, [long]1)
        $reloadFields["_reloadRequestVersion"].SetValue($reloadFixture, [long]1)
        $reloadFields["_reloadPending"].SetValue($reloadFixture, $true)
        $reloadFields["_reloadDueTimestamp"].SetValue($reloadFixture, [long]0)
        $beforePendingTick = $policyStore.Current
        $reloadWatch = [Diagnostics.Stopwatch]::StartNew()
        $null = $reloadTick.Invoke($reloadFixture, [object[]]@())
        Assert-True ($reloadWatch.ElapsedMilliseconds -lt 2000 -and
            [object]::ReferenceEquals($pendingWorker.Task, $reloadFields["_reloadTask"].GetValue($reloadFixture)) -and
            [object]::ReferenceEquals($beforePendingTick, $policyStore.Current)) `
            "An unfinished reload blocks Tick, launches a competing scan or publishes early."
        $null = $reloadRequest.Invoke($reloadFixture, [object[]]@())
        $null = $reloadRequest.Invoke($reloadFixture, [object[]]@())
        Assert-True ($pendingToken.IsCancellationRequested -and
            $reloadFields["_reloadRequestVersion"].GetValue($reloadFixture) -eq 3 -and
            [object]::ReferenceEquals($pendingWorker.Task, $reloadFields["_reloadTask"].GetValue($reloadFixture))) `
            "Folder edits fail to invalidate/cancel the worker, or abandon single-flight ownership."
        $reloadFields["_reloadDueTimestamp"].SetValue($reloadFixture, [long]::MaxValue)
        $pendingWorker.SetResult($prepared)
        $null = $reloadTick.Invoke($reloadFixture, [object[]]@())
        Assert-True ([object]::ReferenceEquals($beforePendingTick, $policyStore.Current) -and
            $null -eq $reloadFields["_reloadTask"].GetValue($reloadFixture) -and
            $reloadFields["_reloadPending"].GetValue($reloadFixture)) `
            "A stale completed reload was published or lost the newer scheduled request."

        $currentWorker = [Activator]::CreateInstance($completionType)
        $reloadFields["_reloadTask"].SetValue($reloadFixture, $currentWorker.Task)
        $reloadFields["_reloadCancellation"].SetValue($reloadFixture, [Threading.CancellationTokenSource]::new())
        $reloadFields["_runningReloadVersion"].SetValue($reloadFixture, [long]3)
        $reloadFields["_reloadPending"].SetValue($reloadFixture, $false)
        $currentWorker.SetResult($prepared)
        $null = $reloadTick.Invoke($reloadFixture, [object[]]@())
        Assert-True ($policyStore.Current.Generation -eq $beforePendingTick.Generation + 1 -and
            $null -eq $reloadFields["_reloadTask"].GetValue($reloadFixture)) `
            "The current completed worker was not published exactly once."

        [IO.File]::WriteAllBytes($brokenReferencePath, [byte[]]@(1, 2, 3, 4))
        $invalidPrepared = $prepareReload.Invoke($policyStore, [object[]]@([Threading.CancellationToken]::None))
        Remove-Item -LiteralPath $brokenReferencePath -Force
        $beforeFailedWorker = $policyStore.Current
        $failedWorker = [Activator]::CreateInstance($completionType)
        $reloadFields["_reloadTask"].SetValue($reloadFixture, $failedWorker.Task)
        $reloadFields["_reloadCancellation"].SetValue($reloadFixture, [Threading.CancellationTokenSource]::new())
        $failedWorker.SetResult($invalidPrepared)
        $null = $reloadTick.Invoke($reloadFixture, [object[]]@())
        Assert-True ([object]::ReferenceEquals($beforeFailedWorker, $policyStore.Current) -and
            $reloadFields["_reloadPending"].GetValue($reloadFixture) -and
            $reloadFields["_reloadRetryCount"].GetValue($reloadFixture) -eq 1) `
            "An invalid background reload replaced the old policy or did not schedule bounded recovery."

        $abandonedWorker = [Activator]::CreateInstance($completionType)
        $abandonedCancellation = [Threading.CancellationTokenSource]::new()
        $abandonedToken = $abandonedCancellation.Token
        $reloadFields["_reloadTask"].SetValue($reloadFixture, $abandonedWorker.Task)
        $reloadFields["_reloadCancellation"].SetValue($reloadFixture, $abandonedCancellation)
        $disposeWatch = [Diagnostics.Stopwatch]::StartNew()
        $null = $reloadDispose.Invoke($reloadFixture, [object[]]@())
        Assert-True ($disposeWatch.ElapsedMilliseconds -lt 2000 -and $abandonedToken.IsCancellationRequested -and
            $reloadFields["_disposed"].GetValue($reloadFixture) -and
            $null -eq $reloadFields["_reloadTask"].GetValue($reloadFixture)) `
            "Disposal waits for an unfinished scan, omits cancellation or retains worker ownership."
        $abandonedWorker.SetException([InvalidOperationException]::new("Late fixture failure"))
        $null = $reloadTick.Invoke($reloadFixture, [object[]]@())
        Assert-True ([object]::ReferenceEquals($beforeFailedWorker, $policyStore.Current)) `
            "A disposed integrity service published a late worker result."
    }
    finally {
        $null = $reloadDispose.Invoke($reloadFixture, [object[]]@())
        $reloadLog.Dispose()
    }

    $selfPolicyRoot = Join-Path $testRoot "self-policy-store"
    $selfLimits = New-Limits
    $selfScanner = New-ReferenceScanner $selfPolicyRoot $selfLimits
    $selfEntry = New-ManifestEntry `
        "sighsorry.servermanager" "ServerManager" $allowedHash
    $selfStoreArguments = [object[]]::new(4)
    $selfStoreArguments[0] = [string]$selfPolicyRoot
    $selfStoreArguments[1] = $selfLimits
    $selfStoreArguments[2] = $selfScanner
    $selfStoreArguments[3] = $selfEntry
    $selfStore = $policyStoreWithSelfConstructor.Invoke($selfStoreArguments)
    $selfInitialReload = $selfStore.TryReload()
    Assert-True $selfInitialReload.Success `
        "The running ServerManager self rule could not initialize an empty folder policy."
    $selfInitialSnapshot = $selfStore.Current
    $selfInitialRules = @($selfInitialSnapshot.Rules)
    Assert-True (
        $selfInitialRules.Count -eq 1 -and
        $selfInitialRules[0].PluginGuid -eq "sighsorry.servermanager" -and
        $selfInitialRules[0].Requirement.ToString() -eq "Required" -and
        @($selfInitialRules[0].AllowedSha256) -contains $allowedHash) `
        "The injected ServerManager rule is not strict, required, and hash-bound."

    $wrongSelfEntry = New-ManifestEntry `
        "sighsorry.servermanager" "ServerManager" $otherHash
    foreach ($allowAdminExceptions in @($false, $true)) {
        $expectedUnlisted = @("validation.unlisted_plugin_present|" + $unlistedGuid)
        $expectedExempted = @()
        if ($allowAdminExceptions) {
            $expectedUnlisted = @()
            $expectedExempted = @("validation.unlisted_plugin_present|" + $unlistedGuid)
        }
        Assert-ValidationResult `
            (Invoke-Validation $selfInitialSnapshot `
                (New-Manifest @($selfEntry, $unlistedEntry)) $allowAdminExceptions) `
            $expectedUnlisted $expectedExempted "Running ServerManager hash"
        Assert-ValidationResult `
            (Invoke-Validation $selfInitialSnapshot `
                (New-Manifest @($unlistedEntry)) $allowAdminExceptions) `
            (@("validation.required_plugin_missing|sighsorry.servermanager") +
                $expectedUnlisted) $expectedExempted "Running ServerManager absent"
        Assert-ValidationResult `
            (Invoke-Validation $selfInitialSnapshot `
                (New-Manifest @($wrongSelfEntry, $unlistedEntry)) $allowAdminExceptions) `
            (@("validation.hash_not_allowed|sighsorry.servermanager") +
                $expectedUnlisted) $expectedExempted "Running ServerManager wrong hash"
    }

    $optionalSelfPath = Join-Path $selfPolicyRoot "optional\ServerManager.dll"
    Copy-Item -LiteralPath $pluginPath -Destination $optionalSelfPath
    $optionalSelfReload = $selfStore.TryReload()
    Assert-True (-not $optionalSelfReload.Success) `
        "An optional ServerManager reference was silently coerced to required."
    Assert-DiagnosticCode `
        @($optionalSelfReload.Diagnostics) `
        "policy.source.role_conflict" `
        "An optional ServerManager reference did not report a role conflict."
    Assert-True (
        [object]::ReferenceEquals($selfInitialSnapshot, $selfStore.Current)) `
        "An optional ServerManager reference replaced the last-known-good policy."

    Remove-Item -LiteralPath $optionalSelfPath -Force
    $requiredSelfPath = Join-Path $selfPolicyRoot "required\ServerManager.dll"
    Copy-Item -LiteralPath $pluginPath -Destination $requiredSelfPath
    $selfRollingReload = $selfStore.TryReload()
    Assert-True $selfRollingReload.Success `
        "A required ServerManager rolling-update reference was rejected."
    $selfRollingRule = @($selfStore.Current.Rules) |
        Where-Object PluginGuid -eq "sighsorry.servermanager" |
        Select-Object -First 1
    $builtPluginHash = (Get-FileHash `
            -LiteralPath $pluginPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True (
        $null -ne $selfRollingRule -and
        @($selfRollingRule.AllowedSha256) -contains $allowedHash -and
        @($selfRollingRule.AllowedSha256) -contains $builtPluginHash) `
        "The running and required-folder ServerManager hashes were not unioned."

    $scannerRoot = Join-Path $testRoot "scanner-valid"
    $scanner = New-ReferenceScanner $scannerRoot (New-Limits)
    $scannerEnsureDirectories.Invoke($scanner, [object[]]@()) | Out-Null
    Assert-True (
        (Test-Path -LiteralPath (Join-Path $scannerRoot "required") -PathType Container) -and
        (Test-Path -LiteralPath (Join-Path $scannerRoot "optional") -PathType Container) -and
        -not (Test-Path -LiteralPath (Join-Path $scannerRoot "policy-sources"))) `
        "Reference policy directory preparation did not create required and optional."
    $nestedRequiredRoot = Join-Path $scannerRoot "required\nested\deeper"
    [IO.Directory]::CreateDirectory($nestedRequiredRoot) | Out-Null
    Copy-Item `
        -LiteralPath $pluginPath `
        -Destination (Join-Path $nestedRequiredRoot "ServerManager.dll")
    $scan = $scannerScan.Invoke($scanner, [object[]]@([Threading.CancellationToken]::None))
    $scanDiagnostics = @(Get-HiddenProperty $scan "Diagnostics")
    Assert-True ($scanDiagnostics.Count -eq 0) `
        ("A valid BepInEx reference DLL was rejected: " +
            (($scanDiagnostics | ForEach-Object { $_.ToString() }) -join "; "))
    $foundServerManager = $false
    foreach ($record in @(Get-HiddenProperty $scan "Records")) {
        $recordEntry = Get-HiddenProperty $record "Entry"
        $recordRequirement = Get-HiddenProperty $record "Requirement"
        if ($recordEntry.PluginGuid -eq "sighsorry.servermanager" -and
            $recordRequirement.ToString() -eq "Required") {
            $foundServerManager = $true
        }
    }
    Assert-True $foundServerManager `
        "A recursively nested required DLL did not produce the ServerManager record."

    Copy-Item `
        -LiteralPath $pluginPath `
        -Destination (Join-Path $scannerRoot "optional\ServerManager.dll")
    $roleConflict = $scannerScan.Invoke($scanner, [object[]]@([Threading.CancellationToken]::None))
    $roleConflictDiagnostics = @(
        Get-HiddenProperty $roleConflict "Diagnostics")
    Assert-True ($roleConflictDiagnostics.Count -ne 0) `
        "The same GUID in required and optional folders was accepted."
    Assert-DiagnosticCode `
        $roleConflictDiagnostics `
        "policy.source.role_conflict" `
        "The reference-folder role conflict diagnostic changed."

    # Package dependencies are ignored in both roles, including renamed files
    # and conflicting builds. A real plugin in the same folders stays mandatory.
    $libraryRoot = Join-Path $testRoot "scanner-managed-library"
    $libraryScanner = New-ReferenceScanner $libraryRoot (New-Limits)
    $scannerEnsureDirectories.Invoke($libraryScanner, [object[]]@()) | Out-Null
    Copy-Item -LiteralPath $cecilPath -Destination (Join-Path $libraryRoot "required/RenamedLibrary.dll")
    New-ManagedReferenceFixture (Join-Path $libraryRoot "optional/OtherBuild.dll") "Mono.Cecil" "99.0.0.0"
    $libraryScan = Invoke-ReferenceScan $libraryScanner
    Assert-True (@(Get-HiddenProperty $libraryScan "Diagnostics").Count -eq 0 -and
        @(Get-HiddenProperty $libraryScan "Records").Count -eq 0 -and
        @(Get-HiddenProperty $libraryScan "SkippedLibraries").Count -eq 2) `
        "Pure libraries must be reported as skipped without rules or role/hash conflicts."
    $libraryStore = $policyStoreConstructor.Invoke([object[]]@([string]$libraryRoot, (New-Limits)))
    $libraryReload = $libraryStore.TryReload()
    Assert-True ($libraryReload.Success -and $libraryStore.Current.Rules.Count -eq 0) `
        "A policy containing only dependency DLLs should impose no plugin requirements."
    Assert-True (Invoke-Validation $libraryStore.Current (New-Manifest @())).Allowed `
        "Absent standalone libraries blocked admission."
    Copy-Item -LiteralPath $pluginPath -Destination (Join-Path $libraryRoot "required/ServerManager.dll")
    Assert-True ($libraryStore.TryReload().Success -and $libraryStore.Current.Rules.Count -eq 1) `
        "A plugin beside skipped libraries lost its required rule."
    Assert-True (-not (Invoke-Validation $libraryStore.Current (New-Manifest @())).Allowed) `
        "Skipped libraries weakened the required-plugin presence check."
    Copy-Item -LiteralPath $pluginPath -Destination (Join-Path $libraryRoot "optional/ServerManager.dll")
    $libraryLastGood = $libraryStore.Current
    Assert-True (-not $libraryStore.TryReload().Success -and
        [object]::ReferenceEquals($libraryLastGood, $libraryStore.Current)) `
        "A plugin role conflict beside libraries replaced the last-known-good policy."

    $reservedGuidRoot = Join-Path $testRoot "scanner-reserved-plugin-guid"
    $reservedGuidScanner = New-ReferenceScanner $reservedGuidRoot (New-Limits)
    $scannerEnsureDirectories.Invoke($reservedGuidScanner, [object[]]@()) | Out-Null
    New-ManagedReferenceFixture (Join-Path $reservedGuidRoot "required\FakeLibraryPlugin.dll") `
        "Reserved.Plugin.Fixture" "1.0.0.0" "assembly:yamldotnet"
    $reservedGuidScan = Invoke-ReferenceScan $reservedGuidScanner
    Assert-DiagnosticCode @(Get-HiddenProperty $reservedGuidScan "Diagnostics") `
        "policy.source.invalid_assembly" "A BepInPlugin GUID impersonated the reserved library namespace."
    Assert-True (@(Get-HiddenProperty $reservedGuidScan "Records").Count -eq 0) `
        "A rejected reserved-GUID plugin left partial library records behind."

    $netmoduleRoot = Join-Path $testRoot "scanner-no-assembly-identity"
    $netmoduleScanner = New-ReferenceScanner $netmoduleRoot (New-Limits)
    $scannerEnsureDirectories.Invoke($netmoduleScanner, [object[]]@()) | Out-Null
    $netmodule = [Mono.Cecil.ModuleDefinition]::CreateModule("NoIdentity", [Mono.Cecil.ModuleKind]::NetModule)
    try { $netmodule.Write((Join-Path $netmoduleRoot "required\NoIdentity.dll")) }
    finally { $netmodule.Dispose() }
    Assert-DiagnosticCode @(Get-HiddenProperty (Invoke-ReferenceScan $netmoduleScanner) "Diagnostics") `
        "policy.source.invalid_assembly" "A module without a managed assembly identity became a library rule."

    $boundedRoot = Join-Path $testRoot "scanner-bounded"
    $boundedScanner = New-ReferenceScanner `
        $boundedRoot `
        (New-Limits -MaxPluginCount 1)
    $scannerEnsureDirectories.Invoke($boundedScanner, [object[]]@()) | Out-Null
    Copy-Item `
        -LiteralPath $pluginPath `
        -Destination (Join-Path $boundedRoot "required\First.dll")
    Copy-Item `
        -LiteralPath $pluginPath `
        -Destination (Join-Path $boundedRoot "required\Second.dll")
    $boundedScan = $scannerScan.Invoke($boundedScanner, [object[]]@([Threading.CancellationToken]::None))
    Assert-DiagnosticCode `
        @(Get-HiddenProperty $boundedScan "Diagnostics") `
        "policy.source.too_many_files" `
        "The reference-folder DLL-count bound was not enforced."

    # Managed PE files may contain large embedded resources or trailing data.
    # Grow disposable copies with FileStream.SetLength, never a DLL-sized array.
    $largeRoot = Join-Path $testRoot "scanner-large-managed"
    $largeScanner = New-ReferenceScanner $largeRoot (New-Limits)
    $scannerEnsureDirectories.Invoke($largeScanner, [object[]]@()) | Out-Null
    $largePaths = @()
    for ($largeIndex = 0; $largeIndex -lt 4; ++$largeIndex) {
        $largePath = Join-Path $largeRoot ("required\Large" + $largeIndex + ".dll")
        Copy-Item -LiteralPath $pluginPath -Destination $largePath
        $largeStream = [IO.File]::Open($largePath, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $largeStream.SetLength([long](65 * 1MB))
            $largeStream.Position = $largeStream.Length - 1
            $largeStream.WriteByte([byte]$largeIndex)
        }
        finally { $largeStream.Dispose() }
        $largePaths += $largePath
    }
    $largeScan = Invoke-ReferenceScan $largeScanner
    $largeDiagnostics = @(Get-HiddenProperty $largeScan "Diagnostics")
    $largeRecords = @(Get-HiddenProperty $largeScan "Records")
    Assert-True ($largeDiagnostics.Count -eq 0 -and $largeRecords.Count -eq 4) `
        "Valid managed DLLs above 64 MiB / 256 MiB aggregate were rejected."
    $largeHashes = @($largeRecords | ForEach-Object { (Get-HiddenProperty $_ "Entry").FileSha256 })
    foreach ($largePath in $largePaths) {
        $expectedHash = (Get-FileHash -LiteralPath $largePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-True ($largeHashes -contains $expectedHash) `
            "Reference hashing omitted or changed the padded portion of a large DLL."
        $releasedHandle = [IO.File]::Open($largePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $releasedHandle.Dispose()
    }

    # The client previously had a separate 512 MiB cap. Verify both paths read
    # the same complete bytes without reinstating either numeric ceiling.
    $hugePath = $largePaths[0]
    $hugeStream = [IO.File]::Open($hugePath, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $hugeStream.SetLength([long](513 * 1MB))
        $hugeStream.Position = $hugeStream.Length - 1
        $hugeStream.WriteByte(173)
    }
    finally { $hugeStream.Dispose() }
    $hugeExpectedHash = (Get-FileHash -LiteralPath $hugePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $hugeScan = Invoke-ReferenceScan $largeScanner
    Assert-True (@(Get-HiddenProperty $hugeScan "Diagnostics").Count -eq 0) `
        "The server reference scanner still rejects a valid DLL above 512 MiB."
    $hugeHashes = @(Get-HiddenProperty $hugeScan "Records" | ForEach-Object { (Get-HiddenProperty $_ "Entry").FileSha256 })
    Assert-True ($hugeHashes -contains $hugeExpectedHash) `
        "The server scanner did not hash the full DLL above 512 MiB."
    $clientScannerType = $plugin.GetType("ServerManager.PluginManifestScanner", $true)
    $clientHashMethod = $clientScannerType.GetMethod("TryHashPluginFile", $staticAll)
    $clientHashArguments = [object[]]@([string]$hugePath, "Large managed fixture", "tests.large", [Threading.CancellationToken]::None, $null, $null)
    Assert-True ([bool]$clientHashMethod.Invoke($null, $clientHashArguments) -and
        $clientHashArguments[4] -eq $hugeExpectedHash -and $null -eq $clientHashArguments[5]) `
        "The client and reference scanner disagree on a valid DLL above 512 MiB."
    Assert-True ($null -eq $limitsType.GetProperty("MaxPluginFileBytes", $instanceAll)) `
        "A local DLL byte-size limit remains in the protocol limits model."

    $scanCancellation = [Threading.CancellationTokenSource]::new()
    try {
        $scanCancellation.Cancel()
        $scanCancelled = $false
        try { $null = $scannerScan.Invoke($largeScanner, [object[]]@($scanCancellation.Token)) }
        catch { $scanCancelled = $_.Exception.GetBaseException() -is [OperationCanceledException] }
        Assert-True $scanCancelled "Reference-scan cancellation became a diagnostic or continued hashing."
    }
    finally { $scanCancellation.Dispose() }
    $runningScanCancellation = [Threading.CancellationTokenSource]::new()
    try {
        $runningScanCancellation.CancelAfter(1)
        $runningScanCancelled = $false
        try { $null = $scannerScan.Invoke($largeScanner, [object[]]@($runningScanCancellation.Token)) }
        catch { $runningScanCancelled = $_.Exception.GetBaseException() -is [OperationCanceledException] }
        Assert-True $runningScanCancelled "An in-progress large reference scan ignored cancellation."
    }
    finally { $runningScanCancellation.Dispose() }
    foreach ($largePath in $largePaths) {
        $releasedHandle = [IO.File]::Open($largePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $releasedHandle.Dispose()
    }
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $leaf = [IO.Path]::GetFileName($resolvedRoot)
    if ($resolvedRoot.StartsWith(
            $resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        $leaf.StartsWith(
            "ServerManager-IntegritySmoke-",
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}

Write-Host "Integrity smoke tests passed."
