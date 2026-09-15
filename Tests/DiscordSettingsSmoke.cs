// Source-linked production YAML settings, isolated in a disposable helper process.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ServerManager.Discord;
using YamlDotNet.RepresentationModel;

internal static class DiscordSettingsSmoke
{
    private const string SafeWebhook = "https://discord.com/api/webhooks/123/offline_Test-token1";
    private const string OtherWebhook = "https://discord.com/api/webhooks/321/offline_Test-token2";
    private const string Secret = "SECRET_SENTINEL_DO_NOT_LOG";
    private const string EnvA = "SERVERMANAGER_SETTINGS_TEST_WEBHOOK_A";
    private const string EnvB = "SERVERMANAGER_SETTINGS_TEST_WEBHOOK_B";
    private static readonly string[] OperatorKinds =
    {
        "security.alert", "security.admin_bypass", "character.validation",
        "character.shadow_stalled", "character.revision_observed", "connection.rejected"
    };
    private static readonly string[] GroupedSourceKinds =
    {
        "server.ready", "server.shutdown", "player.login", "player.leave", "raid.started", "raid.ended",
        "combat.pvp_kill", "character.save_rejected", "character.validation_observed",
        "security.detection", "security.response"
    };
    private static readonly string[] SyntheticEventFilters =
    {
        "server.status", "player.connection", "raid.status", "character.validation", "security.alert", "cron.executed"
    };
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private static string _root = "";
    private static int _cases, _checks;

    private static int Main(string[] args)
    {
        try
        {
            _root = args.Single();
            // Child-process-only variables; never modify the launching shell or user environment.
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_BOT_TOKEN", null);
            Environment.SetEnvironmentVariable(EnvA, null);
            Environment.SetEnvironmentVariable(EnvB, null);
            for (int i = 1; i <= 10; i++) Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_WEBHOOK_" + i, null);
            Defaults(); Valid(); AnonymousPrefixes(); WebhookLanguages(); SteamIdOptions(); GuildSettings(); RemovedBotSchema(); OperatorRoutes(); SourceEventFilters(); RemovedWebhookEvents(); ChatSettings(); BadBot(); RemovedAuthorizationKeys(); RemovedCommandLimits(); BadRoutes(); RemovedWebhookEnvironments(); RemovedPrivacyAndCollections(); Syntax(); EnvironmentOverrides();
            StrictReload(); PartialReload(); ReloadReads(); Comparators();
            Console.WriteLine("PASS: DiscordSettings startup/strict reload/comparators (" + _checks + " assertions, " + _cases + " isolated configs).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Defaults()
    {
        string root = NextRoot();
        var startupLogs = new List<string>();
        DiscordSettings settings = DiscordSettings.Load(root, startupLogs.Add);
        Check(!settings.BotEnabled && settings.WebhookRoutes.Count == 0 && startupLogs.Count == 0,
            "Disabled examples load without missing-credential warnings");
        Safe(string.Join("\n", startupLogs));
        Check(new DiscordWebhookRoute().AnonymousPrefix == "", "Route model defaults to original player names");
        Check(new DiscordWebhookRoute().Language == "English", "Route model defaults to English event messages");
        Check(!new DiscordWebhookRoute().IncludeSteamId, "Route model defaults to withholding Steam IDs");
        foreach (string property in new[] { "RelayShouts", "IncludeCoordinates", "IncludeInventory", "IncludePluginList" })
            Check(typeof(DiscordSettings).GetProperty(property) == null && typeof(DiscordSettings).GetField(property) == null,
                "Removed privacy option has no instance, constant property or field: " + property);
        Check(settings.BotToken == "" && settings.GuildIds.Count == 0 && settings.CommandChannelIds.Count == 0 && settings.AdminUserIds.Count == 0, "Default credentials/guilds/authorization empty");
        Check(typeof(DiscordSettings).GetProperty("GuildId") == null, "Removed single-guild property is absent");
        Check(settings.ChatChannelIds.Count == 0 && settings.ChatRelayChannelCount == 0, "Default Discord-to-game chat disabled");
        foreach (string property in new[] { "AdminRoleIds", "AllowedCommands", "Grants", "EnableRcon" })
            Check(typeof(DiscordSettings).GetProperty(property) == null, "Removed authorization property is absent: " + property);
        Check(typeof(DiscordSettings).Assembly.GetType("ServerManager.Discord.DiscordGrant") == null,
            "The removed per-command grant model is absent");
        string path = Path.Combine(root, "discord.yml");
        string generated = File.ReadAllText(path, Utf8);
        string[] rootKeys = System.Text.RegularExpressions.Regex.Matches(generated, @"(?m)^([a-z_]+):")
            .Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups[1].Value).ToArray();
        Check(rootKeys.SequenceEqual(new[] { "bot", "webhooks" }), "Generated YAML has exactly the two supported root sections");
        string[] botKeys = generated.Replace("\r\n", "\n").Split(new[] { "bot:\n" }, StringSplitOptions.None)[1]
            .Split(new[] { "\n\n" }, StringSplitOptions.None)[0].Split('\n').Where(line => line.StartsWith("  ", StringComparison.Ordinal))
            .Select(line => line.Trim().Split(':')[0]).ToArray();
        Check(botKeys.SequenceEqual(new[] { "enabled", "token", "guild_ids", "admin_user_ids", "admin_channel_ids", "chat_channel_ids" }),
            "Generated bot mapping places administrator users before the two channel lists");
        Check(Regex.Matches(generated, @"(?m)^\s+enabled: true\s*$").Count == 0 &&
            Regex.Matches(generated, @"(?m)^\s+enabled: false\s*$").Count == 5,
            "Generated bot and all four webhook examples are disabled");
        Check(!generated.Contains("guild_id:") && !generated.Contains("command_channel_ids:") && !generated.Contains("\nchat:"),
            "Generated YAML never advertises the removed single-guild or split-chat schema");
        Check(!generated.Contains("rcon:") && !generated.Contains("command_timeout_seconds") &&
            !generated.Contains("minimum_interval_seconds") && !generated.Contains("maximum_output_characters"),
            "Generated YAML contains no removed command limit keys");
        Check(!generated.Contains("url_env") && generated.Contains("SERVERMANAGER_DISCORD_BOT_TOKEN overrides this value"),
            "Generated YAML removes webhook environment indirection while retaining the bot-token environment override");
        Check(generated.Contains("anonymous_prefix: '' keeps names") && generated.Contains("'Anonymous' uses Anonymous 1") &&
            generated.Contains("Anonymity does not remove names that people type into chat or announcements"),
            "Generated route examples document opt-in anonymity without a top-level switch");
        Check(generated.Contains("include_steam_id: true adds verified Steam IDs to joins/leaves only") &&
            generated.Contains("anonymous routes always hide them"),
            "Generated comments explain Steam-ID opt-in scope and mandatory anonymous-route redaction");
        Check(!generated.Contains("privacy:") && !generated.Contains("relay_shouts") && !generated.Contains("include_coordinates") &&
            !generated.Contains("include_inventory") && !generated.Contains("include_plugin_list"), "Generated YAML contains no removed privacy settings");
        Check(generated.Contains("Available webhook events (exact names; no wildcards)") &&
            generated.Contains("Example: events: [chat.shout, player.connection]"),
            "Generated comments explain explicit event selection for independent routes");
        Check(generated.Contains("English and Korean are bundled") &&
            generated.Contains("ServerManager.<Language>.yml under server BepInEx"),
            "Generated comments distinguish bundled translations from server-provided language files");
        Check(generated.Contains("Deleted routes are removed") &&
            generated.Contains("Duplicate routes to one destination can duplicate notifications") &&
            generated.Contains("Fill enabled bot credentials/guild IDs and every enabled webhook URL before reloading"),
            "Generated comments explain duplicate delivery and credential-free inactivity without relaxing reload requirements");
        Check(!generated.Contains("server.started") && !generated.Contains("player.first_join") &&
            new[] { "server.status", "player.connection", "raid.status" }.All(generated.Contains),
            "Generated defaults advertise the three grouped lifecycle filters and no removed lifecycle selectors");
        Check(GroupedSourceKinds.All(kind => !generated.Contains("#   " + kind + " - ")) && generated.Contains("character.validation") &&
            generated.Contains("security.alert"), "Generated YAML uses grouped selectors without advertising removed raw-kind aliases");
        Check(generated.Contains("bot:") && generated.Contains("webhooks:") && generated.Contains("#") &&
            !generated.Contains("grants:") && !generated.Contains("admin_role_ids:") && !generated.Contains("allowed_commands:") &&
            !generated.Replace("\r\n", "\n").Contains("rcon:\n  enabled:"), "Commented YAML excludes all removed authorization keys");
        Check(generated.Contains("Server-only UTF-8") && generated.Contains("Keep bot tokens and webhook URLs private") &&
            generated.Contains("Webhooks work without a bot"), "Default comments retain credential and server-only deployment guidance");
        Check(generated.Contains("Discord user IDs, not Steam IDs; full command/RCON access"),
            "Default comments distinguish account ID types and disclose full console authority");
        Check(generated.Contains("Plain messages become in-game shouts. For either channel list, enable") &&
            generated.Contains("Message Content Intent in the Discord Developer Portal"),
            "Default chat example documents shout delivery and the intent required by either channel list");
        Check(generated.Contains("Public-channel access uses Discord permissions. Admin rules win on overlap") &&
            generated.Contains("Admin channels require admin_user_ids, including for chat"),
            "Default comments preserve public/admin authorization distinctions and administrator-first overlap");
        Check(generated.Contains("These lists apply across all guild_ids, linked to this one Valheim server") &&
            generated.Contains("Discord server IDs; at least one is required when the bot is enabled"),
            "Default comments explain multi-guild shared configuration and the enabled bot's guild requirement");
        Check(generated.Contains("Commands and admin-only chat; displayed as Admin") &&
            generated.Contains("Public chat; displayed with Discord names"), "Default comments distinguish admin and public chat presentation");
        Check(generated.Contains("Valid blocks reload automatically; invalid blocks keep their last valid settings") &&
            generated.Contains("may reconnect Discord; the game server stays running") &&
            generated.Contains("Existing files are not rewritten"), "Default comments describe live reload, last-good settings and existing-file preservation");
        Check(generated.Contains("name: Moderation") && OperatorKinds.All(generated.Contains) &&
            generated.Contains("Use a private destination: bot admin permissions do not protect webhook audiences") &&
            generated.Contains("These summaries are not proof of cheating or a full audit-log mirror"),
            "Moderation route preserves sensitive-audience guidance and evidence limitations");
        Check(generated.Replace("\r\n", "\n").Contains(
            "  # Use a private destination: bot admin permissions do not protect webhook audiences.\n" +
            "  # These summaries are not proof of cheating or a full audit-log mirror.\n" +
            "  - name: Moderation\n"),
            "Sensitive-audience guidance belongs directly to Moderation, not the public examples");
        GeneratedEventCatalog(generated);
        GeneratedRouteDefaults(generated);
        Check(!File.Exists(Path.Combine(root, "discord.cfg")), "No CFG generated");
        byte[] original = File.ReadAllBytes(path);
        Check(!DiscordSettings.Load(root, _ => { }).BotEnabled, "Generated blank credentials remain inactive on repeated startup load");
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "Generated example not rewritten on reload");
        Check(!DiscordSettings.ParseForReload(generated).BotEnabled, "Disabled template is reloadable");
        Check(DiscordSettings.ReadReloadText(root) == generated && original.SequenceEqual(File.ReadAllBytes(path)),
            "Reload reads preserve the generated four-route example byte-for-byte even when its blank credentials reject activation");
        var omittedLogs = new List<string>();
        settings = Load("{}\n", omittedLogs);
        Check(!settings.BotEnabled && settings.ChatChannelIds.Count == 0 && settings.WebhookRoutes.Count == 0 && omittedLogs.Count == 0,
            "An absent bot block preserves the credential-free disabled default");
        settings = Load("bot: {enabled: false}\nwebhooks: []\n");
        Check(!settings.BotEnabled && settings.WebhookRoutes.Count == 0,
            "An existing explicit disabled bot and empty route list remain disabled without rewrite");
        root = NextRoot(); Directory.CreateDirectory(root);
        const string legacy = "[Bot]\nEnabled = true\nToken = legacy-secret-not-used\n";
        File.WriteAllText(Path.Combine(root, "discord.cfg"), legacy, Utf8);
        settings = DiscordSettings.Load(root, _ => { });
        Check(!settings.BotEnabled && settings.BotToken == "", "Legacy CFG never enables bot");
        Check(File.ReadAllText(Path.Combine(root, "discord.cfg"), Utf8) == legacy, "Legacy CFG preserved untouched");
        Check(File.Exists(Path.Combine(root, "discord.yml")), "YAML example generated alongside legacy CFG");
        string commented = "# preserve user formatting\r\n" + new Fixture().Render().Replace("\n", "\r\n") + "# trailing comment\r\n";
        Check(Load(commented).BotEnabled, "User CRLF/comments load without rewrite");
    }

    private static bool HasCompleteEventCatalog(string yaml)
    {
        var documented = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.StartsWith("#   ", StringComparison.Ordinal)) continue;
            Match entry = Regex.Match(line, @"^#   ([a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+) - (.+)$");
            if (!entry.Success) return false;
            string kind = entry.Groups[1].Value;
            string description = entry.Groups[2].Value.Trim();
            if (!documented.Add(kind) || !DiscordSettings.PublicEvents.Contains(kind) ||
                description.Length < 2 || !description.EndsWith(".", StringComparison.Ordinal) ||
                !description.Any(character => character >= 'A' && character <= 'Z' || character >= 'a' && character <= 'z') ||
                description.Any(char.IsControl)) return false;
        }
        return documented.SetEquals(DiscordSettings.PublicEvents);
    }

    private static void GeneratedEventCatalog(string generated)
    {
        Check(HasCompleteEventCatalog(generated),
            "Generated comments describe every supported webhook event exactly once, with an English description");
        string[] eventLines = generated.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.StartsWith("#   ", StringComparison.Ordinal)).ToArray();
        Check(eventLines.Length == 18 && eventLines.Length == DiscordSettings.PublicEvents.Count,
            "Generated comments list all eighteen actual selectable webhook filters");
        foreach (string line in eventLines)
        {
            Check(!HasCompleteEventCatalog(generated.Replace(line, "")), "Catalog checker detects a missing selectable event");
            Check(!HasCompleteEventCatalog(generated + "\n" + line + "\n"), "Catalog checker detects duplicate documented events");
            string noDescription = line.Substring(0, line.IndexOf(" - ", StringComparison.Ordinal) + 3);
            Check(!HasCompleteEventCatalog(generated.Replace(line, noDescription)), "Catalog checker rejects entries without descriptions");
        }
        Check(!HasCompleteEventCatalog(generated + "\n#   event.unknown - Not supported.\n"), "Catalog checker rejects an unknown event");
        Check(!HasCompleteEventCatalog(generated + "\n#   server.started - Removed event.\n"), "Catalog checker rejects a removed lifecycle filter");
        Check(eventLines.Any(line => line.StartsWith("#   chat.shout - ", StringComparison.Ordinal)),
            "The supported shout filter is discoverable in the generated event catalog");
    }

    private static void GeneratedRouteDefaults(string generated)
    {
        var document = new YamlStream();
        using (var reader = new StringReader(generated)) document.Load(reader);
        var root = (YamlMappingNode)document.Documents.Single().RootNode;
        var routes = (YamlSequenceNode)root.Children[new YamlScalarNode("webhooks")];
        string[] names = { "Server status", "Moderation", "examplehook", "examplehook2" };
        string[][] events =
        {
            new[] { "server.status", "server.saved", "chat.shout", "player.connection", "raid.status", "player.death", "boss.killed" },
            new[] { "server.announcement", "moderation.action", "command.executed", "cron.executed", "connection.rejected", "character.revision_observed", "character.validation", "character.shadow_stalled", "security.admin_bypass", "security.alert" },
            new[] { "server.status", "server.saved", "chat.shout" },
            new[] { "server.status", "server.saved", "chat.shout" }
        };
        Check(routes.Children.Count == names.Length, "Generated YAML contains exactly the four approved route examples");
        var actualEvents = new List<HashSet<string>>();
        for (int index = 0; index < names.Length; ++index)
        {
            var route = (YamlMappingNode)routes.Children[index];
            string Value(string key) => ((YamlScalarNode)route.Children[new YamlScalarNode(key)]).Value!;
            Check(Value("name") == names[index] && Value("enabled") == "false" && Value("url") == "",
                "Each named example is disabled and contains no webhook credential");
            string[] selected = ((YamlSequenceNode)route.Children[new YamlScalarNode("events")]).Children
                .Cast<YamlScalarNode>().Select(node => node.Value!).ToArray();
            Check(selected.SequenceEqual(events[index]), "Default route selections use the grouped filters in the intended order");
            Check(selected.Distinct(StringComparer.Ordinal).Count() == selected.Length, "Default route filters are not repeated");
            actualEvents.Add(new HashSet<string>(selected, StringComparer.Ordinal));
            var expectedKeys = new HashSet<string>(new[] { "name", "enabled", "url", "events" }, StringComparer.Ordinal);
            if (index != 3)
            {
                expectedKeys.Add("anonymous_prefix");
                Check(Value("anonymous_prefix") == "", "Explicit anonymity examples preserve original player names");
            }
            if (index < 2)
            {
                expectedKeys.Add("include_steam_id");
                Check(Value("include_steam_id") == "false", "Server status and Moderation explicitly leave Steam-ID display disabled");
            }
            if (index == 0)
            {
                expectedKeys.UnionWith(new[] { "username", "avatar_url", "language" });
                Check(Value("username") == "ServerManager" && Value("avatar_url") == "", "Optional sender presentation defaults are unchanged");
                Check(Value("language") == "English", "Server status example declares the unchanged English default for public events");
            }
            if (index == 2)
            {
                expectedKeys.UnionWith(new[] { "avatar_url", "language" });
                Check(Value("language") == "Korean" && Value("avatar_url") == "", "examplehook demonstrates Korean without an avatar override");
            }
            Check(expectedKeys.SetEquals(route.Children.Keys.Cast<YamlScalarNode>().Select(node => node.Value!)),
                "Each route contains exactly its approved required and optional settings");
        }
        Check(!actualEvents[0].Overlaps(actualEvents[1]) &&
            new HashSet<string>(actualEvents[0].Concat(actualEvents[1]), StringComparer.Ordinal).SetEquals(DiscordSettings.PublicEvents.Except(new[] { "discord.shout" })),
            "Default routes retain their filters; Discord-origin chat remains explicitly opt-in");
        Check(OperatorKinds.All(actualEvents[1].Contains) && !OperatorKinds.Any(actualEvents[0].Contains),
            "All sensitive operator filters are grouped into Moderation");
        Check(!actualEvents[1].Contains("player.connection") && actualEvents[1].Count == 10,
            "Moderation includes the separate cron result selector without adding connection identity notifications");
        Check(actualEvents[2].SetEquals(actualEvents[3]) && actualEvents[2].IsSubsetOf(actualEvents[0]),
            "Both additional examples intentionally repeat the same three public status/shout filters");
        Check(generated.Replace("\r\n", "\n").Contains(
            "  - name: examplehook2\n    enabled: false\n    url: ''\n    events:\n" +
            "      - server.status\n      - server.saved\n      - chat.shout\n"),
            "examplehook2 demonstrates the approved multiline event-list syntax");

        // Activate only an in-memory copy with offline fixtures to validate omitted defaults.
        var bot = (YamlMappingNode)root.Children[new YamlScalarNode("bot")];
        bot.Children[new YamlScalarNode("enabled")] = new YamlScalarNode("false");
        foreach (YamlMappingNode route in routes.Children)
        {
            route.Children[new YamlScalarNode("enabled")] = new YamlScalarNode("true");
            route.Children[new YamlScalarNode("url")] = new YamlScalarNode(SafeWebhook);
        }
        using var writer = new StringWriter();
        document.Save(writer, assignAnchors: false);
        DiscordSettings configured = DiscordSettings.ParseForReload(writer.ToString());
        Check(!configured.BotEnabled && configured.WebhookRoutes.Count == 4,
            "Filling the four URLs and explicitly disabling the unused bot activates all examples on strict reload");
        Check(configured.WebhookRoutes.All(route => route.Username == "ServerManager" && route.AvatarUrl == "" && route.AnonymousPrefix == "" && !route.IncludeSteamId) &&
            configured.WebhookRoutes.Select(route => route.Language).SequenceEqual(new[] { "English", "English", "Korean", "English" }),
            "Explicit and omitted presentation/language fields resolve to the approved defaults independently per route");
    }

    private static void PartialReload()
    {
        var fixture = new Fixture();
        var previous = DiscordSettings.ParseForReload(fixture.Render());
        fixture.Routes[0]["url"] = "' '";
        fixture.Routes[1]["anonymous_prefix"] = "'Viking'";
        var next = DiscordSettings.ParseForPartialReload(fixture.Render(), previous);
        Check(next.WebhookRoutes[0].Url == SafeWebhook && next.WebhookRoutes[1].AnonymousPrefix == "Viking",
            "Bad first URL retains its route while the second route reloads");
        Check(next.ReloadWarnings.Count == 1, "Partial failure produces one diagnostic");
        Safe(string.Join("\n", next.ReloadWarnings));
        fixture.Routes[0]["enabled"] = "false";
        next = DiscordSettings.ParseForPartialReload(fixture.Render(), next);
        Check(next.WebhookRoutes.Count == 1 && next.WebhookRoutes[0].Name == "second", "Explicit disable wins over invalid URL");
        fixture.Routes[0]["enabled"] = "true";
        next = DiscordSettings.ParseForPartialReload(fixture.Render(), next);
        Check(next.WebhookRoutes.Count == 1, "Disabled history does not resurrect an older route");
        fixture.Routes.Clear();
        next = DiscordSettings.ParseForPartialReload(fixture.Render(), previous);
        Check(next.WebhookRoutes.Count == 0, "Deleted routes stay deleted");
        fixture = new Fixture();
        fixture.Bot["token"] = "''";
        fixture.Routes[1]["username"] = "'Updated'";
        next = DiscordSettings.ParseForPartialReload(fixture.Render(), previous);
        Check(next.HasSameBotSettings(previous) && next.WebhookRoutes[1].Username == "Updated", "Invalid bot does not block webhook reload");
        fixture.Bot["enabled"] = "false";
        next = DiscordSettings.ParseForPartialReload(fixture.Render(), previous);
        Check(!next.BotEnabled, "Explicit bot disable overrides invalid credentials");
        fixture = new Fixture();
        fixture.Routes[1]["name"] = fixture.Routes[0]["name"];
        next = DiscordSettings.ParseForPartialReload(fixture.Render(), previous);
        Check(next.WebhookRoutes.Count == 1 && next.WebhookRoutes[0].Url == SafeWebhook, "Duplicate identity retains exactly one old route; removed name stays removed");
        bool rejected = false;
        try { DiscordSettings.ParseForPartialReload("webhooks: [", previous); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && previous.WebhookRoutes.Count == 2, "Broken YAML rejects the entire candidate without modifying previous settings");
    }

    private static void Valid()
    {
        DiscordSettings settings = Load(new Fixture().Render());
        Check(settings.BotEnabled && settings.WebhookRoutes.Count == 2, "Valid bot and two independent routes enabled");
        Check(settings.CommandChannelIds.SetEquals(new[] { "456" }) && settings.AdminUserIds.SetEquals(new[] { "789" }), "ID string lists parsed");
        Check(settings.WebhookRoutes[0].Name == "first" && settings.WebhookRoutes[0].Url == SafeWebhook && settings.WebhookRoutes[0].Events.SetEquals(new[] { "server.status" }), "Named route fields preserved");
        var omitted = new Fixture(); omitted.Bot.Remove("enabled");
        foreach (var route in omitted.Routes) route.Remove("enabled");
        DiscordSettings omittedStartup = Load(omitted.Render());
        DiscordSettings omittedReload = DiscordSettings.ParseForReload(omitted.Render());
        Check(omittedStartup.BotEnabled && omittedStartup.WebhookRoutes.Count == 2 &&
            omittedReload.BotEnabled && omittedReload.WebhookRoutes.Count == 2 &&
            settings.HasSameBotSettings(omittedReload) && settings.HasSameWebhookSettings(omittedReload),
            "Omitted enabled flags match explicit true for a bot and routes with valid credentials");
        omitted.Bot["token"] = "''";
        BotRejected(omitted.Render(), "Omitted bot enabled does not bypass missing-token validation");
        omitted = new Fixture(); omitted.Routes[0].Remove("enabled"); omitted.Routes[0]["url"] = "''";
        RouteRejected(omitted.Render(), "Omitted route enabled does not bypass missing-URL validation");
        string missingBot = new Fixture().Render();
        missingBot = missingBot.Substring(missingBot.IndexOf("webhooks:", StringComparison.Ordinal));
        DiscordSettings webhookOnly = DiscordSettings.ParseForReload(missingBot);
        DiscordSettings webhookOnlyStartup = Load(missingBot);
        Check(!webhookOnly.BotEnabled && webhookOnly.WebhookRoutes.Count == 2 &&
            !webhookOnlyStartup.BotEnabled && webhookOnlyStartup.WebhookRoutes.Count == 2,
            "An absent bot block preserves webhook-only startup and strict reload without bot credentials");
        Check(!DiscordSettings.ParseForReload("bot: {enabled: false}\n" + missingBot).BotEnabled,
            "Explicitly disabling the bot also preserves webhook-only configuration");
        var fixture = new Fixture();
        fixture.Bot["admin_channel_ids"] = "['456','456','18446744073709551615']";
        fixture.Bot["admin_user_ids"] = "['11','12','11']";
        settings = Load(fixture.Render());
        Check(settings.BotEnabled && settings.CommandChannelIds.SetEquals(new[] { "456", "18446744073709551615" }), "64-bit ID strings preserve precision and deduplicate");
        Check(settings.AdminUserIds.SetEquals(new[] { "11", "12" }), "Only global admin user IDs are parsed and deduplicated");
        fixture = new Fixture();
        fixture.Bot["admin_user_ids"] = "[]";
        settings = Load(fixture.Render());
        Check(settings.BotEnabled && settings.AdminUserIds.Count == 0, "An explicitly empty admin list grants no management users");
        fixture.Bot.Remove("admin_user_ids");
        settings = Load(fixture.Render());
        Check(settings.AdminUserIds.Count == 0, "Omitted admin IDs never infer authority from channels or guild");
        fixture = new Fixture();
        fixture.Bot["enabled"] = "false"; fixture.Bot["token"] = "''"; fixture.Bot["guild_ids"] = "[]"; fixture.Bot["admin_channel_ids"] = "[]";
        fixture.Routes[0]["enabled"] = "false"; fixture.Routes[0]["url"] = "''";
        settings = Load(fixture.Render());
        DiscordSettings disabledReload = DiscordSettings.ParseForReload(fixture.Render());
        Check(!settings.BotEnabled && settings.WebhookRoutes.Count == 1 &&
            !disabledReload.BotEnabled && disabledReload.WebhookRoutes.Count == 1,
            "Explicit false still disables bot/routes and permits empty credentials at startup and reload");
    }

    private static void WebhookLanguages()
    {
        DiscordSettings baseline = DiscordSettings.ParseForReload(new Fixture().Render());
        Check(baseline.WebhookRoutes.All(route => route.Language == "English"), "Omitted languages default independently to English");
        foreach (string language in new[] { "English", "Korean", "German", "Portuguese_Brazilian", "zh-Hant", "Custom Language 2", new string('a', 64), "  Korean  " })
        {
            var fixture = new Fixture(); fixture.Routes[0]["language"] = Quote(language);
            DiscordSettings parsed = Load(fixture.Render()), reloaded = DiscordSettings.ParseForReload(fixture.Render());
            Check(parsed.WebhookRoutes[0].Language == language.Trim() && reloaded.WebhookRoutes[0].Language == language.Trim() &&
                parsed.WebhookRoutes[1].Language == "English", "Each safe identifier survives startup/reload without changing another route");
        }
        var english = new Fixture(); english.Routes[0]["language"] = "English";
        Check(baseline.HasSameWebhookSettings(DiscordSettings.ParseForReload(english.Render())), "Explicit default language is semantically unchanged");
        foreach (string bad in new[] { "''", "' '", "null", "[]", "{}", Quote("Korean/English"), Quote("../English"),
            Quote("한국어"), Quote(new string('a', 65)), "\"English\\n\"", "\"\\tKorean\"", "\"Korean\\u200B\"" })
        {
            var fixture = new Fixture(); fixture.Routes[0]["language"] = bad;
            RouteRejected(fixture.Render(), "Invalid language isolates its route at startup");
            fixture.Routes[0]["enabled"] = "false";
            StrictRejected(fixture.Render(), "Disabled routes also reject invalid language on whole-document reload");
        }
    }

    private static void SteamIdOptions()
    {
        DiscordSettings baseline = DiscordSettings.ParseForReload(new Fixture().Render());
        Check(baseline.WebhookRoutes.All(route => !route.IncludeSteamId), "Omitted Steam-ID options default independently to false");
        foreach (bool enabled in new[] { false, true })
        {
            var fixture = new Fixture(); fixture.Routes[0]["include_steam_id"] = enabled ? "true" : "false";
            string yaml = fixture.Render();
            DiscordSettings startup = Load(yaml), reloaded = DiscordSettings.ParseForReload(yaml);
            Check(startup.WebhookRoutes[0].IncludeSteamId == enabled && reloaded.WebhookRoutes[0].IncludeSteamId == enabled &&
                !startup.WebhookRoutes[1].IncludeSteamId && !reloaded.WebhookRoutes[1].IncludeSteamId,
                "The strict Steam-ID boolean applies only to its configured route at startup and reload");
            Check(baseline.HasSameBotSettings(reloaded) && baseline.HasSameWebhookSettings(reloaded) == !enabled,
                "Explicit false equals omission while enabling Steam IDs changes webhook settings only");
            fixture.Routes[0]["anonymous_prefix"] = "Anonymous";
            Check(DiscordSettings.ParseForReload(fixture.Render()).WebhookRoutes[0].IncludeSteamId == enabled,
                "Anonymity and Steam-ID preference remain separate route settings; output redaction is enforced by the sender");
            fixture.Bot["enabled"] = "false"; fixture.Bot["token"] = "''"; fixture.Bot["guild_ids"] = "[]";
            DiscordSettings webhookOnly = DiscordSettings.ParseForReload(fixture.Render());
            Check(!webhookOnly.BotEnabled && webhookOnly.WebhookRoutes[0].IncludeSteamId == enabled,
                "Steam-ID route preference does not require bot credentials or authority");
        }
        foreach (string scalar in new[] { "yes", "no", "on", "off", "1", "0", "True", "FALSE", "'true'", "\"false\"",
            "''", "' '", "null", "~", "[]", "{}", Quote(Secret) })
        {
            var fixture = new Fixture(); fixture.Routes[0]["include_steam_id"] = scalar;
            RouteRejected(fixture.Render(), "Invalid Steam-ID boolean isolates its route at startup and rejects strict reload");
            fixture.Routes[0]["enabled"] = "false";
            RouteRejected(fixture.Render(), "Disabled routes still reject an invalid supplied Steam-ID boolean");
        }
        var topLevel = new Fixture();
        StrictRejected(topLevel.Render() + "include_steam_id: true\n", "Steam-ID display is not a top-level option");
        topLevel.Bot["include_steam_id"] = "true";
        BotRejected(topLevel.Render(), "Steam-ID display is not a bot option");
    }

    private static void AnonymousPrefixes()
    {
        DiscordSettings original = DiscordSettings.ParseForReload(new Fixture().Render());
        Check(original.WebhookRoutes.All(route => route.AnonymousPrefix == ""), "Missing prefix leaves every route non-anonymous");
        foreach (string prefix in new[] { "", "   ", "Anonymous", "  Anonymous  ", "ㅇㅇ", "  익명  사용자  ", "익명\u00a0팀", "😀", new string('가', 32), string.Concat(Enumerable.Repeat("😀", 16)) })
        {
            var fixture = new Fixture(); fixture.Routes[0]["anonymous_prefix"] = Quote(prefix);
            DiscordSettings startup = Load(fixture.Render());
            DiscordSettings reloaded = DiscordSettings.ParseForReload(fixture.Render());
            Check(startup.WebhookRoutes[0].AnonymousPrefix == prefix.Trim() && reloaded.WebhookRoutes[0].AnonymousPrefix == prefix.Trim(),
                "Printable custom prefix preserves Unicode and interior spaces, trimming only edges");
            Check(startup.WebhookRoutes[1].AnonymousPrefix == "" && reloaded.WebhookRoutes[1].AnonymousPrefix == "",
                "Prefix applies only to its configured webhook route");
            Check(original.HasSameBotSettings(reloaded), "Prefix never changes bot authority or chat settings");
            Check(original.HasSameWebhookSettings(reloaded) == (prefix.Trim().Length == 0),
                "Empty and missing prefixes compare equally, while enabling anonymity triggers webhook reload");
        }
        foreach (string value in new[]
        {
            "null", "~", "[]", "{}", Quote(new string('a', 33)), Quote(new string('가', 33)),
            Quote(string.Concat(Enumerable.Repeat("😀", 17))), Quote("\tAnonymous"), Quote("Anonymous\n"),
            Quote("An\ronymous"), "\"Anon\\u0000\"", "\"Anon\\u0085\"",
            "\"An\\u200bonymous\"", "\"An\\u202eonymous\"", "\"An\\u2066onymous\"", "\"An\\ufeffonymous\"",
            "\"An\\u2028onymous\"", "\"An\\u2029onymous\"", "\"An\\U000e0001onymous\"",
            Quote(Secret + "\u200b")
        })
        {
            var fixture = new Fixture(); fixture.Routes[0]["anonymous_prefix"] = value;
            RouteRejected(fixture.Render(), "Invalid anonymous prefix");
            fixture.Routes[0]["enabled"] = "false";
            RouteRejected(fixture.Render(), "Disabled route still validates its supplied anonymous prefix");
        }
        foreach (string value in new[] { "\"Anon\\uD800\"", "\"Anon\\uDC00\"", "\"Anon\\uD800x\"", Quote("Anon\ud800"), Quote("Anon\udc00") })
        {
            var fixture = new Fixture(); fixture.Routes[0]["anonymous_prefix"] = value;
            StrictRejected(fixture.Render(), "Unpaired surrogate prefix is rejected before application");
            fixture.Routes[0]["enabled"] = "false";
            StrictRejected(fixture.Render(), "Disabled route cannot hide an unpaired surrogate prefix");
        }
        var anonymous = new Fixture(); anonymous.Routes[0]["anonymous_prefix"] = Quote("익명  사용자");
        DiscordSettings active = DiscordSettings.ParseForReload(anonymous.Render());
        DiscordSettings previous = active;
        anonymous.Routes[0]["anonymous_prefix"] = Quote("bad\u200bprefix");
        bool rejected = false;
        try { active = DiscordSettings.ParseForReload(anonymous.Render()); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && ReferenceEquals(active, previous) && active.WebhookRoutes[0].AnonymousPrefix == "익명  사용자" &&
            active.WebhookRoutes[1].AnonymousPrefix == "", "Invalid reload cannot replace the last valid per-route prefix snapshot");
        anonymous.Routes[0]["anonymous_prefix"] = "''";
        DiscordSettings cleared = DiscordSettings.ParseForReload(anonymous.Render());
        Check(cleared.WebhookRoutes[0].AnonymousPrefix == "" && !active.HasSameWebhookSettings(cleared) && active.HasSameBotSettings(cleared),
            "Clearing an existing prefix disables anonymity through webhook-only reload");
        anonymous.Routes[0]["anonymous_prefix"] = Quote("  익명  사용자  ");
        Check(active.HasSameWebhookSettings(DiscordSettings.ParseForReload(anonymous.Render())),
            "Whitespace normalization does not cause a redundant webhook rebuild");
        StrictRejected(new Fixture().Render() + "anonymous_prefix: Anonymous\n", "Anonymous prefix is not a top-level option");
        var misplaced = new Fixture(); misplaced.Bot["anonymous_prefix"] = "Anonymous";
        BotRejected(misplaced.Render(), "Anonymous prefix is not a bot option");
    }

    private static void GuildSettings()
    {
        var fixture = new Fixture(); fixture.Bot["guild_ids"] = "['123','321','123','18446744073709551615']";
        DiscordSettings settings = Load(fixture.Render());
        Check(settings.BotEnabled && settings.GuildIds.SetEquals(new[] { "123", "321", "18446744073709551615" }),
            "Multiple guild IDs preserve full precision and deduplicate");
        Check(DiscordSettings.ParseForReload(fixture.Render()).GuildIds.SetEquals(settings.GuildIds),
            "Strict reload accepts multiple guilds");
        Check(settings.CommandChannelIds.SetEquals(new[] { "456" }) && settings.AdminUserIds.SetEquals(new[] { "789" }),
            "Multiple guilds share the explicitly configured management channels and administrator list");
        fixture.Bot["guild_ids"] = "[]";
        BotRejected(fixture.Render(), "Enabled bot needs at least one guild even when its channels are idle");
        fixture.Bot.Remove("guild_ids");
        BotRejected(fixture.Render(), "Missing guild list never infers server membership");
        fixture.Bot["enabled"] = "false";
        Check(Load(fixture.Render()).GuildIds.Count == 0 && DiscordSettings.ParseForReload(fixture.Render()).GuildIds.Count == 0,
            "Disabled bot permits an omitted guild list");
        fixture.Bot["guild_ids"] = "[]";
        Check(!Load(fixture.Render()).BotEnabled && !DiscordSettings.ParseForReload(fixture.Render()).BotEnabled,
            "Disabled bot permits an explicitly empty guild list");
        foreach (string value in new[]
        {
            "null", "'123'", "{}", "[{}]", "[[]]", "[null]", "['']", "['0']", "['0123']", "['-1']", "['1.2']",
            "['18446744073709551616']", "['123456789012345678901']", "['１２３']", "['123','" + Secret + "']"
        })
        {
            var bad = new Fixture(); bad.Bot["guild_ids"] = value;
            BotRejected(bad.Render(), "Invalid guild ID list");
            bad.Bot["enabled"] = "false";
            BotRejected(bad.Render(), "Disabled bot does not ignore an invalid supplied guild list");
        }
        fixture = new Fixture();
        fixture.Bot["guild_ids"] = "[" + string.Join(",", Enumerable.Range(1, 256).Select(i => Quote(i.ToString()))) + "]";
        Check(Load(fixture.Render()).GuildIds.Count == 256 && DiscordSettings.ParseForReload(fixture.Render()).GuildIds.Count == 256,
            "Guild list accepts the existing 256-entry boundary");
        fixture.Bot["guild_ids"] = "[" + string.Join(",", Enumerable.Repeat("'123'", 257)) + "]";
        BotRejected(fixture.Render(), "Guild list bounds raw entries before duplicate removal");
        Invalid(Utf8.GetBytes("bot: {guild_ids: ['123'], guild_ids: ['321']}\n"), "Duplicate guild list keys rejected");
    }

    private static void RemovedBotSchema()
    {
        foreach (string key in new[] { "guild_id", "command_channel_ids", "adminchannel_ids", "chatchannel_ids" })
        foreach (string value in new[] { "[]", "['123']", "'123'", "null" })
        {
            var fixture = new Fixture(); fixture.Bot[key] = value;
            BotRejected(fixture.Render(), "Removed or misspelled bot key is never an alias: " + key);
            fixture.Bot["enabled"] = "false";
            StrictRejected(fixture.Render(), "Disabled bot also rejects removed key: " + key);
        }
        foreach (string value in new[] { "{}", "{channel_ids: []}", "{channel_ids: ['456']}", "null", "[]", "false" })
        {
            string yaml = new Fixture().Render() + "chat: " + value + "\n";
            Invalid(Utf8.GetBytes(yaml), "Removed top-level chat section rejects the entire startup document");
            StrictRejected(yaml, "Removed top-level chat section rejects the entire reload document");
        }
        foreach (string key in new[] { "guild_ids", "admin_channel_ids", "chat_channel_ids" })
            StrictRejected(new Fixture().Render() + key + ": []\n", "Bot lists cannot move to a top-level key: " + key);
    }

    private static void OperatorRoutes()
    {
        Check(DiscordSettings.PublicEvents.Count == 18 && OperatorKinds.All(DiscordSettings.PublicEvents.Contains),
            "One supported webhook catalog includes all six exact operator selectors");
        foreach (string kind in OperatorKinds)
        {
            var fixture = new Fixture(); fixture.Routes[0]["events"] = "[" + kind + "]";
            Check(Load(fixture.Render()).WebhookRoutes[0].Events.SetEquals(new[] { kind }),
                "Startup allows explicit operator route " + kind);
            DiscordSettings strict = DiscordSettings.ParseForReload(fixture.Render());
            Check(strict.WebhookRoutes[0].Events.SetEquals(new[] { kind }),
                "Strict reload allows operator summary through explicit event selection alone " + kind);
        }
        foreach (string kind in new[] { "security.*", "character.*", "connection.*", "security.unknown", "connection.reject", "CONNECTION.REJECTED" })
        {
            var fixture = new Fixture(); fixture.Routes[0]["events"] = "[" + kind + "]";
            RouteRejected(fixture.Render(), "No wildcard or inferred operator kind " + kind);
            StrictRejected(fixture.Render(), "Strict reload rejects unknown operator kind " + kind);
        }
    }

    private static void SourceEventFilters()
    {
        var grouped = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["server.ready"] = "server.status",
            ["server.shutdown"] = "server.status",
            ["player.login"] = "player.connection",
            ["player.leave"] = "player.connection",
            ["raid.started"] = "raid.status",
            ["raid.ended"] = "raid.status",
            ["combat.pvp_kill"] = "player.death",
            ["character.save_rejected"] = "character.validation",
            ["character.validation_observed"] = "character.validation",
            ["security.detection"] = "security.alert",
            ["security.response"] = "security.alert"
        };
        foreach (var pair in grouped)
            Check(DiscordSettings.GetWebhookEventFilter(pair.Key) == pair.Value && !DiscordSettings.PublicEvents.Contains(pair.Key),
                "Raw internal event maps to its grouped selector without becoming a selectable alias: " + pair.Key);
        foreach (string filter in SyntheticEventFilters)
        {
            var fixture = new Fixture(); fixture.Routes[0]["events"] = "['" + filter + "']";
            Check(Load(fixture.Render()).WebhookRoutes[0].Events.SetEquals(new[] { filter }) &&
                DiscordSettings.ParseForReload(fixture.Render()).WebhookRoutes[0].Events.SetEquals(new[] { filter }),
                "Grouped selector is explicitly selectable at startup and strict reload: " + filter);
        }
        foreach (string kind in DiscordSettings.PublicEvents)
        {
            bool synthetic = SyntheticEventFilters.Contains(kind);
            Check(DiscordSettings.GetWebhookEventFilter(kind) == (synthetic ? null : kind),
                "Real source kind retains identity while a synthetic selector cannot be published as an event: " + kind);
        }
        foreach (string kind in new[] { "", "server.started", "player.first_join", "security.unknown", "character.unknown",
            "security.*", "SECURITY.DETECTION", "PLAYER.DEATH", "player.death ", " security.response" })
            Check(DiscordSettings.GetWebhookEventFilter(kind) == null, "Unknown/removed/noncanonical source cannot enter grouped routing: " + kind);
        var projected = new HashSet<string>(DiscordSettings.PublicEvents.Where(kind => !SyntheticEventFilters.Contains(kind))
            .Concat(grouped.Keys).Select(kind => DiscordSettings.GetWebhookEventFilter(kind)!), StringComparer.Ordinal);
        projected.Add("cron.executed"); // Selected from final source=cron command results by the dispatcher.
        Check(projected.SetEquals(DiscordSettings.PublicEvents), "All eighteen selectors correspond to supported real source events or the final cron projection");
    }

    private static void RemovedWebhookEvents()
    {
        foreach (string kind in new[] { "server.started", "player.first_join" }.Concat(GroupedSourceKinds))
        {
            Check(!DiscordSettings.PublicEvents.Contains(kind), "Removed webhook selector is not supported: " + kind);
            var fixture = new Fixture(); fixture.Routes[0]["events"] = "['server.status','" + kind + "']";
            RouteRejected(fixture.Render(), "Removed selector cannot silently expand or translate: " + kind);
            fixture.Routes[0]["enabled"] = "false";
            RouteRejected(fixture.Render(), "Disabled route also rejects removed selector: " + kind);
        }
    }

    private static void ChatSettings()
    {
        var fixture = new Fixture();
        fixture.Bot.Remove("chat_channel_ids");
        string withoutChat = fixture.Render();
        DiscordSettings settings = Load(withoutChat);
        Check(settings.BotEnabled && settings.ChatChannelIds.Count == 0 && settings.ChatRelayChannelCount == 1,
            "Omitted public chat list still allows configured admin-channel plain chat");
        Check(DiscordSettings.ParseForReload(withoutChat).ChatRelayChannelCount == 1, "Reload accepts admin-only plain chat with an omitted public list");
        fixture.Bot["chat_channel_ids"] = "['567','567','18446744073709551615']";
        settings = Load(fixture.Render());
        Check(settings.BotEnabled && settings.ChatChannelIds.SetEquals(new[] { "567", "18446744073709551615" }), "Chat IDs preserve precision and deduplicate");
        Check(settings.CommandChannelIds.SetEquals(new[] { "456" }) && settings.AdminUserIds.SetEquals(new[] { "789" }),
            "Chat channels neither replace management channels nor add administrator IDs");
        Check(settings.ChatRelayChannelCount == 3, "Effective chat channel count includes both independent channel sets");
        fixture.Bot["admin_channel_ids"] = "[]";
        fixture.Bot["admin_user_ids"] = "[]";
        settings = Load(fixture.Render());
        Check(settings.BotEnabled && settings.CommandChannelIds.Count == 0 && settings.ChatChannelIds.Count == 2 && settings.ChatRelayChannelCount == 2,
            "Dedicated chat-only bot needs no management channels or administrator IDs");
        Check(DiscordSettings.ParseForReload(fixture.Render()).BotEnabled, "Strict reload accepts chat-only bot");
        fixture.Bot["enabled"] = "false";
        Check(!Load(fixture.Render()).BotEnabled && !DiscordSettings.ParseForReload(fixture.Render()).BotEnabled,
            "Configured chat channels cannot bypass bot.enabled");
        fixture.Bot["enabled"] = "true"; fixture.Bot["token"] = "''";
        settings = Load(fixture.Render());
        Check(!settings.BotEnabled && settings.ChatChannelIds.Count == 0, "Invalid chat-only bot startup clears partially parsed chat settings");
        StrictRejected(fixture.Render(), "Chat-only bot still requires credentials");
        fixture = new Fixture(); fixture.Bot["admin_channel_ids"] = "[]";
        settings = Load(fixture.Render());
        Check(settings.BotEnabled && settings.CommandChannelIds.Count == 0 && settings.ChatChannelIds.Count == 0,
            "Enabled bot with neither command nor chat channels is safely idle at startup");
        settings = DiscordSettings.ParseForReload(fixture.Render());
        Check(settings.BotEnabled && settings.CommandChannelIds.Count == 0 && settings.ChatChannelIds.Count == 0,
            "Strict reload accepts idle bot without granting either channel access");
        foreach (string[] bad in new[] { new[] { "token", "''" }, new[] { "token", Quote("invalid " + Secret) },
            new[] { "guild_ids", "[]" }, new[] { "guild_ids", "['invalid']" } })
        {
            var idle = new Fixture(); idle.Bot["admin_channel_ids"] = "[]"; idle.Bot[bad[0]] = bad[1];
            BotRejected(idle.Render(), "Idle enabled bot still validates required token and guild");
        }
        foreach (string value in new[]
        {
            "null", "'567'", "{}", "[{}]", "['']", "['0']", "['0123']", "['-1']", "['1.2']",
            "['18446744073709551616']", "['123456789012345678901']", "['１２３']", "['567','" + Secret + "']"
        })
        {
            var bad = new Fixture(); bad.Bot["chat_channel_ids"] = value;
            BotRejected(bad.Render(), "Invalid chat channel ID list");
            bad.Bot["enabled"] = "false";
            StrictRejected(bad.Render(), "Disabled bot still validates chat IDs");
        }
        foreach (string key in new[] { "user_ids", "role_ids", "channel_ids", "unknown_" + Secret })
        { var bad = new Fixture(); bad.Bot[key] = "[]"; BotRejected(bad.Render(), "Bot accepts only the exact supported channel keys"); }
        fixture = new Fixture(); fixture.Bot["chat_channel_ids"] = "['456']";
        settings = Load(fixture.Render());
        Check(settings.CommandChannelIds.Overlaps(settings.ChatChannelIds) && DiscordSettings.ParseForReload(fixture.Render()).BotEnabled,
            "One channel may support both independently gated management commands and ordinary chat");
        Check(settings.ChatRelayChannelCount == 1, "The effective channel count deduplicates admin/public overlap");
        fixture.Bot["admin_user_ids"] = "[]";
        settings = DiscordSettings.ParseForReload(fixture.Render());
        Check(settings.ChatRelayChannelCount == 1 && settings.AdminUserIds.Count == 0,
            "An empty admin allowlist does not remove message intake; the per-message authorization gate decides delivery");
        fixture = new Fixture();
        fixture.Bot["chat_channel_ids"] = "[" + string.Join(",", Enumerable.Range(1, 257).Select(i => Quote(i.ToString()))) + "]";
        BotRejected(fixture.Render(), "Chat channel list is bounded");
        Invalid(Utf8.GetBytes("bot: {chat_channel_ids: [], chat_channel_ids: ['567']}\n"), "Duplicate chat channel keys rejected");
    }

    private static void BadBot()
    {
        foreach (string[] bad in new[]
        {
            new[] { "guild_ids", "'invalid'" }, new[] { "guild_ids", "'0'" }, new[] { "guild_ids", "'0123'" }, new[] { "guild_ids", "[]" },
            new[] { "admin_channel_ids", "'456'" }, new[] { "admin_channel_ids", "['456','invalid']" }, new[] { "admin_channel_ids", "[{}]" },
            new[] { "admin_user_ids", "['not-an-id']" }, new[] { "admin_role_ids", "['-1']" }, new[] { "allowed_commands", "['status','unknown']" }, new[] { "allowed_commands", "['STATUS']" },
            new[] { "allowed_commands", "'status,save'" }, new[] { "allowed_commands", "null" }, new[] { "token", "''" }, new[] { "token", Quote("bad " + Secret) },
            new[] { "token", Quote("bad\t" + Secret) }, new[] { "token", "\"bad\\u0001" + Secret + "\"" }, new[] { "token", Quote("비밀-" + Secret) }, new[] { "token", Quote(new string('x', 513)) }, new[] { "token", "[]" },
            new[] { "enabled", "yes" }, new[] { "enabled", "on" }, new[] { "enabled", "1" }, new[] { "enabled", "null" },
            new[] { "command_timeout_seconds", "2" }, new[] { "command_timeout_seconds", "61" }, new[] { "command_timeout_seconds", "3.5" }, new[] { "command_timeout_seconds", "99999999999999999999999" }, new[] { "unknown_" + Secret, "true" }
        })
        {
            var fixture = new Fixture(); fixture.Bot[bad[0]] = bad[1];
            BotRejected(fixture.Render(), "Invalid bot field " + bad[0]);
        }
        foreach (string raw in new[] { "null", "[]", "false" })
        { var fixture = new Fixture { BotRaw = raw }; BotRejected(fixture.Render(), "Invalid bot section type"); }
        var many = new Fixture();
        many.Bot["admin_channel_ids"] = "[" + string.Join(",", Enumerable.Range(1, 257).Select(i => Quote(i.ToString()))) + "]";
        BotRejected(many.Render(), "ID-list count bounded");
    }

    private static void RemovedAuthorizationKeys()
    {
        foreach (string key in new[] { "admin_role_ids", "allowed_commands" })
        foreach (string value in new[] { "[]", "['123']", "null" })
        {
            var fixture = new Fixture(); fixture.Bot[key] = value;
            BotRejected(fixture.Render(), "Removed bot key is invalid, even when empty: " + key);
            fixture.Bot["enabled"] = "false";
            StrictRejected(fixture.Render(), "Disabled bot does not silently accept removed key: " + key);
        }
        foreach (string value in new[] { "true", "false", "null" })
        {
            var fixture = new Fixture();
            Invalid(Utf8.GetBytes(fixture.Render() + "rcon: {enabled: " + value + "}\n"),
                "Removed RCON section/enable switch is not migrated or ignored");
            fixture.Bot["enabled"] = "false";
            StrictRejected(fixture.Render() + "rcon: {enabled: " + value + "}\n", "Disabled bot still rejects removed RCON section");
        }
        foreach (string value in new[] { "{}", "null", "[]", "{rcon: {user_ids: ['123']}}" })
            Invalid(Utf8.GetBytes(new Fixture().Render() + "grants: " + value + "\n"),
                "Removed grants section is rejected as an unknown root key");
        foreach (string duplicate in new[]
        {
            "bot: {admin_user_ids: [], admin_user_ids: ['123']}\n",
            "bot: {admin_user_ids: [], 'admin_user_ids': ['123']}\n",
            "bot: {enabled: false, admin_user_ids: [], admin_user_ids: []}\n",
            "rcon: {minimum_interval_seconds: 2, minimum_interval_seconds: 3}\n",
            "rcon: {maximum_output_characters: 1800, maximum_output_characters: 128}\n",
            "rcon: {}\nrcon: {}\n", "grants: {}\ngrants: {}\n"
        })
            Invalid(Utf8.GetBytes(duplicate), "Duplicate authorization/limit keys cannot bypass strict validation");
    }

    private static void RemovedCommandLimits()
    {
        const System.Reflection.BindingFlags members = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        foreach (string property in new[] { "RconMinimumIntervalSeconds", "MaximumRconOutputCharacters", "CommandTimeoutSeconds" })
            Check(typeof(DiscordSettings).GetMember(property, members).Length == 0,
                "Fixed command limit has no settings property, field or constant placeholder: " + property);
        Check(typeof(DiscordSettings).GetMethod("Number", members) == null, "Unused configurable integer parser is removed");
        foreach (string value in new[] { "15", "1", "0", "null", "[]", Quote(Secret) })
        {
            var fixture = new Fixture(); fixture.Bot["command_timeout_seconds"] = value;
            BotRejected(fixture.Render(), "Removed timeout is rejected even if it matches the fixed value");
            fixture.Bot["enabled"] = "false";
            StrictRejected(fixture.Render(), "Disabled bot cannot retain the removed timeout key");
        }
        foreach (string key in new[] { "minimum_interval_seconds", "maximum_output_characters" })
        foreach (string value in new[] { "1", "2", "1800", "0", "null", "[]", Quote(Secret) })
        {
            string yaml = new Fixture().Render() + "rcon: {" + key + ": " + value + "}\n";
            Invalid(Utf8.GetBytes(yaml), "Removed RCON limit is an unknown root section: " + key);
            StrictRejected(yaml, "Removed RCON limit cannot change a live fixed limit: " + key);
            StrictRejected(new Fixture().Render() + key + ": " + value + "\n", "Removed limit cannot move to another root key: " + key);
        }
        foreach (string raw in new[] { "{}", "null", "[]", "false" })
        {
            string yaml = new Fixture().Render() + "rcon: " + raw + "\n";
            Invalid(Utf8.GetBytes(yaml), "Removed RCON section rejects every shape");
            StrictRejected(yaml, "Even an empty removed RCON section rejects reload");
        }
    }

    private static void BadRoutes()
    {
        foreach (string url in new[]
        {
            "http://discord.com/api/webhooks/123/token", "https://discord.com.evil.example/api/webhooks/123/token", "https://discord.com@evil.example/api/webhooks/123/token",
            "https://user@discord.com/api/webhooks/123/token", "https://discord.com:444/api/webhooks/123/token", "https://127.0.0.1/api/webhooks/123/token",
            "https://discord.com/api/webhooks/0/token", "https://discord.com/api/webhooks/123/", "https://discord.com/api/webhooks/123/token/extra",
            "https://discord.com/api/webhooks/123/token?wait=true", "https://discord.com/api/webhooks/123/token#fragment", "https://discord.com/api/webhooks/123/encoded%2Ftoken"
        })
        { var fixture = new Fixture(); fixture.Routes[0]["url"] = Quote(url); RouteRejected(fixture.Render(), "Unsafe webhook URL"); }
        foreach (string events in new[] { "['*']", "['chat.normal']", "['chat.whisper']", "['chat.clan']", "['inventory.changed']", "['position.updated']", "['plugins.loaded']", "['server.status','chat.normal']", "['SERVER.STATUS']", "['server.*']", "['player.*']", "['raid.*']", "[]", "'server.status'", "null", "[{}]" })
        {
            var fixture = new Fixture(); fixture.Routes[0]["events"] = events;
            RouteRejected(fixture.Render(), "Private or invalid events remain unsupported");
        }
        foreach (string[] bad in new[]
        {
            new[] { "enabled", "yes" }, new[] { "url", "[]" }, new[] { "username", "''" }, new[] { "username", Quote(new string('a', 81)) },
            new[] { "username", "\"bad\\u0001name\"" }, new[] { "avatar_url", "'http://example.invalid/avatar.png'" }, new[] { "url_env", "[]" },
            new[] { "url_env", "'BAD=ENV'" }, new[] { "url_env", Quote(new string('x', 129)) }, new[] { "unknown_" + Secret, "true" }
        })
        { var fixture = new Fixture(); fixture.Routes[0][bad[0]] = bad[1]; RouteRejected(fixture.Render(), "Invalid webhook field"); }
        foreach (string raw in new[] { "null", "[]", "false" })
        { var fixture = new Fixture { FirstRouteRaw = raw }; RouteRejected(fixture.Render(), "Invalid route container"); }
        var shout = new Fixture(); shout.Routes[0]["events"] = "['chat.shout']";
        DiscordSettings settings = Load(shout.Render());
        Check(settings.WebhookRoutes.Count == 2 && settings.WebhookRoutes[0].Events.SetEquals(new[] { "chat.shout" }),
            "Shout selection is sufficient without a separate privacy option");
        var duplicate = new Fixture();
        duplicate.Routes[0]["name"] = Quote(Secret); duplicate.Routes[1]["name"] = Quote(Secret);
        var logs = new List<string>(); settings = Load(duplicate.Render(), logs);
        Check(settings.BotEnabled && settings.WebhookRoutes.Count == 1 && settings.WebhookRoutes[0].Url == SafeWebhook, "First valid named route retained; later duplicate rejected");
        Check(logs.Count > 0, "Duplicate-name diagnostic logged without its name/value");
        var disabledName = new Fixture();
        disabledName.Routes[0]["name"] = "'second'"; disabledName.Routes[0]["enabled"] = "false";
        settings = Load(disabledName.Render());
        Check(settings.BotEnabled && settings.WebhookRoutes.Count == 1 && settings.WebhookRoutes[0].Url == OtherWebhook, "Disabled route does not reserve its name");
        var invalidName = new Fixture();
        invalidName.Routes[0]["name"] = "'second'"; invalidName.Routes[0]["url"] = "'invalid'";
        settings = Load(invalidName.Render());
        Check(settings.BotEnabled && settings.WebhookRoutes.Count == 1 && settings.WebhookRoutes[0].Url == OtherWebhook, "Invalid route does not reserve its name");
    }

    private static void RemovedWebhookEnvironments()
    {
        foreach (string enabled in new[] { "true", "false" })
        foreach (string value in new[] { "''", Quote(EnvA), "null", "[]", "{}", "true", "1", Quote("BAD=" + Secret), Quote(new string('x', 129)) })
        {
            var fixture = new Fixture(); fixture.Routes[0]["enabled"] = enabled; fixture.Routes[0]["url_env"] = value;
            RouteRejected(fixture.Render(), "Removed url_env rejects its startup route and the whole reload, even for an empty or disabled setting");
        }
        foreach (string key in new[] { "URL_ENV", "url-env", "urlEnv" })
        {
            var fixture = new Fixture(); fixture.Routes[0][key] = Quote(EnvA);
            RouteRejected(fixture.Render(), "Removed webhook environment key has no spelling aliases");
        }
        var envOnly = new Fixture(); envOnly.Routes[0].Remove("url"); envOnly.Routes[0]["url_env"] = Quote(EnvA);
        RouteRejected(envOnly.Render(), "A removed environment key cannot replace the required direct URL");
        Invalid(Utf8.GetBytes("webhooks:\n  - enabled: false\n    url_env: ''\n    url_env: ''\n"),
            "Duplicate removed URL-environment keys still reject the YAML document");
        StrictRejected(new Fixture().Render() + "url_env: ''\n", "The removed route key is not accepted at the root");
    }

    private static void RemovedPrivacyAndCollections()
    {
        foreach (string raw in new[] { "null", "[]", "false", "{}" })
        {
            string yaml = new Fixture().Render() + "privacy: " + raw + "\n";
            Invalid(Utf8.GetBytes(yaml), "Removed privacy section rejected at startup regardless of value");
            StrictRejected(yaml, "Removed privacy section rejected during reload");
        }
        foreach (string key in new[] { "relay_shouts", "include_coordinates", "include_inventory", "include_plugin_list" })
        foreach (string value in new[] { "true", "false", Quote(Secret) })
        {
            string yaml = new Fixture().Render() + "privacy: {" + key + ": " + value + "}\n";
            Invalid(Utf8.GetBytes(yaml), "Legacy privacy option is not imported or ignored: " + key);
            StrictRejected(yaml, "Legacy privacy option is invalid during reload: " + key);
            StrictRejected(new Fixture().Render() + key + ": " + value + "\n", "Removed option cannot be moved to the root: " + key);
        }
        foreach (string raw in new[] { "null", "{}", "false" })
        { var fixture = new Fixture { WebhooksRaw = raw }; AllRoutesRejected(fixture.Render(), "Invalid webhooks collection"); }
        var many = new Fixture();
        while (many.Routes.Count < 11) many.Routes.Add(Route("route" + many.Routes.Count, SafeWebhook));
        AllRoutesRejected(many.Render(), "More than ten routes rejected, not truncated");
        var empty = new Fixture(); empty.Routes.Clear();
        Check(Load(empty.Render()).WebhookRoutes.Count == 0, "Explicit empty webhook list stays empty");
    }

    private static void Syntax()
    {
        foreach (string bad in new[]
        {
            "", "# comments alone are not a mapping\n", "null\n", "[]\n", "42\n", Quote(Secret), "unknown_" + Secret + ": true\n",
            "bot: {}\nbot: {}\n", "bot:\n  enabled: false\n  enabled: true\n", "webhooks:\n- enabled: false\n  enabled: true\n",
            "bot: &secret_anchor {}\n", "bot: *undefined_alias\n", "bot: !!map {}\n", "bot: !custom_tag {}\n", "bot:\n  token: !!str " + Secret + "\n",
            "{}\n---\n{}\n", "bot:\n  token: \"" + Secret + "\n", "bot: [" + Secret + "\n", "{bot: {}, \"bot\": {}}\n"
        }) Invalid(Utf8.GetBytes(bad), "Malformed/unsupported YAML");
        Invalid(Utf8.GetBytes("bot:\n  token: " + new string('[', 32) + Quote(Secret) + new string(']', 32)), "Nesting bounded");
        Invalid(Utf8.GetBytes("bot:\n  admin_user_ids: [" + string.Join(",", Enumerable.Repeat("1", 17000)) + "]\n"), "Parser events bounded before validation");
        Invalid(Utf8.GetBytes(new string('x', 128 * 1024 + 1)), "128 KiB ASCII bound");
        Invalid(Utf8.GetBytes("# " + new string('한', 50000) + "\n{}\n"), "128 KiB is UTF-8 bytes, not characters");
        Invalid(new byte[] { 0xff, 0xfe, 0xff, 0xfe }, "Invalid UTF-8 rejected");
    }

    private static void EnvironmentOverrides()
    {
        const string token = "environment-only-offline-token";
        const string firstUrl = "https://discord.com/api/webhooks/111/environment-only-A";
        const string secondUrl = "https://discord.com/api/webhooks/222/environment-only-B";
        try
        {
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_BOT_TOKEN", token);
            Environment.SetEnvironmentVariable(EnvA, firstUrl); Environment.SetEnvironmentVariable(EnvB, secondUrl);
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_WEBHOOK_1", "https://discord.com/api/webhooks/999/numbered-must-be-ignored");
            var fixture = new Fixture();
            DiscordSettings settings = Load(fixture.Render());
            Check(settings.BotToken == token && settings.WebhookRoutes.Single(r => r.Name == "first").Url == SafeWebhook &&
                settings.WebhookRoutes.Single(r => r.Name == "second").Url == OtherWebhook,
                "Only the bot token uses an environment override; webhook routes use their direct URLs");
            Check(DiscordSettings.ParseForReload(fixture.Render()).WebhookRoutes[0].Url == SafeWebhook,
                "Strict reload ignores all legacy webhook environment values");
            fixture.Routes.Reverse(); settings = Load(fixture.Render());
            Check(settings.WebhookRoutes.Single(r => r.Name == "first").Url == SafeWebhook && settings.WebhookRoutes.Single(r => r.Name == "second").Url == OtherWebhook,
                "Direct URL bindings survive route reordering without environment lookup");
            Check(Load(new Fixture().Render()).WebhookRoutes[0].Url == SafeWebhook, "No implicit numbered webhook environment lookup");
            Environment.SetEnvironmentVariable(EnvA, ""); Environment.SetEnvironmentVariable(EnvB, "invalid-" + Secret);
            Check(Load(new Fixture().Render()).WebhookRoutes.Count == 2,
                "Empty or invalid legacy environment values cannot disable configured URL routes");
            Environment.SetEnvironmentVariable(EnvA, firstUrl);
            fixture = new Fixture(); fixture.Routes[0]["url"] = "''";
            RouteRejected(fixture.Render(), "A valid environment URL never supplies a missing direct URL");
            fixture = new Fixture(); fixture.Bot["token"] = "''";
            Check(Load(fixture.Render()).BotToken == token && DiscordSettings.ParseForReload(fixture.Render()).BotEnabled,
                "The supported bot-token override still enables a bot with an empty YAML token");
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_BOT_TOKEN", "bad " + Secret);
            BotRejected(new Fixture().Render(), "Invalid environment bot token");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_BOT_TOKEN", null);
            Environment.SetEnvironmentVariable(EnvA, null); Environment.SetEnvironmentVariable(EnvB, null);
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_WEBHOOK_1", null);
        }
    }

    private static void BotRejected(string yaml, string title)
    {
        var logs = new List<string>(); DiscordSettings settings = Load(yaml, logs);
        Check(!settings.BotEnabled && settings.WebhookRoutes.Count == 2, title + " disables only bot"); Check(logs.Count > 0, "Bot rejection logged");
        Check(settings.ChatChannelIds.Count == 0 && !settings.BotEnabled, "Invalid bot startup fails closed for chat and all bot commands");
        StrictRejected(yaml, title);
    }
    private static void RouteRejected(string yaml, string title)
    {
        var logs = new List<string>(); DiscordSettings settings = Load(yaml, logs);
        Check(settings.BotEnabled && settings.WebhookRoutes.Count == 1 && settings.WebhookRoutes[0].Name == "second", title + " disables only invalid route"); Check(logs.Count > 0, "Route rejection logged");
        StrictRejected(yaml, title);
    }
    private static void AllRoutesRejected(string yaml, string title)
    {
        var logs = new List<string>(); DiscordSettings settings = Load(yaml, logs);
        Check(settings.BotEnabled && settings.WebhookRoutes.Count == 0, title + " preserves valid bot"); Check(logs.Count > 0, "Collection rejection logged");
        StrictRejected(yaml, title);
    }

    private static void StrictReload()
    {
        DiscordSettings valid = DiscordSettings.ParseForReload(new Fixture().Render());
        Check(valid.BotEnabled && valid.WebhookRoutes.Count == 2, "Strict parser accepts complete valid document");
        Check(!DiscordSettings.ParseForReload("{}\n").BotEnabled,
            "Strict empty mapping preserves the absent-bot disabled default");
        StrictRejected("bot: {}\n", "Strict empty bot mapping also requires credentials");
        Check(!DiscordSettings.ParseForReload("bot: {enabled: false}\n").BotEnabled,
            "Strict explicit false remains a credential-free disabled configuration");
        DiscordSettings retained = valid;
        bool missingCredentialsRejected = false;
        try { retained = DiscordSettings.ParseForReload("bot: {}\n"); }
        catch (InvalidDataException) { missingCredentialsRejected = true; }
        Check(missingCredentialsRejected && ReferenceEquals(retained, valid) && retained.BotEnabled && retained.WebhookRoutes.Count == 2,
            "A credential-less default candidate cannot replace the last-good bot and routes");
        var disabled = new Fixture(); disabled.Bot["enabled"] = "false";
        disabled.Bot["token"] = "''"; disabled.Bot["guild_ids"] = "[]"; disabled.Bot["admin_channel_ids"] = "[]";
        foreach (var route in disabled.Routes) { route["enabled"] = "false"; route["url"] = "''"; route["events"] = "[]"; }
        DiscordSettings inactive = DiscordSettings.ParseForReload(disabled.Render());
        Check(!inactive.BotEnabled && inactive.WebhookRoutes.Count == 0, "Strict disabled entries permit blank credentials and inactive empty events");
        foreach (var bad in new[]
        {
            new[] { "enabled", "no" }, new[] { "guild_ids", Quote(Secret) },
            new[] { "token", Quote("invalid " + Secret) }, new[] { "command_timeout_seconds", "99" },
            new[] { "admin_user_ids", "[{}]" }, new[] { "allowed_commands", "[unknown]" }
        })
        {
            var fixture = new Fixture(); fixture.Bot["enabled"] = "false"; fixture.Bot[bad[0]] = bad[1];
            StrictRejected(fixture.Render(), "Strict disabled bot field checked");
        }
        foreach (var bad in new[]
        {
            new[] { "url", Quote("https://example.invalid/" + Secret) }, new[] { "url", "[]" },
            new[] { "events", "[chat.normal]" }, new[] { "username", "''" },
            new[] { "avatar_url", Quote("http://example.invalid/" + Secret) },
            new[] { "url_env", Quote("BAD=" + Secret) }, new[] { "unknown_" + Secret, "true" }
        })
        {
            var fixture = new Fixture(); fixture.Routes[0]["enabled"] = "false"; fixture.Routes[0][bad[0]] = bad[1];
            StrictRejected(fixture.Render(), "Strict disabled route field checked");
        }
        var duplicate = new Fixture(); duplicate.Routes[0]["name"] = Quote(Secret); duplicate.Routes[1]["name"] = Quote(Secret);
        StrictRejected(duplicate.Render(), "Strict duplicate enabled route names rejected");
        duplicate.Routes[0]["enabled"] = "false";
        Check(DiscordSettings.ParseForReload(duplicate.Render()).WebhookRoutes.Count == 1, "Disabled duplicate name does not reserve an effective route");
        var disabledGrant = new Fixture(); disabledGrant.Bot["enabled"] = "false";
        StrictRejected(disabledGrant.Render() + "grants: {rcon: {user_ids: ['123']}}\n",
            "Disabled bot still rejects removed grants");
        var disabledRcon = new Fixture(); disabledRcon.Bot["enabled"] = "false";
        StrictRejected(disabledRcon.Render() + "rcon: {minimum_interval_seconds: 1}\n", "Disabled bot still rejects removed RCON settings");
        StrictRejected("bot: {token: '" + Secret + "', token: 'duplicate'}", "Strict duplicate secret scalar key");
        StrictRejected("webhooks:\n - name: '" + Secret + "'\n   [" + Secret + "]: true", "Strict non-scalar secret key");
        StrictRejected("# " + new string('한', 50000) + "\n{}\n", "Strict string byte limit");
        StrictRejected(new string('a', 128 * 1024 + 1), "Strict string character upper bound");
        StrictRejected("# invalid unicode \ud800\n{}", "Strict strings reject unpaired UTF-16 surrogates");
        StrictRejected(null!, "Strict missing string rejected safely");
    }

    private static void ReloadReads()
    {
        string missing = Path.Combine(NextRoot(), Secret);
        ReloadReadRejected(missing, "Reload missing directory is rejected");
        Check(!Directory.Exists(missing) && !Directory.Exists(Path.GetDirectoryName(missing)), "Reload never creates the data directory");
        string root = NextRoot(); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "discord.cfg"), Secret, Utf8);
        ReloadReadRejected(root, "Reload missing YAML is rejected");
        Check(!File.Exists(Path.Combine(root, "discord.yml")) && File.ReadAllText(Path.Combine(root, "discord.cfg"), Utf8) == Secret, "Reload neither creates YAML nor reads/migrates CFG");
        string path = Path.Combine(root, "discord.yml");
        string text = "# " + Secret + "\r\n" + new Fixture().Render().Replace("\n", "\r\n");
        File.WriteAllText(path, text, Utf8);
        byte[] before = File.ReadAllBytes(path);
        Check(DiscordSettings.ReadReloadText(root) == text, "Reload reader preserves actual text and comments");
        Check(DiscordSettings.ParseForReload(DiscordSettings.ReadReloadText(root)).BotEnabled, "Reload read plus parse succeeds without rewrite");
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "Reload leaves file bytes unchanged");
        byte[] bom = new byte[] { 0xef, 0xbb, 0xbf }.Concat(before).ToArray();
        File.WriteAllBytes(path, bom);
        Check(DiscordSettings.ReadReloadText(root) == text && bom.SequenceEqual(File.ReadAllBytes(path)), "Reload permits UTF-8 BOM without rewriting");
        string boundary = "{}\n#" + new string('x', 128 * 1024 - 4);
        File.WriteAllText(path, boundary, Utf8);
        Check(DiscordSettings.ReadReloadText(root) == boundary && !DiscordSettings.ParseForReload(boundary).BotEnabled, "Exactly 128 KiB remains permitted");
        foreach (byte[] bad in new[]
        {
            Utf8.GetBytes(boundary + "x"), Utf8.GetBytes("#" + new string('한', 50000)),
            new byte[] { 0xff, 0xfe, 0xff }, new byte[] { 0xe2, 0x82 }
        })
        {
            File.WriteAllBytes(path, bad); ReloadReadRejected(root, "Reload invalid size or UTF-8 rejected");
            Check(bad.SequenceEqual(File.ReadAllBytes(path)), "Failed reload read never modifies file");
        }
        File.Delete(path); ReloadReadRejected(root, "Deleted YAML stays missing");
        Check(!File.Exists(path), "Reload does not regenerate deleted YAML");
    }

    private static void Comparators()
    {
        string yaml = new Fixture().Render();
        DiscordSettings baseline = DiscordSettings.ParseForReload(yaml);
        DiscordSettings equivalent = DiscordSettings.ParseForReload("# another comment\n" + yaml);
        Check(baseline.HasSameBotSettings(equivalent) && baseline.HasSameWebhookSettings(equivalent), "Comments do not change effective settings");
        Check(baseline.HasSameBotSettings(baseline) && baseline.HasSameWebhookSettings(baseline), "Snapshot self-comparison is stable");
        Check(!baseline.HasSameBotSettings(null!) && !baseline.HasSameWebhookSettings(null!), "Missing candidate is not equal");
        var chat = new Fixture(); chat.Bot["chat_channel_ids"] = "['567']";
        BotChanged(baseline, chat, "Adding chat channel requires bot reconfiguration but leaves webhooks unchanged");
        DiscordSettings chatBaseline = DiscordSettings.ParseForReload(chat.Render());
        chat.Bot["chat_channel_ids"] = "['568']";
        BotChanged(chatBaseline, chat, "Replacing chat channel requires bot reconfiguration");
        chat.Bot["chat_channel_ids"] = "[]";
        BotChanged(chatBaseline, chat, "Clearing chat channels requires bot reconfiguration");
        chat.Bot["admin_channel_ids"] = "[]"; chat.Bot["chat_channel_ids"] = "['567']";
        DiscordSettings chatOnly = DiscordSettings.ParseForReload(chat.Render());
        chat.Bot["chat_channel_ids"] = "[]";
        DiscordSettings idleBot = DiscordSettings.ParseForReload(chat.Render());
        Check(idleBot.BotEnabled && idleBot.CommandChannelIds.Count == 0 && idleBot.ChatChannelIds.Count == 0 &&
            !chatOnly.HasSameBotSettings(idleBot) && chatOnly.HasSameWebhookSettings(idleBot),
            "Clearing the final chat-only channel applies as idle bot reconfiguration, not a rejected reload");
        foreach (var change in new[]
        {
            new[] { "enabled", "false" }, new[] { "token", "'different-token'" }, new[] { "guild_ids", "['124']" },
            new[] { "admin_channel_ids", "['457']" }, new[] { "admin_user_ids", "['790']" },
            new[] { "admin_user_ids", "[]" }
        })
        {
            var fixture = new Fixture(); fixture.Bot[change[0]] = change[1];
            BotChanged(baseline, fixture, "Bot comparator sees " + change[0]);
        }
        foreach (var change in new[]
        {
            new[] { "name", "'renamed'" }, new[] { "enabled", "false" }, new[] { "url", Quote(OtherWebhook) },
            new[] { "username", "'AnotherBot'" }, new[] { "avatar_url", "'https://example.invalid/avatar.png'" },
            new[] { "anonymous_prefix", "'Anonymous'" }, new[] { "language", "Korean" }, new[] { "include_steam_id", "true" },
            new[] { "events", "['server.status','server.saved']" }
        })
        { var fixture = new Fixture(); fixture.Routes[0][change[0]] = change[1]; WebhooksChanged(baseline, fixture, "Webhook comparator sees " + change[0]); }
        var reordered = new Fixture(); reordered.Routes.Reverse();
        WebhooksChanged(baseline, reordered, "Webhook order is compared consistently");
        var empty = new Fixture(); empty.Routes.Clear(); WebhooksChanged(baseline, empty, "Webhook removal detected");

        var unorderedA = new Fixture();
        unorderedA.Bot["guild_ids"] = "['123','321']";
        unorderedA.Bot["admin_channel_ids"] = "['456','457']"; unorderedA.Bot["admin_user_ids"] = "['789','790']";
        unorderedA.Bot["chat_channel_ids"] = "['567','568']";
        unorderedA.Routes[0]["events"] = "['server.status','server.saved']";
        DiscordSettings first = DiscordSettings.ParseForReload(unorderedA.Render());
        unorderedA.Bot["guild_ids"] = "['321','123','123']";
        unorderedA.Bot["admin_channel_ids"] = "['457','456','456']"; unorderedA.Bot["admin_user_ids"] = "['790','789']";
        unorderedA.Bot["chat_channel_ids"] = "['568','567','567']";
        unorderedA.Routes[0]["events"] = "['server.saved','server.status','server.status']";
        DiscordSettings second = DiscordSettings.ParseForReload(unorderedA.Render());
        Check(first.HasSameBotSettings(second) && first.HasSameWebhookSettings(second), "All ID and event sets ignore ordering and duplicate values");
        unorderedA.Bot["guild_ids"] = "['321']";
        BotChanged(first, unorderedA, "Removing one guild changes bot authority without changing webhook routes");
        unorderedA.Bot["guild_ids"] = "['123','321','999']";
        BotChanged(first, unorderedA, "Adding a guild requires bot reconfiguration");
        Check(baseline.HasSameBotSettings(DiscordSettings.ParseForReload(yaml)) && baseline.HasSameWebhookSettings(DiscordSettings.ParseForReload(yaml)), "Comparisons did not mutate either snapshot");

        try
        {
            var fixture = new Fixture();
            Environment.SetEnvironmentVariable(EnvA, SafeWebhook);
            first = DiscordSettings.ParseForReload(fixture.Render());
            Check(baseline.HasSameWebhookSettings(first), "Irrelevant legacy webhook environment does not change direct URL settings");
            Environment.SetEnvironmentVariable(EnvA, OtherWebhook);
            second = DiscordSettings.ParseForReload(fixture.Render());
            Check(first.HasSameWebhookSettings(second) && first.HasSameBotSettings(second), "Changing a legacy webhook environment has no semantic effect");
            fixture.Routes[0]["url"] = Quote(OtherWebhook);
            second = DiscordSettings.ParseForReload(fixture.Render());
            Check(!first.HasSameWebhookSettings(second) && first.HasSameBotSettings(second), "Direct URL edits change only webhook settings");
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_BOT_TOKEN", "other-resolved-token");
            DiscordSettings third = DiscordSettings.ParseForReload(fixture.Render());
            Check(!second.HasSameBotSettings(third) && second.HasSameWebhookSettings(third), "Resolved bot secret change detected");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvA, null);
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_BOT_TOKEN", null);
        }
    }

    private static void BotChanged(DiscordSettings original, Fixture fixture, string title)
    {
        DiscordSettings candidate = DiscordSettings.ParseForReload(fixture.Render());
        Check(!original.HasSameBotSettings(candidate) && original.HasSameWebhookSettings(candidate), title);
    }
    private static void WebhooksChanged(DiscordSettings original, Fixture fixture, string title)
    {
        DiscordSettings candidate = DiscordSettings.ParseForReload(fixture.Render());
        Check(original.HasSameBotSettings(candidate) && !original.HasSameWebhookSettings(candidate), title);
    }
    private static void StrictRejected(string yaml, string title)
    {
        bool rejected = false;
        try { DiscordSettings.ParseForReload(yaml); }
        catch (InvalidDataException error) { rejected = true; Check(error.InnerException == null, "Strict errors omit unsafe inner exception"); Safe(error.ToString()); }
        Check(rejected, title + " rejects whole reload");
    }
    private static void ReloadReadRejected(string root, string title)
    {
        bool rejected = false;
        try { DiscordSettings.ReadReloadText(root); }
        catch (InvalidDataException error) { rejected = true; Check(error.InnerException == null, "Read errors omit unsafe inner exception"); Safe(error.ToString()); }
        Check(rejected, title);
    }
    private static DiscordSettings Load(string yaml, List<string>? logs = null)
    {
        string root = NextRoot(); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "discord.yml"); byte[] original = Utf8.GetBytes(yaml); File.WriteAllBytes(path, original);
        var captured = logs ?? new List<string>(); DiscordSettings settings = DiscordSettings.Load(root, captured.Add);
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "Existing YAML/comments/secrets never rewritten");
        Check(!File.Exists(Path.Combine(root, "discord.cfg")), "Loading YAML never generates CFG");
        Safe(string.Join("\n", captured)); return settings;
    }
    private static void Invalid(byte[] original, string title)
    {
        string root = NextRoot(); Directory.CreateDirectory(root); string path = Path.Combine(root, "discord.yml"); File.WriteAllBytes(path, original);
        var logs = new List<string>(); bool rejected = false;
        try { DiscordSettings.Load(root, logs.Add); }
        catch (InvalidDataException error) { rejected = true; Check(error.InnerException == null, "Unsafe parser inner exception omitted"); Safe(error.ToString()); }
        Check(rejected, title + " rejected with InvalidDataException");
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "Rejected YAML preserved without rewrite"); Safe(string.Join("\n", logs));
        try { StrictRejected(Utf8.GetString(original), title); }
        catch (DecoderFallbackException) { ReloadReadRejected(root, title); }
    }
    private static void Safe(string diagnostic) => Check(!diagnostic.Contains(Secret) && !diagnostic.Contains(SafeWebhook) && !diagnostic.Contains(OtherWebhook) && !diagnostic.Contains("environment-only-"), "Diagnostics omit YAML values/URLs/secrets");
    private static string NextRoot() => Path.Combine(_root, "case-" + (++_cases));
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    private static Dictionary<string, string> Route(string name, string url) => new Dictionary<string, string>
    { ["name"] = Quote(name), ["enabled"] = "true", ["url"] = Quote(url), ["events"] = "['server.status']", ["username"] = "'ServerManager'", ["avatar_url"] = "''" };
    private static void Check(bool condition, string title) { if (!condition) throw new InvalidOperationException("FAILED: " + title); ++_checks; }

    private sealed class Fixture
    {
        internal readonly Dictionary<string, string> Bot = new Dictionary<string, string>
        { ["enabled"] = "true", ["token"] = "'offline-test-token'", ["guild_ids"] = "['123']", ["admin_channel_ids"] = "['456']", ["chat_channel_ids"] = "[]", ["admin_user_ids"] = "['789']" };
        internal readonly List<Dictionary<string, string>> Routes = new List<Dictionary<string, string>> { Route("first", SafeWebhook), Route("second", OtherWebhook) };
        internal string? BotRaw, WebhooksRaw, FirstRouteRaw;
        internal string Render()
        {
            var text = new StringBuilder(); Map(text, "bot", Bot, BotRaw);
            if (WebhooksRaw != null) text.Append("webhooks: ").Append(WebhooksRaw).Append('\n');
            else if (Routes.Count == 0) text.Append("webhooks: []\n");
            else
            {
                text.Append("webhooks:\n");
                for (int i = 0; i < Routes.Count; i++)
                {
                    if (i == 0 && FirstRouteRaw != null) { text.Append("  - ").Append(FirstRouteRaw).Append('\n'); continue; }
                    text.Append("  -\n"); foreach (var pair in Routes[i]) text.Append("    ").Append(pair.Key).Append(": ").Append(pair.Value).Append('\n');
                }
            }
            return text.ToString();
        }
        private static void Map(StringBuilder text, string name, Dictionary<string, string> values, string? raw)
        {
            if (raw != null) { text.Append(name).Append(": ").Append(raw).Append('\n'); return; }
            text.Append(name).Append(":\n"); foreach (var pair in values) text.Append("  ").Append(pair.Key).Append(": ").Append(pair.Value).Append('\n');
        }
    }
}
