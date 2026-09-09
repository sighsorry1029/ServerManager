#requires -Version 7.0
# Native Steam-user destination construction reaches Splatform's ReadOnlySpan
# string conversion, which is unavailable in Windows PowerShell 5/.NET Framework.
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

function Get-TypeDefinition {
    param(
        $Assembly,
        [string]$FullName
    )

    return $Assembly.MainModule.Types |
        Where-Object FullName -eq $FullName |
        Select-Object -First 1
}

function Get-MethodDefinition {
    param(
        $Type,
        [string]$Name,
        [string]$ReturnType = "System.Void",
        [string[]]$ParameterTypes = @()
    )

    if ($null -eq $Type) {
        return $null
    }

    return $Type.Methods |
        Where-Object {
            if ($_.Name -ne $Name -or
                $_.ReturnType.FullName -ne $ReturnType -or
                $_.Parameters.Count -ne $ParameterTypes.Count) {
                return $false
            }

            for ($index = 0; $index -lt $ParameterTypes.Count; ++$index) {
                if ($_.Parameters[$index].ParameterType.FullName -ne
                    $ParameterTypes[$index]) {
                    return $false
                }
            }

            return $true
        } |
        Select-Object -First 1
}

function Get-HarmonyPatchType {
    param(
        $Assembly,
        [string]$TargetType,
        [string]$TargetMethod
    )

    foreach ($type in $Assembly.MainModule.Types) {
        foreach ($attribute in $type.CustomAttributes) {
            if ($attribute.AttributeType.FullName -ne
                "HarmonyLib.HarmonyPatch" -or
                $attribute.ConstructorArguments.Count -lt 2) {
                continue
            }

            $patchedType = [string]$attribute.ConstructorArguments[0].Value
            $patchedMethod = [string]$attribute.ConstructorArguments[1].Value
            if ($patchedType -eq $TargetType -and
                $patchedMethod -eq $TargetMethod) {
                return $type
            }
        }
    }

    return $null
}

function Get-HarmonyPatchTypes {
    param(
        $Assembly,
        [string]$TargetType,
        [string]$TargetMethod
    )

    return @($Assembly.MainModule.Types |
        Where-Object {
            foreach ($attribute in $_.CustomAttributes) {
                if ($attribute.AttributeType.FullName -ne
                    "HarmonyLib.HarmonyPatch" -or
                    $attribute.ConstructorArguments.Count -lt 2) {
                    continue
                }

                $patchedType = [string]$attribute.ConstructorArguments[0].Value
                $patchedMethod = [string]$attribute.ConstructorArguments[1].Value
                if ($patchedType -eq $TargetType -and
                    $patchedMethod -eq $TargetMethod) {
                    return $true
                }
            }

            return $false
        })
}

function Get-MethodCall {
    param(
        $Method,
        [string]$DeclaringType,
        [string]$MethodName
    )

    if ($null -eq $Method -or -not $Method.HasBody) {
        return $null
    }

    return $Method.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq $DeclaringType -and
            $_.Operand.Name -eq $MethodName
        } |
        Select-Object -First 1
}

function Get-PresentationInstructions {
    param($Method, [string]$DeclaringTypePrefix = 'ServerManager.ConnectionErrorPanelPresentation')

    # Follow only this presenter's own methods (including nested state/control
    # helpers). Splitting restoration or layout into a helper must not weaken
    # the lifecycle checks or force a test to depend on one large method.
    $pending = New-Object 'System.Collections.Generic.Queue[Mono.Cecil.MethodDefinition]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    if ($null -ne $Method) { $pending.Enqueue($Method) }
    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        if (-not $seen.Add($current.FullName) -or -not $current.HasBody) {
            continue
        }
        foreach ($instruction in $current.Body.Instructions) {
            $instruction
            if ($instruction.Operand -is [Mono.Cecil.MethodReference] -and
                $instruction.Operand.DeclaringType.FullName.StartsWith(
                    $DeclaringTypePrefix,
                    [StringComparison]::Ordinal)) {
                $called = $instruction.Operand.Resolve()
                if ($null -ne $called) { $pending.Enqueue($called) }
            }
        }
    }
}

function Test-InstructionReaches {
    param($Start, $Target)

    # Follow normal IL control flow, including leave instructions emitted for
    # a return from a catch. This detects a failure handler falling through to
    # another stage without depending on Debug/Release instruction layout.
    $pending = New-Object 'System.Collections.Generic.Queue[Mono.Cecil.Cil.Instruction]'
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    if ($null -ne $Start) { $pending.Enqueue($Start) }
    while ($pending.Count -gt 0) {
        $instruction = $pending.Dequeue()
        if (-not $seen.Add($instruction.Offset)) { continue }
        if ($instruction -eq $Target) { return $true }
        $flow = [string]$instruction.OpCode.FlowControl
        if ($flow -in @('Return', 'Throw') -or
            [string]$instruction.OpCode.Code -in @('Endfinally', 'Endfilter')) { continue }
        if ($flow -in @('Branch', 'Cond_Branch')) {
            foreach ($destination in @($instruction.Operand)) {
                if ($destination -is [Mono.Cecil.Cil.Instruction]) { $pending.Enqueue($destination) }
            }
            if ($flow -eq 'Branch') { continue }
        }
        if ($null -ne $instruction.Next) { $pending.Enqueue($instruction.Next) }
    }
    return $false
}

function Get-NestedTypeDefinitions {
    param($Type)

    $Type
    foreach ($nested in $Type.NestedTypes) {
        Get-NestedTypeDefinitions $nested
    }
}

function Test-ConstantIntSetter {
    param($Instructions, [string]$SetterName, [int]$Expected, [string]$DeclaringType = 'TMPro.TMP_Text')

    foreach ($instruction in $Instructions) {
        if ($instruction.Operand -isnot [Mono.Cecil.MethodReference] -or
            $instruction.Operand.DeclaringType.FullName -ne $DeclaringType -or
            $instruction.Operand.Name -ne $SetterName -or $null -eq $instruction.Previous) {
            continue
        }
        $argument = $instruction.Previous
        $codeName = [string]$argument.OpCode.Code
        $constant = $null
        if ($codeName -in @('Ldc_I4', 'Ldc_I4_S')) {
            $constant = [int]$argument.Operand
        }
        elseif ($codeName -eq 'Ldc_I4_M1') { $constant = -1 }
        elseif ($codeName -match '^Ldc_I4_([0-8])$') { $constant = [int]$Matches[1] }
        if ($null -ne $constant -and $constant -eq $Expected) { return $true }
    }
    return $false
}

function Assert-InactiveTmpCreation {
    param($Method)

    # AddComponent can run TMP Awake immediately on an active GameObject.
    # Both owned labels must borrow the vanilla font before that lifecycle starts.
    Assert-True ($null -ne $Method -and $Method.HasBody) 'The owned TMP text creator is missing.'
    $instructions = @($Method.Body.Instructions)
    $constructor = Get-MethodCall $Method 'UnityEngine.GameObject' '.ctor'
    $component = $instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.GenericInstanceMethod] -and
        $_.Operand.DeclaringType.FullName -eq 'UnityEngine.GameObject' -and
        $_.Operand.Name -eq 'AddComponent' -and
        $_.Operand.GenericArguments.Count -eq 1 -and
        $_.Operand.GenericArguments[0].FullName -eq 'TMPro.TextMeshProUGUI'
    } | Select-Object -First 1
    $activation = @($instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq 'UnityEngine.GameObject' -and
        $_.Operand.Name -eq 'SetActive'
    })
    $parent = Get-MethodCall $Method 'UnityEngine.Transform' 'SetParent'
    Assert-True ($null -ne $constructor -and $null -ne $component -and $null -ne $parent -and
        $activation.Count -eq 2 -and
        (Test-ConstantIntSetter @($activation[0]) 'SetActive' 0 'UnityEngine.GameObject') -and
        (Test-ConstantIntSetter @($activation[1]) 'SetActive' 1 'UnityEngine.GameObject')) `
        ($Method.FullName + ' must deactivate its new object, add TMP separately, then activate it exactly once.')
    $constructorTypes = @($instructions | Where-Object {
        $_.Offset -lt $constructor.Offset -and $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldtoken -and
        $_.Operand -is [Mono.Cecil.TypeReference]
    } | ForEach-Object { $_.Operand.FullName })
    Assert-True ($constructorTypes.Count -eq 1 -and $constructorTypes[0] -eq 'UnityEngine.RectTransform' -and
        $constructor.Offset -lt $activation[0].Offset -and
        $activation[0].Offset -lt $parent.Offset -and $parent.Offset -lt $component.Offset -and
        $component.Offset -lt $activation[1].Offset) `
        ($Method.FullName + ' can initialize TMP before deactivation and parenting.')
    foreach ($setter in @('set_font', 'set_fontSharedMaterial')) {
        $assignment = Get-MethodCall $Method 'TMPro.TMP_Text' $setter
        Assert-True ($null -ne $assignment -and $component.Offset -lt $assignment.Offset -and
            $assignment.Offset -lt $activation[1].Offset) `
            ($Method.FullName + ' activates TMP before assigning its vanilla ' + $setter + '.')
    }
}

function Get-HarmonyDelegation {
    param(
        $Assembly,
        [string]$TargetType,
        [string]$TargetMethod,
        [string]$PatchMethod,
        [string]$DelegateType,
        [string]$DelegateMethod
    )

    foreach ($patchType in @(Get-HarmonyPatchTypes `
            $Assembly `
            $TargetType `
            $TargetMethod)) {
        $method = $patchType.Methods |
            Where-Object Name -eq $PatchMethod |
            Select-Object -First 1
        if ($null -ne (Get-MethodCall `
                $method `
                $DelegateType `
                $DelegateMethod)) {
            return $method
        }
    }

    return $null
}

function Get-BrandingCall {
    param($Method)

    if ($null -eq $Method -or -not $Method.HasBody) {
        return $null
    }

    return $Method.Body.Instructions |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ClientMenuBranding"
        } |
        Select-Object -First 1
}

function Assert-HarmonyDelegation {
    param(
        $Assembly,
        [string]$TargetType,
        [string]$TargetMethod,
        [string]$PatchMethod,
        [bool]$MustPreserveOriginal = $false
    )

    $patchType = Get-HarmonyPatchType `
        $Assembly `
        $TargetType `
        $TargetMethod
    Assert-True ($null -ne $patchType) `
        "Missing Harmony patch for $TargetType.$TargetMethod."

    $method = $patchType.Methods |
        Where-Object Name -eq $PatchMethod |
        Select-Object -First 1
    Assert-True ($null -ne $method -and $method.HasBody) `
        "The $TargetType.$TargetMethod patch has no $PatchMethod body."
    Assert-True ($null -ne (Get-BrandingCall $method)) `
        "The $TargetType.$TargetMethod patch no longer delegates to ClientMenuBranding."

    if ($MustPreserveOriginal) {
        Assert-True ($method.ReturnType.FullName -eq "System.Void") `
            "$TargetType.$TargetMethod must augment, not skip, the vanilla method."
    }

    return $method
}

function Get-LoadedType {
    param(
        [Reflection.Assembly]$Assembly,
        [string]$FullName
    )

    $type = $Assembly.GetType($FullName, $false)
    Assert-True ($null -ne $type) "Missing runtime type $FullName."
    return $type
}

function Get-StaticMethod {
    param(
        [Type]$Type,
        [string]$Name,
        [int]$ParameterCount
    )

    $flags = [Reflection.BindingFlags]::Static -bor
        [Reflection.BindingFlags]::Public -bor
        [Reflection.BindingFlags]::NonPublic
    $method = $Type.GetMethods($flags) |
        Where-Object {
            $_.Name -eq $Name -and
            $_.GetParameters().Count -eq $ParameterCount
        } |
        Select-Object -First 1
    Assert-True ($null -ne $method) `
        "Missing pure helper $($Type.FullName).$Name/$ParameterCount."
    return $method
}

function Invoke-TryMethod {
    param(
        [Reflection.MethodInfo]$Method,
        [object[]]$Arguments
    )

    $result = $Method.Invoke($null, $Arguments)
    return [pscustomobject]@{
        Succeeded = [bool]$result
        Arguments = $Arguments
    }
}

function Assert-Near {
    param(
        [double]$Actual,
        [double]$Expected,
        [string]$Message,
        [double]$Tolerance = 0.001
    )

    Assert-True ([Math]::Abs($Actual - $Expected) -le $Tolerance) `
        ("$Message Expected $Expected, got $Actual.")
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$pluginSourcePath = Join-Path $projectRoot "Plugin.cs"
$brandingSourcePath = Join-Path $projectRoot "ClientMenuBranding.cs"
$gameAssemblyPath = Join-Path $GamePath `
    "valheim_Data\Managed\assembly_valheim.dll"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"

Assert-True (Test-Path -LiteralPath $pluginPath) `
    "Build ServerManager before running this smoke test."
Assert-True (Test-Path -LiteralPath $pluginSourcePath) `
    "Plugin.cs was not found."
Assert-True (Test-Path -LiteralPath $brandingSourcePath) `
    "ClientMenuBranding.cs was not found."
Assert-True (Test-Path -LiteralPath $gameAssemblyPath) `
    "The Valheim runtime assembly was not found."
Assert-True (Test-Path -LiteralPath $cecilPath) `
    "Mono.Cecil from BepInEx was not found."

[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
$gameDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $gameAssemblyPath)
$pluginDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    $pluginPath)

# Fail loudly when a Valheim update changes one of the menu/network hook
# signatures this feature relies on.
$fejdStartup = Get-TypeDefinition $gameDefinition "FejdStartup"
Assert-True ($null -ne $fejdStartup) "The installed FejdStartup type was not found."
foreach ($methodName in @(
    "SetupGui",
    "OnStartGame",
    "UpdateCharacterList",
    "OnCharacterStart",
    "OnSelelectCharacterBack",
    "OnConnectionFailedOk",
    "OnDestroy")) {
    Assert-True (
        $null -ne (Get-MethodDefinition $fejdStartup $methodName)) `
        "FejdStartup.$methodName() changed."
}

$zNet = Get-TypeDefinition $gameDefinition "ZNet"
$clientHandshake = Get-MethodDefinition `
    $zNet `
    "RPC_ClientHandshake" `
    "System.Void" `
    @("ZRpc", "System.Boolean", "System.String")
Assert-True ($null -ne $clientHandshake) `
    "ZNet.RPC_ClientHandshake(ZRpc,bool,string) changed."

$serverJoinData = Get-TypeDefinition $gameDefinition "ServerJoinData"
$queuedJoinValid = $serverJoinData.Properties |
    Where-Object {
        $_.Name -eq "IsValid" -and
        $_.PropertyType.FullName -eq "System.Boolean"
    } |
    Select-Object -First 1
$dedicatedJoinData = Get-TypeDefinition `
    $gameDefinition `
    "ServerJoinDataDedicated"
$dedicatedConstructor = $dedicatedJoinData.Methods |
    Where-Object {
        $_.IsConstructor -and
        $_.Parameters.Count -eq 2 -and
        $_.Parameters[0].ParameterType.FullName -eq "System.String" -and
        $_.Parameters[1].ParameterType.FullName -eq "System.UInt16"
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $queuedJoinValid -and
    $null -ne $dedicatedConstructor) `
    "The dedicated queued-join surface changed."
$steamUserJoinData = Get-TypeDefinition $gameDefinition 'ServerJoinDataSteamUser'
$steamUserConstructor = Get-MethodDefinition $steamUserJoinData '.ctor' 'System.Void' @('System.UInt64')
$steamDestinationConstructor = Get-MethodDefinition $serverJoinData '.ctor' 'System.Void' @('ServerJoinDataSteamUser')
$steamJoinField = $steamUserJoinData.Fields | Where-Object {
    $_.Name -eq 'm_joinUserID' -and $_.FieldType.FullName -eq 'Steamworks.CSteamID'
} | Select-Object -First 1
$destinationOwner = $serverJoinData.Fields | Where-Object {
    $_.Name -eq 'm_owner' -and $_.FieldType.FullName -eq 'Splatform.PlatformUserID'
} | Select-Object -First 1
Assert-True ($null -ne $steamUserConstructor -and $steamUserConstructor.IsPublic -and
    $null -ne $steamDestinationConstructor -and $steamDestinationConstructor.IsPublic -and
    $null -ne $steamJoinField -and $null -ne $destinationOwner) `
    'The native Steam-user queued destination constructor or owner surface changed.'
$nativeJoinServer = Get-MethodDefinition $fejdStartup 'JoinServer'
Assert-True ($null -ne (Get-MethodCall $nativeJoinServer 'ServerJoinData' 'get_SteamUser') -and
    @($nativeJoinServer.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'ZNet' -and
        $_.Operand.Name -eq 'SetServerHost' -and $_.Operand.Parameters.Count -eq 1 -and
        $_.Operand.Parameters[0].ParameterType.FullName -eq 'System.UInt64'
    }).Count -eq 1) 'Native JoinServer no longer dispatches a Steam-user destination to the Steam64 host overload.'

$pluginType = Get-TypeDefinition `
    $pluginDefinition `
    "ServerManager.ServerManagerPlugin"
$brandingType = Get-TypeDefinition `
    $pluginDefinition `
    "ServerManager.ClientMenuBranding"
Assert-True ($null -ne $pluginType) "ServerManagerPlugin was not found."
Assert-True ($null -ne $brandingType) "ClientMenuBranding was not built."

$expectedConfiguration = [ordered]@{
    BrandingServerAddress = "System.String"
    BrandingServerPassword = "System.String"
    BrandingButtonText = "System.String"
    BrandingLogoPath = "System.String"
    ShowEventNotifications = "System.Boolean"
}
foreach ($entry in $expectedConfiguration.GetEnumerator()) {
    $property = $pluginType.Properties |
        Where-Object Name -eq $entry.Key |
        Select-Object -First 1
    Assert-True ($null -ne $property) `
        "Missing client-branding config property $($entry.Key)."
    Assert-True (
        $property.PropertyType.FullName -eq
        "BepInEx.Configuration.ConfigEntry``1<$($entry.Value)>") `
        "Client-branding config property $($entry.Key) has the wrong type."
}
Assert-True (@($pluginType.Properties | Where-Object {
    $_.PropertyType.FullName.StartsWith('BepInEx.Configuration.ConfigEntry`1<', [StringComparison]::Ordinal)
}).Count -eq 5 -and $null -eq ($pluginType.Properties | Where-Object Name -eq 'EnableClientBranding' | Select-Object -First 1)) `
    'The compiled plugin must expose exactly the five client entries without an Enabled toggle.'

$pluginSource = [IO.File]::ReadAllText($pluginSourcePath)
foreach ($configKey in @(
    '"Server Address"',
    '"Server Password"',
    '"Server Button Text"',
    '"Logo Path"',
    '"Show Event Notifications"')) {
    Assert-True ($pluginSource.Contains($configKey)) `
        "Client-branding config key $configKey was not bound."
}
Assert-True ($pluginSource.Contains('"https://i.ibb.co/23XsG7tz/download.png"') -and
    $pluginSource.Contains('"Start Modded Valheim Server"') -and
    -not $pluginSource.Contains('"Logo Source"') -and -not $pluginSource.Contains('"Logo URL"')) `
    'The client branding defaults or the Logo Path setting name changed.'
$applyLogo = Get-MethodDefinition $brandingType 'ApplyLogo' 'System.Void' @('FejdStartup')
Assert-True ($null -ne (Get-MethodCall $applyLogo 'BepInEx.Paths' 'get_BepInExRootPath') -and
    $null -eq (Get-MethodCall $applyLogo 'BepInEx.Paths' 'get_ConfigPath')) `
    'Runtime logo loading is not rooted at BepInEx or still silently prepends config/ServerManager.'
$logoLoadInstructions = @(Get-PresentationInstructions $applyLogo 'ServerManager.ClientMenuBranding')
$logoLoadCalls = @($logoLoadInstructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
foreach ($directoryScan in @('GetFiles', 'EnumerateFiles', 'GetDirectories', 'EnumerateDirectories')) {
    Assert-True ($logoLoadCalls -notcontains $directoryScan) `
        ('Logo loading recursively searches for assets instead of loading the explicit configured path: ' + $directoryScan)
}
Assert-True (
    $pluginSource.Contains('"1 - Client"') -and
    -not $pluginSource.Contains('"4 - Client branding"') -and
    -not $pluginSource.Contains('"5 - Client notifications"') -and
    -not $pluginSource.Contains('"Enabled"')) `
    'Client settings must use only 1 - Client with no legacy section or Enabled binding.'
Assert-True ($null -eq ($brandingType.Methods | Where-Object Name -eq 'IsEnabled' | Select-Object -First 1)) `
    'Client branding must not retain the removed Enabled runtime gate.'
Assert-True (
    -not $pluginSource.Contains('"Allow Alt Bypass"') -and
    $null -eq ($pluginType.Properties |
        Where-Object Name -eq "AllowClientBrandingAltBypass" |
        Select-Object -First 1)) `
    "Alt menu bypass must remain available without a configurable disable switch."

$altBypass = Get-MethodDefinition $brandingType "IsAltBypassPressed" "System.Boolean"
Assert-True ($null -ne $altBypass) "The unconditional Alt menu escape is missing."
$altKeyCalls = @($altBypass.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq "ZInput" -and
        $_.Operand.Name -eq "GetKey"
    })
Assert-True ($altKeyCalls.Count -eq 2) `
    "Alt bypass must still check both keyboard Alt keys."
Assert-True ($null -eq ($altBypass.Body.Instructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq $pluginType.FullName
    } |
    Select-Object -First 1)) `
    "The Alt key escape is still gated by plugin configuration."
foreach ($startMethodName in @("BeforeMainStart", "BeforeCharacterStart")) {
    $startMethod = Get-MethodDefinition $brandingType $startMethodName `
        "System.Void" @("FejdStartup")
    Assert-True ($null -ne (Get-MethodCall $startMethod `
        $brandingType.FullName "IsAltBypassPressed")) `
        "$startMethodName no longer offers the ordinary world/server picker via Alt."
}

# The three general tips and menu cleanup belong to every client main menu.
# Only the Alt/world-picker hint depends on having a
# configured branded destination; the existing Alt escape remains untouched.
$menuGuideType = $brandingType.NestedTypes |
    Where-Object Name -eq 'MenuGuide' |
    Select-Object -First 1
Assert-True ($null -ne $menuGuideType) 'The client-only menu guide was not built.'
$menuTextPanelType = $brandingType.NestedTypes | Where-Object Name -eq 'MenuTextPanel' | Select-Object -First 1
Assert-True ($null -ne $menuTextPanelType) 'Both menu columns must share their bounded scrollable text layout.'
$menuGuideMethods = @{}
foreach ($guideMethodName in @('Update', 'Dispose', 'Reflow', 'SetVisible')) {
    $guideMethod = $menuGuideType.Methods | Where-Object Name -eq $guideMethodName | Select-Object -First 1
    Assert-True ($null -ne $guideMethod -and $guideMethod.HasBody) `
        ('The main-menu guide is missing a layout/lifecycle method: ' + $guideMethodName)
    $menuGuideMethods[$guideMethodName] = $guideMethod
}
foreach ($guideUpdateCaller in @('OnSetupGui', 'OnUiUpdate', 'AfterLanguageChanged')) {
    $caller = $brandingType.Methods | Where-Object Name -eq $guideUpdateCaller | Select-Object -First 1
    Assert-True ($null -ne (Get-MethodCall $caller $brandingType.FullName 'UpdateMenuGuide')) `
        ($guideUpdateCaller + ' no longer refreshes the guide when the menu or language changes.')
    if ($guideUpdateCaller -in @('OnSetupGui', 'AfterLanguageChanged')) {
        $guideCall = Get-MethodCall $caller $brandingType.FullName 'UpdateMenuGuide'
        $clientGuard = Get-MethodCall $caller $brandingType.FullName 'CanUseClientUi'
        $endpointGate = Get-MethodCall $caller $brandingType.FullName 'TryGetConfiguredEndpoint'
        Assert-True ($null -ne $clientGuard -and $clientGuard.Offset -lt $guideCall.Offset -and
            $null -ne $endpointGate -and $guideCall.Offset -lt $endpointGate.Offset) `
            ($guideUpdateCaller + ' must keep the guide client-only and independent of a configured destination.')
    }
}
$setupGui = Get-MethodDefinition $brandingType 'OnSetupGui' 'System.Void' @('FejdStartup')
$setupLogo = Get-MethodCall $setupGui $brandingType.FullName 'ApplyLogo'
$setupClientGuard = Get-MethodCall $setupGui $brandingType.FullName 'CanUseClientUi'
$setupEndpointGate = Get-MethodCall $setupGui $brandingType.FullName 'TryGetConfiguredEndpoint'
Assert-True ($null -ne $setupLogo -and $null -ne $setupClientGuard -and $null -ne $setupEndpointGate -and
    $setupClientGuard.Offset -lt $setupLogo.Offset -and $setupLogo.Offset -lt $setupEndpointGate.Offset) `
    'Logo application must be enabled for every client menu even when the quick-connect address is empty.'
foreach ($clientMethodName in @('BeforeMainStart', 'BeforeCharacterStart', 'AfterMainStart', 'AfterCharacterListUpdated')) {
    $clientMethod = Get-MethodDefinition $brandingType $clientMethodName 'System.Void' @('FejdStartup')
    Assert-True ($null -ne (Get-MethodCall $clientMethod $brandingType.FullName 'CanUseClientUi')) `
        ($clientMethodName + ' lost its client-only guard while removing the Enabled toggle.')
}
foreach ($guideDisposeCaller in @('ObserveStartup', 'BeforeStartupDestroyed', 'Shutdown')) {
    $caller = $brandingType.Methods | Where-Object Name -eq $guideDisposeCaller | Select-Object -First 1
    $calls = @(Get-PresentationInstructions $caller 'ServerManager.ClientMenuBranding' |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
        ForEach-Object Operand)
    Assert-True (@($calls | Where-Object { $_.DeclaringType.FullName -eq $menuGuideType.FullName -and $_.Name -eq 'Dispose' }).Count -gt 0) `
        ($guideDisposeCaller + ' can leave menu guide controls or hidden vanilla content behind.')
}
$updateMenuGuide = $brandingType.Methods | Where-Object Name -eq 'UpdateMenuGuide' | Select-Object -First 1
Assert-True ($null -ne $updateMenuGuide -and
    $null -ne (Get-MethodCall $updateMenuGuide $brandingType.FullName 'CanUseClientUi') -and
    $null -eq (Get-MethodCall $updateMenuGuide $brandingType.FullName 'IsEnabled')) `
    'The guide either lost its client-only guard or is still incorrectly gated by branding.'
$guideInstructions = @(@((Get-NestedTypeDefinitions $menuGuideType); (Get-NestedTypeDefinitions $menuTextPanelType)) |
    ForEach-Object { $_.Methods } | Where-Object HasBody | ForEach-Object { $_.Body.Instructions })
$guideStrings = @($guideInstructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr } |
    ForEach-Object { [string]$_.Operand })
foreach ($guideTranslation in @('sm_menu_guide_title', 'sm_menu_guide_character', 'sm_menu_guide_mods', 'sm_menu_guide_update', 'sm_menu_guide_worlds')) {
    Assert-True ($guideStrings -contains $guideTranslation) `
        ('The guide lost a localized general or conditional world-picker tip: ' + $guideTranslation)
}
$guideUpdateInstructions = @($menuGuideMethods['Update'].Body.Instructions)
$hintCondition = Get-MethodCall $menuGuideMethods['Update'] $brandingType.FullName 'ShouldShowWorldMenuHint'
$worldHintText = $guideUpdateInstructions | Where-Object {
    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and $_.Operand -ceq 'sm_menu_guide_worlds'
} | Select-Object -First 1
Assert-True ($null -ne $hintCondition -and $null -ne $worldHintText -and
    $hintCondition.Offset -lt $worldHintText.Offset) `
    'The world-picker tip is no longer guarded by the valid branding destination policy.'
foreach ($generalTip in @('sm_menu_guide_character', 'sm_menu_guide_mods', 'sm_menu_guide_update')) {
    $tipLoad = $guideUpdateInstructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and $_.Operand -ceq $generalTip
    } | Select-Object -First 1
    Assert-True ($null -ne $tipLoad -and $tipLoad.Offset -lt $hintCondition.Offset) `
        ('A general menu tip became conditional on branding: ' + $generalTip)
}
$hintSkip = $guideUpdateInstructions | Where-Object {
    $_.OpCode.FlowControl -eq [Mono.Cecil.Cil.FlowControl]::Cond_Branch -and
    $_.Operand -is [Mono.Cecil.Cil.Instruction] -and
    $_.Offset -gt $hintCondition.Offset -and $_.Offset -lt $worldHintText.Offset -and
    $_.Operand.Offset -gt $worldHintText.Offset
} | Select-Object -First 1
Assert-True ($null -ne $hintSkip) 'The world-picker hint is appended unconditionally even when its display predicate is false.'
foreach ($hintCache in @('_hintEndpoint')) {
    $reads = @($guideUpdateInstructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldfld -and $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq $hintCache
    })
    $writes = @($guideUpdateInstructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stfld -and $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq $hintCache
    })
    Assert-True ($reads.Count -gt 0 -and $writes.Count -gt 0) `
        ('The menu guide cannot refresh the conditional hint when client settings change: ' + $hintCache)
}
Assert-True ($null -eq ($menuGuideType.Fields | Where-Object Name -eq '_hintBrandingEnabled' | Select-Object -First 1)) `
    'The world-picker hint must not cache the removed branding toggle.'
Assert-True ($null -eq ($guideInstructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Instantiate'
} | Select-Object -First 1)) 'The guide clones existing controls instead of owning its temporary UI.'
$guideCanShow = $menuGuideType.Methods | Where-Object Name -eq 'CanShow' | Select-Object -First 1
Assert-True ($null -ne $guideCanShow -and $guideCanShow.HasBody) 'The menu guide has no explicit main-menu/modal visibility boundary.'
$guideVisibilityFields = @($guideCanShow.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] } | ForEach-Object { $_.Operand.Name })
foreach ($visibleGuard in @('m_mainMenu', 'm_menuList', 'm_connectionFailedPanel', 'm_worldVersionPanel',
    'm_playerVersionPanel', 'm_newGameVersionPanel', 'm_ndaPanel', 'm_loading', 'm_pleaseWait')) {
    Assert-True ($guideVisibilityFields -contains $visibleGuard) `
        ('The guide can overlap a non-main-menu state or modal: ' + $visibleGuard)
}
foreach ($popupType in @('UnifiedPopup', 'Feedback')) {
    Assert-True ($null -ne (Get-MethodCall $guideCanShow $popupType 'IsVisible')) `
        ('The menu guide no longer respects the visible ' + $popupType + ' overlay.')
}
$guideCapture = $menuGuideType.Methods | Where-Object Name -eq 'CaptureTargets' | Select-Object -First 1
$guideCaptureInstructions = @(Get-PresentationInstructions $guideCapture 'ServerManager.ClientMenuBranding')
$guideCaptureMembers = @($guideCaptureInstructions |
    Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -or $_.Operand -is [Mono.Cecil.MethodReference] } |
    ForEach-Object { $_.Operand.Name })
foreach ($targetContract in @('m_patchLogScroll', 'm_textField', 'm_showPlayerLog', 'm_moddedText',
    'GetPersistentEventCount', 'GetPersistentTarget', 'GetPersistentMethodName', 'IsChildOf', 'get_activeSelf')) {
    Assert-True ($guideCaptureMembers -contains $targetContract) `
        ('The guide lost an exact vanilla target or its original visibility snapshot: ' + $targetContract)
}
Assert-True ($guideStrings -contains 'OnMerchStoreButton' -and $guideStrings -notcontains 'TopRight') `
    'The guide targets a shared TopRight container instead of the verified merch button.'
$guideSafeTarget = $menuGuideType.Methods | Where-Object Name -eq 'IsSafeTarget' | Select-Object -First 1
$safeTargetFields = @($guideSafeTarget.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.FieldReference]
} | ForEach-Object { $_.Operand.Name })
$safeTargetStrings = @($guideSafeTarget.Body.Instructions | Where-Object {
    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr
} | ForEach-Object { [string]$_.Operand })
Assert-True ($safeTargetFields -notcontains '_logoImage' -and $safeTargetStrings -contains 'Logo/LOGO') `
    'Vanilla logo/ancestor protection still depends on the optional branding logo capture.'
$guideRestore = $menuGuideType.Methods | Where-Object Name -eq 'RestoreTargets' | Select-Object -First 1
$guideRestoreCalls = @($guideRestore.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
foreach ($restoreTargetCall in @('get_Value', 'SetActive', 'Clear')) {
    Assert-True ($guideRestoreCalls -contains $restoreTargetCall) `
        ('Leaving the main menu does not restore each exact previous visibility: ' + $restoreTargetCall)
}
Assert-True ($null -ne (Get-MethodCall $menuGuideMethods['SetVisible'] $menuGuideType.FullName 'RestoreTargets') -and
    $null -ne (Get-MethodCall $menuGuideMethods['Dispose'] $menuGuideType.FullName 'RestoreTargets')) `
    'Hiding or disposing the guide leaves vanilla changelog/modded/merch controls hidden.'
foreach ($guideDestroy in @($guideInstructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Destroy'
})) {
    Assert-True ($null -ne $guideDestroy.Previous -and
        $guideDestroy.Previous.Operand -is [Mono.Cecil.FieldReference] -and
        $guideDestroy.Previous.Operand.Name -eq '_root') `
        'The guide can destroy a vanilla object rather than only its owned root.'
}
Assert-True ($null -ne (Get-MethodCall $menuGuideMethods['Reflow'] $menuTextPanelType.FullName 'Reflow')) `
    'The guide lost its shared text layout delegation.'
$textPanelReflow = $menuTextPanelType.Methods | Where-Object Name -eq 'Reflow' | Select-Object -First 1
$guideReflowCalls = @($textPanelReflow.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
foreach ($guideLayoutCall in @('GetPreferredValues', 'CalculateGuideSize', 'set_sizeDelta', 'ForceMeshUpdate',
    'set_vertical', 'set_verticalNormalizedPosition')) {
    Assert-True ($guideReflowCalls -contains $guideLayoutCall) `
        ('The guide cannot reflow long translations with a bounded scrollable viewport: ' + $guideLayoutCall)
}
$guideCreateText = $menuTextPanelType.Methods | Where-Object Name -eq 'CreateText' | Select-Object -First 1
Assert-InactiveTmpCreation $guideCreateText
$guideCreateTextInstructions = @($guideCreateText.Body.Instructions)
foreach ($optionalCall in @('Tick', 'get_DisplayRevision', 'FormatOptionalList', 'CaptureLoadedOptionalModVersions', 'SetText', 'UpdateLayout')) {
    $optionalUpdate = $menuGuideType.Methods | Where-Object Name -eq 'UpdateOptionalList' | Select-Object -First 1
    Assert-True (@($optionalUpdate.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $optionalCall
    }).Count -gt 0) ('Optional list lost its result-driven presentation: ' + $optionalCall)
}
foreach ($queryTypeName in @('ServerManager.OptionalModQuery', 'ServerManager.OptionalModLobbyQuery')) {
    foreach ($queryCall in @('Tick', 'get_State', 'get_IsStale', 'get_DisplayRevision', 'get_Entries')) {
        Assert-True ($null -ne (Get-MethodCall $optionalUpdate $queryTypeName $queryCall)) `
            ('The optional preview lost its transport-specific result boundary: ' + $queryTypeName + '.' + $queryCall)
    }
    Assert-True ($null -ne (Get-MethodCall $optionalUpdate $queryTypeName 'Pause') -and
        $null -ne (Get-MethodCall $optionalUpdate $queryTypeName 'Dispose')) `
        ('Changing transport or clearing/failing the preview can leave an old query active: ' + $queryTypeName)
}
$optionalUpdateStrings = @($optionalUpdate.Body.Instructions | Where-Object {
    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr
} | ForEach-Object { [string]$_.Operand })
Assert-True ($optionalUpdateStrings -contains 'steam:' -and $optionalUpdateStrings -contains 'sm_menu_optional_host_help' -and
    $null -ne (Get-MethodCall $optionalUpdate 'System.String' 'StartsWith')) `
    'The optional preview lost explicit Steam-host transport selection or its unavailable-host guidance.'
$optionalModeReads = @($optionalUpdate.Body.Instructions | Where-Object {
    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldfld -and $_.Operand -is [Mono.Cecil.FieldReference] -and
    $_.Operand.Name -eq '_optionalLobbyMode'
})
$optionalModeWrites = @($optionalUpdate.Body.Instructions | Where-Object {
    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stfld -and $_.Operand -is [Mono.Cecil.FieldReference] -and
    $_.Operand.Name -eq '_optionalLobbyMode'
})
Assert-True ($optionalModeReads.Count -gt 0 -and $optionalModeWrites.Count -gt 0) `
    'The optional preview no longer tracks transport changes independently of equal display revisions.'
$loadedCapture = $brandingType.Methods | Where-Object Name -eq 'CaptureLoadedOptionalModVersions' | Select-Object -First 1
$loadedCaptureCalls = @($loadedCapture.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference]
} | ForEach-Object { $_.Operand.Name })
foreach ($metadataCall in @('get_PluginInfos', 'get_Metadata', 'get_GUID', 'get_Version', 'get_Instance', 'TryNormalizeGuid')) {
    Assert-True ($loadedCaptureCalls -contains $metadataCall) `
        ('Loaded optional filter lost its loaded-instance/metadata boundary: ' + $metadataCall)
}
Assert-True (@($loadedCaptureCalls | Where-Object { $_ -in @('Scan', 'ComputeHash', 'LoadFile', 'ReadAllBytes', 'GetFiles') }).Count -eq 0) `
    'The optional menu filter must not scan DLLs or repeat admission hashing.'
foreach ($queryTypeName in @('ServerManager.OptionalModQuery', 'ServerManager.OptionalModLobbyQuery')) {
    Assert-True ($null -ne (Get-MethodCall $menuGuideMethods['SetVisible'] $queryTypeName 'Pause') -and
        $null -ne (Get-MethodCall $menuGuideMethods['Dispose'] $queryTypeName 'Dispose')) `
        ('Hidden/destroyed menu must cancel its query before releasing the owner: ' + $queryTypeName)
}
Assert-True ($null -ne (Get-MethodCall $menuGuideMethods['Update'] $menuGuideType.FullName 'CanShow') -and
    $null -ne (Get-MethodCall $menuGuideMethods['Update'] $menuGuideType.FullName 'UpdateOptionalList')) `
    'The optional preview lost the existing main-menu/modal visibility boundary.'
Assert-True (Test-ConstantIntSetter $guideCreateTextInstructions 'set_richText' 0) `
    'Menu guidance can execute markup supplied through translation overrides.'
Assert-True (Test-ConstantIntSetter $guideCreateTextInstructions 'set_enableAutoSizing' 0) `
    'Long guidance is shrunk instead of being kept readable in the scrollable viewport.'

# These prefixes only arm/set up the branded path. They must not replace the
# vanilla character-selection or join implementations.
Assert-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "SetupGui" `
    "Postfix" |
    Out-Null
Assert-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "Update" `
    "Postfix" |
    Out-Null
Assert-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "OnDestroy" `
    "Prefix" `
    $true |
    Out-Null
$startupDestroyPatchOwners = @(Get-HarmonyPatchTypes `
    $pluginDefinition `
    "FejdStartup" `
    "OnDestroy")
Assert-True ($startupDestroyPatchOwners.Count -eq 1) `
    "FejdStartup.OnDestroy has more than one ServerManager patch owner."
Assert-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "OnStartGame" `
    "Prefix" `
    $true |
    Out-Null
Assert-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "UpdateCharacterList" `
    "Postfix" |
    Out-Null
Assert-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "OnCharacterStart" `
    "Prefix" `
    $true |
    Out-Null
Assert-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "OnCharacterStart" `
    "Finalizer" |
    Out-Null
Assert-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "TransitionToMainScene" `
    "Prefix" `
    $true |
    Out-Null
Assert-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "OnSelelectCharacterBack" `
    "Postfix" |
    Out-Null
Assert-HarmonyDelegation `
    $pluginDefinition `
    "ZNet" `
    "RPC_ClientHandshake" `
    "Prefix" `
    $true |
    Out-Null
Assert-HarmonyDelegation `
    $pluginDefinition `
    "ZNet" `
    "RPC_ClientHandshake" `
    "Finalizer" |
    Out-Null

$connectionErrorPatch = Get-HarmonyPatchType `
    $pluginDefinition `
    "FejdStartup" `
    "ShowConnectError"
Assert-True ($null -ne $connectionErrorPatch) `
    "The existing connection-error cleanup patch was not found."
$connectionErrorCleanup = $connectionErrorPatch.Methods |
    Where-Object {
        $_.HasBody -and $null -ne (Get-BrandingCall $_)
    } |
    Select-Object -First 1
Assert-True ($null -ne $connectionErrorCleanup) `
    "Connection errors no longer clear the temporary branded-join state."

$panelPresentationType = Get-TypeDefinition `
    $pluginDefinition `
    "ServerManager.ConnectionErrorPanelPresentation"
Assert-True ($null -ne $panelPresentationType) `
    "The connection-error panel presentation helper was not built."

$showPrefix = Get-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "ShowConnectError" `
    "Prefix" `
    "ServerManager.ConnectionErrorPanelPresentation" `
    "BeforeShow"
$showPostfix = Get-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "ShowConnectError" `
    "Postfix" `
    "ServerManager.ConnectionErrorPanelPresentation" `
    "AfterShow"
$acknowledgePostfix = Get-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "OnConnectionFailedOk" `
    "Postfix" `
    "ServerManager.ConnectionErrorPanelPresentation" `
    "AfterAcknowledged"
$startupCleanupPrefix = Get-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "OnDestroy" `
    "Prefix" `
    "ServerManager.ConnectionErrorPanelPresentation" `
    "BeforeStartupDestroyed"
$panelUpdatePostfix = Get-HarmonyDelegation `
    $pluginDefinition `
    "FejdStartup" `
    "Update" `
    "Postfix" `
    "ServerManager.ConnectionErrorPanelPresentation" `
    "Tick"
Assert-True (
    $null -ne $showPrefix -and
    $null -ne $showPostfix -and
    $null -ne $acknowledgePostfix -and
    $null -ne $startupCleanupPrefix -and
    $null -ne $panelUpdatePostfix) `
    "The connection-error panel apply/acknowledge/destroy lifecycle changed."
foreach ($augmentingPatch in @(
    $showPrefix,
    $showPostfix,
    $acknowledgePostfix,
    $startupCleanupPrefix,
    $panelUpdatePostfix)) {
    Assert-True ($augmentingPatch.ReturnType.FullName -eq "System.Void") `
        "A connection-error UI patch can now skip its vanilla target."
}

$panelMethods = @{}
foreach ($methodName in @(
    "BeforeShow",
    "AfterShow",
    "AfterAcknowledged",
    "BeforeStartupDestroyed",
    "Shutdown",
    "RestoreActiveLayout",
    "ComposeMessage",
    "CalculatePanelHeight",
    "CalculatePageNumber",
    "Reflow",
    "Tick",
    "CreatePager",
    "CreatePageButton",
    "CreatePageLabel",
    "ChangePage")) {
    $method = $panelPresentationType.Methods |
        Where-Object Name -eq $methodName |
        Select-Object -First 1
    Assert-True ($null -ne $method -and $method.HasBody) `
        "Missing connection-error presentation method $methodName."
    $panelMethods[$methodName] = $method
}
Assert-InactiveTmpCreation $panelMethods['CreatePageLabel']

# Verify the installed TMP lifecycle this ordering protects, without running
# Unity objects outside the game. A changed dependency needs an explicit review.
$tmpDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    (Join-Path $GamePath 'valheim_Data\Managed\Unity.TextMeshPro.dll'))
try {
    $tmpUiType = Get-TypeDefinition $tmpDefinition 'TMPro.TextMeshProUGUI'
    $tmpAwake = Get-MethodDefinition $tmpUiType 'Awake'
    $tmpLoadFont = Get-MethodDefinition $tmpUiType 'LoadFontAsset'
    Assert-True ($null -ne (Get-MethodCall $tmpAwake 'TMPro.TMP_Text' 'LoadFontAsset') -and
        $null -ne (Get-MethodCall $tmpLoadFont 'TMPro.TMP_Settings' 'get_defaultFontAsset') -and
        $null -ne (Get-MethodCall $tmpLoadFont 'UnityEngine.Debug' 'LogWarning')) `
        'The installed TMP Awake/default-font warning lifecycle changed; review inactive text initialization.'
    $fontNullBranch = $tmpLoadFont.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'm_fontAsset' -and
        $null -ne $_.Next -and $_.Next.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldnull
    } | Select-Object -First 1
    Assert-True ($null -ne $fontNullBranch -and
        $fontNullBranch.Offset -lt (Get-MethodCall $tmpLoadFont 'TMPro.TMP_Settings' 'get_defaultFontAsset').Offset) `
        'The installed TMP default-font path no longer follows a missing-font check.'
}
finally { $tmpDefinition.Dispose() }

foreach ($restoreCallerName in @(
    "BeforeShow",
    "AfterAcknowledged",
    "BeforeStartupDestroyed",
    "Shutdown")) {
    Assert-True (
        $null -ne (Get-MethodCall `
            $panelMethods[$restoreCallerName] `
            "ServerManager.ConnectionErrorPanelPresentation" `
            "RestoreActiveLayout")) `
        "$restoreCallerName no longer restores the vanilla panel state."
}
$pluginOnDestroy = $pluginType.Methods |
    Where-Object Name -eq "OnDestroy" |
    Select-Object -First 1
Assert-True (
    $null -ne (Get-MethodCall `
        $pluginOnDestroy `
        "ServerManager.ConnectionErrorPanelPresentation" `
        "Shutdown")) `
    "Plugin shutdown no longer restores the active connection-error panel."
foreach ($layoutHelperName in @(
    "ComposeMessage",
    "CalculatePanelHeight")) {
    $afterShowCalls = @(Get-PresentationInstructions $panelMethods["AfterShow"] |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
        ForEach-Object { $_.Operand.Name })
    Assert-True (
        $afterShowCalls -contains $layoutHelperName) `
        "AfterShow no longer uses the pure $layoutHelperName layout seam."
}

# The one-shot reason must be consumed only after it reaches TMP. Otherwise a
# transient UI failure loses the useful server rejection and cannot retry.
$afterShowInstructions = @($panelMethods["AfterShow"].Body.Instructions)
$textAssignment = $afterShowInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq "TMPro.TMP_Text" -and
        $_.Operand.Name -eq "set_text"
    } |
    Select-Object -First 1
$reasonClear = $afterShowInstructions |
    Where-Object {
        ($_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerPlugin" -and
            $_.Operand.Name -eq "set_ConnectionError") -or
        ($_.Operand -is [Mono.Cecil.FieldReference] -and
            $_.Operand.DeclaringType.FullName -eq
                "ServerManager.ServerManagerPlugin" -and
            $_.Operand.Name -like "*ConnectionError*")
    } |
    Select-Object -First 1
Assert-True (
    $null -ne $textAssignment -and
    $null -ne $reasonClear -and
    $textAssignment.Offset -lt $reasonClear.Offset) `
    "The rejection reason is cleared before it is assigned to the visible label."

$restoreInstructions = @(Get-PresentationInstructions $panelMethods["RestoreActiveLayout"])
$restoredMembers = @($restoreInstructions |
    Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -or
        $_.Operand -is [Mono.Cecil.MethodReference]
    } |
    ForEach-Object { $_.Operand.Name })
foreach ($baselineField in @(
    "BaseText",
    "FontSize",
    "FontSizeMin",
    "FontSizeMax",
    "EnableAutoSizing",
    "WrappingMode",
    "OverflowMode",
    "RichText",
    "Alignment",
    "Margin",
    "PageToDisplay",
    "FirstVisibleCharacter",
    "MaxVisibleCharacters",
    "MaxVisibleWords",
    "MaxVisibleLines",
    "UseMaxVisibleDescender",
    "TextRect",
    "BackgroundRect",
    "OkButtonRect")) {
    Assert-True (
        $restoredMembers -contains $baselineField -or
        $restoredMembers -contains ("get_" + $baselineField)) `
        "Panel restoration no longer reads its captured $baselineField baseline."
}
$restoreCalls = @($restoreInstructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
    ForEach-Object { $_.Operand.Name })
foreach ($requiredSetter in @(
    "set_text",
    "set_fontSize",
    "set_fontSizeMin",
    "set_fontSizeMax",
    "set_enableAutoSizing",
    "set_textWrappingMode",
    "set_overflowMode",
    "set_richText",
    "set_alignment",
    "set_margin",
    "set_pageToDisplay",
    "set_firstVisibleCharacter",
    "set_maxVisibleCharacters",
    "set_maxVisibleWords",
    "set_maxVisibleLines",
    "set_useMaxVisibleDescender",
    "set_anchorMin",
    "set_anchorMax",
    "set_pivot",
    "set_localRotation",
    "set_localScale",
    "SetParent",
    "SetSiblingIndex",
    "set_sizeDelta",
    "set_anchoredPosition3D")) {
    Assert-True ($restoreCalls -contains $requiredSetter) `
        "Panel restoration no longer calls $requiredSetter."
}
Assert-True (
    $null -ne ($restoreInstructions |
        Where-Object {
            $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stsfld -and
            $_.Operand -is [Mono.Cecil.FieldReference] -and
            $_.Operand.Name -eq "_activeLayout"
        } |
        Select-Object -First 1)) `
    "Panel restoration does not release its captured layout state."

# Keep Valheim's existing wooden Image/ButtonOk hierarchy. Additional controls
# belong to the pager only; the original panel and OK action stay in place.
$presentationTypes = @(Get-NestedTypeDefinitions $panelPresentationType)
$presentationInstructions = @($presentationTypes | ForEach-Object { $_.Methods } |
    Where-Object HasBody |
    ForEach-Object { $_.Body.Instructions })
$presentationStrings = @($presentationInstructions |
    Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr } |
    ForEach-Object { [string]$_.Operand })
Assert-True (
    $presentationStrings -contains "Image" -and
    $presentationStrings -contains "ButtonOk") `
    "The presenter no longer reuses Valheim's wooden Image/ButtonOk hierarchy."
$creatorNames = @('CreatePager', 'CreatePageButton', 'CreatePageLabel')
foreach ($presentationMethod in @($presentationTypes | ForEach-Object { $_.Methods } | Where-Object HasBody)) {
    $visualCalls = @($presentationMethod.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
        ForEach-Object { $_.Operand.Name })
    Assert-True ($visualCalls -notcontains 'Instantiate') `
        'The pager clones existing UI, potentially retaining the OK persistent listener.'
    if ($presentationMethod.Name -notin $creatorNames) {
        foreach ($skinMutation in @('set_sprite', 'set_material', 'set_color', 'AddComponent')) {
            Assert-True ($visualCalls -notcontains $skinMutation) `
                ('The presenter changes UI skin/components outside owned pager construction: ' + $presentationMethod.Name)
        }
    }
}
$restoreStrings = @($restoreInstructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -or $_.Operand -is [Mono.Cecil.FieldReference] } |
    ForEach-Object { $_.Operand.Name })
Assert-True ($restoreCalls -contains 'Destroy' -and
    ($restoreStrings -contains 'Pager' -or $restoreStrings -contains 'get_Pager')) `
    'Presenter cleanup does not release its owned pager object.'
$destroyInstructions = @($presentationInstructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Destroy' })
foreach ($destroyInstruction in $destroyInstructions) {
    Assert-True ($null -ne $destroyInstruction.Previous -and
        $destroyInstruction.Previous.Operand -is [Mono.Cecil.MethodReference] -and
        $destroyInstruction.Previous.Operand.Name -eq 'get_Pager') `
        'Connection panel cleanup can destroy an original vanilla UI object instead of only its owned pager.'
}
$captureCalls = @($presentationTypes | ForEach-Object { $_.Methods } |
    Where-Object { $_.IsConstructor -and $_.HasBody } |
    ForEach-Object { $_.Body.Instructions } |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
    ForEach-Object { $_.Operand.Name })
foreach ($captureGetter in @('get_fontSize', 'get_richText', 'get_alignment', 'get_margin',
    'get_pageToDisplay', 'get_firstVisibleCharacter', 'get_maxVisibleCharacters',
    'get_maxVisibleWords', 'get_maxVisibleLines', 'get_useMaxVisibleDescender',
    'get_parent', 'GetSiblingIndex', 'get_anchorMin', 'get_anchorMax', 'get_pivot',
    'get_anchoredPosition3D', 'get_sizeDelta', 'get_localRotation', 'get_localScale')) {
    Assert-True ($captureCalls -contains $captureGetter) `
        ('Presentation state is restored without first capturing its original value: ' + $captureGetter)
}
$allPresentationCalls = @($presentationInstructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
    ForEach-Object { $_.Operand.Name })
Assert-True ($allPresentationCalls -contains 'RemoveTextFromCache' -and
    $captureCalls -contains 'TryGetLocalizationTextMeshStrings' -and
    $restoreCalls -contains 'TryGetLocalizationTextMeshStrings' -and
    $restoreCalls -contains 'set_Item') `
    'The temporary literal notice is not detached from and restored to the vanilla localization cache.'
$changePageCalls = @(Get-PresentationInstructions $panelMethods['ChangePage'] |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
    ForEach-Object { $_.Operand.Name })
Assert-True ($changePageCalls -contains 'CalculatePageNumber' -and
    $changePageCalls -contains 'set_pageToDisplay') `
    'Page controls do not use the bounded page-index calculation.'
$tickInstructions = @($panelMethods['Tick'].Body.Instructions)
$tickCalls = @($tickInstructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
    ForEach-Object { $_.Operand.Name })
Assert-True (($tickCalls -contains 'get_activeSelf' -or $tickCalls -contains 'get_activeInHierarchy') -and
    $tickCalls -contains 'Reflow') `
    'The panel update lacks an active-panel guard or resolution reflow path.'
$tickStrings = @($tickInstructions |
    Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr } |
    ForEach-Object { [string]$_.Operand })
Assert-True ($tickCalls -contains 'GetMouseScrollWheel' -and
    $tickCalls -contains 'GetKeyDown' -and $tickCalls -contains 'GetButtonDown' -and
    $tickStrings -contains 'JoyLBumper' -and $tickStrings -contains 'JoyRBumper') `
    'Long notices are not accessible using mouse, keyboard and gamepad page controls.'
$reflowCalls = @(Get-PresentationInstructions $panelMethods['Reflow'] |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
    ForEach-Object { $_.Operand.Name })
Assert-True ($reflowCalls -contains 'set_sizeDelta' -and
    $reflowCalls -contains 'get_rectTransform' -and
    $reflowCalls -contains 'set_offsetMin' -and $reflowCalls -contains 'set_offsetMax' -and
    $reflowCalls -contains 'ForceMeshUpdate') `
    'Panel reflow does not update geometry and regenerate the native TMP pages.'

$brandingSource = [IO.File]::ReadAllText($brandingSourcePath)
Assert-True (
    $brandingSource.Contains("HasQueuedJoinOrCannotInspect(startup)") -and
    $brandingSource.IndexOf(
        "HasQueuedJoinOrCannotInspect(startup)",
        [StringComparison]::Ordinal) -lt
    $brandingSource.IndexOf(
        "TrySetQueuedJoinServer(startup, destination)",
        [StringComparison]::Ordinal)) `
    "An existing Steam invite/+connect queued destination is no longer guarded before assignment."
Assert-True (
    $brandingSource.Contains("TryGetServerPassword") -and
    $brandingSource.Contains("TrySetServerPassword") -and
    $brandingSource.Contains("_ownedPassword") -and
    $brandingSource.Contains("_previousPassword") -and
    $brandingSource.Contains("_passwordAwaitingHandshake") -and
    $brandingSource.Contains("BeforeClientHandshake") -and
    $brandingSource.Contains(
        "TrySetServerPassword(_previousPassword)")) `
    "The quick-connect password is no longer scoped to and restored around the handshake."
Assert-True (
    $brandingSource.Contains("BeforeStartupDestroyed") -and
    $brandingSource.Contains("ReleaseCustomLogo(restoreVanilla: true)")) `
    "FejdStartup destruction no longer releases the menu logo and UI references."
Assert-True (
    $brandingSource.Contains("OriginalEnableAutoSizing") -and
    $brandingSource.Contains("OriginalWrappingMode") -and
    $brandingSource.Contains("textMeshStrings[text]") -and
    $brandingSource.Contains("_vanillaLogoPreserveAspect")) `
    "Button overrides no longer restore their TMP and localization state."
Assert-True (
    $brandingSource.Contains("OnUiUpdate") -and
    $brandingSource.Contains("!HasQueuedJoinOrCannotInspect(startup)")) `
    "A late external queued join can leave a misleading branded character label."
$joinFactory = Get-MethodDefinition $brandingType 'TryCreateJoinDestination' 'System.Boolean' `
    @('System.String', 'ServerJoinData&', 'System.String&')
$configuredDestination = Get-MethodDefinition $brandingType 'TryGetConfiguredEndpoint' 'System.Boolean' @('ServerJoinData&')
$lobbyQueryType = Get-TypeDefinition $pluginDefinition 'ServerManager.OptionalModLobbyQuery'
$lobbyEndpointParser = Get-MethodDefinition $lobbyQueryType 'TryParseHostEndpoint' 'System.Boolean' @('System.String', 'System.UInt64&')
Assert-True ($null -ne $joinFactory -and $null -ne $configuredDestination -and $null -ne $lobbyEndpointParser -and
    $null -ne (Get-MethodCall $configuredDestination $brandingType.FullName 'TryCreateJoinDestination') -and
    $null -ne (Get-MethodCall $joinFactory $lobbyQueryType.FullName 'TryParseHostEndpoint') -and
    $null -ne (Get-MethodCall $joinFactory 'ServerJoinDataSteamUser' '.ctor') -and
    $null -ne (Get-MethodCall $joinFactory 'ServerJoinDataDedicated' '.ctor')) `
    'Quick-connect no longer builds a native destination through the pure two-transport factory.'
$joinFactoryInstructions = @(Get-PresentationInstructions $joinFactory $brandingType.FullName)
$joinFactoryInstructions += @(Get-PresentationInstructions $lobbyEndpointParser $lobbyQueryType.FullName)
$joinFactoryCalls = @($joinFactoryInstructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference]
} | ForEach-Object Operand)
$forbiddenDestinationCalls = @($joinFactoryCalls | Where-Object {
    $_.DeclaringType.FullName -match '^(ZNet|FejdStartup|ZSteamMatchmaking|MultiBackendMatchmaking|SteamManager)$|^Steamworks\.Steam(Friends|Matchmaking|MatchmakingServers|User|Utils)$|^UnityEngine\.' -or
    $_.DeclaringType.FullName -match '^ServerManager\.(OptionalModQuery|OptionalModCatalog|ServerAdmission|Integrity|Character)' -or
    ($_.DeclaringType.FullName -eq $lobbyQueryType.FullName -and $_.Name -ne 'TryParseHostEndpoint')
})
Assert-True ($forbiddenDestinationCalls.Count -eq 0) `
    'Destination construction depends on live presence, catalog/admission state, or prematurely performs networking/scene work.'
Assert-True (@($joinFactoryInstructions | Where-Object {
    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stsfld -or
    ($_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -in @('m_joinData', 'm_queuedJoinLobby', 'm_queuedJoinServer', 'm_joinServer'))
}).Count -eq 0) 'The destination parser mutates shared matchmaking or menu join state.'
$characterStartSetup = Get-MethodDefinition $brandingType 'BeforeCharacterStart' 'System.Void' @('FejdStartup')
$characterQueuedGuard = Get-MethodCall $characterStartSetup $brandingType.FullName 'HasQueuedJoinOrCannotInspect'
$characterDestination = Get-MethodCall $characterStartSetup $brandingType.FullName 'TryGetConfiguredEndpoint'
$characterQueueWrite = Get-MethodCall $characterStartSetup $brandingType.FullName 'TrySetQueuedJoinServer'
Assert-True ($null -ne $characterQueuedGuard -and $null -ne $characterDestination -and $null -ne $characterQueueWrite -and
    $characterQueuedGuard.Offset -lt $characterDestination.Offset -and $characterDestination.Offset -lt $characterQueueWrite.Offset) `
    'The character click does not preserve an external queued join before constructing/assigning the branded destination.'
foreach ($entryPoint in @('BeforeMainStart', 'BeforeCharacterStart')) {
    $entryInstructions = @((Get-MethodDefinition $brandingType $entryPoint 'System.Void' @('FejdStartup')).Body.Instructions)
    Assert-True (@($entryInstructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        ($_.Operand.DeclaringType.FullName -match '^(ZSteamMatchmaking|MultiBackendMatchmaking)$|^Steamworks\.Steam' -or
            $_.Operand.Name -in @('JoinServer', 'SetServerHost', 'CheckIfOnlineAsync', 'QueueServerJoin', 'QueueLobbyJoin'))
    }).Count -eq 0) ($entryPoint + ' bypasses the native selected-character join path or depends on Steam presence.')
}
$worldHintMethod = Get-MethodDefinition $brandingType 'ShouldShowWorldMenuHint' 'System.Boolean' @('System.String')
Assert-True ($null -ne (Get-MethodCall $worldHintMethod $lobbyQueryType.FullName 'TryParseHostEndpoint') -and
    $null -ne (Get-MethodCall $worldHintMethod $brandingType.FullName 'TryParseDedicatedEndpoint')) `
    'The Alt/world-picker hint no longer recognizes both dedicated and Steam-host destinations.'
Assert-True (
    [Text.RegularExpressions.Regex]::IsMatch(
        $brandingSource,
        'Application\.isBatchMode|GraphicsDeviceType\.Null|' +
        'SystemInfo\.graphicsDeviceType')) `
    "Client branding has no explicit headless/batch-mode guard."
# Local paths remain local-only. The separate HTTPS path is exercised through
# a fake HTTP handler in LogoUrlSmoke; these checks cover its real UI ownership
# and prohibit networking or Unity work from leaking into the wrong phase.
$logoRootCall = Get-MethodCall $applyLogo 'ServerManager.ServerDataRoot' 'ResolveCurrentPath'
$logoCachePathCall = Get-MethodCall $applyLogo $brandingType.FullName 'GetLogoCachePath'
$logoApplyStrings = @($applyLogo.Body.Instructions |
    Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr } |
    ForEach-Object { [string]$_.Operand })
Assert-True ($null -ne $logoRootCall -and $null -ne $logoCachePathCall -and
    $logoRootCall.Offset -lt $logoCachePathCall.Offset -and $logoApplyStrings -contains 'cache') `
    'The logo cache is not resolved lazily beneath Valheim local save data/ServerManager/cache.'
$logoStorageCalls = @((Get-NestedTypeDefinitions $brandingType) |
    ForEach-Object { $_.Methods } | Where-Object HasBody |
    ForEach-Object { $_.Body.Instructions } |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
    ForEach-Object Operand)
Assert-True (@($logoStorageCalls | Where-Object {
    ($_.DeclaringType.FullName -eq 'BepInEx.Paths' -and $_.Name -eq 'get_CachePath') -or
    ($_.DeclaringType.FullName -eq 'ServerManager.ServerDataRoot' -and $_.Name -in @('BindAndPrepare', 'get_ActivePath')) -or
    ($_.DeclaringType.FullName -eq 'ServerManager.ServerManagerPlugin' -and $_.Name -in @('get_DataRoot', 'get_CharacterRoot'))
}).Count -eq 0) 'Logo caching still uses a BepInEx profile cache or binds client access to server storage.'
$downloadLogo = $brandingType.Methods | Where-Object Name -eq 'DownloadLogoAsync' | Select-Object -First 1
$asyncAttribute = $downloadLogo.CustomAttributes | Where-Object {
    $_.AttributeType.FullName -eq 'System.Runtime.CompilerServices.AsyncStateMachineAttribute'
} | Select-Object -First 1
Assert-True ($null -ne $downloadLogo -and $null -ne $asyncAttribute) 'The remote logo transport is not an asynchronous method.'
$downloadState = $asyncAttribute.ConstructorArguments[0].Value.Resolve()
$downloadMoveNext = $downloadState.Methods | Where-Object Name -eq 'MoveNext' | Select-Object -First 1
$downloadInstructions = @($downloadMoveNext.Body.Instructions)
$downloadCalls = @($downloadInstructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object Operand)
$logoTimeoutConstant = $brandingType.Fields |
    Where-Object Name -eq 'LogoDownloadTimeoutSeconds' | Select-Object -First 1
$logoDeadlineSeconds = $downloadInstructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and
    $_.Operand.DeclaringType.FullName -eq 'System.TimeSpan' -and $_.Operand.Name -eq 'FromSeconds'
} | Select-Object -First 1
Assert-True ($null -ne $logoTimeoutConstant -and $logoTimeoutConstant.HasConstant -and
    [int]$logoTimeoutConstant.Constant -eq 30 -and $null -ne $logoDeadlineSeconds -and
    $logoDeadlineSeconds.Previous.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldc_R8 -and
    [double]$logoDeadlineSeconds.Previous.Operand -eq 30) `
    'The HTTPS logo deadline is not the shared 30-second allowance.'
$logoHttpTimeout = Get-MethodCall $downloadMoveNext 'System.Net.Http.HttpClient' 'set_Timeout'
Assert-True ($null -ne $logoHttpTimeout -and
    $logoHttpTimeout.Previous.Operand -is [Mono.Cecil.FieldReference] -and
    $logoHttpTimeout.Previous.Operand.DeclaringType.FullName -eq 'System.Threading.Timeout' -and
    $logoHttpTimeout.Previous.Operand.Name -eq 'InfiniteTimeSpan') `
    'HttpClient has a competing timeout that bypasses the linked whole-download deadline.'
foreach ($httpSafetySetter in @('set_AllowAutoRedirect', 'set_UseCookies', 'set_UseDefaultCredentials')) {
    Assert-True (Test-ConstantIntSetter $downloadInstructions $httpSafetySetter 0 'System.Net.Http.HttpClientHandler') `
        ('The default logo HTTP handler permits automatic redirects or credentials: ' + $httpSafetySetter)
}
Assert-True (@($downloadCalls | Where-Object {
    $_.DeclaringType.FullName -match '^(UnityEngine|TMPro)(\.|$)'
}).Count -eq 0) 'The asynchronous logo downloader touches Unity objects off the game thread.'
foreach ($requiredTransportCall in @('TryParseLogoUrl', 'CreateLinkedTokenSource', 'CancelAfter', 'AwaitLogoTask', 'TryValidatePng')) {
    Assert-True (@($downloadCalls | Where-Object Name -eq $requiredTransportCall).Count -gt 0) `
        ('The logo worker lost an HTTPS/timeout/bounds validation boundary: ' + $requiredTransportCall)
}
Assert-True (@($downloadCalls | Where-Object Name -eq 'CancelAfter').Count -eq 1 -and
    (Get-MethodCall $downloadMoveNext 'System.Threading.CancellationTokenSource' 'CancelAfter').Offset -lt
        (Get-MethodCall $downloadMoveNext 'System.Net.Http.HttpClient' 'SendAsync').Offset) `
    'The logo deadline is restarted inside a redirect/read instead of bounding the whole download.'
$downloadTypeChecks = @($downloadInstructions | Where-Object {
    $_.Operand -is [Mono.Cecil.TypeReference]
} | ForEach-Object { $_.Operand.FullName })
$downloadTypeChecks += @($downloadMoveNext.Body.ExceptionHandlers | Where-Object {
    $null -ne $_.CatchType
} | ForEach-Object { $_.CatchType.FullName })
Assert-True ($downloadTypeChecks -contains 'System.OperationCanceledException' -and
    $null -ne (Get-MethodCall $downloadMoveNext 'System.TimeoutException' '.ctor') -and
    $null -ne (Get-MethodCall $downloadMoveNext 'System.Threading.CancellationToken' 'get_IsCancellationRequested')) `
    'The downloader no longer distinguishes its expired deadline from caller cancellation.'
$logoFailureType = $brandingType.NestedTypes | Where-Object Name -eq 'LogoDownloadFailure' | Select-Object -First 1
Assert-True ($null -ne $logoFailureType -and $logoFailureType.IsEnum) `
    'Remote logo failures lack a bounded diagnostic classification.'
foreach ($failureName in @('HttpStatus', 'Network', 'Redirect', 'TooLarge', 'InvalidPng')) {
    Assert-True (@($logoFailureType.Fields | Where-Object Name -eq $failureName).Count -eq 1) `
        ('A safe remote-logo failure classification is missing: ' + $failureName)
}
$logoFailureMessage = Get-MethodDefinition $brandingType 'LogoDownloadFailureMessage' 'System.String' @('System.Exception')
Assert-True ($null -ne $logoFailureMessage -and $logoFailureMessage.HasBody) `
    'The remote logo download warning has no pure exception classifier.'
$logoFailureInstructions = @(Get-PresentationInstructions $logoFailureMessage $brandingType.FullName)
$logoFailureCalls = @($logoFailureInstructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object Operand)
Assert-True (@($logoFailureCalls | Where-Object {
    $_.Name -in @('get_Message', 'get_StackTrace', 'get_InnerException', 'get_Data', 'GetBaseException') -or
    ($_.Name -eq 'ToString' -and $_.DeclaringType.FullName -notin @('System.Int32', 'System.Int64')) -or
    $_.DeclaringType.FullName -eq 'System.Uri' -or
    $_.DeclaringType.FullName -match '^(UnityEngine|TMPro|System\.Net)(\.|$)'
}).Count -eq 0) 'The download warning can expose URL/exception details or perform transport/Unity work.'
$logoFailureChecks = @($logoFailureInstructions | Where-Object {
    $_.Operand -is [Mono.Cecil.TypeReference]
} | ForEach-Object { $_.Operand.FullName })
Assert-True ($logoFailureChecks -contains 'System.TimeoutException' -and
    $logoFailureChecks -contains 'System.OperationCanceledException' -and
    $logoFailureChecks -contains ($brandingType.FullName + '/LogoDownloadException')) `
    'The warning classifier conflates deadline, caller cancellation, and typed download failures.'
$remoteLogoType = $brandingType.NestedTypes | Where-Object Name -eq 'RemoteLogoLoad' | Select-Object -First 1
$remoteLogoConstructor = $remoteLogoType.Methods | Where-Object IsConstructor | Select-Object -First 1
Assert-True ($null -ne (Get-MethodCall $remoteLogoConstructor 'System.Threading.Tasks.Task' 'Run')) `
    'Starting a remote logo no longer dispatches the download off the UI thread.'
$logoTick = $brandingType.Methods | Where-Object Name -eq 'TickRemoteLogo' | Select-Object -First 1
$uiUpdate = $brandingType.Methods | Where-Object Name -eq 'OnUiUpdate' | Select-Object -First 1
$tickCall = Get-MethodCall $uiUpdate $brandingType.FullName 'TickRemoteLogo'
$firstUiReturn = $uiUpdate.Body.Instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ret } | Select-Object -First 1
Assert-True ($null -ne $tickCall -and $tickCall.Offset -lt $firstUiReturn.Offset) `
    'Logo completion is skipped by the unrelated armed quick-connect early return.'
$logoApplyBytes = Get-MethodCall $logoTick $brandingType.FullName 'ApplyLogoBytes'
$logoCommitCache = Get-MethodCall $logoTick $brandingType.FullName 'WriteLogoCache'
$logoRetrieveBytes = $logoTick.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'GetResult'
} | Select-Object -First 1
$logoCompletionCheck = $logoTick.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_IsCompleted'
} | Select-Object -First 1
Assert-True ($null -ne $logoCompletionCheck -and $null -ne $logoRetrieveBytes -and
    $null -ne $logoApplyBytes -and $null -ne $logoCommitCache -and
    $logoCompletionCheck.Offset -lt $logoRetrieveBytes.Offset -and
    $logoRetrieveBytes.Offset -lt $logoApplyBytes.Offset -and $logoApplyBytes.Offset -lt $logoCommitCache.Offset) `
    'The UI waits on an unfinished download or commits its cache before successful Unity decoding.'
Assert-True ($brandingSource -match 'if\s*\(\s*!load\.Download\.IsCompleted\s*\)\s*return\s*;' -and
    @($logoTick.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -in @('Wait', 'WaitAll', 'WaitAny', 'get_Result', 'DownloadLogoAsync')
    }).Count -eq 0) 'The menu completion poll can block on a running remote-logo operation.'
$logoStageHandlers = @{}
foreach ($stage in @(
    @{ Name = 'Retrieve'; Instruction = $logoRetrieveBytes },
    @{ Name = 'Apply'; Instruction = $logoApplyBytes },
    @{ Name = 'Cache'; Instruction = $logoCommitCache })) {
    $handlers = @($logoTick.Body.ExceptionHandlers | Where-Object {
        $_.TryStart.Offset -le $stage.Instruction.Offset -and
        ($null -eq $_.TryEnd -or $_.TryEnd.Offset -gt $stage.Instruction.Offset)
    })
    Assert-True ($handlers.Count -eq 1) `
        ('The remote logo stage does not have an isolated failure boundary: ' + $stage.Name)
    $logoStageHandlers[$stage.Name] = $handlers[0]
}
Assert-True ($logoStageHandlers['Retrieve'] -ne $logoStageHandlers['Apply'] -and
    $logoStageHandlers['Apply'] -ne $logoStageHandlers['Cache'] -and
    $logoStageHandlers['Retrieve'] -ne $logoStageHandlers['Cache']) `
    'Download, Unity decode/apply, and cache failures share a misleading warning handler.'
$stageWarnings = @{
    Apply = 'The HTTPS menu logo failed during Unity PNG decode/apply; keeping the cached or vanilla logo.'
    Cache = 'The HTTPS menu logo was applied, but its cache could not be saved.'
}
foreach ($stageName in @('Retrieve', 'Apply', 'Cache')) {
    $handler = $logoStageHandlers[$stageName]
    Assert-True ($null -ne $handler.FilterStart -and
        @($logoTick.Body.Instructions | Where-Object {
            $_.Offset -ge $handler.FilterStart.Offset -and $_.Offset -lt $handler.HandlerStart.Offset -and
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq 'ServerManager.IntegrityCanonical' -and
            $_.Operand.Name -eq 'IsFatal'
        }).Count -eq 1) `
        ('The logo failure boundary swallows fatal runtime failures: ' + $stageName)
    $handlerInstructions = @($logoTick.Body.Instructions | Where-Object {
        $_.Offset -ge $handler.HandlerStart.Offset -and
        ($null -eq $handler.HandlerEnd -or $_.Offset -lt $handler.HandlerEnd.Offset)
    })
    $handlerCalls = @($handlerInstructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference]
    } | ForEach-Object { $_.Operand.Name })
    Assert-True ($handlerCalls -contains 'WarnOnce' -and $handlerCalls -notcontains 'get_Message' -and
        $handlerCalls -notcontains 'ToString') `
        ('A remote logo failure is silent or leaks exception details: ' + $stageName)
    if ($stageName -eq 'Retrieve') {
        Assert-True ($handlerCalls -contains 'LogoDownloadFailureMessage') `
            'Download failures do not use the secret-free diagnostic classifier.'
    }
    else {
        Assert-True ($handlerCalls -notcontains 'LogoDownloadFailureMessage' -and
            @($handlerInstructions | Where-Object {
                $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and $_.Operand -eq $stageWarnings[$stageName]
            }).Count -eq 1) `
            ('A post-download failure is mislabeled as an HTTP failure: ' + $stageName)
    }
    if ($stageName -in @('Retrieve', 'Apply')) {
        Assert-True (-not (Test-InstructionReaches $handler.HandlerStart $logoCommitCache)) `
            ('A failed remote logo stage can overwrite the working cache: ' + $stageName)
    }
}
Assert-True (-not (Test-InstructionReaches $logoStageHandlers['Retrieve'].HandlerStart $logoApplyBytes)) `
    'A failed byte download can fall through into Unity logo application.'
$logoTickMembers = @($logoTick.Body.Instructions |
    Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -or $_.Operand -is [Mono.Cecil.FieldReference] } |
    ForEach-Object { $_.Operand.Name })
foreach ($logoOwnerGuard in @('Startup', 'Image', 'Source', 'CanUseClientUi', 'get_BrandingLogoPath')) {
    Assert-True ($logoTickMembers -contains $logoOwnerGuard) `
        ('An old or non-client menu download can replace the current logo: ' + $logoOwnerGuard)
    $ownerInstruction = $logoTick.Body.Instructions | Where-Object {
        ($_.Operand -is [Mono.Cecil.MethodReference] -or $_.Operand -is [Mono.Cecil.FieldReference]) -and
        $_.Operand.Name -eq $logoOwnerGuard
    } | Select-Object -First 1
    Assert-True ($ownerInstruction.Offset -lt $logoCompletionCheck.Offset) `
        ('Remote-logo ownership is checked too late to discard an obsolete request: ' + $logoOwnerGuard)
}
Assert-True ($brandingSource.Contains('ReferenceEquals(load.Startup, startup)') -and
    $brandingSource.Contains('ReferenceEquals(load.Image, _logoImage)')) `
    'Remote logo application no longer checks the exact startup and image ownership.'
$releaseLogo = $brandingType.Methods | Where-Object Name -eq 'ReleaseCustomLogo' | Select-Object -First 1
Assert-True ($null -ne (Get-MethodCall $releaseLogo $brandingType.FullName 'CancelRemoteLogo')) `
    'Restoring the vanilla logo no longer invalidates the previous request.'
$cancelLogo = $brandingType.Methods | Where-Object Name -eq 'CancelRemoteLogo' | Select-Object -First 1
Assert-True ($null -ne (Get-MethodCall $cancelLogo 'System.Threading.CancellationTokenSource' 'Cancel')) `
    'Menu/source disposal leaves its remote logo request running.'
$cancelLogoInstructions = @(Get-PresentationInstructions $cancelLogo $brandingType.FullName)
$cancelLogoCalls = @($cancelLogoInstructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference]
} | ForEach-Object { $_.Operand.Name })
foreach ($cancellationCleanup in @('ContinueWith', 'get_IsFaulted', 'get_Exception', 'Dispose')) {
    Assert-True ($cancelLogoCalls -contains $cancellationCleanup) `
        ('Cancelled remote-logo work no longer observes faults or disposes its owner: ' + $cancellationCleanup)
}
$cancelLogoOwnership = $cancelLogo.Body.Instructions | Where-Object {
    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stsfld -and
    $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq '_remoteLogoLoad'
} | Select-Object -First 1
Assert-True ($null -ne $cancelLogoOwnership -and
    $cancelLogoOwnership.Offset -lt (Get-MethodCall $cancelLogo 'System.Threading.CancellationTokenSource' 'Cancel').Offset) `
    'Cancelling an obsolete logo does not first invalidate its UI ownership.'
$logoAttemptInstructions = @($applyLogo.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq '_remoteLogoAttemptedSource'
})
Assert-True (@($logoAttemptInstructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldsfld }).Count -gt 0 -and
    @($logoAttemptInstructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stsfld }).Count -gt 0) `
    'A failed image endpoint can be retried continuously on every menu update.'
Assert-True ($brandingSource -notmatch 'UnityWebRequest|WebClient') `
    'Logo loading gained another transport which bypasses the bounded HTTPS client.'

# Exercise the non-Unity validation boundary. These helpers deliberately use
# simple CLR values so malformed admin configuration can be tested without a
# running Unity scene.
$managedAssemblyRoot = Join-Path $GamePath "valheim_Data\Managed"
foreach ($dependencyName in @(
    "UnityEngine.CoreModule.dll",
    "UnityEngine.ImageConversionModule.dll",
    "UnityEngine.UI.dll",
    "Unity.TextMeshPro.dll",
    "assembly_utils.dll",
    "SoftReferenceableAssets.dll",
    "Splatform.dll",
    "com.rlabrecque.steamworks.net.dll")) {
    $dependencyPath = Join-Path $managedAssemblyRoot $dependencyName
    Assert-True (Test-Path -LiteralPath $dependencyPath) `
        "Required branding test dependency was not found: $dependencyName"
    [Reflection.Assembly]::LoadFrom($dependencyPath) | Out-Null
}
$managedGameAssembly = [Reflection.Assembly]::LoadFrom($gameAssemblyPath)
$managedPluginAssembly = [Reflection.Assembly]::LoadFrom($pluginPath)
$runtimeBrandingType = Get-LoadedType `
    $managedPluginAssembly `
    "ServerManager.ClientMenuBranding"
$runtimePanelPresentationType = Get-LoadedType `
    $managedPluginAssembly `
    "ServerManager.ConnectionErrorPanelPresentation"

Assert-True (Test-ConstantIntSetter $guideCreateTextInstructions 'set_textWrappingMode' ([int][TMPro.TextWrappingModes]::Normal)) `
    'Menu guidance does not wrap to the actual sidebar width.'
Assert-True (Test-ConstantIntSetter $guideCreateTextInstructions 'set_overflowMode' ([int][TMPro.TextOverflowModes]::Overflow)) `
    'Menu guidance can silently replace long text with clipping/ellipsis instead of scrolling its full content.'
$calculateGuideSize = Get-StaticMethod $runtimeBrandingType 'CalculateGuideSize' 3
$shouldShowWorldMenuHint = Get-StaticMethod $runtimeBrandingType 'ShouldShowWorldMenuHint' 1
foreach ($hintFixture in @(
    @{ Endpoint = $null; Expected = $false },
    @{ Endpoint = ''; Expected = $false },
    @{ Endpoint = '   '; Expected = $false },
    @{ Endpoint = 'play example.test:2456'; Expected = $false },
    @{ Endpoint = 'play.example.test:0'; Expected = $false },
    @{ Endpoint = 'play.example.test:65536'; Expected = $false },
    @{ Endpoint = 'https://play.example.test:2456/path'; Expected = $false },
    @{ Endpoint = 'user@play.example.test:2456'; Expected = $false },
    @{ Endpoint = '[not-an-ipv6]:2456'; Expected = $false },
    @{ Endpoint = 'steam:1'; Expected = $false },
    @{ Endpoint = 'steam:76561197960265728'; Expected = $false },
    @{ Endpoint = 'steam:76561193665298433'; Expected = $false },
    @{ Endpoint = 'steam:85568397215006721'; Expected = $false },
    @{ Endpoint = 'steam:76561198000000001:2456'; Expected = $false },
    @{ Endpoint = 'steam:76561198000000001'; Expected = $true },
    @{ Endpoint = ' STEAM:76561198000000001 '; Expected = $true },
    @{ Endpoint = 'play.example.test'; Expected = $true },
    @{ Endpoint = 'play.example.test:2456'; Expected = $true },
    @{ Endpoint = '127.0.0.1:2456'; Expected = $true },
    @{ Endpoint = '[2001:db8::1]:2456'; Expected = $true },
    @{ Endpoint = '::1'; Expected = $true })) {
    $showHint = [bool]$shouldShowWorldMenuHint.Invoke(
        $null, [object[]]@($hintFixture.Endpoint))
    Assert-True ($showHint -eq $hintFixture.Expected) `
        ('The world-picker hint must depend only on a valid quick-connect destination: ' + $hintFixture.Endpoint)
}
foreach ($guideGeometry in @(
    @([single]1920, [single]1080, [single]300, [single]440, [single]340),
    @([single]1280, [single]720, [single]1000, [single]345.6, [single]672),
    @([single]640, [single]480, [single]1000, [single]172.8, [single]432),
    @([single]5120, [single]1440, [single]600, [single]440, [single]640),
    @([single]320, [single]180, [single]1000, [single]86.4, [single]162),
    @([single]1920, [single]1080, [single]-500, [single]440, [single]40),
    @([single]1920, [single]1080, [single]::NaN, [single]440, [single]1032),
    @([single]-1, [single]1080, [single]100, [single]0, [single]0),
    @([single]::NaN, [single]1080, [single]100, [single]0, [single]0),
    @([single]1920, [single]::PositiveInfinity, [single]100, [single]0, [single]0))) {
    $guideSize = $calculateGuideSize.Invoke($null, [object[]]@($guideGeometry[0], $guideGeometry[1], $guideGeometry[2]))
    Assert-Near $guideSize.x $guideGeometry[3] 'The guide width is not bounded to the reserved right-hand portion of the menu.'
    Assert-Near $guideSize.y $guideGeometry[4] 'The guide height escaped the menu or failed to keep overflow scrollable.'
}

$applyInstructions = @(Get-PresentationInstructions $panelMethods['AfterShow'])
Assert-True (Test-ConstantIntSetter $applyInstructions 'set_overflowMode' ([int][TMPro.TextOverflowModes]::Page)) `
    'Connection notices still use clipping/ellipsis instead of native TMP pagination.'
Assert-True (Test-ConstantIntSetter $applyInstructions 'set_richText' 0) `
    'Connection notices can execute rich-text markup in mod/player names.'
Assert-True (Test-ConstantIntSetter $applyInstructions 'set_enableAutoSizing' 0) `
    'Long notices can be made unreadably small instead of using another page.'
foreach ($visibilitySetter in @('set_maxVisibleCharacters', 'set_maxVisibleWords', 'set_maxVisibleLines')) {
    Assert-True (Test-ConstantIntSetter $applyInstructions $visibilitySetter ([int]::MaxValue)) `
        ('The presenter retains a hidden TMP text visibility cap: ' + $visibilitySetter)
}
Assert-True (Test-ConstantIntSetter $applyInstructions 'set_firstVisibleCharacter' 0) `
    'The start of a repeated notice can be hidden by a previous TMP character offset.'

# Exercise panel sizing and page bounds without constructing Unity objects.
# A repeated presentation must not accumulate growth, and a low-resolution
# canvas must constrain even an oversized original panel.
$composeMessage = Get-StaticMethod `
    $runtimePanelPresentationType `
    "ComposeMessage" `
    2
$calculatePanelHeight = Get-StaticMethod `
    $runtimePanelPresentationType `
    "CalculatePanelHeight" `
    3
$calculatePageNumber = Get-StaticMethod `
    $runtimePanelPresentationType `
    "CalculatePageNumber" `
    3

$composedMessage = [string]$composeMessage.Invoke(
    $null,
    [object[]]@("Vanilla connection failed.", "Install Required Mod."))
$recomposedMessage = [string]$composeMessage.Invoke(
    $null,
    [object[]]@("Vanilla connection failed.", "Install Required Mod."))
Assert-True (
    $composedMessage -eq
        "Vanilla connection failed.`n`nInstall Required Mod." -and
    $recomposedMessage -eq $composedMessage) `
    "Connection-error message composition is not deterministic."
Assert-True (
    ([Text.RegularExpressions.Regex]::Matches(
        $composedMessage,
        [Text.RegularExpressions.Regex]::Escape(
            "Install Required Mod."))).Count -eq 1) `
    "Connection-error detail was duplicated in one composed message."
Assert-True (
    [string]$composeMessage.Invoke(
        $null,
        [object[]]@("Vanilla only.   ", "")) -eq "Vanilla only." -and
    [string]$composeMessage.Invoke(
        $null,
        [object[]]@("", "  Detail only.  ")) -eq "Detail only.") `
    "Connection-error composition no longer handles empty halves cleanly."

# Presentation must paginate the complete reason, not remove its tail. Cover
# ordinary new-character guidance, Korean, rich-text-looking literal input and
# large mod mismatch lists independently of a running Unity canvas.
$longModList = ((1..160 | ForEach-Object {
    "Required.Mod.$_ ($('VeryLongPrefabName' * 5)): expected 1.2.3, received 9.8.7."
}) -join "`n") + "`nEND_OF_REQUIRED_MOD_LIST"
$koreanPanelReason = -join [char[]]@(
    0xc800, 0xc7a5, 0xb41c, 0x20, 0xce90, 0xb9ad, 0xd130, 0xac00,
    0x20, 0xc5c6, 0xc2b5, 0xb2c8, 0xb2e4, 0x2e, 0x20, 0xc0c8,
    0x20, 0xce90, 0xb9ad, 0xd130, 0xb97c, 0x20, 0xb9cc, 0xb4e4,
    0xc5b4, 0x20, 0xc8fc, 0xc138, 0xc694, 0x2e)
foreach ($detailFixture in @(
    'This server has no saved character for the selected account and name. Your local character already has world progress and was not overwritten. Return to the lobby and create a new character.',
    ($koreanPanelReason + "`nEND_OF_KOREAN_REASON"),
    '<size=1><color=red>literal player text</color></size>',
    $longModList)) {
    $expectedMessage = "Failed to connect`n`n" + $detailFixture
    $actualMessage = [string]$composeMessage.Invoke(
        $null, [object[]]@('Failed to connect', $detailFixture))
    Assert-True ($actualMessage -ceq $expectedMessage) `
        "Connection-error composition truncated or reinterpreted a complete rejection reason."
}

$unchangedPanelHeight = [single]$calculatePanelHeight.Invoke(
    $null,
    [object[]]@([single]200, [single]50, [single]800))
$grownPanelHeight = [single]$calculatePanelHeight.Invoke(
    $null,
    [object[]]@([single]200, [single]500, [single]800))
$clampedPanelHeight = [single]$calculatePanelHeight.Invoke(
    $null,
    [object[]]@([single]200, [single]1000, [single]500))
$invalidRenderedHeight = [single]$calculatePanelHeight.Invoke(
    $null,
    [object[]]@([single]200, [single]::NaN, [single]800))
Assert-Near $unchangedPanelHeight 200 `
    "Short text changed the vanilla panel height."
Assert-Near $grownPanelHeight 640 `
    "Long text did not receive the expected chrome allowance."
Assert-Near $clampedPanelHeight 500 `
    "Long text exceeded the available canvas height."
Assert-Near $invalidRenderedHeight 200 `
    "An invalid TMP height produced an unsafe panel size."
$smallCanvasHeight = [single]$calculatePanelHeight.Invoke(
    $null, [object[]]@([single]700, [single]100, [single]480))
$repeatedPanelHeight = [single]$calculatePanelHeight.Invoke(
    $null, [object[]]@([single]200, [single]500, [single]800))
Assert-Near $smallCanvasHeight 480 `
    'A panel originally larger than the available canvas still extends off screen.'
Assert-Near $repeatedPanelHeight $grownPanelHeight `
    'Repeated panel presentation accumulates another height increase.'
foreach ($geometryFixture in @(
    @([single]::NaN, [single]300, [single]500),
    @([single]200, [single]::PositiveInfinity, [single]500),
    @([single]200, [single]300, [single]::NaN),
    @([single]200, [single]-100, [single]500),
    @([single]700, [single]1000, [single]180))) {
    $geometryResult = [single]$calculatePanelHeight.Invoke($null, [object[]]$geometryFixture)
    Assert-True (-not [single]::IsNaN($geometryResult) -and
        -not [single]::IsInfinity($geometryResult) -and $geometryResult -ge 0) `
        'A malformed sizing input produced non-finite or negative panel geometry.'
    if ($geometryFixture[2] -gt 0 -and -not [single]::IsNaN($geometryFixture[2])) {
        Assert-True ($geometryResult -le $geometryFixture[2]) `
            'Panel geometry escaped its finite canvas height bound.'
    }
}
foreach ($pageFixture in @(
    @(1, 1, 3, 2),
    @(3, 1, 3, 3),
    @(1, -1, 3, 1),
    @(99, 0, 4, 4),
    @(1, 1, 0, 1),
    @(1, -1, -10, 1),
    @([int]::MaxValue, [int]::MaxValue, 3, 3),
    @([int]::MinValue, [int]::MinValue, 3, 1))) {
    $pageResult = [int]$calculatePageNumber.Invoke(
        $null, [object[]]@([int]$pageFixture[0], [int]$pageFixture[1], [int]$pageFixture[2]))
    Assert-True ($pageResult -eq $pageFixture[3]) `
        'Pager navigation escaped its available pages or overflowed integer arithmetic.'
}

$tryParseEndpoint = Get-StaticMethod `
    $runtimeBrandingType `
    "TryParseDedicatedEndpoint" `
    4
$endpointArguments = [object[]]@(
    "play.example.test:2456",
    $null,
    [uint16]0,
    $null)
$validEndpoint = Invoke-TryMethod $tryParseEndpoint $endpointArguments
Assert-True (
    $validEndpoint.Succeeded -and
    $endpointArguments[1] -eq "play.example.test" -and
    $endpointArguments[2] -eq 2456) `
    "A canonical host:port endpoint was not accepted."

$defaultPortArguments = [object[]]@(
    "play.example.test",
    $null,
    [uint16]0,
    $null)
$defaultPortEndpoint = Invoke-TryMethod `
    $tryParseEndpoint `
    $defaultPortArguments
Assert-True (
    $defaultPortEndpoint.Succeeded -and
    $defaultPortArguments[1] -eq "play.example.test" -and
    $defaultPortArguments[2] -eq 2456) `
    "A host without an explicit port did not receive Valheim's 2456 default."

foreach ($invalidEndpoint in @(
    "",
    "play.example.test:0",
    "play.example.test:65536",
    "play example.test:2456",
    "https://play.example.test:2456/path",
    "user@play.example.test:2456")) {
    $invalidArguments = [object[]]@(
        $invalidEndpoint,
        $null,
        [uint16]0,
        $null)
    $invalidResult = Invoke-TryMethod $tryParseEndpoint $invalidArguments
    Assert-True (-not $invalidResult.Succeeded) `
        "Unsafe or ambiguous endpoint '$invalidEndpoint' was accepted."
}

# Destination construction is deliberately invoked without initializing Steam,
# opening a Unity scene, looking up friendship/lobbies, or installing an optional
# catalog. It must construct only the native values consumed after character choice.
$tryCreateDestination = Get-StaticMethod $runtimeBrandingType 'TryCreateJoinDestination' 3
foreach ($steamEndpoint in @('steam:76561198000000001', 'STEAM:76561198000000001', ' steam:76561198000000001 ')) {
    $destinationArguments = [object[]]@($steamEndpoint, $null, $null)
    $destinationResult = Invoke-TryMethod $tryCreateDestination $destinationArguments
    $destination = $destinationArguments[1]
    Assert-True ($destinationResult.Succeeded -and $destination.IsValid -and
        $destination.m_type.ToString() -eq 'SteamUser' -and
        $destination.SteamUser.m_joinUserID.m_SteamID -eq [uint64]76561198000000001 -and
        $destination.m_owner.IsValid -and $destination.m_owner.m_platform.ToString() -ceq 'Steam' -and
        $destination.m_owner.m_userID -ceq '76561198000000001' -and $destinationArguments[2] -ceq '') `
        'A canonical Steam-host endpoint did not round-trip through the native SteamUser/owner value constructors.'
    $nativeSteam = [ServerJoinDataSteamUser]::new([uint64]76561198000000001)
    $nativeDestination = [ServerJoinData]::new($nativeSteam)
    Assert-True ($destination.Equals($nativeDestination)) `
        'The quick-connect factory produces a different destination than vanilla Steam-user construction.'
}
foreach ($dedicatedFixture in @(
    @{ Endpoint = 'play.example.test'; Host = 'play.example.test'; Port = 2456 },
    @{ Endpoint = 'play.example.test:2457'; Host = 'play.example.test'; Port = 2457 },
    @{ Endpoint = '[2001:db8::1]:2456'; Host = '2001:db8::1'; Port = 2456 })) {
    $destinationArguments = [object[]]@($dedicatedFixture.Endpoint, $null, $null)
    $destinationResult = Invoke-TryMethod $tryCreateDestination $destinationArguments
    $destination = $destinationArguments[1]
    Assert-True ($destinationResult.Succeeded -and $destination.IsValid -and
        $destination.m_type.ToString() -eq 'Dedicated' -and
        $destination.Dedicated.m_host -ceq $dedicatedFixture.Host -and
        $destination.Dedicated.m_port -eq [uint16]$dedicatedFixture.Port -and $destinationArguments[2] -ceq '') `
        'Adding Steam-host endpoints changed the native dedicated-host destination or default port.'
}
foreach ($invalidDestination in @(
    $null, '', '   ', 'steam:', 'steam:0', 'steam:1', 'steam:2456',
    'steam:76561197960265728', # Public individual with zero account ID.
    'steam:76561193665298433', # Individual account with non-desktop instance.
    'steam:85568397215006721', # Valid public game-server ID, not a host user.
    'steam:103582791429521409', # Clan identity, not a host user.
    'steam:109775241001234567', # Lobby identity, not a host user.
    'steam:076561198000000001', 'steam:+76561198000000001', 'steam:-76561198000000001',
    'steam:7656119800000000x', 'steam:76561198000000001.0', 'steam:7.6561198e16',
    'steam:18446744073709551616', 'steam:76561198000000001:2456', 'steam:76561198000000001/path',
    'steam://76561198000000001', 'steam:76561198000000001?password=secret', 'steam: 76561198000000001',
    "steam:76561198`n000000001", 'play.example.test:0', 'https://play.example.test:2456/path')) {
    $destinationArguments = [object[]]@($invalidDestination, $null, $null)
    $destinationResult = Invoke-TryMethod $tryCreateDestination $destinationArguments
    Assert-True (-not $destinationResult.Succeeded -and -not $destinationArguments[1].IsValid -and
        -not [string]::IsNullOrWhiteSpace([string]$destinationArguments[2])) `
        'An invalid or wrong-kind destination was accepted, retained a usable native value, or lacked a rejection reason.'
}

$tryResolveLogoPath = Get-StaticMethod `
    $runtimeBrandingType `
    "TryResolveLogoPath" `
    4
$logoRoot = [IO.Path]::Combine(
    [IO.Path]::GetTempPath(),
    "BepInEx-branding-root")
# This resolver is lexical: no live profile or test asset directory is created,
# and the returned file is not opened by these validation cases.
foreach ($validLogoPath in @(
    'config/ServerManager/MidcrownSummer.png',
    'config\ServerManager\MidcrownSummer.png',
    'plugins/ServerManager/MidcrownSummer.png',
    'plugins\MyModpack\assets\logo.png',
    'logo.png',
    'custom\arbitrary\nested\directory\logo.PNG')) {
    $validPathArguments = [object[]]@($logoRoot, $validLogoPath, $null, $null)
    $validPath = Invoke-TryMethod $tryResolveLogoPath $validPathArguments
    $expectedLogoPath = [IO.Path]::GetFullPath((Join-Path $logoRoot $validLogoPath))
    Assert-True ($validPath.Succeeded -and $validPathArguments[2] -eq $expectedLogoPath) `
        ("A root-relative PNG below BepInEx was rejected or resolved under an extra directory: " + $validLogoPath)
}

foreach ($invalidLogoPath in @(
    "",
    "..\outside.png",
    "plugins\..\..\outside.png",
    "../BepInEx-branding-root-sibling/logo.png",
    "..\BepInEx-branding-root2\logo.png",
    "C:\Windows\logo.png",
    "C:logo.png",
    "\\server\share\logo.png",
    "/outside/logo.png",
    "logo.jpg",
    "logo.png.jpg",
    "logo",
    "logo.png:preview.png",
    "folder:logo.png",
    "https://example.test/logo.png",
    "http://example.test/logo.png",
    "file:///C:/logo.png")) {
    $invalidPathArguments = [object[]]@(
        $logoRoot,
        $invalidLogoPath,
        $null,
        $null)
    $invalidPath = Invoke-TryMethod `
        $tryResolveLogoPath `
        $invalidPathArguments
    Assert-True (-not $invalidPath.Succeeded) `
        "Unsafe/non-PNG logo path '$invalidLogoPath' was accepted."
}

$tryValidatePng = Get-StaticMethod `
    $runtimeBrandingType `
    "TryValidatePng" `
    4
[byte[]]$validPngHeader = @(
    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
    0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
    0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x10,
    0x08, 0x06, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00)
$pngArguments = [object[]]@(
    $validPngHeader,
    0,
    0,
    $null)
$validPng = Invoke-TryMethod $tryValidatePng $pngArguments
Assert-True (
    $validPng.Succeeded -and
    $pngArguments[1] -eq 32 -and
    $pngArguments[2] -eq 16) `
    "A canonical PNG signature/IHDR dimension header was not accepted."

[byte[]]$invalidPngHeader = $validPngHeader.Clone()
$invalidPngHeader[1] = 0
$invalidPngArguments = [object[]]@(
    $invalidPngHeader,
    0,
    0,
    $null)
$invalidPng = Invoke-TryMethod `
    $tryValidatePng `
    $invalidPngArguments
Assert-True (-not $invalidPng.Succeeded) `
    "A non-PNG signature was accepted as a branding image."

$truncatedPngArguments = [object[]]@(
    [byte[]]::new(32),
    0,
    0,
    $null)
$truncatedPng = Invoke-TryMethod `
    $tryValidatePng `
    $truncatedPngArguments
Assert-True (-not $truncatedPng.Succeeded) `
    "A truncated PNG header was accepted as a branding image."

$oversizedFileArguments = [object[]]@(
    [byte[]]::new((8 * 1024 * 1024) + 1),
    0,
    0,
    $null)
$oversizedFile = Invoke-TryMethod `
    $tryValidatePng `
    $oversizedFileArguments
Assert-True (-not $oversizedFile.Succeeded) `
    "A branding image above the 8 MiB file limit was accepted."

[byte[]]$oversizedPngHeader = $validPngHeader.Clone()
[byte[]]$oversizedDimension = @(0x00, 0x00, 0x40, 0x00)
[Array]::Copy($oversizedDimension, 0, $oversizedPngHeader, 16, 4)
$oversizedPngArguments = [object[]]@(
    $oversizedPngHeader,
    0,
    0,
    $null)
$oversizedPng = Invoke-TryMethod `
    $tryValidatePng `
    $oversizedPngArguments
Assert-True (-not $oversizedPng.Succeeded) `
    "An unreasonably large PNG dimension was accepted."

Write-Host "Client menu branding smoke tests passed."
