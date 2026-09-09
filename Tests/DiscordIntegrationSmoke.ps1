param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-BytesEqual([byte[]]$Actual, [byte[]]$Expected, [string]$Message) {
    Assert-True ($Actual.Length -eq $Expected.Length -and
        [Convert]::ToBase64String($Actual) -ceq [Convert]::ToBase64String($Expected)) $Message
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
$parameters = [Mono.Cecil.ReaderParameters]::new()
$parameters.ReadingMode = [Mono.Cecil.ReadingMode]::Deferred
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath, $parameters)
try {
    $module = $assembly.MainModule
    # Both distribution archives retain the complete notices inside their single DLL.
    foreach ($notice in @('LICENSE.txt', 'THIRD_PARTY_NOTICES.md')) {
        $resourceName = 'ServerManager.' + $notice
        $resource = @($module.Resources | Where-Object Name -eq $resourceName)
        Assert-True ($resource.Count -eq 1 -and $resource[0] -is [Mono.Cecil.EmbeddedResource]) `
            "Missing or ambiguous embedded notice: $resourceName"
        Assert-BytesEqual $resource[0].GetResourceData() ([IO.File]::ReadAllBytes((Join-Path $projectRoot $notice))) `
            "Embedded notice does not preserve the complete source file: $resourceName"
    }
    if ($Configuration -eq 'Release') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $version = $assembly.Name.Version.ToString(3)
        $sourceManifestPath = Join-Path $projectRoot 'Thunderstore\manifest.json'
        $manifest = [IO.File]::ReadAllText($sourceManifestPath) | ConvertFrom-Json
        Assert-True ($manifest.version_number -ceq $version) 'Release Build did not update the source manifest from the built DLL version.'
        $thunderstoreFiles = @{
            'ServerManager.dll' = $pluginPath
            'README.md' = Join-Path $projectRoot 'README.md'
            'CHANGELOG.md' = Join-Path $projectRoot 'Thunderstore\CHANGELOG.md'
            'manifest.json' = $sourceManifestPath
            'icon.png' = Join-Path $projectRoot 'Thunderstore\icon.png'
            'ServerManager.English.yml' = Join-Path $projectRoot 'translations\English.yml'
        }
        foreach ($distribution in @('Thunderstore', 'Nexus')) {
            $expectedFiles = if ($distribution -eq 'Thunderstore') { $thunderstoreFiles } else { @{ 'ServerManager.dll' = $pluginPath } }
            $archivePath = Join-Path $projectRoot "$distribution\ServerManager_v$version.zip"
            Assert-True ([IO.File]::Exists($archivePath)) "Release Build did not create $distribution package: $archivePath"
            $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
            try {
                $entryNames = @($archive.Entries | ForEach-Object FullName)
                Assert-True ($entryNames.Count -eq $expectedFiles.Count) "$distribution package contains extra or missing files."
                foreach ($name in $expectedFiles.Keys) {
                    Assert-True (@($entryNames | Where-Object { $_ -ceq $name }).Count -eq 1) `
                        "$distribution package must contain exactly one flat file named $name."
                    $entryStream = $archive.GetEntry($name).Open()
                    $entryBytes = [IO.MemoryStream]::new()
                    try {
                        $entryStream.CopyTo($entryBytes)
                        Assert-BytesEqual $entryBytes.ToArray() ([IO.File]::ReadAllBytes($expectedFiles[$name])) `
                            "$distribution package contains stale or changed content: $name"
                    }
                    finally {
                        $entryStream.Dispose()
                        $entryBytes.Dispose()
                    }
                }
            }
            finally { $archive.Dispose() }
        }
    }
    $references = @($module.AssemblyReferences | ForEach-Object Name)
    foreach ($forbidden in @('Newtonsoft.Json', 'Discord.Net', 'Discord.Net.Core', 'Discord.Net.WebSocket',
        'OrbOfDiscord', 'OrbOfDiscord.Core', 'YamlDotNet')) {
        Assert-True ($references -notcontains $forbidden) "Single DLL still requires $forbidden."
    }
    Assert-True ($null -ne $module.GetType('Newtonsoft.Json.Linq.JObject')) 'Internalized JSON dependency missing.'
    $yamlParser = $module.GetType('YamlDotNet.Core.Parser')
    Assert-True ($null -ne $yamlParser -and -not $yamlParser.IsPublic) 'Internalized YAML dependency missing or exposed publicly.'
    $plugin = $module.GetType('ServerManager.ServerManagerPlugin')
    Assert-True (@($plugin.CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin'
    }).Count -eq 1) 'Plugin declaration missing.'
    $incompatibilities = @($plugin.CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'BepInEx.BepInIncompatibility'
    })
    Assert-True ($incompatibilities.Count -eq 1 -and
        $incompatibilities[0].ConstructorArguments[0].Value -ceq 'Azumatt.MaxPlayerCount') `
        'Only the explicitly incompatible MaxPlayerCount plugin may prevent ServerManager loading; other listener checks remain runtime fail-closed.'

    $runtime = $module.GetType('ServerManager.Discord.DiscordRuntime')
    $settings = $module.GetType('ServerManager.Discord.DiscordSettings')
    $events = $module.GetType('ServerManager.Events.ServerEventRuntime')
    $core = $module.GetType('ServerManager.ServerManagerRuntime')
    $terminalCommands = $module.GetType('ServerManager.ServerManagerTerminalCommands')
    $registerTerminal = @($terminalCommands.Methods | Where-Object Name -eq 'EnsureRegistered')[0]
    Assert-True (@($registerTerminal.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'ServerManager.Commands.ServerCommands' -and
        $_.Operand.Name -eq 'get_FlatCommandNames'
    }).Count -eq 1) 'Native F5 registration must enumerate the shared flat feature catalog.'
    $registrationStrings = @($registerTerminal.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr
    } | ForEach-Object Operand)
    Assert-True ($registrationStrings -contains 'sm:' -and $registrationStrings -notcontains 'sm') 'Native F5 must register colon-prefixed feature commands, not the removed generic sm root.'
    foreach ($type in @($runtime, $settings, $module.GetType('ServerManager.Discord.DiscordGateway'),
        $module.GetType('ServerManager.Discord.DiscordHttp'), $module.GetType('ServerManager.Discord.DiscordCommands'))) {
        Assert-True ($null -ne $type -and -not $type.IsPublic) 'Discord implementation must be internal and bundled.'
    }
    $start = @($runtime.Methods | Where-Object Name -eq 'Start')[0]
    $serverCheck = @($start.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'IsServer' })[0]
    $configRead = @($start.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $settings.FullName -and $_.Operand.Name -eq 'Load' })[0]
    Assert-True ($null -ne $serverCheck -and $null -ne $configRead -and $serverCheck.Offset -lt $configRead.Offset) 'Credentials load before server authority is checked.'
    $loadCalls = @($module.Types | Where-Object { $_.Namespace -eq 'ServerManager.Discord' } | ForEach-Object {
        $_.Methods | Where-Object HasBody | ForEach-Object { $_.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $settings.FullName -and $_.Operand.Name -eq 'Load'
        } }
    })
    Assert-True ($loadCalls.Count -eq 1) 'Discord settings gained a second, potentially client-side load path.'
    $settingsStrings = @($settings.Methods | Where-Object HasBody | ForEach-Object {
        $_.Body.Instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr } | ForEach-Object Operand
    })
    Assert-True ($settingsStrings -contains 'discord.yml') 'Discord settings no longer load the server-only YAML file.'
    Assert-True ($settingsStrings -notcontains 'discord.cfg') 'Removed Discord cfg compatibility path remains in the settings loader.'
    foreach ($removed in @('AdminRoleIds', 'AllowedCommands', 'Grants', 'EnableRcon')) {
        Assert-True (@($settings.Properties | Where-Object Name -eq $removed).Count -eq 0) "Removed Discord authorization field remains: $removed"
    }
    Assert-True ($null -eq $module.GetType('ServerManager.Discord.DiscordGrant')) 'Removed per-command grant type remains bundled.'
    $discordCommands = $module.GetType('ServerManager.Discord.DiscordCommands')
    $authorize = @($discordCommands.Methods | Where-Object Name -eq 'IsAuthorized')[0]
    Assert-True ($authorize.Parameters.Count -eq 4) 'Discord authorization must use command, guild, channel and individual user only.'
    $constructor = @($discordCommands.Methods | Where-Object Name -eq '.ctor')[0]
    Assert-True (@($constructor.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Newobj -and $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq 'ServerManager.Discord.DiscordRconCapture'
    }).Count -eq 1) 'Discord command runtime must initialize the RCON capture without an obsolete opt-in switch.'
    $rcon = $module.GetType('ServerManager.Discord.DiscordRconCapture')
    $adapterExecute = @($rcon.Methods | Where-Object Name -eq 'Execute')[0]
    $consoleExecutor = $module.GetType('ServerManager.Commands.ServerConsoleExecutor')
    Assert-True ($null -ne $consoleExecutor) 'Bounded console execution must be shared independently of Discord.'
    Assert-True (@($adapterExecute.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $consoleExecutor.FullName -and
        $_.Operand.Name -eq 'Execute'
    }).Count -eq 1) 'Discord must delegate each raw command to the shared executor exactly once.'
    $executeRcon = @($consoleExecutor.Methods | Where-Object { $_.Name -eq 'Execute' -and $_.Parameters.Count -eq 3 })[0]
    foreach ($bridge in @('ExecuteConsoleKick', 'DescribeWorldSaveRequest')) {
        Assert-True (@($executeRcon.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $events.FullName -and
            $_.Operand.Name -eq $bridge
        }).Count -eq 1) "Raw RCON lost its verified save/operational-kick bridge: $bridge"
    }
    $cheatRestored = $false
    foreach ($handler in $executeRcon.Body.ExceptionHandlers | Where-Object HandlerType -eq 'Finally') {
        $instructions = @($executeRcon.Body.Instructions | Where-Object {
            $_.Offset -ge $handler.HandlerStart.Offset -and
            ($null -eq $handler.HandlerEnd -or $_.Offset -lt $handler.HandlerEnd.Offset)
        })
        if (@($instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'CheatField'
        }).Count -gt 0 -and @($instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'SetValue'
        }).Count -gt 0) { $cheatRestored = $true }
    }
    Assert-True $cheatRestored 'RCON must restore the previous cheat flag in a finally block, including command/validation failures.'

    $worldStart = @($events.Methods | Where-Object Name -eq 'OnServerStarted')[0]
    $discordStart = @($worldStart.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $runtime.FullName -and $_.Operand.Name -eq 'Start' })[0]
    $firstPublish = @($worldStart.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Publish' })[0]
    Assert-True ($null -ne $discordStart -and $discordStart.Offset -lt $firstPublish.Offset) 'Startup event can be missed by embedded Discord.'
    # Webhook presentation must not remove the existing audit/API events.
    $worldStartStrings = @($worldStart.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr
    } | ForEach-Object Operand)
    Assert-True ($worldStartStrings -contains 'server.started') 'Removing the startup webhook must retain the internal server.started event.'
    $playerReady = @($events.Methods | Where-Object Name -eq 'OnPlayerReady')[0]
    $readyInstructions = @($playerReady.Body.Instructions)
    $readyStrings = @($readyInstructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr
    } | ForEach-Object Operand)
    Assert-True ($readyStrings -contains 'player.first_join' -and $readyStrings -contains 'player.login') `
        'Merged login webhooks must preserve the separate first-join and login audit/API events.'
    Assert-True (@($readyInstructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Publish'
    }).Count -eq 2) 'Player readiness gained a duplicate event or lost its existing first-join/login publication.'
    $firstJoinParameter = @($playerReady.Parameters | Where-Object Name -eq 'firstJoin')[0]
    Assert-True ($null -ne $firstJoinParameter -and $firstJoinParameter.Index -eq 3 -and
        $firstJoinParameter.ParameterType.FullName -eq 'System.Boolean') 'Login first-join metadata must use the authoritative readiness flag.'
    $firstJoinKey = @($readyInstructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and $_.Operand -eq 'first_join'
    })
    Assert-True ($firstJoinKey.Count -eq 1) 'The login event must contain exactly one first_join metadata field.'
    $followingFields = @($readyInstructions | Where-Object {
        $_.Offset -gt $firstJoinKey[0].Offset -and $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Fields'
    })[0]
    Assert-True ($null -ne $followingFields) 'The first_join metadata is not connected to the login fields.'
    $flagInstructions = @($readyInstructions | Where-Object {
        $_.Offset -gt $firstJoinKey[0].Offset -and $_.Offset -lt $followingFields.Offset
    })
    $loadsFirstJoin = @($flagInstructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldarg_3 -or
        ($_.Operand -is [Mono.Cecil.ParameterDefinition] -and $_.Operand.Name -eq 'firstJoin')
    })
    $flagBranches = @($flagInstructions | Where-Object {
        $_.OpCode.Code -in @([Mono.Cecil.Cil.Code]::Brtrue, [Mono.Cecil.Cil.Code]::Brtrue_S,
            [Mono.Cecil.Cil.Code]::Brfalse, [Mono.Cecil.Cil.Code]::Brfalse_S)
    })
    $flagStrings = @($flagInstructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr
    } | ForEach-Object Operand)
    Assert-True ($loadsFirstJoin.Count -eq 1 -and $flagBranches.Count -eq 1 -and
        $flagStrings.Count -eq 2 -and $flagStrings -contains 'true' -and $flagStrings -contains 'false') `
        'The first_join login field must preserve both true and false readiness values, not assume every login is new.'
    $worldStop = @($events.Methods | Where-Object Name -eq 'OnServerShutdown')[0]
    $discordStop = @($worldStop.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $runtime.FullName -and $_.Operand.Name -eq 'Stop' })[0]
    $lastPublish = @($worldStop.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Publish' })[0]
    Assert-True ($null -ne $discordStop -and $lastPublish.Offset -lt $discordStop.Offset) 'Shutdown stops Discord before the final event is queued.'
    $publish = @($events.Methods | Where-Object Name -eq 'Publish')[0]
    $privacy = @($publish.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'IsLogOnlyChatKind' })[0]
    $outbound = @($publish.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $runtime.FullName })[0]
    Assert-True ($privacy.Offset -lt $outbound.Offset) 'Discord route bypasses the private-chat filter.'

    $admission = @($events.Methods | Where-Object Name -eq 'SetCommandAdmission')[0]
    Assert-True ($null -ne $admission) 'World-bound command admission is missing.'
    foreach ($name in @('Initialize', 'Shutdown', 'OnServerStarted', 'OnServerShutdown')) {
        $method = @($events.Methods | Where-Object Name -eq $name)[0]
        Assert-True (@($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'SetCommandAdmission'
        }).Count -gt 0) "$name does not retire queued commands at the world boundary."
    }
    $allTypes = [Collections.Generic.Queue[object]]::new()
    foreach ($type in $module.Types | Where-Object { $_.Namespace -eq 'ServerManager.Discord' }) { $allTypes.Enqueue($type) }
    while ($allTypes.Count -gt 0) {
        $type = $allTypes.Dequeue()
        foreach ($nested in $type.NestedTypes) { $allTypes.Enqueue($nested) }
        foreach ($method in $type.Methods | Where-Object HasBody) {
            foreach ($instruction in $method.Body.Instructions) {
                $operand = $instruction.Operand
                if ($operand -isnot [Mono.Cecil.MethodReference]) { continue }
                Assert-True (-not ($operand.DeclaringType.FullName -eq 'System.Diagnostics.Process' -and $operand.Name -eq 'Start')) 'Embedded Discord must not launch a hidden external bot.'
                Assert-True (-not $operand.DeclaringType.FullName.StartsWith('System.IO.Pipes.')) 'Retired IPC remains in the embedded runtime.'
                Assert-True (-not ($operand.DeclaringType.FullName -in @('System.Net.Sockets.TcpListener', 'System.Net.HttpListener') -and
                    $operand.Name -in @('.ctor', 'Start'))) 'Embedded RCON must not open an external TCP/HTTP listener.'
            }
        }
    }
}
finally { $assembly.Dispose() }
Write-Host 'Discord single-DLL JSON/YAML, complete embedded license notices, Release archive contents/version, server-only credentials, private-chat boundary, startup/shutdown routing, no process/IPC, and world-bound command lifecycle smoke passed.'
