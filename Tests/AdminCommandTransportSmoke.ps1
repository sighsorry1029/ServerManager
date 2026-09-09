param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
# AccessTools initializes its real installed Cecil dependency when the optional
# completion type caches Terminal's input-field metadata in this headless host.
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.dll', 'assembly_utils.dll',
    'SoftReferenceableAssets.dll', 'com.rlabrecque.steamworks.net.dll', 'Splatform.dll', 'assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (Split-Path -Parent $pluginPath) $name))) | Out-Null
}
$assembly = [Reflection.Assembly]::LoadFrom($pluginPath)
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$codec = $assembly.GetType('ServerManager.Commands.AdminCommandCodec', $true)
$packetType = $assembly.GetType('ServerManager.Commands.AdminPacket', $true)
$kindType = $assembly.GetType('ServerManager.Commands.AdminPacketKind', $true)
$runtime = $assembly.GetType('ServerManager.ServerManagerRuntime', $true)
$encode = $codec.GetMethod('Encode', $staticFlags)
$decode = $codec.GetMethod('TryDecode', $staticFlags)
$maximum = $codec.GetField('MaximumPacketBytes', $staticFlags).GetRawConstantValue()
Assert-True ($maximum -eq 16384) 'The administrative wire budget must remain 16 KiB.'

# Pure completion policy only: never construct a Terminal or query a live player.
$completionType = $assembly.GetType('ServerManager.ServerManagerTerminalCommands', $true)
$completionGate = $completionType.GetMethod('IsFirstArgumentCompletion', $staticFlags)
$completeNames = $completionType.GetMethod('GetPlayerNameCompletions', $staticFlags)
foreach ($text in @('sm:players ', 'sm:players ha', 'SM:PLAYERS ha')) {
    Assert-True ($completionGate.Invoke($null, [object[]]@('players', $text, $text.Length, $text.Length, $text.Length))) 'First target at the end of an unselected input must allow bounded suggestions.'
}
foreach ($text in @('sm players ha', 'players ha', 'sm:playerinfo ha', 'sm:player-info ha', 'sm:characterinfo ha', 'sm:players  ha',
    'sm:players ha ', 'sm:players ha next', 'sm:players "ha', 'sm:players ha\',
    ('sm:players ' + ('x' * 2048)))) {
    Assert-True (-not $completionGate.Invoke($null, [object[]]@('players', $text, $text.Length, $text.Length, $text.Length))) 'Legacy, later, quoted or oversized input must not enter native first-argument replacement.'
}
$completionText = 'sm:players halla'
Assert-True (-not $completionGate.Invoke($null, [object[]]@('players', $completionText, 5, 5, 5))) 'Completion must not replace from a caret in the middle of input.'
Assert-True (-not $completionGate.Invoke($null, [object[]]@('players', $completionText, $completionText.Length, 0, $completionText.Length))) 'Completion must not overwrite an active selection.'
$rawNames = [string[]]@('Loki', 'Halla_2', 'Halla', 'halla', '', $null, 'Two Words', 'quoted"name', 'back\slash', 'a/b',
    '<b>Name</b>', "line`nfeed", ('Hidden' + [char]0x200B + 'Name'), 'Steam_76561198000000001', 'STEAM_76561198000000001', 'steamworks:76561198000000001',
    '123', '-123', '+123', '76561198000000001', ('x' * 129))
$nameSuggestions = @($completeNames.Invoke($null, [object[]]@(,$rawNames)))
Assert-True ($nameSuggestions.Count -eq 2 -and $nameSuggestions -contains 'Loki' -and $nameSuggestions -contains 'Halla_2') 'Suggestions must exclude ambiguous, multiword, reserved-identity, quoted, control and markup names.'
$manyNames = [string[]]@((0..255 | ForEach-Object { 'zzPlayer' + $_.ToString('D3') }) + @('AAA_beyond_scan_limit'))
$limitedSuggestions = @($completeNames.Invoke($null, [object[]]@(,$manyNames)))
Assert-True ($limitedSuggestions.Count -eq 128 -and $limitedSuggestions -notcontains 'AAA_beyond_scan_limit') 'Optional suggestions must scan at most 256 names and return at most 128.'

function Field($Value, [string]$Name) { $Value.GetType().GetField($Name, $instanceFlags).GetValue($Value) }
function Set-Field($Value, [string]$Name, $Data) { $Value.GetType().GetField($Name, $instanceFlags).SetValue($Value, $Data) }
function New-Packet([string]$Kind, [string[]]$Arguments) {
    $packet = [Activator]::CreateInstance($packetType, $true)
    Set-Field $packet 'Kind' ([Enum]::Parse($kindType, $Kind))
    Set-Field $packet 'SessionId' ([byte[]](1..16))
    Set-Field $packet 'Nonce' ([byte[]](1..32))
    Set-Field $packet 'Sequence' ([uint32]1)
    Set-Field $packet 'RequestId' ([Guid]::NewGuid())
    Set-Field $packet 'Revision' ([long]23)
    Set-Field $packet 'Arguments' $Arguments
    return $packet
}
function Encode-Bytes($Packet) {
    try { $package = $encode.Invoke($null, @($Packet)) }
    catch { throw "Encoding fixture failed: kind=$(Field $Packet 'Kind') lengths=$((Field $Packet 'Arguments' | ForEach-Object Length) -join ','): $_" }
    return ,([byte[]]$package.GetArray())
}
function Decode-Bytes([byte[]]$Bytes, [bool]$Expected) {
    $package = [ZPackage]::new($Bytes)
    $arguments = [object[]]@($package, $null)
    $accepted = $decode.Invoke($null, $arguments)
    Assert-True ($accepted -eq $Expected) "Unexpected packet decoding result for $($Bytes.Length) bytes."
    if ($accepted) { return $arguments[1] }
}
function Assert-EncodeRejected($Packet, [string]$Message) {
    $failed = $false
    try { $encode.Invoke($null, @($Packet)) | Out-Null }
    catch { $failed = $true }
    Assert-True $failed $Message
}

# Every protocol direction has an actual encode/decode round trip with identity preserved.
foreach ($fixture in @(
    @{ Kind = 'Request'; Args = [string[]]@('sm:chat "hello \"world\""') },
    @{ Kind = 'Result'; Args = [string[]]@('1', 'save_requested', 'Not a disk completion.', 'save-operation-23') },
    @{ Kind = 'PlayerAction'; Args = [string[]]@('item', 'give', 'Wood', '10', '1') },
    @{ Kind = 'PlayerAction'; Args = [string[]]@('item', 'give', 'ShieldWood', '1', '2', '{"mod#lock":"","mod#value":"a\\b\n\u0085\u2028\u2029"}') },
    @{ Kind = 'PlayerResult'; Args = [string[]]@('1', 'ram_accepted', 'RAM accepted, checkpoint pending.') },
    @{ Kind = 'Shout'; Args = [string[]]@('Server', ('Unicode: ' + [char]0xD55C + [char]0xAE00)) },
    @{ Kind = 'Shout'; Args = [string[]]@('[Discord] Name', 'Literal text: sm ban is not a command') }
)) {
    $packet = New-Packet $fixture.Kind $fixture.Args
    $roundTrip = Decode-Bytes (Encode-Bytes $packet) $true
    foreach ($fieldName in @('Kind', 'Sequence', 'RequestId', 'Revision')) {
        Assert-True ((Field $roundTrip $fieldName) -eq (Field $packet $fieldName)) "Round trip changed $fieldName."
    }
    foreach ($fieldName in @('SessionId', 'Nonce', 'Arguments')) {
        Assert-True (((Field $roundTrip $fieldName) -join '|') -ceq ((Field $packet $fieldName) -join '|')) "Round trip changed $fieldName."
    }
}

$request = New-Packet 'Request' @('help')
$bytes = Encode-Bytes $request
for ($length = 0; $length -lt $bytes.Length; ++$length) {
    $cut = [byte[]]::new($length)
    [Array]::Copy($bytes, $cut, $length)
    Decode-Bytes $cut $false | Out-Null
}
Decode-Bytes ([byte[]]($bytes + [byte]0)) $false | Out-Null
# Fixed header offsets: magic/version/kind, 16-byte session, 32-byte nonce,
# uint sequence, 16-byte request ID, long revision, byte argument count.
foreach ($mutation in @(@(0, 0), @(4, 2), @(5, 0), @(5, 5), @(5, 7), @(82, 17), @(85, 255))) {
    $bad = [byte[]]$bytes.Clone()
    $bad[[int]$mutation[0]] = [byte]$mutation[1]
    Decode-Bytes $bad $false | Out-Null
}
foreach ($sequence in @([uint32]0, [uint32]::MaxValue)) {
    $bad = [byte[]]$bytes.Clone()
    [Array]::Copy([BitConverter]::GetBytes($sequence), 0, $bad, 54, 4)
    Decode-Bytes $bad $false | Out-Null
}
$bad = [byte[]]$bytes.Clone()
[Array]::Clear($bad, 58, 16)
Decode-Bytes $bad $false | Out-Null
$bad = [byte[]]$bytes.Clone()
[Array]::Copy([BitConverter]::GetBytes([long]-1), 0, $bad, 74, 8)
Decode-Bytes $bad $false | Out-Null
$bad = [byte[]]$bytes.Clone()
$bad[83] = 255; $bad[84] = 255
Decode-Bytes $bad $false | Out-Null
Decode-Bytes ([byte[]]::new(16385)) $false | Out-Null

$boundary = New-Packet 'PlayerAction' @(('a' * 4096), ('b' * 4096), ('c' * 4096), ('d' * 4005))
$boundaryBytes = Encode-Bytes $boundary
Assert-True ($boundaryBytes.Length -eq 16384) 'Exact 16 KiB packet fixture has wrong size.'
Decode-Bytes $boundaryBytes $true | Out-Null
Set-Field $boundary 'Arguments' ([string[]]@(('a' * 4096), ('b' * 4096), ('c' * 4096), ('d' * 4006)))
Assert-EncodeRejected $boundary 'Encoder must reject packets exceeding the shared wire budget.'
foreach ($packet in @(
    (New-Packet 'Request' @('a' * 2049)),
    (New-Packet 'Result' @('yes', 'code', 'text')),
    (New-Packet 'PlayerResult' @('1', ('a' * 65), 'text')),
    (New-Packet 'Shout' @('title', ('a' * 501))),
    (New-Packet 'Shout' @(('a' * 101), 'text')),
    (New-Packet 'PlayerAction' @('one')),
    (New-Packet 'Request' @("a`0b"))
)) { Assert-EncodeRejected $packet 'Encoder must reject a malformed command body.' }

Assert-True (-not [Enum]::IsDefined($kindType, 'Display')) 'Removed private/normal display must not remain a protocol enum member.'
$removedDisplay = New-Packet 'Shout' @('Server', 'not a private message')
$removedDisplayBytes = Encode-Bytes $removedDisplay
$removedDisplayBytes[5] = 5
Decode-Bytes $removedDisplayBytes $false | Out-Null
Set-Field $removedDisplay 'Kind' ([Enum]::ToObject($kindType, [byte]5))
Assert-EncodeRejected $removedDisplay 'Removed display kind 5 must not encode or decode even with its formerly valid body.'

$parseResult = $runtime.GetMethod('ParseAdminResult', $staticFlags)
$saveResultPacket = New-Packet 'Result' @('1', 'save_requested', 'Save requested.', 'actual-world-save-operation')
$saveResult = $parseResult.Invoke($null, @($saveResultPacket))
Assert-True ($saveResult.OperationId -ceq 'actual-world-save-operation') 'Result transport must preserve the actual save operation ID, not substitute its request correlation ID.'
$readResult = $parseResult.Invoke($null, @((New-Packet 'PlayerResult' @('1', 'skills', 'Run=10'))))
Assert-True ($readResult.OperationId -eq '') 'A player result without an operation ID must not manufacture one from its request GUID.'

# Exercise the actual capture acknowledgement gate, including pending later saves.
$pipelineType = $assembly.GetType('ServerManager.ClientCharacterSavePipeline', $true)
$reasonType = $assembly.GetType('ServerManager.ClientCharacterSaveReason', $true)
$pipeline = [Activator]::CreateInstance($pipelineType, $instanceFlags, $null, [object[]]@([long]10, [long]50), $null)
$offer = $pipelineType.GetMethod('Offer', $instanceFlags)
$start = $pipelineType.GetMethod('TryStartNext', $instanceFlags)
$ack = $pipelineType.GetMethod('TryCompleteAcknowledgement', $instanceFlags)
$hasAck = $pipelineType.GetMethod('HasAcknowledgedCapture', $instanceFlags)
$full = [Enum]::Parse($reasonType, 'Vanilla')
$inventory = [Enum]::Parse($reasonType, 'InventoryDirty')
$capture = $offer.Invoke($pipeline, [object[]]@([byte[]]@(10, 20, 30), $full))
Assert-True (-not $hasAck.Invoke($pipeline, @($capture))) 'Offering a capture is not an acknowledgement.'
$startArgs = [object[]]@([long]8, [long]100, $null)
Assert-True ($start.Invoke($pipeline, $startArgs)) 'Capture should start at the acknowledged revision.'
$wrongAck = [object[]]@([long]8, [long]10, $null, $null, $null)
Assert-True (-not $ack.Invoke($pipeline, $wrongAck)) 'Wrong ACK revision must not confirm a capture.'
Assert-True (-not $hasAck.Invoke($pipeline, @($capture))) 'Rejected ACK must leave the action unconfirmed.'
$later = $offer.Invoke($pipeline, [object[]]@([byte[]]@(40, 50), $inventory))
$goodAck = [object[]]@([long]8, [long]9, $null, $null, $null)
Assert-True ($ack.Invoke($pipeline, $goodAck)) 'Exact ACK should be accepted.'
Assert-True ($hasAck.Invoke($pipeline, @($capture))) 'Exact capture ACK should confirm the action even with a later pending save.'
Assert-True (-not $hasAck.Invoke($pipeline, @($later))) 'Earlier ACK must not confirm a later pending capture.'
Assert-True (-not $hasAck.Invoke($pipeline, @([uint64]0))) 'Zero capture ID must never count as accepted.'
$pipelineType.GetMethod('Close', $instanceFlags).Invoke($pipeline, @()) | Out-Null
Assert-True (-not $hasAck.Invoke($pipeline, @($capture))) 'Retired pipeline must not confirm prior captures.'

# Identity-shaped selectors must never fall through to a user-controlled name.
$matches = $runtime.GetMethod('MatchesAdminTarget', $staticFlags)
$id = '76561198000000001'
$other = '76561198000000002'
foreach ($selector in @($id, ('Steam_' + $id), ('steamworks:' + $id), ($id + '/Viking'), ('steamworks:' + $id + '/Viking'))) {
    Assert-True ($matches.Invoke($null, [object[]]@($selector, ('steamworks:' + $id), 'Viking', [long]42))) "Canonical account selector should match: $selector"
}
Assert-True ($matches.Invoke($null, [object[]]@('vIkInG', ('steamworks:' + $id), 'Viking', [long]42))) 'Unique ordinary names may match case-insensitively.'
Assert-True ($matches.Invoke($null, [object[]]@('42', ('steamworks:' + $id), 'Viking', [long]42))) 'Explicit character player ID should match its actual ID.'
Assert-True (-not $matches.Invoke($null, [object[]]@($id, ('steamworks:' + $other), $id, [long]42))) 'A numeric player name must not shadow another account Steam64.'
Assert-True (-not $matches.Invoke($null, [object[]]@(('Steam_' + $id), ('steamworks:' + $other), ('Steam_' + $id), [long]42))) 'A prefixed player name must not shadow an explicit Steam64.'
Assert-True (-not $matches.Invoke($null, [object[]]@('42', ('steamworks:' + $other), '42', [long]43))) 'A numeric player name must not shadow another character player ID.'

# Exercise the actual selector for zero/one/multiple candidates, not a mock
# online session or a claim that Unity's full ready-peer resolver was executed.
$lookupCandidates = @(
    @{ Account = 'steamworks:' + $id; Name = 'Viking'; PlayerId = [long]42 },
    @{ Account = 'steamworks:' + $other; Name = 'Viking'; PlayerId = [long]43 },
    @{ Account = 'steamworks:76561198000000003'; Name = 'Loki'; PlayerId = [long]44 }
)
foreach ($lookup in @(
    @{ Selector = 'Missing'; Count = 0 },
    @{ Selector = 'Loki'; Count = 1 },
    @{ Selector = 'vIkInG'; Count = 2 },
    @{ Selector = $id + '/Viking'; Count = 1 },
    @{ Selector = '42'; Count = 1 }
)) {
    $matchedCount = @($lookupCandidates | Where-Object {
        $matches.Invoke($null, [object[]]@($lookup.Selector, $_.Account, $_.Name, $_.PlayerId))
    }).Count
    Assert-True ($matchedCount -eq $lookup.Count) 'Unified online lookup must retain exact, missing and ambiguous selector behavior.'
}

# Completion correlation and revocation can be exercised without touching Unity:
# mismatched peers/sessions return immediately, and a revoked caller fails first.
Add-Type -TypeDefinition @'
public static class AdminTransportSmokeCaller
{
    public static bool Deny() { return false; }
}
'@
$callerType = $assembly.GetType('ServerManager.Commands.CommandCaller', $true)
$deny = [Delegate]::CreateDelegate([Func[bool]], [AdminTransportSmokeCaller].GetMethod('Deny'))
$caller = [Activator]::CreateInstance($callerType, $instanceFlags, $null,
    [object[]]@('discord', 'original-actor-123', 'Original actor', $deny, [Threading.CancellationToken]::None), $null)
$completeResult = $runtime.GetMethod('CompletePlayerAdminAction', $staticFlags)
$pendingType = $runtime.GetNestedType('PendingAdminAction', [Reflection.BindingFlags]'NonPublic')
$actions = $runtime.GetField('AdminActions', $staticFlags).GetValue($null)
Assert-True ($actions.Count -eq 0) 'Isolated completion tests must start with no live actions.'
$rpc = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([ZRpc])
$differentRpc = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([ZRpc])
$packet = New-Packet 'PlayerResult' @('1', 'ram_accepted', 'Client reports success.')
$pending = [Activator]::CreateInstance($pendingType, $true)
Set-Field $pending 'Rpc' $rpc
Set-Field $pending 'Id' (Field $packet 'RequestId')
Set-Field $pending 'Session' ([byte[]](Field $packet 'SessionId').Clone())
Set-Field $pending 'Deadline' ([long]::MaxValue)
Set-Field $pending 'Caller' $caller
$completion = Field $pending 'Completion'
try {
    $actions.Add((Field $packet 'RequestId'), $pending)
    $completeResult.Invoke($null, @($differentRpc, $packet)) | Out-Null
    Assert-True (-not $completion.Task.IsCompleted -and $actions.Count -eq 1) 'Another authenticated connection must not complete this target action.'
    $wrongSession = [byte[]](Field $packet 'SessionId').Clone()
    $wrongSession[0] = 99
    Set-Field $packet 'SessionId' $wrongSession
    $completeResult.Invoke($null, @($rpc, $packet)) | Out-Null
    Assert-True (-not $completion.Task.IsCompleted -and $actions.Count -eq 1) 'A retired session must not complete a current action.'
    Set-Field $packet 'SessionId' ([byte[]](Field $pending 'Session').Clone())
    $completeResult.Invoke($null, @($rpc, $packet)) | Out-Null
    Assert-True ($completion.Task.IsCompleted -and $completion.Task.Result.Code -eq 'action_unconfirmed' -and $actions.Count -eq 0) 'Revocation must reject a completion before any success claim.'
} finally { $actions.Clear() }
$managed = $runtime.GetMethod('ExecuteManagedAdminCommandAsync', $staticFlags)
if ($PSVersionTable.PSVersion.Major -ge 6) {
    $offline = $managed.Invoke($null, [object[]]@([string[]]@('skill', 'set', ($id + '/Viking'), 'Run', '10'), $caller))
    Assert-True ($offline.IsCompleted -and $offline.Result.Code -eq 'unauthorized') 'Offline mutation entry must recheck the original caller, not grant authority from target account text.'
} else {
    # JITing this dispatch method on desktop CLR4 encounters a dependency's
    # default-interface implementation before reaching the authorization guard.
    # The actual game uses Mono. Run this script with pwsh to add this execution test.
    Write-Output 'CLR4: managed offline entry checked via compiled IL; run with pwsh for its additional executable authorization test.'
}

# Inspect compiled call sites for security boundaries requiring a live Unity world.
# These are static integration checks, not a claim that native RPC execution was simulated.
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')) | Out-Null
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
$runtimeIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerManagerRuntime'
function Method-IL([string]$Name) { @($runtimeIL.Methods | Where-Object Name -eq $Name)[0] }
function Calls($Method, [string]$Name) {
    return @($Method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $Name })
}
function Has-Text($Method, [string]$Text) {
    return @($Method.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $Text }).Count -ne 0
}
$completionIL = $definition.MainModule.GetType('ServerManager.ServerManagerTerminalCommands')
$firstOptionsIL = @($completionIL.Methods | Where-Object Name -eq 'GetFirstArgumentOptions')[0]
foreach ($required in @('HasPlayerFirstArgument', 'get_isFocused', 'IsFirstArgumentCompletion', 'GetPlayerList', 'GetPlayerNameCompletions')) {
    Assert-True ((Calls $firstOptionsIL $required).Count -eq 1) "Optional completion lost its first-target/UI/replicated-name boundary: $required"
}
$completionMethods = @($completionIL.Methods | Where-Object { $_.Name -in @('GetFirstArgumentOptions', 'IsFirstArgumentCompletion', 'GetPlayerNameCompletions') })
$completionMethods += @($completionIL.NestedTypes | ForEach-Object { $_.Methods } | Where-Object { $_.Name -match 'GetFirstArgumentOptions|GetPlayerNameCompletions' })
foreach ($method in $completionMethods) {
    foreach ($instruction in $method.Body.Instructions) {
        $operand = $instruction.Operand
        if ($operand -isnot [Mono.Cecil.MethodReference]) { continue }
        Assert-True (-not $operand.DeclaringType.FullName.StartsWith('System.IO.') -and
            $operand.Name -notin @('SendAdminPacket', 'InvokeRoutedRPC', 'GetAdminCharacters', 'GetStatusSnapshot', 'TryGetServerSession', 'CaptureProfileToBytes', 'SubmitTerminalCommand', 'ExecuteAsync')) 'Tab suggestions must not issue RPCs, read disk/snapshots, or execute commands.'
    }
}
$registerCompletion = @($completionIL.Methods | Where-Object Name -eq 'EnsureRegistered')[0]
$terminalConstructors = @($registerCompletion.Body.Instructions | Where-Object {
    $_.OpCode.Name -eq 'newobj' -and $_.Operand.DeclaringType.FullName -eq 'Terminal/ConsoleCommand'
})
Assert-True ($terminalConstructors.Count -eq 1 -and $terminalConstructors[0].Previous.Previous.Previous.OpCode.Name -eq 'ldc.i4.1') 'Native command options must refresh on each request rather than retain stale player names.'
$receive = Method-IL 'OnAdminCommandPacket'
Assert-True ((Calls $receive 'TryDecode').Count -eq 1 -and (Calls $receive 'IsAdminSession').Count -ge 1) 'RPC receiver must decode and resolve an authenticated ready session.'
Assert-True ((Calls $receive 'FixedTimeEquals').Count -ge 2) 'RPC receiver must verify both session ID and nonce.'
foreach ($text in @('Clients cannot issue server player actions.', 'Invalid server administrative packet direction.', 'Stale or replayed administrative packet.')) {
    Assert-True (Has-Text $receive $text) "Missing compiled direction/replay rejection branch: $text"
}
$receivedWrites = @($receive.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'Received' })
$identityChecks = @(Calls $receive 'FixedTimeEquals')
Assert-True ($receivedWrites.Count -eq 1 -and $receivedWrites[0].Offset -gt $identityChecks[-1].Offset) 'Sequence advancement must follow identity/replay checks.'
$sessionGuard = Method-IL 'IsAdminSession'
foreach ($name in @('get_State', 'get_PeerInfoAuthenticated', 'TryResolveActiveDetectionPeer', 'IsCurrentServerAdmin')) {
    Assert-True ((Calls $sessionGuard $name).Count -gt 0) "Session guard must retain $name."
}
$executeIL = Method-IL 'ExecuteManagedAdminCommandAsync'
Assert-True ((Calls $executeIL 'get_IsAuthorized').Count -gt 0 -and (Calls $executeIL 'get_IsCancellationRequested').Count -gt 0) 'Execution must recheck current caller permission and retirement.'
$clientTick = Method-IL 'TickClientAdminAction'
$captureCall = @(Calls $clientTick 'CaptureProfileToBytes')
$ackCall = @(Calls $clientTick 'HasAcknowledgedCapture')
Assert-True ($captureCall.Count -eq 1 -and $ackCall.Count -eq 1 -and $captureCall[0].Offset -lt $ackCall[0].Offset) 'Client action must capture then wait for the specific save acknowledgement.'
$complete = Method-IL 'CompletePlayerAdminAction'
Assert-True ((Calls $complete 'TryGetServerSession').Count -ge 1 -and (Calls $complete 'get_CurrentRevision').Count -ge 1) 'Server must independently verify an accepted character revision before success.'
Assert-True ((Calls $complete 'get_IsAuthorized').Count -gt 0 -and (Calls $complete 'IsAdminSession').Count -gt 0) 'Incoming action completion must recheck the current caller and target session, not race the next tick.'
$clientActionIL = $runtimeIL.NestedTypes | Where-Object Name -eq 'ClientAdminAction'
foreach ($pinned in @('Player', 'Profile', 'Character')) {
    Assert-True (@($clientActionIL.Fields | Where-Object Name -eq $pinned).Count -eq 1) "Client action must pin its original $pinned."
    Assert-True (@($clientTick.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq $pinned }).Count -gt 0) "Client completion must validate its original $pinned before capture."
}
$referenceBranches = @($clientTick.Body.Instructions | Where-Object {
    $_.Offset -lt $captureCall[0].Offset -and $_.OpCode.Name -like 'bne.un*'
})
Assert-True ($referenceBranches.Count -ge 4) 'Client action must compare connection, player, profile, and character identity references before capture.'
$cleanup = Method-IL 'ShutdownAdminCommands'
Assert-True ((Calls $cleanup 'TrySetResult').Count -ge 2) 'Shutdown must settle pending remote and host actions.'
$shout = Method-IL 'TryBroadcastServerShout'
foreach ($name in @('IsServer', 'get_WorldReady', 'IsAdminSession', 'SendAdminPacket', 'ShowServerShout')) {
    Assert-True ((Calls $shout $name).Count -ge 1) "Shared shout is missing the server-only validated broadcast boundary: $name."
}
$discordShout = Method-IL 'TryBroadcastDiscordShout'
$adminShout = Method-IL 'ExecuteAdminShout'
foreach ($entry in @($discordShout, $adminShout)) {
    Assert-True ((Calls $entry 'TryBroadcastServerShout').Count -eq 1) 'Both Discord text and sm:chat must use the same global shout broadcast.'
    Assert-True ((Calls $entry 'SendAdminPacket').Count -eq 0 -and (Calls $entry 'ShowServerShout').Count -eq 0) 'Entry points must not keep a separate delivery loop.'
    Assert-True ((Calls $entry 'TryFindAdminTarget').Count -eq 0) 'Global shout must not resolve a private recipient.'
    Assert-True (Has-Text $entry '[Discord] Admin') 'Both administrative shout paths must use the same display-only Admin label.'
}
Assert-True ($discordShout.Parameters.Count -eq 4 -and $discordShout.Parameters[3].ParameterType.FullName -eq 'System.Boolean') 'Discord chat must pass channel classification separately from the real author.'
Assert-True (Has-Text $discordShout '[Discord] ') 'Public-channel chat must keep the real Discord display name.'
Assert-True ((Calls $adminShout 'get_Name').Count -eq 0) 'Discord /chat must use the fixed Admin display title, not expose the caller name as its title.'
Assert-True ((Calls $discordShout 'RecordDiscordShout').Count -eq 1) 'Ordinary Discord shout must retain local-only logging.'
$recordShoutCall = (Calls $discordShout 'RecordDiscordShout')[0]
Assert-True ($recordShoutCall.Previous.OpCode.Name -eq 'ldarg.2' -and
    $recordShoutCall.Previous.Previous.OpCode.Name -eq 'ldarg.1' -and
    $recordShoutCall.Previous.Previous.Previous.OpCode.Name -eq 'ldarg.0') 'Log identity must receive original user ID/name/message, never the Admin display alias.'
foreach ($entry in @($shout, $discordShout, $adminShout)) {
    foreach ($name in @('ExecuteAsync', 'ExecuteManagedAdminCommandAsync', 'TryRunCommand', 'SendText', 'Instantiate', 'InvokeRoutedRPC')) {
        Assert-True ((Calls $entry $name).Count -eq 0) "Shout text must not become a command, fake player or ordinary routed player chat: $name."
    }
}
Assert-True ((Calls $receive 'ShowServerShout').Count -eq 1) 'The authenticated client receiver must handle server shout display.'
foreach ($removed in @('ExecuteAdminMessage', 'ShowAdminChat', 'ShowServerChat')) {
    Assert-True (@($runtimeIL.Methods | Where-Object Name -eq $removed).Count -eq 0) "The removed targeted/multi-style path must not survive: $removed."
}
$eventRuntimeIL = $definition.MainModule.GetType('ServerManager.Events.ServerEventRuntime')
$discordLogIL = @($eventRuntimeIL.Methods | Where-Object Name -eq 'RecordDiscordShout')[0]
Assert-True ((Calls $discordLogIL 'TryWrite').Count -eq 1) 'Discord-origin shout must use the local event log.'
Assert-True ((Calls $discordLogIL 'Publish').Count -eq 0) 'Discord-origin shout must never fan out to subscribers/webhooks.'
Assert-True ((Has-Text $discordLogIL ' (ID: ') -and (Has-Text $discordLogIL 'discord:') -and
    -not (Has-Text $discordLogIL '[Discord] Admin')) 'The persisted chat message must include the real Discord ID, not the display-only Admin alias.'
$installedGame = [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot 'assembly_valheim.dll'))
$terminalType = $installedGame.GetType('Terminal', $true)
$talkerKind = $installedGame.GetType('Talker+Type', $true)
$displayMethod = $terminalType.GetMethod('AddString', [Reflection.BindingFlags]'Public,Instance', $null,
    [Type[]]@([string], [string], $talkerKind, [bool]), $null)
Assert-True ($null -ne $displayMethod -and [Enum]::IsDefined($talkerKind, 'Shout')) 'Native title-based shout display must exist without requiring a player/platform identity.'
$chatType = $installedGame.GetType('Chat', $true)
$hideTimer = $chatType.GetField('m_hideTimer', [Reflection.BindingFlags]'NonPublic,Instance')
Assert-True ($null -ne $hideTimer -and $hideTimer.FieldType -eq [single]) 'Native chat visibility timer must remain accessible through reflection.'
$showChat = Method-IL 'ShowServerShout'
Assert-True ($showChat.Parameters.Count -eq 2) 'Server display must accept only title/text, not a selectable normal/private chat style.'
$boxedStyle = @($showChat.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'box' -and $_.Operand.FullName -eq 'Talker/Type' })
$shoutValue = [int][Enum]::Parse($talkerKind, 'Shout')
Assert-True ($boxedStyle.Count -eq 1 -and $boxedStyle[0].Previous.OpCode.Name -eq "ldc.i4.$shoutValue") 'The one server display style must be the native Shout enum value.'
Assert-True (Has-Text $showChat 'm_hideTimer') 'Incoming server chat must reset visibility, not only append to a hidden buffer.'
Assert-True ((Calls $showChat 'ActivateInputField').Count -eq 0 -and (Calls $showChat 'AddInworldText').Count -eq 0) 'Discord shout must not steal input focus or manufacture world text.'
$definition.Dispose()
Write-Output 'Admin transport codec, malformed packets, capture ACKs, target identity, and compiled guard smoke tests passed.'
