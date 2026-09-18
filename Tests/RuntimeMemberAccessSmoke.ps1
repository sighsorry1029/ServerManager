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

    return $Type.IsPublic -or $Type.IsNestedPublic
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
foreach ($assemblyName in @(
    "assembly_valheim.dll",
    "assembly_utils.dll",
    "assembly_guiutils.dll")) {
    $assemblyPath = Join-Path $managedRoot $assemblyName
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
            if ($member -is [Mono.Cecil.FieldReference] -and
                $runtimeTypes.ContainsKey($member.DeclaringType.FullName)) {
                $runtimeType = $runtimeTypes[$member.DeclaringType.FullName]
                $runtimeField = $runtimeType.Fields |
                    Where-Object FullName -eq $member.FullName |
                    Select-Object -First 1
                if ($null -ne $runtimeField -and -not $runtimeField.IsPublic) {
                    $violations.Add(
                        "$($method.FullName) directly references non-public field $($member.FullName)")
                }
            }
            elseif ($member -is [Mono.Cecil.MethodReference] -and
                    $runtimeTypes.ContainsKey($member.DeclaringType.FullName)) {
                $runtimeType = $runtimeTypes[$member.DeclaringType.FullName]
                $runtimeMethod = $runtimeType.Methods |
                    Where-Object FullName -eq $member.FullName |
                    Select-Object -First 1
                if ($null -ne $runtimeMethod -and -not $runtimeMethod.IsPublic) {
                    $violations.Add(
                        "$($method.FullName) directly calls non-public method $($member.FullName)")
                }
            }
        }
    }
}

foreach ($reference in $plugin.MainModule.GetTypeReferences()) {
    if (-not $runtimeTypes.ContainsKey($reference.FullName)) {
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
        "ServerManager contains runtime-inaccessible Valheim references:`n" +
        ($details -join "`n"))
}

Write-Output (
    "Runtime member access smoke test passed: required reflection schema " +
    "exists and there are no direct non-public Valheim member or type references.")
