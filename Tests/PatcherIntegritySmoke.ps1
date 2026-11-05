param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSHOME "Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1") -ErrorAction Stop

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Code($Diagnostics, [string]$Code, [string]$Message) {
    Assert-True (@($Diagnostics | Where-Object Code -eq $Code).Count -gt 0) $Message
}

function Get-Hidden($Instance, [string]$Name) {
    $property = $Instance.GetType().GetProperty($Name, $script:instanceAll)
    Assert-True ($null -ne $property) "Missing property $Name."
    return $property.GetValue($Instance, $null)
}

function Get-Ctor([Type]$Type, [Type[]]$Types) {
    $ctor = $Type.GetConstructor($script:instanceAll, $null, $Types, $null)
    Assert-True ($null -ne $ctor) "Missing constructor on $($Type.FullName)."
    return $ctor
}

function Get-Method([Type]$Type, [string]$Name, [Type[]]$Types) {
    $method = $Type.GetMethod($Name, ($script:instanceAll -bor $script:staticAll), $null, $Types, $null)
    Assert-True ($null -ne $method) "Missing method $($Type.FullName).$Name."
    return $method
}

function New-List([Type]$Element, [object[]]$Values) {
    $list = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($Element))
    foreach ($value in $Values) { $list.Add($value) }
    return ,$list
}

function New-Entry([string]$Id, [string]$Hash, [string]$Kind = "Patcher") {
    return $script:entryCtor.Invoke([object[]]@($Id, "Fixture $Kind", $Hash,
        [Enum]::Parse($script:kindType, $Kind)))
}

function New-Manifest([object[]]$Entries = @()) {
    $args = [object[]]::new(1)
    $args[0] = New-List $script:entryType $Entries
    return $script:manifestCtor.Invoke($args)
}

function New-Rule([string]$Id, [string]$Hash, [string]$Requirement, [string]$Kind = "Patcher") {
    return $script:ruleCtor.Invoke([object[]]@($Id, "Fixture $Kind",
        [Enum]::Parse($script:requirementType, $Requirement), [string[]]@($Hash),
        [Enum]::Parse($script:kindType, $Kind)))
}

function New-Policy([object[]]$Rules = @()) {
    $args = [object[]]::new(2)
    $args[0] = [long]1
    $args[1] = New-List $script:ruleType $Rules
    return $script:policyCtor.Invoke($args)
}

function New-Limits([int]$Count = 1024) {
    return $script:limitsCtor.Invoke([object[]]@(262144, $Count, 256, 512))
}

function Invoke-Validation($Policy, $Manifest, [bool]$Admin = $false) {
    return $script:validate.Invoke($null, [object[]]@($Policy, $Manifest, $Admin))
}

function Invoke-RejectionText([Reflection.MethodInfo]$Method, $Policy, $Manifest, $Diagnostics) {
    return [string]$Method.Invoke($null, [object[]]@($Policy, $Manifest, $Diagnostics))
}

function Invoke-Decode([byte[]]$Payload) {
    $args = [object[]]::new(1)
    $args[0] = $Payload
    return $script:decode.Invoke($null, $args)
}

function Invoke-ClientBuild([string]$Root, $Limits, [object[]]$Plugins = @()) {
    $args = [object[]]::new(3)
    $args[0] = New-List $script:pluginInfoType $Plugins
    $args[1] = $Limits
    $args[2] = $Root
    return $script:clientBuild.Invoke($null, $args)
}

function Invoke-InstalledScan([string]$Root, $Limits) {
    return $script:installedScan.Invoke($null, [object[]]@($Root, $Limits,
        [Threading.CancellationToken]::None))
}

function Invoke-Preparation([string]$Root, $Limits) {
    $args = [object[]]::new(3)
    $args[0] = New-List $script:pluginInfoType @()
    $args[1] = $Limits
    $args[2] = $Root
    $preparation = $script:clientBegin.Invoke($null, $args)
    try {
        $getResult = $preparation.GetType().GetMethod("TryGetResult", $script:instanceAll)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($timer.Elapsed.TotalSeconds -lt 15) {
            $resultArgs = [object[]]@($null)
            if ($getResult.Invoke($preparation, $resultArgs)) { return $resultArgs[0] }
            Start-Sleep -Milliseconds 10
        }
        throw "Timed out waiting for asynchronous patcher preparation."
    }
    finally { $preparation.Dispose() }
}

function New-Store([string]$Root, $Limits) {
    return $script:storeCtor.Invoke([object[]]@($Root, $Limits))
}

function Get-PatcherId([string]$AssemblyName, [string]$TypeName) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($AssemblyName + "`nSmPatcherFixtures." + $TypeName))
        return ([BitConverter]::ToString($bytes)).Replace("-", "").ToLowerInvariant()
    }
    finally { $hasher.Dispose() }
}

function Add-ThrowBody($Method) {
    # No fixture method may run: getter, Patch, Initialize and .cctor all throw.
    $il = $Method.Body.GetILProcessor()
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldnull))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Throw))
}

function New-Fixture {
    param([string]$Path, [string]$AssemblyName, [string[]]$PatcherNames = @(),
        [switch]$ByRef, [switch]$FakeHelper, [string]$PluginGuid = "",
        [string]$Version = "1.0.0.0")

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $assembly = [Mono.Cecil.AssemblyDefinition]::CreateAssembly(
        [Mono.Cecil.AssemblyNameDefinition]::new($AssemblyName, [Version]$Version),
        ($AssemblyName + ".dll"), [Mono.Cecil.ModuleKind]::Dll)
    try {
        $module = $assembly.MainModule
        foreach ($name in $PatcherNames) {
            $type = [Mono.Cecil.TypeDefinition]::new("SmPatcherFixtures", $name,
                ([Mono.Cecil.TypeAttributes]::Public -bor [Mono.Cecil.TypeAttributes]::Abstract -bor
                    [Mono.Cecil.TypeAttributes]::Sealed), $module.TypeSystem.Object)
            $module.Types.Add($type)
            $publicStatic = [Mono.Cecil.MethodAttributes]::Public -bor [Mono.Cecil.MethodAttributes]::Static
            $targets = $module.ImportReference([Collections.Generic.IEnumerable[string]])
            $getter = [Mono.Cecil.MethodDefinition]::new("get_TargetDLLs",
                ($publicStatic -bor [Mono.Cecil.MethodAttributes]::SpecialName), $targets)
            $type.Methods.Add($getter)
            Add-ThrowBody $getter
            $property = [Mono.Cecil.PropertyDefinition]::new("TargetDLLs", [Mono.Cecil.PropertyAttributes]::None, $targets)
            $property.GetMethod = $getter
            $type.Properties.Add($property)
            $patch = [Mono.Cecil.MethodDefinition]::new("Patch", $publicStatic, $module.TypeSystem.Void)
            $argumentType = $module.ImportReference([Mono.Cecil.AssemblyDefinition])
            if ($ByRef) { $argumentType = [Mono.Cecil.ByReferenceType]::new($argumentType) }
            # A similarly named helper with the wrong argument type is not a patcher.
            if ($FakeHelper) { $argumentType = $module.TypeSystem.String }
            $patch.Parameters.Add([Mono.Cecil.ParameterDefinition]::new("assembly",
                [Mono.Cecil.ParameterAttributes]::None, $argumentType))
            $type.Methods.Add($patch)
            Add-ThrowBody $patch
            $initialize = [Mono.Cecil.MethodDefinition]::new("Initialize", $publicStatic, $module.TypeSystem.Void)
            $type.Methods.Add($initialize)
            Add-ThrowBody $initialize
            $cctor = [Mono.Cecil.MethodDefinition]::new(".cctor",
                ([Mono.Cecil.MethodAttributes]::Private -bor [Mono.Cecil.MethodAttributes]::Static -bor
                    [Mono.Cecil.MethodAttributes]::SpecialName -bor [Mono.Cecil.MethodAttributes]::RTSpecialName),
                $module.TypeSystem.Void)
            $type.Methods.Add($cctor)
            Add-ThrowBody $cctor
        }
        if ($PluginGuid) {
            $pluginType = [Mono.Cecil.TypeDefinition]::new("SmPatcherFixtures", "Plugin",
                [Mono.Cecil.TypeAttributes]::Public, $module.TypeSystem.Object)
            $module.Types.Add($pluginType)
            $attributeCtor = [BepInEx.BepInPlugin].GetConstructor([Type[]]@([string], [string], [string]))
            $attribute = [Mono.Cecil.CustomAttribute]::new($module.ImportReference($attributeCtor))
            foreach ($value in @($PluginGuid, "Dual role fixture", "1.0.0")) {
                $attribute.ConstructorArguments.Add(
                    [Mono.Cecil.CustomAttributeArgument]::new($module.TypeSystem.String, [string]$value))
            }
            $pluginType.CustomAttributes.Add($attribute)
        }
        $assembly.Write($Path)
    }
    finally { $assembly.Dispose() }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"
$bepInExPath = Join-Path $GamePath "BepInEx\core\BepInEx.dll"
foreach ($path in @($pluginPath, $cecilPath, $bepInExPath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing test dependency: $path"
}
foreach ($dependencyPath in @($bepInExPath, $cecilPath)) {
    # Resolve the merged plugin's dependencies in the default load context while
    # reading bytes, without changing Windows attachment metadata on user files.
    $dependencyStream = [IO.MemoryStream]::new([IO.File]::ReadAllBytes($dependencyPath))
    try { [Runtime.Loader.AssemblyLoadContext]::Default.LoadFromStream($dependencyStream) | Out-Null }
    finally { $dependencyStream.Dispose() }
}
$plugin = [Reflection.Assembly]::LoadFrom($pluginPath)
$instanceAll = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
$staticAll = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
$kindType = $plugin.GetType("ServerManager.IntegrityModuleKind", $true)
$entryType = $plugin.GetType("ServerManager.IntegrityManifestEntry", $true)
$manifestType = $plugin.GetType("ServerManager.IntegrityManifest", $true)
$ruleType = $plugin.GetType("ServerManager.IntegrityPolicyRule", $true)
$policyType = $plugin.GetType("ServerManager.IntegrityPolicySnapshot", $true)
$requirementType = $plugin.GetType("ServerManager.IntegrityRequirement", $true)
$limitsType = $plugin.GetType("ServerManager.IntegrityLimits", $true)
$codecType = $plugin.GetType("ServerManager.IntegrityManifestCodec", $true)
$clientType = $plugin.GetType("ServerManager.PluginManifestScanner", $true)
$entryCtor = Get-Ctor $entryType ([Type[]]@([string], [string], [string], $kindType))
$manifestCtor = Get-Ctor $manifestType ([Type[]]@([Collections.Generic.IEnumerable``1].MakeGenericType($entryType)))
$ruleCtor = Get-Ctor $ruleType ([Type[]]@([string], [string], $requirementType,
    [Collections.Generic.IEnumerable[string]], $kindType))
$policyCtor = Get-Ctor $policyType ([Type[]]@([long], [Collections.Generic.IEnumerable``1].MakeGenericType($ruleType)))
$limitsCtor = Get-Ctor $limitsType ([Type[]]@([int], [int], [int], [int]))
$storeCtor = Get-Ctor ($plugin.GetType("ServerManager.IntegrityPolicyStore", $true)) ([Type[]]@([string], $limitsType))
$validate = Get-Method ($plugin.GetType("ServerManager.IntegrityValidator", $true)) "Validate" ([Type[]]@($policyType, $manifestType, [bool]))
$serviceType = $plugin.GetType("ServerManager.ServerIntegrityService", $true)
$diagnosticEnumerable = [Collections.Generic.IEnumerable``1].MakeGenericType(
    $plugin.GetType("ServerManager.IntegrityDiagnostic", $true))
$rejectionParameters = [Type[]]@($policyType, $manifestType, $diagnosticEnumerable)
$clientRejection = Get-Method $serviceType "BuildClientRejectionMessage" $rejectionParameters
$auditDetail = Get-Method $serviceType "BuildRejectionAuditDetail" $rejectionParameters
$webhookSummary = Get-Method $serviceType "BuildRejectionPluginSummary" $rejectionParameters
$encode = Get-Method $codecType "TryEncode" ([Type[]]@($manifestType))
$decode = Get-Method $codecType "TryDecode" ([Type[]]@([byte[]]))
# Use the plugin's resolved BepInEx type, not an identically named type from a
# separate Assembly.Load(byte[]) context in this isolated PowerShell process.
$clientBuild = $clientType.GetMethods($staticAll) | Where-Object {
    $_.Name -eq "Build" -and $_.GetParameters().Count -eq 3
} | Select-Object -First 1
Assert-True ($null -ne $clientBuild) "Missing explicit-root client manifest builder."
$pluginEnumerable = $clientBuild.GetParameters()[0].ParameterType
$pluginInfoType = $pluginEnumerable.GetGenericArguments()[0]
$clientBegin = Get-Method $clientType "Begin" ([Type[]]@($pluginEnumerable, $limitsType, [string]))
$installedScan = Get-Method $clientType "ScanInstalledPatchers" ([Type[]]@([string], $limitsType, [Threading.CancellationToken]))
$limits = New-Limits
$id = "a" * 64
$hash = "b" * 64
$wrongHash = "c" * 64
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ServerManager-PatcherSmoke-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$junctionPath = $null
$junctionCreated = $false

try {
    $required = New-Policy @((New-Rule $id $hash Required))
    $optional = New-Policy @((New-Rule $id $hash Optional))
    $empty = New-Manifest
    $present = New-Manifest @((New-Entry $id $hash))
    $wrong = New-Manifest @((New-Entry $id $wrongHash))
    foreach ($admin in @($false, $true)) {
        $missing = Invoke-Validation $required $empty $admin
        Assert-True (-not $missing.Allowed) "Required patcher absence was exempted."
        Assert-Code $missing.Diagnostics "validation.required_patcher_missing" "Missing patcher diagnostic changed."
        Assert-True ($missing.Diagnostics[0].Kind.ToString() -eq "Patcher") "Missing patcher lost its diagnostic kind."
        $mismatch = Invoke-Validation $required $wrong $admin
        Assert-True (-not $mismatch.Allowed) "Required patcher hash mismatch was exempted."
        Assert-Code $mismatch.Diagnostics "validation.patcher_hash_not_allowed" "Patcher mismatch diagnostic changed."
        Assert-True (Invoke-Validation $required $present $admin).Allowed "An approved required patcher was rejected."
        Assert-True (Invoke-Validation $optional $empty $admin).Allowed "An absent optional patcher was rejected."
        $optionalMismatch = Invoke-Validation $optional $wrong $admin
        Assert-True ($optionalMismatch.Allowed -eq $admin) "Optional patcher administrator semantics changed."
        $unlisted = Invoke-Validation (New-Policy) $present $admin
        Assert-True ($unlisted.Allowed -eq $admin) "Unlisted patcher administrator semantics changed."
        if ($admin) {
            Assert-Code $optionalMismatch.ExemptedDiagnostics "validation.patcher_hash_not_allowed" "Optional exception was not audited."
            Assert-Code $unlisted.ExemptedDiagnostics "validation.unlisted_patcher_present" "Unlisted exception was not audited."
        }
    }

    $sameIdPolicy = New-Policy @((New-Rule $id $hash Required Plugin), (New-Rule $id $wrongHash Required Patcher))
    $sameIdManifest = New-Manifest @((New-Entry $id $wrongHash Patcher), (New-Entry $id $hash Plugin))
    Assert-True (Invoke-Validation $sameIdPolicy $sameIdManifest).Allowed "Plugin/patcher namespaces collided."
    Assert-True ($sameIdManifest.Entries[0].Kind.ToString() -eq "Plugin") "Canonical order does not place plugin kind first."
    $pluginOnly = Invoke-Validation $sameIdPolicy (New-Manifest @((New-Entry $id $hash Plugin)))
    Assert-Code $pluginOnly.Diagnostics "validation.required_patcher_missing" "A plugin satisfied a patcher requirement."
    $crossHashes = New-Manifest @((New-Entry $id $wrongHash Plugin), (New-Entry $id $hash Patcher))
    $crossResult = Invoke-Validation $sameIdPolicy $crossHashes
    Assert-True (@($crossResult.Diagnostics).Count -eq 2) "Hashes crossed module-kind boundaries."

    # Rejection UI/audit/webhook consumers must retain the same kind-scoped
    # lookup and deduplication contract as the validator, even for identical IDs.
    $bothMissing = Invoke-Validation $sameIdPolicy $empty
    $missingMessage = Invoke-RejectionText $clientRejection $sameIdPolicy $empty $bothMissing.Diagnostics
    Assert-True ($missingMessage.Contains("Install required") -and
        $missingMessage.Contains("Fixture Plugin") -and
        $missingMessage.Contains("[Patcher] Fixture Patcher")) "Same-ID missing-module UI lost a kind or patcher label."
    $patcherMissingMessage = Invoke-RejectionText $clientRejection $sameIdPolicy `
        (New-Manifest @((New-Entry $id $hash Plugin))) $pluginOnly.Diagnostics
    Assert-True ($patcherMissingMessage.Contains("[Patcher] Fixture Patcher") -and
        -not $patcherMissingMessage.Contains("Fixture Plugin")) "Missing patcher rejection borrowed the plugin's display name."
    $hashMessage = Invoke-RejectionText $clientRejection $sameIdPolicy $crossHashes $crossResult.Diagnostics
    Assert-True ($hashMessage.Contains("Update or reinstall") -and
        $hashMessage.Contains("Fixture Plugin") -and $hashMessage.Contains("[Patcher] Fixture Patcher")) `
        "Same-ID hash-rejection UI lost a module or patcher label."
    Assert-True (-not $hashMessage.Contains($hash) -and -not $hashMessage.Contains($wrongHash) -and
        -not $hashMessage.Contains($id)) "Client rejection leaked a file hash or raw module identity."
    $audit = Invoke-RejectionText $auditDetail $sameIdPolicy $crossHashes $crossResult.Diagnostics
    $pluginAudit = @($audit -split "`n" | Where-Object { $_.StartsWith("name=Fixture Plugin;") })
    $patcherAudit = @($audit -split "`n" | Where-Object { $_.StartsWith("name=Fixture Patcher;") })
    Assert-True ($pluginAudit.Count -eq 1 -and $patcherAudit.Count -eq 1 -and
        $audit.Contains("mismatches_shown=2; mismatches_omitted=0")) "Same-ID audit entries were merged or misnamed."
    Assert-True ($pluginAudit[0].Contains("guid=$id; mismatch=hash_not_allowed") -and
        $pluginAudit[0].Contains("expected_sha256=$hash;") -and
        $pluginAudit[0].Contains("reported_sha256=$wrongHash") -and
        -not $pluginAudit[0].Contains("kind=patcher")) "Plugin audit used the patcher's hashes or kind."
    Assert-True ($patcherAudit[0].Contains("guid=$id; mismatch=hash_not_allowed; kind=patcher") -and
        $patcherAudit[0].Contains("expected_sha256=$wrongHash;") -and
        $patcherAudit[0].Contains("reported_sha256=$hash")) "Patcher audit used the plugin's hashes or lost its kind."
    $missingAudit = Invoke-RejectionText $auditDetail $sameIdPolicy $empty $bothMissing.Diagnostics
    Assert-True ($missingAudit.Contains("name=Fixture Patcher; guid=$id; mismatch=missing; kind=patcher") -and
        $missingAudit.Contains("reported_sha256=not_reported")) "Missing patcher audit classification or absence marker changed."
    $summary = Invoke-RejectionText $webhookSummary $sameIdPolicy $crossHashes $crossResult.Diagnostics
    Assert-True ($summary.Contains("name=Fixture Plugin;") -and $summary.Contains("name=Fixture Patcher;") -and
        $summary.Contains("kind=patcher") -and $summary.Contains("mismatches_shown=2; mismatches_omitted=0") -and
        -not $summary.Contains("sha256") -and -not $summary.Contains($hash) -and
        -not $summary.Contains($wrongHash)) "Webhook summary lost kind/name separation or leaked file hashes."
    $unlistedBoth = Invoke-Validation (New-Policy) $sameIdManifest
    $unlistedMessage = Invoke-RejectionText $clientRejection (New-Policy) $sameIdManifest $unlistedBoth.Diagnostics
    Assert-True ($unlistedMessage.Contains("Remove not allowed") -and $unlistedMessage.Contains("Fixture Plugin") -and
        $unlistedMessage.Contains("[Patcher] Fixture Patcher")) "Same-ID unlisted-module UI used the wrong manifest label."
    $encoded = $encode.Invoke($null, [object[]]@($sameIdManifest))
    Assert-True ($encoded.Success -and [BitConverter]::ToInt32($encoded.Payload, 4) -eq 3) "Manifest schema is not 3."
    $decoded = Invoke-Decode $encoded.Payload
    Assert-True ($decoded.Success -and $decoded.Manifest.Entries.Count -eq 2 -and
        (Invoke-Validation $sameIdPolicy $decoded.Manifest).Allowed) "Kind-aware manifest roundtrip failed."
    $oldSchema = [byte[]]$encoded.Payload.Clone()
    [Buffer]::BlockCopy([BitConverter]::GetBytes([int]2), 0, $oldSchema, 4, 4)
    Assert-Code (Invoke-Decode $oldSchema).Diagnostics "wire.unsupported_schema" "Schema 2 was accepted."
    $badKind = [byte[]]$encoded.Payload.Clone()
    [Buffer]::BlockCopy([BitConverter]::GetBytes([int]99), 0, $badKind, 12, 4)
    Assert-Code (Invoke-Decode $badKind).Diagnostics "manifest.invalid_kind" "Unknown module kind was accepted."
    $patchOnlyEncoded = $encode.Invoke($null, [object[]]@($present))
    $badId = [byte[]]$patchOnlyEncoded.Payload.Clone()
    $badId[20] = [byte][char]'g'
    Assert-Code (Invoke-Decode $badId).Diagnostics "manifest.invalid_patcher_id" "A non-hex patcher identity was accepted."
    $duplicate = [byte[]]::new(12 + 2 * ($patchOnlyEncoded.Payload.Length - 12))
    [Buffer]::BlockCopy($patchOnlyEncoded.Payload, 0, $duplicate, 0, $patchOnlyEncoded.Payload.Length)
    [Buffer]::BlockCopy($patchOnlyEncoded.Payload, 12, $duplicate, $patchOnlyEncoded.Payload.Length, $patchOnlyEncoded.Payload.Length - 12)
    [Buffer]::BlockCopy([BitConverter]::GetBytes([int]2), 0, $duplicate, 8, 4)
    Assert-Code (Invoke-Decode $duplicate).Diagnostics "manifest.duplicate_guid" "Same-kind duplicate identity was accepted."

    $policyRoot = Join-Path $testRoot "reference"
    $store = New-Store $policyRoot $limits
    New-Fixture (Join-Path $policyRoot "required\renamed\Anything.dll") "SmPatcherFixture.Static" @("Main")
    New-Fixture (Join-Path $policyRoot "optional\Ref.dll") "SmPatcherFixture.Ref" @("ByReference") -ByRef
    New-Fixture (Join-Path $policyRoot "required\NotAPatcher.dll") "SmPatcherFixture.Fake" @("Helper") -FakeHelper
    New-Fixture (Join-Path $policyRoot "optional\Library.dll") "SmPatcherFixture.Library"
    $reload = $store.TryReload()
    Assert-True ($reload.Success -and $store.Current.Rules.Count -eq 2) "Patcher metadata or library classification failed."
    $staticId = Get-PatcherId "SmPatcherFixture.Static" "Main"
    $staticRule = @($store.Current.Rules | Where-Object PluginGuid -eq $staticId)[0]
    Assert-True ($staticRule.Kind.ToString() -eq "Patcher" -and $staticRule.Requirement.ToString() -eq "Required") "Static patcher identity/role is incorrect."
    Assert-True (@($store.Current.Rules | Where-Object Requirement -eq Optional).Count -eq 1) "Ref-AssemblyDefinition signature was not recognized."
    $referenceScannerType = $plugin.GetType("ServerManager.ReferencePluginPolicyScanner", $true)
    $scanner = (Get-Ctor $referenceScannerType ([Type[]]@([string], $limitsType))).Invoke([object[]]@([string]$policyRoot, $limits))
    $scan = $referenceScannerType.GetMethod("Scan", $instanceAll).Invoke($scanner, [object[]]@([Threading.CancellationToken]::None))
    Assert-True (@(Get-Hidden $scan "SkippedLibraries").Count -eq 2) "Helpers/dependencies were not reported as skipped libraries."
    $snapshot = $store.Current
    Copy-Item -LiteralPath (Join-Path $policyRoot "required\renamed\Anything.dll") -Destination (Join-Path $policyRoot "optional\Conflict.dll")
    $conflict = $store.TryReload()
    Assert-Code $conflict.Diagnostics "policy.source.role_conflict" "Patcher role conflict was accepted."
    Assert-True (-not $conflict.Success -and $conflict.KeptPreviousSnapshot -and
        [object]::ReferenceEquals($snapshot, $store.Current)) "Failed patcher reload replaced the last-known-good policy."

    $dualRoot = Join-Path $testRoot "dual"
    $dualStore = New-Store $dualRoot $limits
    $dualPath = Join-Path $dualRoot "required\Dual.dll"
    $dualId = Get-PatcherId "SmPatcherFixture.Dual" "First"
    New-Fixture $dualPath "SmPatcherFixture.Dual" @("First", "Second") -PluginGuid $dualId
    Assert-True ($dualStore.TryReload().Success -and $dualStore.Current.Rules.Count -eq 3) "Multiple patcher types or dual-role DLL records were lost."
    Assert-True (@($dualStore.Current.Rules | Where-Object PluginGuid -eq $dualId).Count -eq 2) "Real scanner conflated a plugin GUID and same-text patcher ID."
    $tinyStore = New-Store $dualRoot (New-Limits 2)
    Assert-True (-not $tinyStore.TryReload().Success -and $null -eq $tinyStore.Current) "Combined plugin/patcher policy limits were bypassed."

    $installedRoot = Join-Path $testRoot "installed"
    $installedPath = Join-Path $installedRoot "nested\Renamed.dll"
    New-Fixture $installedPath "SmPatcherFixture.Installed" @("Main")
    $expectedId = Get-PatcherId "SmPatcherFixture.Installed" "Main"
    New-Fixture (Join-Path $installedRoot "helper.dll") "SmPatcherFixture.ClientHelper" @("Helper") -FakeHelper
    $nativeHelper = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) "version.dll"
    if (Test-Path -LiteralPath $nativeHelper -PathType Leaf) {
        Copy-Item -LiteralPath $nativeHelper -Destination (Join-Path $installedRoot "NativeDependency.dll")
    }
    else { Write-Host "SKIP: native Windows helper fixture unavailable." }
    $installed = Invoke-InstalledScan $installedRoot $limits
    Assert-True ((Get-Hidden $installed "Success") -and @(Get-Hidden $installed "Records").Count -eq 1) "Installed scanner classified a helper as a patcher."
    $build = Invoke-ClientBuild $installedRoot $limits
    Assert-True ($build.Success -and $build.Manifest.Entries.Count -eq 1 -and
        $build.Manifest.Entries[0].PluginGuid -eq $expectedId) "Client installed-patcher manifest is incorrect."
    $renamedPath = Join-Path $installedRoot "Other.dll"
    Move-Item -LiteralPath $installedPath -Destination $renamedPath
    Assert-True ((Invoke-ClientBuild $installedRoot $limits).Manifest.Entries[0].PluginGuid -eq $expectedId) "Patcher identity changed on file rename/move."
    Copy-Item -LiteralPath $renamedPath -Destination (Join-Path $installedRoot "Duplicate.dll")
    $duplicateBuild = Invoke-ClientBuild $installedRoot $limits
    Assert-True (-not $duplicateBuild.Success -and $null -eq $duplicateBuild.Manifest) "Duplicate installed identity produced a partial manifest."
    Remove-Item -LiteralPath (Join-Path $installedRoot "Duplicate.dll") -Force
    $malformed = Join-Path $installedRoot "Broken.dll"
    [IO.File]::WriteAllBytes($malformed, [byte[]]@(1, 2, 3, 4))
    $brokenScan = Invoke-InstalledScan $installedRoot $limits
    Assert-True (-not (Get-Hidden $brokenScan "Success") -and @(Get-Hidden $brokenScan "Records").Count -eq 0) "Malformed installed DLL was silently omitted."
    Assert-True (-not (Invoke-ClientBuild $installedRoot $limits).Success) "Malformed installed DLL produced successful admission evidence."
    Remove-Item -LiteralPath $malformed -Force

    # The plugin registry contains metadata only; the fixture DLL is never loaded.
    $registryPlugin = [Activator]::CreateInstance($pluginInfoType, $true)
    $metadataProperty = $pluginInfoType.GetProperty("Metadata", $instanceAll)
    $metadata = $metadataProperty.PropertyType.GetConstructor([Type[]]@([string], [string], [string])).Invoke(
        [object[]]@("tests.mixed.plugin", "Mixed plugin", "1.0.0"))
    $metadataProperty.SetValue($registryPlugin, $metadata, $null)
    $pluginInfoType.GetProperty("Location", $instanceAll).SetValue($registryPlugin, $dualPath, $null)
    $mixedRoot = Join-Path $testRoot "mixed-client"
    New-Fixture (Join-Path $mixedRoot "Patcher.dll") "SmPatcherFixture.Mixed" @("Main")
    $mixedBuild = Invoke-ClientBuild $mixedRoot (New-Limits 1) @($registryPlugin)
    Assert-True (-not $mixedBuild.Success -and $null -eq $mixedBuild.Manifest) "Combined client plugin/patcher count exceeded its limit."

    # Same-length content mutation with restored timestamps must still be rehashed.
    $freshRoot = Join-Path $testRoot "fresh"
    $freshPath = Join-Path $freshRoot "Fresh.dll"
    New-Fixture $freshPath "SmPatcherFixture.Fresh" @("Main")
    $stream = [IO.File]::Open($freshPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $stream.SetLength($stream.Length + 16) } finally { $stream.Dispose() }
    $before = Invoke-Preparation $freshRoot $limits
    Assert-True $before.Success "First asynchronous patcher preparation failed."
    $timestamp = [IO.File]::GetLastWriteTimeUtc($freshPath)
    $created = [IO.File]::GetCreationTimeUtc($freshPath)
    $length = (Get-Item -LiteralPath $freshPath).Length
    $stream = [IO.File]::Open($freshPath, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Position = $stream.Length - 1; $stream.WriteByte(1) } finally { $stream.Dispose() }
    [IO.File]::SetLastWriteTimeUtc($freshPath, $timestamp)
    [IO.File]::SetCreationTimeUtc($freshPath, $created)
    $after = Invoke-Preparation $freshRoot $limits
    Assert-True ($after.Success -and $after.Manifest.Entries[0].PluginGuid -eq $before.Manifest.Entries[0].PluginGuid -and
        $after.Manifest.Entries[0].FileSha256 -ne $before.Manifest.Entries[0].FileSha256 -and
        (Get-Item -LiteralPath $freshPath).Length -eq $length) "A later connection reused stale patcher hashes."

    $linkRoot = Join-Path $testRoot "links"
    [IO.Directory]::CreateDirectory($linkRoot) | Out-Null
    $junctionPath = Join-Path $linkRoot "linked"
    try {
        New-Item -ItemType Junction -Path $junctionPath -Target $freshRoot -ErrorAction Stop | Out-Null
        $junctionCreated = $true
    }
    catch { Write-Host "SKIP: junction fixture unavailable on this filesystem/account." }
    if ($junctionCreated) {
        $linkScan = Invoke-InstalledScan $linkRoot $limits
        Assert-True (-not (Get-Hidden $linkScan "Success") -and @(Get-Hidden $linkScan "Records").Count -eq 0) "Installed reparse-point traversal was accepted."
        Remove-Item -LiteralPath $junctionPath -Force
        $junctionCreated = $false
    }

    Assert-True (@([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
        $_.GetName().Name.StartsWith("SmPatcherFixture.", [StringComparison]::Ordinal)
    }).Count -eq 0) "Metadata-only fixtures were loaded into the CLR."
    Write-Host "Patcher integrity smoke passed: policy, kind isolation, rejection/audit privacy, wire schema 3, metadata-only discovery, bounds, failure retention and per-connection hashing."
}
finally {
    # Validate exact generated root before recursive cleanup; detach our junction first.
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolvedRoot.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot).StartsWith("ServerManager-PatcherSmoke-", [StringComparison]::Ordinal)) {
        if ($junctionCreated -and $junctionPath -and
            [IO.Path]::GetFullPath($junctionPath).StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $junctionPath -Force
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
