param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"

function Get-AllCecilTypes {
    param($Types)

    foreach ($type in $Types) {
        $type
        if ($type.HasNestedTypes) {
            Get-AllCecilTypes $type.NestedTypes
        }
    }
}

function Test-ExternallyVisibleType {
    param($Type)

    while ($null -ne $Type) {
        if ($Type.IsNested) {
            if (-not $Type.IsNestedPublic) { return $false }
        }
        elseif (-not $Type.IsPublic) { return $false }
        $Type = $Type.DeclaringType
    }
    return $true
}

function Test-RuntimeReference {
    param($Type)

    return $Type.Scope -is [Mono.Cecil.AssemblyNameReference] -and
        $script:runtimeAssemblyNames -contains $Type.Scope.Name
}

function Assert-RuntimeFieldExists {
    param(
        [hashtable]$RuntimeTypes,
        [string]$TypeName,
        [string]$FieldName
    )

    if (-not $RuntimeTypes.ContainsKey($TypeName) -or
        $null -eq ($RuntimeTypes[$TypeName].Fields |
            Where-Object Name -eq $FieldName |
            Select-Object -First 1)) {
        throw "Required Valheim runtime field changed: $TypeName.$FieldName"
    }
}

function Assert-RuntimeMethodExists {
    param(
        [hashtable]$RuntimeTypes,
        [string]$TypeName,
        [string]$MethodName,
        [string[]]$ParameterTypes
    )

    if (-not $RuntimeTypes.ContainsKey($TypeName)) {
        throw "Required Valheim runtime type changed: $TypeName"
    }

    $method = $RuntimeTypes[$TypeName].Methods |
        Where-Object {
            $_.Name -eq $MethodName -and
            $_.Parameters.Count -eq $ParameterTypes.Count -and
            (($_.Parameters | ForEach-Object ParameterType |
                ForEach-Object FullName) -join "`n") -ceq
                ($ParameterTypes -join "`n")
        } |
        Select-Object -First 1
    if ($null -eq $method) {
        throw (
            "Required Valheim runtime method changed: " +
            "$TypeName.$MethodName(" + ($ParameterTypes -join ", ") + ")")
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath "valheim_Data\Managed"
$cecilPath = Join-Path $GamePath "BepInEx\core\Mono.Cecil.dll"

if (-not (Test-Path -LiteralPath $pluginPath)) {
    throw "Build ServerManager before running the runtime-member access smoke test."
}

if (-not (Test-Path -LiteralPath $cecilPath)) {
    throw "Mono.Cecil from BepInEx was not found."
}

[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null

$runtimeTypes = @{}
$runtimeAssemblyNames = @('assembly_valheim', 'assembly_utils', 'assembly_guiutils')
foreach ($assemblyName in $runtimeAssemblyNames) {
    $assemblyPath = Join-Path $managedRoot ($assemblyName + '.dll')
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        throw "The actual Valheim runtime assembly is missing: $assemblyPath"
    }

    $definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyPath)
    foreach ($type in @(Get-AllCecilTypes $definition.MainModule.Types)) {
        $runtimeTypes[$type.FullName] = $type
    }
}

foreach ($requiredField in @(
    [pscustomobject]@{ Type = "ZNet"; Name = "m_openServer" },
    [pscustomobject]@{ Type = "ZNet"; Name = "m_connectionStatus" },
    [pscustomobject]@{ Type = "ZNet"; Name = "m_bannedList" },
    [pscustomobject]@{ Type = "ZNet"; Name = "m_adminList" },
    [pscustomobject]@{ Type = "ZNet"; Name = "m_serverPassword" },
    [pscustomobject]@{ Type = "ZRpc"; Name = "m_socket" },
    [pscustomobject]@{ Type = "ZRpc"; Name = "m_functions" },
    [pscustomobject]@{ Type = "ZRpc"; Name = "m_DEBUG" },
    [pscustomobject]@{ Type = "ZRpc"; Name = "m_sentPackages" },
    [pscustomobject]@{ Type = "ZRpc"; Name = "m_sentData" },
    [pscustomobject]@{ Type = "Game"; Name = "m_playerProfile" },
    [pscustomobject]@{ Type = "PlayerProfile"; Name = "m_playerData" },
    [pscustomobject]@{ Type = "PlayerProfile"; Name = "m_worldData" },
    [pscustomobject]@{ Type = "Player"; Name = "m_timeSinceDeath" },
    [pscustomobject]@{ Type = "Player"; Name = "m_guardianPower" },
    [pscustomobject]@{ Type = "Player"; Name = "m_guardianPowerCooldown" },
    [pscustomobject]@{ Type = "Player"; Name = "m_skinColor" },
    [pscustomobject]@{ Type = "ZDOMan"; Name = "m_destroySendList" },
    [pscustomobject]@{ Type = "Terminal"; Name = "commands" },
    [pscustomobject]@{
        Type = "Version"
        Name = "FirstVersionWithNetworkVersion"
    })) {
    Assert-RuntimeFieldExists `
        $runtimeTypes `
        $requiredField.Type `
        $requiredField.Name
}

foreach ($requiredMethod in @(
    [pscustomobject]@{
        Type = "ZNet"
        Name = "SendPeerInfo"
        Parameters = @("ZRpc", "System.String")
    },
    [pscustomobject]@{
        Type = "ZNet"
        Name = "InternalKick"
        Parameters = @("ZNetPeer")
    },
    [pscustomobject]@{
        Type = "Character"
        Name = "CheckDeath"
        Parameters = @()
    },
    [pscustomobject]@{
        Type = "ZDOMan"
        Name = "FlushClientObjects"
        Parameters = @()
    },
    [pscustomobject]@{
        Type = "ZDOMan"
        Name = "SendDestroyed"
        Parameters = @()
    },
    [pscustomobject]@{
        Type = "Inventory"
        Name = "AddItem"
        Parameters = @("ItemDrop/ItemData", "System.Int32", "System.Int32", "System.Int32", "System.Boolean")
    },
    [pscustomobject]@{
        Type = "Inventory"
        Name = "Changed"
        Parameters = @("System.Boolean", "System.Boolean")
    })) {
    Assert-RuntimeMethodExists `
        $runtimeTypes `
        $requiredMethod.Type `
        $requiredMethod.Name `
        $requiredMethod.Parameters
}

$plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
$violations = [System.Collections.Generic.List[string]]::new()

foreach ($type in @(Get-AllCecilTypes $plugin.MainModule.Types)) {
    foreach ($method in $type.Methods) {
        if (-not $method.HasBody) {
            continue
        }

        foreach ($instruction in $method.Body.Instructions) {
            $member = $instruction.Operand
            if ($member -isnot [Mono.Cecil.FieldReference] -and
                $member -isnot [Mono.Cecil.MethodReference]) { continue }
            if (-not (Test-RuntimeReference $member.DeclaringType)) { continue }
            if (-not $runtimeTypes.ContainsKey($member.DeclaringType.FullName)) {
                $violations.Add("$($method.FullName) references missing type $($member.DeclaringType.FullName)")
                continue
            }
            $runtimeType = $runtimeTypes[$member.DeclaringType.FullName]
            if ($member -is [Mono.Cecil.FieldReference]) {
                $runtimeField = $runtimeType.Fields |
                    Where-Object FullName -ceq $member.FullName |
                    Select-Object -First 1
                if ($null -eq $runtimeField) {
                    $violations.Add("$($method.FullName) references missing field $($member.FullName)")
                }
                elseif (-not $runtimeField.IsPublic) {
                    $violations.Add(
                        "$($method.FullName) directly references non-public field $($member.FullName)")
                }
            }
            else {
                $lookup = if ($member -is [Mono.Cecil.GenericInstanceMethod]) { $member.ElementMethod } else { $member }
                $runtimeMethod = $runtimeType.Methods |
                    Where-Object FullName -ceq $lookup.FullName |
                    Select-Object -First 1
                if ($null -eq $runtimeMethod) {
                    $violations.Add("$($method.FullName) calls missing method $($lookup.FullName)")
                }
                elseif (-not $runtimeMethod.IsPublic) {
                    $violations.Add(
                        "$($method.FullName) directly calls non-public method $($member.FullName)")
                }
            }
        }
    }
}

foreach ($reference in $plugin.MainModule.GetTypeReferences()) {
    if (-not (Test-RuntimeReference $reference)) { continue }
    if (-not $runtimeTypes.ContainsKey($reference.FullName)) {
        $violations.Add("ServerManager references missing runtime type $($reference.FullName)")
        continue
    }

    $runtimeType = $runtimeTypes[$reference.FullName]
    if (-not (Test-ExternallyVisibleType $runtimeType)) {
        $violations.Add(
            "ServerManager references non-public runtime type $($reference.FullName)")
    }
}

if ($violations.Count -ne 0) {
    $details = $violations | Sort-Object -Unique
    throw (
        "ServerManager contains unresolved or runtime-inaccessible Valheim references:`n" +
        ($details -join "`n"))
}

Write-Output (
    "Runtime member access smoke test passed: required reflection schema " +
    "exists and there are no missing or non-public direct Valheim member or type references.")
