param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$cecilPath = Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll'
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    ++$script:assertions
    if (-not $Condition) { throw $Message }
}
foreach ($path in @($pluginPath, $cecilPath)) {
    Assert-True (Test-Path -LiteralPath $path) "Plugin incompatibility smoke prerequisite missing: $path. Build first."
}

# Read compiled metadata and method bodies only. No plugin Awake, Harmony patch,
# Unity object, NewCron plugin, or Steam API is loaded or executed by this test.
[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory((Join-Path $GamePath 'BepInEx\core'))
$resolver.AddSearchDirectory((Join-Path $GamePath 'valheim_Data\Managed'))
$resolver.AddSearchDirectory((Split-Path -Parent $pluginPath))
$parameters = New-Object Mono.Cecil.ReaderParameters
$parameters.AssemblyResolver = $resolver
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath, $parameters)
try {
    $plugin = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerManagerPlugin'
    Assert-True ($null -ne $plugin) 'The built ServerManager plugin entry point is missing.'
    $identity = @($plugin.CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin'
    })
    Assert-True ($identity.Count -eq 1 -and $identity[0].ConstructorArguments[0].Value -ceq 'sighsorry.ServerManager') `
        'The incompatibility declaration must remain on the actual ServerManager plugin.'
    $incompatibilities = @($plugin.CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'BepInEx.BepInIncompatibility'
    })
    $playerLimitConflicts = @($incompatibilities | Where-Object {
        $_.ConstructorArguments.Count -eq 1 -and
        $_.ConstructorArguments[0].Value -ceq 'Azumatt.MaxPlayerCount'
    })
    Assert-True ($playerLimitConflicts.Count -eq 1) `
        'Declare the exact case-sensitive Azumatt.MaxPlayerCount GUID once, so BepInEx skips ServerManager before Awake when both are present.'

    $dependencies = @($plugin.CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'BepInEx.BepInDependency'
    })
    foreach ($dependency in $dependencies) {
        $guid = [string]$dependency.ConstructorArguments[0].Value
        if ($guid -ine 'blizz.NewCron') { continue }
        # The GUID-only and GUID/version constructors are hard dependencies;
        # an explicitly supplied DependencyFlags value is SoftDependency=2.
        $soft = $dependency.ConstructorArguments.Count -eq 2 -and
            $dependency.ConstructorArguments[1].Type.FullName -eq 'BepInEx.BepInDependency/DependencyFlags' -and
            [int]$dependency.ConstructorArguments[1].Value -eq 2
        Assert-True $soft 'Integrated scheduling must not hard-depend on the external blizz.NewCron plugin.'
    }
    Assert-True (@($definition.MainModule.AssemblyReferences | Where-Object Name -eq 'NewCron').Count -eq 0) `
        'Integrated scheduling must not require the external NewCron assembly at runtime.'

    # Declaring plugin incompatibility must not remove or conditionally disable
    # essential authentication hooks for normal, nonconflicting installations.
    $peerPatch = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerPeerInfoPatch'
    Assert-True ($null -ne $peerPatch) 'The critical PeerInfo authentication patch class is missing.'
    $targets = @($peerPatch.CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' -and
        $_.ConstructorArguments.Count -ge 2 -and
        $_.ConstructorArguments[0].Value.FullName -eq 'ZNet' -and
        $_.ConstructorArguments[1].Value -eq 'RPC_PeerInfo'
    })
    Assert-True ($targets.Count -eq 1) 'Critical authentication hooks must still target vanilla ZNet.RPC_PeerInfo.'
    foreach ($entry in @(
        @('Prefix', 'BeforeServerPeerInfo'),
        @('Postfix', 'AfterServerPeerInfo'),
        @('Finalizer', 'HandlePeerInfoException'),
        @('Finalizer', 'AbortPeerInfoAuthentication')
    )) {
        $method = $peerPatch.Methods | Where-Object Name -eq $entry[0]
        Assert-True ($null -ne $method -and $method.HasBody -and @($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq 'ServerManager.ServerManagerRuntime' -and
            $_.Operand.Name -eq $entry[1]
        }).Count -eq 1) "Critical authentication hook was removed: $($entry[0]) -> $($entry[1])."
    }
}
finally { $definition.Dispose(); $resolver.Dispose() }
Write-Output "Plugin incompatibility metadata and preserved PeerInfo authentication smoke passed ($script:assertions assertions)."
