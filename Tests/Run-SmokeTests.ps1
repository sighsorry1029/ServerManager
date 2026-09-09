param(
    [string]$Configuration = "Debug",
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

function Assert-LastExitCode {
    param(
        [string]$Operation,
        [int]$ExitCode
    )

    if ($ExitCode -ne 0) {
        throw "$Operation failed with exit code $ExitCode."
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot "ServerManager.csproj"
# These tests instantiate game types using default interface methods or Span
# APIs unavailable on the Windows PowerShell 5 Desktop CLR. Keep all other
# tests on their existing Windows PowerShell runtime.
$powerShell7 = Get-Command pwsh.exe -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $powerShell7) {
    throw "PowerShell 7 (pwsh.exe on PATH) is required for common-command, chat and client-branding smoke tests."
}
$powerShell7Major = & $powerShell7.Source -NoProfile -Command '$PSVersionTable.PSVersion.Major'
Assert-LastExitCode "PowerShell 7 version check" $LASTEXITCODE
if ([int]$powerShell7Major -lt 7) {
    throw "Common-command, chat and client-branding smoke tests require PowerShell 7 or newer."
}

if (-not $SkipBuild) {
    Write-Host "==> Building ServerManager ($Configuration)"
    $bepInExPath = Join-Path $GamePath "BepInEx"
    $managedPath = Join-Path $GamePath "valheim_Data\Managed"
    & dotnet msbuild `
        $projectPath `
        -restore `
        /t:Build `
        "/p:Configuration=$Configuration" `
        "/p:GamePath=$GamePath" `
        "/p:ValheimGamePath=$GamePath" `
        "/p:BepInExPath=$bepInExPath" `
        "/p:CorlibPath=$managedPath" `
        /v:minimal `
        /nodeReuse:false
    Assert-LastExitCode "ServerManager build" $LASTEXITCODE
}

$smokeTests = @(
    [pscustomobject]@{
        Name = "Optional mod catalog metadata and framing"
        File = "OptionalModCatalogSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Optional mod catalog publication lifecycle"
        File = "OptionalModPublicationSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Optional mod menu query lifecycle"
        File = "OptionalModQuerySmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Steam friend lobby optional mod query lifecycle"
        File = "OptionalModLobbyQuerySmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Conflicting plugin load guard"
        File = "PluginIncompatibilitySmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Server schedule configuration and claims"
        File = "ServerScheduleSmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Durable server schedule journal"
        File = "ServerScheduleJournalSmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Optional Upgrade World maintenance bridge"
        File = "UpgradeWorldScheduleSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Server schedule lifecycle and execution"
        File = "ServerScheduleRuntimeSmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Shared server console execution"
        File = "ServerConsoleExecutorSmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Shared event messages and overlay tags"
        File = "EventMessageSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Explicit environmental death causes"
        File = "DeathCauseSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Character creation administrator authority"
        File = "CharacterCreationAdminSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Managed character poison persistence"
        File = "PoisonPersistenceSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Player-facing localization"
        File = "PlayerLocalizationSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Administrator stat-cap bypass"
        File = "AdminStatCapSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Item-data presets and live reload"
        File = "ItemDataPresetsSmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Custom-data item grants"
        File = "CharacterItemDataGrantSmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Common administration commands"
        File = "CommonCommandsSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Administrative command transport"
        File = "AdminCommandTransportSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Character administration"
        File = "CharacterAdminSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Discord common command adapters"
        File = "DiscordCommonCommandsSmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Configuration surface"
        File = "ConfigurationSurfaceSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Character audit"
        File = "CharacterAuditSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Raid event centers"
        File = "RaidEventsSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Connection rejection audit and protocol"
        File = "ConnectionRejectionSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Server YAML settings"
        File = "ServerSettingsSmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Server player capacity and Steam advertisement"
        File = "ServerPlayerCapacitySmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Character starter items"
        File = "CharacterStarterItemsSmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Integrity"
        File = "IntegritySmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Scoped library protocol"
        File = "DependencyProtocolSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Asynchronous client manifest preparation"
        File = "ManifestPreparationSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Targeted managed dependency manifest"
        File = "DependencyManifestSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Character semantic policy split"
        File = "CharacterSemanticPolicySplitSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Character semantic validation"
        File = "CharacterSemanticValidationSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Character save pipeline"
        File = "CharacterSavePipelineSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Operational kick"
        File = "OperationalKickSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Character inventory fast path"
        File = "CharacterInventoryFastPathSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Character shadow checkpoint"
        File = "CharacterShadowCheckpointSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Backup-only character capture"
        File = "BackupOnlySmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Local host character service"
        File = "LocalHostCharacterServiceSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Local host character lifecycle"
        File = "LocalHostCharacterSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "SteamID-directory native character storage"
        File = "CharacterDirectStorageSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Fragment expiry"
        File = "FragmentExpirySmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Anti-cheat"
        File = "RuntimeSecuritySmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Direct RPC socket compatibility"
        File = "BufferedWorldSocketSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Reservation-bound Steam identity"
        File = "SteamSessionIdentitySmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Steam callback registration and authentication lifecycle"
        File = "SteamAuthenticationSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Authenticated character sessions through socket wrappers"
        File = "CharacterAuthenticatedSessionSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Client menu branding"
        File = "ClientMenuBrandingSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "HTTPS menu logo and last-good cache"
        File = "LogoUrlSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Runtime member access"
        File = "RuntimeMemberAccessSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Player telemetry log writer"
        File = "PlayerTelemetryLogWriterSmoke.ps1"
        NeedsGamePath = $false
    },
    [pscustomobject]@{
        Name = "Player activity integration"
        File = "PlayerActivityIntegrationSmoke.ps1"
        NeedsGamePath = $true
    },
    [pscustomobject]@{
        Name = "Chat logging"
        File = "ChatLoggingSmoke.ps1"
        NeedsGamePath = $true
    }
)

$smokeTests += @(
    [pscustomobject]@{ Name = "Discord integration"; File = "DiscordIntegrationSmoke.ps1"; NeedsGamePath = $true },
    [pscustomobject]@{ Name = "Discord settings"; File = "DiscordSettingsSmoke.ps1"; NeedsGamePath = $true },
    [pscustomobject]@{ Name = "Discord live reload"; File = "DiscordReloadSmoke.ps1"; NeedsGamePath = $false },
    [pscustomobject]@{ Name = "Discord commands"; File = "DiscordCommandsSmoke.ps1"; NeedsGamePath = $true },
    [pscustomobject]@{ Name = "Discord ordinary chat"; File = "DiscordChatSmoke.ps1"; NeedsGamePath = $false },
    [pscustomobject]@{ Name = "Discord Gateway"; File = "DiscordGatewaySmoke.ps1"; NeedsGamePath = $false },
    [pscustomobject]@{ Name = "Discord transport"; File = "DiscordTransportSmoke.ps1"; NeedsGamePath = $false },
    [pscustomobject]@{ Name = "Discord actual game Mono"; File = "DiscordMonoSmoke.ps1"; NeedsGamePath = $true }
)

foreach ($smokeTest in $smokeTests) {
    $scriptPath = Join-Path $PSScriptRoot $smokeTest.File
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "Smoke test is missing: $scriptPath"
    }

    Write-Host "==> Running $($smokeTest.Name) smoke"
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $scriptPath,
        "-Configuration",
        $Configuration
    )
    if ($smokeTest.NeedsGamePath) {
        $arguments += @("-GamePath", $GamePath)
    }

    $shellPath = if ($smokeTest.File -in @("CommonCommandsSmoke.ps1", "ChatLoggingSmoke.ps1", "ClientMenuBrandingSmoke.ps1")) {
        $powerShell7.Source
    }
    else { "powershell.exe" }
    & $shellPath @arguments
    Assert-LastExitCode "$($smokeTest.Name) smoke test" $LASTEXITCODE
}

Write-Host "All ServerManager smoke tests passed."
