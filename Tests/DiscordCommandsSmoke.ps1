param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Wait-Until([scriptblock]$Condition, [string]$Message) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($watch.ElapsedMilliseconds -gt 5000) { throw $Message }
        Start-Sleep -Milliseconds 10
    }
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.dll', 'assembly_utils.dll',
    'SoftReferenceableAssets.dll', 'com.rlabrecque.steamworks.net.dll', 'Splatform.dll', 'assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (Split-Path -Parent $pluginPath) $name))) | Out-Null
}
$assembly = [Reflection.Assembly]::LoadFrom($pluginPath)
$settingsType = $assembly.GetType('ServerManager.Discord.DiscordSettings', $true)
$commandsType = $assembly.GetType('ServerManager.Discord.DiscordCommands', $true)
$commonType = $assembly.GetType('ServerManager.Commands.ServerCommands', $true)
$httpType = $assembly.GetType('ServerManager.Discord.DiscordHttp', $true)
$jsonType = $assembly.GetType('Newtonsoft.Json.Linq.JObject', $true)
$parseJson = $jsonType.GetMethod('Parse', [type[]]@([string]))
$instanceFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$staticFlags = [Reflection.BindingFlags]'Static,Public,NonPublic'
foreach ($limit in @{ RconMinimumIntervalSeconds = 1; MaximumRconOutputCharacters = 1800; CommandTimeoutSeconds = 15 }.GetEnumerator()) {
    Assert-True ($null -eq $settingsType.GetProperty($limit.Key, $instanceFlags)) "Execution limit must not remain configurable: $($limit.Key)"
    $field = $commandsType.GetField($limit.Key, $staticFlags)
    Assert-True ($field.IsLiteral -and $field.GetRawConstantValue() -eq $limit.Value) "Execution limit must be a fixed constant: $($limit.Key)"
}
$catalogNames = @($commandsType.GetField('Names', $staticFlags).GetValue($null))
$flatNames = @($commonType.GetProperty('FlatCommandNames', $staticFlags).GetValue($null, $null))
Assert-True ($flatNames.Count -eq 33 -and $catalogNames.Count -eq 34 -and
    $flatNames -notcontains 'importstatus' -and $catalogNames -notcontains 'importstatus') 'Shared flat feature and Discord catalog sizes drifted or importstatus remains supported.'
Assert-True (@($catalogNames | Where-Object { $_.Contains('-') }).Count -eq 0) 'Public Discord and shared feature names must not retain hyphenated aliases.'
Assert-True ((@($flatNames | Sort-Object) -join '|') -ceq (@($catalogNames | Where-Object { $_ -ne 'rcon' } | Sort-Object) -join '|')) 'Discord and F5 must expose exactly the same flat feature names, with RCON exclusive to Discord.'
Assert-True ($flatNames -contains 'banlist' -and $flatNames -notcontains 'rcon') 'The shared catalog must include banlist but must not create sm:rcon.'

$compileOptions = @{}
if ($PSVersionTable.PSVersion.Major -lt 6) {
    Add-Type -AssemblyName System.Net.Http
    $compileOptions.ReferencedAssemblies = @('System.dll', 'System.Core.dll', [System.Net.Http.HttpClient].Assembly.Location)
}
Add-Type @compileOptions -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
public sealed class DiscordCommandSmokeHandler : HttpMessageHandler
{
    public readonly ConcurrentQueue<string> Paths = new ConcurrentQueue<string>();
    public readonly ConcurrentQueue<string> Bodies = new ConcurrentQueue<string>();
    public static readonly ConcurrentQueue<object> Audits = new ConcurrentQueue<object>();
    public bool FailAcknowledgement;
    public bool StaleEdit;
    public bool FailRegistration;
    public string FailedGuild = "";
    public int Reads;
    public int Callbacks;
    public int UnexpectedAuthorization;
    public static void Log(string value) { }
    public static void Audit(object value) { Audits.Enqueue(value); }
    public static void InvokeOnWorker(System.Reflection.MethodInfo method, object[] arguments)
    { Task.Run(() => method.Invoke(null, arguments)).GetAwaiter().GetResult(); }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string path = request.RequestUri.AbsolutePath;
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
        Paths.Enqueue(request.Method.Method + " " + path);
        Bodies.Enqueue(body);
        if (FailedGuild.Length != 0 && path.Contains("/guilds/" + FailedGuild + "/commands"))
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
        if (path.Contains("/callback")) {
            Interlocked.Increment(ref Callbacks);
            if (request.Headers.Authorization != null) Interlocked.Increment(ref UnexpectedAuthorization);
            return new HttpResponseMessage(FailAcknowledgement ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent) {
                Content = new StringContent(FailAcknowledgement ? "{\"code\":40060}" : "") };
        }
        if (request.Method == HttpMethod.Get) {
            int read = Interlocked.Increment(ref Reads);
            if (StaleEdit && read == 1)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "[{\"id\":\"123\",\"type\":1,\"name\":\"status\",\"description\":\"outdated\"}]") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "[{\"id\":\"888\",\"type\":1,\"name\":\"unrelated\",\"description\":\"do not delete\"}]") };
        }
        if (StaleEdit && path.EndsWith("/123"))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"code\":10063}") };
        if (FailRegistration)
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout) { Content = new StringContent("{}") };
        string name = Regex.Match(body, "\\\"name\\\":\\\"([^\\\"]+)\\\"").Groups[1].Value;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
            "{\"id\":\"" + (path.Contains("/guilds/112/") ? "334" : "333") + "\",\"name\":\"" + name + "\",\"type\":1}") };
    }
}
'@
$log = [Delegate]::CreateDelegate([Action[string]], [DiscordCommandSmokeHandler].GetMethod('Log'))
$auditType = [Action``1].MakeGenericType($assembly.GetType('ServerManager.Events.ServerManagerEvent', $true))
$audit = [Delegate]::CreateDelegate($auditType, [DiscordCommandSmokeHandler].GetMethod('Audit'))

function New-Commands([string[]]$Guilds = @('111')) {
    $settings = [Activator]::CreateInstance($settingsType, $true)
    $settings.BotEnabled = $true
    $settings.BotToken = 'test-token-never-used-on-network'
    foreach ($guild in $Guilds) { $settings.GuildIds.Add($guild) | Out-Null }
    $settings.CommandChannelIds.Add('222') | Out-Null
    $settings.AdminUserIds.Add('444') | Out-Null
    $handler = [DiscordCommandSmokeHandler]::new()
    $http = $httpType.GetConstructors($instanceFlags)[0].Invoke([object[]]@($settings.BotToken, $log, $handler))
    $commands = $commandsType.GetConstructors($instanceFlags)[0].Invoke([object[]]@(
        $settings, $http, $log, $audit, [Threading.CancellationToken]::None))
    return [pscustomobject]@{ Settings = $settings; Handler = $handler; Http = $http; Commands = $commands }
}
function Field($value, [string]$name) { return ,($commandsType.GetField($name, $instanceFlags).GetValue($value)) }
function Set-Field($value, [string]$name, $data) { $commandsType.GetField($name, $instanceFlags).SetValue($value, $data) }
function Dispatch($commands, [string]$event, [string]$json) {
    $data = $parseJson.Invoke($null, [object[]]@($json))
    $task = $commandsType.GetMethod('HandleDispatchAsync').Invoke($commands, [object[]]@($event, $data))
    Assert-True $task.IsCompleted 'Gateway dispatch must return without waiting for HTTP or the main thread.'
}
function Interaction([string]$id, [string]$user = '444', [string]$guild = '111', [string]$commandId = '333') {
    return '{"id":"' + $id + '","application_id":"777","type":2,"token":"interaction-test-token",' +
        '"guild_id":"' + $guild + '","channel_id":"222","member":{"user":{"id":"' + $user + '"},"roles":[]},' +
        '"data":{"id":"' + $commandId + '","type":1,"name":"status","options":[]}}'
}
function Allow($fixture, [string]$name, [string]$guild = '111', [string]$channel = '222',
    [string]$user = '444') {
    return $commandsType.GetMethod('IsAuthorized', $instanceFlags).Invoke($fixture.Commands,
        [object[]]@($name, $guild, $channel, $user))
}

$fixture = New-Commands
try {
    foreach ($name in $catalogNames) {
        Assert-True (Allow $fixture $name) "Explicit admin user must authorize registered command $name."
        Assert-True (-not (Allow $fixture $name '111' '222' '999')) "Unlisted user must not authorize $name."
    }
    foreach ($name in @('save', 'kick', 'ban', 'unban', 'sm', 'playerinfo', 'skilladd', 'skillreset', 'unknown')) {
        Assert-True (-not (Allow $fixture $name)) "Removed or unknown slash command must not authorize: $name"
    }
    Assert-True (-not (Allow $fixture 'status' '112')) 'Wrong guild must fail closed.'
    Assert-True (-not (Allow $fixture 'status' '111' '223')) 'Wrong channel must fail closed.'
    Assert-True (Allow $fixture 'rcon') 'Listed admin receives RCON without a removed feature switch or secondary grant.'
    $fixture.Settings.AdminUserIds.Clear()
    Assert-True (-not (Allow $fixture 'rcon')) 'An empty admin list must revoke RCON.'
    $fixture.Settings.AdminUserIds.Add('444') | Out-Null
    $fixture.Settings.CommandChannelIds.Clear()
    Assert-True (-not (Allow $fixture 'status')) 'Empty channel allowlist must deny every channel.'
    $fixture.Settings.CommandChannelIds.Add('222') | Out-Null

    $validate = $commandsType.GetMethod('ValidateArguments', $staticFlags)
    $arguments = [Collections.Generic.Dictionary[string,string]]::new()
    Assert-True ($validate.Invoke($null, [object[]]@('status', $arguments))) 'Argument-free status should validate.'
    Assert-True ($validate.Invoke($null, [object[]]@('players', $arguments))) 'Players without a selector must remain a list query.'
    $playerArguments = [Collections.Generic.Dictionary[string,string]]::new()
    $playerArguments.Add('player', 'Some Player')
    Assert-True ($validate.Invoke($null, [object[]]@('players', $playerArguments))) 'Players must accept one optional player selector.'
    $sharedLine = $commandsType.GetMethod('SharedLine', $staticFlags).Invoke($null, [object[]]@('players', $playerArguments))
    Assert-True ($sharedLine -ceq 'players "Some Player"') 'Players selector must be safely quoted for the shared parser.'
    foreach ($removed in @('playerinfo', 'skilladd', 'skillreset')) {
        Assert-True (-not $validate.Invoke($null, [object[]]@($removed, $playerArguments))) "Removed slash command must have no parser alias: $removed"
    }
    $arguments.Add('command', "save`nshutdown")
    Assert-True (-not $validate.Invoke($null, [object[]]@('rcon', $arguments))) 'RCON must reject embedded control characters.'
    $arguments['command'] = 'save'
    Assert-True ($validate.Invoke($null, [object[]]@('rcon', $arguments))) 'Single-line RCON should validate.'
    Assert-True (-not $validate.Invoke($null, [object[]]@('sm', $arguments))) 'Removed generic SM slash input must not validate.'
    foreach ($removedPrefix in @('sm status', 'sm:status', 'SM:STATUS', '"sm:status"', "'sm:status'", 'sm:rcon save')) {
        $arguments['command'] = $removedPrefix
        Assert-True (-not $validate.Invoke($null, [object[]]@('rcon', $arguments))) "Raw RCON must not become a back door into the F5 namespace: $removedPrefix"
    }
    $arguments['command'] = 'x' * 1001
    Assert-True (-not $validate.Invoke($null, [object[]]@('rcon', $arguments))) 'Raw console input must remain bounded to 1000 characters.'
    $arguments['command'] = 'save'
    $arguments.Add('unknown', 'value')
    Assert-True (-not $validate.Invoke($null, [object[]]@('rcon', $arguments))) 'Unexpected options must be rejected.'
    $teleportArguments = [Collections.Generic.Dictionary[string,string]]::new()
    $teleportArguments.Add('player', 'Source Viking')
    $teleportArguments.Add('to', 'Target Viking')
    $sharedLine = $commandsType.GetMethod('SharedLine', $staticFlags)
    Assert-True ($sharedLine.Invoke($null, [object[]]@('teleport', $teleportArguments)) -ceq 'teleport "Source Viking" to "Target Viking"') 'Discord teleport must use the new to token without reviving the --to alias.'
    Assert-True ($sharedLine.Invoke($null, [object[]]@('banlist', [Collections.Generic.Dictionary[string,string]]::new())) -ceq 'banlist') 'Discord banlist must use its flat public command name.'

    $saveType = $assembly.GetType('ServerManager.Events.ServerManagerSaveOperationSnapshot', $true)
    $stateType = $assembly.GetType('ServerManager.Events.ServerManagerSaveState', $true)
    $scopeType = $assembly.GetType('ServerManager.Events.ServerManagerCharacterCommitScope', $true)
    $save = $saveType.GetConstructors($instanceFlags)[0].Invoke([object[]]@(
        'save-operation-123', [Enum]::Parse($stateType, 'CheckpointCompleted'), [DateTime]::UtcNow,
        [Nullable[DateTime]][DateTime]::UtcNow, '', [Enum]::Parse($scopeType, 'PartialRetainedShadowsAtCutoff'),
        10, 8, 2))
    $statusType = $assembly.GetType('ServerManager.Events.ServerManagerStatusSnapshot', $true)
    $players = [Array]::CreateInstance($assembly.GetType('ServerManager.Events.ServerManagerPlayerSnapshot', $true), 0)
    $status = $statusType.GetConstructors($instanceFlags)[0].Invoke([object[]]@(
        $true, $true, $true, $true, $true, $false, 'Test server', 'Test world', $players, $save))
    $summary = $commonType.GetMethod('FormatStatus', $staticFlags).Invoke($null, [object[]]@($status))
    foreach ($expected in @('save_operation_id: save-operation-123', 'save_state: CheckpointCompleted',
        'save_character_commit_scope: PartialRetainedShadowsAtCutoff', 'save_captured_character_count: 10',
        'save_persisted_character_count: 8', 'save_pending_character_count: 2')) {
        Assert-True ($summary.Contains($expected)) "Status must retain verified save checkpoint detail: $expected"
    }
    Assert-True ($summary.Length -lt 2000) 'Normal status reply must fit Discord limits.'

    # Audit events must be useful in the existing line-oriented log AND retain no secret command data.
    $auditJson = $parseJson.Invoke($null, [object[]]@(
        '{"id":"9090","application_id":"777","type":2,"token":"interaction-test-token",' +
        '"guild_id":"111","channel_id":"222","member":{"user":{"id":"444"},"roles":[]},' +
        '"data":{"id":"333","type":1,"name":"rcon","options":[{"name":"command","type":3,"value":"say RAW_ARGUMENT_SECRET"}]}}'))
    $auditArguments = [object[]]::new(1)
    $auditArguments[0] = $auditJson
    $auditPending = $commandsType.GetMethod('Parse', $instanceFlags).Invoke($fixture.Commands, $auditArguments)
    $auditPending.GetType().GetField('Arguments', $instanceFlags).GetValue($auditPending).Add('output', 'RAW_OUTPUT_SECRET')
    $auditMethod = $commandsType.GetMethod('Audit', $instanceFlags)
    $auditMethod.Invoke($fixture.Commands, [object[]]@($auditPending, $true, 'rcon_dispatched')) | Out-Null
    $auditMethod.Invoke($fixture.Commands, [object[]]@($auditPending, $false, 'rcon_failed')) | Out-Null
    $auditEvents = @([DiscordCommandSmokeHandler]::Audits.ToArray() | Where-Object { $_.Fields['request_id'] -eq '9090' })
    Assert-True ($auditEvents.Count -eq 2) 'Successful and failed command audits must both be emitted.'
    foreach ($event in $auditEvents) {
        $auditText = $event.Fields['message']
        $outcome = if ($event.Fields['success'] -eq 'true') { 'succeeded' } else { 'failed' }
        Assert-True ($auditText.Length -gt 0 -and $auditText.Length -le 256) 'Line-oriented audit message must be nonempty and bounded.'
        foreach ($expected in @('/rcon', $outcome, $event.Fields['result_code'], '444')) {
            Assert-True ($auditText.Contains($expected)) "Audit message must include command/outcome/result/actor: $expected"
        }
        Assert-True ($event.Actor.Id -eq '444' -and $event.Actor.Name.Contains('444')) 'Log-rendered actor must identify the Discord user.'
        Assert-True ($event.Fields['actor_discord_user_id'] -eq '444') 'Webhook-visible fields must retain the Discord user ID.'
        $rendered = ($event.Fields.Values -join ' ') + ' ' + $event.Actor.Name
        foreach ($secret in @('RAW_ARGUMENT_SECRET', 'RAW_OUTPUT_SECRET', 'interaction-test-token', $fixture.Settings.BotToken)) {
            Assert-True (-not $rendered.Contains($secret)) 'Audit must never retain command arguments, output, or credentials.'
        }
    }
    $resultType = $assembly.GetType('ServerManager.Events.ServerManagerCommandResult', $true)
    foreach ($code in @('save_requested', 'save_scheduled')) {
        $result = $resultType.GetConstructors($instanceFlags)[0].Invoke([object[]]@(
            $true, $code, 'Completion has not yet been claimed.', 'op-123',
            [Collections.Generic.Dictionary[string,string]]::new()))
        $formatted = $commandsType.GetMethod('FormatIntegrationResult', $staticFlags).Invoke($null, [object[]]@($result))
        $text = $formatted.GetType().GetProperty('Message', $instanceFlags).GetValue($formatted)
        Assert-True ($text.StartsWith(([string][char]0xC694)) -and $text.Contains('/status')) 'Save acceptance must not be reported as disk/checkpoint completion.'
    }

    Dispatch $fixture.Commands 'READY' '{"application":{"id":"777"}}'
    Wait-Until { (Field $fixture.Commands '_commandIds').Count -eq $catalogNames.Count } 'Slash registration did not finish.'
    Assert-True (-not ($fixture.Handler.Paths.ToArray() -match 'DELETE|PUT')) 'Registration must not bulk replace/delete unrelated commands.'
    Assert-True (($fixture.Handler.Bodies.ToArray() -match '"name":"rcon"').Count -eq 1) 'RCON must be registered with the fixed command catalog.'
    Assert-True (-not ($fixture.Handler.Bodies.ToArray() -match '"name":"(save|kick|ban|unban|sm)"')) 'Removed duplicate and generic slash commands must not be registered.'
    Set-Field $fixture.Commands '_ready' $true
    Dispatch $fixture.Commands 'INTERACTION_CREATE' (Interaction '1001')
    Wait-Until { (Field $fixture.Commands '_queue').Count -eq 1 } 'Successful defer must queue the command.'
    Assert-True ($fixture.Handler.Callbacks -eq 1) 'Exactly one acknowledgement expected.'
    Assert-True ($fixture.Handler.UnexpectedAuthorization -eq 0) 'Interaction callback must not carry the bot credential.'
    Assert-True (($fixture.Handler.Bodies.ToArray() -match '"type":5').Count -eq 1) 'Accepted command must defer before game execution.'
    Dispatch $fixture.Commands 'INTERACTION_CREATE' (Interaction '1001')
    Assert-True ($fixture.Handler.Callbacks -eq 1) 'Duplicate interaction must not be acknowledged or replayed.'
    $fixture.Commands.Dispose()
    Assert-True ((Field $fixture.Commands '_queue').Count -eq 0) 'Disposal must clear pending commands.'
} finally { $fixture.Commands.Dispose(); $fixture.Http.Dispose() }

$chatScope = New-Commands
try {
    $chatScope.Settings.ChatChannelIds.Add('333') | Out-Null
    $chatAuthorization = $commandsType.GetMethod('TryAuthorizeChat', $instanceFlags)
    Assert-True ($null -ne $chatAuthorization) 'Ingress and Tick must share one chat authorization policy.'
    foreach ($case in @(
        @('111', '222', '444', $true, $true), @('111', '222', '999', $false, $false),
        @('111', '333', '444', $true, $false), @('111', '333', '999', $true, $false),
        @('999', '333', '444', $false, $false), @('111', '334', '444', $false, $false))) {
        $arguments = [object[]]@($case[0], $case[1], $case[2], $false)
        $allowed = $chatAuthorization.Invoke($chatScope.Commands, $arguments)
        Assert-True ($allowed -eq $case[3]) 'Chat must require the selected guild and appropriate admin/public channel membership.'
        if ($allowed) { Assert-True ($arguments[3] -eq $case[4]) 'Only the channel designation, not merely admin identity, selects the fixed Admin display mode.' }
    }
    $chatScope.Settings.ChatChannelIds.Add('222') | Out-Null
    $overlap = [object[]]@('111', '222', '999', $false)
    Assert-True (-not $chatAuthorization.Invoke($chatScope.Commands, $overlap)) 'An overlapping public list must not bypass an admin-channel restriction.'
    $chatScope.Settings.AdminUserIds.Clear()
    $revoked = [object[]]@('111', '222', '444', $false)
    Assert-True (-not $chatAuthorization.Invoke($chatScope.Commands, $revoked)) 'Revoked or empty global admin membership must immediately deny admin-channel chat.'
    $public = [object[]]@('111', '333', '444', $false)
    Assert-True ($chatAuthorization.Invoke($chatScope.Commands, $public) -and -not $public[3]) 'Public-only channels remain ordinary chat after admin revocation.'
} finally { $chatScope.Commands.Dispose(); $chatScope.Http.Dispose() }

$multi = New-Commands -Guilds @('111', '112')
try {
    Assert-True ($null -eq $settingsType.GetProperty('GuildId', $instanceFlags)) 'The removed singular GuildId setting must not survive as an alias.'
    foreach ($name in $catalogNames) {
        Assert-True ((Allow $multi $name '111') -and (Allow $multi $name '112') -and -not (Allow $multi $name '999')) "Configured guild membership must gate the shared administrator for $name."
    }
    Dispatch $multi.Commands 'READY' '{"application":{"id":"777"}}'
    Wait-Until { (Field $multi.Commands '_registering') -eq 0 -and (Field $multi.Commands '_commandIds').Count -eq 2 * $catalogNames.Count } 'Both guild command catalogs did not finish registration.'
    $multiIds = Field $multi.Commands '_commandIds'
    Assert-True ($multiIds['111:status'] -eq '333' -and $multiIds['112:status'] -eq '334') 'Different guild command IDs must occupy different verified registration keys.'
    Assert-True (($multi.Handler.Paths.ToArray() -match '^GET .*\/guilds\/(111|112)\/commands$').Count -eq 2) 'Registration must read each selected guild exactly once.'
    Set-Field $multi.Commands '_ready' $true
    Dispatch $multi.Commands 'INTERACTION_CREATE' (Interaction '2101' '444' '112' '333')
    Wait-Until { (Field $multi.Commands '_inFlight') -eq 0 } 'Cross-guild command ID rejection did not complete.'
    Assert-True ((Field $multi.Commands '_queue').Count -eq 0) 'A first-guild command ID must not authorize a second-guild interaction.'
    Dispatch $multi.Commands 'INTERACTION_CREATE' (Interaction '2102' '444' '112' '334')
    Wait-Until { (Field $multi.Commands '_queue').Count -eq 1 } 'The second guild own verified command ID did not queue.'
    $pendingGuild = (Field $multi.Commands '_queue').Peek()
    Assert-True ($pendingGuild.GetType().GetField('GuildId', $instanceFlags).GetValue($pendingGuild) -eq '112') 'The authenticated source guild must remain attached to deferred work.'
    $callbacks = $multi.Handler.Callbacks
    Dispatch $multi.Commands 'INTERACTION_CREATE' (Interaction '2102' '444' '111' '333')
    Assert-True ($multi.Handler.Callbacks -eq $callbacks -and (Field $multi.Commands '_queue').Count -eq 1) 'Changing guild must not replay a seen interaction ID.'
    $multi.Settings.GuildIds.Remove('112') | Out-Null
    Assert-True (-not (Allow $multi 'status' '112') -and (Allow $multi 'status' '111')) 'Removing one guild must revoke only that guild authority.'
    Dispatch $multi.Commands 'INTERACTION_CREATE' (Interaction '2103' '444' '112' '334')
    Wait-Until { (Field $multi.Commands '_inFlight') -eq 1 } 'Removed guild denial did not complete independently of already queued work.'
    Assert-True ((Field $multi.Commands '_queue').Count -eq 1) 'A removed guild must not enqueue additional work despite a retained old registration ID.'
} finally { $multi.Commands.Dispose(); $multi.Http.Dispose() }

foreach ($failedGuild in @('111', '112')) {
    $isolated = New-Commands -Guilds @('111', '112')
    try {
        $isolated.Handler.FailedGuild = $failedGuild
        Dispatch $isolated.Commands 'READY' '{"application":{"id":"777"}}'
        Wait-Until { (Field $isolated.Commands '_registering') -eq 0 -and $isolated.Handler.Paths.Count -gt 0 } 'Guild-isolated registration failure did not retire its worker.'
        $healthyGuild = if ($failedGuild -eq '111') { '112' } else { '111' }
        $isolatedIds = Field $isolated.Commands '_commandIds'
        Assert-True ($isolatedIds.Count -eq $catalogNames.Count -and @($isolatedIds.Keys | Where-Object { -not $_.StartsWith($healthyGuild + ':') }).Count -eq 0) 'A guild registration failure must not suppress another guild or authorize unverified IDs.'
        Assert-True (($isolated.Handler.Paths.ToArray() -match '^GET .*\/guilds\/(111|112)\/commands$').Count -eq 2) 'Both guilds must be attempted even when one registration fails.'
    } finally { $isolated.Commands.Dispose(); $isolated.Http.Dispose() }
}

# Reload transfers only bounded recent history, never authority, registration, or executable work.
$retiring = New-Commands
$replacement = New-Commands
try {
    Set-Field $retiring.Commands '_applicationId' '777'
    Set-Field $retiring.Commands '_ready' $true
    Set-Field $retiring.Commands '_bound' $true
    (Field $retiring.Commands '_commandIds').Add('111:status', '333')
    $retiring.Settings.AdminUserIds.Add('999') | Out-Null
    Dispatch $retiring.Commands 'INTERACTION_CREATE' (Interaction '1101')
    Wait-Until { (Field $retiring.Commands '_queue').Count -eq 1 } 'Reload fixture did not defer and queue its old command.'
    $retiredPending = (Field $retiring.Commands '_queue').Peek()
    $historyAt = [Diagnostics.Stopwatch]::GetTimestamp()
    (Field $retiring.Commands '_userRate')['444'] = $historyAt
    (Field $retiring.Commands '_globalRate').Clear()
    (Field $retiring.Commands '_responseRate').Clear()
    for ($index = 0; $index -lt 8; $index++) { (Field $retiring.Commands '_globalRate').Enqueue($historyAt) }
    for ($index = 0; $index -lt 16; $index++) { (Field $retiring.Commands '_responseRate').Enqueue($historyAt) }
    Set-Field $retiring.Commands '_hasRconCompleted' $true
    Set-Field $retiring.Commands '_lastRconCompleted' $historyAt

    $replacement.Settings.AdminUserIds.Clear()
    $replacement.Settings.AdminUserIds.Add('666') | Out-Null
    # Freeze old admission before its final history snapshot, as the runtime swap does.
    $retiring.Commands.Dispose()
    Wait-Until { (Field $retiring.Commands '_inFlight') -eq 0 } 'Retired interaction did not release its admission slot.'
    $commandsType.GetMethod('InheritRecentState', $instanceFlags).Invoke($replacement.Commands,
        [object[]]@($retiring.Commands)) | Out-Null

    foreach ($name in @('_seen', '_userRate', '_globalRate', '_responseRate')) {
        $oldHistory = Field $retiring.Commands $name
        $newHistory = Field $replacement.Commands $name
        Assert-True (-not [object]::ReferenceEquals($oldHistory, $newHistory)) "Reload must copy rather than share mutable history: $name"
        Assert-True ($oldHistory.Count -eq $newHistory.Count) "Reload must preserve recent history entries: $name"
    }
    Assert-True ((Field $replacement.Commands '_seen').ContainsKey('1101')) 'Reload must retain the deferred interaction ID.'
    Assert-True ((Field $replacement.Commands '_userRate')['444'] -eq $historyAt) 'Reload must retain per-user rate timestamps.'
    Assert-True ((Field $replacement.Commands '_globalRate').Peek() -eq $historyAt) 'Reload must retain global rate timestamps.'
    Assert-True ((Field $replacement.Commands '_responseRate').Peek() -eq $historyAt) 'Reload must retain acknowledgement rate timestamps.'
    Assert-True ((Field $replacement.Commands '_hasRconCompleted') -and
        (Field $replacement.Commands '_lastRconCompleted') -eq $historyAt) 'Reload must retain the completed RCON cooldown.'
    $commandsType.GetMethod('InheritRecentState', $instanceFlags).Invoke($replacement.Commands,
        [object[]]@($retiring.Commands)) | Out-Null
    Assert-True ((Field $replacement.Commands '_globalRate').Count -eq 8 -and
        (Field $replacement.Commands '_responseRate').Count -eq 16) 'A final post-retirement recopy must not double rate history.'
    Assert-True ([object]::ReferenceEquals((Field $replacement.Commands '_settings'), $replacement.Settings)) 'Reload must keep the replacement settings snapshot.'
    Assert-True (-not (Allow $replacement 'status')) 'Retired global admin authority must not carry into the replacement.'
    Assert-True (Allow $replacement 'status' '111' '222' '666') 'The replacement must apply its own admin authority.'
    Assert-True (-not (Allow $replacement 'rcon' '111' '222' '999')) 'Retired administrator access must not carry over to RCON.'
    foreach ($name in @('_queue', '_commandIds')) {
        Assert-True ((Field $replacement.Commands $name).Count -eq 0) "Reload must not inherit old work or registration: $name"
    }
    foreach ($name in @('_ready', '_bound', '_retired')) {
        Assert-True (-not (Field $replacement.Commands $name)) "The replacement must establish its own lifecycle state: $name"
    }
    Assert-True ((Field $replacement.Commands '_applicationId') -eq '') 'READY application identity must not carry over.'
    Assert-True ((Field $replacement.Commands '_inFlight') -eq 0 -and
        (Field $replacement.Commands '_registering') -eq 0) 'Worker counters must not carry into the replacement.'
    Assert-True ((Field $retiring.Commands '_queue').Count -eq 0) 'Retirement must empty the old execution queue.'
    Assert-True (-not $retiredPending.GetType().GetField('Started', $instanceFlags).GetValue($retiredPending)) 'Retiring queued work must not execute it.'
    $retiredCompletion = $retiredPending.GetType().GetField('Completion', $instanceFlags).GetValue($retiredPending)
    $retiredResult = $retiredCompletion.Task.GetAwaiter().GetResult()
    Assert-True ($retiredResult.GetType().GetProperty('Code', $instanceFlags).GetValue($retiredResult) -eq 'shutdown') 'Retired queued work must complete with shutdown, not success.'
    Dispatch $retiring.Commands 'INTERACTION_CREATE' (Interaction '1102')
    Assert-True ((Field $retiring.Commands '_seen').Count -eq 1) 'Disposed admission must not accept new interaction history.'

    $acceptRate = $commandsType.GetMethod('AcceptRate', $instanceFlags)
    Assert-True (-not $acceptRate.Invoke($replacement.Commands, [object[]]@('666', $historyAt))) 'Copied global rate history must still reject a burst.'
    (Field $replacement.Commands '_globalRate').Clear()
    Assert-True (-not $acceptRate.Invoke($replacement.Commands, [object[]]@('444', $historyAt))) 'Copied individual rate history must still reject a burst.'
    Set-Field $replacement.Commands '_applicationId' '777'
    Dispatch $replacement.Commands 'INTERACTION_CREATE' (Interaction '1103' '666')
    Assert-True (-not (Field $replacement.Commands '_seen').ContainsKey('1103')) 'Copied response rate history must block excess acknowledgement work.'
    (Field $replacement.Commands '_responseRate').Clear()
    Dispatch $replacement.Commands 'INTERACTION_CREATE' (Interaction '1101' '666')
    Assert-True ($replacement.Handler.Callbacks -eq 0 -and (Field $replacement.Commands '_inFlight') -eq 0) 'A deferred ID must not be acknowledged or replayed after replacement.'

    # A capture shell proves the cooldown check happens before any Unity/console access.
    $cooldownCaptureType = $assembly.GetType('ServerManager.Discord.DiscordRconCapture', $true)
    $cooldownCapture = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($cooldownCaptureType)
    $executorType = $assembly.GetType('ServerManager.Commands.ServerConsoleExecutor', $true)
    $executorShell = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($executorType)
    $executorType.GetField('<IsAvailable>k__BackingField', $instanceFlags).SetValue($executorShell, $true)
    $ownerCount = $executorType.GetField('_owners', $staticFlags)
    $ownerCount.SetValue($null, [int]$ownerCount.GetValue($null) + 1)
    $cooldownCaptureType.GetField('_executor', $instanceFlags).SetValue($cooldownCapture, $executorShell)
    (Field $replacement.Commands '_rcon').Dispose()
    Set-Field $replacement.Commands '_rcon' $cooldownCapture
    # Recopy a fresh completion timestamp so slow CI setup cannot outlive the
    # fixed one-second interval while proving that reload preserves it.
    Set-Field $retiring.Commands '_lastRconCompleted' ([Diagnostics.Stopwatch]::GetTimestamp())
    $commandsType.GetMethod('InheritRecentState', $instanceFlags).Invoke($replacement.Commands,
        [object[]]@($retiring.Commands)) | Out-Null
    $cooldownResult = $commandsType.GetMethod('ExecuteRcon', $instanceFlags).Invoke($replacement.Commands, [object[]]@('help'))
    Assert-True ($cooldownResult.GetType().GetProperty('Code', $instanceFlags).GetValue($cooldownResult) -eq 'rcon_cooldown') 'Copied RCON cooldown must reject before touching the console.'
    $tryBeginRcon = $commandsType.GetMethod('TryBeginRcon', $instanceFlags)
    Set-Field $replacement.Commands '_lastRconCompleted' ([Diagnostics.Stopwatch]::GetTimestamp() - 2L * [Diagnostics.Stopwatch]::Frequency)
    Assert-True ($tryBeginRcon.Invoke($replacement.Commands, [object[]]@())) 'An elapsed fixed one-second RCON cooldown must permit a new execution.'
    Assert-True (-not $tryBeginRcon.Invoke($replacement.Commands, [object[]]@())) 'An in-flight RCON execution must exclude concurrent execution even after cooldown elapsed.'
    $commandsType.GetMethod('CompleteRcon', $instanceFlags).Invoke($replacement.Commands, [object[]]@()) | Out-Null
    Assert-True (-not $tryBeginRcon.Invoke($replacement.Commands, [object[]]@())) 'Completion must start a new fixed one-second RCON cooldown.'
} finally {
    $retiring.Commands.Dispose(); $retiring.Http.Dispose()
    $replacement.Commands.Dispose(); $replacement.Http.Dispose()
}

# Scoped integration calls remain backward-compatible; Discord now uses the common queue first.
# Cancelled execution returns before ZNet access, so these checks need no Unity runtime.
$eventRuntimeType = $assembly.GetType('ServerManager.Events.ServerEventRuntime', $true)
$eventCommandType = $assembly.GetType('ServerManager.Events.ServerEventCommandKind', $true)
$integrationType = $assembly.GetType('ServerManager.Events.ServerManagerIntegrationApi', $true)
$enqueue = $eventRuntimeType.GetMethod('EnqueueCommand', $staticFlags)
$processCommands = $eventRuntimeType.GetMethod('ProcessCommands', $staticFlags)
$admission = $eventRuntimeType.GetMethod('SetCommandAdmission', $staticFlags)
$integrationQueue = $eventRuntimeType.GetField('Commands', $staticFlags).GetValue($null)
$initializedField = $eventRuntimeType.GetField('_initialized', $staticFlags)
$admissionField = $eventRuntimeType.GetField('_acceptCommands', $staticFlags)
$previousInitialized = $initializedField.GetValue($null)
$previousAdmission = $admissionField.GetValue($null)
$commonQueue = $commonType.GetField('Queue', $staticFlags).GetValue($null)
$commonInitializedField = $commonType.GetField('_initialized', $staticFlags)
$previousCommonInitialized = $commonInitializedField.GetValue($null)
$scoped = New-Commands
$requestStop = [Threading.CancellationTokenSource]::new()
try {
    Assert-True ($integrationQueue.Count -eq 0) 'The isolated smoke must start with an empty integration queue.'
    $initializedField.SetValue($null, $true)
    $admissionField.SetValue($null, $true)
    $saveKind = [Enum]::Parse($eventCommandType, 'Save')
    $queuedTask = $enqueue.Invoke($null, [object[]]@($saveKind, '', '', $requestStop.Token, $null))
    Assert-True (-not $queuedTask.IsCompleted -and $integrationQueue.Count -eq 1) 'An active scoped request must wait in the integration queue.'
    $queuedRequest = $integrationQueue.Peek()
    $queuedCancellation = $queuedRequest.GetType().GetProperty('Cancellation', $instanceFlags).GetValue($queuedRequest)
    Assert-True ($queuedCancellation.CanBeCanceled -and $queuedCancellation.Equals($requestStop.Token)) 'The shared queue must retain the requesting session token.'
    $requestStop.Cancel()
    $processCommands.Invoke($null, [object[]]@()) | Out-Null
    $cancelledResult = $queuedTask.GetAwaiter().GetResult()
    Assert-True ($cancelledResult.Code -eq 'cancelled' -and -not $cancelledResult.Success) 'Cancellation after admission must finish without Unity access or mutation.'
    $preCancelled = $enqueue.Invoke($null, [object[]]@($saveKind, '', '', $requestStop.Token, $null))
    Assert-True ($preCancelled.IsCompleted -and $preCancelled.Result.Code -eq 'cancelled' -and
        $integrationQueue.Count -eq 0) 'A cancelled lifetime must be rejected before queue admission.'

    $externalTask = $integrationType.GetMethod('RequestWorldSaveAsync', $staticFlags).Invoke($null, [object[]]@())
    $externalRequest = $integrationQueue.Peek()
    $externalCancellation = $externalRequest.GetType().GetProperty('Cancellation', $instanceFlags).GetValue($externalRequest)
    Assert-True (-not $externalCancellation.CanBeCanceled -and -not $externalTask.IsCompleted) 'The unchanged public integration API must use CancellationToken.None.'
    $admission.Invoke($null, [object[]]@($false)) | Out-Null
    Assert-True ($externalTask.Result.Code -eq 'world_changed') 'External commands must retain their existing world-lifetime retirement semantics.'
    $admission.Invoke($null, [object[]]@($true)) | Out-Null

    Assert-True ($commonQueue.Count -eq 0) 'The isolated smoke must start with an empty common command queue.'
    $commonInitializedField.SetValue($null, $true)
    Set-Field $scoped.Commands '_applicationId' '777'
    Set-Field $scoped.Commands '_ready' $true
    Set-Field $scoped.Commands '_bound' $true
    $typedRequests = @()
    foreach ($command in @('status', 'players', 'announce', 'chat', 'characterinfo', 'adminadd', 'keyadd')) {
        (Field $scoped.Commands '_commandIds').Add(('111:' + $command), '333')
        $options = switch ($command) {
            'announce' { '[{"name":"message","type":3,"value":"smoke only"}]' }
            'chat' { '[{"name":"message","type":3,"value":"ordinary chat smoke"}]' }
            'adminadd' { '[{"name":"steam-id","type":3,"value":"76561198000000001"}]' }
            'keyadd' { '[{"name":"key","type":3,"value":"defeated_eikthyr"}]' }
            { $_ -in @('players', 'characterinfo') } { '[{"name":"player","type":3,"value":"smoke target"}]' }
            default { '[]' }
        }
        $commandJson = (Interaction ([string](1200 + $typedRequests.Count))).Replace('"name":"status"', '"name":"' + $command + '"').Replace('"options":[]', '"options":' + $options)
        $typedJson = $parseJson.Invoke($null, [object[]]@($commandJson))
        $typedArguments = [object[]]::new(1)
        $typedArguments[0] = $typedJson
        $typedPending = $commandsType.GetMethod('Parse', $instanceFlags).Invoke($scoped.Commands, $typedArguments)
        $typedTask = $commandsType.GetMethod('ExecuteAsync', $instanceFlags).Invoke($scoped.Commands, [object[]]@($typedPending))
        $typedRequests += $typedTask
        Assert-True (-not $typedTask.IsCompleted) "Discord /$command must await the common executor's actual result."
        $queuedTyped = @($commonQueue.ToArray())[-1]
        $typedCaller = $queuedTyped.GetType().GetField('Caller', $instanceFlags).GetValue($queuedTyped)
        $typedCancellation = $typedCaller.GetType().GetProperty('Cancellation', $instanceFlags).GetValue($typedCaller)
        Assert-True ($typedCancellation.CanBeCanceled -and $typedCancellation.Equals((Field $scoped.Commands '_token'))) "Discord /$command must pass its lifetime into the common queue."
        Assert-True ($typedCaller.GetType().GetProperty('Source', $instanceFlags).GetValue($typedCaller) -eq 'discord' -and
            $typedCaller.GetType().GetProperty('Id', $instanceFlags).GetValue($typedCaller) -eq '444') "Discord /$command must retain its authenticated source and user ID."
        if ($command -eq 'chat') {
            $chatActorName = $typedCaller.GetType().GetProperty('Name', $instanceFlags).GetValue($typedCaller)
            Assert-True ($chatActorName -ne 'Admin' -and $chatActorName.Contains('444')) 'Fixed Admin chat display must not overwrite the original authenticated actor used by logs and command auditing.'
        }
    }
    Assert-True ($commonQueue.Count -eq 7 -and $integrationQueue.Count -eq 0) 'Typed leaf commands must first reach the common executor instead of a divergent integration path.'
    $scoped.Commands.Dispose()
    $commonType.GetMethod('Tick', $staticFlags).Invoke($null, [object[]]@()) | Out-Null
    foreach ($typedTask in $typedRequests) {
        $typedResult = $typedTask.GetAwaiter().GetResult()
        Assert-True ($typedResult.GetType().GetProperty('Code', $instanceFlags).GetValue($typedResult) -eq 'cancelled') 'Reload retirement must cancel every typed mutation after queue handoff.'
    }
    Assert-True ($commonQueue.Count -eq 0 -and $integrationQueue.Count -eq 0) 'Cancelled common work must release all queue entries without touching game APIs.'
} finally {
    $requestStop.Dispose()
    $scoped.Commands.Dispose(); $scoped.Http.Dispose()
    $admission.Invoke($null, [object[]]@($false)) | Out-Null
    $initializedField.SetValue($null, $previousInitialized)
    $admissionField.SetValue($null, $previousAdmission)
    $commonInitializedField.SetValue($null, $previousCommonInitialized)
}

$failure = New-Commands
try {
    Set-Field $failure.Commands '_applicationId' '777'
    Set-Field $failure.Commands '_ready' $true
    (Field $failure.Commands '_commandIds').Add('111:status', '333')
    $failure.Handler.FailAcknowledgement = $true
    Dispatch $failure.Commands 'INTERACTION_CREATE' (Interaction '1002')
    Wait-Until { (Field $failure.Commands '_inFlight') -eq 0 } 'Failed ACK did not finish.'
    Assert-True ($failure.Handler.Callbacks -eq 1) 'Mutating interaction ACK must not retry after failure.'
    Assert-True ((Field $failure.Commands '_queue').Count -eq 0) 'Failed/ambiguous defer must never queue game mutation.'
    Dispatch $failure.Commands 'INTERACTION_CREATE' (Interaction '1002')
    Assert-True ($failure.Handler.Callbacks -eq 1) 'Failed interaction must not replay on Gateway duplicate.'
    Set-Field $failure.Commands '_inFlight' 64
    Dispatch $failure.Commands 'INTERACTION_CREATE' (Interaction '1003')
    Assert-True ($failure.Handler.Callbacks -eq 1) 'Admission capacity must bound outstanding HTTP work.'
    Set-Field $failure.Commands '_inFlight' 0
} finally { $failure.Commands.Dispose(); $failure.Http.Dispose() }

$expired = New-Commands
try {
    Set-Field $expired.Commands '_applicationId' '777'
    Set-Field $expired.Commands '_ready' $true
    (Field $expired.Commands '_commandIds').Add('111:status', '333')
    $expiryJson = $parseJson.Invoke($null, [object[]]@((Interaction '1004')))
    $expiryArguments = [object[]]::new(1)
    $expiryArguments[0] = $expiryJson
    $pending = $commandsType.GetMethod('Parse', $instanceFlags).Invoke($expired.Commands, $expiryArguments)
    $pendingType = $pending.GetType()
    $deadline = $pendingType.GetField('Deadline', $instanceFlags)
    $received = $pendingType.GetField('ReceivedAt', $instanceFlags).GetValue($pending)
    Assert-True (($deadline.GetValue($pending) - $received) -eq 15L * [Diagnostics.Stopwatch]::Frequency) 'Parsed commands must receive the fixed 15-second lifetime.'
    # Age only this admitted test request before ProcessInteraction creates its
    # delay. Changing a deadline after Task.Delay begins would not age the timer.
    $deadline.SetValue($pending, [long]([Diagnostics.Stopwatch]::GetTimestamp() + [Diagnostics.Stopwatch]::Frequency))
    Set-Field $expired.Commands '_inFlight' 1
    (Field $expired.Commands '_seen').Add('1004', [Diagnostics.Stopwatch]::GetTimestamp())
    $expiryTask = $commandsType.GetMethod('ProcessInteractionAsync', $instanceFlags).Invoke($expired.Commands, [object[]]@($pending))
    Wait-Until { (Field $expired.Commands '_queue').Count -eq 1 } 'Deferred command did not queue for expiry test.'
    Wait-Until { (Field $expired.Commands '_inFlight') -eq 0 } 'Expired command did not release its admission slot.'
    $expiryTask.GetAwaiter().GetResult() | Out-Null
    Assert-True ($pending.GetType().GetField('Expired', $instanceFlags).GetValue($pending)) 'Expired pending request must be marked non-executable.'
    Assert-True (-not $pending.GetType().GetField('Started', $instanceFlags).GetValue($pending)) 'Waiting for Unity must not execute commands on a worker.'
    Dispatch $expired.Commands 'INTERACTION_CREATE' (Interaction '1004')
    Assert-True ($expired.Handler.Callbacks -eq 1) 'An expired interaction must not be acknowledged or executed again on duplicate delivery.'
} finally { $expired.Commands.Dispose(); $expired.Http.Dispose() }

$registration = New-Commands
try {
    $registration.Handler.StaleEdit = $true
    Dispatch $registration.Commands 'READY' '{"application":{"id":"777"}}'
    Wait-Until { (Field $registration.Commands '_commandIds').Count -eq $catalogNames.Count } 'Confirmed stale command ID must recover via one fresh read.'
    Assert-True ($registration.Handler.Reads -eq 2) 'Stale edit recovery must re-read authoritative command state.'
    Assert-True (($registration.Handler.Paths.ToArray() -match '^PATCH .*\/123$').Count -eq 1) 'Stale command ID must not be retried blindly.'
    Assert-True (($registration.Handler.Paths.ToArray() -match '^POST ').Count -eq $catalogNames.Count) 'Each confirmed missing catalog command may be created only once.'
} finally { $registration.Commands.Dispose(); $registration.Http.Dispose() }

$ambiguous = New-Commands
try {
    $ambiguous.Handler.FailRegistration = $true
    Dispatch $ambiguous.Commands 'READY' '{"application":{"id":"777"}}'
    Wait-Until { (Field $ambiguous.Commands '_registering') -eq 0 } 'Failed registration did not finish.'
    Assert-True (($ambiguous.Handler.Paths.ToArray() -match '^POST ').Count -eq 1) 'Ambiguous registration mutation must not be repeated.'
    Assert-True ((Field $ambiguous.Commands '_commandIds').Count -eq 0) 'Failed registration must not authorize an unverified command ID.'
} finally { $ambiguous.Commands.Dispose(); $ambiguous.Http.Dispose() }

# Exercise the capture filter without starting Unity or invoking a game command.
$rconType = $assembly.GetType('ServerManager.Commands.ServerConsoleExecutor', $true)
$captureType = $rconType.GetNestedType('Capture', [Reflection.BindingFlags]'NonPublic')
$capture = $captureType.GetConstructors($instanceFlags)[0].Invoke([object[]]@($null, 1800))
$active = $rconType.GetField('_active', $staticFlags)
$captureOutput = $rconType.GetMethod('CaptureOutput', $staticFlags)
$active.SetValue($null, $capture)
try {
    [DiscordCommandSmokeHandler]::InvokeOnWorker($captureOutput, [object[]]@($null, 'unrelated worker output'))
    Assert-True ($captureType.GetField('Text', $instanceFlags).GetValue($capture).Length -eq 0) 'Capture must ignore other-thread output.'
    $captureOutput.Invoke($null, [object[]]@($null, 'Error executing command.')) | Out-Null
    $captureOutput.Invoke($null, [object[]]@($null, ('x' * 3000))) | Out-Null
    Assert-True ($captureType.GetField('Text', $instanceFlags).GetValue($capture).Length -eq 1800) 'Capture must enforce the fixed 1800-character output bound.'
    Assert-True ($captureType.GetField('Truncated', $instanceFlags).GetValue($capture)) 'Capture must flag truncated output.'
    Assert-True ($captureType.GetField('Failed', $instanceFlags).GetValue($capture)) 'Console errors must not be reported as successful commands.'
    # A retiring adapter must not erase a different caller's in-flight capture.
    $owners = $rconType.GetField('_owners', $staticFlags)
    $baselineOwners = [int]$owners.GetValue($null)
    $retiringCapture = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($rconType)
    $rconType.GetField('<IsAvailable>k__BackingField', $instanceFlags).SetValue($retiringCapture, $true)
    $owners.SetValue($null, $baselineOwners + 1)
    $retiringCapture.Dispose()
    Assert-True ([object]::ReferenceEquals($active.GetValue($null), $capture)) 'Disposing an executor must preserve current shared capture.'
    Assert-True ($owners.GetValue($null) -eq $baselineOwners) 'Disposal must release exactly one shared hook lease.'
    $retiringCapture.Dispose()
    Assert-True ($owners.GetValue($null) -eq $baselineOwners) 'Repeated disposal must not release another caller lease.'
} finally { $active.SetValue($null, $null) }
Write-Output 'DiscordCommandsSmoke: PASS (admin-only authorization, always-available RCON catalog, flattened typed commands, validation, verified save status/semantics, bounded secret-free audit, registration preservation, defer-before-queue, reload history without authority/work transfer, scoped cancellation through both queues, no replay/token leak, bounds, expiry/disposal, synchronous RCON capture).'
