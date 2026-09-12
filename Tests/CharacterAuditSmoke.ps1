param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Observations {
    param(
        [object[]]$Findings,
        [string]$Account = 'steamworks:76561198000000001',
        [string]$Character = 'Audit Viking',
        [long]$Revision = 2,
        [string]$Source = 'incoming'
    )
    $arguments = [object[]]::new(5)
    $arguments[0] = $Account
    $arguments[1] = $Character
    $arguments[2] = $Revision
    $arguments[3] = $Source
    if ($null -ne $Findings) {
        $typed = [Array]::CreateInstance($script:observationType, $Findings.Count)
        for ($index = 0; $index -lt $Findings.Count; ++$index) {
            $value = $Findings[$index]
            if ($value -is [string]) {
                # Convenience for the existing literal fixtures only. Production
                # receives metadata directly from the semantic evaluator.
                $category = 'ValidationObserved'; $code = 'stored_policy_violation'
                if ($value -match '^\[admin_bypass:(forbidden_prefab)') {
                    $category = 'AdminBypass'; $code = $Matches[1]
                } elseif ($value -match '^\[(skill_gain|skill_accumulator):|^\[used_cheats\]') {
                    $category = 'RevisionObserved'; $code = $Matches[1]
                    if ($value -match '^\[used_cheats\]') { $code = 'used_cheats' }
                } elseif ($value -notmatch '^\[would_reject:') { continue }
                $key = $value.Substring(0, $value.IndexOf(']') + 1)
                $value = New-AuditObservation $category $code $key $value
            }
            $typed.SetValue($value, $index)
        }
        $arguments[4] = $typed
    }
    $script:record.Invoke($null, $arguments) | Out-Null
}

function New-AuditObservation {
    param([string]$Category, [string]$Code, [string]$Key, [string]$Detail)
    return $script:observationConstructor.Invoke([object[]]@(
        [Enum]::Parse($script:observationKind, $Category), $Code, $Key, $Detail))
}

function Reset-TestQueue {
    param([int]$Capacity = 64)
    $script:queue = $script:queueType.GetConstructor([Type[]]@([int])).Invoke(
        [object[]]@($Capacity))
    $script:queueField.SetValue($script:writer, $script:queue)
    $script:writerType.GetField('_dropped', $script:instanceFlags).
        SetValue($script:writer, 0)
    $script:clearAudit.Invoke($null, $null) | Out-Null
}

function Invoke-SecurityAudit {
    param(
        [string]$Kind = 'security.detection',
        [string]$Account = 'steamworks:76561198000000001',
        [string]$Character = 'Audit Viking',
        [string]$Source = 'snapshot_received',
        [string]$Evidence = 'MaximumHealthExceeded',
        [string]$Response = 'Kick',
        [string]$Outcome = 'accepted',
        [string]$Detail = 'maximum health 2000 exceeds 1000'
    )
    $arguments = [object[]]@(
        $Kind, $Account, $Character, $Source, $Evidence,
        [Enum]::Parse($script:responseType, $Response), $Outcome, $Detail)
    $script:recordSecurity.Invoke($null, $arguments) | Out-Null
}

function Invoke-ConnectionAudit {
    param(
        [string]$Account = 'steamworks:76561198000000001',
        [string]$Character = 'Audit Viking',
        [string]$Category = 'mod_policy',
        [string]$Stage = 'Challenged',
        [string]$Reason = 'validation.hash_not_allowed',
        [string]$Source = 'server_observed',
        [string]$IdentityStatus = 'authenticated',
        [string]$Detail = 'ExampleMod expected SHA A; received SHA B',
        [string]$Plugins = 'ExampleMod (example.mod): hash_not_allowed'
    )
    $script:recordConnection.Invoke($null, [object[]]@($Account, $Character, $Category,
        $Stage, $Reason, $Source, $IdentityStatus, $Detail, $Plugins)) | Out-Null
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginPath = Join-Path $projectRoot "bin\$Configuration\ServerManager.dll"
$managedRoot = Join-Path $GamePath 'valheim_Data\Managed'
Assert-True (Test-Path -LiteralPath $pluginPath) 'Build ServerManager before this smoke test.'
foreach ($name in @(
    'UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll',
    'UnityEngine.dll', 'assembly_utils.dll', 'SoftReferenceableAssets.dll',
    'com.rlabrecque.steamworks.net.dll', 'Splatform.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $name)) | Out-Null
}
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes(
        (Join-Path (Split-Path -Parent $pluginPath) $name))) | Out-Null
}
[Reflection.Assembly]::LoadFrom((Join-Path $managedRoot 'assembly_valheim.dll')) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom($pluginPath)
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic'
$runtimeType = $assembly.GetType('ServerManager.Events.ServerEventRuntime', $true)
$writerType = $assembly.GetType('ServerManager.Events.EventLogWriter', $true)
$eventType = $assembly.GetType('ServerManager.Events.ServerManagerEvent', $true)
$writer = $runtimeType.GetField('LogWriter', $staticFlags).GetValue($null)
$queueField = $writerType.GetField('_queue', $instanceFlags)
$queueType = [Collections.Concurrent.BlockingCollection``1].MakeGenericType($eventType)
$dedupe = $runtimeType.GetField('CharacterObservationTimes', $staticFlags).GetValue($null)
$clearAudit = $runtimeType.GetMethod('ClearCharacterAuditState', $staticFlags)
$record = $runtimeType.GetMethod('RecordCharacterObservations', $staticFlags)
$recordShadow = $runtimeType.GetMethod('RecordCharacterShadowWarning', $staticFlags)
$recordSecurity = $runtimeType.GetMethod('RecordSecurityEvent', $staticFlags)
$recordConnection = $runtimeType.GetMethod('RecordConnectionRejected', $staticFlags)
$responseType = $assembly.GetType('ServerManager.DetectionAction', $true)
$observationType = $assembly.GetType('ServerManager.CharacterAuditObservation', $true)
$observationKind = $assembly.GetType('ServerManager.CharacterAuditObservationKind', $true)
$observationConstructor = $observationType.GetConstructors($instanceFlags)[0]
Assert-True ($null -ne $record -and $null -ne $recordShadow -and
    $null -ne $recordSecurity -and $recordSecurity.GetParameters().Count -eq 8) `
    'Character/security audit entry points are missing.'
Assert-True ($recordConnection.GetParameters().Count -eq 9) 'Connection rejection audit contract is missing.'
$originalFlags = @{}
foreach ($fieldName in @('_initialized', '_serverStarted', '_shutdownStarted')) {
    $field = $runtimeType.GetField($fieldName, $staticFlags)
    $originalFlags[$fieldName] = $field.GetValue($null)
    $field.SetValue($null, $fieldName -ne '_shutdownStarted')
}
$originalQueue = $queueField.GetValue($writer)
Assert-True ($null -eq $originalQueue) 'Run the smoke test in an isolated process.'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ServerManager-character-audit-' + [Guid]::NewGuid().ToString('N'))
$temporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
$temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') +
    [IO.Path]::DirectorySeparatorChar + 'ServerManager-character-audit-'
Assert-True ($temporaryRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) `
    'Temporary audit path escaped the intended test directory.'

try {
    Reset-TestQueue
    Invoke-ConnectionAudit
    $connection = $queue.ToArray()[0]
    Assert-True ($connection.Kind -eq 'connection.rejected' -and $connection.Reliability -eq 'authoritative' -and
        $connection.Fields['identity_status'] -eq 'authenticated' -and
        $connection.Fields['reason_code'] -eq 'validation.hash_not_allowed' -and
        $connection.Fields['plugin_details'].Contains('ExampleMod') -and
        $connection.Fields['message'].Contains('expected SHA A; received SHA B')) 'Connection audit lost the actual decision or bounded mismatch evidence.'
    Invoke-ConnectionAudit -IdentityStatus 'unverified'
    Assert-True ($queue.ToArray()[1].Actor.Kind -eq 'unverified_peer') 'Pre-authentication identity was represented as authenticated.'
    Invoke-ConnectionAudit -IdentityStatus 'unavailable'
    Assert-True ($null -eq $queue.ToArray()[2].Actor -and $queue.ToArray()[2].Fields['account_id'] -eq '' -and
        $queue.ToArray()[2].Fields['character'] -eq '') 'An unidentified connection retained a fabricated identity.'
    Invoke-ConnectionAudit -IdentityStatus 'unverified' -Account 'attacker-chosen'
    Assert-True ($queue.ToArray()[3].Fields['identity_status'] -eq 'unavailable') 'Malformed unverified identity was not downgraded.'
    Invoke-ConnectionAudit -Category 'character' -Stage 'CharacterSent' -Reason 'fresh_local_character_required' -Source 'client_reported'
    Assert-True ($queue.ToArray()[4].Reliability -eq 'client_reported' -and
        $queue.ToArray()[4].Fields['outcome'] -eq 'client_aborted') 'A client-reported guard was falsely labelled as a server-verified finding.'
    Invoke-ConnectionAudit
    Assert-True ($queue.Count -eq 6 -and $dedupe.Count -eq 0) 'Separate reconnect attempts were merged by the audit sink.'
    Invoke-ConnectionAudit -Account 'attacker-chosen'
    Invoke-ConnectionAudit -Category 'unknown'
    Invoke-ConnectionAudit -Source 'client_claimed'
    Invoke-ConnectionAudit -IdentityStatus 'trusted'
    Assert-True ($queue.Count -eq 6) 'Invalid connection provenance entered the audit stream.'
    Invoke-ConnectionAudit -Reason 'forged secret reason' -Stage 'C:\secret\file' -Detail ("line`n" + ('x' * 5000))
    $boundedConnection = $queue.ToArray()[6]
    Assert-True ($boundedConnection.Fields['reason_code'] -eq 'connection_rejected' -and
        $boundedConnection.Fields['stage'] -eq 'unknown' -and $boundedConnection.Fields['detail'].Length -le 4096 -and
        $boundedConnection.Fields['message'] -notmatch '[\r\n\t]') 'Connection audit retained unbounded/unsafe diagnostic metadata.'
    foreach ($inactive in @('_initialized', '_serverStarted', '_shutdownStarted')) {
        $field = $runtimeType.GetField($inactive, $staticFlags)
        $field.SetValue($null, $inactive -eq '_shutdownStarted')
        Invoke-ConnectionAudit
        Assert-True ($queue.Count -eq 7) 'Inactive connection diagnostics were admitted.'
        $field.SetValue($null, $inactive -ne '_shutdownStarted')
    }
    Reset-TestQueue
    Invoke-Observations @(
        '[item_gain:Wood] Wood increased by 201',
        '[item_total_gain] positive inventory gains totalled 501')
    Assert-True ($queue.Count -eq 0 -and $dedupe.Count -eq 0) `
        'Removed item-delta finding codes still create audit records or consume cooldown.'
    $findings = @(
        '[would_reject:forbidden_item] forbidden item prefab found',
        '[skill_gain:1] skill 1 gained 10 levels',
        '[skill_accumulator:2] accumulator outside vanilla envelope',
        '[unknown] not an existing semantic observation',
        'unstructured diagnostic')
    Invoke-Observations $findings
    $events = $queue.ToArray()
    Assert-True ($events.Count -eq 3) 'Known findings were lost or unknown findings misclassified.'
    Assert-True (@($events | Where-Object Kind -eq 'character.validation_observed').Count -eq 1 -and
        @($events | Where-Object Kind -eq 'character.revision_observed').Count -eq 2) `
        'Absolute Observe findings and transition anomalies have the wrong audit kinds.'
    foreach ($event in $events) {
        Assert-True ($event.Reliability -eq 'observed' -and
            $event.Fields['account_id'] -eq 'steamworks:76561198000000001' -and
            $event.Fields['character'] -eq 'Audit Viking' -and
            $event.Fields['revision'] -eq '2' -and $event.Fields['source'] -eq 'incoming') `
            'Audit fields lost the authenticated identity, revision, source, or observed reliability.'
    }
    Invoke-Observations @(
        '[skill_gain:1] skill 1 gained 20 levels',
        '[skill_accumulator:2] changed accumulator magnitude') -Revision 3
    Assert-True ($queue.Count -eq 3) 'Changing skill amounts/revisions bypassed stable-code cooldown.'
    Invoke-Observations @($findings[1]) -Account 'steamworks:76561198000000002'
    Invoke-Observations @($findings[1]) -Character 'Other Viking'
    Invoke-Observations @($findings[1]) -Source 'stored_host'
    Assert-True ($queue.Count -eq 6) 'Dedupe incorrectly merged distinct identities or sources.'

    $cooldown = [long]$runtimeType.GetField('CharacterObservationCooldownTicks', $staticFlags).
        GetValue($null)
    Assert-True ($cooldown -eq 300L * [Diagnostics.Stopwatch]::Frequency) `
        'The semantic audit cooldown is not five minutes.'
    foreach ($key in @($dedupe.Keys)) { $dedupe[$key] = [Diagnostics.Stopwatch]::GetTimestamp() - $cooldown }
    Invoke-Observations @($findings[1]) -Revision 4
    Assert-True ($queue.Count -eq 7) 'An expired skill observation cooldown did not permit a new record.'

    Reset-TestQueue
    foreach ($source in @('stored', 'stored_host', 'incoming', 'incoming_host')) {
        Invoke-Observations @($findings[1], $findings[2]) -Source $source
    }
    Assert-True ($queue.Count -eq 8 -and
        @($queue.ToArray() | Where-Object Kind -eq 'character.revision_observed').Count -eq 8) `
        'Skill observations lost a stored/incoming remote/host audit path.'

    Reset-TestQueue
    Invoke-Observations @('[admin_bypass:forbidden_prefab:SwordCheat] administrator retained item')
    Invoke-Observations @('[admin_bypass:forbidden_prefab:SwordCheat] changed count') -Revision 3
    $adminEvents = @($queue.ToArray())
    Assert-True ($adminEvents.Count -eq 1 -and
        $adminEvents[0].Kind -eq 'security.admin_bypass' -and
        $adminEvents[0].Fields['source'] -eq 'incoming') `
        'Admin forbidden-prefab exception lost its explicit local audit or stable cooldown.'
    Invoke-Observations @('[admin_bypass:forbidden_prefab:SwordCheat] host exception') -Source 'incoming_host'
    Assert-True ($queue.Count -eq 2) 'The host admin bypass was not independently audited.'
    Reset-TestQueue
    Invoke-Observations @('[used_cheats] character retains achievement marker')
    Invoke-Observations @('[used_cheats] repeated save') -Revision 3
    Invoke-Observations @('[used_cheats] host usage flag') -Source 'incoming_host'
    Assert-True ($queue.Count -eq 2 -and
        @($queue.ToArray() | Where-Object Kind -eq 'character.revision_observed').Count -eq 2 -and
        @($queue.ToArray() | Where-Object { $_.Fields['reason_code'] -eq 'used_cheats' }).Count -eq 2) `
        'Achievement metadata observations lost remote/host audit routing or stable cooldown.'
    Reset-TestQueue
    Invoke-Observations @((New-AuditObservation 'RevisionObserved' 'used_cheats' '[used_cheats]' 'A new sentence without any classification prefix.'))
    Invoke-Observations @((New-AuditObservation 'RevisionObserved' 'used_cheats' '[used_cheats]' '[skill_gain:99] misleading display prefix')) -Revision 7
    Assert-True ($queue.Count -eq 1 -and $queue.ToArray()[0].Kind -eq 'character.revision_observed' -and
        $queue.ToArray()[0].Fields['reason_code'] -eq 'used_cheats' -and
        $queue.ToArray()[0].Fields['finding'] -eq 'A new sentence without any classification prefix.') `
        'Observation classification or stable dedupe still depends on display wording.'
    Reset-TestQueue
    Invoke-SecurityAudit -Kind 'security.admin_bypass' -Source 'client_reported' `
        -Evidence 'validation.hash_not_allowed' -Response 'Log' -Outcome 'mod_policy_bypassed'
    Invoke-SecurityAudit -Kind 'security.admin_bypass' -Source 'client_reported' `
        -Evidence 'validation.hash_not_allowed' -Response 'Log' -Outcome 'mod_policy_bypassed'
    Assert-True ($queue.Count -eq 2 -and
        $queue.ToArray()[0].Reliability -eq 'client_reported') `
        'Admin mod exceptions were hidden across separate admissions or misrepresented as verified DLL evidence.'

    Reset-TestQueue
    $capacity = [int]$runtimeType.GetField('MaximumCharacterObservationKeys', $staticFlags).
        GetRawConstantValue()
    for ($index = 0; $index -lt $capacity; ++$index) { $dedupe.Add("seed-$index", [long]$index) }
    Invoke-Observations @($findings[0])
    Assert-True ($dedupe.Count -eq $capacity -and -not $dedupe.ContainsKey('seed-0')) `
        'The observation dictionary is unbounded or does not evict its oldest entry.'

    Reset-TestQueue
    $unsafe = '[would_reject:forbidden_item] tab' + "`tline`n" + [char]0x202E +
        [char]0x2028 + [char]0xD800 + ' "forged" \ ' + ('x' * 2000)
    Invoke-Observations @($unsafe) -Character "Audit`nViking"
    $sanitized = $queue.ToArray()[0]
    Assert-True ($sanitized.Fields['finding'].Length -le 512 -and
        $sanitized.Fields['message'].Length -lt 1000 -and
        $sanitized.Fields['finding'] -notmatch '[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]' -and
        $sanitized.Fields['finding'] -notmatch '["\\]' -and
        $sanitized.Actor.Name -notmatch '[\r\n\t]') 'Audit text is not bounded and single-line safe.'
    Invoke-Observations @($findings[0]) -Account ''
    Invoke-Observations @($findings[0]) -Revision 0
    Invoke-Observations @($findings[0]) -Source 'client_claimed'
    Invoke-Observations $null
    Assert-True ($queue.Count -eq 1) 'Incomplete identity or invalid provenance produced an audit record.'

    Reset-TestQueue -Capacity 1
    $queue.TryAdd($sanitized) | Out-Null
    Invoke-Observations @($findings[0])
    Invoke-SecurityAudit
    Invoke-SecurityAudit -Kind 'security.response'
    Invoke-SecurityAudit -Kind 'character.save_rejected'
    Assert-True ($dedupe.Count -eq 0) 'A full/failed queue consumed the observation cooldown.'
    $queueField.SetValue($writer, $null)
    Invoke-Observations @($findings[0])
    Invoke-SecurityAudit
    Assert-True ($dedupe.Count -eq 0) 'A stopped writer consumed the observation cooldown.'
    Reset-TestQueue
    $queue.Dispose()
    Invoke-SecurityAudit
    Assert-True ($dedupe.Count -eq 0) 'A disposed writer escaped or consumed the security cooldown.'
    Reset-TestQueue
    foreach ($inactive in @('_initialized', '_serverStarted', '_shutdownStarted')) {
        $field = $runtimeType.GetField($inactive, $staticFlags)
        $field.SetValue($null, $inactive -eq '_shutdownStarted')
        Invoke-Observations @($findings[0])
        Invoke-SecurityAudit
        Assert-True ($queue.Count -eq 0 -and $dedupe.Count -eq 0) `
            "Inactive lifecycle state $inactive admitted a record."
        $field.SetValue($null, $inactive -ne '_shutdownStarted')
    }

    $identityType = $assembly.GetType('ServerManager.CharacterIdentity', $true)
    $identity = $identityType.GetConstructors()[0].Invoke([object[]]@(
        'steamworks:76561198000000001', 'Audit Viking'))
    $snapshotType = $assembly.GetType('ServerManager.CharacterSessionSaveHealthSnapshot', $true)
    $now = [Diagnostics.Stopwatch]::GetTimestamp()
    $old = $now - 600L * [Diagnostics.Stopwatch]::Frequency
    $snapshot = $snapshotType.GetConstructors($instanceFlags)[0].Invoke([object[]]@(
        $identity, [Guid]::NewGuid(), [long]7, $now, $old, $null, $null, $null,
        [long]3, [int]2, "failed`nrequest", $null, $true))
    $recordShadow.Invoke($null, [object[]]@($snapshot, 'incoming_host')) | Out-Null
    $shadow = $queue.ToArray()[0]
    Assert-True ($shadow.Kind -eq 'character.shadow_stalled' -and
        $shadow.Fields['revision'] -eq '7' -and $shadow.Fields['source'] -eq 'incoming_host' -and
        $shadow.Fields['finding'] -like '*age_seconds=600.0*' -and
        $shadow.Fields['finding'] -like '*accepted_shadows=3*' -and $dedupe.Count -eq 0) `
        'RAM-shadow warning fields changed or the audit sink took over health-warning dedupe.'

    Reset-TestQueue
    Invoke-SecurityAudit
    $security = $queue.ToArray()[0]
    Assert-True ($security.Kind -eq 'security.detection' -and
        $security.Reliability -eq 'observed' -and
        $security.Actor.Id -eq 'steamworks:76561198000000001' -and
        $security.Fields['account_id'] -eq $security.Actor.Id -and
        $security.Fields['character'] -eq 'Audit Viking' -and
        $security.Fields['source'] -eq 'snapshot_received' -and
        $security.Fields['evidence'] -eq 'MaximumHealthExceeded' -and
        $security.Fields['response'] -eq 'Kick' -and
        $security.Fields['outcome'] -eq 'accepted' -and
        $security.Fields['message'] -notmatch 'would_reject') `
        'Accepted cap evidence must retain its actual outcome independently of the configured response.'
    for ($index = 0; $index -lt 100; ++$index) {
        Invoke-SecurityAudit -Detail "maximum health $index; changing diagnostic"
    }
    Assert-True ($queue.Count -eq 1 -and $dedupe.Count -eq 1) `
        'Changing magnitudes/details bypassed the security cooldown.'
    Invoke-SecurityAudit -Kind 'security.response'
    Invoke-SecurityAudit -Account 'steamworks:76561198000000002'
    Invoke-SecurityAudit -Character 'Other Viking'
    Invoke-SecurityAudit -Source 'client_reported'
    Invoke-SecurityAudit -Evidence 'MaximumStaminaExceeded'
    Invoke-SecurityAudit -Response 'Log'
    Invoke-SecurityAudit -Outcome 'observed'
    Assert-True ($queue.Count -eq 8 -and $dedupe.Count -eq 7) `
        'Security dedupe merged distinct kind/identity/source/evidence/response/outcome keys.'
    Assert-True (@($queue.ToArray() | Where-Object Reliability -eq 'client_reported').Count -eq 1) `
        'Client-reported detection was falsely labelled server-observed.'
    foreach ($key in @($dedupe.Keys)) { $dedupe[$key] = [Diagnostics.Stopwatch]::GetTimestamp() - $cooldown }
    Invoke-SecurityAudit
    Assert-True ($queue.Count -eq 9) 'An expired security cooldown did not admit a fresh record.'

    Reset-TestQueue
    foreach ($outcome in @('response_requested', 'banlist_add_returned', 'banlist_add_failed',
        'kick_call_returned', 'disconnect_fallback_scheduled')) {
        Invoke-SecurityAudit -Kind 'security.response' -Response 'Ban' -Outcome $outcome
    }
    Assert-True ($queue.Count -eq 5 -and $dedupe.Count -eq 0 -and
        @($queue.ToArray() | Where-Object { $_.Fields['response'] -eq 'Ban' }).Count -eq 5 -and
        @($queue.ToArray() | Where-Object { $_.Fields['outcome'] -eq 'banlist_add_failed' }).Count -eq 1 -and
        @($queue.ToArray() | Where-Object { $_.Fields['outcome'] -match 'persisted|confirmed|banned$' }).Count -eq 0) `
        'Requested action hid its terminal failure/success outcome or a failed ban was relabelled as successful.'
    Invoke-SecurityAudit -Kind 'security.response' -Response 'Ban' -Outcome 'kick_call_returned'
    Invoke-SecurityAudit -Kind 'character.save_rejected' -Outcome 'save_rejected'
    Invoke-SecurityAudit -Kind 'character.save_rejected' -Outcome 'save_rejected'
    Assert-True ($queue.Count -eq 8 -and $dedupe.Count -eq 0 -and
        @($queue.ToArray() | Where-Object Kind -eq 'character.save_rejected').Count -eq 2) `
        'A reconnect with the same identity/outcome lost its distinct terminal response or rejected-save record.'

    Reset-TestQueue
    foreach ($source in @('client_reported', 'snapshot_received', 'server_observed',
        'stored', 'stored_host', 'incoming', 'incoming_host')) {
        Invoke-SecurityAudit -Source $source
    }
    Assert-True ($queue.Count -eq 7) 'A defined security provenance value was dropped.'
    foreach ($badAccount in @('', '76561198000000001', 'playfab:123', 'steamworks:0',
        'steamworks:076561198000000001', "steamworks:76561198000000001`n")) {
        Invoke-SecurityAudit -Account $badAccount
    }
    Invoke-SecurityAudit -Kind 'player.login'
    Invoke-SecurityAudit -Source 'client_claimed'
    Invoke-SecurityAudit -Evidence ''
    Invoke-SecurityAudit -Outcome ''
    Invoke-SecurityAudit -Response '999'
    Assert-True ($queue.Count -eq 7) 'Invalid identity/kind/provenance/action entered the private security audit.'
    Invoke-SecurityAudit -Character ''
    Assert-True ($queue.Count -eq 8 -and $queue.ToArray()[7].Actor.Name -eq '') `
        'An authenticated pre-character account cannot retain security evidence with an unknown character name.'

    Reset-TestQueue
    $unsafeSecurity = "tab`tline`n" + [char]0x202E + [char]0x2028 +
        [char]0xD800 + ' "forged" \ ' + ('x' * 2000)
    Invoke-SecurityAudit -Character $unsafeSecurity -Evidence $unsafeSecurity `
        -Outcome $unsafeSecurity -Detail $unsafeSecurity
    $safeSecurity = $queue.ToArray()[0]
    foreach ($fieldName in @('character', 'evidence', 'outcome', 'detail', 'message')) {
        Assert-True ($safeSecurity.Fields[$fieldName] -notmatch '[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]') `
            "Security field $fieldName retained log-forging characters."
    }
    Assert-True ($safeSecurity.Fields['character'].Length -le 96 -and
        $safeSecurity.Fields['evidence'].Length -le 96 -and
        $safeSecurity.Fields['outcome'].Length -le 64 -and
        $safeSecurity.Fields['detail'].Length -le 384 -and
        $safeSecurity.Fields['message'].Length -lt 1000 -and
        $safeSecurity.Fields['detail'] -notmatch '["\\]') `
        'Security diagnostics exceeded the existing writer budget or retained unsafe delimiters.'
    Reset-TestQueue
    for ($index = 0; $index -lt $capacity; ++$index) { $dedupe.Add("seed-$index", [long]$index) }
    Invoke-SecurityAudit
    Assert-True ($dedupe.Count -eq $capacity -and -not $dedupe.ContainsKey('seed-0')) `
        'Security evidence bypassed the shared bounded dedupe capacity.'

    # A genuine subscriber and active bounded dispatch queue expose accidental
    # routing. Keep the worker unstarted: no real Discord/UI/Unity host needed.
    $integrationType = $assembly.GetType('ServerManager.Events.ServerManagerIntegrationApi', $true)
    $dispatchQueueField = $integrationType.GetField('_dispatchQueue', $staticFlags)
    $dispatchItemType = $integrationType.GetNestedType('EventDispatchItem', $instanceFlags)
    $dispatchQueueType = [Collections.Concurrent.BlockingCollection``1].MakeGenericType($dispatchItemType)
    $dispatchQueue = $dispatchQueueType.GetConstructor([Type[]]@([int])).Invoke([object[]]@(16))
    $publishedEvent = $integrationType.GetEvent('EventPublished')
    $handler = [System.Management.Automation.LanguagePrimitives]::ConvertTo(
        [scriptblock]{ param($sender, $arguments) }, $publishedEvent.EventHandlerType)
    $originalDispatchQueue = $dispatchQueueField.GetValue($null)
    Assert-True ($null -eq $originalDispatchQueue) 'Run subscriber isolation in a fresh process.'
    $dispatchQueueField.SetValue($null, $dispatchQueue)
    $publishedEvent.AddEventHandler($null, $handler)
    try {
        Reset-TestQueue
        $publicPublish = $integrationType.GetMethod('Publish', $staticFlags)
        $runtimePublish = $runtimeType.GetMethod('Publish', $staticFlags)
        foreach ($kind in @('security.detection', 'security.response', 'character.save_rejected', 'security.admin_bypass')) {
            Invoke-SecurityAudit -Kind $kind
            $localEvent = $queue.ToArray()[-1]
            $publicPublish.Invoke($null, [object[]]@($localEvent)) | Out-Null
            $fields = [Collections.Generic.Dictionary[string,string]]::new()
            $fields.Add('message', 'private security evidence')
            $runtimePublish.Invoke($null, [object[]]@(
                $kind, 'observed', $localEvent.Actor, $null, $fields, $null)) | Out-Null
        }
        Assert-True ($queue.Count -eq 8 -and $dispatchQueue.Count -eq 0) `
            'Security kinds escaped through direct runtime/integration Publish or failed local audit routing.'
        Invoke-ConnectionAudit
        $publicPublish.Invoke($null, [object[]]@($queue.ToArray()[-1])) | Out-Null
        Assert-True ($dispatchQueue.Count -eq 0) 'Connection diagnostics escaped through the public integration API.'
        $ordinaryEvent = $eventType.GetConstructors($instanceFlags)[0].Invoke([object[]]@(
            'public-control', [DateTime]::UtcNow, 'test-server', 'player.login',
            'observed', $null, $null, $null))
        $publicPublish.Invoke($null, [object[]]@($ordinaryEvent)) | Out-Null
        Assert-True ($dispatchQueue.Count -eq 1) `
            'The isolation probe did not have a working subscriber queue, or ordinary public events were suppressed.'
    }
    finally {
        $publishedEvent.RemoveEventHandler($null, $handler)
        $dispatchQueueField.SetValue($null, $originalDispatchQueue)
        $dispatchQueue.Dispose()
    }

    # Connect the actual summary router to an unstarted session: no worker,
    # sockets, Gateway or HTTP calls. This verifies both sink-failure directions.
    $discordRuntimeType = $assembly.GetType('ServerManager.Discord.DiscordRuntime', $true)
    $discordSessionType = $discordRuntimeType.GetNestedType('Session', $instanceFlags)
    $sessionField = $discordRuntimeType.GetField('_session', $staticFlags)
    $originalDiscordSession = $sessionField.GetValue($null)
    $settingsType = $assembly.GetType('ServerManager.Discord.DiscordSettings', $true)
    $routeType = $assembly.GetType('ServerManager.Discord.DiscordWebhookRoute', $true)
    $webhookType = $assembly.GetType('ServerManager.Discord.DiscordWebhooks', $true)
    $httpType = $assembly.GetType('ServerManager.Discord.DiscordHttp', $true)
    $settings = [Activator]::CreateInstance($settingsType, $true)
    $route = [Activator]::CreateInstance($routeType, $true)
    $routeType.GetProperty('Name').SetValue($route, 'Offline operator smoke')
    $routeType.GetProperty('Url').SetValue($route, 'https://discord.com/api/webhooks/123/offline_Test-token1')
    $routeEvents = $routeType.GetProperty('Events').GetValue($route)
    $eventFilter = $settingsType.GetMethod('GetWebhookEventFilter', $staticFlags)
    $selectableEvents = $settingsType.GetField('PublicEvents', $staticFlags).GetValue($null)
    Assert-True ($null -ne $eventFilter -and $selectableEvents.Count -eq 16) 'Grouped webhook selector catalog is missing.'
    foreach ($kind in @('security.detection', 'security.response', 'security.admin_bypass', 'character.save_rejected',
        'character.shadow_stalled', 'character.validation_observed', 'character.revision_observed', 'connection.rejected')) {
        $expectedFilter = switch ($kind) {
            'security.detection' { 'security.alert' }
            'security.response' { 'security.alert' }
            'character.save_rejected' { 'character.validation' }
            'character.validation_observed' { 'character.validation' }
            default { $kind }
        }
        $filter = $eventFilter.Invoke($null, [object[]]@($kind))
        Assert-True ($filter -ceq $expectedFilter -and $selectableEvents.Contains($filter)) `
            'An internal audit event lost its selectable grouped webhook route.'
        $routeEvents.Add($filter) | Out-Null
    }
    Assert-True ($routeEvents.Count -eq 6) 'The eight distinct operator audit kinds should select six webhook filters.'
    foreach ($kind in @('server.status', 'player.connection', 'raid.status', 'security.alert', 'character.validation', 'event.unknown')) {
        Assert-True ($null -eq $eventFilter.Invoke($null, [object[]]@($kind))) `
            'Synthetic selector names and unknown names must not become source audit events.'
    }
    $settingsType.GetProperty('WebhookRoutes').GetValue($settings).Add($route)
    $inertHttp = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($httpType)
    $webhooks = $webhookType.GetConstructors()[0].Invoke([object[]]@($settings, $inertHttp, $null))
    $webhookQueue = $webhookType.GetField('_queue', $instanceFlags).GetValue($webhooks)
    $inertSession = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($discordSessionType)
    $webhookField = $discordSessionType.GetField('_webhooks', $instanceFlags)
    $webhookField.SetValue($inertSession, $webhooks)
    $sessionField.SetValue($null, $inertSession)
    try {
        Reset-TestQueue
        Invoke-SecurityAudit
        Invoke-Observations @('[skill_gain:1] raw diagnostic SECRET_MARKER /private/profile')
        Invoke-ConnectionAudit
        Assert-True ($queue.Count -eq 3 -and $webhookQueue.Count -eq 3) 'Operator events did not reach both independent sinks.'
        Reset-TestQueue -Capacity 1
        Invoke-ConnectionAudit -Reason 'before_full'
        $notificationsBefore = $webhookQueue.Count
        Invoke-ConnectionAudit -Reason 'after_full'
        Assert-True ($queue.Count -eq 1 -and $webhookQueue.Count -eq $notificationsBefore + 1) 'Full audit queue or its failed warning logger blocked the webhook sink.'
        $queueField.SetValue($writer, $null)
        $notificationsBefore = $webhookQueue.Count
        Invoke-ConnectionAudit -Reason 'stopped_writer'
        Assert-True ($webhookQueue.Count -eq $notificationsBefore + 1) 'A stopped audit writer blocked optional webhook delivery.'
        Reset-TestQueue
        $webhookField.SetValue($inertSession, $null)
        Invoke-ConnectionAudit -Reason 'broken_webhook'
        Assert-True ($queue.Count -eq 1) 'A broken webhook sink prevented local audit or escaped rejection handling.'
    }
    finally {
        $sessionField.SetValue($null, $originalDiscordSession)
        $webhooks.Dispose()
    }

    # Exercise the real BepInEx ManualLogSource event path with a listener that
    # throws. Both Info diagnostics and Warning detections must still return
    # normally and retain their local audit event before the optional console.
    Reset-TestQueue
    $networkRuntimeType = $assembly.GetType('ServerManager.ServerManagerRuntime', $true)
    $logDetection = $networkRuntimeType.GetMethod('LogDetection', $staticFlags)
    $peerIdentityType = $assembly.GetType('ServerManager.ServerPeerIdentity', $true)
    $peerIdentityConstructor = $peerIdentityType.GetConstructors($instanceFlags)[0]
    $peer = [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
        $peerIdentityConstructor.GetParameters()[0].ParameterType)
    $peerIdentity = $peerIdentityConstructor.Invoke([object[]]@(
        $peer, '76561198000000001', 'local-test', 'Audit Viking'))
    $reportType = $assembly.GetType('ServerManager.DetectionReport', $true)
    $evidenceType = $assembly.GetType('ServerManager.DetectionEvidence', $true)
    $pluginType = $assembly.GetType('ServerManager.ServerManagerPlugin', $true)
    $pluginLogProperty = $pluginType.GetProperty('Log', $staticFlags)
    $originalConsole = $pluginLogProperty.GetValue($null)
    $console = $pluginLogProperty.PropertyType.GetConstructor([Type[]]@([string])).Invoke(
        [object[]]@('Throwing audit smoke listener'))
    $consoleEvent = $console.GetType().GetEvent('LogEvent')
    $script:throwingConsoleCalls = 0
    $throwingListener = [System.Management.Automation.LanguagePrimitives]::ConvertTo(
        [scriptblock]{
            param($sender, $arguments)
            ++$script:throwingConsoleCalls
            throw [InvalidOperationException]::new('Expected console listener failure')
        }, $consoleEvent.EventHandlerType)
    $consoleEvent.AddEventHandler($console, $throwingListener)
    $pluginLogProperty.SetValue($null, $console)
    try {
        foreach ($evidenceName in @('ProcessDetectorUnavailable', 'MaximumHealthLimitExceeded')) {
            $report = $reportType.GetConstructors($instanceFlags)[0].Invoke([object[]]@(
                [byte[]]::new(16), [byte[]]::new(32), [uint32]1,
                [Enum]::Parse($evidenceType, $evidenceName), '', [uint32]1))
            $logDetection.Invoke($null, [object[]]@(
                $peerIdentity, $report, [Enum]::Parse($responseType, 'Log'),
                'accepted snapshot; logger failure probe', 'snapshot_received')) | Out-Null
        }
        Assert-True ($script:throwingConsoleCalls -eq 2 -and $queue.Count -eq 2 -and
            @($queue.ToArray() | Where-Object { $_.Fields['outcome'] -eq 'diagnostic' }).Count -eq 1 -and
            @($queue.ToArray() | Where-Object { $_.Fields['outcome'] -eq 'observed' }).Count -eq 1) `
            'A throwing Info/Warning listener escaped LogDetection or cancelled its local audit event.'
    }
    finally {
        $pluginLogProperty.SetValue($null, $originalConsole)
        $consoleEvent.RemoveEventHandler($console, $throwingListener)
        $console.Dispose()
    }

    # A null list fails before UnityEngine.Object's native-backed null operator
    # is touched. This is a deliberate observer fault, not a live-host fixture:
    # the complete post-ACK hook must contain it and retain a failure diagnostic.
    Reset-TestQueue
    $recordStatLimits = $networkRuntimeType.GetMethod('RecordCharacterStatLimits', $staticFlags)
    $recordStatLimits.Invoke($null, [object[]]@(
        $null, 'steamworks:76561198000000001', 'Audit Viking',
        $null, 'stored', $false)) | Out-Null
    $processingFailure = $queue.ToArray()[0]
    Assert-True ($queue.Count -eq 1 -and
        $processingFailure.Kind -eq 'security.detection' -and
        $processingFailure.Fields['source'] -eq 'stored' -and
        $processingFailure.Fields['evidence'] -eq 'stat_limit_processing' -and
        $processingFailure.Fields['response'] -eq 'Log' -and
        $processingFailure.Fields['outcome'] -eq 'processing_failed' -and
        $processingFailure.Fields['detail'].Length -gt 0) `
        'A failed stat observer escaped the post-ACK hook or lost its processing_failed diagnostic.'
    $findingType = $assembly.GetType('ServerManager.CharacterStatLimitFinding', $true)
    $validFinding = $findingType.GetConstructors($instanceFlags)[0].Invoke([object[]]@(
        'maximum_health', [single]2000, [single]1000))
    $validFindings = [Array]::CreateInstance($findingType, 1)
    $validFindings.SetValue($validFinding, 0)
    $clearAudit.Invoke($null, $null) | Out-Null
    $recordStatLimits.Invoke($null, [object[]]@(
        $null, 'steamworks:76561198000000001', 'Audit Viking',
        $validFindings, 'stored', $false)) | Out-Null
    Assert-True ($queue.Count -eq 2 -and
        $queue.ToArray()[1].Fields['evidence'] -eq 'MaximumHealthLimitExceeded' -and
        $queue.ToArray()[1].Fields['outcome'] -eq 'observed' -and
        $queue.ToArray()[1].Fields['detail'] -like '*numeric cap does not reject*') `
        'The failure guard suppressed a valid stored stat observation or changed its non-reject semantics.'

    # Exercise the actual bounded background writer and its existing 10 MiB rotation.
    $clearAudit.Invoke($null, $null) | Out-Null
    $queueField.SetValue($writer, $null)
    $writerType.GetMethod('Start', $instanceFlags).Invoke($writer, [object[]]@($temporaryRoot)) | Out-Null
    $auditPath = Join-Path $temporaryRoot 'logs\events-audit.log'
    $stream = [IO.File]::Open($auditPath, [IO.FileMode]::Create, [IO.FileAccess]::Write)
    try { $stream.SetLength(10L * 1024 * 1024) } finally { $stream.Dispose() }
    Invoke-Observations @($findings[1]) -Source 'stored_host'
    $recordShadow.Invoke($null, [object[]]@($snapshot, 'incoming_host')) | Out-Null
    Invoke-SecurityAudit -Kind 'character.save_rejected' -Outcome 'rejected'
    Invoke-ConnectionAudit -Detail (('expected_sha256=' + ('a' * 64) + '; ') * 30 + 'MISMATCH_TAIL_SENTINEL')
    $writerType.GetMethod('Stop', $instanceFlags).Invoke($writer, $null) | Out-Null
    $lines = [IO.File]::ReadAllLines($auditPath)
    Assert-True ($lines.Count -eq 4 -and (Test-Path -LiteralPath ($auditPath + '.1')) -and
        -not (Test-Path -LiteralPath (Join-Path $temporaryRoot 'logs\events-chat.log'))) `
        'Character records did not reuse audit routing, queued writing and 10 MiB rotation.'
    foreach ($line in $lines) {
        $columns = $line.Split([char]9)
        $expectedReliability = if ($columns[1] -eq 'connection.rejected') { 'authoritative' } else { 'observed' }
        Assert-True ($columns.Count -eq 5 -and $columns[2] -eq $expectedReliability -and
            $columns[4] -like '*account="steamworks:76561198000000001"*' -and
            $columns[4] -like '*character="Audit Viking"*' -and
            $columns[4] -like '*source=*') 'Audit file format omitted operator-identifying fields.'
        if ($columns[1] -eq 'connection.rejected') {
            Assert-True ($columns[4].Length -gt 1000 -and $columns[4].Contains('MISMATCH_TAIL_SENTINEL')) 'Existing generic line truncation lost bounded per-mod mismatch details.'
        }
        if ($columns[1] -eq 'character.save_rejected') {
            Assert-True ($columns[4] -like '*evidence="MaximumHealthExceeded"*' -and
                $columns[4] -like '*response=Kick*' -and $columns[4] -like '*outcome="rejected"*' -and
                $columns[4] -like '*detail=*') 'The security file record lost its evidence, response or actual outcome.'
        }
        elseif ($columns[1] -ne 'connection.rejected') {
            Assert-True ($columns[4] -like '*revision=*') 'A character diagnostic lost its revision.'
        }
    }

    # Invoke the worker synchronously so a missing guard fails the test instead
    # of crashing the probe process with an unhandled background exception.
    $blockedDirectory = Join-Path $temporaryRoot 'blocked-writer'
    [IO.Directory]::CreateDirectory((Join-Path $blockedDirectory 'events-audit.log')) | Out-Null
    $failedQueue = $queueType.GetConstructor([Type[]]@([int])).Invoke([object[]]@(3))
    $failedQueue.TryAdd($shadow) | Out-Null
    $failedQueue.TryAdd($sanitized) | Out-Null
    $failedQueue.TryAdd($security) | Out-Null
    $failedQueue.CompleteAdding()
    $pluginType = $assembly.GetType('ServerManager.ServerManagerPlugin', $true)
    Assert-True ($null -eq $pluginType.GetProperty('Log', $staticFlags).GetValue($null)) `
        'The blocked-writer probe requires an unavailable secondary logger.'
    $writerType.GetMethod('WriteLoop', $staticFlags).Invoke(
        $null, [object[]]@($failedQueue, [string]$blockedDirectory)) | Out-Null
    Assert-True ($failedQueue.Count -eq 0) `
        'A disk error plus secondary logger failure escaped or stopped the writer loop.'

    # Check isolation and lifecycle wiring without requiring a running Unity world.
    $runtimeSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Events\ServerEventRuntime.cs') -Raw
    $helper = [regex]::Match($runtimeSource,
        '(?s)private static void TryWriteCharacterAudit\(.*?(?=private static string SafeCharacterAuditValue)').Value
    Assert-True ($helper -match 'LogWriter\.TryWrite\(value\)' -and
        $helper -notmatch '(ServerManagerIntegrationApi|BroadcastEventDisplay)\.' -and
        $helper -match 'TryPublishOperatorWebhook\(value\)' -and
        $helper -match 'try \{ Discord.DiscordRuntime.Publish\(value\); \}') 'Operator audit lost independent summary-only webhook routing or reached public/UI consumers.'
    Assert-True ($runtimeSource -match
        '(?s)if \(ServerManagerEventKinds.IsOperatorAudit\(kind\)\).*?TryQueueOperatorAudit\(value, null\);.*?return value;.*?if \(IsLogOnlyChatKind\(kind\)\).*?return value;.*?ServerManagerIntegrationApi.Publish') `
        'The generic publish path no longer guards operator/private-chat kinds from public/UI delivery.'
    $securityHelper = [regex]::Match($runtimeSource,
        '(?s)internal static void RecordSecurityEvent\(.*?(?=private static void TryWriteCharacterAudit)').Value
    Assert-True ($securityHelper -match 'TryQueueOperatorAudit\(value, key\)' -and
        $securityHelper -match 'catch \(Exception exception\) when \(!IntegrityCanonical.IsFatal\(exception\)\)' -and
        $securityHelper -notmatch '(ServerManagerIntegrationApi|DiscordRuntime|BroadcastEventDisplay)\.' -and
        $securityHelper -notmatch '\bPublish\(') 'Security audit failures can escape or evidence can reach public/UI consumers.'
    $observationReset = [regex]::Match($runtimeSource,
        '(?s)private static void ResetObservationState\(\).*?(?=private static void RunShutdownStep)').Value
    Assert-True ($observationReset -match 'ClearCharacterAuditState\(\)') `
        'Observation-state reset no longer clears character audit dedupe.'
    foreach ($methodName in @('Initialize', 'OnServerStarted', 'OnServerShutdown')) {
        $body = [regex]::Match($runtimeSource,
            ('(?s)internal static void ' + $methodName + '\(.*?(?=\r?\n        internal static)')).Value
        Assert-True ($body -match 'ClearCharacterAuditState\(\)' -or
            $body -match 'ResetObservationState\(\)') "Audit dedupe survives $methodName."
    }
    $mainSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Networking\ServerManagerRuntime.cs') -Raw
    Assert-True ($mainSource -match
        '(?s)internal static bool BeforeLocalHostGameplay\(.*?BeforeNetworkStart\(network\);.*?LocalHostCharacterRuntime.Prepare\(' -and
        $mainSource -match
        '(?s)internal static bool BeforeNetworkStart\(.*?ServerEventRuntime.OnServerStarted\(znet\)') `
        'Listen-host stored observations can run before the audit writer starts.'
}
finally {
    $writerType.GetMethod('Stop', $instanceFlags).Invoke($writer, $null) | Out-Null
    $queueField.SetValue($writer, $originalQueue)
    $clearAudit.Invoke($null, $null) | Out-Null
    foreach ($fieldName in $originalFlags.Keys) {
        $runtimeType.GetField($fieldName, $staticFlags).SetValue($null, $originalFlags[$fieldName])
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $temporaryRoot).Path)
        Assert-True ($resolvedTemporaryRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) `
            'Refusing to remove an unexpected audit test path.'
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}

Write-Output 'Character/security audit classification, canonical identity, sanitization, bounded shared cooldown, accurate outcomes, lifecycle, direct Publish isolation, throwing-console/post-ACK failure safety and queued rotation smoke passed.'
