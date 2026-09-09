// Source-linked production settings/runtime with inert Discord and game boundaries.
// All file/environment changes stay in the owned helper process and temp root.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerManager;
using ServerManager.Discord;
using ServerManager.Events;

internal static class DiscordReloadSmoke
{
    internal const string Secret = "OFFLINE_SECRET_MUST_NOT_APPEAR";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Runtime = typeof(DiscordRuntime);
    private static string _root = string.Empty;
    private static int _cases, _checks;
    internal static int MainThread;

    private static int Main(string[] args)
    {
        try
        {
            _root = args.Single();
            MainThread = Thread.CurrentThread.ManagedThreadId;
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_BOT_TOKEN", null);
            NoClientStartup(); DisabledRecovery(); MalformedStartupRecovery();
            StartupIsolationThenStrictReload(); DebounceAndSameMetadata(); DirectWebhookUrlReload(); AnonymousPrefixReload(); WebhookLanguageReload(); SteamIdOptionReload();
            WholeDocumentLastGood(); OperatorRouteReload(); GroupedRouteReload(); AllBotSettingsReload(); GuildSettingsLifecycle(); ChatSettingsLifecycle(); AdminChatLifecycle(); CandidateFailure();
            StopAndWorldGuards(); DelayedGatewayActivation(); AdminOperations();
            Check(FakeLog.Messages.All(message => !message.Contains(Secret)),
                "No token, webhook credential, malformed YAML value or exception secret was logged");
            Check(DiscordWebhooks.OffThreadReloads == 0, "All webhook reloads applied on main thread");
            Check(DiscordCommands.OffThreadTicks == 0, "All command ticks remained on main thread");
            Console.WriteLine("PASS: DiscordRuntime live YAML reload (" + _checks +
                " assertions, " + _cases + " isolated server lifecycles; no network/game).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally { DiscordRuntime.Stop(); }
    }

    private static void NoClientStartup()
    {
        NewWorld(null, server: false);
        int components = DiscordWebhooks.All.Count;
        DiscordRuntime.Start();
        Check(Session == null, "Client does not create Discord session");
        Check(!File.Exists(ConfigPath), "Client never creates YAML");
        Check(DiscordWebhooks.All.Count == components, "Client creates no sender");
        ZNet.instance = null;
        DiscordRuntime.Start();
        Check(Session == null, "Absent world does not create Discord session");
    }

    private static void AdminOperations()
    {
        NewWorld(Config(Secret + "_name")); DiscordRuntime.Start();
        ServerManagerCommandResult status = DiscordRuntime.ExecuteAdminOperation("status");
        Check(status.Success && status.Code == "discord_status" && status.Message.Contains("runtime_active: true") &&
            status.Message.Contains("bot_enabled: true") && status.Message.Contains("configured_webhook_routes: 1"),
            "Admin status reports active runtime and configured components");
        Check(status.Message.Contains("chat_enabled: true") && status.Message.Contains("chat_channel_count: 1") && Gateway!.ReceiveMessages,
            "An admin-channel-only configuration enables message intake and reports its effective channel count");
        Check(!status.Message.Contains(Secret) && !status.Message.Contains("https://") && status.Data.Count == 0,
            "Admin status never exposes configured names, token, URL or hidden data");
        Check(DiscordRuntime.ExecuteAdminOperation("test").Code == "discord_no_test_route" && Hooks.Queued == 0,
            "Test does not broadcast to routes that did not select server.announcement");
        Check(DiscordRuntime.ExecuteAdminOperation("https://example.invalid/").Code == "invalid_command",
            "Admin operation is not an arbitrary URL sender");
        Apply(Config(Secret + "_name", bot: false).Replace("['server.status']", "['server.announcement']"));
        ServerManagerCommandResult result = DiscordRuntime.ExecuteAdminOperation("test");
        Check(result.Success && result.Code == "discord_test_queued" && result.Message.Contains("does not confirm delivery"),
            "Explicit test reports queue acceptance without claiming remote delivery");
        Check(Hooks.Queued == 1 && Hooks.LastEvent?.Kind == "server.announcement" &&
            Hooks.LastEvent.Fields["test"] == "true" && Hooks.LastEvent.Fields["title"].Contains("TEST") &&
            Hooks.LastEvent.Fields["message"].Contains("not a game announcement"),
            "Test uses only the configured event router and is unmistakably marked");
        Check(!string.Join(" ", Hooks.LastEvent!.Fields.Values).Contains(Secret), "Test payload has no configured credentials");
        Hooks.RejectNext = true;
        Check(DiscordRuntime.ExecuteAdminOperation("test").Code == "discord_test_not_queued" && Hooks.Queued == 1,
            "Sender rejection cannot report queued success or retry");
        ZNet.World = new object();
        Check(DiscordRuntime.ExecuteAdminOperation("test").Code == "discord_inactive" && Hooks.Queued == 1,
            "A stale world's session cannot send a test");
        DiscordRuntime.Stop();
        status = DiscordRuntime.ExecuteAdminOperation("status");
        Check(status.Success && status.Message.Contains("runtime_active: false"), "Stopped runtime reports inactive");
        ZNet.instance!.Server = false;
        Check(DiscordRuntime.ExecuteAdminOperation("test").Code == "server_only" &&
            DiscordRuntime.ExecuteAdminOperation("status").Code == "server_only", "Client cannot inspect or send server Discord operations");
    }

    private static void DisabledRecovery()
    {
        NewWorld(null);
        DiscordRuntime.Start();
        Check(File.Exists(ConfigPath), "Missing file generated only at server Start");
        Check(Session != null && Hooks.Settings.WebhookRoutes.Count == 0,
            "Disabled defaults retain reload monitor");
        int bots = DiscordCommands.All.Count;
        Apply(Config("enabled1", bot: false));
        Check(RouteName == "enabled1", "Enabled webhook recovered from disabled start");
        Check(DiscordCommands.All.Count == bots, "Webhook activation does not create a disabled bot");
        DiscordRuntime.Publish(new ServerManagerEvent());
        Check(Hooks.Queued == 1, "Newly enabled sender receives events");
        Apply("{}\n");
        Check(Hooks.Settings.WebhookRoutes.Count == 0 && Session != null,
            "Disabling all routes keeps monitor alive");
        Apply(Config("enabled2", bot: false));
        Check(RouteName == "enabled2", "Repeated disabled-to-enabled transitions work");
    }

    private static void MalformedStartupRecovery()
    {
        NewWorld("webhooks: [" + Secret + "\n");
        string original = File.ReadAllText(ConfigPath, Utf8);
        DiscordRuntime.Start();
        Check(Session != null && Hooks.Settings.WebhookRoutes.Count == 0,
            "Malformed initial YAML retains disabled monitor");
        Check(File.ReadAllText(ConfigPath, Utf8) == original, "Malformed startup YAML is not rewritten");
        Apply(Config("recovered", bot: true));
        Check(RouteName == "recovered" && Commands != null,
            "Correcting malformed startup activates webhook and bot live");
    }

    private static void StartupIsolationThenStrictReload()
    {
        string badBot = Config("startup1").Replace("token: '" + Secret + "_A'", "token: ''");
        NewWorld(badBot);
        DiscordRuntime.Start();
        Check(RouteName == "startup1" && Commands == null,
            "Startup retains valid webhook beside invalid bot");
        object original = Session!;
        Apply(badBot.Replace("startup1", "rejected"));
        Check(ReferenceEquals(Session, original) && RouteName == "startup1",
            "Reload rejects entire partially invalid document");
        Apply(Config("corrected"));
        Check(RouteName == "corrected" && Commands != null,
            "Strict correction replaces disabled bot snapshot");
    }

    private static void DebounceAndSameMetadata()
    {
        NewWorld(Config("routeAAA"));
        DiscordRuntime.Start();
        DiscordWebhooks hooks = Hooks;
        DiscordCommands commands = Commands!;
        DiscordGateway gateway = Gateway!;
        DateTime stamp = File.GetLastWriteTimeUtc(ConfigPath);
        long length = new FileInfo(ConfigPath).Length;

        Write(Config("routeBBB"));
        File.SetLastWriteTimeUtc(ConfigPath, stamp);
        Check(new FileInfo(ConfigPath).Length == length && File.GetLastWriteTimeUtc(ConfigPath) == stamp,
            "Regression fixture preserves both size and last-write timestamp");
        Observe();
        Check(hooks.ReloadCalls == 0 && RouteName == "routeAAA", "First changed read is debounced");
        Write(Config("routeCCC"));
        File.SetLastWriteTimeUtc(ConfigPath, stamp);
        Observe();
        Check(hooks.ReloadCalls == 0, "Unstable consecutive observations are not applied");
        Observe();
        Check(ReferenceEquals(Hooks, hooks) && hooks.ReloadCalls == 1 && RouteName == "routeCCC",
            "Stable actual bytes reload despite identical filesystem metadata");
        Check(ReferenceEquals(Commands, commands) && ReferenceEquals(Gateway, gateway),
            "Webhook-only reload retains bot and Gateway");
        Apply(Config("routeCCC", shout: true));
        Check(Hooks.Settings.WebhookRoutes[0].Events.Contains("chat.shout"),
            "Shout route selection is live without an extra privacy toggle");
        Check(ReferenceEquals(Commands, commands) && hooks.ReloadCalls == 2,
            "Event-filter-only reload keeps bot and reloads sender");
        Apply("# comments do not replace components\n" + Config("routeCCC", shout: true));
        Check(hooks.ReloadCalls == 2 && ReferenceEquals(Commands, commands),
            "Semantically unchanged comments do not recreate components");
    }

    private static void DirectWebhookUrlReload()
    {
        const string environment = "SERVERMANAGER_DISCORD_RELOAD_TEST_WEBHOOK";
        const string replacementUrl = "https://discord.com/api/webhooks/321/offline-replacement";
        string legacy = Config("legacyenv").Replace("    events:", "    url_env: ''\n    events:");
        NewWorld(legacy); DiscordRuntime.Start();
        Check(Commands != null && Hooks.Settings.WebhookRoutes.Count == 0 && File.ReadAllText(ConfigPath, Utf8) == legacy,
            "A removed url_env key disables only its startup route and never rewrites the old file");
        Apply(Config("directurl"));
        Check(RouteName == "directurl" && Commands != null,
            "Removing url_env and supplying a direct URL restores the route through strict live reload");
        DiscordCommands commands = Commands!;
        DiscordGateway gateway = Gateway!;
        DiscordWebhooks hooks = Hooks;
        object session = Session!;
        string direct = Config("directurl").Replace("https://discord.com/api/webhooks/123/" + Secret + "_HOOK", replacementUrl);
        try
        {
            Environment.SetEnvironmentVariable(environment, "https://discord.com/api/webhooks/999/" + Secret + "_ENV");
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_WEBHOOK_1", "https://discord.com/api/webhooks/999/" + Secret + "_NUMBERED");
            Apply(direct);
            Check(ReferenceEquals(Session, session) && ReferenceEquals(Commands, commands) && ReferenceEquals(Gateway, gateway) &&
                ReferenceEquals(Hooks, hooks) && hooks.Settings.WebhookRoutes.Single().Url == replacementUrl,
                "Direct URL live edits update the sender without reconnecting the bot or consulting webhook environment variables");
            int reloads = hooks.ReloadCalls;
            Environment.SetEnvironmentVariable(environment, "");
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_WEBHOOK_1", "invalid-" + Secret);
            Apply("# legacy environment variables have no effect\n" + direct);
            Check(ReferenceEquals(Hooks, hooks) && hooks.ReloadCalls == reloads && hooks.Settings.WebhookRoutes.Single().Url == replacementUrl,
                "Changing irrelevant webhook environment variables does not disable routes or replace equal settings");
            foreach (string enabled in new[] { "true", "false" })
            foreach (string value in new[] { "''", "'" + environment + "'", "null", "[]", "{}", "true", "'BAD=" + Secret + "'" })
            {
                string candidate = Config("blockedenv", admins: "[]")
                    .Replace("    enabled: true", "    enabled: " + enabled)
                    .Replace("    events:", "    url_env: " + value + "\n    events:");
                Apply(candidate);
                Check(ReferenceEquals(Session, session) && ReferenceEquals(Commands, commands) && !commands.Disposed &&
                    commands.Settings.AdminUserIds.SetEquals(new[] { "789" }) && hooks.ReloadCalls == reloads &&
                    hooks.Settings.WebhookRoutes.Single().Url == replacementUrl && File.ReadAllText(ConfigPath, Utf8) == candidate,
                    "Removed url_env on enabled or disabled routes rejects all changes and preserves the last-good authority/URL without rewriting");
            }
            foreach (string invalidUrl in new[] { "", "http://discord.com/api/webhooks/123/token", "https://example.invalid/" + Secret })
            {
                Apply(direct.Replace(replacementUrl, invalidUrl));
                Check(ReferenceEquals(Session, session) && ReferenceEquals(Commands, commands) &&
                    hooks.ReloadCalls == reloads && hooks.Settings.WebhookRoutes.Single().Url == replacementUrl,
                    "An empty or invalid direct URL retains the entire last-good configuration");
            }
            Apply(Config("directagain"));
            Check(ReferenceEquals(Commands, commands) && ReferenceEquals(Gateway, gateway) && RouteName == "directagain" &&
                hooks.Settings.WebhookRoutes.Single().Url.EndsWith(Secret + "_HOOK", StringComparison.Ordinal),
                "A corrected direct URL applies after rejected legacy keys and invalid URL edits");
        }
        finally
        {
            Environment.SetEnvironmentVariable(environment, null);
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_WEBHOOK_1", null);
        }
    }

    private static void AnonymousPrefixReload()
    {
        NewWorld(Config("anonymous")); DiscordRuntime.Start();
        object session = Session!;
        DiscordWebhooks hooks = Hooks;
        DiscordCommands commands = Commands!;
        DiscordGateway gateway = Gateway!;
        Check(hooks.Settings.WebhookRoutes.Single().AnonymousPrefix == string.Empty,
            "An omitted prefix preserves original-name mode");

        string WithPrefix(string scalar) => Config("anonymous").Replace("    username:",
            "    anonymous_prefix: " + scalar + "\n    username:");
        Apply(WithPrefix("'Anonymous'"));
        Check(ReferenceEquals(Session, session) && ReferenceEquals(Hooks, hooks) &&
            ReferenceEquals(Commands, commands) && ReferenceEquals(Gateway, gateway) &&
            hooks.ReloadCalls == 1 && hooks.Settings.WebhookRoutes.Single().AnonymousPrefix == "Anonymous",
            "Enabling anonymity is a live webhook-only change without bot or gateway replacement");
        Apply(WithPrefix("'  Anonymous  '"));
        Check(hooks.ReloadCalls == 1 && hooks.Settings.WebhookRoutes.Single().AnonymousPrefix == "Anonymous",
            "Equal trimmed prefixes do not reload the sender");
        Apply(WithPrefix("'o o'"));
        Check(hooks.ReloadCalls == 2 && hooks.Settings.WebhookRoutes.Single().AnonymousPrefix == "o o",
            "A custom printable prefix applies through the normal settings comparison");
        Apply(WithPrefix("'익명 😀'"));
        Check(hooks.ReloadCalls == 3 && hooks.Settings.WebhookRoutes.Single().AnonymousPrefix == "익명 😀",
            "Printable non-ASCII prefixes survive live YAML reads");

        foreach (string scalar in new[] { "[]", "{}", "null", "'" + new string('x', 33) + "'",
            "\"" + Secret + "\\n\"", "\"\\tAnonymous\"", "\"Anonymous\\u200B\"", "\"Anonymous\\u2028\"" })
        foreach (string enabled in new[] { "true", "false" })
        {
            string invalid = WithPrefix(scalar).Replace("    enabled: true", "    enabled: " + enabled)
                .Replace("admin_user_ids: ['789']", "admin_user_ids: []");
            Apply(invalid);
            Check(ReferenceEquals(Session, session) && ReferenceEquals(Hooks, hooks) &&
                ReferenceEquals(Commands, commands) && hooks.ReloadCalls == 3 &&
                hooks.Settings.WebhookRoutes.Single().AnonymousPrefix == "익명 😀" &&
                commands.Settings.AdminUserIds.SetEquals(new[] { "789" }) && File.ReadAllText(ConfigPath, Utf8) == invalid,
                "Invalid prefixes on enabled or disabled routes preserve the full last-good document without rewriting it");
        }

        Apply(WithPrefix("'  '"));
        Check(hooks.ReloadCalls == 4 && hooks.Settings.WebhookRoutes.Single().AnonymousPrefix.Length == 0 &&
            ReferenceEquals(Commands, commands), "Whitespace-only prefix disables anonymity without replacing the bot");
        Apply(Config("anonymous"));
        Check(hooks.ReloadCalls == 4, "Removing an already empty prefix is semantically unchanged");
        Apply(WithPrefix("'" + new string('a', 32) + "'"));
        Check(hooks.ReloadCalls == 5 && hooks.Settings.WebhookRoutes.Single().AnonymousPrefix.Length == 32,
            "The exact prefix length boundary can be enabled after rejected edits");

        NewWorld(WithPrefix("\"" + Secret + "\\n\"")); DiscordRuntime.Start();
        Check(Commands != null && Hooks.Settings.WebhookRoutes.Count == 0,
            "Malformed startup prefix disables its route without disabling a valid bot");
        Apply(WithPrefix("'Anonymous'"));
        Check(Hooks.Settings.WebhookRoutes.Single().AnonymousPrefix == "Anonymous",
            "Correcting the invalid startup prefix restores its route through strict reload");
    }

    private static void WebhookLanguageReload()
    {
        NewWorld(Config("language")); DiscordRuntime.Start();
        object session = Session!;
        DiscordWebhooks hooks = Hooks;
        DiscordCommands commands = Commands!;
        DiscordGateway gateway = Gateway!;
        string WithLanguage(string scalar) => Config("language").Replace("    username:",
            "    language: " + scalar + "\n    username:");
        Check(hooks.Settings.WebhookRoutes.Single().Language == "English", "Omitted webhook language starts in English");
        Apply(WithLanguage("English"));
        Check(hooks.ReloadCalls == 0, "Explicit English does not reload an English default");
        Apply(WithLanguage("Korean"));
        Check(ReferenceEquals(Session, session) && ReferenceEquals(Hooks, hooks) &&
            ReferenceEquals(Commands, commands) && ReferenceEquals(Gateway, gateway) && hooks.ReloadCalls == 1 &&
            hooks.Settings.WebhookRoutes.Single().Language == "Korean", "Language is a webhook-only live change without reconnecting the bot");
        foreach (string scalar in new[] { "''", "[]", "null", "'../English'", "'한국어'", "\"Korean\\n\"", "'" + new string('x', 65) + "'" })
        foreach (string enabled in new[] { "true", "false" })
        {
            string invalid = WithLanguage(scalar).Replace("    enabled: true", "    enabled: " + enabled)
                .Replace("admin_user_ids: ['789']", "admin_user_ids: []");
            Apply(invalid);
            Check(ReferenceEquals(Session, session) && ReferenceEquals(Hooks, hooks) && hooks.ReloadCalls == 1 &&
                hooks.Settings.WebhookRoutes.Single().Language == "Korean" &&
                commands.Settings.AdminUserIds.SetEquals(new[] { "789" }) && File.ReadAllText(ConfigPath, Utf8) == invalid,
                "Invalid language on active/inactive routes retains the full last-good settings and file bytes");
        }
        Apply(WithLanguage("Custom_Language"));
        Check(hooks.ReloadCalls == 2 && hooks.Settings.WebhookRoutes.Single().Language == "Custom_Language",
            "Safe unknown language is accepted for external translations or English fallback");
        Apply(Config("language"));
        Check(hooks.ReloadCalls == 3 && hooks.Settings.WebhookRoutes.Single().Language == "English",
            "Removing a webhook language restores its English default live");
    }

    private static void SteamIdOptionReload()
    {
        NewWorld(Config("steamid")); DiscordRuntime.Start();
        object session = Session!;
        DiscordWebhooks hooks = Hooks;
        DiscordCommands commands = Commands!;
        DiscordGateway gateway = Gateway!;
        string WithSteamId(string scalar) => Config("steamid").Replace("    username:",
            "    include_steam_id: " + scalar + "\n    username:");
        Check(!hooks.Settings.WebhookRoutes.Single().IncludeSteamId, "Omitted Steam-ID option starts disabled");
        Apply(WithSteamId("false"));
        Check(hooks.ReloadCalls == 0, "Explicit false does not reload the default-disabled Steam-ID preference");
        Apply(WithSteamId("true"));
        Check(ReferenceEquals(Session, session) && ReferenceEquals(Hooks, hooks) &&
            ReferenceEquals(Commands, commands) && ReferenceEquals(Gateway, gateway) && hooks.ReloadCalls == 1 &&
            hooks.Settings.WebhookRoutes.Single().IncludeSteamId,
            "Enabling Steam-ID display is a live webhook-only change without bot or gateway replacement");
        Apply("# Steam ID preference unchanged\n" + WithSteamId("true"));
        Check(hooks.ReloadCalls == 1, "An equivalent enabled Steam-ID preference does not rebuild the sender");
        foreach (string scalar in new[] { "yes", "no", "on", "off", "1", "0", "True", "FALSE", "'true'", "\"false\"",
            "''", "' '", "[]", "{}", "null", "~", "'" + Secret + "'" })
        foreach (bool enabled in new[] { true, false })
        {
            string invalid = WithSteamId(scalar).Replace("    enabled: true", "    enabled: " + Flag(enabled))
                .Replace("admin_user_ids: ['789']", "admin_user_ids: []");
            Apply(invalid);
            Check(ReferenceEquals(Session, session) && ReferenceEquals(Hooks, hooks) && ReferenceEquals(Commands, commands) &&
                ReferenceEquals(Gateway, gateway) && hooks.ReloadCalls == 1 && hooks.Settings.WebhookRoutes.Single().IncludeSteamId &&
                commands.Settings.AdminUserIds.SetEquals(new[] { "789" }) && File.ReadAllText(ConfigPath, Utf8) == invalid,
                "Invalid Steam-ID booleans on enabled/disabled routes retain all last-good settings and preserve the rejected file");
        }
        Apply(WithSteamId("false"));
        Check(hooks.ReloadCalls == 2 && !hooks.Settings.WebhookRoutes.Single().IncludeSteamId &&
            ReferenceEquals(Commands, commands) && ReferenceEquals(Gateway, gateway),
            "Clearing Steam-ID display recovers after invalid edits without restarting the bot");
        Apply(Config("steamid"));
        Check(hooks.ReloadCalls == 2, "Omitting an already false Steam-ID preference is semantically unchanged");
        Apply(WithSteamId("true"));
        Apply(Config("steamid"));
        Check(hooks.ReloadCalls == 4 && !hooks.Settings.WebhookRoutes.Single().IncludeSteamId &&
            ReferenceEquals(Session, session) && ReferenceEquals(Commands, commands),
            "Removing a true Steam-ID preference restores false through webhook-only reload");

        NewWorld(WithSteamId("yes")); DiscordRuntime.Start();
        Check(Commands != null && Hooks.Settings.WebhookRoutes.Count == 0,
            "An invalid startup Steam-ID preference isolates its route and leaves the bot running");
        Apply(WithSteamId("true"));
        Check(Hooks.Settings.WebhookRoutes.Single().IncludeSteamId,
            "Correcting a rejected startup Steam-ID preference restores its route through strict reload");

        NewWorld(Config("steamid", bot: false)); DiscordRuntime.Start();
        session = Session!; hooks = Hooks;
        Apply(WithSteamId("true").Replace("bot:\n  enabled: true", "bot:\n  enabled: false"));
        Check(ReferenceEquals(Session, session) && ReferenceEquals(Hooks, hooks) && Commands == null && Gateway == null &&
            hooks.ReloadCalls == 1 && hooks.Settings.WebhookRoutes.Single().IncludeSteamId,
            "A webhook-only server can change Steam-ID display without creating a bot connection");
    }

    private static void WholeDocumentLastGood()
    {
        NewWorld(Config("lastgood"));
        DiscordRuntime.Start();
        object session = Session!;
        DiscordWebhooks hooks = Hooks;
        string[] invalid =
        {
            "webhooks: [" + Secret + "\n",
            "privacy:\n  relay_shouts: false\n  relay_shouts: '" + Secret + "'\n",
            Config("badgrant").Replace("user_ids: ['789']", "user_ids: ['invalid']"),
            Config("bad_priv") + "privacy: {relay_shouts: true}\n",
            Config("bad_priv") + "privacy: {include_coordinates: false}\n",
            Config("bad_priv") + "privacy: {include_inventory: false}\n",
            Config("bad_priv") + "privacy: {include_plugin_list: true}\n",
            Config("bad_priv") + "privacy: {}\n",
            Config("badtoken").Replace("token: '" + Secret + "_A'", "token: 'bad " + Secret + "'"),
            Config("badroute").Replace("https://discord.com/api/webhooks/123/", "https://invalid.example/"),
            Config("removedstart").Replace("['server.status']", "['server.status','server.started']"),
            Config("removedjoin").Replace("['server.status']", "['player.first_join']"),
            Config("disabledstart").Replace("    enabled: true", "    enabled: false").Replace("['server.status']", "['server.started']"),
            Config("disabledjoin").Replace("    enabled: true", "    enabled: false").Replace("['server.status']", "['player.first_join']"),
            Config("badlimit") + "rcon: {minimum_interval_seconds: 1}\n",
            Config("badlimit") + "rcon: {maximum_output_characters: 1800}\n",
            Config("badlimit") + "rcon: {}\n",
            Config("badlimit").Replace("  enabled: true\n", "  enabled: true\n  command_timeout_seconds: 15\n"),
            Config("badmulti") + "---\n{}\n"
        };
        foreach (string yaml in invalid)
        {
            Apply(yaml);
            Check(ReferenceEquals(Session, session) && RouteName == "lastgood" && hooks.ReloadCalls == 0,
                "Invalid stable document preserves entire active session and routes");
        }
        Write(new string('x', 128 * 1024 + 1)); Observe(); Observe();
        Check(ReferenceEquals(Session, session) && RouteName == "lastgood", "Oversized file retains last good state");
        File.WriteAllBytes(ConfigPath, new byte[] { 0xc3, 0x28 }); Observe(); Observe();
        Check(ReferenceEquals(Session, session) && RouteName == "lastgood", "Invalid UTF8 retains last good state");
        File.Delete(ConfigPath); Observe(); Observe();
        Check(!File.Exists(ConfigPath) && ReferenceEquals(Session, session),
            "Missing reload file is not recreated and last good state remains");
        Apply(Config("fixednow"));
        Check(RouteName == "fixednow", "Correction after malformed/oversized/missing file recovers");
    }

    private static void OperatorRouteReload()
    {
        NewWorld(Config("operatorbase")); DiscordRuntime.Start();
        DiscordWebhooks initial = Hooks;
        string yaml = Config("operatorroute").Replace("events: ['server.status']",
            "events: ['security.alert','security.admin_bypass','character.validation','character.shadow_stalled','character.revision_observed','connection.rejected']");
        Apply(yaml);
        Check(ReferenceEquals(initial, Hooks) && Hooks.Settings.WebhookRoutes[0].Events.Count == 6,
            "Explicit operator filter reload preserves the active bot and sender");
        Apply(yaml.Replace("connection.rejected", "connection.unknown"));
        Check(ReferenceEquals(initial, Hooks) && Hooks.Settings.WebhookRoutes[0].Events.Contains("connection.rejected"),
            "Unknown operator kind preserves the full last-good route");
        Apply(yaml.Replace("admin_user_ids: ['789']", "admin_user_ids: ['987']"));
        Check(!ReferenceEquals(initial, Hooks) && ReferenceEquals(Hooks.InheritedFrom, initial) && Hooks.InheritCalls == 2,
            "Bot-driven session replacement copies operator history before and after retirement");
        DiscordWebhooks enabled = Hooks;
        Apply(yaml.Replace("enabled: true", "enabled: false"));
        Check(ReferenceEquals(Hooks.InheritedFrom, enabled), "Disabling the bot still preserves recent operator history");
    }

    private static void GroupedRouteReload()
    {
        string ConfigFor(string filter) => Config("grouped").Replace("['server.status']", "['" + filter + "']");
        NewWorld(ConfigFor("security.alert")); DiscordRuntime.Start();
        object session = Session!;
        DiscordWebhooks hooks = Hooks;
        DiscordCommands commands = Commands!;
        DiscordGateway gateway = Gateway!;
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["server.ready"] = "server.status", ["server.shutdown"] = "server.status",
            ["player.login"] = "player.connection", ["player.leave"] = "player.connection",
            ["raid.started"] = "raid.status", ["raid.ended"] = "raid.status",
            ["security.detection"] = "security.alert", ["security.response"] = "security.alert",
            ["character.save_rejected"] = "character.validation", ["character.validation_observed"] = "character.validation",
            ["player.death"] = "player.death", ["combat.pvp_kill"] = "player.death"
        };
        string[] synthetic = { "server.status", "player.connection", "raid.status", "security.alert", "character.validation" };
        // Runtime and parsing are real here; the inert sender uses the production
        // source-to-filter projection. Actual sender queues/dedup have separate tests.
        foreach (string filter in new[] { "server.status", "player.connection", "raid.status", "security.alert", "character.validation", "player.death" })
        {
            Apply(ConfigFor(filter));
            Check(ReferenceEquals(Session, session) && ReferenceEquals(Hooks, hooks) && ReferenceEquals(Commands, commands) &&
                ReferenceEquals(Gateway, gateway) && hooks.Settings.WebhookRoutes.Single().Events.SetEquals(new[] { filter }),
                "Grouped filter switch replaces selection without restarting the bot or retaining a previous group");
            foreach (string kind in sources.Keys.Concat(synthetic))
            {
                int queued = hooks.Queued;
                var source = new ServerManagerEvent { Kind = kind };
                DiscordRuntime.Publish(source);
                bool selected = sources.TryGetValue(kind, out string? projected) && projected == filter;
                Check(hooks.Queued == queued + (selected ? 1 : 0) && (!selected ||
                    ReferenceEquals(hooks.LastEvent, source) && hooks.LastEvent!.Kind == kind),
                    "Live grouped selection routes only its real source kinds without rewriting the internal event: " + filter + "/" + kind);
            }
            int unchanged = hooks.ReloadCalls;
            Apply("# equivalent filter\n" + ConfigFor(filter));
            Check(hooks.ReloadCalls == unchanged, "Equivalent grouped selection does not reset sender settings");
        }
        int reloads = hooks.ReloadCalls;
        foreach (string removed in new[] { "server.ready", "server.shutdown", "player.login", "player.leave", "raid.started", "raid.ended",
            "combat.pvp_kill", "character.save_rejected", "character.validation_observed",
            "security.detection", "security.response" })
        foreach (bool enabled in new[] { true, false })
        {
            string bad = ConfigFor(removed).Replace("admin_user_ids: ['789']", "admin_user_ids: ['987']");
            if (!enabled) bad = bad.Replace("    enabled: true", "    enabled: false");
            Apply(bad);
            Check(ReferenceEquals(Session, session) && ReferenceEquals(Commands, commands) && ReferenceEquals(Gateway, gateway) &&
                ReferenceEquals(Hooks, hooks) && hooks.ReloadCalls == reloads &&
                commands.Settings.AdminUserIds.SetEquals(new[] { "789" }) &&
                hooks.Settings.WebhookRoutes.Single().Events.SetEquals(new[] { "player.death" }) &&
                File.ReadAllText(ConfigPath, Utf8) == bad,
                "Removed selector rejects the complete edit, retaining last-good authority/filters and user file bytes: " + removed);
        }
        Apply(ConfigFor("character.validation"));
        Check(hooks.ReloadCalls == reloads + 1 && hooks.Settings.WebhookRoutes.Single().Events.SetEquals(new[] { "character.validation" }),
            "Corrected grouped selector recovers after rejected old names without restarting the session");
    }

    private static void AllBotSettingsReload()
    {
        NewWorld(Config("reload01"));
        DiscordRuntime.Start();
        DiscordCommands oldCommands = Commands!;
        DiscordGateway oldGateway = Gateway!;
        DiscordWebhooks oldHooks = Hooks;
        Task prepared = Stage(Config("reload02", token: Secret + "_B"));
        oldCommands.Queued = 1;
        oldHooks.Queued = 1;
        FinishRead(prepared);
        Check(!ReferenceEquals(Commands, oldCommands) && Commands!.Settings.BotToken == Secret + "_B",
            "Bot token change creates replacement with new immutable settings");
        Check(ReferenceEquals(Commands!.InheritedFrom, oldCommands),
            "Replacement receives recent replay/rate state from previous bot");
        Check(oldCommands.Disposed && oldCommands.Queued == 0 && oldCommands.Executed == 0,
            "Replacement retires old queued commands before a command tick");
        Check(oldGateway.Disposed && SpinWait.SpinUntil(() => oldGateway.TokenCancelled, 1000),
            "Old Gateway retired and cancelled");
        Check(oldHooks.Disposed && oldHooks.DrainCalls == 0 && oldHooks.Queued == 0,
            "Reload discards old webhook queue without normal-shutdown drain");
        Check(RouteName == "reload02", "Whole bot replacement applies webhook changes too");

        oldCommands = Commands!;
        Apply(Config("reload02", token: Secret + "_B", admins: "['987']"));
        Check(!ReferenceEquals(Commands, oldCommands) && oldCommands.Disposed &&
            Commands!.Settings.AdminUserIds.SetEquals(new[] { "987" }), "Admin user changes replace bot immediately");
        oldCommands = Commands!;
        Apply(Config("reload02", token: Secret + "_B", admins: "[]"));
        Check(!ReferenceEquals(Commands, oldCommands) && Commands!.Settings.AdminUserIds.Count == 0,
            "Explicit empty live admin list revokes all management access");
        foreach (string removed in new[] { "  admin_role_ids: []\n", "  allowed_commands: []\n", "grants: {}\n", "rcon:\n  enabled: true\n",
            "  command_timeout_seconds: 15\n", "rcon: {minimum_interval_seconds: 1}\n", "rcon: {maximum_output_characters: 1800}\n", "rcon: {}\n",
            "  guild_id: '123'\n", "  command_channel_ids: []\n", "chat: {channel_ids: []}\n", "chat: {}\n" })
        {
            oldCommands = Commands!;
            string candidate = removed.StartsWith("  ", StringComparison.Ordinal)
                ? Config("removedkey").Replace("  guild_ids: ['123']\n", removed + "  guild_ids: ['123']\n")
                : Config("removedkey") + removed;
            Apply(candidate);
            Check(ReferenceEquals(Commands, oldCommands) && !oldCommands.Disposed && RouteName == "reload02",
                "Removed authorization, limit or single-guild/split-chat key rejects reload and retains last valid session");
        }
        oldCommands = Commands!;
        Apply(Config("disabled", bot: false));
        Check(Commands == null && oldCommands.Disposed && Session != null && RouteName == "disabled",
            "Disabling bot retires it without losing webhook or reload monitor");
        Apply(Config("reenable"));
        Check(Commands != null && RouteName == "reenable", "Bot can be re-enabled without world restart");
        Check(ReferenceEquals(Commands!.InheritedFrom, oldCommands),
            "Bot off-to-on keeps recent command history from the retired enabled bot");

        string currentYaml = Config("reenable");
        foreach (string[] change in new[]
        {
            new[] { "guild_ids: ['123']", "guild_ids: ['321']", "guild" },
            new[] { "admin_channel_ids: ['456']", "admin_channel_ids: ['654']", "command channels" },
            new[] { "admin_user_ids: ['789']", "admin_user_ids: ['987']", "admin users" }
        })
        {
            currentYaml = currentYaml.Replace(change[0], change[1]);
            oldCommands = Commands!;
            Apply(currentYaml);
            Check(!ReferenceEquals(Commands, oldCommands) && oldCommands.Disposed,
                "Changing " + change[2] + " replaces the active bot");
            Check(Commands!.Settings.HasSameBotSettings(DiscordSettings.ParseForReload(currentYaml)),
                "Replacement holds newly parsed " + change[2] + " settings");
        }
    }

    private static void GuildSettingsLifecycle()
    {
        NewWorld(Config("twoguild", guilds: "['123','321']", chatChannels: "['654']"));
        DiscordRuntime.Start();
        DiscordCommands previous = Commands!;
        DiscordWebhooks hooks = Hooks;
        Check(previous.Settings.GuildIds.SetEquals(new[] { "123", "321" }) && Gateway!.ReceiveMessages,
            "Two guilds share one active bot session and configured chat intake");
        Check(previous.Settings.CommandChannelIds.SetEquals(new[] { "456" }) && previous.Settings.AdminUserIds.SetEquals(new[] { "789" }),
            "Multi-guild startup preserves the common explicit channel and administrator sets");
        Apply(Config("twoguild", guilds: "['321','123','123']", chatChannels: "['654']"));
        Check(ReferenceEquals(Commands, previous) && ReferenceEquals(Hooks, hooks) && hooks.ReloadCalls == 0,
            "Guild order and duplicates do not reconnect the bot or sender");
        Task prepared = Stage(Config("oneguild", guilds: "['321']", chatChannels: "['654']"));
        previous.Queued = previous.QueuedChat = 1;
        previous.SeenChat.Add("accepted-before-guild-removal");
        FinishRead(prepared);
        Check(previous.Disposed && previous.Queued == 0 && previous.QueuedChat == 0 && previous.Executed == 0 && previous.ExecutedChat == 0,
            "Removing a guild retires both old queues before a command or chat tick");
        Check(!ReferenceEquals(Commands, previous) && Commands!.Settings.GuildIds.SetEquals(new[] { "321" }) &&
            Commands.Queued == 0 && Commands.QueuedChat == 0 && Commands.SeenChat.Contains("accepted-before-guild-removal"),
            "Replacement only authorizes remaining guilds and inherits replay history but no queued work");
        previous = Commands!;
        foreach (string guilds in new[] { "[]", "null", "'321'", "['321','invalid']", "[{}]" })
        {
            Apply(Config("badguild", guilds: guilds));
            Check(ReferenceEquals(Commands, previous) && !previous.Disposed && RouteName == "oneguild" &&
                previous.Settings.GuildIds.SetEquals(new[] { "321" }),
                "An invalid or empty enabled guild list preserves all last-good authority and routes");
        }
        Apply(Config("threeguild", guilds: "['321','123','999']", chatChannels: "['654']"));
        Check(previous.Disposed && Commands!.Settings.GuildIds.SetEquals(new[] { "123", "321", "999" }),
            "Adding guilds replaces the bot with the exact new authority set");
        Apply(Config("guildidle", guilds: "['321','123']", commandChannels: "[]", chatChannels: "[]"));
        Check(Commands!.Settings.BotEnabled && Commands.Settings.GuildIds.Count == 2 &&
            Commands.Settings.CommandChannelIds.Count == 0 && Commands.Settings.ChatChannelIds.Count == 0 && !Gateway!.ReceiveMessages,
            "Multiple configured guilds with both channel lists empty remain a valid idle bot");
        previous = Commands;
        Apply(Config("guildoff", bot: false, guilds: "[]"));
        Check(Commands == null && previous.Disposed && RouteName == "guildoff",
            "Disabled bot may clear every guild without disabling webhook routes or the monitor");
    }

    private static void ChatSettingsLifecycle()
    {
        NewWorld(Config("chatonly", commandChannels: "[]", chatChannels: "['456']"));
        DiscordRuntime.Start();
        Check(Commands != null && Commands.Settings.BotEnabled && Commands.Settings.CommandChannelIds.Count == 0 &&
            Commands.Settings.ChatChannelIds.SetEquals(new[] { "456" }) && Gateway!.ReceiveMessages,
            "Chat-only bot creates the dispatch handler and opts its Gateway into message events");
        ServerManagerCommandResult status = DiscordRuntime.ExecuteAdminOperation("status");
        Check(status.Message.Contains("chat_enabled: true") && status.Message.Contains("chat_channel_count: 1") &&
            !status.Message.Contains("456") && !status.Message.Contains(Secret),
            "Chat status reports only enabled/count fields without IDs or credentials");

        DiscordCommands previous = Commands!;
        Task staged = Stage(Config("chatmoved", commandChannels: "[]", chatChannels: "['654']"));
        previous.QueuedChat = 1;
        previous.SeenChat.Add("accepted-before-reload");
        previous.ChatAcceptedWhileRetiring = "accepted-during-preparation";
        FinishRead(staged);
        Check(previous.Disposed && previous.QueuedChat == 0 && previous.ExecutedChat == 0 &&
            !ReferenceEquals(Commands, previous) && Commands!.Settings.ChatChannelIds.SetEquals(new[] { "654" }),
            "A chat-channel edit retires the old chat queue before the next Unity tick");
        Check(Commands!.InheritCalls == 2 && Commands.LastInheritedDisposed &&
            Commands.SeenChat.SetEquals(new[] { "accepted-before-reload", "accepted-during-preparation" }) &&
            Commands.QueuedChat == 0,
            "Final post-retirement inheritance retains frozen chat replay history but never queued work");

        previous = Commands;
        Apply(Config("chatbad", commandChannels: "[]", chatChannels: "['invalid-channel']"));
        Check(ReferenceEquals(Commands, previous) && RouteName == "chatmoved",
            "Invalid chat-channel reload retains the entire last-good snapshot");
        Apply(Config("guildbad", commandChannels: "[]", chatChannels: "['654']").Replace("guild_ids: ['123']", "guild_ids: ['invalid-guild']"));
        Check(ReferenceEquals(Commands, previous) && RouteName == "chatmoved",
            "Invalid guild reload retains active chat and the entire last-good snapshot");
        Apply(Config("chatroute", commandChannels: "[]", chatChannels: "['654']", shout: true));
        Check(ReferenceEquals(Commands, previous) && Gateway!.ReceiveMessages && Hooks.Settings.WebhookRoutes[0].Events.Contains("chat.shout"),
            "Webhook event-filter reload does not reconnect or discard the chat dispatcher");
        staged = Stage(Config("noinputs", commandChannels: "[]", chatChannels: "[]"));
        previous.QueuedChat = 1;
        FinishRead(staged);
        Check(previous.Disposed && previous.QueuedChat == 0 && previous.ExecutedChat == 0 &&
            !ReferenceEquals(Commands, previous) && Commands!.Settings.BotEnabled &&
            Commands.Settings.CommandChannelIds.Count == 0 && Commands.Settings.ChatChannelIds.Count == 0 &&
            !Gateway!.ReceiveMessages && RouteName == "noinputs",
            "Clearing chat-only bot channels applies a safe idle snapshot and discards pending chat before Tick");
        Check(Commands!.SeenChat.SetEquals(new[] { "accepted-before-reload", "accepted-during-preparation" }) &&
            Commands.QueuedChat == 0 && DiscordRuntime.ExecuteAdminOperation("status").Message.Contains("chat_enabled: false"),
            "A bot with no input channels retains replay history but disables message intake");
        previous = Commands;
        Apply(Config("newinput", commandChannels: "[]", chatChannels: "['987']"));
        Check(previous.Disposed && ReferenceEquals(Commands!.InheritedFrom, previous) && Gateway!.ReceiveMessages &&
            Commands.SeenChat.SetEquals(new[] { "accepted-before-reload", "accepted-during-preparation" }) &&
            Commands.QueuedChat == 0 && Commands.ExecutedChat == 0,
            "Enabling a later chat channel preserves idle-state replay history without restoring discarded chat");

        previous = Commands!;
        Apply(Config("chatoff", chatChannels: "[]"));
        Check(previous.Disposed && Commands != null && Gateway!.ReceiveMessages &&
            DiscordRuntime.ExecuteAdminOperation("status").Message.Contains("chat_enabled: true"),
            "Clearing public chat while retaining admin channels keeps administrator-only message intake enabled");
        previous = Commands!;
        Apply(Config("bot_off", bot: false, commandChannels: "[]", chatChannels: "['654']"));
        Check(Commands == null && Gateway == null && previous.Disposed &&
            DiscordRuntime.ExecuteAdminOperation("status").Message.Contains("chat_enabled: false"),
            "Bot disable stops ordinary chat despite configured chat channels and preserves webhooks");
        Apply(Config("chatagain", commandChannels: "[]", chatChannels: "['654']"));
        Check(ReferenceEquals(Commands!.InheritedFrom, previous) && Gateway!.ReceiveMessages &&
            Commands.SeenChat.Contains("accepted-before-reload") && Commands.SeenChat.Contains("accepted-during-preparation"),
            "Bot off/on retains same-world chat replay history through the disabled session");

        NewWorld(Config("freshchat", commandChannels: "[]", chatChannels: "['654']"));
        DiscordRuntime.Start();
        Check(Commands!.InheritedFrom == null && Commands.SeenChat.Count == 0,
            "A new game world never inherits the old world's chat work or history");
    }

    private static void AdminChatLifecycle()
    {
        NewWorld(Config("adminchat", commandChannels: "['456']", chatChannels: "[]"));
        DiscordRuntime.Start();
        Check(Commands!.Settings.ChatRelayChannelCount == 1 && Gateway!.ReceiveMessages,
            "An admin-only bot enables ordinary message events even without public chat channels");
        DiscordCommands previous = Commands;
        Apply(Config("overlap", commandChannels: "['456','654']", chatChannels: "['456','987']"));
        ServerManagerCommandResult status = DiscordRuntime.ExecuteAdminOperation("status");
        Check(previous.Disposed && Commands!.Settings.ChatRelayChannelCount == 3 && Gateway!.ReceiveMessages &&
            status.Message.Contains("chat_enabled: true") && status.Message.Contains("chat_channel_count: 3"),
            "Message intake/status count includes both channel lists and counts overlapping channels once");
        previous = Commands;
        Task staged = Stage(Config("revokeadmin", admins: "[]", commandChannels: "['456']", chatChannels: "[]"));
        previous.QueuedChat = 1;
        FinishRead(staged);
        Check(previous.Disposed && previous.QueuedChat == 0 && previous.ExecutedChat == 0 &&
            Commands!.Settings.AdminUserIds.Count == 0 && Commands.Settings.ChatRelayChannelCount == 1 && Gateway!.ReceiveMessages,
            "Admin revocation discards queued chat before Tick while message intake remains available for the strict per-message gate");
        previous = Commands;
        staged = Stage(Config("publiconly", admins: "[]", commandChannels: "[]", chatChannels: "['456']"));
        previous.QueuedChat = 1;
        FinishRead(staged);
        Check(previous.Disposed && previous.QueuedChat == 0 && previous.ExecutedChat == 0 &&
            Commands!.Settings.CommandChannelIds.Count == 0 && Commands.Settings.ChatChannelIds.Contains("456") && Gateway!.ReceiveMessages,
            "Changing a channel from admin to public replaces authority and discards pending work even when the union count is unchanged");
        previous = Commands;
        Apply(Config("neither", admins: "[]", commandChannels: "[]", chatChannels: "[]"));
        status = DiscordRuntime.ExecuteAdminOperation("status");
        Check(previous.Disposed && Commands!.Settings.ChatRelayChannelCount == 0 && !Gateway!.ReceiveMessages &&
            status.Message.Contains("chat_enabled: false") && status.Message.Contains("chat_channel_count: 0"),
            "Only clearing both configured channel lists removes message intake from an enabled bot");
    }

    private static void CandidateFailure()
    {
        NewWorld(Config("original"));
        DiscordRuntime.Start();
        object original = Session!;
        DiscordCommands commands = Commands!;
        DiscordHttp.FailToken = Secret + "_FAIL";
        Apply(Config("failure1", token: DiscordHttp.FailToken));
        Check(ReferenceEquals(Session, original) && ReferenceEquals(Commands, commands) &&
            !commands.Disposed && RouteName == "original", "Candidate construction failure retains old running session");
        DiscordHttp.FailToken = null;
        DiscordCommands.FailNextInherit = true;
        Apply(Config("prepfail", token: Secret + "_D"));
        Check(ReferenceEquals(Session, original) && ReferenceEquals(Commands, commands) && !commands.Disposed,
            "Candidate preparation failure retains old running bot");
        Check(DiscordCommands.All.Last().Disposed && DiscordWebhooks.All.Last().Disposed &&
            DiscordWebhooks.All.Last().DrainCalls == 0,
            "Failed prepared candidate retires only its own components without draining notifications");
        Apply(Config("repaired", token: Secret + "_C"));
        Check(!ReferenceEquals(Session, original) && RouteName == "repaired",
            "Corrected replacement succeeds after constructor failure");
        original = Session!;
        DiscordWebhooks hooks = Hooks;
        DiscordWebhooks.FailNextReload = true;
        Apply(Config("hookfail", token: Secret + "_C"));
        Check(ReferenceEquals(Session, original) && ReferenceEquals(Hooks, hooks) && RouteName == "repaired",
            "Sender reload failure does not replace active settings");
        Apply(Config("hookgood", token: Secret + "_C"));
        Check(RouteName == "hookgood", "Sender reload recovers after corrected edit");
    }

    private static void StopAndWorldGuards()
    {
        NewWorld(Config("oldworld")); DiscordRuntime.Start();
        DiscordWebhooks oldHooks = Hooks;
        Task stale = Stage(Config("staleres"));
        // Preserve a real parsed result, but delay its delivery until after a
        // new world starts. This exercises late completion, not just disposal
        // of a task that had already finished when Stop was called.
        object result = stale.GetType().GetProperty("Result")!.GetValue(stale)!;
        Type sourceType = typeof(TaskCompletionSource<>).MakeGenericType(result.GetType());
        object source = Activator.CreateInstance(sourceType)!;
        Task delayed = (Task)sourceType.GetProperty("Task")!.GetValue(source)!;
        Field("_reloadRead").SetValue(null, delayed);
        DiscordRuntime.Stop();
        Check(Session == null && oldHooks.Disposed, "Stop synchronously removes old session");
        Write(Config("newworld"));
        ZNet.instance = new ZNet(); ZNet.World = new object();
        DiscordRuntime.Start();
        sourceType.GetMethod("SetResult")!.Invoke(source, new[] { result });
        FinishRead(delayed);
        Check(RouteName == "newworld" && !ReferenceEquals(Hooks, oldHooks),
            "Completed old-world read cannot apply after Stop/new Start");
        DiscordRuntime.Publish(new ServerManagerEvent());
        Check(Hooks.Queued == 1 && oldHooks.Queued == 0, "Events never reach stopped world's sender");

        oldHooks = Hooks;
        Stage(Config("wrongnet"));
        ZNet.instance = new ZNet();
        DiscordRuntime.Tick();
        Check(Session == null && oldHooks.Disposed && oldHooks.ReloadCalls == 0,
            "Changed network identity blocks completed reload");

        NewWorld(Config("worldobj")); DiscordRuntime.Start(); oldHooks = Hooks;
        Stage(Config("wrongobj"));
        ZNet.World = new object(); DiscordRuntime.Tick();
        Check(Session == null && oldHooks.Disposed && oldHooks.ReloadCalls == 0,
            "Changed world identity blocks completed reload");

        NewWorld(Config("serverok")); DiscordRuntime.Start(); oldHooks = Hooks;
        ZNet.instance!.Server = false; DiscordRuntime.Tick();
        Check(Session == null && oldHooks.Disposed, "Loss of server authority stops monitor and components");
    }

    private static void DelayedGatewayActivation()
    {
        NewWorld(Config("delayold")); DiscordRuntime.Start();
        Check(SpinWait.SpinUntil(() => Gateway!.RunCalls == 1, 1000),
            "Ordinary world startup runs Gateway without reload delay");
        Apply(Config("delayone", token: Secret + "_DELAY1"));
        DiscordGateway superseded = Gateway!;
        Task supersededWorker = GatewayWorker();
        Check(superseded.RunCalls == 0 && !supersededWorker.IsCompleted,
            "Reloaded Gateway does not run immediately after activation");
        Apply(Config("delaytwo", token: Secret + "_DELAY2"));
        DiscordGateway cancelled = Gateway!;
        Task cancelledWorker = GatewayWorker();
        Check(superseded.Disposed && cancelled.RunCalls == 0,
            "Rapid YAML replacement retires the first still-delayed Gateway");
        DiscordRuntime.Stop();
        Check(Task.WaitAll(new[] { supersededWorker, cancelledWorker }, 3000),
            "Superseded and stopped delayed workers finish promptly on cancellation");
        Check(superseded.RunCalls == 0 && cancelled.RunCalls == 0 && cancelled.Disposed,
            "Completed cancelled workers never enter Gateway.RunAsync");

        NewWorld(Config("delaynew")); DiscordRuntime.Start();
        Apply(Config("delayrun", token: Secret + "_DELAY3"));
        DiscordGateway delayed = Gateway!;
        Task delayedWorker = GatewayWorker();
        Check(delayed.RunCalls == 0, "Final replacement is also delayed");
        // Only this positive case waits out the real production six-second
        // delay. All other lifecycle tests drive read timing deterministically.
        Check(SpinWait.SpinUntil(() => delayed.RunCalls == 1, 7500),
            "Latest non-retired Gateway starts after the bounded reload delay");
        Check((delayed.RunStartedAt - delayed.CreatedAt) / (double)Stopwatch.Frequency >= 5.75,
            "Reload delay preserves the greater-than-five-second Identify spacing");
        Check(ReferenceEquals(Gateway, delayed) && !delayed.Disposed,
            "Only the latest active replacement reached Gateway.RunAsync");
        DiscordRuntime.Stop();
        Check(delayedWorker.Wait(3000) && delayed.TokenCancelled,
            "A started delayed Gateway still stops and cancels normally");
    }

    private static Task GatewayWorker() => (Task)(Session!.GetType().GetField("_gatewayWorker",
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Session)!);

    private static object? Session => Field("_session").GetValue(null);
    private static DiscordWebhooks Hooks => DiscordWebhooks.All.Last(value => !value.Disposed);
    private static DiscordCommands? Commands => DiscordCommands.All.LastOrDefault(value => !value.Disposed);
    private static DiscordGateway? Gateway => DiscordGateway.All.LastOrDefault(value => !value.Disposed);
    private static string RouteName => Hooks.Settings.WebhookRoutes.Single().Name;
    private static string ConfigPath => Path.Combine(ServerManagerPlugin.DataRoot, "discord.yml");

    private static void NewWorld(string? yaml, bool server = true)
    {
        DiscordRuntime.Stop();
        ServerManagerPlugin.DataRoot = Path.Combine(_root, (++_cases).ToString("D2"));
        ZNet.instance = new ZNet { Server = server };
        ZNet.World = new object();
        if (yaml != null) Write(yaml);
    }

    private static void Write(string yaml)
    {
        Directory.CreateDirectory(ServerManagerPlugin.DataRoot);
        File.WriteAllText(ConfigPath, yaml, Utf8);
    }

    private static void Apply(string yaml)
    {
        Task prepared = Stage(yaml);
        FinishRead(prepared);
    }

    // Complete two actual bounded file reads, with no multi-second wall waits.
    private static Task Stage(string yaml)
    {
        Write(yaml);
        Observe();
        return BeginRead();
    }

    private static void Observe() => FinishRead(BeginRead());

    private static Task BeginRead()
    {
        Field("_nextReloadPollTimestamp").SetValue(null, 0L);
        DiscordRuntime.Tick();
        Task read = Field("_reloadRead").GetValue(null) as Task ??
            throw new InvalidOperationException("Reload Tick did not schedule the expected bounded background read.");
        if (!read.Wait(3000)) throw new TimeoutException("Offline configuration read exceeded three seconds.");
        return read;
    }

    private static void FinishRead(Task read)
    {
        if (!read.IsCompleted) throw new InvalidOperationException("Test attempted to apply unfinished read.");
        Field("_nextReloadPollTimestamp").SetValue(null, long.MaxValue);
        DiscordRuntime.Tick();
    }

    private static FieldInfo Field(string name) => Runtime.GetField(name, StaticFlags) ??
        throw new MissingFieldException(Runtime.FullName, name);

    private static string Config(string route, bool bot = true, string? token = null,
        string admins = "['789']", bool shout = false,
        string commandChannels = "['456']", string chatChannels = "[]", string guilds = "['123']") =>
        "bot:\n  enabled: " + Flag(bot) + "\n  token: '" + (token ?? Secret + "_A") +
        "'\n  guild_ids: " + guilds + "\n  admin_channel_ids: " + commandChannels + "\n  admin_user_ids: " + admins + "\n" +
        "  chat_channel_ids: " + chatChannels + "\n" +
        "webhooks:\n  - name: '" + route + "'\n    enabled: true\n" +
        "    url: 'https://discord.com/api/webhooks/123/" + Secret + "_HOOK'\n" +
        "    events: " + (shout ? "['server.status','chat.shout']" : "['server.status']") + "\n    username: 'Offline smoke'\n";

    private static string Flag(bool value) => value ? "true" : "false";
    private static void Check(bool condition, string message)
    {
        ++_checks;
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}

internal sealed class ZNet
{
    internal static ZNet? instance;
    internal static object? World;
    internal bool Server = true;
    internal bool IsServer() => Server;
}

namespace ServerManager
{
    internal static class ServerManagerPlugin
    {
        internal static string DataRoot = string.Empty;
        internal static readonly FakeLog Log = new();
    }
    internal sealed class FakeLog
    {
        internal static readonly ConcurrentQueue<string> Messages = new();
        internal void LogInfo(object value) => Messages.Enqueue(value?.ToString() ?? string.Empty);
    }
    internal static class IntegrityCanonical
    {
        internal static bool IsFatal(Exception error) => error is OutOfMemoryException || error is StackOverflowException;
    }
}

namespace ServerManager.Events
{
    internal sealed class ServerManagerEvent
    {
        internal ServerManagerEvent() { }
        internal ServerManagerEvent(string id, DateTime occurred, string server, string kind, string reliability,
            ServerManagerActor actor, ServerManagerActor? target, IDictionary<string, string> fields)
        { Kind = kind; Fields = fields; }
        internal string Kind = "";
        internal IDictionary<string, string> Fields = new Dictionary<string, string>();
    }
    internal sealed class ServerManagerActor
    {
        internal ServerManagerActor(string id, string name, string kind) { }
    }
    internal static class ServerManagerEventReliability { internal const string Authoritative = "authoritative"; }
    internal sealed class ServerManagerCommandResult
    {
        internal ServerManagerCommandResult(bool success, string code, string message, string operation, IDictionary<string, string> data)
        { Success = success; Code = code; Message = message; Data = data; }
        internal bool Success { get; }
        internal string Code { get; }
        internal string Message { get; }
        internal IDictionary<string, string> Data { get; }
    }
    internal static class ServerEventRuntime
    {
        internal static void RecordDiscordCommand(ServerManagerEvent value) { }
    }
}

namespace Newtonsoft.Json.Linq { internal sealed class JObject { } }

namespace ServerManager.Discord
{
    internal sealed class DiscordHttp : IDisposable
    {
        internal static string? FailToken;
        internal DiscordHttp(string token, Action<string> log)
        {
            if (token == FailToken) throw new InvalidOperationException(DiscordReloadSmoke.Secret);
        }
        public void Dispose() { }
    }

    internal sealed class DiscordWebhooks : IDisposable
    {
        internal static readonly List<DiscordWebhooks> All = new();
        internal static int OffThreadReloads;
        internal static bool FailNextReload;
        internal DiscordSettings Settings;
        internal bool Disposed;
        internal bool RejectNext;
        internal ServerManagerEvent? LastEvent;
        internal int ReloadCalls, Queued, DrainCalls, InheritCalls;
        internal DiscordWebhooks? InheritedFrom;
        internal DiscordWebhooks(DiscordSettings settings, DiscordHttp http, Action<string> log)
        { Settings = settings; All.Add(this); }
        internal void InheritRecentOperatorState(DiscordWebhooks previous)
        { ++InheritCalls; InheritedFrom = previous; }
        public bool Reload(DiscordSettings settings)
        {
            if (Thread.CurrentThread.ManagedThreadId != DiscordReloadSmoke.MainThread) ++OffThreadReloads;
            if (Disposed) return false;
            if (FailNextReload) { FailNextReload = false; throw new InvalidOperationException(DiscordReloadSmoke.Secret); }
            Settings = settings;
            ++ReloadCalls;
            return true;
        }
        public bool Enqueue(ServerManagerEvent value)
        {
            if (Disposed) return false;
            if (RejectNext) { RejectNext = false; return false; }
            if (value.Kind.Length > 0)
            {
                string? filter = DiscordSettings.GetWebhookEventFilter(value.Kind);
                if (filter == null || !Settings.WebhookRoutes.Any(route => route.Events.Contains(filter))) return false;
            }
            ++Queued; LastEvent = value; return true;
        }
        public Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
        public Task StopAsync(TimeSpan timeout) { ++DrainCalls; Queued = 0; return Task.CompletedTask; }
        public void Dispose() { Disposed = true; Queued = 0; }
    }

    internal sealed class DiscordCommands : IDisposable
    {
        internal static readonly List<DiscordCommands> All = new();
        internal static int OffThreadTicks;
        internal static bool FailNextInherit;
        internal readonly DiscordSettings Settings;
        internal DiscordCommands? InheritedFrom;
        internal bool Disposed;
        internal int Queued, Executed;
        internal int QueuedChat, ExecutedChat, InheritCalls;
        internal bool LastInheritedDisposed;
        internal readonly HashSet<string> SeenChat = new(StringComparer.Ordinal);
        internal string? ChatAcceptedWhileRetiring;
        internal DiscordCommands(DiscordSettings settings, DiscordHttp http, Action<string> log,
            Action<ServerManagerEvent> audit, CancellationToken ct)
        { Settings = settings; All.Add(this); }
        public Task HandleDispatchAsync(string eventType, Newtonsoft.Json.Linq.JObject data) => Task.CompletedTask;
        internal void InheritRecentState(DiscordCommands previous)
        {
            if (FailNextInherit)
            {
                FailNextInherit = false;
                throw new InvalidOperationException(DiscordReloadSmoke.Secret);
            }
            InheritedFrom = previous;
            ++InheritCalls;
            LastInheritedDisposed = previous.Disposed;
            SeenChat.Clear();
            SeenChat.UnionWith(previous.SeenChat);
        }
        public void Tick()
        {
            if (Thread.CurrentThread.ManagedThreadId != DiscordReloadSmoke.MainThread) ++OffThreadTicks;
            if (Disposed) return;
            Executed += Queued;
            Queued = 0;
            ExecutedChat += QueuedChat;
            QueuedChat = 0;
        }
        public void Dispose()
        {
            // Model a message accepted after the preparatory history snapshot.
            // Runtime's second copy must see it only after admission has frozen.
            if (ChatAcceptedWhileRetiring != null) SeenChat.Add(ChatAcceptedWhileRetiring);
            Disposed = true; Queued = QueuedChat = 0;
        }
    }

    internal sealed class DiscordGateway : IDisposable
    {
        internal static readonly List<DiscordGateway> All = new();
        internal readonly string Token;
        internal readonly bool ReceiveMessages;
        internal readonly long CreatedAt = Stopwatch.GetTimestamp();
        internal bool Disposed;
        private CancellationToken _token;
        private int _runCalls;
        private long _runStartedAt;
        internal int RunCalls => Volatile.Read(ref _runCalls);
        internal long RunStartedAt => Interlocked.Read(ref _runStartedAt);
        internal bool TokenCancelled => _token.IsCancellationRequested;
        internal string ConnectionStatus => Disposed ? "stopped" : RunCalls > 0 ? "connected" : "pending";
        internal DiscordGateway(string token, Func<string, Newtonsoft.Json.Linq.JObject, Task> dispatch,
            Action<string> log, DiscordHttp http, bool receiveMessages = false)
        { Token = token; ReceiveMessages = receiveMessages; All.Add(this); }
        public Task RunAsync(CancellationToken ct)
        {
            _token = ct;
            Interlocked.Exchange(ref _runStartedAt, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _runCalls);
            return Task.Delay(Timeout.Infinite, ct);
        }
        public void Dispose() { Disposed = true; }
    }
}
