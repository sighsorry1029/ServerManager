param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
# Load the built plugin with the existing in-memory native-startup shims. No
# character scenarios, game config writes, Harmony patching, or live UI run.
. (Join-Path $PSScriptRoot 'BackupOnlySmoke.ps1') -Configuration $Configuration -GamePath $GamePath -FixtureOnly
# Match the menu fixture's metadata dependencies: ClientMenuBranding caches
# private FejdStartup members, whose signatures include these game/UI types.
foreach ($dependencyName in @('UnityEngine.ImageConversionModule.dll', 'UnityEngine.UI.dll',
    'Unity.TextMeshPro.dll', 'SoftReferenceableAssets.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managedRoot $dependencyName)) | Out-Null
}
# Only the game-language singleton is shimmed to absent. The production helper
# must use its English fallback, and no Unity PlayerPrefs/resource call runs.
Load-TestDependency (Join-Path $managedRoot 'assembly_guiutils.dll') {
    param($definition)
    $localization = $definition.MainModule.Types | Where-Object Name -eq 'Localization'
    $singleton = $localization.Methods | Where-Object Name -eq 'get_instance'
    Clear-Body $singleton
    $singleton.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldnull))
    $singleton.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
} | Out-Null
# Keep Harmony's adjacent MonoMod dependencies in the same load-from context.
[Reflection.Assembly]::LoadFrom((Join-Path $GamePath 'BepInEx\core\0Harmony.dll')) | Out-Null
[string]$localizationRoot = Join-Path ([IO.Path]::GetTempPath()) ('smlocalization-' + [Guid]::NewGuid().ToString('N'))
[string]$bepinexRoot = Join-Path $localizationRoot 'BepInEx'
[string]$configurationDirectory = Join-Path $bepinexRoot 'config'
[IO.Directory]::CreateDirectory($configurationDirectory) | Out-Null
$ownedLocalizationLinks = [Collections.Generic.List[string]]::new()

function Read-TranslationResource([string]$Language) {
    $names = @($script:plugin.GetManifestResourceNames() | Where-Object {
        $_.EndsWith('.' + $Language + '.yml', [StringComparison]::Ordinal)
    })
    Assert-True ($names.Count -eq 1) ('Expected exactly one embedded ' + $Language + ' localization resource.')
    $stream = $script:plugin.GetManifestResourceStream($names[0])
    $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true))
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose(); $stream.Dispose() }
}
function Parse-TranslationYaml([string]$Yaml) {
    $builderType = $script:plugin.GetType('YamlDotNet.Serialization.DeserializerBuilder', $true)
    $builder = [Activator]::CreateInstance($builderType, $true)
    $builder = $builderType.GetMethod('IgnoreFields').Invoke($builder, @())
    $deserializer = $builderType.GetMethod('Build').Invoke($builder, @())
    $deserialize = $deserializer.GetType().GetMethods($script:allInstance) | Where-Object {
        $_.Name -eq 'Deserialize' -and $_.IsGenericMethodDefinition -and
        $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType -eq [string]
    }
    return ,$deserialize.MakeGenericMethod([Collections.Generic.Dictionary[string,string]]).Invoke($deserializer, [object[]]@($Yaml))
}
function Localized([string]$Language, [string]$Key, [string[]]$Values = @()) {
    return $script:textForLanguage.Invoke($null, [object[]]@($Language, $Key, $Values))
}
function Reload-TestLanguage([string]$Language, [string]$SearchRoot = $script:bepinexRoot, [string]$ConfigRoot = $script:configurationDirectory) {
    return $script:reloadLanguage.Invoke($null, [object[]]@($Language, $SearchRoot, $ConfigRoot))
}
function Write-TestTranslation([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}
function New-Rejection([string]$Code = 'ProtocolVersionMismatch', [string]$Diagnostic = 'ENGLISH_DIAGNOSTIC_ONLY') {
    return $script:rejectionType.GetConstructors()[0].Invoke([object[]]@(
        [Enum]::Parse($script:rejectCodeType, $Code), $Diagnostic, [int]3, $true))
}
function With-PlayerMessage($Rejection, [string]$Key, [string[]]$Values = @()) {
    return $script:withPlayerMessage.Invoke($Rejection, [object[]]@($Key, $Values))
}
function New-RejectPacket([byte[]]$Payload) {
    return $script:packetType.GetConstructors()[0].Invoke([object[]]@(
        [Enum]::Parse($script:packetKindType, 'Reject'), [uint32]0,
        $script:sessionId, $script:nonce, $Payload))
}
function Encode-And-DecodeReject($Rejection) {
    $package = $script:createReject.Invoke($null, [object[]]@($script:sessionId, $script:nonce, $Rejection, $script:limits))
    $arguments = [object[]]@($package, $script:limits, $null, $null)
    Assert-True ($script:decodePacket.Invoke($null, $arguments)) 'A typed rejection did not decode through the production packet codec.'
    $decoded = $script:decodeReject.Invoke($null, [object[]]@($arguments[2], $script:limits))
    return [pscustomobject]@{ Package = $package; Packet = $arguments[2]; Rejection = $decoded }
}
function New-RawRejectPayload([int]$Code, [string]$Key, [string[]]$Values = @(), [byte[]]$Trailing = @()) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream, [Text.UTF8Encoding]::new($false, $true))
    try {
        $writer.Write($Code); $writer.Write([int]3)
        foreach ($value in @('SERVER_DIAGNOSTIC', $Key)) {
            [byte[]]$encoded = [Text.Encoding]::UTF8.GetBytes($value)
            $writer.Write([int]$encoded.Length); $writer.Write($encoded)
        }
        $writer.Write([int]$Values.Count)
        foreach ($value in $Values) {
            [byte[]]$encoded = [Text.Encoding]::UTF8.GetBytes($value)
            $writer.Write([int]$encoded.Length); $writer.Write($encoded)
        }
        $writer.Write($Trailing); $writer.Flush()
        return ,$stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

try {
    $localizerType = $plugin.GetType('ServerManager.PlayerLocalizer', $true)
    $textForLanguage = $localizerType.GetMethod('TextForLanguage', $allStatic)
    $reloadLanguage = $localizerType.GetMethod('ReloadLanguage', $allStatic)
    $isKnownMessage = $localizerType.GetMethod('IsKnownMessage', $allStatic)
    Assert-True ($null -ne $textForLanguage -and $textForLanguage.GetParameters().Count -eq 3) 'The language-explicit localization helper is missing.'
    Assert-True ($null -ne $reloadLanguage -and $reloadLanguage.GetParameters().Count -eq 3 -and $null -ne $isKnownMessage) 'The explicit BepInEx/config-root reload or typed-message validation seam is missing.'
    foreach ($language in @('English', 'Korean', 'UnlistedLanguage')) {
        Assert-True (Reload-TestLanguage $language) 'Initial localization load from an empty fixture directory failed.'
    }
    $english = Parse-TranslationYaml (Read-TranslationResource 'English')
    $korean = Parse-TranslationYaml (Read-TranslationResource 'Korean')
    Assert-True ($english.Count -gt 0 -and $english.Count -eq $korean.Count) 'Embedded translation tables are empty or have different key counts.'
    Assert-True ((@($english.Keys | Sort-Object) -join '|') -ceq (@($korean.Keys | Sort-Object) -join '|')) 'English and Korean localization key sets diverged.'
    $menuGuideKeys = @('sm_menu_guide_title', 'sm_menu_guide_character', 'sm_menu_guide_mods', 'sm_menu_guide_update', 'sm_menu_guide_worlds')
    foreach ($menuGuideKey in $menuGuideKeys) {
        Assert-True ($english.ContainsKey($menuGuideKey) -and $korean.ContainsKey($menuGuideKey) -and
            $english[$menuGuideKey] -notmatch '\{[0-9]+\}' -and $korean[$menuGuideKey] -notmatch '\{[0-9]+\}') `
            ('The local menu guide is missing a zero-argument English/Korean message: ' + $menuGuideKey)
    }
    $optionalKeys = @('title', 'note', 'loading', 'unavailable', 'unsupported', 'host_help', 'too_large', 'stale', 'empty', 'installed', 'version_unknown')
    foreach ($suffix in $optionalKeys) {
        $key = 'sm_menu_optional_' + $suffix
        Assert-True ($english.ContainsKey($key) -and $korean.ContainsKey($key) -and
            -not $isKnownMessage.Invoke($null, [object[]]@($key, 0))) `
            ('Optional preview translation missing or expanded the rejection protocol: ' + $key)
    }
    # Exercise the real presentation formatter with the existing absent-game-language
    # shim. This does not instantiate UI or issue a Steam/DNS request.
    $menuType = $plugin.GetType('ServerManager.ClientMenuBranding', $true)
    $formatOptional = $menuType.GetMethod('FormatOptionalList', $allStatic)
    $queryStateType = $plugin.GetType('ServerManager.OptionalModQueryState', $true)
    $catalogEntryType = $plugin.GetType('ServerManager.OptionalModCatalogEntry', $true)
    $emptyEntries = [Array]::CreateInstance($catalogEntryType, 0)
    $sampleEntries = [Array]::CreateInstance($catalogEntryType, 1)
    $entryConstructor = $catalogEntryType.GetConstructors($allInstance) | Select-Object -First 1
    $sampleEntries.SetValue($entryConstructor.Invoke([object[]]@('example.mod', 'Example Mod', [string[]]@('1.0.0', '2.0.0'))), 0)
    foreach ($stateName in @('Loading', 'Unavailable', 'Unsupported', 'TooLarge')) {
        $state = [Enum]::Parse($queryStateType, $stateName)
        $currentText = $formatOptional.Invoke($null, [object[]]@($state, $false, $sampleEntries, $null))
        $staleText = $formatOptional.Invoke($null, [object[]]@($state, $true, $sampleEntries, $null))
        Assert-True (-not $currentText.Contains('Example Mod') -and
            $staleText.Contains('Example Mod') -and $staleText.Contains('1.0.0, 2.0.0') -and
            $staleText.Contains((Localized 'English' 'sm_menu_optional_stale'))) `
            ('Unconfirmed preview is shown as current, or cached versions are lost: ' + $stateName)
    }
    $availableState = [Enum]::Parse($queryStateType, 'Available')
    $emptyText = $formatOptional.Invoke($null, [object[]]@($availableState, $false, $emptyEntries, $null))
    Assert-True ($emptyText.Contains((Localized 'English' 'sm_menu_optional_empty')) -and
        -not $emptyText.Contains((Localized 'English' 'sm_menu_optional_unavailable'))) `
        'An empty available catalog must not be represented as a failed request.'
    $loadedVersions = [Collections.Generic.Dictionary[string,Version]]::new([StringComparer]::Ordinal)
    $loadedVersions.Add('example.mod', [Version]'1.0.0')
    $installedText = $formatOptional.Invoke($null, [object[]]@($availableState, $false, $sampleEntries, $loadedVersions))
    Assert-True (-not $installedText.Contains('Example Mod') -and
        $installedText.Contains((Localized 'English' 'sm_menu_optional_installed')) -and
        -not $installedText.Contains((Localized 'English' 'sm_menu_optional_empty'))) `
        'Entirely filtered and genuinely empty catalogs must remain distinct.'
    $staleInstalled = $formatOptional.Invoke($null, [object[]]@($availableState, $true, $sampleEntries, $loadedVersions))
    Assert-True ($staleInstalled.Contains((Localized 'English' 'sm_menu_optional_stale'))) `
        'Filtering loaded mods must not remove the stale-result qualification.'
    $isLoaded = $menuType.GetMethod('IsOptionalModLoaded', $allStatic)
    foreach ($fixture in @(
        @{ Guid='example.mod'; Versions=@('1.0.0'); Expected=$true },
        @{ Guid='example.mod'; Versions=@('2.0.0', '1.0.0'); Expected=$true },
        @{ Guid='example.mod'; Versions=@('01.00.000'); Expected=$true },
        @{ Guid='example.mod'; Versions=@('2.0.0'); Expected=$false },
        @{ Guid='example.mod'; Versions=@('1.0'); Expected=$false },
        @{ Guid='example.mod'; Versions=@('1.0.0.0'); Expected=$false },
        @{ Guid='example.mod'; Versions=@('not-a-version'); Expected=$false },
        @{ Guid='example.mod'; Versions=@(); Expected=$false },
        @{ Guid='different.mod'; Versions=@('1.0.0'); Expected=$false }
    )) {
        $entry = $entryConstructor.Invoke([object[]]@($fixture.Guid, 'Example Mod', [string[]]$fixture.Versions))
        Assert-True ([bool]$isLoaded.Invoke($null, [object[]]@($entry, $loadedVersions)) -eq $fixture.Expected) `
            ('Loaded preview filtering must use GUID and exact parsed version, not display name: ' + $fixture.Guid + '/' + ($fixture.Versions -join ','))
    }
    $loadedVersions['example.mod'] = [Version]'9.0.0'
    $wrongVersionText = $formatOptional.Invoke($null, [object[]]@($availableState, $false, $sampleEntries, $loadedVersions))
    Assert-True ($wrongVersionText.Contains('Example Mod') -and $wrongVersionText.Contains('1.0.0, 2.0.0')) `
        'A loaded but non-allowed version must remain visible.'
    foreach ($key in $english.Keys) {
        Assert-True ($key -cmatch '^sm_[a-z0-9_]+$' -and $key.Length -le 64) 'An embedded message key is not a bounded namespaced token.'
        Assert-True (-not [string]::IsNullOrWhiteSpace($english[$key]) -and -not [string]::IsNullOrWhiteSpace($korean[$key])) 'An embedded translation is empty.'
        $englishSlots = @([Regex]::Matches($english[$key], '\{([0-9]+)\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $koreanSlots = @([Regex]::Matches($korean[$key], '\{([0-9]+)\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        Assert-True (($englishSlots -join '|') -ceq ($koreanSlots -join '|')) ('Translation placeholder mismatch: ' + $key)
        $expectedArguments = if ($englishSlots.Count) { ([int]($englishSlots | Measure-Object -Maximum).Maximum) + 1 } else { 0 }
        $expectedWireMessage = -not $key.StartsWith('sm_menu_', [StringComparison]::Ordinal) -and
            -not $key.StartsWith('sm_event_', [StringComparison]::Ordinal) -and
            -not $key.StartsWith('sm_discord_', [StringComparison]::Ordinal)
        Assert-True ([bool]$isKnownMessage.Invoke($null, [object[]]@($key, $expectedArguments)) -eq $expectedWireMessage -and
            -not $isKnownMessage.Invoke($null, [object[]]@($key, ($expectedArguments + 1)))) ('Typed argument count validation disagrees with the English template: ' + $key)
        $plainArguments = [string[]]@('VALUE_ZERO', 'VALUE_ONE', 'VALUE_TWO', 'VALUE_THREE')
        foreach ($language in @('English', 'Korean', 'UnlistedLanguage')) {
            $rendered = Localized $language $key $plainArguments
            Assert-True (-not [string]::IsNullOrWhiteSpace($rendered) -and $rendered -notmatch '\{[0-9]+\}') ('Translation rendering left an unresolved placeholder: ' + $key)
            if ($language -eq 'UnlistedLanguage') {
                Assert-True ($rendered -ceq (Localized 'English' $key $plainArguments)) 'Unknown languages do not fall back to English deterministically.'
            }
        }
    }
    # Use the plugin's real embedded dictionaries, not copies of the bot text.
    # Discord presentation must not enlarge the player-message wire allowlist.
    $discordCommandNames = @('status', 'players', 'announce', 'chat', 'banlist', 'adminlist',
        'adminadd', 'adminremove', 'accesslist', 'accessadd', 'accessremove', 'keylist', 'keyadd',
        'keyremove', 'eventstart', 'eventstop', 'characterlist', 'characterinfo', 'characterbackups',
        'characterrestore', 'giveitem', 'teleport', 'skillget', 'skillset', 'heal', 'damage',
        'modsstatus', 'modsreload', 'discordstatus', 'discordtest', 'cronstatus', 'cronack', 'help', 'rcon')
    $discordOptionNames = @('player', 'steam_id', 'skill', 'x', 'y', 'z', 'announcement', 'chat',
        'key', 'event', 'page', 'backup_id', 'prefab', 'amount', 'quality', 'data_id', 'to', 'value',
        'health', 'damage', 'job', 'command')
    $discordNoticeArities = @{
        unauthorized = 0; invalid_arguments = 0; registration_stale = 0; rate_limited = 0
        not_ready = 0; server_unavailable = 0; queue_full = 0; timeout_started = 0; timeout_waiting = 0
        stale_command = 0; command_failed = 1; authorization_changed = 0; rcon_cooldown = 0
        rcon_unavailable = 0; shutdown = 0; request_accepted = 1; success = 1; failed = 1
        save_pending = 0; help_teleport = 0; help_rcon = 0
    }
    $discordArities = @{}
    foreach ($name in $discordCommandNames) { $discordArities['sm_discord_command_' + $name] = 0 }
    foreach ($name in $discordOptionNames) { $discordArities['sm_discord_option_' + $name] = 0 }
    foreach ($entry in $discordNoticeArities.GetEnumerator()) { $discordArities['sm_discord_' + $entry.Key] = [int]$entry.Value }
    [string[]]$discordKeys = @($english.Keys | Where-Object { $_.StartsWith('sm_discord_', [StringComparison]::Ordinal) } | Sort-Object)
    Assert-True ($discordCommandNames.Count -eq 34 -and $discordOptionNames.Count -eq 22 -and
        $discordArities.Count -eq 77 -and ($discordKeys -join '|') -ceq (@($discordArities.Keys | Sort-Object) -join '|')) `
        'The real Discord resources must contain exactly 34 command descriptions, 22 option descriptions and 21 adapter notices.'
    foreach ($discordKey in $discordKeys) {
        $arity = [int]$discordArities[$discordKey]
        [string[]]$literalValues = if ($arity -eq 1) { @('RAW_{0}_sm_discord_success_@everyone_<player>') } else { @() }
        foreach ($language in @('English', 'Korean')) {
            $table = if ($language -eq 'Korean') { $korean } else { $english }
            Assert-True ($table.ContainsKey($discordKey)) ('A Discord translation is absent from the real ' + $language + ' resource: ' + $discordKey)
            $template = $table[$discordKey]
            $slots = @([Regex]::Matches($template, '\{([0-9]+)\}'))
            Assert-True ($slots.Count -eq $arity -and ($arity -eq 0 -or $slots[0].Groups[1].Value -ceq '0')) `
                ('A Discord template changed its canonical simple-placeholder shape: ' + $discordKey)
            $expected = if ($arity -eq 1) { $template.Replace('{0}', $literalValues[0]) } else { $template }
            Assert-True ((Localized $language $discordKey $literalValues) -ceq $expected) `
                ('Discord rendering changed a literal argument, failed to use the embedded resource, or recursively translated text: ' + $discordKey)
        }
        Assert-True ($english[$discordKey] -cne $korean[$discordKey] -and $korean[$discordKey] -match '[\uac00-\ud7a3]') `
            ('A Discord Korean resource is untranslated: ' + $discordKey)
        foreach ($testArity in 0..3) {
            Assert-True (-not $isKnownMessage.Invoke($null, [object[]]@($discordKey, $testArity))) `
                ('A Discord-only token expanded the player-message wire allowlist: ' + $discordKey)
        }
    }
    foreach ($descriptionKey in @($discordCommandNames | ForEach-Object { 'sm_discord_command_' + $_ }) +
        @($discordOptionNames | ForEach-Object { 'sm_discord_option_' + $_ })) {
        Assert-True ($english[$descriptionKey].Length -ge 1 -and $english[$descriptionKey].Length -le 100 -and
            $korean[$descriptionKey].Length -ge 1 -and $korean[$descriptionKey].Length -le 100) `
            ('An embedded Discord command/option description exceeds its 1-100 character registration limit: ' + $descriptionKey)
    }
    [string[]]$eventKeys = @($english.Keys | Where-Object { $_.StartsWith('sm_event_', [StringComparison]::Ordinal) } | Sort-Object)
    Assert-True ($eventKeys.Count -eq 52) 'The event resource surface must contain 39 story keys, four auxiliary labels and nine webhook-only labels.'
    $webhookLabels = @{
        sm_event_server_ready = 0; sm_event_server_shutdown = 0; sm_event_world_saved = 0
        sm_event_player_joined = 1; sm_event_player_first_join = 1; sm_event_player_left = 1
        sm_event_raid_started = 0; sm_event_raid_ended = 0; sm_event_announcement = 0
    }
    $eventMessageType = $plugin.GetType('ServerManager.Events.EventMessageText', $true)
    $isValidEvent = $eventMessageType.GetMethod('IsValidMessage', $allStatic)
    $renderEvent = $eventMessageType.GetMethod('RenderForLanguage', $allStatic)
    $renderLocalEvent = $eventMessageType.GetMethod('Render', $allStatic)
    $eventStories = 0
    foreach ($eventKey in $eventKeys) {
        $slots = @([Regex]::Matches($english[$eventKey], '\{([0-9]+)\}') | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique)
        $arity = if ($slots.Count) { [int]($slots | Measure-Object -Maximum).Maximum + 1 } else { 0 }
        Assert-True (-not $isKnownMessage.Invoke($null, [object[]]@($eventKey, $arity))) ('Event localization expanded the remote rejection allowlist: ' + $eventKey)
        $acceptedKinds = @(@('player.death', 'combat.pvp_kill', 'boss.killed') | Where-Object {
            $isValidEvent.Invoke($null, [object[]]@([string]$_, $eventKey, $arity))
        })
        if ($acceptedKinds.Count -eq 0) { continue }
        ++$eventStories
        [string[]]$names = if ($arity -eq 1) { @('NAME_ZERO') } else { @('NAME_ZERO', 'NAME_ONE') }
        $koreanEvent = $renderEvent.Invoke($null, [object[]]@($eventKey, $names, [byte]0, 'Korean'))
        $englishEvent = $renderEvent.Invoke($null, [object[]]@($eventKey, $names, [byte]0, 'English'))
        Assert-True ($koreanEvent -ceq (Localized 'Korean' $eventKey $names) -and
            $englishEvent -ceq (Localized 'English' $eventKey $names) -and $koreanEvent -cne $englishEvent -and
            $koreanEvent -match '[\uac00-\ud7a3]') ('A translated event fell back to English or did not use its actual embedded Korean resource: ' + $eventKey)
        Assert-True ($renderLocalEvent.Invoke($null, [object[]]@($eventKey, $names, [byte]0)) -ceq $englishEvent) 'Local event rendering did not use the existing no-game-singleton English fallback.'
    }
    Assert-True ($eventStories -eq 39) 'The production event display validator does not cover exactly 39 translated story keys.'
    foreach ($webhookLabelEntry in $webhookLabels.GetEnumerator()) {
        $webhookKey = [string]$webhookLabelEntry.Key
        $expectedArity = [int]$webhookLabelEntry.Value
        Assert-True ($english.ContainsKey($webhookKey) -and $korean.ContainsKey($webhookKey)) ('A webhook-only label is missing from the real resources: ' + $webhookKey)
        $slots = @([Regex]::Matches($english[$webhookKey], '\{([0-9]+)\}') | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique)
        $arity = if ($slots.Count) { [int]($slots | Measure-Object -Maximum).Maximum + 1 } else { 0 }
        Assert-True ($arity -eq $expectedArity) ('Webhook heading argument arity changed: ' + $webhookKey)
        [string[]]$names = @()
        if ($expectedArity -eq 1) { $names = @('LITERAL_NAME') }
        $englishLabel = Localized 'English' $webhookKey $names
        $koreanLabel = Localized 'Korean' $webhookKey $names
        Assert-True ($englishLabel -cne $koreanLabel -and $koreanLabel -match '[\uac00-\ud7a3]' -and
            ($expectedArity -eq 0 -or ($englishLabel.Contains('LITERAL_NAME') -and $koreanLabel.Contains('LITERAL_NAME')))) `
            ('A webhook-only label lost its Korean translation or literal name: ' + $webhookKey)
        foreach ($kind in @('player.death', 'combat.pvp_kill', 'boss.killed', 'server.announcement', 'chat.shout')) {
            foreach ($testArity in 0..3) {
                Assert-True (-not $isValidEvent.Invoke($null, [object[]]@($kind, $webhookKey, $testArity)) -and
                    -not $isKnownMessage.Invoke($null, [object[]]@($webhookKey, $testArity))) `
                    ('A webhook-only label expanded the combat display or rejection allowlist: ' + $webhookKey)
            }
        }
    }
    [string[]]$eventFallbackArguments = @('sm_event_unknown_player', 'sm_event_unknown_creature')
    $localizedFallbackEvent = $renderEvent.Invoke($null, [object[]]@('sm_event_death_creature_1', $eventFallbackArguments, [byte]3, 'Korean'))
    Assert-True ($localizedFallbackEvent.Contains((Localized 'Korean' 'sm_event_unknown_player')) -and
        $localizedFallbackEvent.Contains((Localized 'Korean' 'sm_event_unknown_creature')) -and
        -not $localizedFallbackEvent.Contains('sm_event_') -and ($eventFallbackArguments -join '|') -ceq 'sm_event_unknown_player|sm_event_unknown_creature') `
        'Fallback labels must localize only under their explicit mask and must not mutate the supplied argument array.'
    $literalTokenEvent = $renderEvent.Invoke($null, [object[]]@('sm_event_death_creature_1', $eventFallbackArguments, [byte]0, 'Korean'))
    Assert-True ($literalTokenEvent.Contains('sm_event_unknown_player') -and $literalTokenEvent.Contains('sm_event_unknown_creature')) `
        'Player or creature names matching locale tokens were recursively localized without an explicit label mask.'
    $argumentKey = @($english.Keys | Where-Object { $english[$_].Contains('{0}') } | Sort-Object)[0]
    Assert-True (-not [string]::IsNullOrEmpty($argumentKey)) 'The resource fixtures contain no argument-bearing message.'
    $unsafeArgument = '<b>$sm_secret {1}</b>' + [char]0x202e + "`r`nInjected"
    $literalRendered = Localized 'Korean' $argumentKey ([string[]]@($unsafeArgument, 'SECOND', 'THIRD', 'FOURTH'))
    Assert-True ($literalRendered.Contains($unsafeArgument)) 'The formatting helper recursively interpreted or changed a supplied literal argument.'
    $runtimeType = $plugin.GetType('ServerManager.ServerManagerRuntime', $true)
    $sanitizeRemote = $runtimeType.GetMethod('SanitizeRemoteRejectMessage', $allStatic)
    $sanitizeNotice = $runtimeType.GetMethod('SanitizePlayerNotice', $allStatic)
    Assert-True ($null -ne $sanitizeRemote -and $null -ne $sanitizeNotice) 'The independent remote-diagnostic and player-notice sanitizers are missing.'
    $safeRendered = $sanitizeRemote.Invoke($null, [object[]]@($literalRendered))
    Assert-True (-not $safeRendered.Contains('<b>') -and -not $safeRendered.Contains('</b>') -and
        -not $safeRendered.Contains([string][char]0x202e)) 'The bounded remote diagnostic path permits rich text or directional controls.'
    $safeNotice = $sanitizeNotice.Invoke($null, [object[]]@($literalRendered))
    Assert-True ($safeNotice.Contains('<b>$sm_secret {1}</b>') -and
        -not $safeNotice.Contains([string][char]0x202e)) 'Plain-text player notices lost literal markup or retained directional controls.'
    $longNotice = ('LongRequiredModName=' * 80) + "`n`n" +
        ((1..30 | ForEach-Object { "Required.Mod.$_ mismatch" }) -join "`n") +
        "`nEND_OF_PLAYER_NOTICE"
    Assert-True ($sanitizeNotice.Invoke($null, [object[]]@($longNotice)) -ceq $longNotice) 'The complete player notice is still truncated by a diagnostic character/newline limit.'
    Assert-True ($sanitizeRemote.Invoke($null, [object[]]@(('x' * 900))).Length -le 512) 'Removing the presentation limit also removed the remote diagnostic bound.'
    $mixedBreaks = "a`r`nb`rc`n`n" + [char]0x2028 + 'd' + [char]0x2029 + 'e'
    Assert-True ($sanitizeNotice.Invoke($null, [object[]]@($mixedBreaks)) -ceq "a`nb`nc`n`n`nd`ne") 'Player notice newline normalization drops blank lines or retains nonstandard line separators.'
    foreach ($unsafeCodepoint in @(0x0, 0x7, 0x9, 0x200d, 0x202e, 0xfeff, 0xd800, 0xdc00)) {
        $unsafeNotice = 'a' + [char]$unsafeCodepoint + 'b'
        Assert-True ($sanitizeNotice.Invoke($null, [object[]]@($unsafeNotice)) -ceq 'a b') 'A control, directional format character or isolated surrogate survived player-notice sanitization.'
    }
    $supplementaryNotice = 'a' + [char]::ConvertFromUtf32(0x1f600) + [char]::ConvertFromUtf32(0x20000) + 'b'
    Assert-True ($sanitizeNotice.Invoke($null, [object[]]@($supplementaryNotice)) -ceq $supplementaryNotice) 'Valid supplementary characters were split or removed from player notices.'
    $supplementaryFormat = 'a' + [char]::ConvertFromUtf32(0xe0001) + 'b'
    Assert-True ($sanitizeNotice.Invoke($null, [object[]]@($supplementaryFormat)) -ceq 'a b') 'A supplementary Unicode format character survived player-notice sanitization.'
    Assert-True ($sanitizeNotice.Invoke($null, [object[]]@('')) -ceq '' -and
        $sanitizeNotice.Invoke($null, [object[]]@('<>&')) -ceq '<>&') 'Empty or plain-text markup player notices were reinterpreted.'

    # All external files and traversal/link fixtures belong to this temporary
    # BepInEx tree. Distributed translation first, exact config overlay last.
    $plainKey = @($english.Keys | Where-Object { $english[$_] -notmatch '\{[0-9]+\}' } | Sort-Object)[0]
    $otherKey = @($english.Keys | Where-Object { $_ -cne $plainKey -and $english[$_] -notmatch '\{[0-9]+\}' } | Sort-Object)[0]
    $baselineKorean = Localized 'Korean' $plainKey
    $baselineOther = Localized 'Korean' $otherKey
    $thirdKey = @($english.Keys | Where-Object { $_ -cne $plainKey -and $_ -cne $otherKey -and $english[$_] -notmatch '\{[0-9]+\}' } | Sort-Object)[0]
    $baselineThird = Localized 'Korean' $thirdKey
    $overridePath = Join-Path $configurationDirectory 'ServerManager.Korean.yml'
    Write-TestTranslation $overridePath ($plainKey + ": 'OVERRIDE_LITERAL_KOREAN'")
    Assert-True ((Reload-TestLanguage 'Korean') -and
        (Localized 'Korean' $plainKey) -ceq 'OVERRIDE_LITERAL_KOREAN' -and
        (Localized 'Korean' $otherKey) -ceq $baselineOther) 'A valid partial override did not replace exactly the selected language key.'
    $invalidTranslations = @(
        ($plainKey + ': [sequence]'),
        ($plainKey + ': {nested: value}'),
        ($plainKey + ": one`n" + $plainKey + ': duplicate'),
        ($plainKey + ': &anchor one'),
        ($plainKey + ': *missing'),
        ($plainKey + ': !!str explicit-tag'),
        ($plainKey + ": first`n---`n" + $otherKey + ': second'),
        'sm_unknown_override_fixture: unknown',
        ($plainKey + ': null'),
        ($plainKey + ': ""'),
        ($argumentKey + ": 'missing required placeholder'"))
    foreach ($invalidYaml in $invalidTranslations) {
        Write-TestTranslation $overridePath $invalidYaml
        Assert-True (-not (Reload-TestLanguage 'Korean') -and
            (Localized 'Korean' $plainKey) -ceq 'OVERRIDE_LITERAL_KOREAN' -and
            (Localized 'Korean' $otherKey) -ceq $baselineOther) 'Invalid external YAML partially replaced or discarded the last valid language table.'
    }
    [IO.File]::Delete($overridePath)
    $distributedCandidates = @(
        (Join-Path $bepinexRoot 'ServerManager.Korean.yml'),
        (Join-Path $bepinexRoot 'plugins\Sighsorry-ServerManager\translations\ServerManager.Korean.yml'),
        (Join-Path $configurationDirectory 'nested\translations\ServerManager.Korean.yml'))
    foreach ($distributedPath in $distributedCandidates) {
        Write-TestTranslation $distributedPath ($plainKey + ': DISTRIBUTED_NAME')
        Assert-True ((Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq 'DISTRIBUTED_NAME' -and
            (Localized 'Korean' $otherKey) -ceq $baselineOther) 'An exact root/plugin/config-nested translation was not discovered and merged over the embedded locale.'
        [IO.File]::Delete($distributedPath)
        Assert-True ((Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq $baselineKorean) 'Removing the only distributed file did not reset to embedded translations.'
    }
    $distributedPath = $distributedCandidates[1]
    Write-TestTranslation $distributedPath ($plainKey + ": DISTRIBUTED_NAME`n" + $otherKey + ': DISTRIBUTED_OTHER')
    Write-TestTranslation $overridePath ($plainKey + ': CONFIG_WINS')
    Assert-True ((Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq 'CONFIG_WINS' -and
        (Localized 'Korean' $otherKey) -ceq 'DISTRIBUTED_OTHER' -and (Localized 'Korean' $thirdKey) -ceq $baselineThird) `
        'The exact config file must overlay the distributed file last while preserving unspecified distributed and embedded keys.'

    # Cached reads must not rescan or apply edits; explicit reload is the only
    # refresh here. Check the compiled cache branch separately below as well.
    $languageTables = $localizerType.GetField('Languages', $allStatic).GetValue($null)
    $cachedKorean = $languageTables['Korean']
    Write-TestTranslation $distributedPath ($plainKey + ": CHANGED_DISTRIBUTED`n" + $otherKey + ': CHANGED_OTHER')
    Write-TestTranslation $overridePath ($plainKey + ': CHANGED_CONFIG')
    foreach ($read in 1..10) {
        Assert-True ((Localized 'Korean' $plainKey) -ceq 'CONFIG_WINS' -and
            (Localized 'Korean' $otherKey) -ceq 'DISTRIBUTED_OTHER' -and [object]::ReferenceEquals($cachedKorean, $languageTables['Korean'])) `
            'Cached TextForLanguage reads performed a filesystem refresh or replaced the cached dictionary.'
    }
    Assert-True ((Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq 'CHANGED_CONFIG' -and
        (Localized 'Korean' $otherKey) -ceq 'CHANGED_OTHER') 'Explicit refresh did not publish valid edits as one complete new language table.'
    $cachedKorean = $languageTables['Korean']
    foreach ($invalidYaml in $invalidTranslations) {
        Write-TestTranslation $distributedPath $invalidYaml
        Write-TestTranslation $overridePath ($plainKey + ': MUST_NOT_PARTIALLY_APPLY')
        Assert-True (-not (Reload-TestLanguage 'Korean') -and [object]::ReferenceEquals($cachedKorean, $languageTables['Korean']) -and
            (Localized 'Korean' $plainKey) -ceq 'CHANGED_CONFIG' -and (Localized 'Korean' $otherKey) -ceq 'CHANGED_OTHER') `
            'Invalid distributed YAML applied the valid config overlay or discarded the last-good language.'
        Write-TestTranslation $distributedPath ($plainKey + ": MUST_NOT_PARTIALLY_APPLY`n" + $otherKey + ': MUST_NOT_PARTIALLY_APPLY')
        Write-TestTranslation $overridePath $invalidYaml
        Assert-True (-not (Reload-TestLanguage 'Korean') -and [object]::ReferenceEquals($cachedKorean, $languageTables['Korean']) -and
            (Localized 'Korean' $plainKey) -ceq 'CHANGED_CONFIG' -and (Localized 'Korean' $otherKey) -ceq 'CHANGED_OTHER') `
            'Invalid config YAML leaked a previously parsed distributed overlay into the active language.'
    }
    Write-TestTranslation $distributedPath ($plainKey + ': AMBIGUOUS_FIRST')
    Write-TestTranslation $distributedCandidates[0] ($plainKey + ': AMBIGUOUS_SECOND')
    Write-TestTranslation $overridePath ($plainKey + ': DUPLICATE_CONFIG_MUST_NOT_APPLY')
    Assert-True (-not (Reload-TestLanguage 'Korean') -and [object]::ReferenceEquals($cachedKorean, $languageTables['Korean']) -and
        (Localized 'Korean' $plainKey) -ceq 'CHANGED_CONFIG' -and (Localized 'Korean' $otherKey) -ceq 'CHANGED_OTHER') `
        'Two distributed files were chosen by traversal order or permitted a partial config overlay.'
    [IO.File]::Delete($distributedCandidates[0])
    [IO.File]::Delete($overridePath)
    Assert-True ((Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq 'AMBIGUOUS_FIRST' -and
        (Localized 'Korean' $otherKey) -ceq $baselineOther) 'Removing the duplicate and config file failed to restore the sole distributed overlay.'
    [IO.File]::Delete($distributedPath)
    foreach ($decoyPath in @((Join-Path $bepinexRoot 'plugins\OtherPlugin.Korean.yml'),
        (Join-Path $bepinexRoot 'plugins\ServerManager.Korean.yaml'),
        (Join-Path $bepinexRoot 'plugins\ServerManager.Korean.yml.old'),
        (Join-Path $bepinexRoot 'plugins\ServerManager.Japanese.yml'))) {
        Write-TestTranslation $decoyPath ($plainKey + ': [invalid-decoy]')
    }
    Assert-True ((Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq $baselineKorean -and
        (Localized 'Korean' $otherKey) -ceq $baselineOther) 'Unrelated filenames/extensions/languages were parsed or removal failed to reset embedded translations.'
    $initialInvalidPath = Join-Path $bepinexRoot 'plugins\ServerManager.German.yml'
    Write-TestTranslation $initialInvalidPath ($plainKey + ': [invalid]')
    Assert-True (-not (Reload-TestLanguage 'German') -and
        (Localized 'German' $plainKey) -ceq (Localized 'English' $plainKey)) 'An initially invalid locale override did not retain the embedded English fallback.'
    $initialDistributed = Join-Path $bepinexRoot 'plugins\ServerManager.French.yml'
    $initialConfig = Join-Path $configurationDirectory 'ServerManager.French.yml'
    Write-TestTranslation $initialDistributed ($plainKey + ': MUST_NOT_APPEAR_ON_FIRST_FAILURE')
    Write-TestTranslation $initialConfig ($otherKey + ': [invalid]')
    Assert-True (-not (Reload-TestLanguage 'French') -and (Localized 'French' $plainKey) -ceq (Localized 'English' $plainKey) -and
        (Localized 'French' $otherKey) -ceq (Localized 'English' $otherKey)) 'First-use config failure retained a partially merged distributed file instead of the complete embedded fallback.'
    Write-TestTranslation (Join-Path $bepinexRoot 'plugins\ServerManager.Swedish.yml') ($plainKey + ': FIRST_DUPLICATE')
    Write-TestTranslation (Join-Path $bepinexRoot 'ServerManager.Swedish.yml') ($plainKey + ': SECOND_DUPLICATE')
    Assert-True (-not (Reload-TestLanguage 'Swedish') -and (Localized 'Swedish' $plainKey) -ceq (Localized 'English' $plainKey)) `
        'First-use distributed ambiguity chose one file instead of the embedded fallback.'
    Write-TestTranslation $distributedPath ($plainKey + ': MUST_NOT_REPLACE_EMBEDDED_KOREAN')
    Write-TestTranslation $overridePath ($otherKey + ': [invalid]')
    $languageTables.Remove('Korean') | Out-Null
    Assert-True (-not (Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq $baselineKorean -and
        (Localized 'Korean' $otherKey) -ceq $baselineOther) 'First-use failure discarded the embedded Korean locale or retained a partial distributed override.'
    [IO.File]::Delete($distributedPath)
    [IO.File]::Delete($overridePath)

    $separateConfig = Join-Path $localizationRoot 'custom-config'
    $separateOverride = Join-Path $separateConfig 'ServerManager.Korean.yml'
    Write-TestTranslation $distributedPath ($plainKey + ": PACK_NAME`n" + $otherKey + ': PACK_OTHER')
    Write-TestTranslation $separateOverride ($plainKey + ': OUTSIDE_CONFIG_WINS')
    Assert-True ((Reload-TestLanguage 'Korean' $bepinexRoot $separateConfig) -and
        (Localized 'Korean' $plainKey) -ceq 'OUTSIDE_CONFIG_WINS' -and (Localized 'Korean' $otherKey) -ceq 'PACK_OTHER') `
        'An explicitly configured directory outside BepInEx was ignored or failed to overlay the discovered pack.'
    [IO.File]::Delete($separateOverride)
    Write-TestTranslation $distributedCandidates[0] ($plainKey + ': ROOT_IS_CONFIG')
    Assert-True ((Reload-TestLanguage 'Korean' ($bepinexRoot + '\') ($bepinexRoot + '\')) -and
        (Localized 'Korean' $plainKey) -ceq 'ROOT_IS_CONFIG' -and (Localized 'Korean' $otherKey) -ceq 'PACK_OTHER') `
        'Config equal to BepInEx root was counted as a duplicate pack or lost its override precedence.'
    [IO.File]::Delete($distributedPath)
    [IO.File]::Delete($distributedCandidates[0])
    Assert-True ((Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq $baselineKorean) 'Alternate explicit-root tests left a cached override behind.'
    Assert-True ((Reload-TestLanguage 'MissingRootFixture' (Join-Path $localizationRoot 'absent-root') (Join-Path $localizationRoot 'absent-config')) -and
        (Localized 'MissingRootFixture' $plainKey) -ceq (Localized 'English' $plainKey)) 'Absent roots did not safely initialize embedded English fallback.'

    # Directory reparse points must never be traversed, even when they contain
    # matching poisoned YAML or form a cycle back into the search root.
    $outsideDirectory = Join-Path $localizationRoot 'outside'
    Write-TestTranslation (Join-Path $outsideDirectory 'ServerManager.Korean.yml') ($plainKey + ': [poisoned-outside]')
    foreach ($linkFixture in @(
        @{ Path = (Join-Path $bepinexRoot 'plugins\poisoned-junction'); Target = $outsideDirectory },
        @{ Path = (Join-Path $bepinexRoot 'plugins\cycle-junction'); Target = $bepinexRoot })) {
        New-Item -ItemType Junction -Path $linkFixture.Path -Target $linkFixture.Target -ErrorAction Stop | Out-Null
        $ownedLocalizationLinks.Add($linkFixture.Path)
        Assert-True (([IO.File]::GetAttributes($linkFixture.Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'The poisoned-directory fixture is not a real reparse point.'
    }
    Assert-True ((Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq $baselineKorean) `
        'Distributed discovery followed a poisoned/cyclic junction instead of skipping the reparse point.'
    Assert-True ((Reload-TestLanguage 'Korean' $bepinexRoot (Join-Path $bepinexRoot 'plugins\poisoned-junction')) -and
        (Localized 'Korean' $plainKey) -ceq $baselineKorean) 'An exact config override beneath a reparse-point directory was followed.'
    foreach ($linkPath in @((Join-Path $bepinexRoot 'plugins\ServerManager.Korean.yml'), $overridePath)) {
        $createdLink = $false
        try {
            New-Item -ItemType SymbolicLink -Path $linkPath -Target (Join-Path $outsideDirectory 'ServerManager.Korean.yml') -ErrorAction Stop | Out-Null
            $createdLink = $true
        }
        catch { Write-Host ('SKIP: File symlink creation is unavailable on this Windows host (' + $_.Exception.GetType().Name + ').') }
        if ($createdLink) {
            $ownedLocalizationLinks.Add($linkPath)
            Assert-True (([IO.File]::GetAttributes($linkPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0 -and
                (Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq $baselineKorean) `
                'A distributed or exact-config file symlink was opened instead of being skipped.'
            [IO.File]::Delete($linkPath)
            $ownedLocalizationLinks.Remove($linkPath) | Out-Null
        }
    }

    Write-TestTranslation $overridePath ($plainKey + ': BEFORE_SCAN_FAILURE')
    Assert-True (Reload-TestLanguage 'Korean') 'The last-good scan-failure fixture did not load.'
    $cachedKorean = $languageTables['Korean']
    $lockedOverride = [IO.File]::Open($overridePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        Assert-True (-not (Reload-TestLanguage 'Korean') -and [object]::ReferenceEquals($cachedKorean, $languageTables['Korean'])) `
            'An unreadable selected file was treated as missing or partially replaced the cached locale.'
    }
    finally { $lockedOverride.Dispose() }
    $invalidRoot = Join-Path $localizationRoot 'not-a-directory'
    Write-TestTranslation $invalidRoot 'ordinary file'
    Assert-True (-not (Reload-TestLanguage 'Korean' $invalidRoot $configurationDirectory) -and
        [object]::ReferenceEquals($cachedKorean, $languageTables['Korean']) -and (Localized 'Korean' $plainKey) -ceq 'BEFORE_SCAN_FAILURE') `
        'A filesystem traversal error silently reset the existing language instead of retaining last-good.'
    $deepRoot = Join-Path $localizationRoot 'deep-root'
    $deepPath = $deepRoot
    foreach ($depth in 1..65) { $deepPath = Join-Path $deepPath 'd' }
    [IO.Directory]::CreateDirectory($deepPath) | Out-Null
    Assert-True (-not (Reload-TestLanguage 'Korean' $deepRoot (Join-Path $localizationRoot 'deep-config')) -and
        [object]::ReferenceEquals($cachedKorean, $languageTables['Korean'])) 'A directory tree deeper than the discovery budget was partially accepted.'
    [IO.Directory]::Delete($deepPath)
    Assert-True ((Reload-TestLanguage 'Korean' $deepRoot (Join-Path $localizationRoot 'deep-config')) -and
        (Localized 'Korean' $plainKey) -ceq $baselineKorean) 'The inclusive depth-64 traversal boundary was rejected.'
    foreach ($limit in @(@{ Name = 'MaximumDirectories'; Value = 8192 }, @{ Name = 'MaximumDirectoryEntries'; Value = 100000 }, @{ Name = 'MaximumDirectoryDepth'; Value = 64 })) {
        Assert-True ($localizerType.GetField($limit.Name, $allStatic).GetRawConstantValue() -eq $limit.Value) `
            ('A documented directory-discovery bound changed: ' + $limit.Name)
    }
    [IO.File]::Delete($overridePath)
    Assert-True ((Reload-TestLanguage 'Korean') -and (Localized 'Korean' $plainKey) -ceq $baselineKorean -and
        (Localized 'Korean' $otherKey) -ceq $baselineOther) 'Removing all applicable external files did not reset the complete embedded locale.'
    foreach ($invalidLanguage in @('../Korean', '..\Korean', 'Korean.yml', ('x' * 65), '')) {
        Assert-True ((Localized $invalidLanguage $plainKey) -ceq (Localized 'English' $plainKey)) 'A language name escaped its exact configuration filename boundary.'
    }
    $boundedText = $localizerType.GetMethod('ReadBoundedText', $allStatic)
    foreach ($invalidBytes in @([byte[]]@(0xff), [byte[]]::new(256 * 1024 + 1))) {
        $invalidStream = [IO.MemoryStream]::new($invalidBytes, $false)
        try { Assert-Throws { $boundedText.Invoke($null, [object[]]@($invalidStream)) } }
        finally { $invalidStream.Dispose() }
    }

    $rejectionType = $plugin.GetType('ServerManager.ProtocolRejection', $true)
    $rejectCodeType = $plugin.GetType('ServerManager.ProtocolRejectCode', $true)
    $withPlayerMessage = $rejectionType.GetMethod('WithPlayerMessage', $allInstance)
    $withAudit = $rejectionType.GetMethod('WithConnectionAudit', $allInstance)
    $source = New-Rejection
    $audit = $withAudit.Invoke($source, [object[]]@('integrity', 'hash_mismatch', 'PRIVATE_AUDIT_DETAIL', 'PRIVATE_PLUGIN_SUMMARY', 'manifest'))
    [string[]]$supplied = @('v1', 'v2')
    $localizedRejection = With-PlayerMessage $audit $argumentKey $supplied
    $supplied[0] = 'MUTATED_CALLER_ARRAY'
    Assert-True (-not [object]::ReferenceEquals($source, $localizedRejection) -and
        $localizedRejection.SafeMessage -ceq $source.SafeMessage -and
        (Get-Hidden $localizedRejection 'PlayerMessageArguments')[0] -ceq 'v1') 'Presentation metadata mutated the English diagnostic or retained a caller-owned argument array.'
    foreach ($auditField in @('AuditCategory', 'AuditReasonCode', 'AuditDetail', 'AuditPluginSummary', 'AuditStage')) {
        Assert-True ((Get-Hidden $audit $auditField) -ceq (Get-Hidden $localizedRejection $auditField)) 'Adding localized presentation changed server-local audit evidence.'
    }
    $reverseCopy = $withAudit.Invoke($localizedRejection, [object[]]@('character', 'same_reason', 'OTHER_PRIVATE_AUDIT', '', 'apply'))
    Assert-True ((Get-Hidden $reverseCopy 'PlayerMessageKey') -ceq $argumentKey -and
        ((Get-Hidden $reverseCopy 'PlayerMessageArguments') -join '|') -ceq 'v1|v2') 'Adding audit metadata dropped the player message key or arguments.'

    $codec = $plugin.GetType('ServerManager.ProtocolPacketCodec', $true)
    $packetType = $plugin.GetType('ServerManager.ProtocolPacket', $true)
    $packetKindType = $plugin.GetType('ServerManager.ProtocolPacketKind', $true)
    $limitsType = $plugin.GetType('ServerManager.ConnectionProtocolLimits', $true)
    $limitsCtor = $limitsType.GetConstructors()[0]
    $limitArguments = [object[]]::new($limitsCtor.GetParameters().Length)
    $limitParameters = $limitsCtor.GetParameters()
    for ($index = 0; $index -lt $limitArguments.Length; ++$index) {
        $limitArguments[$index] = [Management.Automation.LanguagePrimitives]::ConvertTo($limitParameters[$index].DefaultValue, $limitParameters[$index].ParameterType)
    }
    $limits = $limitsCtor.Invoke($limitArguments)
    $sessionId = [byte[]](1..16); $nonce = [byte[]](21..52)
    $createReject = $codec.GetMethod('CreateReject', $allStatic)
    $decodeReject = $codec.GetMethod('DecodeRejectPayload', $allStatic)
    $decodePacket = $codec.GetMethods($allStatic) | Where-Object { $_.Name -eq 'TryDecode' -and $_.GetParameters()[0].ParameterType.Name -eq 'ZPackage' }
    $roundtrip = Encode-And-DecodeReject $localizedRejection
    Assert-True ($roundtrip.Rejection.Code -eq $source.Code -and $roundtrip.Rejection.SafeMessage -ceq $source.SafeMessage -and
        (Get-Hidden $roundtrip.Rejection 'PlayerMessageKey') -ceq $argumentKey -and
        ((Get-Hidden $roundtrip.Rejection 'PlayerMessageArguments') -join '|') -ceq 'v1|v2') 'Typed rejection metadata did not round-trip independently of the English diagnostic.'
    $wireText = [Text.Encoding]::UTF8.GetString($roundtrip.Package.GetArray())
    Assert-True (-not $wireText.Contains('PRIVATE_AUDIT_DETAIL') -and -not $wireText.Contains('PRIVATE_PLUGIN_SUMMARY')) 'Server-local audit details leaked into a localized rejection packet.'
    $maximumKey = 'sm_' + ('a' * 61)
    $maximumUtf8Argument = ([string][char]0xac00 * 85) + 'x'
    Assert-True ([Text.Encoding]::UTF8.GetByteCount($maximumUtf8Argument) -eq 256) 'The multibyte wire-boundary fixture is not exactly 256 UTF-8 bytes.'
    $maximumMetadata = With-PlayerMessage $source $maximumKey ([string[]]@(
        $maximumUtf8Argument, $maximumUtf8Argument, $maximumUtf8Argument, $maximumUtf8Argument))
    $maximumRoundtrip = Encode-And-DecodeReject $maximumMetadata
    Assert-True ((Get-Hidden $maximumRoundtrip.Rejection 'PlayerMessageKey').Length -eq 64 -and
        (Get-Hidden $maximumRoundtrip.Rejection 'PlayerMessageArguments').Count -eq 4 -and
        (Get-Hidden $maximumRoundtrip.Rejection 'PlayerMessageArguments')[3] -ceq $maximumUtf8Argument -and
        $maximumRoundtrip.Packet.Payload.Length -eq (12 + [Text.Encoding]::UTF8.GetByteCount($source.SafeMessage) + 1112)) 'Exact maximum key, argument count, UTF-8 byte length, or total metadata bounds failed to round-trip.'
    foreach ($invalid in @(
        @{ Key = 'not_namespaced'; Values = [string[]]@() },
        @{ Key = 'sm_UPPER'; Values = [string[]]@() },
        @{ Key = 'sm_' + ('a' * 62); Values = [string[]]@() },
        @{ Key = ''; Values = [string[]]@('not-empty') },
        @{ Key = $argumentKey; Values = [string[]]@('a', 'b', 'c', 'd', 'e') },
        @{ Key = $argumentKey; Values = [string[]]@(('x' * 257)) },
        @{ Key = $argumentKey; Values = [string[]]@($maximumUtf8Argument + 'y') })) {
        $badRejection = With-PlayerMessage $source $invalid.Key $invalid.Values
        Assert-Throws { $createReject.Invoke($null, [object[]]@($sessionId, $nonce, $badRejection, $limits)) }
        $badPayload = New-RawRejectPayload ([int]$source.Code) $invalid.Key $invalid.Values
        Assert-Throws { $decodeReject.Invoke($null, [object[]]@((New-RejectPacket $badPayload), $limits)) }
    }
    foreach ($malformedPayload in @(
        (New-RawRejectPayload 0 $argumentKey),
        (New-RawRejectPayload 999999 $argumentKey),
        (New-RawRejectPayload ([int]$source.Code) $argumentKey @() ([byte[]]@(0))))) {
        Assert-Throws { $decodeReject.Invoke($null, [object[]]@((New-RejectPacket $malformedPayload), $limits)) }
    }
    [byte[]]$invalidUtf8Payload = New-RawRejectPayload ([int]$source.Code) $argumentKey ([string[]]@('x'))
    $invalidUtf8Payload[$invalidUtf8Payload.Length - 1] = 0xff
    Assert-Throws { $decodeReject.Invoke($null, [object[]]@((New-RejectPacket $invalidUtf8Payload), $limits)) }
    [byte[]]$negativeCountPayload = New-RawRejectPayload ([int]$source.Code) $argumentKey
    [Array]::Copy([BitConverter]::GetBytes([int]-1), 0, $negativeCountPayload, $negativeCountPayload.Length - 4, 4)
    Assert-Throws { $decodeReject.Invoke($null, [object[]]@((New-RejectPacket $negativeCountPayload), $limits)) }

    $playerMessages = $plugin.GetType('ServerManager.PlayerConnectionMessages', $true)
    $fromRejection = $playerMessages.GetMethod('FromRejection', $allStatic)
    foreach ($menuGuideKey in $menuGuideKeys) {
        $menuRejection = With-PlayerMessage (New-Rejection 'InternalError' 'PRIVATE_REMOTE_DIAGNOSTIC') $menuGuideKey ([string[]]@())
        $menuRejectionText = $fromRejection.Invoke($null, [object[]]@($menuRejection))
        Assert-True ($menuRejectionText -ceq (Localized 'English' 'sm_connection_failed') -and
            $menuRejectionText -cne (Localized 'English' $menuGuideKey)) `
            ('Adding a local menu guide expanded the allowed remote rejection display surface: ' + $menuGuideKey)
    }
    foreach ($eventKey in $eventKeys) {
        $slots = @([Regex]::Matches($english[$eventKey], '\{([0-9]+)\}') | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique)
        $arity = if ($slots.Count) { [int]($slots | Measure-Object -Maximum).Maximum + 1 } else { 0 }
        [string[]]$eventArguments = if ($arity -eq 0) { @() } elseif ($arity -eq 1) { @('PRIVATE_EVENT_ARGUMENT') } else { @('PRIVATE_EVENT_ARGUMENT', 'PRIVATE_TARGET_ARGUMENT') }
        $eventRejection = With-PlayerMessage (New-Rejection 'InternalError' 'PRIVATE_REMOTE_DIAGNOSTIC') $eventKey $eventArguments
        $eventRejectionText = $fromRejection.Invoke($null, [object[]]@($eventRejection))
        Assert-True ($eventRejectionText -ceq (Localized 'English' 'sm_connection_failed') -and
            -not $eventRejectionText.Contains('PRIVATE_EVENT_ARGUMENT') -and -not $eventRejectionText.Contains('PRIVATE_REMOTE_DIAGNOSTIC')) `
            ('An event key or auxiliary event label was accepted as a remote rejection message: ' + $eventKey)
    }
    $securityExpected = Localized 'English' 'sm_security_ended'
    foreach ($securityCode in @('CheatDetected', 'DetectionProtocolViolation')) {
        $security = With-PlayerMessage (New-Rejection $securityCode 'PRIVATE_SECURITY_REASON SHA256=abcdef C:\private\file.dll') `
            'sm_character_profile_limit' ([string[]]@('LEAK_IF_HONORED'))
        $securityRendered = $fromRejection.Invoke($null, [object[]]@($security))
        Assert-True ($securityRendered -ceq $securityExpected -and -not $securityRendered.Contains('LEAK_IF_HONORED')) 'Security rejection disclosed details or honored unrelated player-message metadata.'
    }
    $knownArgumentRejection = With-PlayerMessage (New-Rejection 'CharacterTransferFailed' 'HIDDEN_TECHNICAL_DETAIL') `
        'sm_character_profile_limit' ([string[]]@($unsafeArgument))
    $knownMessage = $fromRejection.Invoke($null, [object[]]@($knownArgumentRejection))
    Assert-True ($knownMessage -ceq (Localized 'English' 'sm_character_profile_limit' ([string[]]@($unsafeArgument))) -and
        -not $knownMessage.Contains('HIDDEN_TECHNICAL_DETAIL')) 'Typed player presentation lost its canonical localized template or copied the technical diagnostic.'
    $visibleKnownMessage = $sanitizeNotice.Invoke($null, [object[]]@($knownMessage))
    Assert-True ($visibleKnownMessage.Contains('<b>$sm_secret {1}</b>') -and
        -not $visibleKnownMessage.Contains([string][char]0x202e)) 'Client argument rendering removed literal markup or reinterpreted placeholders/localization tokens.'
    foreach ($unknownMetadata in @(
        @{ Key = 'sm_unknown_wire_fixture'; Values = [string[]]@('SECRET_ARGUMENT') },
        @{ Key = 'sm_character_profile_limit'; Values = [string[]]@() })) {
        $unknown = With-PlayerMessage (New-Rejection 'InternalError' 'PRIVATE_DIAGNOSTIC') $unknownMetadata.Key $unknownMetadata.Values
        $unknownRendered = $fromRejection.Invoke($null, [object[]]@($unknown))
        Assert-True ($unknownRendered -ceq (Localized 'English' 'sm_connection_failed') -and
            -not $unknownRendered.Contains('PRIVATE_DIAGNOSTIC') -and -not $unknownRendered.Contains('SECRET_ARGUMENT')) 'Unknown player keys or wrong argument counts copied diagnostic/argument data instead of a safe fallback.'
    }
    $runtimeType.GetField('_connectionLimits', $allStatic).SetValue($null, $limits)
    $englishDecoder = $runtimeType.GetMethod('DecodeRejectMessage', $allStatic)
    $playerDecoder = $runtimeType.GetMethod('DecodePlayerRejectMessage', $allStatic)
    $typedRoundtrip = Encode-And-DecodeReject $knownArgumentRejection
    Assert-True ($englishDecoder.Invoke($null, [object[]]@($typedRoundtrip.Packet)).Contains('HIDDEN_TECHNICAL_DETAIL') -and
        $playerDecoder.Invoke($null, [object[]]@($typedRoundtrip.Packet)) -ceq $knownMessage) 'Localized UI decoding changed the independent English diagnostic path.'

    foreach ($exceptionName in @('CharacterProtocolException', 'CharacterStorageException')) {
        $exceptionType = $plugin.GetType('ServerManager.' + $exceptionName, $true)
        $exception = $exceptionType.GetConstructor(@([string])).Invoke(@('ORIGINAL_CHARACTER_DIAGNOSTIC'))
        [string[]]$exceptionArguments = @('5')
        $typedException = $exceptionType.GetMethod('WithPlayerMessage', $allInstance).Invoke($exception,
            [object[]]@('sm_character_profile_limit', $exceptionArguments))
        $exceptionArguments[0] = 'MUTATED'
        Assert-True ($typedException.Message -ceq 'ORIGINAL_CHARACTER_DIAGNOSTIC' -and
            (Get-Hidden $typedException 'PlayerMessageKey') -ceq 'sm_character_profile_limit' -and
            (Get-Hidden $typedException 'PlayerMessageArguments')[0] -ceq '5') 'Character exception presentation mutated its diagnostic or retained caller-owned argument data.'
    }

    # Check the real emitted program, not the dependency shims: all formatted
    # text must reach TMP directly, and private game members must never become
    # direct field/method accesses through publicized compilation references.
    $definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($pluginPath)
    try {
        $localizerIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.PlayerLocalizer'
        foreach ($method in $localizerIL.Methods | Where-Object HasBody) {
            foreach ($instruction in $method.Body.Instructions) {
                if ($instruction.Operand -is [Mono.Cecil.FieldReference]) {
                    Assert-True (-not ($instruction.Operand.DeclaringType.Name -eq 'Localization' -and
                        $instruction.Operand.Name -eq 'm_translations')) 'The localizer directly accesses a private game translations field.'
                }
                if ($instruction.Operand -is [Mono.Cecil.MethodReference]) {
                    $called = $instruction.Operand
                    Assert-True (-not (($called.DeclaringType.Name -eq 'Localization' -and $called.Name -in @('AddWord', 'Localize')) -or
                        ($called.DeclaringType.Name -eq 'FejdStartup' -and $called.Name -in @('Start', 'SetupGui')))) 'The localizer directly calls private game methods or recursively localizes formatted text.'
                    Assert-True ($called.Name -ne 'PatchAll') 'Localization loading creates its own patch lifecycle.'
                }
            }
        }
        $textForLanguageIL = $localizerIL.Methods | Where-Object Name -eq 'TextForLanguage'
        $textInstructions = @($textForLanguageIL.Body.Instructions)
        $cacheLookup = $textInstructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'TryGetValue' } | Select-Object -First 1
        $reloadCall = $textInstructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'ReloadLanguageLocked' } | Select-Object -First 1
        $cachedBranch = $textInstructions | Where-Object { $_.Offset -gt $cacheLookup.Offset -and $_.Offset -lt $reloadCall.Offset -and
            $_.OpCode.Code -in @('Brtrue', 'Brtrue_S', 'Brfalse', 'Brfalse_S') } | Select-Object -First 1
        $cacheCondition = @($textInstructions | Where-Object { $_.Offset -gt $cacheLookup.Offset -and
            $_.Offset -lt $cachedBranch.Offset -and $_.OpCode.Code -ne 'Nop' })
        # Debug spills !TryGetValue through one Boolean local. Accept only a
        # matching store/load pair, then require the branch's exact polarity.
        if ($cacheCondition.Count -ge 2 -and $cacheCondition[-2].OpCode.Name -like 'stloc*' -and
            $cacheCondition[-1].OpCode.Name -ceq ($cacheCondition[-2].OpCode.Name -replace '^stloc', 'ldloc') -and
            $cacheCondition[-1].Operand -eq $cacheCondition[-2].Operand) {
            $cacheCondition = @($cacheCondition | Select-Object -First ($cacheCondition.Count - 2))
        }
        $conditionSignature = ($cacheCondition | ForEach-Object { $_.OpCode.Name }) -join ';'
        $cacheHitBranches = ($conditionSignature -eq '' -and $cachedBranch.OpCode.Code -in @('Brtrue', 'Brtrue_S')) -or
            ($conditionSignature -eq 'ldc.i4.0;ceq' -and $cachedBranch.OpCode.Code -in @('Brfalse', 'Brfalse_S'))
        Assert-True ($null -ne $cacheLookup -and $null -ne $reloadCall -and $cacheHitBranches -and
            $cachedBranch.Operand -is [Mono.Cecil.Cil.Instruction] -and $cachedBranch.Operand.Offset -gt $reloadCall.Offset) `
            'The actual compiled cached-language path does not bypass language loading and recursive discovery.'
        $automaticReloadMethods = @($localizerIL.Methods | Where-Object Name -in @('Initialize', 'ReloadCurrentLanguage', 'TextForLanguage')) +
            @(($localizerIL.NestedTypes | Where-Object Name -eq 'LanguageChangedPatch').Methods | Where-Object Name -eq 'Postfix')
        foreach ($automaticReloadMethod in $automaticReloadMethods) {
            $pathCalls = @($automaticReloadMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'BepInEx.Paths' } | ForEach-Object { $_.Operand.Name })
            Assert-True ($pathCalls -contains 'get_BepInExRootPath' -and $pathCalls -contains 'get_ConfigPath') `
                ('An automatic localization load failed to use both real BepInEx and config roots: ' + $automaticReloadMethod.Name)
        }
        $panel = $definition.MainModule.Types | Where-Object Name -eq 'ConnectionErrorPanelPresentation'
        $panelCalls = @($panel.Methods | Where-Object HasBody | ForEach-Object {
            $_.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object Operand
        })
        Assert-True (@($panelCalls | Where-Object { $_.DeclaringType.Name -eq 'Localization' -and $_.Name -eq 'Localize' }).Count -eq 0 -and
            @($panelCalls | Where-Object { $_.Name -eq 'set_text' -and $_.DeclaringType.FullName -like 'TMPro.*' }).Count -gt 0) 'The final connection panel reinterprets literal arguments as localization tokens or does not set TMP text directly.'
        $runtimeIL = $definition.MainModule.Types | Where-Object FullName -eq 'ServerManager.ServerManagerRuntime'
        $failClientIL = $runtimeIL.Methods | Where-Object Name -eq 'FailClient'
        $sanitizeCall = $failClientIL.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'SanitizePlayerNotice' } | Select-Object -First 1
        $visibleAssignment = $failClientIL.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'set_ConnectionError' } | Select-Object -First 1
        Assert-True ($null -ne $sanitizeCall -and $null -ne $visibleAssignment -and
            $sanitizeCall.Offset -lt $visibleAssignment.Offset) 'The player-visible connection error is assigned before argument rendering is sanitized.'
    }
    finally { $definition.Dispose() }
    Write-Host "Player localization smoke passed ($script:assertions assertions; embedded parity, language fallback, safe arguments and typed rejection privacy)."
}
finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($fixtureAssemblyResolver)
    $resolvedRoot = [IO.Path]::GetFullPath($localizationRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-True ($resolvedRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot).StartsWith('smlocalization-', [StringComparison]::Ordinal)) 'Unsafe localization fixture cleanup target.'
    foreach ($linkPath in $ownedLocalizationLinks) {
        $resolvedLink = [IO.Path]::GetFullPath($linkPath)
        Assert-True ($resolvedLink.StartsWith($resolvedRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Unsafe localization link cleanup target.'
        $attributes = [IO.File]::GetAttributes($resolvedLink)
        Assert-True (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'Refusing to unlink a fixture path that stopped being a reparse point.'
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { [IO.Directory]::Delete($resolvedLink) }
        else { [IO.File]::Delete($resolvedLink) }
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
