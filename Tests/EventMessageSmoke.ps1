param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$yamlPath = Join-Path $env:USERPROFILE '.nuget\packages\yamldotnet\18.1.0\lib\net47\YamlDotNet.dll'
$bepInExPath = Join-Path $GamePath 'BepInEx\core\BepInEx.dll'
$frameworkPath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdkLine = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A modern .NET SDK is required for the C# 10 compiler.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-EventMessage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $harness = Join-Path $testDirectory 'EventMessageSmoke.exe'
    $compileArgs = @(
        $compiler, '/nologo', '/noconfig', '/nostdlib+', '/langversion:10.0', '/nullable:enable',
        '/target:exe', "/out:$harness",
        ("/reference:" + (Join-Path $frameworkPath 'mscorlib.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.dll')),
        ("/reference:" + (Join-Path $frameworkPath 'System.Core.dll')),
        "/reference:$yamlPath",
        "/reference:$bepInExPath",
        (Join-Path $projectRoot 'Events\EventMessageText.cs'),
        (Join-Path $projectRoot 'Events\EventReportProtocol.cs'),
        (Join-Path $projectRoot 'Events\ServerEventOverlay.cs'),
        (Join-Path $projectRoot 'Tests\EventMessageSmoke.cs')
    )
    & dotnet @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "Event message smoke compilation failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath $yamlPath -Destination (Join-Path $testDirectory 'YamlDotNet.dll')
    Copy-Item -LiteralPath $bepInExPath -Destination (Join-Path $testDirectory 'BepInEx.dll')
    & $harness (Join-Path $projectRoot 'translations\English.yml') (Join-Path $projectRoot 'translations\Korean.yml') (Join-Path $testDirectory 'client-notifications.cfg')
    if ($LASTEXITCODE -ne 0) { throw "Event message smoke failed ($LASTEXITCODE)." }
}
finally {
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    if ((Split-Path -Parent $resolvedTestDirectory) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolvedTestDirectory) -notmatch '^ServerManager-EventMessage-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected event-message test directory.'
    }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}

# Verify actual build wiring without executing any Unity or RPC method.
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"))
try {
    $producer = $assembly.MainModule.GetType('ServerManager.Events.ServerEventRuntime')
    $publish = $producer.Methods | Where-Object Name -eq 'Publish'
    $calls = @($publish.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
    $formatCall = @($calls | Where-Object { $_.Operand.DeclaringType.FullName -eq 'ServerManager.Events.EventMessageText' -and $_.Operand.Name -eq 'TryCreate' })
    $auditCall = @($calls | Where-Object { $_.Operand.Name -eq 'TryWrite' })
    $broadcastCall = @($calls | Where-Object { $_.Operand.Name -eq 'BroadcastEventDisplay' })
    if ($formatCall.Count -ne 1 -or $auditCall.Count -eq 0 -or $broadcastCall.Count -ne 1 -or
        $formatCall[0].Offset -le $auditCall[0].Offset -or $formatCall[0].Offset -ge $broadcastCall[0].Offset) {
        throw 'Server broadcast must select the display token after factual audit output, through the shared helper.'
    }
    if (@($calls | Where-Object { $_.Operand.DeclaringType.FullName -eq 'ServerManager.PlayerLocalizer' -or
        ($_.Operand.DeclaringType.FullName -eq 'ServerManager.Events.EventMessageText' -and $_.Operand.Name -in @('TryFormat', 'Render', 'RenderForLanguage')) }).Count -ne 0) {
        throw 'The server publisher must not render a final server-language sentence before broadcasting localized tokens.'
    }
    $runtime = $assembly.MainModule.GetType('ServerManager.ServerManagerRuntime')
    $receive = $runtime.Methods | Where-Object Name -eq 'OnEventDisplayPacket'
    $sequence = @($receive.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'set_LastEventDisplaySequence' })
    $show = @($receive.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'ShowEventDisplay' })
    $authentication = @($receive.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'FixedTimeEquals' })
    if ($sequence.Count -ne 1 -or $show.Count -ne 1 -or $authentication.Count -ne 2 -or
        $sequence[0].Offset -ge $show[0].Offset -or $authentication[-1].Offset -ge $sequence[0].Offset) {
        throw 'Remote display rendering must follow the authenticated session/nonce and consumed sequence.'
    }
    $showMethod = $runtime.Methods | Where-Object Name -eq 'ShowEventDisplay'
    $localRender = @($showMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq 'ServerManager.Events.EventMessageText' -and $_.Operand.Name -eq 'Render' })
    $broadcast = $runtime.Methods | Where-Object Name -eq 'BroadcastEventDisplay'
    if ($localRender.Count -ne 1 -or @($broadcast.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq 'ShowEventDisplay' }).Count -ne 1) { throw 'Remote clients and the listen host must share the same local-language display path.' }
    if (($runtime.Fields | Where-Object Name -eq 'EventDisplayRpcName').Constant -cne 'sighsorry.ServerManager.EventDisplay.v2' -or
        ($assembly.MainModule.GetType('ServerManager.ProtocolPacketCodec').Fields | Where-Object Name -eq 'WireVersion').Constant -ne 21) {
        throw 'Localized event displays require the new display endpoint and handshake wire version, without legacy fallback.'
    }
    $webhooks = $assembly.MainModule.GetType('ServerManager.Discord.DiscordWebhooks')
    $bossPublish = $producer.Methods | Where-Object Name -eq 'PublishBoss'
    $killerRead = $bossPublish.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_FinalAttacker'
    }
    if ($null -eq $killerRead -or $killerRead.Next.Next.OpCode.Code.ToString() -ne 'Ldstr' -or
        $killerRead.Next.Next.Operand -ne 'Unknown') {
        throw 'A missing boss final attacker must not become the reporting player.'
    }
    $cards = $webhooks.Methods | Where-Object Name -eq 'BuildEmbedsLocked'
    if (@($cards.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.DeclaringType.FullName -eq 'ServerManager.Events.EventMessageText' -and $_.Operand.Name -eq 'TryFormat' }).Count -lt 1) {
        throw 'Discord combat cards must use the same formatter as the game display.'
    }
    $notificationReaders = @(
        foreach ($type in $assembly.MainModule.Types) {
            foreach ($method in $type.Methods) {
                if (-not $method.HasBody) { continue }
                foreach ($instruction in $method.Body.Instructions) {
                    if ($instruction.Operand -is [Mono.Cecil.MethodReference] -and
                        $instruction.Operand.DeclaringType.FullName -eq 'ServerManager.ServerManagerPlugin' -and
                        $instruction.Operand.Name -eq 'get_ShowEventNotifications') { $method }
                }
            }
        }
    )
    if ($notificationReaders.Count -lt 1 -or @($notificationReaders | Where-Object {
        $_.DeclaringType.FullName -ne 'ServerManager.Events.ServerEventOverlay'
    }).Count -ne 0) {
        throw 'The client notification toggle must be consumed only by the overlay, never server audit, broadcast, protocol, webhook, or shared formatting paths.'
    }
    $overlayType = $assembly.MainModule.GetType('ServerManager.Events.ServerEventOverlay')
    if (@($overlayType.Methods | Where-Object HasBody | ForEach-Object { $_.Body.Instructions } | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'ServerManager.ServerManagerPlugin' -and
        $_.Operand.Name -eq 'get_EnableClientBranding'
    }).Count -ne 0) { throw 'Notification visibility must not depend on client branding being enabled.' }
} finally { $assembly.Dispose() }
Write-Output 'PASS: factual audit precedes token creation; authenticated clients and host render locally; Discord uses the same formatter; notification toggle affects only the branding-independent client overlay.'
