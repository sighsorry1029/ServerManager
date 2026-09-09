param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
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
$commands = $assembly.GetType('ServerManager.Commands.ServerCommands', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$tokenize = $commands.GetMethod('TryTokenize', $flags)
$parse = $commands.GetMethod('TryParseCommand', $flags)
$quote = $commands.GetMethod('Quote', $flags)

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Read-Tokens([string]$Line, [bool]$Expected = $true) {
    $arguments = [object[]]@($Line, $null, $null)
    $accepted = $tokenize.Invoke($null, $arguments)
    Assert-True ($accepted -eq $Expected) "Unexpected tokenization result: $Line"
    if ($accepted) { return ,([string[]]$arguments[1]) }
}
function Read-Command([string]$Line, [bool]$Expected = $true) {
    $arguments = [object[]]@($Line, $null, $null)
    $accepted = $parse.Invoke($null, $arguments)
    Assert-True ($accepted -eq $Expected) "Unexpected public syntax result: $Line"
    if ($accepted) { return ,([string[]]$arguments[1]) }
}
function Assert-Syntax([string]$Line, [bool]$Expected) {
    Read-Command $Line $Expected | Out-Null
}

$tokens = Read-Tokens 'KiCk "Mixed Case Name" "a \"quote\" and \\ slash"'
Assert-True ($tokens.Count -eq 3 -and $tokens[1] -ceq 'Mixed Case Name') 'Quoted target must retain case/spaces.'
Assert-True ($tokens[2] -ceq 'a "quote" and \ slash') 'Only supported quote escapes must decode.'
foreach ($text in @('hello', 'space name', 'a "quote"', 'one\two')) {
    $quoted = $quote.Invoke($null, @($text))
    $roundTrip = Read-Tokens $quoted
    Assert-True ($roundTrip.Count -eq 1 -and $roundTrip[0] -ceq $text) 'Quote must round-trip input.'
}
foreach ($line in @('chat "unterminated', 'chat "x"suffix', 'chat x"y', 'chat "bad\escape"', 'chat ""')) {
    Read-Tokens $line $false | Out-Null
}
Read-Tokens ('a' * 2049) $false | Out-Null
Read-Tokens ((1..17 | ForEach-Object { 'x' }) -join ' ') $false | Out-Null
Read-Tokens ("status`nban someone") $false | Out-Null
Assert-True ((Read-Tokens ('a' * 2048)).Count -eq 1) 'Length boundary must be accepted.'
Assert-True ((Read-Tokens ((1..16 | ForEach-Object { 'x' }) -join ' ')).Count -eq 16) 'Argument boundary must be accepted.'

# Each public leaf must map to the existing validated internal operation,
# with the same argument case and quoting in terminal and Discord spellings.
$cases = [ordered]@{
    'status' = @('', 'status')
    'players' = @('', 'players')
    'announce' = @('"Mixed Case sm:help"', 'announce "Mixed Case sm:help"')
    'chat' = @('"TeLl Mixed Case sm:help"', 'chat "TeLl Mixed Case sm:help"')
    'adminlist' = @('', 'admin list')
    'adminadd' = @('76561198000000001', 'admin add 76561198000000001')
    'adminremove' = @('76561198000000001', 'admin remove 76561198000000001')
    'accesslist' = @('', 'access list')
    'accessadd' = @('76561198000000001', 'access add 76561198000000001')
    'accessremove' = @('76561198000000001', 'access remove 76561198000000001')
    'banlist' = @('', 'ban list')
    'keylist' = @('', 'key list')
    'keyadd' = @('defeated_Eikthyr', 'key add defeated_Eikthyr')
    'keyremove' = @('defeated_Eikthyr', 'key remove defeated_Eikthyr')
    'eventstart' = @('army_Eikthyr 0 2 -1', 'event start army_Eikthyr 0 2 -1')
    'eventstop' = @('', 'event stop')
    'characterlist' = @('', 'character list')
    'characterinfo' = @('"Some Name"', 'character info "Some Name"')
    'characterbackups' = @('"Some Name" 2', 'character backups "Some Name" 2')
    'characterrestore' = @('"Some Name" 0123456789abcdef0123456789abcdef', 'character restore "Some Name" 0123456789abcdef0123456789abcdef')
    'giveitem' = @('"Some Name" Wood 10 1 Mixed-Preset_01', 'item give "Some Name" Wood 10 1 Mixed-Preset_01')
    'teleport' = @('"Some Name" TO "Other Player"', 'teleport "Some Name" to "Other Player"')
    'skillget' = @('"Some Name" Run', 'skill get "Some Name" Run')
    'skillset' = @('"Some Name" Run 99', 'skill set "Some Name" Run 99')
    'heal' = @('"Some Name" 12.5', 'heal "Some Name" 12.5')
    'damage' = @('"Some Name" 12.5', 'damage "Some Name" 12.5')
    'modsstatus' = @('', 'mods status')
    'modsreload' = @('', 'mods reload')
    'discordstatus' = @('', 'discord status')
    'discordtest' = @('', 'discord test')
    'cronstatus' = @('', 'cronstatus')
    'cronack' = @('morning-maintenance', 'cronack morning-maintenance')
    'help' = @('', 'help')
}
$flatNames = [string[]]$commands.GetProperty('FlatCommandNames', $flags).GetValue($null)
$syntax = $commands.GetMethod('GetCommandSyntax', $flags)
$playerFirst = $commands.GetMethod('HasPlayerFirstArgument', $flags)
$playerNames = @('players', 'characterinfo', 'characterbackups', 'characterrestore', 'giveitem', 'teleport', 'skillget', 'skillset', 'heal', 'damage')
Assert-True ($flatNames.Length -eq 33 -and ($flatNames -join ',') -ceq ($cases.Keys -join ',')) 'Shared registry must contain exactly the 33 feature leaves in stable order.'
Assert-True (@($flatNames | Where-Object { $_ -notmatch '^[a-z]+$' }).Count -eq 0) 'Every public feature name must be a single lowercase word without hyphens.'
foreach ($line in @('cronack', 'cronack one two', 'cronack "two words"', 'cronack 한글', ('cronack ' + ('a' * 65)), 'cronstatus extra')) {
    Assert-Syntax $line $false
}
Assert-Syntax ('cronack ' + ('a' * 64)) $true
foreach ($name in $cases.Keys) {
    $tail = if ($cases[$name][0].Length) { ' ' + $cases[$name][0] } else { '' }
    $expected = Read-Tokens $cases[$name][1]
    foreach ($prefix in @('', 'sm:', 'SM:')) {
        $actual = Read-Command ($prefix + $name.ToUpperInvariant() + $tail)
        Assert-True (($actual -join "`0") -ceq ($expected -join "`0")) "Flat name must translate without changing payload: $prefix$name"
    }
    Assert-Syntax ('sm ' + $name + $tail) $false
    Assert-Syntax ('sm:sm:' + $name + $tail) $false
    Assert-Syntax ('"sm:' + $name + '"' + $tail) $false
    $usage = $syntax.Invoke($null, @($name.ToUpperInvariant()))
    Assert-True ($usage.StartsWith('sm:' + $name) -and $usage -notmatch '--to|\bsm ') "Help must use the canonical namespace: $name"
    Assert-True ($playerFirst.Invoke($null, @($name)) -eq ($playerNames -contains $name)) "Player completion metadata must match the actual first argument: $name"
}
foreach ($retiredName in @('playerinfo', 'skilladd', 'skillreset', 'player-info', 'admin-list', 'admin-add', 'admin-remove', 'access-list', 'access-add', 'access-remove', 'ban-list', 'key-list', 'key-add', 'key-remove', 'event-start', 'event-stop', 'character-list', 'character-info', 'import-status', 'skill-get', 'skill-set', 'skill-add', 'skill-reset', 'mods-status', 'mods-reload', 'discord-status', 'discord-test')) {
    $name = $retiredName.Replace('-', '')
    $tail = if ($cases.Contains($name) -and $cases[$name][0].Length) { ' ' + $cases[$name][0] } else { '' }
    foreach ($prefix in @('', 'sm:', 'SM:')) {
        Assert-Syntax ($prefix + $retiredName + $tail) $false
    }
    Assert-True ($syntax.Invoke($null, @($retiredName)) -ceq '') 'Retired names must not expose help or aliases.'
    Assert-True (-not $playerFirst.Invoke($null, @($retiredName))) 'Retired names must not expose player completion.'
}
foreach ($name in @('', 'sm', 'sm:help', 'ban', 'unban', 'save', 'kick', 'rcon', 'item', 'unknown')) {
    Assert-True ($syntax.Invoke($null, @($name)) -ceq '') 'Unknown or raw-console commands must not acquire feature syntax.'
    Assert-True (-not $playerFirst.Invoke($null, @($name))) 'Unknown or raw-console commands must not acquire feature player completion.'
}
foreach ($line in @('  sm:HELP  ', 'ban "Some Name" Mixed Case Reason', 'UNBAN 76561198000000001',
    'teleport player to "Other Player"', 'sm:teleport player -1000000 2 1000000',
    'giveitem player Wood 1', 'skillget player', 'skillset player Run 0', 'skillset player all 0',
    'players halla', 'sm:players "Some Name"', 'SM:PLAYERS 76561198000000001', 'players 42',
    'players "76561198000000001/Some Name"',
    'characterbackups player', 'sm:characterbackups "76561198000000001/Some Name" 1', 'characterbackups 1234 1000',
    'chat message with preserved Case', 'CHAT TeLl player Message')) {
    Assert-Syntax $line $true
}
foreach ($line in @('', ' ', 'sm:', 'sm:unknown', 'sm:sm:help', 'sm::help', 'sm :help', '/sm:help', '"help"', "'sm:help'",
    'sm:ban 76561198000000001', 'sm:unban 76561198000000001', 'sm:save', 'sm:kick player', 'sm:rcon save',
    'sm help', 'player info player', 'ban list', 'admin list', 'admin add 76561198000000001',
    'access list', 'key add defeated_eikthyr', 'event start army_eikthyr 0 2 -1', 'event stop',
    'item give player Wood 1', 'character info player', 'import status', 'skill set player Run 1', 'mods', 'mods status', 'discord test',
    'spawn Troll', 'item drop Wood 1', 'scan', 'maintenance', 'shutdown',
    'save', 'save status', 'kick player', 'save now', 'status extra', 'tell player Message', 'TeLl "Player Name" "Message"',
    'chat', 'announce', 'adminadd 123', 'accessremove 123', 'banlist extra', 'banlist 76561198000000001',
    'eventstart event NaN 0 0', 'eventstart event Infinity 0 0', 'eventstart event 1000001 0 0',
    'giveitem player Wood 0', 'giveitem player Wood 1001', 'giveitem player Wood 1 0', 'giveitem player Wood 1 101',
    'teleport player --to other', 'teleport player 1 2', 'teleport player to other 1', 'teleport player NaN 0 0',
    'skillset player Run 101', 'skillset player Run -1', 'skilladd player Run -101', 'skillreset player', 'heal player 0', 'damage player Infinity',
    'players halla extra', 'players ""',
    'importstatus', 'importstatus extra', 'characterlist extra', 'discordtest https://example.com')) {
    Assert-Syntax $line $false
}
foreach ($line in @('characterbackups', 'characterbackups player 0', 'characterbackups player 1001',
    'characterbackups player -1', 'characterbackups player +1', 'characterbackups player 1.5',
    'characterbackups player NaN', 'characterbackups player 1 extra', 'characterrestore', 'characterrestore player',
    'characterrestore player 0123456789ABCDEF0123456789ABCDEF',
    'characterrestore player 01234567-89ab-cdef-0123-456789abcdef',
    'characterrestore player ../backup', 'characterrestore player C:\backup.fch',
    'characterrestore player 0123456789abcdef0123456789abcdeg',
    'characterrestore player 0123456789abcdef0123456789abcdef extra',
    'character backups player', 'character restore player 0123456789abcdef0123456789abcdef')) {
    Assert-Syntax $line $false
}
foreach ($badBackupId in @(('a' * 31), ('a' * 33), ('ａ' * 32))) {
    Assert-Syntax ('characterrestore player ' + $badBackupId) $false
}
$tokens = Read-Command 'SM:CHAT TeLl "Mixed Case sm:Message"'
Assert-True ($tokens[0] -ceq 'chat' -and $tokens[1] -ceq 'TeLl' -and $tokens[2] -ceq 'Mixed Case sm:Message') 'Only the command prefix may normalize; message case and embedded namespace must remain literal.'
$safePayload = 'Some-Name "Name" \\ sm:character-info; ban other'
$safeLine = 'sm:players ' + $quote.Invoke($null, @($safePayload))
$safeArgs = Read-Command $safeLine
Assert-True ($safeArgs.Length -eq 2 -and $safeArgs[1] -ceq $safePayload) 'Quoted payload resembling commands must stay a single literal argument.'
Assert-True ($syntax.Invoke($null, @('players')) -ceq 'sm:players [target]') 'Players help must expose the optional online target.'
foreach ($prefix in @('', 'sm:', 'SM:')) {
    Assert-True (((Read-Command ($prefix + 'PLAYERS "MiXeD Name"')) -join '|') -ceq 'players|MiXeD Name') 'Optional player lookup must not lowercase the target or translate to the retired player hierarchy.'
    foreach ($retiredLine in @('playerinfo halla', 'skilladd halla Swords 10', 'skillreset halla Swords')) {
        Assert-Syntax ($prefix + $retiredLine) $false
    }
}
$hyphenArgs = Read-Command 'sm:giveitem "Some-Name" Sword-Custom 1'
Assert-True (($hyphenArgs -join '|') -ceq 'item|give|Some-Name|Sword-Custom|1') 'Hyphens in player names and prefab arguments must remain unchanged.'
Assert-True (((Read-Command 'sm:giveitem player Wood 2 3') -join '|') -ceq 'item|give|player|Wood|2|3') 'Existing explicit quality must retain the six-token internal shape.'
Assert-True ($syntax.Invoke($null, @('giveitem')) -ceq 'sm:giveitem <target> <prefab> <amount> [quality] [dataId]') 'Giveitem help must document the preset ID after quality.'
$commandItemDataId = $commands.GetMethod('IsItemDataId', $flags)
$presetItemDataId = $assembly.GetType('ServerManager.Commands.ItemDataPresets', $true).GetMethod('IsValidId', $flags)
$identifierCases = @($null, '', 'A', 'Az_09-', ('a' * 64), ('a' * 65), 'with space',
    '../preset', 'a.b', '한글', 'ａ', "preset`n")
$identifierCases += 0..127 | ForEach-Object { [string][char]$_ }
foreach ($dataId in $identifierCases) {
    $expectedId = $null -ne $dataId -and $dataId -cmatch '\A[a-zA-Z0-9_-]{1,64}\z'
    Assert-True ($commandItemDataId.Invoke($null, [object[]]@($dataId)) -eq $expectedId) `
        'The command preset ID grammar, null handling or 64-character boundary changed.'
    Assert-True ($presetItemDataId.Invoke($null, [object[]]@($dataId)) -eq $expectedId) `
        'Command and YAML preset IDs must accept exactly the same case-sensitive ASCII grammar.'
}
foreach ($dataId in @('A', 'a0_Z-9', ('a' * 64))) {
    $presetArgs = Read-Command ('sm:giveitem "Some Name" Sword-Custom 2 100 ' + $dataId)
    Assert-True ($presetArgs.Length -eq 7 -and $presetArgs[5] -ceq '100' -and $presetArgs[6] -ceq $dataId) 'Preset IDs must follow explicit quality and preserve exact ASCII spelling without an at-sign.'
}
foreach ($dataId in @('@preset', '.', '..', 'preset.json', '../preset', 'C:\preset', 'a/b', 'a:b',
    'a;b', 'a=b', 'with space', '한글', 'ａ', ('a' * 65), '{"key":"RAW_SECRET"}')) {
    Assert-Syntax ('sm:giveitem player Wood 1 1 ' + $quote.Invoke($null, @($dataId))) $false
}
foreach ($line in @('sm:giveitem player Wood 1 Preset', 'sm:giveitem player Wood 1 1 ""',
    'sm:giveitem player Wood 1 0 Preset', 'sm:giveitem player Wood 1 101 Preset',
    'sm:giveitem player Wood 1 1 Preset extra')) {
    Assert-Syntax $line $false
}
$hyphenArgs = Read-Command 'sm:teleport "Some-Name" -2 0 5'
Assert-True (($hyphenArgs -join '|') -ceq 'teleport|Some-Name|-2|0|5') 'Negative coordinate arguments must remain unchanged.'
$target = $commands.GetMethod('Target', $flags)
Assert-True ($target.Invoke($null, [object[]]@(,$tokens)) -ceq '') 'Global shout must not acquire a player target from its message.'
Assert-True ($target.Invoke($null, [object[]]@(,[string[]]@('players'))) -ceq '') 'Online listing has no audit target.'
Assert-True ($target.Invoke($null, [object[]]@(,[string[]]@('players', 'MiXeD Name'))) -ceq 'MiXeD Name') 'Online lookup audits must preserve the optional target.'

$route = $commands.GetMethod('IsRconManagedLine', $flags)
foreach ($line in @('ban 76561198000000001', 'BAN "unterminated', 'unban invalid')) {
    Assert-True ($route.Invoke($null, @($line))) 'A managed or malformed protected verb must never fall through to raw console.'
}
foreach ($line in @('save', 'kick player', 'sm status', 'sm:status', 'sm:ban 76561198000000001', 'banlist', 'smoke', 'printplayers', 'event army_eikthyr', 'heal', 'help', 'status', 'giveitem player Wood 1', 'skillset player Run 1')) {
    Assert-True (-not $route.Invoke($null, @($line))) 'Vanilla or unrelated verbs must not use the common parser.'
}

# Execute real common-queue failure paths without creating a Unity world.
Add-Type -TypeDefinition @'
public static class CommonCommandSmokeAuthorization
{
    public static bool Allowed = true;
    public static int Calls;
    public static bool Check() { Calls++; return Allowed; }
}
'@
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$callerType = $assembly.GetType('ServerManager.Commands.CommandCaller', $true)
$authorize = [Delegate]::CreateDelegate([Func[bool]], [CommonCommandSmokeAuthorization].GetMethod('Check'))
$caller = [Activator]::CreateInstance($callerType, $instanceFlags, $null,
    [object[]]@('test', 'actor-123', 'Smoke caller', $authorize, [Threading.CancellationToken]::None), $null)
$execute = $commands.GetMethod('ExecuteAsync', $flags)
$tick = $commands.GetMethod('Tick', $flags)
$initialize = $commands.GetMethod('Initialize', $flags)
$shutdown = $commands.GetMethod('Shutdown', $flags)
$runtime = $assembly.GetType('ServerManager.Events.ServerEventRuntime', $true)
$epoch = $runtime.GetField('_commandWorldEpoch', $flags)
$originalEpoch = $epoch.GetValue($null)
$initialize.Invoke($null, @()) | Out-Null
try {
    [CommonCommandSmokeAuthorization]::Allowed = $true
    $work = $execute.Invoke($null, @('sm:help', $caller))
    Assert-True (-not $work.IsCompleted) 'Authorized command must still queue for Unity main-thread execution.'
    [CommonCommandSmokeAuthorization]::Allowed = $false
    $tick.Invoke($null, @()) | Out-Null
    Assert-True ($work.Result.Code -eq 'unauthorized' -and -not $work.Result.Success) 'Revoked queued caller must fail closed.'

    [CommonCommandSmokeAuthorization]::Allowed = $true
    $help = $execute.Invoke($null, @('SM:HELP', $caller))
    $tick.Invoke($null, @()) | Out-Null
    Assert-True ($help.Result.Success -and $help.Result.Message.Contains('sm:chat <message> (global shout)') -and
        $help.Result.Message.Contains('sm:announce <message>') -and $help.Result.Message -notmatch '\btell\b|--to|\bsm ') 'Help must expose flat global shout and separate announce, without removed syntax.'
    foreach ($name in $flatNames) {
        Assert-True ($help.Result.Message.Contains($syntax.Invoke($null, @($name)))) "Help must include every shared syntax without truncation: $name"
    }
    Assert-True ($help.Result.Message.Length -le 1800 -and $help.Result.Message.Contains('vanilla save/kick/ban/unban')) 'Bounded flat help must explain the separate vanilla console operations.'
    Assert-True ($help.Result.Message -notmatch 'playerinfo|skilladd|skillreset') 'Help must not advertise removed commands.'

    $work = $execute.Invoke($null, @('SM:PLAYERS "MiXeD Name"', $caller))
    $queued = @($commands.GetField('Queue', $flags).GetValue($null))[0]
    Assert-True ($queued.GetType().GetField('Action', $instanceFlags).GetValue($queued) -ceq 'players') 'Targeted lookup must use the unified audit action.'
    Assert-True ($queued.GetType().GetField('Target', $instanceFlags).GetValue($queued) -ceq 'MiXeD Name') 'Targeted lookup must retain its authenticated-command audit target.'
    Assert-True (($queued.GetType().GetField('Args', $instanceFlags).GetValue($queued) -join '|') -ceq 'players|MiXeD Name') 'Targeted lookup must retain its two-token dispatch shape.'
    [CommonCommandSmokeAuthorization]::Allowed = $false
    $tick.Invoke($null, @()) | Out-Null
    Assert-True ($work.Result.Code -eq 'unauthorized') 'Optional player lookup must still reauthorize before executing.'
    [CommonCommandSmokeAuthorization]::Allowed = $true

    $work = $execute.Invoke($null, @('SM:GIVEITEM "Some Name" Wood 10', $caller))
    $queuedItems = @($commands.GetField('Queue', $flags).GetValue($null))
    Assert-True ($queuedItems.Count -eq 1) 'A validated flat feature must enter the same bounded queue.'
    $queued = $queuedItems[0]
    $queuedType = $queued.GetType()
    Assert-True ($queuedType.GetField('Action', $instanceFlags).GetValue($queued) -ceq 'giveitem') 'Audit action must use the canonical public leaf, not internal item give.'
    Assert-True ($queuedType.GetField('Target', $instanceFlags).GetValue($queued) -ceq 'Some Name') 'Audit target must still derive from the validated internal argument position.'
    Assert-True (($queuedType.GetField('Args', $instanceFlags).GetValue($queued) -join '|') -ceq 'item|give|Some Name|Wood|10') 'Queued backend dispatch must retain its existing validated internal shape.'
    [CommonCommandSmokeAuthorization]::Allowed = $false
    $tick.Invoke($null, @()) | Out-Null
    Assert-True ($work.Result.Code -eq 'unauthorized') 'Flat feature translation must not bypass execution-time authorization.'
    [CommonCommandSmokeAuthorization]::Allowed = $true

    $work = $execute.Invoke($null, @('sm:giveitem "Some Name" Wood 10 2 Mixed-Preset_01', $caller))
    $queued = @($commands.GetField('Queue', $flags).GetValue($null))[0]
    Assert-True (($queued.GetType().GetField('Args', $instanceFlags).GetValue($queued) -join '|') -ceq 'item|give|Some Name|Wood|10|2|Mixed-Preset_01') 'Preset selection must enter the same queue as the seven-token canonical dispatch shape.'
    Assert-True ($queued.GetType().GetField('Target', $instanceFlags).GetValue($queued) -ceq 'Some Name') 'A preset ID must not change the player audit target.'
    [CommonCommandSmokeAuthorization]::Allowed = $false
    $tick.Invoke($null, @()) | Out-Null
    Assert-True ($work.Result.Code -eq 'unauthorized' -and $work.Result.Data['data_id'] -ceq 'Mixed-Preset_01') 'Preset selection must retain authorization and only the validated selected ID for auditing.'
    [CommonCommandSmokeAuthorization]::Allowed = $true

    $work = $execute.Invoke($null, @('sm:characterrestore "Some Name" 0123456789abcdef0123456789abcdef', $caller))
    [CommonCommandSmokeAuthorization]::Allowed = $false
    $tick.Invoke($null, @()) | Out-Null
    Assert-True ($work.Result.Code -eq 'unauthorized' -and $work.Result.Data['backup_id'] -ceq '0123456789abcdef0123456789abcdef') 'A denied restore must retain only its validated selected backup identity without executing.'
    [CommonCommandSmokeAuthorization]::Allowed = $true

    $work = $execute.Invoke($null, @('SM:SKILLSET "Some-Name" Run 50', $caller))
    $queued = @($commands.GetField('Queue', $flags).GetValue($null))[0]
    Assert-True ($queued.GetType().GetField('Action', $instanceFlags).GetValue($queued) -ceq 'skillset') 'Audit action must use the new hyphen-free feature name.'
    Assert-True ($queued.GetType().GetField('Target', $instanceFlags).GetValue($queued) -ceq 'Some-Name') 'Audit target must preserve hyphens in player names.'
    [CommonCommandSmokeAuthorization]::Allowed = $false
    $tick.Invoke($null, @()) | Out-Null
    Assert-True ($work.Result.Code -eq 'unauthorized') 'Renamed skill commands must retain execution-time authorization.'
    [CommonCommandSmokeAuthorization]::Allowed = $true

    foreach ($line in @('sm:players', 'sm:players "MiXeD Name"')) {
        $work = $execute.Invoke($null, @($line, $caller))
        $tick.Invoke($null, @()) | Out-Null
        Assert-True ($work.Result.Code -eq 'server_not_ready' -and -not $work.Result.Success) 'Both online listing and selected-player lookup require a ready server world.'
    }

    $work = $execute.Invoke($null, @('ban 76561198000000001', $caller))
    $tick.Invoke($null, @()) | Out-Null
    Assert-True ($work.Result.Code -eq 'server_not_ready' -and -not $work.Result.Success) 'Mutation without a ready server world must fail.'

    $work = $execute.Invoke($null, @('help', $caller))
    $epoch.SetValue($null, [long]($originalEpoch + 1))
    $tick.Invoke($null, @()) | Out-Null
    Assert-True ($work.Result.Code -eq 'world_changed' -and -not $work.Result.Success) 'Work must not cross world generations.'
    $epoch.SetValue($null, $originalEpoch)

    foreach ($line in @('save', 'sm save', 'sm:save', 'kick player', 'sm kick player', 'sm:kick player',
        'sm:ban player', 'sm:unban 76561198000000001', 'sm:rcon save', 'sm:sm:help', '"sm:help"', '/sm:help',
        'spawn Troll', 'scan', 'remove object', 'item give player Wood 1', 'item drop Wood 1', 'maintenance', 'import apply',
        'sm:playerinfo halla', 'playerinfo halla', 'sm:skilladd halla Swords 10', 'skilladd halla Swords 10',
        'sm:skillreset halla Swords', 'skillreset halla Swords',
        'ban list', 'banlist extra', 'teleport player --to other', 'tell player Message', 'sm tell player Message', 'SM TeLl "Player Name" "Message"')) {
        $callsBefore = [CommonCommandSmokeAuthorization]::Calls
        $rejected = $execute.Invoke($null, @($line, $caller))
        Assert-True ($rejected.IsCompleted -and -not $rejected.Result.Success -and $rejected.Result.Code -eq 'invalid_command') 'Excluded command must return explicit failure.'
        Assert-True ([CommonCommandSmokeAuthorization]::Calls -eq $callsBefore) 'Excluded commands must be denied before executable work is admitted.'
    }

    $stop = [Threading.CancellationTokenSource]::new()
    try {
        $cancelledCaller = [Activator]::CreateInstance($callerType, $instanceFlags, $null,
            [object[]]@('test', 'actor-123', 'Smoke caller', $authorize, $stop.Token), $null)
        $stop.Cancel()
        $cancelled = $execute.Invoke($null, @('help', $cancelledCaller))
        Assert-True ($cancelled.IsCompleted -and $cancelled.Result.Code -eq 'cancelled') 'Retired session must fail before queue admission.'
        $cancelled = $execute.Invoke($null, @('characterrestore player 0123456789abcdef0123456789abcdef', $cancelledCaller))
        Assert-True ($cancelled.Result.Code -eq 'cancelled' -and $cancelled.Result.Data['backup_id'] -ceq '0123456789abcdef0123456789abcdef') 'Prequeue cancellation must preserve the selected backup for audit.'
    } finally { $stop.Dispose() }

    $pending = @()
    foreach ($number in 1..64) { $pending += $execute.Invoke($null, @('help', $caller)) }
    $overflow = $execute.Invoke($null, @('help', $caller))
    Assert-True ($overflow.IsCompleted -and $overflow.Result.Code -eq 'queue_full') 'Queue must enforce its outstanding work limit.'
    $overflow = $execute.Invoke($null, @('characterrestore player 0123456789abcdef0123456789abcdef', $caller))
    Assert-True ($overflow.Result.Code -eq 'queue_full' -and $overflow.Result.Data['backup_id'] -ceq '0123456789abcdef0123456789abcdef') 'Full-queue rejection must preserve the selected backup for audit.'
    foreach ($number in 1..8) { $tick.Invoke($null, @()) | Out-Null }
    Assert-True (@($pending | Where-Object { -not $_.IsCompleted -or -not $_.Result.Success }).Count -eq 0) 'Main-thread ticks must drain admitted help commands.'
} finally {
    $epoch.SetValue($null, $originalEpoch)
    $shutdown.Invoke($null, @()) | Out-Null
}

# The reused five-operation queue must also reauthorize at its later mutation boundary.
$eventInitialized = $runtime.GetField('_initialized', $flags)
$eventAdmission = $runtime.GetField('_acceptCommands', $flags)
$oldInitialized = $eventInitialized.GetValue($null)
$oldAdmission = $eventAdmission.GetValue($null)
$kindType = $assembly.GetType('ServerManager.Events.ServerEventCommandKind', $true)
try {
    $eventInitialized.SetValue($null, $true)
    $eventAdmission.SetValue($null, $true)
    [CommonCommandSmokeAuthorization]::Allowed = $false
    foreach ($name in @('Save', 'Announce', 'Kick', 'Ban', 'Unban')) {
        $kind = [Enum]::Parse($kindType, $name)
        $primary = if ($name -eq 'Save') { '' } else { '76561198000000001' }
        $task = $runtime.GetMethod('EnqueueCommand', $flags).Invoke($null,
            [object[]]@($kind, $primary, '', [Threading.CancellationToken]::None, $authorize))
        Assert-True (-not $task.IsCompleted) 'Shared event operation must enter the existing queue.'
        $runtime.GetMethod('ProcessCommands', $flags).Invoke($null, @()) | Out-Null
        Assert-True ($task.Result.Code -eq 'unauthorized' -and -not $task.Result.Success) 'Each reused event operation must reject its revoked execution guard before Unity access.'
    }
} finally {
    $eventInitialized.SetValue($null, $oldInitialized)
    $eventAdmission.SetValue($null, $oldAdmission)
}

# Valheim's IsAdmin/IsAllowed treats bare Steam64 and Steam_<id> as
# equivalent, but SyncedList.Remove removes only one exact List<string> item.
# Exercise production membership selection with those actual CLR semantics.
$listEntries = $commands.GetMethod('GetSteamListEntries', $flags)
$steamId = '76561198000000001'
$otherSteamId = '76561198000000002'
foreach ($commandKind in @('adminremove', 'accessremove', 'unban')) {
    $fixture = [Collections.Generic.List[string]]::new()
    foreach ($entry in @($steamId, ('Steam_' + $steamId), $steamId, ('Steam_' + $steamId),
        $otherSteamId, ('Steam_' + $otherSteamId), ('steamworks:' + $steamId), 'Other Player')) {
        $fixture.Add($entry)
    }
    $selected = [string[]]$listEntries.Invoke($null, [object[]]@($fixture, $steamId))
    Assert-True ($selected.Length -eq 4) "$commandKind must select every exact native alias occurrence."
    $prefixed = [string[]]$listEntries.Invoke($null, [object[]]@($fixture, ('Steam_' + $steamId)))
    Assert-True ($prefixed.Length -eq 4) "$commandKind must also handle an authenticated prefixed transport identity."
    foreach ($entry in $selected) { $fixture.Remove($entry) | Out-Null }
    $remaining = [string[]]$listEntries.Invoke($null, [object[]]@($fixture, $steamId))
    Assert-True ($remaining.Length -eq 0) "$commandKind must revoke membership, not leave an alias/duplicate."
    Assert-True ($fixture.Count -eq 4 -and $fixture.Contains($otherSteamId) -and
        $fixture.Contains('Steam_' + $otherSteamId) -and $fixture.Contains('steamworks:' + $steamId) -and
        $fixture.Contains('Other Player')) "$commandKind must not migrate other entries or reinterpret names."
}
$prefixOnly = [string[]]@('Steam_' + $steamId)
Assert-True (([string[]]$listEntries.Invoke($null, [object[]]@($prefixOnly, $steamId))).Length -eq 1) 'Add must recognize an existing native prefixed identity and avoid an alias duplicate.'

# Compiled production paths must use that policy for both selection and
# post-mutation verification, while retaining literal SyncedList.Remove calls.
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
$commonIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.Commands.ServerCommands'
$eventIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.Events.ServerEventRuntime'
foreach ($method in @(
    ($commonIL.Methods | Where-Object Name -eq 'EditList'),
    ($eventIL.Methods | Where-Object Name -eq 'TrySetBanListEntry')
)) {
    $calls = @($method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object Operand)
    Assert-True (@($calls | Where-Object Name -eq 'GetSteamListEntries').Count -ge 2) "$($method.Name) must verify actual alias membership after mutation."
    Assert-True (@($calls | Where-Object { $_.Name -eq 'Remove' -and $_.DeclaringType.FullName -eq 'SyncedList' }).Count -ge 1) "$($method.Name) must remove literal entries, never reinterpret a target name."
}
$definition.Dispose()

# Audit accepts only the explicit restore metadata schema, not arbitrary service data.
$auditFields = $runtime.GetMethod('AddCommandResultFields', $flags)
$presetMetadata = [Collections.Generic.Dictionary[string,string]]::new()
$presetMetadata.Add('data_id', 'restoringnow')
$presetMetadata.Add('CustomData', 'DO_NOT_PUBLISH_CUSTOM_DATA')
$presetAudit = [Collections.Generic.Dictionary[string,string]]::new()
$presetAudit.Add('message', 'Administrative grant')
$auditFields.Invoke($null, @('giveitem', $presetMetadata, $presetAudit)) | Out-Null
Assert-True ($presetAudit.Count -eq 2 -and $presetAudit['data_id'] -ceq 'restoringnow' -and $presetAudit['message'] -ceq 'Administrative grant; data_id=restoringnow') 'Preset audit must retain only a validated ID, not custom data.'
$presetMetadata['data_id'] = "not/a/preset"
$presetAudit.Clear()
$auditFields.Invoke($null, @('giveitem', $presetMetadata, $presetAudit)) | Out-Null
Assert-True ($presetAudit.Count -eq 0) 'Invalid preset IDs must not enter the audit allowlist.'
$metadata = [Collections.Generic.Dictionary[string,string]]::new()
foreach ($pair in @{
    backup_id = '0123456789abcdef0123456789abcdef'; previous_revision = '4'; restored_revision = '2'; new_revision = '5'
    target_account = 'steam:76561198000000001'; target_character = 'Some Name'
    payload = 'PROFILE_SECRET'; path = 'C:\BACKUP_PATH_SECRET'; message = 'OUTPUT_SECRET'; command = 'OVERRIDE_SECRET'
}.GetEnumerator()) { $metadata.Add($pair.Key, $pair.Value) }
$auditData = [Collections.Generic.Dictionary[string,string]]::new()
$auditData.Add('message', 'Administrative restore')
$auditFields.Invoke($null, @('characterrestore', $metadata, $auditData)) | Out-Null
Assert-True ($auditData.Count -eq 4 -and $auditData['backup_id'] -ceq $metadata['backup_id'] -and
    -not $auditData.ContainsKey('previous_revision') -and
    -not $auditData.ContainsKey('restored_revision') -and
    -not $auditData.ContainsKey('new_revision')) `
    'Restore audit must keep the selected ID and target without inventing disk revisions.'
Assert-True (-not (($auditData.Values -join '|') -match 'SECRET')) 'Audit must exclude raw payload, paths, output, and unrecognized fields.'
foreach ($key in @('backup_id', 'target_account', 'target_character')) {
    Assert-True ($auditData['message'].Contains($key + '=' + $metadata[$key])) "Human events-audit.log message must retain allowlisted restore metadata: $key"
}
Assert-True ($auditData['message'].Length -lt 1000) 'Restore metadata must remain bounded in the human audit message.'
$auditData.Clear()
$auditFields.Invoke($null, @('characterinfo', $metadata, $auditData)) | Out-Null
Assert-True ($auditData.Count -eq 0) 'Other command kinds must not acquire restore result fields.'
$metadata['backup_id'] = '0123456789ABCDEF0123456789ABCDEF'
$metadata['previous_revision'] = '-1'
$metadata['restored_revision'] = '9223372036854775808'
$metadata['new_revision'] = '1.5'
$metadata['target_character'] = 'x' * 200
$auditFields.Invoke($null, @('characterrestore', $metadata, $auditData)) | Out-Null
Assert-True ($auditData.Count -eq 2 -and $auditData['target_character'].Length -le 128) 'Malformed identities/revisions must be omitted and target metadata bounded.'

$source = Get-Content -LiteralPath (Join-Path $projectRoot 'Commands\ServerCommands.cs') -Raw
$events = Get-Content -LiteralPath (Join-Path $projectRoot 'Events\ServerEventRuntime.cs') -Raw
Assert-True ($source.Contains('ServerEventRuntime.EnqueueCommand(kind, primary, secondary,')) 'Existing mutations must reuse the event queue.'
Assert-True ($events.Contains('command.ExecutionGuard != null && !command.ExecutionGuard()')) 'Queued legacy operations must recheck caller authorization.'
Assert-True ($source.Contains('work.WorldEpoch != ServerEventRuntime.CommandWorldEpoch')) 'Queued commands must be pinned to a world epoch.'
Assert-True ($source.Contains('work.Pending.IsCompleted') -and $source.Contains('FinishTask(work)')) 'Async completion and audits must return through the main-thread tick.'
Assert-True (-not $source.Contains('TryRunCommand') -and -not $source.Contains('FakePlayer')) 'Common executor must not permit raw console dispatch or FakePlayer.'
Write-Output 'Common command parser, grammar, exclusion, authorization, cancellation, world epoch, and queue boundary smoke tests passed.'
