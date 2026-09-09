param(
    [string]$Configuration = 'Debug',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
$assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw $Message }
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Plugin.cs') -Raw
$bepInExPath = Join-Path $GamePath 'BepInEx\core\BepInEx.dll'
Assert-True (Test-Path -LiteralPath $bepInExPath) 'Real BepInEx configuration assembly is required.'

# Execute production bindings against real BepInEx without launching Unity.
$propertyMatches = [regex]::Matches($pluginSource,
    'internal static ConfigEntry<[^>]+> \w+ \{ get; private set; \} = null!;')
Assert-True ($propertyMatches.Count -eq 5) 'CFG must expose exactly four branding properties and one client notification toggle.'
$properties = ($propertyMatches | ForEach-Object { $_.Value.Replace(' = null!;', '') }) -join [Environment]::NewLine
$methodStart = $pluginSource.IndexOf('    private void BindConfiguration()')
$classEnd = $pluginSource.LastIndexOf('}')
Assert-True ($methodStart -ge 0 -and $classEnd -gt $methodStart) 'Could not extract production bindings.'
$methods = $pluginSource.Substring($methodStart, $classEnd - $methodStart)
$probeSource = @"
using System;
using BepInEx.Configuration;
namespace ServerManagerConfigurationSmoke
{
    public sealed class Probe
    {
        $properties
        public ConfigFile Config { get; private set; }
        public Probe(string path)
        {
            Config = new ConfigFile(path, false);
            Config.SaveOnConfigSet = false;
            BindConfiguration();
            Config.Save();
        }
        public ConfigEntryBase Get(string key)
        { return Config[new ConfigDefinition("1 - Client", key)]; }
        public ConfigEntryBase GetNotification()
        { return Get("Show Event Notifications"); }
        public bool NotificationsVisible() { return ShowEventNotifications.Value; }
        $methods
    }
}
"@
$compilerReferences = @($bepInExPath)
if ($PSVersionTable.PSEdition -eq 'Core') {
    [Reflection.Assembly]::LoadFrom($bepInExPath) | Out-Null
    $compilerReferences += @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
} else {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($bepInExPath)) | Out-Null
}
Add-Type -TypeDefinition $probeSource -ReferencedAssemblies $compilerReferences
$expected = @'
Server Address||5
Server Password||4
Server Button Text|Start Modded Valheim Server|3
Logo Path|https://i.ibb.co/23XsG7tz/download.png|2
Show Event Notifications|True|1
'@
function Assert-ClientSurface($Probe) {
    Assert-True ($Probe.Config.Count -eq 5) 'ConfigFile must contain exactly five active client-local entries.'
    Assert-True (@($Probe.Config.Keys | Where-Object { $_.Section -cne '1 - Client' }).Count -eq 0) 'Every active setting must belong to the single 1 - Client section.'
    Assert-True (@($Probe.Config.Keys | Where-Object { $_.Key -ceq 'Enabled' }).Count -eq 0) 'The client branding Enabled toggle must not be active.'
    $orderedKeys = foreach ($row in ($expected -split '\r?\n')) {
        $columns = $row.Split('|')
        $entry = $Probe.Get($columns[0])
        Assert-True ($null -ne $entry) ('Missing client cfg key: ' + $columns[0])
        $tags = @($entry.Description.Tags | Where-Object { $_.GetType().Name -eq 'ConfigurationManagerAttributes' })
        Assert-True ($tags.Count -eq 1) ('The Configuration Manager order metadata is missing or duplicated: ' + $columns[0])
        $orderField = $tags[0].GetType().GetField('Order', [Reflection.BindingFlags]'Public, Instance')
        Assert-True ($null -ne $orderField -and $orderField.FieldType -eq [Nullable[int]]) ('Order must be a public nullable-int field readable by Configuration Manager: ' + $columns[0])
        $order = $orderField.GetValue($tags[0])
        Assert-True ($null -ne $order -and [int]$order -eq [int]$columns[2]) ('Wrong Configuration Manager descending order: ' + $columns[0])
        [pscustomobject]@{ Key = $columns[0]; Order = [int]$order }
    }
    Assert-True ((@($orderedKeys | Sort-Object Order -Descending | ForEach-Object Key) -join '|') -ceq
        'Server Address|Server Password|Server Button Text|Logo Path|Show Event Notifications') 'Configuration Manager must display the five client settings in the requested order.'
}
function Assert-ClientDefaults($Probe) {
    foreach ($row in ($expected -split '\r?\n')) {
        $columns = $row.Split('|')
        $entry = $Probe.Get($columns[0])
        Assert-True ([Convert]::ToString($entry.BoxedValue, [Globalization.CultureInfo]::InvariantCulture) -ceq $columns[1]) ('Unexpected default: ' + $columns[0])
    }
}
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testDirectory = Join-Path $temporaryRoot ('ServerManager-Configuration-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $configPath = Join-Path $testDirectory 'sighsorry.ServerManager.cfg'
    $probe = New-Object ServerManagerConfigurationSmoke.Probe($configPath)
    Assert-ClientSurface $probe
    Assert-ClientDefaults $probe
    $generated = Get-Content -LiteralPath $configPath -Raw
    $sections = [regex]::Matches($generated, '(?m)^\[([^\r\n]+)\]')
    Assert-True ($sections.Count -eq 1 -and $sections[0].Groups[1].Value -ceq '1 - Client') 'Fresh client CFG must serialize only the 1 - Client section.'
    Assert-True ([regex]::Matches($generated, '(?m)^[^#\[\r\n][^\r\n]* = ').Count -eq 5) 'Serialized fresh client CFG has wrong key count.'
    Assert-True ($generated -notmatch '(?m)^Enabled = ') 'Fresh client CFG must not serialize an Enabled toggle.'
    $notification = $probe.GetNotification()
    Assert-True ($null -ne $notification -and $notification.BoxedValue -eq $true -and $probe.NotificationsVisible()) 'Event notifications must default to true.'
    Assert-True ($notification.Description.Description -match 'client-local') 'Notification setting must explicitly remain client-local.'
    $notification.BoxedValue = $false
    Assert-True (-not $probe.NotificationsVisible()) 'A real in-memory ConfigEntry update must immediately expose false without reloading CFG.'
    Assert-True ($probe.Get('Server Button Text').BoxedValue -ceq 'Start Modded Valheim Server' -and
        $probe.Get('Logo Path').BoxedValue -ceq 'https://i.ibb.co/23XsG7tz/download.png') 'Notification changes must not alter branding settings.'
    $notification.BoxedValue = $true
    Assert-True ($probe.NotificationsVisible()) 'A real in-memory ConfigEntry update must expose true again without rebinding.'
    $notification.BoxedValue = $false
    $probe.Config.Save()
    foreach ($removed in @('Maximum Profiles Per Account', 'Backups To Keep', 'Forbidden Item Prefabs',
        'Cheat Detection Response', 'Maximum Health', 'Maximum Stamina', 'Maximum Eitr',
        'Maximum Carry Weight', 'Maximum Damage', 'Stat Limit Response', 'Incoming Save Policy',
        'Player logging', 'Handshake Timeout Seconds', 'Maximum Character Bytes')) {
        Assert-True (-not $generated.Contains($removed)) ('Server/fixed policy returned to CFG: ' + $removed)
    }
    Assert-True ($pluginSource -notmatch 'DetectionResponseValues|ServerSync|ConfigSync') 'CFG must not keep a server-policy adapter or remote config authority.'
    Assert-True ($pluginSource -notmatch 'EnableClientBranding') 'The removed Enabled toggle must not retain a config property or binding.'
    Assert-True ($probe.Get('Server Address').Description.Description -match 'client-local') 'Branding must remain client-local.'
    Assert-True ($probe.Get('Server Password').Description.Description -match 'never synchronized') 'Branding password must not be synchronized.'
    Assert-True ($probe.Get('Server Address').Description.Description -match 'Alt.*never bypasses server security') 'Menu Alt must not bypass security.'

    # Deliberately orphaned old server and client keys are not migration inputs.
    $orphanPath = Join-Path $testDirectory 'orphaned.cfg'
    [IO.File]::WriteAllText($orphanPath, @'
[1 - Server]
Maximum Profiles Per Account = 128
Backups To Keep = 1
[2 - Character validation]
Forbidden Item Prefabs = Wood
Incoming Save Policy = Disabled
[3 - Anti-cheat]
Cheat Detection Response = Off
Maximum Health = 999999
Maximum Stamina = 999999
Maximum Eitr = 999999
Maximum Carry Weight = 999999
Maximum Damage = 999999999
Stat Limit Response = Off
[4 - Client branding]
Enabled = true
Server Address = example.invalid:2456
Server Password = fixture-only
Server Button Text = Fixture Server
Logo Path = fixture.png
[5 - Client notifications]
Show Event Notifications = false
'@)
    $orphan = New-Object ServerManagerConfigurationSmoke.Probe($orphanPath)
    Assert-ClientSurface $orphan
    Assert-ClientDefaults $orphan

    # The new section is authoritative even when conflicting legacy sections remain.
    $persistedPath = Join-Path $testDirectory 'persisted-client.cfg'
    [IO.File]::WriteAllText($persistedPath, @'
[1 - Client]
Enabled = false
Server Address = configured.example.invalid:2467
Server Password = configured-fixture-only
Server Button Text = Configured Server
Logo Path = plugins/Fixture/logo.png
Show Event Notifications = false
[4 - Client branding]
Enabled = true
Server Address = legacy.example.invalid:2456
Server Password = legacy-fixture-only
Server Button Text = Legacy Server
Logo Path = legacy.png
[5 - Client notifications]
Show Event Notifications = true
'@)
    $persisted = New-Object ServerManagerConfigurationSmoke.Probe($persistedPath)
    Assert-ClientSurface $persisted
    Assert-True ($persisted.Get('Server Address').BoxedValue -ceq 'configured.example.invalid:2467') 'The new client section address must be respected.'
    Assert-True ($persisted.Get('Server Password').BoxedValue -ceq 'configured-fixture-only') 'The new client section password must be respected.'
    Assert-True ($persisted.Get('Server Button Text').BoxedValue -ceq 'Configured Server') 'The new client section button must be respected.'
    Assert-True ($persisted.Get('Logo Path').BoxedValue -ceq 'plugins/Fixture/logo.png') 'The new client section logo must be respected.'
    Assert-True (-not $persisted.GetNotification().BoxedValue -and -not $persisted.NotificationsVisible()) 'The new client section notification-off value must override the true default without reading legacy settings.'
    $persisted.GetNotification().BoxedValue = $true
    Assert-True ($persisted.NotificationsVisible() -and $persisted.Get('Server Button Text').BoxedValue -ceq 'Configured Server') 'Notification visibility must change independently of persisted branding values.'
    $probe.Config.Save()
    $reopened = New-Object ServerManagerConfigurationSmoke.Probe($configPath)
    Assert-ClientSurface $reopened
    Assert-True (-not $reopened.GetNotification().BoxedValue -and -not $reopened.NotificationsVisible()) 'Saving and reopening the real CFG must preserve the notification off value.'
    Write-Host "Configuration surface smoke passed: $assertions assertions, five client-local keys, one section, ordered metadata, real BepInEx ConfigFile."
} finally {
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolvedTestDirectory.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Parent $resolvedTestDirectory) -ne $temporaryRoot -or
        (Split-Path -Leaf $resolvedTestDirectory) -notmatch '^ServerManager-Configuration-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected configuration test directory.'
    }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}
