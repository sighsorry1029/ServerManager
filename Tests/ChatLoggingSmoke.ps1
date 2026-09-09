param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Message)
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert-True $threw $Message
}

function New-ChatReport {
    param([string]$Kind, [string]$Text, [uint32]$Sequence = 1)
    $report = [Activator]::CreateInstance($script:reportType, $true)
    foreach ($pair in @(
        @('SessionId', $script:sessionId),
        @('Nonce', $script:nonce),
        @('Sequence', $Sequence),
        @('Kind', [Enum]::Parse($script:reportKindType, $Kind)),
        @('Text', $Text))) {
        $script:reportType.GetProperty($pair[0], $script:instanceFlags).
            SetValue($report, $pair[1], $null)
    }
    return $report
}

function Decode-ChatBytes {
    param([byte[]]$Bytes)
    $constructorArguments = [object[]]::new(1)
    $constructorArguments[0] = $Bytes
    $arguments = [object[]]::new(3)
    $arguments[0] = $script:packageConstructor.Invoke($constructorArguments)
    $accepted = $script:decodeReport.Invoke($null, $arguments)
    return [pscustomobject]@{
        Accepted = $accepted
        Report = $arguments[1]
        Rejection = $arguments[2]
    }
}

function New-LogEvent {
    param([string]$Kind, [string]$Message)
    $fields = [Collections.Generic.Dictionary[string, string]]::new()
    $fields.Add('message', $Message)
    return $script:eventConstructor.Invoke([object[]]@(
        [Guid]::NewGuid().ToString('N'),
        [DateTime]::UtcNow,
        'chat-test-server',
        $Kind,
        'client_reported',
        $script:actor,
        $null,
        $fields))
}

function Test-Reachable {
    param($Start, $Target)
    if ($null -eq $Start -or $null -eq $Target) { return $false }
    $pending = [Collections.Generic.Queue[object]]::new()
    $visited = @{}
    $pending.Enqueue($Start)
    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        if ($visited.ContainsKey($current.Offset)) { continue }
        if ($current.Offset -eq $Target.Offset) { return $true }
        $visited[$current.Offset] = $true
        $flow = $current.OpCode.FlowControl.ToString()
        if ($flow -eq 'Return' -or $flow -eq 'Throw') { continue }
        if ($flow -eq 'Branch' -or $flow -eq 'Cond_Branch') {
            foreach ($targetInstruction in @($current.Operand)) {
                if ($null -ne $targetInstruction) {
                    $pending.Enqueue($targetInstruction)
                }
            }
        }
        if ($flow -ne 'Branch' -and $null -ne $current.Next) {
            $pending.Enqueue($current.Next)
        }
    }
    return $false
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
$cecilPath = Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll'
foreach ($path in @($pluginPath, $cecilPath,
    (Join-Path $managedRoot 'assembly_valheim.dll'))) {
    Assert-True (Test-Path -LiteralPath $path) "Required assembly missing: $path"
}
foreach ($name in @(
    'UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll',
    'UnityEngine.dll', 'assembly_utils.dll', 'SoftReferenceableAssets.dll',
    'com.rlabrecque.steamworks.net.dll', 'Splatform.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    # Dependency copies can retain download-zone metadata. Load their bytes for
    # this isolated probe without changing the installed game or file metadata.
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes(
        (Join-Path (Split-Path -Parent $pluginPath) $name))) | Out-Null
}
$gameAssembly = [Reflection.Assembly]::LoadFrom(
    (Join-Path $managedRoot 'assembly_valheim.dll'))
$assembly = [Reflection.Assembly]::LoadFrom($pluginPath)
[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory((Split-Path -Parent $pluginPath))
$resolver.AddSearchDirectory((Join-Path $GamePath 'BepInEx\core'))
$resolver.AddSearchDirectory($managedRoot)
$readerParameters = [Mono.Cecil.ReaderParameters]::new()
$readerParameters.AssemblyResolver = $resolver
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath, $readerParameters)
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic'
$reportType = $assembly.GetType('ServerManager.Events.EventClientReport', $true)
$reportKindType = $assembly.GetType(
    'ServerManager.Events.EventClientReportKind', $true)
$codecType = $assembly.GetType(
    'ServerManager.Events.EventClientReportCodec', $true)
$encodeReport = $codecType.GetMethod('Encode', $staticFlags)
$decodeReport = $codecType.GetMethod('TryDecode', $staticFlags)
$packageConstructor = $gameAssembly.GetType('ZPackage', $true).
    GetConstructor([Type[]]@([byte[]]))
$sessionId = [byte[]](1..16)
$nonce = [byte[]](1..32)
$kindOffset = 4 + 2 + $sessionId.Length + $nonce.Length + 4
$payloadLengthOffset = $kindOffset + 1
$payloadOffset = $payloadLengthOffset + 2
$textOffset = $payloadOffset + 2

Assert-True ($codecType.GetField('WireVersion', $staticFlags).
    GetRawConstantValue() -eq 2) 'Expanded chat reports require event wire-v2.'
foreach ($expected in @(
    @('Shout', 1), @('Death', 2), @('BossKilled', 3),
    @('Normal', 4), @('Whisper', 5))) {
    Assert-True ([int][Enum]::Parse($reportKindType, $expected[0]) -eq
        $expected[1]) "The wire value changed for $($expected[0])."
}
Assert-True ([Enum]::GetNames($reportKindType) -notcontains 'Clan') `
    'Client reports must not be able to claim server-accepted clan chat.'

foreach ($kind in @('Shout', 'Normal', 'Whisper')) {
    foreach ($text in @(('bounded ' + $kind), ('x' * 2000),
        (([string][char]0xD55C) * 666 + 'ab'))) {
        $report = New-ChatReport $kind $text 42
        $package = $encodeReport.Invoke($null, [object[]]@($report))
        Assert-True ($package.Size() -le 4096) "$kind exceeded the packet bound."
        $decoded = Decode-ChatBytes $package.GetArray()
        Assert-True ($decoded.Accepted -and $null -eq $decoded.Rejection) `
            "Valid $kind text was rejected."
        foreach ($property in @('Kind', 'Sequence', 'Text')) {
            $member = $reportType.GetProperty($property, $instanceFlags)
            Assert-True ($member.GetValue($decoded.Report, $null) -eq
                $member.GetValue($report, $null)) "$kind $property did not round-trip."
        }
        foreach ($property in @('SessionId', 'Nonce')) {
            $member = $reportType.GetProperty($property, $instanceFlags)
            Assert-True ([Convert]::ToBase64String(
                $member.GetValue($decoded.Report, $null)) -eq
                [Convert]::ToBase64String($member.GetValue($report, $null))) `
                "$kind lost its session-binding $property."
        }
    }
    foreach ($badText in @(('x' * 2001),
        (([string][char]0xD55C) * 667), "line`nbreak", "tab`tbreak", '   ')) {
        Assert-Throws {
            $encodeReport.Invoke($null, [object[]]@(
                (New-ChatReport $kind $badText)))
        } "$kind accepted oversized, whitespace-only, or control-character text."
    }

    $validBytes = $encodeReport.Invoke($null, [object[]]@(
        (New-ChatReport $kind 'bounded text'))).GetArray()
    $mutations = [Collections.Generic.List[object]]::new()
    foreach ($unknown in @(0, 6, 255)) {
        $bytes = [byte[]]$validBytes.Clone()
        $bytes[$kindOffset] = [byte]$unknown
        $mutations.Add(@("unknown/forged clan kind $unknown", $bytes))
    }
    $bytes = [byte[]]$validBytes.Clone()
    $bytes[4] = 1
    $mutations.Add(@('obsolete wire-v1', $bytes))
    $bytes = [byte[]]$validBytes.Clone()
    [Array]::Clear($bytes, $kindOffset - 4, 4)
    $mutations.Add(@('zero sequence', $bytes))
    $bytes = [byte[]]$validBytes.Clone()
    $bytes[$payloadLengthOffset] = 0
    $bytes[$payloadLengthOffset + 1] = 0
    $mutations.Add(@('inconsistent payload length', $bytes))
    $bytes = [byte[]]$validBytes.Clone()
    [Array]::Copy([BitConverter]::GetBytes([uint16]2001), 0,
        $bytes, $payloadOffset, 2)
    $mutations.Add(@('oversized declared text length', $bytes))
    $bytes = [byte[]]$validBytes.Clone()
    $bytes[$textOffset] = 0xC0
    $bytes[$textOffset + 1] = 0xAF
    $mutations.Add(@('malformed UTF-8', $bytes))
    $bytes = [byte[]]$validBytes.Clone()
    $bytes[$textOffset] = 10
    $mutations.Add(@('control character on wire', $bytes))
    $bytes = [byte[]]::new($validBytes.Length + 1)
    [Array]::Copy($validBytes, $bytes, $validBytes.Length)
    $mutations.Add(@('trailing envelope data', $bytes))
    $bytes = [byte[]]$bytes.Clone()
    [Array]::Copy([BitConverter]::GetBytes([uint16](
        $bytes.Length - $payloadOffset)), 0, $bytes, $payloadLengthOffset, 2)
    $mutations.Add(@('trailing payload data', $bytes))
    $mutations.Add(@('truncated text',
        [byte[]]$validBytes[0..($validBytes.Length - 2)]))
    foreach ($mutation in $mutations) {
        $decoded = Decode-ChatBytes $mutation[1]
        Assert-True (-not $decoded.Accepted -and $null -eq $decoded.Report -and
            $null -ne $decoded.Rejection) "$kind accepted $($mutation[0])."
    }
}

$eventType = $assembly.GetType('ServerManager.Events.ServerManagerEvent', $true)
$actorType = $assembly.GetType('ServerManager.Events.ServerManagerActor', $true)
$eventConstructor = $eventType.GetConstructors($instanceFlags)[0]
$actor = $actorType.GetConstructors($instanceFlags)[0].Invoke(
    [object[]]@('test-account', 'chat-player', 'player'))
$runtimeType = $assembly.GetType('ServerManager.Events.ServerEventRuntime', $true)
$runtimeDefinition = $definition.MainModule.Types | Where-Object FullName -eq `
    'ServerManager.Events.ServerEventRuntime' | Select-Object -First 1
$writerType = $assembly.GetType('ServerManager.Events.EventLogWriter', $true)
$writeLog = $writerType.GetMethod('Write', $staticFlags)
$isChatKind = $writerType.GetMethod('IsChatKind', $staticFlags)
$isLogOnly = $runtimeType.GetMethod('IsLogOnlyChatKind', $staticFlags)
$isDisplayKind = $runtimeType.GetMethod('IsDisplayKind', $staticFlags)
$chatKinds = @('chat.shout', 'chat.normal', 'chat.whisper', 'chat.clan')
$eventKindsType = $assembly.GetType('ServerManager.Events.ServerManagerEventKinds', $true)
foreach ($mapping in @(@('ChatShout', 'chat.shout'), @('ChatNormal', 'chat.normal'),
    @('ChatWhisper', 'chat.whisper'), @('ChatClan', 'chat.clan'))) {
    Assert-True ($eventKindsType.GetField($mapping[0]).GetRawConstantValue() -eq
        $mapping[1]) 'The public chat-kind constants do not match the log schema.'
}
foreach ($kind in $chatKinds) {
    Assert-True ($isChatKind.Invoke($null, [object[]]@($kind))) `
        "$kind is not routed to the chat log."
    Assert-True ($isLogOnly.Invoke($null, [object[]]@($kind)) -eq
        ($kind -ne 'chat.shout')) "$kind has the wrong subscriber privacy policy."
    Assert-True (-not $isDisplayKind.Invoke($null, [object[]]@($kind))) `
        "$kind was promoted to a server-wide event display."
}
Assert-True (-not $isChatKind.Invoke($null, [object[]]@('player.death'))) `
    'Audit events were redirected into the chat log.'

# Check the central fan-out path as well as the predicate: the log-only branch
# must be taken after enqueueing the log and before either external fan-out.
$publishDefinition = $runtimeDefinition.Methods |
    Where-Object Name -eq 'Publish' | Select-Object -First 1
$publishCalls = @($publishDefinition.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference]
})
$logCall = $publishCalls | Where-Object {
    $_.Operand.Name -eq 'TryWrite' -and
    $_.Operand.DeclaringType.FullName -eq 'ServerManager.Events.EventLogWriter'
} | Select-Object -First 1
$privacyCall = $publishCalls | Where-Object {
    $_.Operand.Name -eq 'IsLogOnlyChatKind'
} | Select-Object -First 1
$integrationCall = $publishCalls | Where-Object {
    $_.Operand.Name -eq 'Publish' -and $_.Operand.DeclaringType.FullName -eq
        'ServerManager.Events.ServerManagerIntegrationApi'
} | Select-Object -First 1
$broadcastCall = $publishCalls | Where-Object {
    $_.Operand.Name -eq 'BroadcastEventDisplay'
} | Select-Object -First 1
Assert-True ($null -ne $logCall -and $null -ne $privacyCall -and
    $null -ne $integrationCall -and $null -ne $broadcastCall -and
    $logCall.Offset -lt $privacyCall.Offset -and
    $privacyCall.Offset -lt $integrationCall.Offset) `
    'The central publisher no longer logs before enforcing chat privacy.'
$privacyBranch = $publishDefinition.Body.Instructions | Where-Object {
    $_.Offset -gt $privacyCall.Offset -and
    $_.Offset -lt $integrationCall.Offset -and
    $_.OpCode.FlowControl.ToString() -eq 'Cond_Branch'
} | Select-Object -First 1
Assert-True ($null -ne $privacyBranch) 'The chat privacy early-return guard is missing.'
$privatePaths = @(@($privacyBranch.Operand, $privacyBranch.Next) | Where-Object {
    -not (Test-Reachable $_ $integrationCall) -and
    -not (Test-Reachable $_ $broadcastCall)
})
Assert-True ($privatePaths.Count -eq 1) `
    'Log-only chat has a reachable subscriber or broadcast path.'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
public sealed class ServerManagerChatEventCollector
{
    public readonly ConcurrentQueue<string> Kinds = new ConcurrentQueue<string>();
    public void OnEvent(object sender, object arguments)
    {
        object value = arguments.GetType().GetProperty("Event").GetValue(arguments, null);
        Kinds.Enqueue((string)value.GetType().GetProperty("Kind").GetValue(value, null));
    }
}
public static class ServerManagerFakeClanApi
{
    public static event Action<string, long, string, string, string, string> ServerChatAccepted;
    public static int SubscriberCount
    {
        get { return ServerChatAccepted == null ? 0 : ServerChatAccepted.GetInvocationList().Length; }
    }
}
'@

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryRoot (
    'ServerManager-chat-smoke-' + [Guid]::NewGuid().ToString('N'))
$liveWriter = $runtimeType.GetField('LogWriter', $staticFlags).GetValue($null)
$apiType = $assembly.GetType('ServerManager.Events.ServerManagerIntegrationApi', $true)
$collector = [ServerManagerChatEventCollector]::new()
$publishedEvent = $apiType.GetEvent('EventPublished')
$handler = [Delegate]::CreateDelegate($publishedEvent.EventHandlerType,
    $collector, $collector.GetType().GetMethod('OnEvent'))
$writerStarted = $false
$dispatcherStarted = $false
$subscribed = $false
try {
    [string]$routingRoot = Join-Path $testRoot 'routing'
    [IO.Directory]::CreateDirectory($routingRoot) | Out-Null
    foreach ($kind in $chatKinds) {
        $writeLog.Invoke($null, [object[]]@(
            (New-LogEvent $kind "message for $kind"), $routingRoot))
    }
    $writeLog.Invoke($null, [object[]]@(
        (New-LogEvent 'player.death' 'audit sentinel'), $routingRoot))
    $chatPath = Join-Path $routingRoot 'events-chat.log'
    $auditPath = Join-Path $routingRoot 'events-audit.log'
    $chatLines = [IO.File]::ReadAllLines($chatPath)
    Assert-True ($chatLines.Length -eq 4) 'Each chat channel must produce one chat-log record.'
    foreach ($kind in $chatKinds) {
        Assert-True (@($chatLines | Where-Object {
            $_.Split([char]9)[1] -eq $kind
        }).Count -eq 1) "$kind was lost or duplicated in the chat log."
    }
    Assert-True ([IO.File]::ReadAllLines($auditPath).Length -eq 1 -and
        [IO.File]::ReadAllText($auditPath).Contains('audit sentinel')) `
        'Chat routing changed the independent audit channel.'
    $writeLog.Invoke($null, [object[]]@(
        (New-LogEvent 'chat.clan' "injected`r`nentry`tfield"), $routingRoot))
    $chatLines = [IO.File]::ReadAllLines($chatPath)
    Assert-True ($chatLines.Length -eq 5 -and
        $chatLines[4].Split([char]9).Length -eq 5) `
        'Chat text can inject extra log records or fields.'

    $maximumFileBytes = [long]$writerType.GetField('MaximumFileBytes', $staticFlags).
        GetRawConstantValue()
    Assert-True ($maximumFileBytes -eq 10MB) 'The existing event-log rotation bound changed.'
    foreach ($rotation in @(1, 2)) {
        $stream = [IO.File]::Open($chatPath, [IO.FileMode]::Open, [IO.FileAccess]::Write)
        try { $stream.SetLength($maximumFileBytes) } finally { $stream.Dispose() }
        $writeLog.Invoke($null, [object[]]@(
            (New-LogEvent 'chat.normal' "rotation $rotation"), $routingRoot))
        Assert-True ([IO.File]::Exists($chatPath + '.1') -and
            [IO.FileInfo]::new($chatPath + '.1').Length -eq $maximumFileBytes -and
            [IO.File]::ReadAllLines($chatPath).Length -eq 1 -and
            [IO.File]::ReadAllText($chatPath).Contains("rotation $rotation") -and
            [IO.File]::ReadAllLines($auditPath).Length -eq 1) `
            'Chat rotation lost the new record or disturbed the audit channel.'
    }
    $reader = [IO.File]::OpenText($chatPath + '.1')
    try { $previousFirstLine = $reader.ReadLine() } finally { $reader.Dispose() }
    Assert-True ($previousFirstLine.Contains('rotation 1') -and
        -not [IO.File]::Exists($chatPath + '.2')) `
        'Event log rotation no longer retains exactly the latest predecessor.'

    # Stop immediately after enqueueing: the worker must keep its captured
    # queue and data root even if it is first scheduled after Stop clears fields.
    $restartWriter = [Activator]::CreateInstance($writerType, $true)
    try {
        for ($epoch = 0; $epoch -lt 12; ++$epoch) {
            [string]$restartRoot = Join-Path $testRoot "restart-$epoch"
            $writerType.GetMethod('Start', $instanceFlags).Invoke(
                $restartWriter, [object[]]@($restartRoot))
            Assert-True ($writerType.GetMethod('TryWrite', $instanceFlags).Invoke(
                $restartWriter, [object[]]@(
                    (New-LogEvent 'chat.normal' "restart $epoch")))) `
                'A restarted log writer rejected its first message.'
            $writerType.GetMethod('Stop', $instanceFlags).Invoke($restartWriter, $null)
            $restartPath = Join-Path $restartRoot 'logs\events-chat.log'
            Assert-True ([IO.File]::Exists($restartPath) -and
                [IO.File]::ReadAllLines($restartPath).Length -eq 1 -and
                [IO.File]::ReadAllText($restartPath).Contains("restart $epoch")) `
                'Immediate event-writer shutdown lost a record or reused the previous world root.'
        }
    }
    finally {
        $writerType.GetMethod('Stop', $instanceFlags).Invoke($restartWriter, $null)
    }

    # Reproduce a worker first entering after Stop retired its queue, both with
    # no replacement and with a new generation already published. Invoke the
    # real worker against completed queues so scheduling cannot hide the race.
    $dispatchQueueField = $apiType.GetField('_dispatchQueue', $staticFlags)
    $dispatchLoop = $apiType.GetMethod('DispatchLoop', $staticFlags)
    $publishEvent = $apiType.GetMethod('Publish', $staticFlags)
    Assert-True ($null -eq $dispatchQueueField.GetValue($null)) `
        'The dispatcher ownership probe requires an inactive dispatcher.'
    foreach ($replacementStarted in @($false, $true)) {
        $dispatchCollector = [ServerManagerChatEventCollector]::new()
        $dispatchHandler = [Delegate]::CreateDelegate($publishedEvent.EventHandlerType,
            $dispatchCollector, $dispatchCollector.GetType().GetMethod('OnEvent'))
        $retiredQueue = [Activator]::CreateInstance($dispatchQueueField.FieldType)
        $replacementQueue = $null
        $publishedEvent.AddEventHandler($null, $dispatchHandler)
        try {
            $dispatchQueueField.SetValue($null, $retiredQueue)
            $publishEvent.Invoke($null, [object[]]@(
                (New-LogEvent 'server.started' 'retired generation')))
            $retiredQueue.CompleteAdding()
            $dispatchQueueField.SetValue($null, $null)
            if ($replacementStarted) {
                $replacementQueue = [Activator]::CreateInstance($dispatchQueueField.FieldType)
                $dispatchQueueField.SetValue($null, $replacementQueue)
                $publishEvent.Invoke($null, [object[]]@(
                    (New-LogEvent 'server.saved' 'replacement generation')))
                $replacementQueue.CompleteAdding()
            }

            # Accept the original zero-argument worker as well, so this probe
            # fails on its lost delivery rather than merely a changed signature.
            $dispatchArguments = [object[]]::new($dispatchLoop.GetParameters().Length)
            if ($dispatchArguments.Length -ne 0) {
                $dispatchArguments[0] = $retiredQueue
            }
            $dispatchLoop.Invoke($null, $dispatchArguments)
            $retiredDelivered = $dispatchCollector.Kinds.ToArray()
            Assert-True ($retiredQueue.IsCompleted -and
                $retiredDelivered.Length -eq 1 -and
                $retiredDelivered[0] -eq 'server.started') `
                "A delayed dispatcher abandoned its retired queue or consumed the replacement (restart=$replacementStarted)."
            if ($replacementStarted) {
                Assert-True ($replacementQueue.Count -eq 1) `
                    'The retired dispatcher consumed the next generation event.'
                $dispatchArguments[0] = $replacementQueue
                $dispatchLoop.Invoke($null, $dispatchArguments)
                $bothDelivered = $dispatchCollector.Kinds.ToArray()
                Assert-True ($replacementQueue.IsCompleted -and
                    $bothDelivered.Length -eq 2 -and
                    $bothDelivered[1] -eq 'server.saved') `
                    'The replacement dispatcher lost or duplicated its event.'
            }
        }
        finally {
            $dispatchQueueField.SetValue($null, $null)
            $publishedEvent.RemoveEventHandler($null, $dispatchHandler)
            $retiredQueue.Dispose()
            if ($null -ne $replacementQueue) { $replacementQueue.Dispose() }
        }
    }

    # Also exercise production Start/Publish/Stop wiring and queue draining.
    $dispatchCollector = [ServerManagerChatEventCollector]::new()
    $dispatchHandler = [Delegate]::CreateDelegate($publishedEvent.EventHandlerType,
        $dispatchCollector, $dispatchCollector.GetType().GetMethod('OnEvent'))
    $publishedEvent.AddEventHandler($null, $dispatchHandler)
    try {
        for ($epoch = 0; $epoch -lt 12; ++$epoch) {
            $apiType.GetMethod('StartDispatcher', $staticFlags).Invoke($null, $null)
            $dispatcherStarted = $true
            $publishEvent.Invoke($null, [object[]]@(
                (New-LogEvent "dispatcher.restart.$epoch" 'immediate stop')))
            $apiType.GetMethod('StopDispatcher', $staticFlags).Invoke($null, $null)
            $dispatcherStarted = $false
            $restartDelivered = $dispatchCollector.Kinds.ToArray()
            Assert-True ($restartDelivered.Length -eq $epoch + 1 -and
                $restartDelivered[$epoch] -eq "dispatcher.restart.$epoch") `
                'Immediate dispatcher shutdown lost, duplicated, or reordered an event.'
        }
    }
    finally {
        if ($dispatcherStarted) {
            $apiType.GetMethod('StopDispatcher', $staticFlags).Invoke($null, $null)
            $dispatcherStarted = $false
        }
        $publishedEvent.RemoveEventHandler($null, $dispatchHandler)
    }

    [string]$liveRoot = Join-Path $testRoot 'live'
    $writerType.GetMethod('Start', $instanceFlags).Invoke(
        $liveWriter, [object[]]@($liveRoot))
    $writerStarted = $true
    $publishedEvent.AddEventHandler($null, $handler)
    $subscribed = $true
    $apiType.GetMethod('StartDispatcher', $staticFlags).Invoke($null, $null)
    $dispatcherStarted = $true
    $publish = $runtimeType.GetMethod('Publish', $staticFlags)
    foreach ($kind in @('chat.normal', 'chat.whisper', 'chat.clan',
        'chat.shout', 'server.started')) {
        $fields = [Collections.Generic.Dictionary[string, string]]::new()
        $fields.Add('message', "central $kind")
        $null = $publish.Invoke($null, [object[]]@(
            $kind, 'client_reported', $actor, $null, $fields, [DateTime]::UtcNow))
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ($collector.Kinds.Count -lt 2 -and [DateTime]::UtcNow -lt $deadline) {
        [Threading.Thread]::Sleep(10)
    }
    $apiType.GetMethod('StopDispatcher', $staticFlags).Invoke($null, $null)
    $dispatcherStarted = $false
    $writerType.GetMethod('Stop', $instanceFlags).Invoke($liveWriter, $null)
    $writerStarted = $false
    $delivered = $collector.Kinds.ToArray()
    Assert-True ($delivered.Length -eq 2 -and
        $delivered -contains 'chat.shout' -and $delivered -contains 'server.started') `
        'Private/local chat leaked to EventPublished, or public events stopped flowing.'
    $liveChatLines = [IO.File]::ReadAllLines((Join-Path $liveRoot 'logs\events-chat.log'))
    Assert-True ($liveChatLines.Length -eq 4) `
        'The central publisher did not retain all four chat channels locally.'
}
finally {
    if ($dispatcherStarted) {
        $apiType.GetMethod('StopDispatcher', $staticFlags).Invoke($null, $null)
    }
    if ($subscribed) { $publishedEvent.RemoveEventHandler($null, $handler) }
    if ($writerStarted) {
        $writerType.GetMethod('Stop', $instanceFlags).Invoke($liveWriter, $null)
    }
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
        Assert-True ($resolvedRoot.StartsWith($temporaryRoot,
            [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedRoot).StartsWith('ServerManager-chat-smoke-')) `
            'Refusing to remove a test directory outside the temporary root.'
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}

# Exercise the actual rolling limiter with an injected clock. This catches
# fixed-window edge bursts as well as an accidental lifetime cap.
$rateType = $runtimeType.GetNestedType('RateWindow',
    [Reflection.BindingFlags]::NonPublic)
$consume = $rateType.GetMethod('TryConsume', $instanceFlags)
$clearRate = $rateType.GetMethod('Clear', $instanceFlags)
$rate = [Activator]::CreateInstance($rateType, $true)
$rateStart = [DateTime]::SpecifyKind([DateTime]'2026-09-03', [DateTimeKind]::Utc)
for ($index = 0; $index -lt 600; ++$index) {
    Assert-True ($consume.Invoke($rate, [object[]]@(
        32, [TimeSpan]::FromSeconds(10), $rateStart.AddSeconds(2 * $index)))) `
        "Ordinary spaced chat was capped after $index reports."
}
$clearRate.Invoke($rate, $null)
for ($index = 0; $index -lt 32; ++$index) {
    Assert-True ($consume.Invoke($rate, [object[]]@(
        32, [TimeSpan]::FromSeconds(10), $rateStart.AddMilliseconds(300 * $index)))) `
        'The chat rolling limiter rejected an allowed report.'
}
Assert-True (-not $consume.Invoke($rate, [object[]]@(
    32, [TimeSpan]::FromSeconds(10), $rateStart.AddMilliseconds(9999)))) `
    'A chat flood exceeded the rolling bound.'
Assert-True ($consume.Invoke($rate, [object[]]@(
    32, [TimeSpan]::FromSeconds(10), $rateStart.AddSeconds(10)))) `
    'The oldest rolling-window slot did not become available.'
Assert-True (-not $consume.Invoke($rate, [object[]]@(
    32, [TimeSpan]::FromSeconds(10), $rateStart.AddSeconds(10)))) `
    'Crossing a window boundary incorrectly replenished every slot.'
Assert-True ($rateType.GetField('_acceptedUtc', $instanceFlags).
    GetValue($rate).Count -eq 32) 'The rolling limiter queue was not bounded.'

# The installed Character metadata has default interface members that Windows
# PowerShell's CLR cannot load. Keep this native-type execution probe on Core;
# both hosts still execute the codec, logs, privacy, limiter and IL assertions.
if ($PSVersionTable.PSEdition -eq 'Core') {
$stateType = $runtimeType.GetNestedType('EventPeerState',
    [Reflection.BindingFlags]::NonPublic)
$state = [Activator]::CreateInstance($stateType, $true)
$rpc = [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
    $gameAssembly.GetType('ZRpc', $true))
foreach ($pair in @(@('Ready', $true), @('Rpc', $rpc),
    @('PlayerName', 'chat-player'), @('Actor', $actor))) {
    $stateType.GetField($pair[0], $instanceFlags).SetValue($state, $pair[1])
}
$peers = $runtimeType.GetField('Peers', $staticFlags).GetValue($null)
$initializedField = $runtimeType.GetField('_initialized', $staticFlags)
$originalInitialized = $initializedField.GetValue($null)
$tryProcess = $runtimeType.GetMethod('TryProcessClientReport', $staticFlags)
$serverRate = $runtimeType.GetField('ServerReports', $staticFlags).GetValue($null)
$reportRate = $stateType.GetProperty('Reports', $instanceFlags).GetValue($state, $null)
$chatRate = $stateType.GetProperty('Chats', $instanceFlags).GetValue($state, $null)
try {
    $peers.Add($rpc, $state)
    $initializedField.SetValue($null, $true)
    for ($index = 1; $index -le 600; ++$index) {
        # Empty windows model an interval of at least ten seconds. Do not reset
        # peer identity or sequence: a session-wide total would fail at 257.
        $clearRate.Invoke($reportRate, $null)
        $clearRate.Invoke($chatRate, $null)
        $clearRate.Invoke($serverRate, $null)
        $arguments = [object[]]@($rpc,
            (New-ChatReport 'Normal' 'spaced chat' ([uint32]$index)), $null)
        Assert-True ($tryProcess.Invoke($null, $arguments)) `
            "A long-lived authenticated chat session stopped at report $index."
    }
    $clearRate.Invoke($reportRate, $null)
    $clearRate.Invoke($chatRate, $null)
    $clearRate.Invoke($serverRate, $null)
    for ($index = 601; $index -le 856; ++$index) {
        $arguments = [object[]]@($rpc,
            (New-ChatReport 'Whisper' 'bounded burst' ([uint32]$index)), $null)
        Assert-True ($tryProcess.Invoke($null, $arguments)) `
            'The authenticated report limiter rejected its allowed burst.'
    }
    $arguments = [object[]]@($rpc,
        (New-ChatReport 'Whisper' 'excess burst' 857), $null)
    Assert-True (-not $tryProcess.Invoke($null, $arguments)) `
        'The authenticated peer exceeded 256 reports in one rolling window.'
    Assert-True ($rateType.GetField('_acceptedUtc', $instanceFlags).
        GetValue($chatRate).Count -eq 32) `
        'The shared chat limiter did not bound normal/whisper output.'
    $clearRate.Invoke($reportRate, $null)
    Assert-True ($tryProcess.Invoke($null, $arguments)) `
        'The peer could not resume after its rate window expired.'
    Assert-True (-not $tryProcess.Invoke($null, $arguments)) `
        'A replayed chat sequence was accepted.'
}
finally {
    $peers.Remove($rpc) | Out-Null
    $initializedField.SetValue($null, $originalInitialized)
    $clearRate.Invoke($serverRate, $null)
}
}

$peerDefinition = $runtimeDefinition.NestedTypes | Where-Object Name -eq 'EventPeerState'
$processClient = $runtimeDefinition.Methods |
    Where-Object Name -eq 'TryProcessClientReport' | Select-Object -First 1
Assert-True ($peerDefinition.Fields.Name -notcontains 'ReportCount' -and
    $runtimeDefinition.Fields.Name -notcontains 'MaximumReportsPerSession' -and
    @($processClient.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq 'get_Reports'
    }).Count -eq 1) 'The remote chat pipeline regained a lifetime report quota.'

$listenHost = $runtimeDefinition.Methods | Where-Object Name -eq `
    'ProcessListenHostReport' | Select-Object -First 1
$listenFields = @($listenHost.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.FieldReference]
} | ForEach-Object { $_.Operand.Name })
Assert-True ($listenFields -contains '_listenHostState' -and
    $listenFields -contains 'ServerReports' -and
    @($listenHost.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq 'TryConsume'
    }).Count -ge 2) 'The listen host does not retain and enforce its report limiters.'

$patch = $definition.MainModule.Types | Where-Object FullName -eq `
    'ServerManager.ServerEventChatPatch' | Select-Object -First 1
$postfix = $patch.Methods | Where-Object Name -eq 'Postfix' | Select-Object -First 1
$patchCalls = @($postfix.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference]
})
$localIdCall = $patchCalls | Where-Object {
    $_.Operand.Name -eq 'get_LocalPlayerCharacterID' -and
    $_.Operand.DeclaringType.FullName -eq 'ZNet'
} | Select-Object -First 1
$userIdCall = $patchCalls | Where-Object {
    $_.Operand.Name -eq 'get_UserID' -and $_.Operand.DeclaringType.FullName -eq 'ZDOID'
} | Select-Object -First 1
$reportCall = $patchCalls | Where-Object {
    $_.Operand.Name -eq 'ReportLocalChat'
} | Select-Object -First 1
Assert-True ($null -ne $localIdCall -and $null -ne $userIdCall -and
    $null -ne $reportCall -and $localIdCall.Offset -lt $userIdCall.Offset -and
    $userIdCall.Offset -lt $reportCall.Offset -and
    $postfix.Parameters[0].Name -eq 'senderID' -and
    @($patch.CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' -and
        @($_.ConstructorArguments | Where-Object {
            $_.Value -eq 'OnNewChatMessage'
        }).Count -eq 1
    }).Count -eq 1) 'Chat observation no longer validates the local echo sender.'
$senderComparisons = @($postfix.Body.Instructions | Where-Object {
    $_.Offset -gt $userIdCall.Offset -and $_.Offset -lt $reportCall.Offset -and
    ($_.OpCode.Code.ToString() -eq 'Ceq' -or
     $_.OpCode.Code.ToString() -like 'Bne_Un*' -or
     $_.OpCode.Code.ToString() -like 'Beq*')
})
Assert-True ($senderComparisons.Count -ge 1 -and
    @($postfix.Body.Instructions | Where-Object {
        $_.Offset -gt $userIdCall.Offset -and $_.Offset -lt $reportCall.Offset -and
        $_.OpCode.FlowControl.ToString() -eq 'Cond_Branch' -and
        ((Test-Reachable $_.Operand $reportCall) -ne
         (Test-Reachable $_.Next $reportCall))
    }).Count -ge 1) 'Remote chat echoes can reach the local-report path.'

$mainRuntime = $definition.MainModule.Types | Where-Object FullName -eq `
    'ServerManager.ServerManagerRuntime' | Select-Object -First 1
$reportLocalChat = $mainRuntime.Methods | Where-Object Name -eq `
    'ReportLocalChat' | Select-Object -First 1
$kindSwitch = $reportLocalChat.Body.Instructions | Where-Object {
    $_.OpCode.Code.ToString() -eq 'Switch'
} | Select-Object -First 1
$sendLocal = $reportLocalChat.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and
    $_.Operand.Name -eq 'SendLocalEventReport'
} | Select-Object -First 1
Assert-True ($null -ne $kindSwitch -and $kindSwitch.Operand.Count -eq 3 -and
    $null -ne $sendLocal -and -not (Test-Reachable $kindSwitch.Next $sendLocal)) `
    'Unknown chat types (including clan) can enter the client report channel.'
foreach ($mapping in @(@(0, 'Ldc_I4_5'), @(1, 'Ldc_I4_4'), @(2, 'Ldc_I4_1'))) {
    Assert-True ($kindSwitch.Operand[$mapping[0]].OpCode.Code.ToString() -eq
        $mapping[1]) 'Whisper/normal/shout were mapped to the wrong wire kinds.'
}

$clanDefinition = $definition.MainModule.Types | Where-Object FullName -eq `
    'ServerManager.Events.ClanChatIntegration' | Select-Object -First 1
$clanType = $assembly.GetType('ServerManager.Events.ClanChatIntegration', $true)
$clanHandler = $clanType.GetField('ChatAccepted', $staticFlags).GetValue($null)
$clanEventField = $clanType.GetField('_chatEvent', $staticFlags)
$fakeEvent = [ServerManagerFakeClanApi].GetEvent('ServerChatAccepted')
Assert-True ($clanHandler.GetType() -eq $fakeEvent.EventHandlerType -and
    $definition.MainModule.AssemblyReferences.Name -notcontains 'Clan') `
    'The optional Clan bridge gained a hard dependency or changed its callback schema.'
$pluginDefinition = $definition.MainModule.Types |
    Where-Object FullName -eq 'ServerManager.ServerManagerPlugin' | Select-Object -First 1
$clanDependency = @($pluginDefinition.CustomAttributes | Where-Object {
    $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' -and
    $_.ConstructorArguments.Count -eq 2 -and
    $_.ConstructorArguments[0].Value -eq 'sighsorry.Clan'
})
Assert-True ($clanDependency.Count -eq 1 -and
    [int]$clanDependency[0].ConstructorArguments[1].Value -eq 2) `
    'Clan must remain an explicitly optional BepInEx SoftDependency.'
$clanCallback = $clanDefinition.Methods | Where-Object Name -eq `
    'OnServerChatAccepted' | Select-Object -First 1
Assert-True (@($clanCallback.Body.Instructions | Where-Object {
    $_.Operand -is [Mono.Cecil.MethodReference] -and
    $_.Operand.Name -eq 'RecordClanChat'
}).Count -eq 1) 'Accepted clan messages are not forwarded to the private log sink.'
try {
    $fakeEvent.AddEventHandler($null, $clanHandler)
    $clanEventField.SetValue($null, $fakeEvent)
    $clanType.GetMethod('Start', $staticFlags).Invoke($null, $null)
    Assert-True ([ServerManagerFakeClanApi]::SubscriberCount -eq 1) `
        'Starting an attached Clan bridge duplicated its event subscription.'
    $clanType.GetMethod('Stop', $staticFlags).Invoke($null, $null)
    $clanType.GetMethod('Stop', $staticFlags).Invoke($null, $null)
    Assert-True ([ServerManagerFakeClanApi]::SubscriberCount -eq 0 -and
        $null -eq $clanEventField.GetValue($null)) `
        'Stopping the Clan bridge left a live subscription behind.'
    $fakeEvent.AddEventHandler($null, $clanHandler)
    $clanEventField.SetValue($null, $fakeEvent)
    $clanType.GetMethod('Start', $staticFlags).Invoke($null, $null)
    Assert-True ([ServerManagerFakeClanApi]::SubscriberCount -eq 1) `
        'Reattaching a new-world Clan bridge duplicated or lost its subscription.'
    $clanType.GetMethod('Stop', $staticFlags).Invoke($null, $null)

    $worldStart = $runtimeDefinition.Methods | Where-Object Name -eq `
        'OnServerStarted' | Select-Object -First 1
    $worldShutdown = $runtimeDefinition.Methods | Where-Object Name -eq `
        'OnServerShutdown' | Select-Object -First 1
    foreach ($fieldName in @('_worldReady', '_shutdownStarted')) {
        Assert-True (@($worldStart.Body.Instructions | Where-Object {
            $_.OpCode.Code.ToString() -eq 'Stsfld' -and
            $_.Operand.Name -eq $fieldName -and
            $_.Previous.OpCode.Code.ToString() -in @('Ldnull', 'Ldc_I4_0')
        }).Count -ge 1) "A new world retains stale $fieldName state."
    }
    $resetObservations = $runtimeDefinition.Methods | Where-Object Name -eq `
        'ResetObservationState' | Select-Object -First 1
    Assert-True (@($worldStart.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        $_.Operand.Name -eq 'ResetObservationState'
    }).Count -eq 1 -and @($resetObservations.Body.Instructions | Where-Object {
        $_.OpCode.Code.ToString() -eq 'Stsfld' -and
        $_.Operand.Name -eq '_listenHostState' -and
        $_.Previous.OpCode.Code.ToString() -eq 'Ldnull'
    }).Count -eq 1) 'A new world retains stale listen-host observation state.'
    $shutdownFinally = $worldShutdown.Body.ExceptionHandlers | Where-Object {
        $_.HandlerType.ToString() -eq 'Finally'
    } | Select-Object -First 1
    Assert-True ($null -ne $shutdownFinally) `
        'World shutdown does not guarantee event-resource cleanup.'
    $finallyInstructions = @($worldShutdown.Body.Instructions | Where-Object {
        $_.Offset -ge $shutdownFinally.HandlerStart.Offset -and
        ($null -eq $shutdownFinally.HandlerEnd -or
         $_.Offset -lt $shutdownFinally.HandlerEnd.Offset)
    })
    foreach ($fieldName in @('_serverStarted', '_worldReady')) {
        Assert-True (@($finallyInstructions | Where-Object {
            $_.OpCode.Code.ToString() -eq 'Stsfld' -and
            $_.Operand.Name -eq $fieldName -and
            $_.Previous.OpCode.Code.ToString() -eq 'Ldc_I4_0'
        }).Count -eq 1) "World shutdown leaves $fieldName active for the next world."
    }
    foreach ($owner in @('ServerManager.Events.ClanChatIntegration',
        'ServerManager.Events.EventLogWriter')) {
        Assert-True (@($finallyInstructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq 'Stop' -and $_.Operand.DeclaringType.FullName -eq $owner
        }).Count -ge 1 -and @($worldStart.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.Name -eq 'Start' -and $_.Operand.DeclaringType.FullName -eq $owner
        }).Count -ge 1) "World restart does not retire and reattach $owner."
    }
}
finally {
    $fakeEvent.RemoveEventHandler($null, $clanHandler)
    $clanEventField.SetValue($null, $null)
    $definition.Dispose()
    $resolver.Dispose()
}

if ($PSVersionTable.PSEdition -ne 'Core') {
    Write-Output 'Skipped the native-type 600-report probe on Desktop CLR; run this smoke under PowerShell Core too.'
}
Write-Output ('Chat wire-v2 bounds, channel routing/rotation, private fan-out isolation, ' +
    'dispatcher generation/drain, rolling limits, local-sender filtering, ' +
    'and optional Clan/world-restart smoke passed.')
