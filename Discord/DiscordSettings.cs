using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ServerManager.Configuration;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ServerManager.Discord;

// Server-only settings, deliberately separate from the modpack's ordinary cfg.
// Use YAML nodes rather than runtime type deserialization; no tags or aliases.
internal sealed class DiscordSettings
{
    private const int MaximumFileBytes = 128 * 1024;
    // Selectable webhook filters, including grouped operator summaries. This
    // does not rename the underlying audit/API or in-game event kinds.
    internal static readonly HashSet<string> PublicEvents = new(StringComparer.Ordinal)
    {
        "server.status", "server.saved", "server.announcement",
        "player.connection", "chat.shout", "raid.status", "player.death",
        "boss.killed", "moderation.action", "command.executed",
        "security.alert", "security.admin_bypass",
        "character.validation", "character.shadow_stalled",
        "character.revision_observed", "connection.rejected"
    };

    // Map only for delivery selection. Preserve the original event for card
    // wording, attribution and dedupe: a response is not another detection.
    internal static string? GetWebhookEventFilter(string kind) => kind switch
    {
        "server.ready" or "server.shutdown" => "server.status",
        "player.login" or "player.leave" => "player.connection",
        "raid.started" or "raid.ended" => "raid.status",
        "combat.pvp_kill" => "player.death",
        "character.save_rejected" or "character.validation_observed" => "character.validation",
        "security.detection" or "security.response" => "security.alert",
        // These names are selectors, never new event kinds that callers can emit.
        "server.status" or "player.connection" or "raid.status" or
            "character.validation" or "security.alert" => null,
        _ => PublicEvents.Contains(kind) ? kind : null
    };

    public bool BotEnabled { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public HashSet<string> GuildIds { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> CommandChannelIds { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> ChatChannelIds { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> AdminUserIds { get; set; } = new(StringComparer.Ordinal);
    public List<DiscordWebhookRoute> WebhookRoutes { get; set; } = new();
    internal int ChatRelayChannelCount => CommandChannelIds.Union(ChatChannelIds).Count();

    internal static DiscordSettings Load(string dataRoot, Action<string> log)
    {
        string path = Path.Combine(dataRoot, "discord.yml");
        Directory.CreateDirectory(dataRoot);
        if (!File.Exists(path))
        {
            FileStream? created = null;
            try
            {
                created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another creator may have won. Never truncate or overwrite its file.
            }
            if (created != null)
            {
                using (created)
                {
                    // Do not hide our own write/flush failures as a creation race.
                    byte[] defaults = new UTF8Encoding(false).GetBytes(DefaultYaml);
                    created.Write(defaults, 0, defaults.Length);
                }
            }
        }

        return ParseSettings(ReadBoundedUtf8(path), log, strict: false);
    }

    internal static string ReadReloadText(string dataRoot)
    {
        // Reload never creates a directory/example or rewrites the user's file.
        try { return ReadBoundedUtf8(Path.Combine(dataRoot, "discord.yml")); }
        catch (FileNotFoundException) { throw Invalid("discord.yml is missing"); }
        catch (DirectoryNotFoundException) { throw Invalid("discord.yml is missing"); }
        catch (UnauthorizedAccessException) { throw Invalid("discord.yml could not be read"); }
        catch (IOException)
        { throw Invalid("discord.yml could not be read"); }
    }

    internal static DiscordSettings ParseForReload(string yaml) =>
        ParseSettings(yaml, _ => { }, strict: true);

    private static DiscordSettings ParseSettings(string yaml, Action<string> log, bool strict)
    {
        if (yaml == null) throw Invalid("YAML text is missing");
        if (yaml.Length > MaximumFileBytes) throw Invalid("file exceeds 128 KiB");
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(yaml) > MaximumFileBytes)
                throw Invalid("file exceeds 128 KiB");
        }
        catch (EncoderFallbackException) { throw Invalid("file must be valid UTF-8"); }

        YamlStream document = new();
        try
        {
            using StringReader reader = new(yaml);
            document.Load(new BoundedYamlParser(reader, 16384, 16, Invalid));
        }
        catch (YamlException exception)
        {
            // Parser messages and inner exceptions can contain the full token or URL.
            throw Invalid("invalid YAML syntax or duplicate key at line " +
                exception.Start.Line.ToString(CultureInfo.InvariantCulture) + ", column " +
                exception.Start.Column.ToString(CultureInfo.InvariantCulture));
        }
        catch (ArgumentException)
        {
            throw Invalid("invalid YAML structure or duplicate key");
        }
        if (document.Documents.Count != 1)
            throw Invalid("exactly one YAML document is required");
        Dictionary<string, YamlNode> root = Map(document.Documents[0].RootNode, "root",
            "bot", "webhooks");
        DiscordSettings settings = new();

        try { settings.ReadBot(root, strict); }
        catch (InvalidDataException exception) when (!strict)
        {
            settings.BotEnabled = false;
            settings.ChatChannelIds.Clear();
            log("Discord bot disabled. " + exception.Message);
        }

        settings.ReadWebhooks(root, log, strict);
        // Existing files are never rewritten: retain comments/order and never persist env secrets.
        return settings;
    }

    private void ReadBot(Dictionary<string, YamlNode> root, bool strict)
    {
        Dictionary<string, YamlNode> bot = Section(root, "bot", "enabled", "token", "guild_ids",
            "admin_channel_ids", "chat_channel_ids", "admin_user_ids");
        // An absent bot block still supports webhook-only configurations.
        BotEnabled = root.ContainsKey("bot") && Flag(bot, "enabled");
        BotToken = ResolveSecret("SERVERMANAGER_DISCORD_BOT_TOKEN", Text(bot, "token"));
        GuildIds = IdSet(List(bot, "guild_ids"), "bot.guild_ids");
        CommandChannelIds = IdSet(List(bot, "admin_channel_ids"), "bot.admin_channel_ids");
        ChatChannelIds = IdSet(List(bot, "chat_channel_ids"), "bot.chat_channel_ids");
        AdminUserIds = IdSet(List(bot, "admin_user_ids"), "bot.admin_user_ids");
        if (strict && (BotToken.Length > 512 || BotToken.Any(c => c <= 32 || c >= 127)))
            throw Invalid("bot has an invalid supplied ASCII bot token");
        // Empty channel sets are a valid idle bot. In particular, clearing the last
        // chat channel must disable forwarding rather than reject a live reload.
        if (BotEnabled && (GuildIds.Count == 0 ||
            BotToken.Length == 0 || BotToken.Length > 512 || BotToken.Any(c => c <= 32 || c >= 127)))
            throw Invalid("bot requires at least one valid guild ID and an ASCII bot token");
    }

    private void ReadWebhooks(Dictionary<string, YamlNode> root, Action<string> log, bool strict)
    {
        if (!root.TryGetValue("webhooks", out YamlNode node)) return;
        if (node is not YamlSequenceNode routes || routes.Children.Count > 10)
        {
            if (strict) throw Invalid("webhooks must be a list of at most 10 routes");
            log("Discord webhooks disabled: webhooks must be a list of at most 10 routes.");
            return;
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        for (int index = 0; index < routes.Children.Count; index++)
        {
            // Use a trusted numeric index in errors; a user-supplied name may itself contain a secret.
            string location = "webhooks[" + (index + 1).ToString(CultureInfo.InvariantCulture) + "]";
            try
            {
                Dictionary<string, YamlNode> route = Map(routes.Children[index], location,
                    "name", "enabled", "url", "events", "username", "avatar_url", "anonymous_prefix", "language", "include_steam_id");
                bool enabled = Flag(route, "enabled");
                bool includeSteamId = route.ContainsKey("include_steam_id") && Flag(route, "include_steam_id");
                string name = Text(route, "name", "Webhook " + (index + 1).ToString(CultureInfo.InvariantCulture));
                string url = Text(route, "url");
                string username = Text(route, "username", "ServerManager");
                string avatar = Text(route, "avatar_url");
                string anonymousPrefix = ReadAnonymousPrefix(route, location);
                string language = ReadWebhookLanguage(route, location);
                HashSet<string> events = new(List(route, "events"), StringComparer.Ordinal);
                if (name.Length == 0 || name.Length > 80 || name.Any(char.IsControl) ||
                    username.Length == 0 || username.Length > 80 || username.Any(char.IsControl) ||
                    events.Any(kind => !PublicEvents.Contains(kind)) ||
                    (avatar.Length > 0 && (!Uri.TryCreate(avatar, UriKind.Absolute, out Uri avatarUri) ||
                        avatarUri.Scheme != "https" || avatarUri.UserInfo.Length > 0 || avatar.Length > 2048)))
                    throw Invalid(location + " has invalid display or event settings");
                if (!enabled)
                {
                    if (strict && url.Length > 0 && !IsWebhook(url))
                        throw Invalid(location + " has an invalid supplied Discord webhook URL");
                    continue;
                }
                if (!IsWebhook(url) || events.Count == 0)
                    throw Invalid(location + " requires a valid Discord webhook URL and nonempty events");
                if (!names.Add(name)) throw Invalid(location + " has a duplicate enabled route name");
                WebhookRoutes.Add(new DiscordWebhookRoute
                {
                    Name = name, Url = url, Events = events, Username = username, AvatarUrl = avatar,
                    AnonymousPrefix = anonymousPrefix, Language = language, IncludeSteamId = includeSteamId
                });
            }
            catch (InvalidDataException exception) when (!strict)
            {
                log(location + " disabled. " + exception.Message);
            }
        }
    }

    internal bool HasSameBotSettings(DiscordSettings other)
    {
        if (other == null || BotEnabled != other.BotEnabled || BotToken != other.BotToken ||
            !GuildIds.SetEquals(other.GuildIds) || !CommandChannelIds.SetEquals(other.CommandChannelIds) ||
            !ChatChannelIds.SetEquals(other.ChatChannelIds) ||
            !AdminUserIds.SetEquals(other.AdminUserIds)) return false;
        return true;
    }

    internal bool HasSameWebhookSettings(DiscordSettings other)
    {
        if (other == null || WebhookRoutes.Count != other.WebhookRoutes.Count)
            return false;
        for (int index = 0; index < WebhookRoutes.Count; index++)
        {
            DiscordWebhookRoute left = WebhookRoutes[index], right = other.WebhookRoutes[index];
            if (left.Name != right.Name || left.Url != right.Url || left.Username != right.Username ||
                left.AvatarUrl != right.AvatarUrl || left.AnonymousPrefix != right.AnonymousPrefix || left.Language != right.Language ||
                left.IncludeSteamId != right.IncludeSteamId ||
                !left.Events.SetEquals(right.Events)) return false;
        }
        return true;
    }

    internal static bool IsValidWebhookLanguage(string? language) =>
        !string.IsNullOrWhiteSpace(language) && language!.Length <= 64 &&
        language.All(ch => ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z' ||
            ch >= '0' && ch <= '9' || ch == ' ' || ch == '_' || ch == '-');

    private static string ReadWebhookLanguage(Dictionary<string, YamlNode> route, string location)
    {
        if (!route.TryGetValue("language", out YamlNode node)) return "English";
        string language = Scalar(node, location + ".language");
        if (!IsValidWebhookLanguage(((YamlScalarNode)node).Value))
            throw Invalid(location + " has an invalid language identifier");
        return language;
    }

    private static string ReadAnonymousPrefix(Dictionary<string, YamlNode> route, string location)
    {
        if (!route.TryGetValue("anonymous_prefix", out YamlNode node)) return string.Empty;
        string prefix = Scalar(node, location + ".anonymous_prefix");
        string raw = ((YamlScalarNode)node).Value!;
        // Validate before trimming so trailing controls cannot be hidden by
        // normalization. Preserve printable Unicode, including paired surrogates.
        for (int index = 0; index < raw.Length; ++index)
        {
            char value = raw[index];
            bool pair = char.IsHighSurrogate(value) && index + 1 < raw.Length && char.IsLowSurrogate(raw[index + 1]);
            if (char.IsSurrogate(value) && !pair || char.IsControl(value))
                throw Invalid(location + " has an invalid anonymous prefix");
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(raw, index);
            if (category == UnicodeCategory.Format || category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator)
                throw Invalid(location + " has an invalid anonymous prefix");
            if (pair) ++index;
        }
        if (prefix.Length > 32) throw Invalid(location + " anonymous prefix exceeds 32 characters");
        return prefix;
    }

    private static Dictionary<string, YamlNode> Section(Dictionary<string, YamlNode> parent,
        string name, params string[] keys) => parent.TryGetValue(name, out YamlNode value)
        ? Map(value, name, keys) : new Dictionary<string, YamlNode>(StringComparer.Ordinal);

    private static Dictionary<string, YamlNode> Map(YamlNode node, string location, params string[] keys)
    {
        if (node is not YamlMappingNode mapping) throw Invalid(location + " must be a mapping");
        Dictionary<string, YamlNode> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            if (entry.Key is not YamlScalarNode key || key.Value == null ||
                !keys.Contains(key.Value) || result.ContainsKey(key.Value))
                throw Invalid(location + " contains an unknown or duplicate key");
            result.Add(key.Value, entry.Value);
        }
        return result;
    }

    private static string Scalar(YamlNode node, string key)
    {
        if (node is not YamlScalarNode scalar || scalar.Value == null ||
            (scalar.Style == ScalarStyle.Plain &&
                (scalar.Value.Length == 0 || scalar.Value == "~" ||
                 string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase))))
            throw Invalid(key + " must be a scalar value, not null");
        return scalar.Value.Trim();
    }

    private static string Text(Dictionary<string, YamlNode> map, string key, string fallback = "") =>
        map.TryGetValue(key, out YamlNode value) ? Scalar(value, key) : fallback;

    private static bool Flag(Dictionary<string, YamlNode> map, string key)
    {
        if (!map.TryGetValue(key, out YamlNode node)) return true;
        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain ||
            (scalar.Value != "true" && scalar.Value != "false"))
            throw Invalid(key + " must be true or false");
        return scalar.Value == "true";
    }

    private static string[] List(Dictionary<string, YamlNode> map, string key, string[]? fallback = null)
    {
        if (!map.TryGetValue(key, out YamlNode node)) return fallback ?? Array.Empty<string>();
        if (node is not YamlSequenceNode sequence || sequence.Children.Count > 256)
            throw Invalid(key + " must be a list of at most 256 values");
        return sequence.Children.Select(value => Scalar(value, key)).ToArray();
    }

    internal static bool IsId(string value) => value.Length > 0 && value.Length <= 20 &&
        value[0] != '0' && value.All(c => c >= '0' && c <= '9') &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static HashSet<string> IdSet(IEnumerable<string> values, string key)
    {
        HashSet<string> ids = new(values, StringComparer.Ordinal);
        if (ids.Count > 256 || ids.Any(id => !IsId(id))) throw Invalid(key + " contains an invalid Discord ID");
        return ids;
    }

    internal static bool IsWebhook(string url)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
            uri.Scheme != "https" || !string.Equals(uri.Host, "discord.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return false;
        string[] parts = uri.AbsolutePath.Split('/');
        return parts.Length == 5 && parts[1] == "api" && parts[2] == "webhooks" &&
               IsId(parts[3]) && parts[4].Length > 0 &&
               parts[4].All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                   (c >= '0' && c <= '9') || c == '-' || c == '_');
    }

    private static string ResolveSecret(string environmentName, string fallback) =>
        (Environment.GetEnvironmentVariable(environmentName) ?? fallback).Trim();

    private static string ReadBoundedUtf8(string path)
    {
        byte[] bytes = new byte[MaximumFileBytes + 1];
        int count = 0;
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0)
                count += read;
        }
        if (count > MaximumFileBytes) throw Invalid("file exceeds 128 KiB");
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes, 0, count);
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }
        catch (DecoderFallbackException) { throw Invalid("file must be valid UTF-8"); }
    }

    private static InvalidDataException Invalid(string reason) =>
        new("Discord YAML configuration: " + reason + ".");

    private const string DefaultYaml = @"# Server-only UTF-8. Keep bot tokens and webhook URLs private.
# Valid edits reload automatically; invalid edits keep the active settings.
# Bot changes may reconnect Discord; the game server stays running.
# Existing files are not rewritten. Webhooks work without a bot.
# enabled defaults to true. Set unused components to false or remove them.
# Fill enabled bot credentials/guild IDs and every enabled webhook URL before reloading.
bot:
  enabled: true
  token: '' # SERVERMANAGER_DISCORD_BOT_TOKEN overrides this value.
  guild_ids: [] # Discord server IDs; at least one is required when the bot is enabled.
  admin_user_ids: [] # Discord user IDs, not Steam IDs; full command/RCON access.
  admin_channel_ids: [] # Commands and admin-only chat; displayed as Admin.
  chat_channel_ids: [] # Public chat; displayed with Discord names.

# Plain messages become in-game shouts. For either channel list, enable
# Message Content Intent in the Discord Developer Portal.
# Admin channels require admin_user_ids, including for chat.
# Public-channel access uses Discord permissions. Admin rules win on overlap.
# These lists apply across all guild_ids, linked to this one Valheim server.

# Available webhook events (exact names; no wildcards):
#   server.status - World ready for players or server shutting down normally.
#   server.saved - World saved to disk; pending character saves add a warning.
#   server.announcement - Server announcement text.
#   player.connection - Remote player joined (including first-time joins) or disconnected.
#   chat.shout - In-game shout chat.
#   raid.status - Raid started or ended, with verified center coordinates when available.
#   player.death - All reported player deaths, including PvP; keeps cause-specific wording.
#   boss.killed - Boss defeated.
#   moderation.action - Administrative kick, ban or unban request.
#   command.executed - Administrative command result, including failures.
#   security.alert - Cheat/stat-limit findings, detector diagnostics and response outcomes.
#   security.admin_bypass - Administrator policy exemption.
#   character.validation - Character save rejection or observe-only validation warning.
#   character.shadow_stalled - Character uploads delayed.
#   character.revision_observed - Unusual skill gain or progress.
#   connection.rejected - Connection refused or aborted, including mod/character issues.
#
# Grouped filters keep separate outcome messages; they do not combine alerts.
# Give enabled routes unique names, then add a URL and events.
# Disable or remove unused examples before reloading.
# Duplicate routes to one destination can duplicate notifications.
# Example: events: [chat.shout, player.connection]
# English and Korean are bundled; other languages need ServerManager.<Language>.yml under server BepInEx.
# anonymous_prefix: '' keeps names; 'Anonymous' uses Anonymous 1, etc.
# Anonymity does not remove names that people type into chat or announcements.
# include_steam_id: true adds verified Steam IDs to joins/leaves only; anonymous routes always hide them.
# This is per webhook URL, independent of bot.admin_channel_ids. Use a private destination for IDs.
webhooks:
  - name: Server status
    enabled: true
    url: ''
    events:
      - server.status
      - server.saved
      - chat.shout
      - player.connection
      - raid.status
      - player.death
      - boss.killed
    language: English
    username: ServerManager # Optional webhook display name.
    avatar_url: '' # Optional webhook image URL.
    anonymous_prefix: ''
    include_steam_id: false

  # Use a private destination: bot admin permissions do not protect webhook audiences.
  # These summaries are not proof of cheating or a full audit-log mirror.
  - name: Moderation
    enabled: true
    url: ''
    events:
      - server.announcement
      - moderation.action
      - command.executed
      - connection.rejected
      - character.revision_observed
      - character.validation
      - character.shadow_stalled
      - security.admin_bypass
      - security.alert
    anonymous_prefix: ''
    include_steam_id: false

  - name: examplehook
    enabled: true
    url: ''
    events: [server.status, server.saved, chat.shout]
    language: Korean
    avatar_url: ''
    anonymous_prefix: ''

  - name: examplehook2
    enabled: true
    url: ''
    events:
      - server.status
      - server.saved
      - chat.shout
";
}

internal sealed class DiscordWebhookRoute
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Username { get; set; } = "ServerManager";
    public string AvatarUrl { get; set; } = string.Empty;
    public string AnonymousPrefix { get; set; } = string.Empty;
    public string Language { get; set; } = "English";
    public bool IncludeSteamId { get; set; }
    public HashSet<string> Events { get; set; } = new(StringComparer.Ordinal);
}
