// This assembly is compiled against net48 and executed by Valheim's actual Mono.
// It never initializes the plugin, opens game data, or makes an external request.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServerManager.Tests
{
    public static class DiscordMonoProbe
    {
        private static int _checks;
        public static int Run()
        {
            try
            {
                RunAsync().GetAwaiter().GetResult();
                Console.WriteLine("PASS: actual Valheim Mono Discord/common/managed command/cron probe (" + _checks + " checks, loopback only).");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: Mono probe: " + error);
                return 1;
            }
        }

        private static async Task RunAsync()
        {
            Check(Type.GetType("Mono.Runtime") != null, "Managed code is executing under Mono");
            Console.WriteLine("Mono corlib: " + typeof(object).Assembly.Location);
            Console.WriteLine("Mono HTTP: " + typeof(HttpClient).Assembly.Location);
            string managedRoot = Environment.GetEnvironmentVariable("SERVERMANAGER_MONO_PROBE_MANAGED");
            Check(string.Equals(Path.GetFullPath(typeof(object).Assembly.Location), Path.Combine(managedRoot, "mscorlib.dll"), StringComparison.OrdinalIgnoreCase), "Corlib comes from the actual Valheim installation");
            Check(string.Equals(Path.GetFullPath(typeof(HttpClient).Assembly.Location), Path.Combine(managedRoot, "System.Net.Http.dll"), StringComparison.OrdinalIgnoreCase), "HTTP assembly comes from the actual Valheim installation");
            Assembly plugin = Assembly.LoadFrom(Environment.GetEnvironmentVariable("SERVERMANAGER_MONO_PROBE_DLL"));
            Check(!plugin.GetReferencedAssemblies().Any(a => a.Name == "Newtonsoft.Json"), "No external Newtonsoft assembly reference");
            Check(!plugin.GetReferencedAssemblies().Any(a => a.Name == "YamlDotNet"), "No external YAML assembly reference");
            Type json = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            object parsed = json.GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { "{\"probe\":\"한글\"}" });
            Check(parsed.GetType().Assembly == plugin && !json.IsPublic, "Bundled internal Newtonsoft executes");
            TestCommandAuthorizationUnderGameMono(plugin, managedRoot);
            TestBundledYamlSettings(plugin);
            TestBundledServerYamlSettings(plugin);
            TestBundledSchedule(plugin);
            await TestBundledDisabledChatAsync(plugin).ConfigureAwait(false);
            await TestBundledAdminChatAdmissionAsync(plugin).ConfigureAwait(false);
            await TestBundledMultiGuildAsync(plugin).ConfigureAwait(false);
            Type gateway = plugin.GetType("ServerManager.Discord.DiscordGateway", true);
            var url = (Uri)gateway.GetMethod("ValidateGatewayUrl", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { "wss://gateway-us-east1-b.discord.gg/" });
            Check(url.Scheme == "wss" && url.Query == "?v=10&encoding=json", "Bundled Gateway helper executes");

            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            using (var handler = new OfflineHandler())
            using (var http = (IDisposable)Activator.CreateInstance(transport, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { "offline-test-token", new Action<string>(_ => { }), handler }, null))
            using (var stop = new CancellationTokenSource(5000))
            {
                Task request = (Task)transport.GetMethod("SendAsync").Invoke(http, new object[]
                { HttpMethod.Get, "https://discord.com/api/v10/gateway/bot", null, true, stop.Token, false });
                await request.ConfigureAwait(false);
                object response = request.GetType().GetProperty("Result").GetValue(request, null);
                Check(handler.Calls == 1 && response.GetType().Assembly == plugin, "Bundled HTTP transport executes with an in-memory handler");
            }
            await TestBundledWebhookReloadAsync(plugin).ConfigureAwait(false);
            await TestBundledPlainShoutsAsync(plugin).ConfigureAwait(false);
            await TestBundledCompactCardsAsync(plugin).ConfigureAwait(false);
            await TestBundledConnectionSteamIdsAsync(plugin).ConfigureAwait(false);
            await TestBundledTranslationDiscoveryAsync(plugin).ConfigureAwait(false);
            await TestBundledAnonymousWebhooksAsync(plugin).ConfigureAwait(false);
            await TestBundledAnonymousStoryTargetsAsync(plugin).ConfigureAwait(false);
            await TestBundledOperatorSummariesAsync(plugin).ConfigureAwait(false);
            await TestBundledRaidCoordinatesAsync(plugin).ConfigureAwait(false);
            await TestHttpLoopbackAsync().ConfigureAwait(false);
            await TestWebSocketLoopbackAsync(gateway).ConfigureAwait(false);
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Newtonsoft.Json"), "No separate Newtonsoft assembly was loaded");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "YamlDotNet"), "No separate YAML assembly was loaded");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Cronos"), "No separate Cronos assembly was loaded");
        }

        private static void TestBundledSchedule(Assembly plugin)
        {
            Check(!plugin.GetReferencedAssemblies().Any(a => a.Name == "Cronos"), "No external Cronos assembly reference");
            Type cron = plugin.GetType("Cronos.CronExpression", true);
            Check(cron.Assembly == plugin && !cron.IsPublic, "Cronos is bundled and internalized");
            Type settingsType = plugin.GetType("ServerManager.ServerScheduleSettings", true);
            MethodInfo parse = settingsType.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            const string yaml = "timezone: UTC\njobs:\n" +
                "  - schedule: '*/10 * * * * *'\n    command: 'announce test'\n" +
                "  - schedule: '*/10 * * * * *'\n    commands: ['save']\n    useGameTime: true\n";
            object settings = parse.Invoke(null, new object[] { yaml, null });
            Check(((IEnumerable)settingsType.GetProperty("Jobs").GetValue(settings, null)).Cast<object>().Count() == 2,
                "Actual Mono parses cron YAML through bundled YAML/Cronos");
            bool rejected = false;
            try { parse.Invoke(null, new object[] { yaml + "unknown: 5\n", null }); }
            catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
            Check(rejected, "Actual Mono cron parser rejects unknown fields atomically");
            foreach (string field in new[] { "catchUp", "maintenance", "enabled", "id", "cron" })
            foreach (string value in new[] { "true", "false" })
            {
                rejected = false;
                try { parse.Invoke(null, new object[] { yaml + "    " + field + ": " + value + "\n", null }); }
                catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
                Check(rejected, "Actual Mono rejects removed cron policy field " + field + "=" + value);
            }
            foreach (string command in new[] { "objects_remove start", "world_clean", "time_set 1000", "upgrade tarpits start", "objects_count", "announce test" })
            {
                object classified = parse.Invoke(null, new object[] { "jobs:\n  - schedule: '* * * * *'\n    command: '" + command + "'\n", null });
                object job = ((IEnumerable)settingsType.GetProperty("Jobs").GetValue(classified, null)).Cast<object>().Single();
                bool mutation = command != "objects_count" && command != "announce test";
                Check((bool)job.GetType().GetProperty("Maintenance").GetValue(job, null) == mutation &&
                    (bool)job.GetType().GetProperty("CatchUp").GetValue(job, null) == mutation,
                    "Actual Mono derives maintenance/catch-up from reviewed command behavior: " + command);
            }
            foreach (string command in new[] { "start", "stop", "save_disable", "save_enable", "verbose", "uw_check", "upgrade onions", "sm:world_clean" })
            {
                rejected = false;
                try { parse.Invoke(null, new object[] { "jobs:\n  - schedule: '* * * * *'\n    command: '" + command + "'\n", null }); }
                catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
                Check(rejected, "Actual Mono rejects unsupported UW control or variant: " + command);
            }

            Type engineType = plugin.GetType("ServerManager.ServerScheduleEngine", true);
            object engine = Activator.CreateInstance(engineType, true);
            DateTime now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
            DateTime game = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            MethodInfo apply = engineType.GetMethod("Apply");
            MethodInfo claim = engineType.GetMethod("ClaimDue");
            MethodInfo complete = engineType.GetMethod("Complete");
            apply.Invoke(engine, new[] { settings, (object)now, game, null });
            Func<DateTime, DateTime, object[]> due = (utc, gameUtc) =>
                ((IEnumerable)claim.Invoke(engine, new object[] { utc, gameUtc })).Cast<object>().ToArray();
            Check(due(now, game).Length == 0, "Actual Mono cron starts after the current boundary without offline replay");
            object occurrence = due(now.AddSeconds(10), game).Single();
            Check((DateTime)occurrence.GetType().GetProperty("OccurrenceUtc").GetValue(occurrence, null) == now.AddSeconds(10),
                "Bundled Cronos calculates real UTC occurrences under game Mono");
            Check(due(now.AddSeconds(20), game).Length == 0, "Actual Mono cron claims suppress overlapping occurrences");
            complete.Invoke(engine, new[] { occurrence });
            Check(due(now.AddSeconds(20), game).Length == 0, "Actual Mono cron completion does not replay skipped occurrences");
            object gameOccurrence = due(now.AddSeconds(20), game.AddSeconds(10)).Single();
            Check((DateTime)gameOccurrence.GetType().GetProperty("OccurrenceUtc").GetValue(gameOccurrence, null) == game.AddSeconds(10),
                "Bundled cron handles the independent virtual game UTC clock");
            complete.Invoke(engine, new[] { gameOccurrence });
            engineType.GetMethod("Reset").Invoke(engine, new object[0]);
        }

        private static void TestBundledServerYamlSettings(Assembly plugin)
        {
            Type settings = plugin.GetType("ServerManager.ServerSettings", true);
            MethodInfo parse = settings.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            string yaml = "serverSettings:\n  maxCharactersPerAccount: 5\n  backupsPerProfile: 30\n" +
                "forbiddenItems: [SwordCheat]\ncheatDetection:\n  action: kick\n" +
                "statCaps:\n  action: log\n  health: 800\n  stamina: 800\n  eitr: 500\n  weight: 2000\n  damage: 9999\n" +
                "startItems:\n  - Wood\n  - Stone, 5\n";
            object value = parse.Invoke(null, new object[] { yaml });
            Func<string, object> get = name => settings.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(value, null);
            Check((int)get("MaxCharactersPerAccount") == 5 && (int)get("BackupsPerProfile") == 30,
                "Actual Mono server YAML parses profile and backup settings");
            Check(get("CheatDetectionResponse").ToString() == "Kick" && get("StatLimitResponse").ToString() == "Log",
                "Actual Mono server YAML preserves separate common cheat/stat responses");
            Check((float)get("MaximumHealth") == 800f && (float)get("MaximumEitr") == 500f && (float)get("MaximumDamage") == 9999f,
                "Actual Mono server YAML parses independent numeric caps");
            var items = (IReadOnlyDictionary<string, int>)get("StartItems");
            Check(items.Count == 2 && items["Wood"] == 1 && items["Stone"] == 5,
                "Actual Mono server YAML parses the startItems list with omitted quantity defaulting to one");
            foreach (string invalid in new[] { yaml + "spawn: []\n", yaml + "startItems: {}\n",
                yaml.Replace("health: 800", "health: .nan"), yaml.Replace("action: kick", "action: off"),
                yaml.Replace("- Wood", "- Wood, -1"), yaml.Replace("- Wood", "- Wood,"),
                yaml.Replace("- Stone, 5", "- Wood, 5"), "startItems: {}", "startItems: {Wood: 5}" })
            {
                bool rejected = false;
                try { parse.Invoke(null, new object[] { invalid }); }
                catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
                Check(rejected, "Actual Mono server YAML rejects invalid policy/template without changing existing state");
            }
            object empty = parse.Invoke(null, new object[] { "startItems: []" });
            Check(((IReadOnlyDictionary<string, int>)settings.GetProperty("StartItems").GetValue(empty, null)).Count == 0,
                "Actual Mono accepts an explicit empty starter inventory");
        }

        private static void TestCommandAuthorizationUnderGameMono(Assembly plugin, string managedRoot)
        {
            // Load real installed dependencies for the actual command methods, not
            // test doubles. We still do not instantiate a Unity component or call
            // plugin Initialize/Start, and denial precedes native/game operations.
            string gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(managedRoot));
            string core = Path.Combine(gameRoot, "BepInEx", "core");
            foreach (string name in new[] { "BepInEx.dll", "0Harmony.dll" })
            {
                string path = Path.Combine(core, name);
                Check(File.Exists(path), "Installed command dependency is present: " + name);
                Assembly.LoadFrom(path);
            }
            // Keep actual localization resource/fallback reads inside this
            // disposable probe directory; never inspect a live BepInEx config.
            Type paths = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "BepInEx")
                .GetType("BepInEx.Paths", true);
            string isolatedBepInEx = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "isolated-bepinex");
            paths.GetProperty("BepInExRootPath", BindingFlags.Static | BindingFlags.Public).GetSetMethod(true)
                .Invoke(null, new object[] { isolatedBepInEx });
            paths.GetProperty("ConfigPath", BindingFlags.Static | BindingFlags.Public).GetSetMethod(true)
                .Invoke(null, new object[] { Path.Combine(isolatedBepInEx, "config") });
            const BindingFlags methods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type commands = plugin.GetType("ServerManager.Commands.ServerCommands", true);
            Type callerType = plugin.GetType("ServerManager.Commands.CommandCaller", true);
            Type runtime = plugin.GetType("ServerManager.ServerManagerRuntime", true);
            int authorizationChecks = 0;
            Func<bool> deny = () => { ++authorizationChecks; return false; };
            object caller = Activator.CreateInstance(callerType, instance, null,
                new object[] { "mono-probe", "original-actor", "Offline test operator", deny, CancellationToken.None }, null);
            MethodInfo execute = commands.GetMethod("ExecuteAsync", methods);
            MethodInfo tick = commands.GetMethod("Tick", methods);
            MethodInfo initialize = commands.GetMethod("Initialize", methods);
            MethodInfo shutdown = commands.GetMethod("Shutdown", methods);
            string[] flatNames = ((IEnumerable)commands.GetProperty("FlatCommandNames", methods).GetValue(null, null)).Cast<string>().ToArray();
            Type discordCommands = plugin.GetType("ServerManager.Discord.DiscordCommands", true);
            string[] slashNames = ((IEnumerable)discordCommands.GetField("Names", methods).GetValue(null)).Cast<string>().ToArray();
            Check(flatNames.Length == 33 && slashNames.Length == 34 && flatNames.Contains("banlist") &&
                flatNames.Contains("cronstatus") && flatNames.Contains("cronack") && slashNames.All(name => !name.Contains("-")) &&
                flatNames.OrderBy(value => value).SequenceEqual(slashNames.Where(value => value != "rcon").OrderBy(value => value)),
                "Actual Mono shares exactly one flat feature catalog between F5 and Discord, with Discord-only RCON");
            initialize.Invoke(null, new object[0]); // Initializes only the bounded common queue.
            try
            {
                foreach (string obsolete in new[] { "sm status", "player info halla", "character info halla", "item give halla Wood 1",
                    "teleport halla --to frodo", "sm:rcon save", "sm:save", "rcon save", "sm:player-info halla", "skill-set halla Run 10",
                    "sm:playerinfo halla", "sm:skilladd halla Run 5", "sm:skillreset halla Run" })
                {
                    int before = authorizationChecks;
                    Task rejected = (Task)execute.Invoke(null, new[] { (object)obsolete, caller });
                    Check(rejected.IsCompleted, "Removed public syntax is rejected without queueing: " + obsolete);
                    object rejectedResult = rejected.GetType().GetProperty("Result").GetValue(rejected, null);
                    Check(authorizationChecks == before && !(bool)rejectedResult.GetType().GetProperty("Success").GetValue(rejectedResult, null),
                        "Actual Mono rejects removed public syntax or raw-console namespace escape before game access: " + obsolete);
                }
                foreach (string line in new[] { "help", "sm:status", "players", "sm:players \"76561198000000001/Test Viking\"", "giveitem \"76561198000000001/Test Viking\" Wood 1",
                    "skillset \"76561198000000001/Test Viking\" Run 10" })
                {
                    int before = authorizationChecks;
                    Task queued = (Task)execute.Invoke(null, new[] { (object)line, caller });
                    Check(!queued.IsCompleted && authorizationChecks == before,
                        "Actual Mono common executor queues before checking the caller: " + line.Split(' ')[0]);
                    tick.Invoke(null, new object[0]);
                    queued.GetAwaiter().GetResult();
                    object result = queued.GetType().GetProperty("Result").GetValue(queued, null);
                    Check(!(bool)result.GetType().GetProperty("Success").GetValue(result, null) &&
                        (string)result.GetType().GetProperty("Code").GetValue(result, null) == "unauthorized" &&
                        authorizationChecks == before + 1,
                        "Actual Mono common Tick denies unauthorized work before Unity access: " + line.Split(' ')[0]);
                }
                MethodInfo managed = runtime.GetMethod("ExecuteManagedAdminCommandAsync", methods);
                int previousChecks = authorizationChecks;
                // This engine-less host embeds the real Mono runtime but does not
                // register Unity native internal calls. JIT may therefore print a
                // Time.frameCount resolution warning for a later, untaken branch.
                // Do not suppress it; the required unauthorized result below proves
                // this probe returned before invoking any native game operation.
                Console.WriteLine("NOTE: Engine-less Mono may warn about Unity native calls while JITing the managed entry; this probe requires denial before invoking them.");
                Task denied = (Task)managed.Invoke(null, new object[]
                {
                    new[] { "skill", "set", "76561198000000001/Test Viking", "Run", "10" }, caller
                });
                Check(denied.IsCompleted, "Actual Mono can JIT the real managed command entry without a Unity scene");
                object managedResult = denied.GetType().GetProperty("Result").GetValue(denied, null);
                Check(!(bool)managedResult.GetType().GetProperty("Success").GetValue(managedResult, null) &&
                    (string)managedResult.GetType().GetProperty("Code").GetValue(managedResult, null) == "unauthorized" &&
                    authorizationChecks == previousChecks + 1,
                    "Actual Mono managed offline skill entry rechecks the original caller before mutation");
            }
            finally { shutdown.Invoke(null, new object[0]); }
        }

        private static void TestBundledYamlSettings(Assembly plugin)
        {
            Type parser = plugin.GetType("YamlDotNet.Core.Parser", true);
            Check(parser.Assembly == plugin && !parser.IsPublic, "Bundled YAML parser is internal to ServerManager.dll");
            Type settings = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            MethodInfo load = settings.GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic);
            // This code runs only in the disposable child process. Never let a real
            // inherited bot credential participate in the offline settings probe.
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_BOT_TOKEN", null);
            string testRoot = Path.Combine(Path.GetDirectoryName(plugin.Location), "yaml-settings-probe");
            Directory.CreateDirectory(testRoot);
            string path = Path.Combine(testRoot, "discord.yml");
            Action<string> quiet = _ => { };
            object defaults = load.Invoke(null, new object[] { testRoot, quiet });
            Check(File.Exists(path) && !File.Exists(Path.Combine(testRoot, "discord.cfg")), "Missing settings create only discord.yml in the temporary probe root");
            Check(!(bool)settings.GetProperty("BotEnabled").GetValue(defaults, null), "Generated YAML defaults keep the embedded bot disabled");
            Check(ChatChannels(defaults).Length == 0 && ChatRelayChannelCount(defaults) == 0,
                "Generated YAML defaults leave both sources of ordinary Discord chat disabled under Mono");
            string generated = File.ReadAllText(path);
            Check(!generated.Contains("privacy:") && !generated.Contains("relay_shouts") &&
                !generated.Contains("include_coordinates") && !generated.Contains("include_inventory") &&
                !generated.Contains("include_plugin_list"), "Generated Mono YAML has no removed privacy switches");
            Check(!generated.Contains("rcon:") && !generated.Contains("minimum_interval_seconds") &&
                !generated.Contains("maximum_output_characters") && !generated.Contains("command_timeout_seconds"),
                "Generated Mono YAML has no removed RCON or command execution-limit settings");
            Check(generated.Contains("guild_ids:") && generated.Contains("admin_channel_ids:") && generated.Contains("chat_channel_ids:") &&
                !generated.Contains("guild_id:") && !generated.Contains("command_channel_ids:") && !generated.Contains("\nchat:"),
                "Generated Mono YAML groups both channel lists and multiple guild IDs under bot");
            Check(!generated.Contains("url_env") && generated.Contains("    url:"),
                "Generated Mono webhook routes support only direct url fields, with no environment indirection");
            object roundtrip = load.Invoke(null, new object[] { testRoot, quiet });
            Check(!(bool)settings.GetProperty("BotEnabled").GetValue(roundtrip, null)
                && File.ReadAllText(path) == generated, "Generated YAML reloads unchanged under the actual game Mono");

            string configured = "webhooks:\n  - name: mono-test\n    enabled: true\n"
                + "    url: https://discord.com/api/webhooks/123/offline-token\n"
                + "    username: 한글 서버\n    events: [server.status]\n";
            File.WriteAllText(path, configured, new UTF8Encoding(false));
            object loaded = load.Invoke(null, new object[] { testRoot, quiet });
            IList routes = (IList)settings.GetProperty("WebhookRoutes").GetValue(loaded, null);
            Check(routes.Count == 1, "A real YAML webhook sequence loads through the bundled settings parser");
            object route = routes[0];
            Type routeType = route.GetType();
            Check((string)routeType.GetProperty("Name").GetValue(route, null) == "mono-test"
                && (string)routeType.GetProperty("Username").GetValue(route, null) == "한글 서버"
                && (string)routeType.GetProperty("Url").GetValue(route, null) == "https://discord.com/api/webhooks/123/offline-token"
                && ((IEnumerable)routeType.GetProperty("Events").GetValue(route, null)).Cast<string>().Single() == "server.status",
                "Direct webhook URL, scalar UTF-8 and exact event-list values survive parsing in Mono");
            Check(File.ReadAllText(path) == configured, "Loading existing YAML does not rewrite the operator's document");

            string chatOnly = "bot:\n  enabled: true\n  token: offline-chat-token\n  guild_ids: ['123', '321']\n"
                + "  admin_channel_ids: []\n  chat_channel_ids: ['456', '654']\n";
            File.WriteAllText(path, chatOnly, new UTF8Encoding(false));
            object chat = load.Invoke(null, new object[] { testRoot, quiet });
            Check((bool)settings.GetProperty("BotEnabled").GetValue(chat, null)
                && !((IEnumerable)settings.GetProperty("CommandChannelIds").GetValue(chat, null)).Cast<string>().Any()
                && ChatChannels(chat).OrderBy(value => value).SequenceEqual(new[] { "456", "654" })
                && ((HashSet<string>)settings.GetProperty("GuildIds").GetValue(chat, null)).SetEquals(new[] { "123", "321" }),
                "Bundled Mono settings enable chat-only bot in multiple guilds independently of admin channels");
            MethodInfo strictParse = settings.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic);
            PropertyInfo includeSteamId = routeType.GetProperty("IncludeSteamId");
            Check(includeSteamId != null && includeSteamId.PropertyType == typeof(bool) && includeSteamId.CanRead && includeSteamId.CanWrite &&
                !(bool)includeSteamId.GetValue(route, null) && !(bool)includeSteamId.GetValue(Activator.CreateInstance(routeType, true), null),
                "Actual Mono route DTO exposes bool IncludeSteamId and both DTO/parser omission default to false");
            foreach (bool optIn in new[] { false, true })
            {
                object candidate = strictParse.Invoke(null, new object[] { configured + "    include_steam_id: " + (optIn ? "true" : "false") + "\n" });
                object candidateRoute = ((IList)settings.GetProperty("WebhookRoutes").GetValue(candidate, null))[0];
                Check((bool)includeSteamId.GetValue(candidateRoute, null) == optIn,
                    "Actual Mono strictly parses an explicit per-route Steam ID switch: " + optIn);
            }
            foreach (string invalidFlag in new[] { "True", "yes", "1", "null", "''", "'true'", "\"false\"", "[]", "{}" })
            foreach (bool enabled in new[] { true, false })
            {
                bool rejected = false;
                try { strictParse.Invoke(null, new object[] { configured.Replace("enabled: true", "enabled: " + (enabled ? "true" : "false")) +
                    "    include_steam_id: " + invalidFlag + "\n" }); }
                catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
                Check(rejected, "Actual Mono rejects malformed include_steam_id even on disabled routes: " + invalidFlag);
            }
            object configuredDefaults = strictParse.Invoke(null, new object[] { generated
                .Replace("\r\n", "\n")
                .Replace("  enabled: true\n  token:", "  enabled: false\n  token:")
                .Replace("    enabled: false\n", "    enabled: true\n")
                .Replace("url: ''", "url: https://discord.com/api/webhooks/123/generated-offline-token") });
            IList defaultRoutes = (IList)settings.GetProperty("WebhookRoutes").GetValue(configuredDefaults, null);
            string[][] expectedDefaultFilters = {
                new[] { "server.status", "server.saved", "chat.shout", "player.connection", "raid.status", "player.death", "boss.killed" },
                new[] { "server.announcement", "moderation.action", "command.executed", "cron.executed", "connection.rejected", "character.revision_observed",
                    "character.validation", "character.shadow_stalled", "security.admin_bypass", "security.alert" },
                new[] { "server.status", "server.saved", "chat.shout" }, new[] { "server.status", "server.saved", "chat.shout" }
            };
            Check(defaultRoutes.Count == 4, "Actual Mono generated YAML retains all four route examples");
            for (int index = 0; index < defaultRoutes.Count; ++index)
                Check(((HashSet<string>)routeType.GetProperty("Events").GetValue(defaultRoutes[index], null)).SetEquals(expectedDefaultFilters[index]) &&
                    !(bool)includeSteamId.GetValue(defaultRoutes[index], null),
                    "Actual Mono default route selectors and private-ID opt-out remain exact: " + index);
            const string webhookEnvironment = "SERVERMANAGER_MONO_REMOVED_WEBHOOK_SECRET";
            Environment.SetEnvironmentVariable(webhookEnvironment, "https://discord.com/api/webhooks/987/ignored-environment-token");
            Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_BOT_TOKEN", "offline-env-bot-token");
            try
            {
                object direct = strictParse.Invoke(null, new object[] { chatOnly + configured });
                object directRoute = ((IList)settings.GetProperty("WebhookRoutes").GetValue(direct, null))[0];
                Check((string)routeType.GetProperty("Url").GetValue(directRoute, null) == "https://discord.com/api/webhooks/123/offline-token",
                    "Ambient webhook environment values cannot override a direct URL under Mono");
                Check((string)settings.GetProperty("BotToken").GetValue(direct, null) == "offline-env-bot-token",
                    "The supported bot token environment override is preserved under Mono");
                object envLoaded = load.Invoke(null, new object[] { testRoot, quiet });
                Check((string)settings.GetProperty("BotToken").GetValue(envLoaded, null) == "offline-env-bot-token" &&
                    File.ReadAllText(path) == chatOnly && !File.ReadAllText(path).Contains("offline-env-bot-token"),
                    "Loading an environment-backed bot token never persists it into YAML");
                foreach (string removedYaml in new[] {
                    configured.Replace("    username:", "    url_env: ''\n    username:"),
                    configured.Replace("    username:", "    url_env: " + webhookEnvironment + "\n    username:"),
                    configured.Replace("enabled: true", "enabled: false").Replace("    username:", "    url_env: " + webhookEnvironment + "\n    username:") })
                {
                    Exception removed = null;
                    try { strictParse.Invoke(null, new object[] { removedYaml }); }
                    catch (TargetInvocationException error) { removed = error.InnerException; }
                    Check(removed is InvalidDataException && !removed.ToString().Contains(webhookEnvironment) &&
                        !removed.ToString().Contains("offline-token") && !removed.ToString().Contains("ignored-environment-token"),
                        "Removed url_env is rejected even when empty, resolvable or disabled, without exposing secrets");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(webhookEnvironment, null);
                Environment.SetEnvironmentVariable("SERVERMANAGER_DISCORD_BOT_TOKEN", null);
            }
            object adminOnly = strictParse.Invoke(null, new object[] { chatOnly.Replace("admin_channel_ids: []", "admin_channel_ids: ['456']")
                .Replace("chat_channel_ids: ['456', '654']", "chat_channel_ids: []") });
            Check(ChatChannels(adminOnly).Length == 0 && ChatRelayChannelCount(adminOnly) == 1,
                "Actual Mono includes admin-only channels in effective message intake even before any administrator is configured");
            object overlap = strictParse.Invoke(null, new object[] { chatOnly.Replace("admin_channel_ids: []", "admin_channel_ids: ['456', '789']") });
            Check(ChatRelayChannelCount(overlap) == 3, "Actual Mono counts the union of admin/public channels without duplicate overlap");
            foreach (string removed in new[] { "AdminRoleIds", "AllowedCommands", "Grants", "EnableRcon",
                "RelayShouts", "IncludeCoordinates", "IncludeInventory", "IncludePluginList",
                "RconMinimumIntervalSeconds", "MaximumRconOutputCharacters", "CommandTimeoutSeconds", "GuildId" })
                Check(settings.GetProperty(removed) == null, "Removed Discord setting is absent under Mono: " + removed);
            Type commands = plugin.GetType("ServerManager.Discord.DiscordCommands", true);
            foreach (var limit in new Dictionary<string, int> { ["RconMinimumIntervalSeconds"] = 1,
                ["MaximumRconOutputCharacters"] = 1800, ["CommandTimeoutSeconds"] = 15 })
            {
                FieldInfo field = commands.GetField(limit.Key, BindingFlags.Static | BindingFlags.NonPublic);
                Check(field != null && field.IsLiteral && (int)field.GetRawConstantValue() == limit.Value,
                    "Actual Mono adapter has the immutable execution limit " + limit.Key);
            }
            foreach (string rejectedYaml in new[] { "bot:\n  admin_role_ids: []\n", "bot:\n  allowed_commands: []\n",
                "grants: {}\n", "rcon:\n  enabled: true\n", "privacy: {}\n", "privacy: {relay_shouts: true}\n",
                "privacy: {include_coordinates: false}\n", "privacy: {include_inventory: false}\n", "privacy: {include_plugin_list: true}\n",
                "rcon: {}\n", "rcon:\n  minimum_interval_seconds: 1\n", "rcon:\n  maximum_output_characters: 1800\n",
                "bot:\n  command_timeout_seconds: 15\n", "bot:\n  guild_id: '123'\n", "bot:\n  command_channel_ids: []\n",
                "chat:\n  channel_ids: []\n" })
            {
                Exception removedRejection = null;
                try { strictParse.Invoke(null, new object[] { rejectedYaml }); }
                catch (TargetInvocationException error) { removedRejection = error.InnerException; }
                Check(removedRejection is InvalidDataException, "Removed Discord authority/privacy/execution-limit setting fails closed under Mono");
            }
            object chatReplacement = strictParse.Invoke(null, new object[] { chatOnly.Replace("['456', '654']", "['987']") });
            Check(!(bool)settings.GetMethod("HasSameBotSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(chat, new[] { chatReplacement }), "Chat-channel changes participate in strict live bot replacement under Mono");
            object guildReplacement = strictParse.Invoke(null, new object[] { chatOnly.Replace("['123', '321']", "['123']") });
            Check(!(bool)settings.GetMethod("HasSameBotSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(chat, new[] { guildReplacement }), "Removing a guild participates in strict bot generation replacement under Mono");
            object guildReordered = strictParse.Invoke(null, new object[] { chatOnly.Replace("['123', '321']", "['321', '123']") });
            Check((bool)settings.GetMethod("HasSameBotSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(chat, new[] { guildReordered }), "Guild configuration comparison is set-based under Mono");
            object idleBot = strictParse.Invoke(null, new object[] { chatOnly.Replace("['456', '654']", "[]") });
            Check((bool)settings.GetProperty("BotEnabled").GetValue(idleBot, null) && ChatChannels(idleBot).Length == 0 && ChatRelayChannelCount(idleBot) == 0 &&
                !((IEnumerable)settings.GetProperty("CommandChannelIds").GetValue(idleBot, null)).Cast<string>().Any(),
                "Mono accepts clearing a chat-only bot's channels as safe idle configuration");
            Exception chatRejection = null;
            try { strictParse.Invoke(null, new object[] { chatOnly.Replace("['456', '654']", "['offline-invalid-chat-secret']") }); }
            catch (TargetInvocationException error) { chatRejection = error.InnerException; }
            Check(chatRejection is InvalidDataException && !chatRejection.ToString().Contains("offline-invalid-chat-secret"),
                "Bundled Mono rejects invalid chat-channel IDs without echoing source values");

            string invalid = "webhooks: [\n  secret: offline-invalid-yaml-marker\n";
            File.WriteAllText(path, invalid, new UTF8Encoding(false));
            Exception rejection = null;
            try { load.Invoke(null, new object[] { testRoot, quiet }); }
            catch (TargetInvocationException error) { rejection = error.InnerException; }
            Check(rejection is InvalidDataException && !rejection.ToString().Contains("offline-invalid-yaml-marker"),
                "Malformed YAML fails safely without exposing its source text");
            Check(File.ReadAllText(path) == invalid, "Rejected YAML remains untouched for operator repair");
        }

        private static string[] ChatChannels(object settings) =>
            ((IEnumerable)settings.GetType().GetProperty("ChatChannelIds").GetValue(settings, null)).Cast<string>().ToArray();
        private static int ChatRelayChannelCount(object settings) =>
            (int)settings.GetType().GetProperty("ChatRelayChannelCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(settings, null);

        private static async Task TestBundledDisabledChatAsync(Assembly plugin)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type commandsType = plugin.GetType("ServerManager.Discord.DiscordCommands", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type json = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            MethodInfo parse = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo dispatch = commandsType.GetMethod("HandleDispatchAsync");
            MethodInfo parseJson = json.GetMethod("Parse", new[] { typeof(string) });
            Delegate audit = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(eventType),
                typeof(DiscordMonoProbe).GetMethod("RejectChatAudit", BindingFlags.Static | BindingFlags.NonPublic));
            const string bot = "bot:\n  enabled: true\n  token: offline-chat-token\n  guild_ids: ['123', '321']\n  admin_channel_ids: ['456']\n";
            string enabledChat = bot + "  chat_channel_ids: ['456']\n  admin_user_ids: ['789']\n";
            string publicOnly = bot.Replace("admin_channel_ids: ['456']", "admin_channel_ids: []") + "  chat_channel_ids: ['456']\n";
            string[] settingsCases = { "{}\n", bot, enabledChat, enabledChat,
                bot + "  admin_user_ids: ['789']\n", publicOnly,
                bot.Replace("admin_channel_ids: ['456']", "admin_channel_ids: []"), bot + "  chat_channel_ids: ['456']\n" };
            for (int index = 0; index < settingsCases.Length; ++index)
            {
                object settings = parse.Invoke(null, new object[] { settingsCases[index] });
                using (var handler = new OfflineHandler())
                using (var http = (IDisposable)Activator.CreateInstance(transport, instance, null,
                    new object[] { "offline-chat-token", new Action<string>(_ => { }), handler }, null))
                using (var stop = new CancellationTokenSource())
                {
                    if (index == 2) stop.Cancel();
                    using (var commands = (IDisposable)Activator.CreateInstance(commandsType, instance, null,
                        new object[] { settings, http, new Action<string>(_ => { }), audit, stop.Token }, null))
                    {
                        string messageId = ((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1420070400000L) << 22)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture);
                        object message = parseJson.Invoke(null, new object[]
                        {
                            "{\"id\":\"" + messageId + "\",\"guild_id\":\"123\",\"channel_id\":\"456\",\"type\":0,"
                            + "\"content\":\"한글 offline chat\",\"author\":{\"id\":\"789\",\"username\":\"Mono Viking\",\"bot\":false}}"
                        });
                        Task handled = (Task)dispatch.Invoke(commands, new[] { (object)"MESSAGE_CREATE", message });
                        Check(handled.IsCompleted, "Disabled/unauthorized/empty-channel/cancelled/unready chat returns without waiting for Unity under Mono: " + index);
                        await handled.ConfigureAwait(false);
                        object queue = commandsType.GetField("_chatQueue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(commands);
                        Check((int)queue.GetType().GetProperty("Count").GetValue(queue, null) == 0 && handler.Calls == 0,
                            "Disabled/unauthorized/empty-channel/cancelled/unready chat admits no work or REST request under Mono: " + index);
                        IDictionary seen = (IDictionary)commandsType.GetField("_chatSeen", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(commands);
                        Check(seen.Count == (index >= 3 && index <= 5 ? 1 : 0),
                            "Mono records only fresh enabled chat drops for no replay before world readiness: " + index);
                        commands.Dispose();
                        await ((Task)dispatch.Invoke(commands, new[] { (object)"MESSAGE_CREATE", message })).ConfigureAwait(false);
                        Check((int)queue.GetType().GetProperty("Count").GetValue(queue, null) == 0 && handler.Calls == 0,
                            "Disposed bundled chat handler remains inert under Mono: " + index);
                    }
                }
            }
        }

        private static async Task TestBundledAdminChatAdmissionAsync(Assembly plugin)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type commandsType = plugin.GetType("ServerManager.Discord.DiscordCommands", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type json = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            MethodInfo parse = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo dispatch = commandsType.GetMethod("HandleDispatchAsync");
            MethodInfo authorize = commandsType.GetMethod("TryAuthorizeChat", instance);
            MethodInfo parseJson = json.GetMethod("Parse", new[] { typeof(string) });
            Delegate audit = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(eventType),
                typeof(DiscordMonoProbe).GetMethod("RejectChatAudit", BindingFlags.Static | BindingFlags.NonPublic));
            MethodInfo sink = plugin.GetType("ServerManager.ServerManagerRuntime", true)
                .GetMethod("TryBroadcastDiscordShout", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string), typeof(string), typeof(string), typeof(bool) }, null);
            Check(sink != null && sink.ReturnType == typeof(bool),
                "Actual Mono sink preserves separate author ID/name/message and administrator-display classification");
            // Production admission only: set readiness flags but never call Tick or a Unity/network sink.
            foreach (string mode in new[] { "admin_allowed", "admin_denied", "overlap_allowed", "overlap_denied",
                "public_user", "public_admin", "foreign_guild", "foreign_channel" })
            {
                bool publicChannel = mode.StartsWith("public", StringComparison.Ordinal);
                bool allowed = mode.EndsWith("allowed", StringComparison.Ordinal) || publicChannel;
                bool listedAdmin = mode.EndsWith("allowed", StringComparison.Ordinal) || mode == "public_admin";
                string guild = mode == "foreign_guild" ? "999" : "123";
                string channel = mode == "foreign_channel" ? "999" : "456";
                string yaml = "bot:\n  enabled: true\n  token: offline-chat-token\n  guild_ids: ['123','321']\n" +
                    "  admin_channel_ids: " + (publicChannel ? "[]" : "['456']") + "\n" +
                    "  chat_channel_ids: " + (publicChannel || mode.StartsWith("overlap", StringComparison.Ordinal) ? "['456']" : "[]") + "\n" +
                    "  admin_user_ids: " + (listedAdmin ? "['789']" : "[]") + "\n";
                object settings = parse.Invoke(null, new object[] { yaml });
                using (var handler = new OfflineHandler())
                using (var http = (IDisposable)Activator.CreateInstance(transport, instance, null,
                    new object[] { "offline-chat-token", new Action<string>(_ => { }), handler }, null))
                using (var commands = (IDisposable)Activator.CreateInstance(commandsType, instance, null,
                    new object[] { settings, http, new Action<string>(_ => { }), audit, CancellationToken.None }, null))
                {
                    commandsType.GetField("_bound", instance).SetValue(commands, true);
                    commandsType.GetField("_ready", instance).SetValue(commands, true);
                    object[] authorization = { guild, channel, "789", false };
                    Check((bool)authorize.Invoke(commands, authorization) == allowed,
                        "Mono chat authorization distinguishes administrator-first overlap from public access: " + mode);
                    if (allowed) Check((bool)authorization[3] == !publicChannel,
                        "Public-channel administrators retain actual-name display instead of forcing Admin: " + mode);
                    string messageId = ((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1420070400000L) << 22)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                    object message = parseJson.Invoke(null, new object[]
                    {
                        "{\"id\":\"" + messageId + "\",\"guild_id\":\"" + guild + "\",\"channel_id\":\"" + channel + "\",\"type\":0," +
                        "\"content\":\"good night\",\"member\":{\"nick\":\"Actual Mono Viking\"}," +
                        "\"author\":{\"id\":\"789\",\"username\":\"fallback-name\",\"bot\":false}}"
                    });
                    await ((Task)dispatch.Invoke(commands, new[] { (object)"MESSAGE_CREATE", message })).ConfigureAwait(false);
                    object queue = commandsType.GetField("_chatQueue", instance).GetValue(commands);
                    Check((int)queue.GetType().GetProperty("Count").GetValue(queue, null) == (allowed ? 1 : 0) && handler.Calls == 0,
                        "Actual Mono ingress enforces channel/user policy without REST or native calls: " + mode);
                    if (allowed)
                    {
                        object pending = queue.GetType().GetMethod("Peek").Invoke(queue, null);
                        Type pendingType = pending.GetType();
                        Check((string)pendingType.GetField("UserId", instance).GetValue(pending) == "789" &&
                            (string)pendingType.GetField("UserName", instance).GetValue(pending) == "Actual Mono Viking" &&
                            (string)pendingType.GetField("Message", instance).GetValue(pending) == "good night",
                            "Queued admin/public messages preserve actual author ID/name and unprefixed message: " + mode);
                        if (!publicChannel)
                        {
                            ((HashSet<string>)settingsType.GetProperty("AdminUserIds").GetValue(settings, null)).Clear();
                            Check(!(bool)authorize.Invoke(commands, new object[] { guild, channel, "789", false }),
                                "The delivery-time helper denies revoked admins even when public overlap exists: " + mode);
                        }
                    }
                    commands.Dispose();
                    Check((int)queue.GetType().GetProperty("Count").GetValue(queue, null) == 0,
                        "Retirement discards pending admin/public chat without delivering it: " + mode);
                }
            }
        }

        private static async Task TestBundledMultiGuildAsync(Assembly plugin)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type commandsType = plugin.GetType("ServerManager.Discord.DiscordCommands", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type json = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            string yaml = "bot:\n  enabled: true\n  token: offline-guild-token\n  guild_ids: ['111', '112']\n" +
                "  admin_channel_ids: ['222']\n  chat_channel_ids: []\n  admin_user_ids: ['444']\n";
            object settings = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { yaml });
            Delegate audit = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(eventType),
                typeof(DiscordMonoProbe).GetMethod("IgnoreCommandAudit", BindingFlags.Static | BindingFlags.NonPublic));
            MethodInfo dispatch = commandsType.GetMethod("HandleDispatchAsync");
            MethodInfo parseJson = json.GetMethod("Parse", new[] { typeof(string) });
            MethodInfo authorized = commandsType.GetMethod("IsAuthorized", instance);
            foreach (string failedGuild in new[] { "", "111", "112" })
            {
                using (var handler = new OfflineHandler { GuildRegistration = true, FailedGuild = failedGuild })
                using (var http = (IDisposable)Activator.CreateInstance(transport, instance, null,
                    new object[] { "offline-guild-token", new Action<string>(_ => { }), handler }, null))
                using (var commands = (IDisposable)Activator.CreateInstance(commandsType, instance, null,
                    new object[] { settings, http, new Action<string>(_ => { }), audit, CancellationToken.None }, null))
                {
                    object ready = parseJson.Invoke(null, new object[] { "{\"application\":{\"id\":\"777\"}}" });
                    await ((Task)dispatch.Invoke(commands, new[] { (object)"READY", ready })).ConfigureAwait(false);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while ((int)commandsType.GetField("_registering", instance).GetValue(commands) != 0)
                    {
                        if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("Offline Mono guild registration did not finish.");
                        await Task.Delay(5).ConfigureAwait(false);
                    }
                    var ids = (Dictionary<string, string>)commandsType.GetField("_commandIds", instance).GetValue(commands);
                    Check(ids.Count == (failedGuild.Length == 0 ? 68 : 34), "Actual Mono registers every healthy guild independently: " + failedGuild);
                    foreach (string guild in new[] { "111", "112" })
                    {
                        Check(handler.Urls.Any(url => url.Contains("/guilds/" + guild + "/commands")), "Actual Mono attempts configured guild " + guild);
                        Check(guild == failedGuild ? !ids.Keys.Any(key => key.StartsWith(guild + ":", StringComparison.Ordinal)) :
                            ids[guild + ":status"] == (guild == "111" ? "333" : "334"),
                            "Actual Mono keeps verified command IDs scoped to their guild " + guild);
                    }
                    Check((bool)authorized.Invoke(commands, new object[] { "status", "111", "222", "444" }) &&
                        (bool)authorized.Invoke(commands, new object[] { "status", "112", "222", "444" }) &&
                        !(bool)authorized.Invoke(commands, new object[] { "status", "999", "222", "444" }),
                        "Actual Mono admin authority requires configured guild membership");
                    if (failedGuild.Length == 0)
                    {
                        var guilds = (HashSet<string>)settingsType.GetProperty("GuildIds").GetValue(settings, null);
                        guilds.Remove("112");
                        Check(!(bool)authorized.Invoke(commands, new object[] { "status", "112", "222", "444" }) &&
                            (bool)authorized.Invoke(commands, new object[] { "status", "111", "222", "444" }),
                            "Actual Mono removing a guild revokes it without revoking retained guilds");
                        guilds.Add("112");
                    }
                }
            }
        }

        private static void IgnoreCommandAudit(object value) { }

        private static void RejectChatAudit(object value) =>
            throw new InvalidOperationException("Ordinary Discord chat must not become a command audit event.");

        private static async Task TestBundledWebhookReloadAsync(Assembly plugin)
        {
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            MethodInfo parse = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic);
            const string oldUrl = "https://discord.com/api/webhooks/123/offline-old-token";
            const string newUrl = "https://discord.com/api/webhooks/456/offline-new-token";
            string oldYaml = "webhooks:\n"
                + "  - name: mono-old\n    enabled: true\n    url: " + oldUrl
                + "\n    events: [server.status, server.saved, chat.shout]\n";
            string newYaml = "webhooks:\n"
                + "  - name: mono-new\n    enabled: true\n    url: " + newUrl
                + "\n    events: [server.status]\n";
            object original = parse.Invoke(null, new object[] { oldYaml });
            object replacement = parse.Invoke(null, new object[] { newYaml });
            Check(((IList)settingsType.GetProperty("WebhookRoutes").GetValue(replacement, null)).Count == 1,
                "Bundled strict reload parser accepts a complete route candidate without privacy switches under Mono");
            Exception rejected = null;
            try
            {
                parse.Invoke(null, new object[] { "bot:\n  enabled: true\n  token: offline-reload-secret\n" + newYaml });
            }
            catch (TargetInvocationException error) { rejected = error.InnerException; }
            Check(rejected is InvalidDataException && !rejected.ToString().Contains("offline-reload-secret"),
                "Strict reload rejects an invalid bot section despite valid webhooks without exposing its secret");

            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type sender = plugin.GetType("ServerManager.Discord.DiscordWebhooks", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            MethodInfo enqueue = sender.GetMethod("Enqueue");
            MethodInfo reload = sender.GetMethod("Reload");
            using (var handler = new OfflineHandler())
            using (var http = (IDisposable)Activator.CreateInstance(transport, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { "", new Action<string>(_ => { }), handler }, null))
            using (var webhooks = (IDisposable)Activator.CreateInstance(sender, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { original, http, new Action<string>(_ => { }) }, null))
            using (var stop = new CancellationTokenSource(5000))
            {
                object oldEvent = ReloadEvent(eventType, "mono-old-event", "server.ready", "old-generation-marker");
                Check((bool)enqueue.Invoke(webhooks, new[] { oldEvent }), "Mono sender queues old-generation work before reload");
                Check((bool)enqueue.Invoke(webhooks, new[] { ReloadEvent(eventType, "mono-old-shout", "chat.shout", "old-shout-marker") }),
                    "Selecting chat.shout alone admits shouts under actual Mono");
                Check((bool)reload.Invoke(webhooks, new[] { replacement })
                    && !(bool)enqueue.Invoke(webhooks, new[] { oldEvent }),
                    "Bundled webhook reload commits and preserves no-replay deduplication under Mono");
                object newEvent = ReloadEvent(eventType, "mono-new-event", "server.ready", "new-generation-marker");
                Check((bool)enqueue.Invoke(webhooks, new[] { newEvent })
                    && !(bool)enqueue.Invoke(webhooks, new[] { ReloadEvent(eventType, "mono-removed-event", "server.saved", "removed-kind") })
                    && !(bool)enqueue.Invoke(webhooks, new[] { ReloadEvent(eventType, "mono-removed-shout", "chat.shout", "removed-shout") }),
                    "Mono sender immediately uses the replacement event filters");
                Task running = (Task)sender.GetMethod("RunAsync").Invoke(webhooks, new object[] { stop.Token });
                await ((Task)sender.GetMethod("StopAsync").Invoke(webhooks, new object[] { TimeSpan.FromSeconds(2) })).ConfigureAwait(false);
                await running.ConfigureAwait(false);
                Check(handler.Calls == 1 && handler.Urls.Single() == newUrl + "?wait=true"
                    && handler.Bodies.Single().Contains("Server ready")
                    && !handler.Bodies.Single().Contains("old-generation-marker")
                    && !handler.Bodies.Single().Contains("old-shout-marker")
                    && !handler.Bodies.Single().Contains("987,654,321")
                    && !handler.Bodies.Single().Contains("MONOPRIVATEINVENTORY")
                    && !handler.Bodies.Single().Contains("MONOPLUGINMETADATA"),
                    "Actual Mono drops the old queue and sends only the new compact route with fixed data exclusions, entirely in memory");
                Check(!(bool)reload.Invoke(webhooks, new[] { original }), "Stopped Mono sender refuses further reloads safely");
            }
        }

        private static async Task TestBundledPlainShoutsAsync(Assembly plugin)
        {
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type sender = plugin.GetType("ServerManager.Discord.DiscordWebhooks", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            Type jsonType = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            MethodInfo parseJson = jsonType.GetMethod("Parse", new[] { typeof(string) });
            MethodInfo jsonItem = jsonType.GetMethod("get_Item", new[] { typeof(string) });
            Func<object, string, object> item = (value, name) => jsonItem.Invoke(value, new object[] { name });
            string yaml = "webhooks:\n  - name: mono-plain-shout\n    enabled: true\n" +
                "    url: https://discord.com/api/webhooks/123/plain-offline-token\n" +
                "    username: Mono relay\n    avatar_url: https://example.test/avatar.png\n" +
                "    events: [chat.shout, server.status]\n";
            object settings = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { yaml });
            using (var handler = new OfflineHandler())
            using (var http = (IDisposable)Activator.CreateInstance(transport, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { "", new Action<string>(_ => { }), handler }, null))
            using (var webhooks = (IDisposable)Activator.CreateInstance(sender, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { settings, http, new Action<string>(_ => { }) }, null))
            {
                string[] messages = { "Complete: asdf", "Complete: **bold** @everyone <@12345> \"quoted\"\0\r\nnext",
                    new string('x', 1998) + "\ud83d\ude03" + new string('z', 2000) };
                foreach (string message in messages)
                {
                    var fields = new Dictionary<string, string>
                    {
                        ["message"] = message, ["title"] = "MONO_SHOUT_TITLE", ["player_name"] = "MONO_ALTERNATE_NAME",
                        ["text"] = "MONO_ALTERNATE_TEXT", ["shout"] = "MONO_ALTERNATE_SHOUT", ["plugins"] = "MONO_SHOUT_PLUGINS",
                        ["coordinates"] = "987,654,321", ["inventory"] = "MONO_PRIVATE_INVENTORY", ["token"] = "MONO_PRIVATE_TOKEN"
                    };
                    object value = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new object[] { Guid.NewGuid().ToString("N"), DateTime.UtcNow, "mono-server-id", "chat.shout", "client_reported", null, null, fields }, null);
                    Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { value }), "Actual Mono queues nonempty canonical shout content");
                }
                foreach (string blank in new[] { "", " \r\n\t", "\0\r" })
                {
                    object value = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new object[] { Guid.NewGuid().ToString("N"), DateTime.UtcNow, "mono-server-id", "chat.shout", "client_reported", null, null,
                            new Dictionary<string, string> { ["message"] = blank, ["text"] = "MONO_DO_NOT_FALLBACK" } }, null);
                    Check(!(bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { value }), "Actual Mono rejects empty plain content without falling back to unrelated fields");
                }
                Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { ReloadEvent(eventType, "mono-nonshout-embed", "server.ready", "ordinary-embed") }),
                    "Actual Mono still queues ordinary embedded event summaries");
                Task running = (Task)sender.GetMethod("RunAsync").Invoke(webhooks, new object[] { CancellationToken.None });
                await ((Task)sender.GetMethod("StopAsync").Invoke(webhooks, new object[] { TimeSpan.FromSeconds(2) })).ConfigureAwait(false);
                await running.ConfigureAwait(false);
                Check(handler.Calls == 4, "Actual Mono sends only nonempty plain shouts and the ordinary event");
                for (int index = 0; index < 3; ++index)
                {
                    object payload = parseJson.Invoke(null, new object[] { handler.Bodies[index] });
                    object content = item(payload, "content");
                    Check(content != null && item(payload, "embeds") == null && item(payload, "flags").ToString() == "4",
                        "Actual Mono produces content with suppressed link previews and no embeds for chat.shout");
                    IEnumerable properties = (IEnumerable)jsonType.GetMethod("Properties", Type.EmptyTypes).Invoke(payload, null);
                    Check(properties.Cast<object>().Select(property => (string)property.GetType().GetProperty("Name").GetValue(property, null))
                        .All(name => new[] { "content", "flags", "allowed_mentions", "username", "avatar_url" }.Contains(name)),
                        "Actual Mono plain shout has no title, fields, footer, timestamp, reliability or event metadata");
                    object mentions = item(payload, "allowed_mentions");
                    Check(item(mentions, "parse").ToString() == "[]" && item(mentions, "users").ToString() == "[]" &&
                        item(mentions, "roles").ToString() == "[]" && item(mentions, "replied_user").ToString() == "False",
                        "Actual Mono plain shout disables all mention parsing");
                    Check(item(payload, "username").ToString() == "Mono relay" && item(payload, "avatar_url").ToString() == "https://example.test/avatar.png",
                        "Actual Mono retains the configured webhook sender identity for plain shout delivery");
                    Check(!handler.Bodies[index].Contains("MONO_") && !handler.Bodies[index].Contains("987,654,321") &&
                        !handler.Bodies[index].Contains("mono-server-id"), "Actual Mono plain shout never appends alternate body, plugin or privacy metadata");
                    if (index == 0) Check(content.ToString() == "Complete: asdf", "Actual Mono renders exactly the player name and canonical shout message");
                    else if (index == 1) Check(content.ToString().Contains("\\*\\*bold\\*\\*") && content.ToString().Contains("\"quoted\"") &&
                        !content.ToString().Contains("@everyone") && !content.ToString().Contains("<@12345>") && !content.ToString().Contains("\0") &&
                        content.ToString().Contains("\nnext"), "Actual Mono preserves literal text while neutralizing mentions, markdown and unsafe controls");
                    else Check(content.ToString().Length <= 2000 && content.ToString().EndsWith("…", StringComparison.Ordinal) &&
                        new UTF8Encoding(false, true).GetByteCount(content.ToString()) > 0, "Actual Mono bounds plain Discord content without splitting surrogate pairs");
                }
                object ordinary = parseJson.Invoke(null, new object[] { handler.Bodies[3] });
                Check(item(ordinary, "content") == null && item(ordinary, "flags") == null && item(ordinary, "embeds") != null &&
                    !handler.Bodies[3].Contains("MONOPLUGINMETADATA") && handler.Bodies[3].Contains("Server ready"),
                    "Actual Mono renders the non-shout lifecycle card without arbitrary metadata");
            }
        }

        private static async Task TestBundledCompactCardsAsync(Assembly plugin)
        {
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type sender = plugin.GetType("ServerManager.Discord.DiscordWebhooks", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            Type actorType = plugin.GetType("ServerManager.Events.ServerManagerActor", true);
            Type jsonType = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            MethodInfo parseJson = jsonType.GetMethod("Parse", new[] { typeof(string) });
            MethodInfo item = jsonType.GetMethod("get_Item", new[] { typeof(string) });
            Func<string, string, object> actor = (name, kind) => Activator.CreateInstance(actorType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { "MONO_PRIVATE_ID", name, kind }, null);
            object player = actorType.GetMethod("WithPlayerAccount", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(actor("Complete", "player"), new object[] { "steamworks:76561198000000001" });
            var kinds = new[] { "server.ready", "server.shutdown", "server.saved", "player.login", "player.leave", "player.death", "boss.killed",
                "raid.started", "raid.ended", "server.announcement" };
            string[] filters = { "server.status", "server.saved", "player.connection", "player.death", "boss.killed",
                "raid.status", "server.announcement" };
            foreach (bool anonymous in new[] { false, true })
            foreach (string language in new[] { "", "English", "Korean", "Unknown_Public_Language" })
            {
                bool korean = language == "Korean";
                string yaml = "webhooks:\n  - name: mono-compact\n    enabled: true\n" +
                    "    url: https://discord.com/api/webhooks/123/compact-offline-token\n" +
                    (anonymous ? "    anonymous_prefix: Guest\n" : "") + (language.Length == 0 ? "" : "    language: " + language + "\n") +
                    "    events: [" + string.Join(",", filters) + "]\n";
                object settings = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { yaml });
                using (var handler = new OfflineHandler())
                using (var http = (IDisposable)Activator.CreateInstance(transport, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { "", new Action<string>(_ => { }), handler }, null))
                using (var webhooks = (IDisposable)Activator.CreateInstance(sender, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { settings, http, new Action<string>(_ => { }) }, null))
                {
                    var expectedTitles = new List<string>();
                    foreach (string kind in kinds)
                    {
                        string who = anonymous ? "Guest 1" : "Complete";
                        var fields = new Dictionary<string, string> { ["title"] = "MONO_PRIVATE_TITLE", ["message"] = "MONO_PRIVATE_MESSAGE",
                            ["first_join"] = "true", ["cause"] = "creature", ["boss"] = "Eikthyr", ["operation_id"] = "MONO_PRIVATE_OPERATION",
                            ["character_commit_scope"] = "partial_retained_shadows_at_cutoff", ["completion_scope"] = "world_disk_with_partial_retained_characters",
                            ["pending_character_count"] = "2" };
                        if (kind == "raid.started" || kind == "raid.ended")
                        {
                            fields["raid"] = "Forest {0}";
                            fields["raid_coordinates"] = "-371, 40, 1591";
                        }
                        if (kind == "server.announcement") fields["message"] = "Complete {0}: English and 한국어 remain literal.";
                        object target = kind == "boss.killed" ? actor("Eikthyr", "boss") : kind == "player.death" ? actor("Greydwarf", "creature") : null;
                        object value = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, new object[] { Guid.NewGuid().ToString("N"), DateTime.UtcNow, "MONO_PRIVATE_SERVER", kind,
                                "authoritative", player, target, fields }, null);
                        Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { value }), "Actual Mono admits compact card " + kind);
                        switch (kind)
                        {
                            case "server.ready": expectedTitles.Add(korean ? "서버 준비 완료" : "Server ready"); break;
                            case "server.shutdown": expectedTitles.Add(korean ? "서버 종료 중" : "Server shutdown"); break;
                            case "server.saved": expectedTitles.Add(korean ? "월드 저장 완료" : "World saved"); break;
                            case "player.login": expectedTitles.Add(who + (korean ? " 첫 접속" : " joined for the first time")); break;
                            case "player.leave": expectedTitles.Add(who + (korean ? " 접속 종료" : " left")); break;
                            case "raid.started": expectedTitles.Add((korean ? "습격 시작" : "Raid started") + " — Forest {0} \\[-371, 40, 1591\\]"); break;
                            case "raid.ended": expectedTitles.Add((korean ? "습격 종료" : "Raid ended") + " — Forest {0} \\[-371, 40, 1591\\]"); break;
                            case "server.announcement": expectedTitles.Add(korean ? "공지" : "Announcement"); break;
                            default:
                                object[] formatArgs = { value, who,
                                    kind == "boss.killed" ? "Eikthyr" : "Greydwarf", language, null };
                                Check((bool)plugin.GetType("ServerManager.Events.EventMessageText", true)
                                    .GetMethod("TryFormat", BindingFlags.Static | BindingFlags.NonPublic)
                                    .Invoke(null, formatArgs), "Actual Mono formats a shared event sentence");
                                expectedTitles.Add((string)formatArgs[4]);
                                if (language == "Korean") Check(((string)formatArgs[4]).Any(ch => ch >= '\uac00' && ch <= '\ud7a3'),
                                    "Actual Mono resolves embedded Korean event translation resources");
                                break;
                        }
                    }
                    foreach (string removed in new[] { "server.started", "player.first_join" })
                        Check(!(bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { ReloadEvent(eventType, Guid.NewGuid().ToString("N"), removed, "removed") }),
                            "Actual Mono never dispatches removed webhook kind " + removed);
                    Task running = (Task)sender.GetMethod("RunAsync").Invoke(webhooks, new object[] { CancellationToken.None });
                    await ((Task)sender.GetMethod("StopAsync").Invoke(webhooks, new object[] { TimeSpan.FromSeconds(3) })).ConfigureAwait(false);
                    await running.ConfigureAwait(false);
                    Check(handler.Calls == kinds.Length, "Actual Mono keeps the partial-save warning in the same single delivery");
                    for (int index = 0; index < kinds.Length; index++)
                    {
                        string body = handler.Bodies[index]; object payload = parseJson.Invoke(null, new object[] { body });
                        object[] cards = ((IEnumerable)item.Invoke(payload, new object[] { "embeds" })).Cast<object>().ToArray();
                        Check(cards.Length == (kinds[index] == "server.saved" ? 2 : 1) &&
                            item.Invoke(cards[0], new object[] { "title" }).ToString() == expectedTitles[index],
                            "Actual Mono compact event title and card count match " + kinds[index]);
                        Check(!body.Contains("MONO_PRIVATE") && !body.Contains("\"fields\"") && !body.Contains("\"footer\"") && !body.Contains("\"timestamp\""),
                            "Actual Mono compact cards never append diagnostic fields, footer or timestamp");
                        if (kinds[index] == "server.saved") Check(item.Invoke(cards[1], new object[] { "title" }).ToString() == "Character save pending — 2" &&
                            item.Invoke(cards[0], new object[] { "description" }) == null && item.Invoke(cards[1], new object[] { "description" }) == null,
                            "Actual Mono world/partial-character cards are distinct title-only statements");
                        if (kinds[index] == "raid.started" || kinds[index] == "raid.ended")
                            Check(item.Invoke(cards[0], new object[] { "description" }) == null,
                                "Actual Mono raid name and verified center share one localized title on named and anonymous routes");
                        if (kinds[index] == "server.announcement")
                            Check(item.Invoke(cards[0], new object[] { "description" }).ToString() == "Complete {0}: English and 한국어 remain literal.",
                                "Actual Mono translates only the announcement heading, never administrator-authored text");
                        if (kinds[index] == "player.death")
                        {
                            string sentence = item.Invoke(cards[0], new object[] { "title" }).ToString();
                            Check(item.Invoke(cards[0], new object[] { "description" }) == null && !sentence.Contains("\n") &&
                                sentence.Contains("Greydwarf"),
                                "Actual Mono death card preserves typed NPC names while projecting the player identity");
                        }
                    }
                }
            }
        }

        private static async Task TestBundledConnectionSteamIdsAsync(Assembly plugin)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type sender = plugin.GetType("ServerManager.Discord.DiscordWebhooks", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            Type actorType = plugin.GetType("ServerManager.Events.ServerManagerActor", true);
            Type jsonType = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            MethodInfo parseJson = jsonType.GetMethod("Parse", new[] { typeof(string) });
            MethodInfo item = jsonType.GetMethod("get_Item", new[] { typeof(string) });
            MethodInfo withAccount = actorType.GetMethod("WithPlayerAccount", instance);
            PropertyInfo principal = actorType.GetProperty("PlayerAccountId", instance);
            Func<string, string, object> actor = (id, name) => Activator.CreateInstance(actorType, instance, null,
                new object[] { id, name, "player" }, null);
            const string steam64 = "76561198000000001";
            const string account = "steamworks:" + steam64;
            object verified = withAccount.Invoke(actor("MONO_PRIVATE_RAW_ID", "Complete"), new object[] { account });
            // kind, actor, expected verified Steam64, first-join flag. Forged
            // adapter attribution below tests validation at the disclosure edge.
            var cases = new List<Tuple<string, object, string, bool>>();
            foreach (string kind in new[] { "player.login", "player.leave" })
            {
                foreach (string valid in new[] { steam64, "76561197960265729", "76561202255233023" })
                    cases.Add(Tuple.Create(kind, withAccount.Invoke(actor("MONO_PRIVATE_RAW_ID", "Complete"),
                        new object[] { "steamworks:" + valid }), valid, false));
                cases.Add(Tuple.Create(kind, (object)null, "", false));
                cases.Add(Tuple.Create(kind, actor(account, "Complete"), "", false));
                cases.Add(Tuple.Create(kind, actor("MONO_PRIVATE_RAW_ID", steam64), "", false));
                foreach (string invalid in new[] { null, "", steam64, "Steamworks:" + steam64, "playfab:" + steam64,
                    "steamworks:076561198000000001", account + " ", account + "\n", "steamworks:+76561198000000001",
                    "steamworks:76561197960265728", "steamworks:76561202255233024", "steamworks:103582791429521408",
                    "steamworks:00000000000000001", "steamworks:7656119800000000x", "steamworks:７６５６１１９８００００００００１" })
                {
                    object unverified = withAccount.Invoke(verified, new object[] { invalid });
                    Check(principal.GetValue(unverified, null).ToString() == "",
                        "Actual Mono rejects malformed/non-individual producer account attribution");
                    object forged = actor("MONO_PRIVATE_RAW_ID", "Complete");
                    principal.GetSetMethod(true).Invoke(forged, new object[] { invalid });
                    cases.Add(Tuple.Create(kind, forged, "", false));
                }
            }
            cases.Add(Tuple.Create("player.login", verified, steam64, true));
            foreach (string kind in new[] { "server.ready", "server.shutdown", "server.saved", "raid.started", "raid.ended", "player.death", "chat.shout" })
                cases.Add(Tuple.Create(kind, verified, "", false));
            foreach (string option in new[] { "omitted", "false", "true", "anonymous" })
            foreach (string language in new[] { "English", "Korean" })
            {
                bool anonymous = option == "anonymous";
                bool optIn = option == "true" || anonymous;
                string yaml = "webhooks:\n  - name: mono-steam-id\n    url: https://discord.com/api/webhooks/123/identity-offline-token\n" +
                    "    events: [player.connection, server.status, server.saved, raid.status, player.death, chat.shout]\n" +
                    "    language: " + language + "\n" + (anonymous ? "    anonymous_prefix: Guest\n" : "") +
                    (option == "omitted" ? "" : "    include_steam_id: " + (optIn ? "true" : "false") + "\n");
                object settings = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { yaml });
                using (var handler = new OfflineHandler())
                using (var http = (IDisposable)Activator.CreateInstance(transport, instance, null,
                    new object[] { "", new Action<string>(_ => { }), handler }, null))
                using (var webhooks = (IDisposable)Activator.CreateInstance(sender, instance, null,
                    new object[] { settings, http, new Action<string>(_ => { }) }, null))
                {
                    object route = ((IList)settingsType.GetProperty("WebhookRoutes").GetValue(settings, null))[0];
                    route.GetType().GetProperty("IncludeSteamId").SetValue(route, !optIn, null);
                    foreach (var test in cases)
                    {
                        var fields = new Dictionary<string, string> { ["account_id"] = account, ["steam_id"] = steam64,
                            ["player_account_id"] = account, ["message"] = "MONO_PRIVATE_MESSAGE " + account,
                            ["text"] = "safe words", ["raid"] = "Forest", ["first_join"] = test.Item4 ? "true" : "false" };
                        if (test.Item1 == "chat.shout") fields["message"] = "Complete: safe words";
                        object value = Activator.CreateInstance(eventType, instance, null,
                            new object[] { Guid.NewGuid().ToString("N"), DateTime.UtcNow, "mono-probe", test.Item1,
                                "authoritative", test.Item2, verified, fields }, null);
                        string originalPrincipal = test.Item2 == null ? null : (string)principal.GetValue(test.Item2, null);
                        Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { value }),
                            "Actual Mono routes Steam-ID privacy case " + test.Item1 + "/" + option);
                        Check((string)eventType.GetProperty("Kind").GetValue(value, null) == test.Item1 && fields["account_id"] == account &&
                            (test.Item2 == null || (string)principal.GetValue(test.Item2, null) == originalPrincipal),
                            "Actual Mono Steam ID rendering never mutates raw source kinds, evidence or adapter attribution");
                    }
                    Task running = (Task)sender.GetMethod("RunAsync").Invoke(webhooks, new object[] { CancellationToken.None });
                    await ((Task)sender.GetMethod("StopAsync").Invoke(webhooks, new object[] { TimeSpan.FromSeconds(3) })).ConfigureAwait(false);
                    await running.ConfigureAwait(false);
                    Check(handler.Calls == cases.Count, "Actual Mono delivers each Steam ID case once with a detached route option");
                    for (int index = 0; index < cases.Count; ++index)
                    {
                        var test = cases[index]; string body = handler.Bodies[index];
                        bool show = optIn && !anonymous && test.Item3.Length > 0;
                        object payload = parseJson.Invoke(null, new object[] { body });
                        Check(!body.Contains("steamworks:") && !body.Contains("MONO_PRIVATE") && !body.Contains("player_account_id") &&
                            !body.Contains("\"fields\"") && !body.Contains("\"footer\"") && !body.Contains("\"timestamp\""),
                            "Actual Mono never exposes account prefixes, raw-ID fallbacks, unstructured fields or extra metadata");
                        if (test.Item1 == "chat.shout")
                        {
                            Check(!body.Contains("Steam ID:") && !body.Contains(steam64) && item.Invoke(payload, new object[] { "embeds" }) == null,
                                "Actual Mono Steam ID opt-in never changes plain shout payloads");
                            continue;
                        }
                        object card = ((IEnumerable)item.Invoke(payload, new object[] { "embeds" })).Cast<object>().Single();
                        object description = item.Invoke(card, new object[] { "description" });
                        Check(show ? description != null && description.ToString() == "Steam ID: " + test.Item3 : description == null,
                            "Actual Mono Steam ID is a dedicated connection description only after opt-in and verified attribution");
                        if (show)
                            Check(body.IndexOf(test.Item3, StringComparison.Ordinal) == body.LastIndexOf(test.Item3, StringComparison.Ordinal),
                                "Actual Mono publishes the verified Steam64 exactly once");
                        if (anonymous) Check(!body.Contains(steam64) && !body.Contains("Steam ID:"),
                            "Actual Mono anonymous_prefix overrides include_steam_id even for valid account attribution");
                        if (!anonymous && test.Item3.Length > 0 && (test.Item1 == "player.login" || test.Item1 == "player.leave"))
                            Check(item.Invoke(card, new object[] { "title" }).ToString() == "Complete" +
                                (test.Item1 == "player.leave" ? language == "Korean" ? " 접속 종료" : " left" :
                                    test.Item4 ? language == "Korean" ? " 첫 접속" : " joined for the first time" :
                                        language == "Korean" ? " 접속" : " joined"),
                                "Actual Mono Steam ID description preserves localized connection/first-join titles");
                    }
                }
            }
        }

        private static async Task TestBundledTranslationDiscoveryAsync(Assembly plugin)
        {
            const string language = "MonoDiscoveryFixture";
            Type paths = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "BepInEx")
                .GetType("BepInEx.Paths", true);
            string root = (string)paths.GetProperty("BepInExRootPath").GetValue(null, null);
            string config = (string)paths.GetProperty("ConfigPath").GetValue(null, null);
            Check(root == Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "isolated-bepinex") && config == Path.Combine(root, "config"),
                "Actual Mono translation scans are isolated from the installed BepInEx and config roots");
            string packDirectory = Path.Combine(root, "plugins", "MonoPack", "translations");
            string otherDirectory = Path.Combine(root, "patchers", "OtherPack");
            Directory.CreateDirectory(packDirectory); Directory.CreateDirectory(config); Directory.CreateDirectory(otherDirectory);
            string fileName = "ServerManager." + language + ".yml";
            string distributedFile = Path.Combine(packDirectory, fileName), configFile = Path.Combine(config, fileName);
            string duplicateFile = Path.Combine(otherDirectory, fileName);
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(distributedFile, "sm_event_server_ready: Mono pack ready\nsm_event_server_shutdown: Mono pack shutdown\n", utf8);
            File.WriteAllText(configFile, "sm_event_server_ready: Mono config ready\n", utf8);
            Type localizer = plugin.GetType("ServerManager.PlayerLocalizer", true);
            MethodInfo reloadLanguage = localizer.GetMethod("ReloadLanguage", BindingFlags.Static | BindingFlags.NonPublic);
            Func<bool> reload = () => (bool)reloadLanguage.Invoke(null, new object[] { language, root, config });
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type sender = plugin.GetType("ServerManager.Discord.DiscordWebhooks", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            Type jsonType = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            MethodInfo parseJson = jsonType.GetMethod("Parse", new[] { typeof(string) });
            MethodInfo item = jsonType.GetMethod("get_Item", new[] { typeof(string) });
            string yaml = "webhooks:\n - name: discovered\n   enabled: true\n   url: https://discord.com/api/webhooks/123/offline-discovery\n" +
                "   language: " + language + "\n   events: [server.status]\n";
            object settings = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { yaml });
            using (var handler = new OfflineHandler())
            using (var http = (IDisposable)Activator.CreateInstance(transport, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { "", new Action<string>(_ => { }), handler }, null))
            using (var webhooks = (IDisposable)Activator.CreateInstance(sender, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { settings, http, new Action<string>(_ => { }) }, null))
            {
                var expected = new List<string>();
                Action<string, string> enqueue = (kind, title) =>
                {
                    Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { ReloadEvent(eventType, Guid.NewGuid().ToString("N"), kind, "discarded raw message") }),
                        "Actual Mono webhook renders a discovered language through the normal event path");
                    expected.Add(title);
                };
                enqueue("server.ready", "Mono config ready"); enqueue("server.shutdown", "Mono pack shutdown");
                File.WriteAllText(configFile, "sm_event_server_ready: Mono changed config\n", utf8);
                File.WriteAllText(distributedFile, "sm_event_server_shutdown: Mono changed pack\n", utf8);
                enqueue("server.ready", "Mono config ready"); enqueue("server.shutdown", "Mono pack shutdown");
                Check(reload(), "Actual Mono explicitly reloads a nested package plus the unique high-priority config override");
                enqueue("server.ready", "Mono changed config"); enqueue("server.shutdown", "Mono changed pack");
                File.WriteAllText(duplicateFile, "sm_event_server_ready: REJECTED_DUPLICATE\n", utf8);
                File.WriteAllText(configFile, "sm_event_server_ready: REJECTED_CONFIG\n", utf8);
                Check(!reload(), "Actual Mono rejects duplicate distributed files without traversal-order selection");
                enqueue("server.ready", "Mono changed config"); enqueue("server.shutdown", "Mono changed pack");
                File.Move(duplicateFile, duplicateFile + ".disabled");
                File.WriteAllText(distributedFile, "sm_event_server_ready: [broken\n", utf8);
                Check(!reload(), "Actual Mono rejects malformed package YAML atomically with pending config changes");
                enqueue("server.ready", "Mono changed config"); enqueue("server.shutdown", "Mono changed pack");
                File.WriteAllText(distributedFile, "sm_event_server_shutdown: Mono recovered pack\n", utf8);
                File.WriteAllText(configFile, "sm_event_server_ready: Mono recovered config\n", utf8);
                Check(reload(), "Actual Mono recovers after correcting package and config files");
                enqueue("server.ready", "Mono recovered config"); enqueue("server.shutdown", "Mono recovered pack");
                Task running = (Task)sender.GetMethod("RunAsync").Invoke(webhooks, new object[] { CancellationToken.None });
                await ((Task)sender.GetMethod("StopAsync").Invoke(webhooks, new object[] { TimeSpan.FromSeconds(3) })).ConfigureAwait(false);
                await running.ConfigureAwait(false);
                Check(handler.Calls == expected.Count, "Actual Mono sends each cached/layered event exactly once");
                for (int index = 0; index < expected.Count; ++index)
                {
                    object payload = parseJson.Invoke(null, new object[] { handler.Bodies[index] });
                    object card = ((IEnumerable)item.Invoke(payload, new object[] { "embeds" })).Cast<object>().Single();
                    Check(item.Invoke(card, new object[] { "title" }).ToString() == expected[index] &&
                        item.Invoke(card, new object[] { "description" }) == null && !handler.Bodies[index].Contains("REJECTED"),
                        "Actual Mono keeps config priority, no per-message reload and last-good content across rejected candidates");
                }
            }
        }

        private static async Task TestBundledAnonymousWebhooksAsync(Assembly plugin)
        {
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type sender = plugin.GetType("ServerManager.Discord.DiscordWebhooks", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            Type actorType = plugin.GetType("ServerManager.Events.ServerManagerActor", true);
            Type jsonType = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            MethodInfo parseJson = jsonType.GetMethod("Parse", new[] { typeof(string) });
            MethodInfo jsonItem = jsonType.GetMethod("get_Item", new[] { typeof(string) });
            MethodInfo withAccount = actorType.GetMethod("WithPlayerAccount", BindingFlags.Instance | BindingFlags.NonPublic);
            PropertyInfo principal = actorType.GetProperty("PlayerAccountId", BindingFlags.Instance | BindingFlags.NonPublic);
            Func<string, string, object> actor = (id, name) => Activator.CreateInstance(actorType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { id, name, "player" }, null);
            object raw = actor("steamworks:76561198000000001", "MONO_SECRET_ORIGINAL");
            object verified = withAccount.Invoke(raw, new object[] { "steamworks:76561198000000001" });
            Check(!ReferenceEquals(raw, verified) && principal.GetValue(raw, null).ToString() == "" &&
                principal.GetValue(verified, null).ToString() == "steamworks:76561198000000001" &&
                actorType.GetProperty("Id").GetValue(raw, null).Equals(actorType.GetProperty("Id").GetValue(verified, null)),
                "Actual Mono verified account attribution clones actors without deriving identity from a raw actor ID");
            foreach (string invalid in new[] { "", "76561198000000001", "Steamworks:76561198000000001",
                "steamworks:00000000000000001", "steamworks:7656119800000000x", "steamworks:76561198000000001 ",
                "steamworks:76561197960265728", "steamworks:76561202255233024" })
                Check(principal.GetValue(withAccount.Invoke(verified, new object[] { invalid }), null).ToString() == "",
                    "Actual Mono invalid account attribution safely produces an unverified clone: " + invalid);
            string[] kinds = { "server.ready", "server.saved", "server.shutdown", "server.announcement",
                "player.login", "player.leave", "chat.shout", "raid.started", "raid.ended", "player.death",
                "combat.pvp_kill", "boss.killed", "moderation.action", "command.executed", "security.detection", "security.response",
                "security.admin_bypass", "character.save_rejected", "character.shadow_stalled", "character.validation_observed",
                "character.revision_observed", "connection.rejected" };
            string[] filters = { "server.status", "server.saved", "server.announcement",
                "player.connection", "chat.shout", "raid.status", "player.death",
                "boss.killed", "moderation.action", "command.executed", "security.alert", "security.admin_bypass",
                "character.validation", "character.shadow_stalled", "character.revision_observed", "connection.rejected" };
            string yaml = "webhooks:\n  - name: mono-anonymous\n    enabled: true\n" +
                "    url: https://discord.com/api/webhooks/123/anonymous-offline-token\n    anonymous_prefix: Guest\n    include_steam_id: true\n" +
                "    username: Public relay\n    avatar_url: https://example.test/public.png\n    events: [" + string.Join(",", filters) + "]\n";
            object settings = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { yaml });
            using (var handler = new OfflineHandler())
            using (var http = (IDisposable)Activator.CreateInstance(transport, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { "", new Action<string>(_ => { }), handler }, null))
            using (var webhooks = (IDisposable)Activator.CreateInstance(sender, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { settings, http, new Action<string>(_ => { }) }, null))
            {
                foreach (string kind in kinds)
                {
                    var fields = new Dictionary<string, string>();
                    foreach (string key in new[] { "message", "title", "character", "player_name", "target", "target_name", "account_id",
                        "source", "reason_code", "outcome", "response", "category", "stage", "evidence", "detail", "finding", "command",
                        "plugin_details", "plugins", "revision", "count", "raid_coordinates", "inventory", "token", "MONO_SECRET_FIELD" })
                        fields[key] = "MONO_SECRET_" + key;
                    fields["text"] = "public words";
                    if (kind == "server.announcement") fields["message"] = "Operator-written public announcement";
                    if (kind == "raid.started" || kind == "raid.ended") fields["raid"] = "Public Forest";
                    object target = raw;
                    if (kind == "player.death" || kind == "boss.killed")
                    {
                        fields["cause"] = kind == "player.death" ? "creature" : "boss";
                        target = Activator.CreateInstance(actorType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, new object[] { "MONO_SECRET_NPC_ID", kind == "player.death" ? "Public Greydwarf" : "Public Eikthyr",
                                kind == "player.death" ? "creature" : "boss" }, null);
                    }
                    object value = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new object[] { Guid.NewGuid().ToString("N"), DateTime.UtcNow, "MONO_SECRET_SERVER", kind,
                            "MONO_SECRET_RELIABILITY", verified, target, fields }, null);
                    Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { value }), "Actual Mono anonymous routes accept " + kind);
                }
                // A canonical-looking raw Id has no verified principal and must
                // never receive a number by guessing, even after that account's
                // verified actor has already been encountered above.
                object unknown = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { Guid.NewGuid().ToString("N"), DateTime.UtcNow, "MONO_SECRET_SERVER", "chat.shout", "authoritative",
                        raw, null, new Dictionary<string, string> { ["text"] = "unknown", ["message"] = "MONO_SECRET_ORIGINAL: unknown" } }, null);
                Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { unknown }), "Actual Mono keeps unverified public chat anonymous without guessing an account");
                Task running = (Task)sender.GetMethod("RunAsync").Invoke(webhooks, new object[] { CancellationToken.None });
                await ((Task)sender.GetMethod("StopAsync").Invoke(webhooks, new object[] { TimeSpan.FromSeconds(3) })).ConfigureAwait(false);
                await running.ConfigureAwait(false);
                Check(handler.Calls == 23, "Actual Mono sends all 22 anonymous event kinds plus the unverified shout");
                for (int index = 0; index < handler.Bodies.Count; index++)
                {
                    string body = handler.Bodies[index];
                    Check(!body.Contains("MONO_SECRET") && !body.Contains("76561198000000001") && !body.Contains("steamworks:"),
                        "Actual Mono anonymous JSON contains no raw player identity, target ID, server, reliability, plugin or arbitrary field values");
                    if (index == 7 || index == 8) Check(body.Contains("Public Forest"), "Anonymous raid cards retain the public raid name");
                    if (index == 9) Check(body.Contains("Public Greydwarf"), "Anonymous death cards retain a typed NPC name");
                    if (index == 11) Check(body.Contains("Public Eikthyr"), "Anonymous boss cards retain a typed boss name");
                    object payload = parseJson.Invoke(null, new object[] { body });
                    object mentions = jsonItem.Invoke(payload, new object[] { "allowed_mentions" });
                    Check(jsonItem.Invoke(mentions, new object[] { "parse" }).ToString() == "[]" &&
                        jsonItem.Invoke(payload, new object[] { "username" }).ToString() == "Public relay",
                        "Actual Mono anonymous projection retains route branding and disabled mention parsing");
                    if (index == 6 || index == 22)
                    {
                        string content = jsonItem.Invoke(payload, new object[] { "content" }).ToString();
                        Check(jsonItem.Invoke(payload, new object[] { "embeds" }) == null &&
                            jsonItem.Invoke(payload, new object[] { "flags" }).ToString() == "4" &&
                            content.EndsWith(index == 6 ? ": public words" : ": unknown", StringComparison.Ordinal),
                            "Actual Mono anonymous shout is plain alias/text content with suppressed link previews");
                        if (index == 22) Check(content == "Guest player: unknown", "Actual Mono unverified actor uses the generic non-numbered alias");
                    }
                    else Check(!body.Contains("\"fields\"") && !body.Contains("\"footer\"") && !body.Contains("\"timestamp\""),
                        "Actual Mono anonymous cards never contain redundant fields, footer or timestamp");
                }
            }
        }

        private static async Task TestBundledAnonymousStoryTargetsAsync(Assembly plugin)
        {
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type sender = plugin.GetType("ServerManager.Discord.DiscordWebhooks", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            Type actorType = plugin.GetType("ServerManager.Events.ServerManagerActor", true);
            Type jsonType = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            MethodInfo parseJson = jsonType.GetMethod("Parse", new[] { typeof(string) });
            MethodInfo item = jsonType.GetMethod("get_Item", new[] { typeof(string) });
            MethodInfo withAccount = actorType.GetMethod("WithPlayerAccount", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo format = plugin.GetType("ServerManager.Events.EventMessageText", true).GetMethod("TryFormat", BindingFlags.Static | BindingFlags.NonPublic);
            Func<string, string, bool, object> actor = (name, kind, account) =>
            {
                object value = Activator.CreateInstance(actorType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { "steamworks:76561198000000002", name, kind }, null);
                return account ? withAccount.Invoke(value, new object[] { "steamworks:76561198000000002" }) : value;
            };
            object player = withAccount.Invoke(actor("MONO_PRIVATE_ACTOR", "player", false), new object[] { "steamworks:76561198000000001" });
            // Event kind, cause, target kind, verified account, projected other name.
            // A target's player signal wins even when another field claims NPC/boss.
            var cases = new List<string[]>
            {
                new[] { "player.death", "creature", "creature", "no", "Public creature" },
                new[] { "player.death", "enemyhit", "creature", "no", "Public creature" },
                new[] { "player.death", "boss", "boss", "no", "Public creature" },
                new[] { "boss.killed", "", "boss", "no", "Public creature" },
                new[] { "combat.pvp_kill", "creature", "creature", "yes", "Guest 2" },
                new[] { "combat.pvp_kill", "creature", "creature", "no", "Guest player" },
                new[] { "player.death", "pvp", "creature", "yes", "Guest 2" },
                new[] { "player.death", "playerhit", "boss", "no", "Guest player" },
                new[] { "player.death", "creature", "PlAyEr", "no", "Guest player" },
                new[] { "player.death", "boss", "PLAYER", "yes", "Guest 2" },
                new[] { "player.death", "creature", "creature", "yes", "Guest 2" },
                new[] { "boss.killed", "", "boss", "yes", "Guest 2" },
                new[] { "boss.killed", "", "pLaYeR", "no", "Guest player" },
                new[] { "player.death", "creature", "unknown", "no", "Public creature" },
                new[] { "player.death", "creature", "<null>", "no", "Public creature" },
                new[] { "boss.killed", "", "unknown", "no", "Public creature" },
                new[] { "boss.killed", "", "<null>", "no", "Public creature" }
            };
            foreach (string cause in new[] { "smoke", "freezing", "burning", "poisoned", "drowning", "fall", "tree", "unknown", "MONO_PRIVATE_CAUSE" })
                cases.Add(new[] { "player.death", cause, "creature", "no", "" });
            foreach (string language in new[] { "English", "Korean" })
            {
                string yaml = "webhooks:\n  - name: mono-target-projection\n    enabled: true\n" +
                    "    url: https://discord.com/api/webhooks/123/projection-offline-token\n    anonymous_prefix: Guest\n" +
                    "    language: " + language + "\n    events: [player.death, boss.killed]\n";
                object settings = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { yaml });
                using (var handler = new OfflineHandler())
                using (var http = (IDisposable)Activator.CreateInstance(transport, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { "", new Action<string>(_ => { }), handler }, null))
                using (var webhooks = (IDisposable)Activator.CreateInstance(sender, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { settings, http, new Action<string>(_ => { }) }, null))
                {
                    var expected = new List<string>();
                    foreach (string[] test in cases)
                    {
                        string targetName = test[4] == "Public creature" ? test[4] : "MONO_PRIVATE_TARGET";
                        object target = test[2] == "<null>" ? null : actor(targetName, test[2], test[3] == "yes");
                        string otherField = test[4] == "Public creature" ? test[4] : "MONO_PRIVATE_ATTACKER";
                        var fields = new Dictionary<string, string> { ["cause"] = test[1], ["attacker"] = otherField,
                            ["boss"] = otherField, ["message"] = "MONO_PRIVATE_MESSAGE", ["player_name"] = "MONO_PRIVATE_PLAYER" };
                        object value = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, new object[] { Guid.NewGuid().ToString("N"), DateTime.UtcNow, "MONO_PRIVATE_SERVER", test[0], "authoritative", player, target, fields }, null);
                        object[] formatArgs = { value, "Guest 1", test[4], language, null };
                        Check((bool)format.Invoke(null, formatArgs), "Actual Mono selects the expected anonymous story template");
                        expected.Add((string)formatArgs[4]);
                        Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { value }), "Actual Mono accepts target projection case " + string.Join("/", test));
                        Check(fields["attacker"] == otherField && fields["cause"] == test[1] &&
                            (target == null || actorType.GetProperty("Name").GetValue(target, null).ToString() == targetName),
                            "Anonymous target projection never mutates factual event fields or actor names");
                    }
                    Task running = (Task)sender.GetMethod("RunAsync").Invoke(webhooks, new object[] { CancellationToken.None });
                    await ((Task)sender.GetMethod("StopAsync").Invoke(webhooks, new object[] { TimeSpan.FromSeconds(3) })).ConfigureAwait(false);
                    await running.ConfigureAwait(false);
                    Check(handler.Calls == cases.Count, "Actual Mono dispatches every target projection fixture once");
                    for (int index = 0; index < cases.Count; ++index)
                    {
                        string body = handler.Bodies[index];
                        object payload = parseJson.Invoke(null, new object[] { body });
                        object card = ((IEnumerable)item.Invoke(payload, new object[] { "embeds" })).Cast<object>().Single();
                        Check(item.Invoke(card, new object[] { "title" }).ToString() == expected[index] &&
                            item.Invoke(card, new object[] { "description" }) == null,
                            "Anonymous story uses only the intended typed NPC name or player alias: " + string.Join("/", cases[index]));
                        Check(!body.Contains("MONO_PRIVATE") && !body.Contains("steamworks:") && !body.Contains("7656119800000000"),
                            "Player signals and unknown/environmental causes never leak raw identities or free-form attacker fields");
                    }
                }
            }
        }

        private static async Task TestBundledOperatorSummariesAsync(Assembly plugin)
        {
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type sender = plugin.GetType("ServerManager.Discord.DiscordWebhooks", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            string[] kinds = { "security.detection", "security.response", "security.admin_bypass", "character.save_rejected",
                "character.shadow_stalled", "character.validation_observed", "character.revision_observed", "connection.rejected" };
            string[] filters = { "security.alert", "security.admin_bypass", "character.validation",
                "character.shadow_stalled", "character.revision_observed", "connection.rejected" };
            {
                string yaml = "webhooks:\n  - name: operator-offline\n    enabled: true\n" +
                    "    url: https://discord.com/api/webhooks/123/operator-offline-token\n    events: [" + string.Join(",", filters) + "]\n";
                object settings = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { yaml });
                using (var handler = new OfflineHandler())
                using (var http = (IDisposable)Activator.CreateInstance(transport, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { "", new Action<string>(_ => { }), handler }, null))
                using (var webhooks = (IDisposable)Activator.CreateInstance(sender, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { settings, http, new Action<string>(_ => { }) }, null))
                {
                    foreach (string kind in kinds)
                    {
                        var fields = new Dictionary<string, string>
                        {
                            ["message"] = "OPERATORRAWPRIVATE", ["detail"] = "OPERATORRAWPRIVATE", ["finding"] = "OPERATORRAWPRIVATE",
                            ["session"] = "OPERATORRAWPRIVATE", ["ip"] = "OPERATORRAWPRIVATE", ["raw_payload"] = "OPERATORRAWPRIVATE",
                            ["coordinates"] = "OPERATORRAWPRIVATE", ["inventory"] = "OPERATORRAWPRIVATE",
                            ["expected_sha256"] = new string('a', 64), ["reported_sha256"] = new string('b', 64),
                            ["account_id"] = "steamworks:76561198000000001", ["character"] = "Mono operator fixture",
                            ["identity_status"] = "authenticated", ["source"] = "server_observed", ["outcome"] = "observed",
                            ["reason_code"] = "validation.hash_not_allowed", ["evidence"] = "MaximumHealth", ["response"] = "Log",
                            ["category"] = "mod_policy", ["stage"] = "ManifestValidation", ["revision"] = "2",
                            ["plugin_details"] = "OPERATORPLUGINSUMMARY"
                        };
                        object value = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, new object[] { Guid.NewGuid().ToString("N"), DateTime.UtcNow, "mono-probe", kind, "observed", null, null, fields }, null);
                        Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { value }), "Actual Mono accepts opted-in operator kind " + kind);
                    }
                    Task running = (Task)sender.GetMethod("RunAsync").Invoke(webhooks, new object[] { CancellationToken.None });
                    await ((Task)sender.GetMethod("StopAsync").Invoke(webhooks, new object[] { TimeSpan.FromSeconds(2) })).ConfigureAwait(false);
                    await running.ConfigureAwait(false);
                    Check(handler.Calls == 8 && handler.Bodies.All(body => !body.Contains("OPERATORRAWPRIVATE") &&
                        !body.Contains(new string('a', 64)) && !body.Contains(new string('b', 64))),
                        "Actual Mono operator projection always excludes raw diagnostics, SHA values, coordinates and inventory");
                    Check(handler.Bodies.Count(body => body.Contains("OPERATORPLUGINSUMMARY")) == 1,
                        "Actual Mono includes the connection plugin summary without an additional privacy setting");
                }
            }
            // Selectors group delivery only: original event kinds still control
            // audit meaning, rendering and source-specific summaries.
            MethodInfo parse = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo filterFor = settingsType.GetMethod("GetWebhookEventFilter", BindingFlags.Static | BindingFlags.NonPublic);
            var selectable = (HashSet<string>)settingsType.GetField("PublicEvents", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Check(selectable.Count == 17, "Actual Mono exposes the 17 grouped webhook selectors");
            const string securityUrl = "https://discord.com/api/webhooks/123/grouped-security-offline";
            const string validationUrl = "https://discord.com/api/webhooks/456/grouped-validation-offline";
            const string serverUrl = "https://discord.com/api/webhooks/789/grouped-server-offline";
            const string connectionUrl = "https://discord.com/api/webhooks/987/grouped-connection-offline";
            const string raidUrl = "https://discord.com/api/webhooks/654/grouped-raid-offline";
            string groupedYaml = "webhooks:\n  - name: security\n    url: " + securityUrl + "\n    events: [security.alert]\n" +
                "  - name: validation\n    url: " + validationUrl + "\n    events: [character.validation]\n" +
                "  - name: server\n    url: " + serverUrl + "\n    events: [server.status]\n" +
                "  - name: connection\n    url: " + connectionUrl + "\n    events: [player.connection]\n" +
                "  - name: raid\n    url: " + raidUrl + "\n    events: [raid.status]\n";
            foreach (string old in new[] { "server.ready", "server.shutdown", "player.login", "player.leave", "raid.started", "raid.ended",
                "combat.pvp_kill", "security.detection", "security.response",
                "character.save_rejected", "character.validation_observed" })
            {
                bool rejected = false;
                try { parse.Invoke(null, new object[] { groupedYaml.Replace("[security.alert]", "[" + old + "]") }); }
                catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
                Check(rejected && !selectable.Contains(old), "Actual Mono rejects the retired raw-kind selector " + old);
                rejected = false;
                try { parse.Invoke(null, new object[] { groupedYaml.Replace("  - name: security\n", "  - name: security\n    enabled: false\n")
                    .Replace("[security.alert]", "[" + old + "]") }); }
                catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
                Check(rejected, "Actual Mono rejects retired selectors even on disabled routes " + old);
            }
            Check((string)filterFor.Invoke(null, new object[] { "combat.pvp_kill" }) == "player.death",
                "Actual Mono routes the preserved PvP source kind through player.death");
            object groupedSettings = parse.Invoke(null, new object[] { groupedYaml });
            using (var handler = new OfflineHandler())
            using (var http = (IDisposable)Activator.CreateInstance(transport, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { "", new Action<string>(_ => { }), handler }, null))
            using (var webhooks = (IDisposable)Activator.CreateInstance(sender, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { groupedSettings, http, new Action<string>(_ => { }) }, null))
            {
                string[] sources = { "security.detection", "security.response", "character.save_rejected", "character.validation_observed",
                    "server.ready", "server.shutdown", "player.login", "player.leave", "raid.started", "raid.ended" };
                string[] groupedFilters = { "security.alert", "character.validation", "server.status", "player.connection", "raid.status" };
                for (int index = 0; index < sources.Length; ++index)
                {
                    object value = ReloadEvent(eventType, "grouped-mono-" + index, sources[index], "private unstructured detail");
                    if (sources[index].StartsWith("raid.", StringComparison.Ordinal))
                        value = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, new object[] { "grouped-mono-" + index, DateTime.UtcNow, "mono-probe", sources[index], "authoritative", null, null,
                                new Dictionary<string, string> { ["raid"] = "Forest", ["raid_coordinates"] = "-371, 40, 1591" } }, null);
                    Check((string)filterFor.Invoke(null, new object[] { sources[index] }) == groupedFilters[index / 2],
                        "Actual Mono maps each source to its expected grouped filter");
                    Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { value }) &&
                        (string)eventType.GetProperty("Kind").GetValue(value, null) == sources[index],
                        "Grouped routing preserves the original source event " + sources[index]);
                }
                foreach (string synthetic in new[] { "server.status", "player.connection", "raid.status", "security.alert", "character.validation", "cron.executed", "unknown.event" })
                    Check(filterFor.Invoke(null, new object[] { synthetic }) == null &&
                        !(bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { ReloadEvent(eventType, Guid.NewGuid().ToString("N"), synthetic, "not a source") }),
                        "Selectors are not synthetic source events and unknown sources cannot dispatch");
                Task running = (Task)sender.GetMethod("RunAsync").Invoke(webhooks, new object[] { CancellationToken.None });
                await ((Task)sender.GetMethod("StopAsync").Invoke(webhooks, new object[] { TimeSpan.FromSeconds(3) })).ConfigureAwait(false);
                await running.ConfigureAwait(false);
                Check(handler.Calls == 10 && new[] { securityUrl, validationUrl, serverUrl, connectionUrl, raidUrl }
                    .All(destination => handler.Urls.Count(url => url == destination + "?wait=true") == 2),
                    "Each grouped route delivers only its two distinct source events, once each");
                foreach (string title in new[] { "Security observation", "Security response", "Character save rejected", "Character validation notice",
                    "Server ready", "Server shutdown", "Unknown player joined", "Unknown player left", "Raid started", "Raid ended" })
                    Check(handler.Bodies.Count(body => body.Contains(title)) == 1,
                        "Grouped delivery retains the source-specific formatter: " + title);
                Check(handler.Bodies.Where((body, index) => handler.Urls[index] == raidUrl + "?wait=true")
                    .All(body => body.Contains("Forest") && body.Contains("-371, 40, 1591") && !body.Contains("\"description\"")),
                    "Both members of raid.status retain the raid name and validated center in the title only");
            }
        }

        private static async Task TestBundledRaidCoordinatesAsync(Assembly plugin)
        {
            Type settingsType = plugin.GetType("ServerManager.Discord.DiscordSettings", true);
            Type sender = plugin.GetType("ServerManager.Discord.DiscordWebhooks", true);
            Type transport = plugin.GetType("ServerManager.Discord.DiscordHttp", true);
            Type eventType = plugin.GetType("ServerManager.Events.ServerManagerEvent", true);
            Type jsonType = plugin.GetType("Newtonsoft.Json.Linq.JObject", true);
            MethodInfo parseJson = jsonType.GetMethod("Parse", new[] { typeof(string) });
            MethodInfo item = jsonType.GetMethod("get_Item", new[] { typeof(string) });
            string yaml = "webhooks:\n  - name: raid-offline\n    enabled: true\n" +
                "    url: https://discord.com/api/webhooks/123/raid-offline-token\n" +
                "    events: [raid.status, server.status, chat.shout]\n";
            object settings = settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { yaml });
            // kind, reliability, coordinates, exact field key, expected visibility.
            var cases = new List<string[]>();
            foreach (string kind in new[] { "raid.started", "raid.ended" })
            {
                foreach (string coordinates in new[] { "-371, 40, 1591", "0, 0, 0",
                    "-2147483648, 2147483647, -2147483648" })
                    cases.Add(new[] { kind, "authoritative", coordinates, "raid_coordinates", "visible" });
                foreach (string coordinates in new[] { "NaN, 2, 3", "Infinity, 2, 3", "-Infinity, 2, 3",
                    "2147483648, 2, 3", "-2147483649, 2, 3", "1.0, 2, 3", "1,5, 2, 3", "1e2, 2, 3",
                    "1, 2", "1, 2, 3, 4", "1; 2; 3", "1,2,3", "+1, 2, 3", "01, 2, 3", "-0, 2, 3",
                    " 1, 2, 3", "1, 2, 3 ", "1,  2, 3", "[1, 2, 3]", "1, 2, 3x" })
                    cases.Add(new[] { kind, "authoritative", coordinates, "raid_coordinates", "hidden" });
                foreach (string reliability in new[] { "observed", "client_reported", "Authoritative", "" })
                    cases.Add(new[] { kind, reliability, "-371, 40, 1591", "raid_coordinates", "hidden" });
                cases.Add(new[] { kind, "authoritative", "-371, 40, 1591", "Raid_Coordinates", "hidden" });
            }
            foreach (string kind in new[] { "server.ready", "chat.shout" })
                cases.Add(new[] { kind, "authoritative", "-371, 40, 1591", "raid_coordinates", "hidden" });
            var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
                foreach (bool anonymous in new[] { false, true })
                foreach (string[] test in cases)
                {
                    object routeSettings = anonymous ? settingsType.GetMethod("ParseForReload", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { yaml + "    anonymous_prefix: Guest\n" }) : settings;
                    string label = test[0] + "/" + test[1] + "/" + test[3] + "=" + test[2] + "/anonymous=" + anonymous;
                    using (var handler = new OfflineHandler())
                    using (var http = (IDisposable)Activator.CreateInstance(transport, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new object[] { "", new Action<string>(_ => { }), handler }, null))
                    using (var webhooks = (IDisposable)Activator.CreateInstance(sender, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new object[] { routeSettings, http, new Action<string>(_ => { }) }, null))
                    {
                        var fields = new Dictionary<string, string>
                        {
                            [test[3]] = test[2],
                            ["raid"] = "Forest",
                            ["text"] = "safe public chat",
                            ["message"] = "Raid fixture at [" + test[2] + "]. 987, 654, 321 MONORAIDINVENTORY MONORAIDCREDENTIAL",
                            ["player_position"] = "987, 654, 321",
                            ["inventory_items"] = "MONORAIDINVENTORY", ["bot_token"] = "MONORAIDCREDENTIAL"
                        };
                        object value = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, new object[] { Guid.NewGuid().ToString("N"), DateTime.UtcNow, "mono-probe", test[0], test[1], null, null, fields }, null);
                        Check((bool)sender.GetMethod("Enqueue").Invoke(webhooks, new[] { value }),
                            "Actual Mono routes selected raid-coordinate case " + label);
                        Task running = (Task)sender.GetMethod("RunAsync").Invoke(webhooks, new object[] { CancellationToken.None });
                        await ((Task)sender.GetMethod("StopAsync").Invoke(webhooks, new object[] { TimeSpan.FromSeconds(2) })).ConfigureAwait(false);
                        await running.ConfigureAwait(false);
                        Check(handler.Calls == 1, "Actual Mono sends raid-coordinate case once " + label);
                        string body = handler.Bodies.Single().Replace("\\", "");
                        if (test[0] == "raid.started" || test[0] == "raid.ended")
                        {
                            object payload = parseJson.Invoke(null, new object[] { handler.Bodies.Single() });
                            object card = ((IEnumerable)item.Invoke(payload, new object[] { "embeds" })).Cast<object>().Single();
                            string expectedTitle = (test[0] == "raid.started" ? "Raid started" : "Raid ended") + " — Forest" +
                                (test[4] == "visible" ? " \\[" + test[2] + "\\]" : "");
                            Check(item.Invoke(card, new object[] { "title" }).ToString() == expectedTitle && item.Invoke(card, new object[] { "description" }) == null,
                                "Raid title retains its name and only validated coordinates without a second body on either route " + label);
                        }
                        if (test[4] == "visible")
                        {
                            int first = body.IndexOf(test[2], StringComparison.Ordinal);
                            Check(first >= 0 && body.IndexOf(test[2], first + test[2].Length, StringComparison.Ordinal) < 0
                                && !body.Contains("raid_coordinates") && !body.Contains("\"fields\""),
                                "Actual Mono exposes canonical authoritative raid center only once, in the same concise title " + label);
                        }
                        else
                            Check(!body.Contains(test[2]) && !body.Contains(test[3]) && (test[0] != "chat.shout" || anonymous || body.Contains("[redacted]")),
                                "Actual Mono excludes invalid center fields and never copies arbitrary raid messages " + label);
                        Check(!body.Contains("987, 654, 321") && !body.Contains("MONORAIDINVENTORY")
                            && !body.Contains("MONORAIDCREDENTIAL") && !body.Contains("player_position")
                            && !body.Contains("inventory_items") && !body.Contains("bot_token"),
                            "Actual Mono raid exception preserves other coordinate, inventory and credential exclusions " + label);
                    }
                }
                foreach (string kind in new[] { "raid.started", "raid.ended" })
                {
                    // Long external translation overrides can exceed Discord's
                    // title cap even though the raid-name field remains bounded.
                    string title = new string('*', 140) + " — Forest [-371, 40, 1591]";
                    object value = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new object[] { Guid.NewGuid().ToString("N"), DateTime.UtcNow, "mono-probe", kind, "authoritative", null, null,
                            new Dictionary<string, string> { ["raid"] = "Forest", ["raid_coordinates"] = "-371, 40, 1591" } }, null);
                    object card = sender.GetMethod("CompactEmbed", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { value, title, "" });
                    string expectedBody = string.Concat(Enumerable.Repeat("\\*", 140)) + " — Forest \\[-371, 40, 1591\\]";
                    Check(item.Invoke(card, new object[] { "title" }) == null && item.Invoke(card, new object[] { "description" }).ToString() == expectedBody,
                        "Actual Mono moves a long escaped raid line intact into its body with no duplicate title or lost coordinates");
                }
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = originalCulture; }
        }

        private static object ReloadEvent(Type type, string id, string kind, string message)
        {
            return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { id, DateTime.UtcNow, "mono-probe", kind, "authoritative", null, null,
                    new Dictionary<string, string> { ["message"] = message, ["coordinates"] = "987,654,321",
                        ["inventory"] = "MONOPRIVATEINVENTORY", ["plugins"] = "MONOPLUGINMETADATA" } }, null);
        }

        private static async Task TestHttpLoopbackAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            using (var stop = new CancellationTokenSource(7000))
            using (var handler = new HttpClientHandler { UseProxy = false })
            using (var client = new HttpClient(handler))
            {
                Task server = Task.Run(async () =>
                {
                    using (TcpClient peer = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                    using (NetworkStream stream = peer.GetStream())
                    {
                        await ReadHeadersAsync(stream, stop.Token).ConfigureAwait(false);
                        byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                        await stream.WriteAsync(reply, 0, reply.Length, stop.Token).ConfigureAwait(false);
                    }
                });
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    using (HttpResponseMessage response = await client.GetAsync("http://127.0.0.1:" + port + "/probe", stop.Token).ConfigureAwait(false))
                        Check(await response.Content.ReadAsStringAsync().ConfigureAwait(false) == "{}", "Mono HttpClient loopback request/response");
                    await server.ConfigureAwait(false);
                }
                finally { listener.Stop(); }
            }
        }

        private static async Task TestWebSocketLoopbackAsync(Type gateway)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            using (var stop = new CancellationTokenSource(7000))
            using (var socket = new ClientWebSocket())
            {
                socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
                socket.Options.Proxy = null;
                Task server = Task.Run(async () =>
                {
                    using (TcpClient peer = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                    using (NetworkStream stream = peer.GetStream())
                    {
                        string headers = await ReadHeadersAsync(stream, stop.Token).ConfigureAwait(false);
                        string key = headers.Split(new[] { "\r\n" }, StringSplitOptions.None)
                            .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim();
                        string accept;
                        using (SHA1 sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        byte[] handshake = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                        await stream.WriteAsync(handshake, 0, handshake.Length, stop.Token).ConfigureAwait(false);
                        byte[] header = await ReadBytesAsync(stream, 2, stop.Token).ConfigureAwait(false);
                        Check((header[0] & 15) == 1 && (header[1] & 128) != 0 && (header[1] & 127) < 126, "Mono WebSocket sends masked text");
                        byte[] mask = await ReadBytesAsync(stream, 4, stop.Token).ConfigureAwait(false);
                        byte[] sent = await ReadBytesAsync(stream, header[1] & 127, stop.Token).ConfigureAwait(false);
                        for (int i = 0; i < sent.Length; i++) sent[i] ^= mask[i % 4];
                        Check(Encoding.UTF8.GetString(sent) == "local-probe", "Mono WebSocket outbound payload");
                        byte[] payload = Encoding.UTF8.GetBytes("{\"op\":11,\"d\":null,\"probe\":\"한글\"}");
                        int split = Array.IndexOf(payload, (byte)0xed) + 1; // Split inside the Korean UTF-8 character.
                        await WriteFrameAsync(stream, 0x01, payload.Take(split).ToArray(), stop.Token).ConfigureAwait(false);
                        await WriteFrameAsync(stream, 0x80, payload.Skip(split).ToArray(), stop.Token).ConfigureAwait(false);
                        await Task.Delay(100, stop.Token).ConfigureAwait(false);
                    }
                });
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/probe"), stop.Token).ConfigureAwait(false);
                    byte[] payload = Encoding.UTF8.GetBytes("local-probe");
                    await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, stop.Token).ConfigureAwait(false);
                    Task read = (Task)gateway.GetMethod("ReceiveMessageAsync", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { socket, stop.Token });
                    await read.ConfigureAwait(false);
                    object result = read.GetType().GetProperty("Result").GetValue(read, null);
                    object message = result.GetType().GetProperty("Payload", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result, null);
                    Check(message.ToString().Contains("한글"), "Bundled Gateway receives fragmented UTF-8 via Mono ClientWebSocket");
                    await server.ConfigureAwait(false);
                }
                finally { socket.Abort(); listener.Stop(); }
            }
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
        {
            var text = new StringBuilder();
            while (text.Length < 16384)
            {
                text.Append((char)(await ReadBytesAsync(stream, 1, ct).ConfigureAwait(false))[0]);
                if (text.Length >= 4 && text.ToString(text.Length - 4, 4) == "\r\n\r\n") return text.ToString();
            }
            throw new InvalidDataException("Loopback header limit exceeded.");
        }
        private static async Task<byte[]> ReadBytesAsync(NetworkStream stream, int count, CancellationToken ct)
        {
            var bytes = new byte[count];
            for (int offset = 0; offset < count;)
            {
                int read = await stream.ReadAsync(bytes, offset, count - offset, ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return bytes;
        }
        private static async Task WriteFrameAsync(NetworkStream stream, byte opcode, byte[] bytes, CancellationToken ct)
        {
            if (bytes.Length >= 126) throw new InvalidDataException("Probe payload must stay small.");
            byte[] header = { opcode, (byte)bytes.Length };
            await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
        }
        private static void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            Interlocked.Increment(ref _checks);
            Console.WriteLine("PASS: " + description);
        }
        private sealed class OfflineHandler : HttpMessageHandler
        {
            internal int Calls;
            internal bool GuildRegistration;
            internal string FailedGuild = "";
            internal readonly List<string> Urls = new List<string>();
            internal readonly List<string> Bodies = new List<string>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (Urls)
                {
                    Urls.Add(request.RequestUri.AbsoluteUri);
                    Bodies.Add(body);
                    ++Calls;
                }
                if (GuildRegistration && request.RequestUri.AbsolutePath.Contains("/commands"))
                {
                    string guild = request.RequestUri.AbsolutePath.Split(new[] { "/guilds/" }, StringSplitOptions.None)[1].Split('/')[0];
                    if (guild == FailedGuild)
                        return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Get
                        ? "[]" : "{\"id\":\"" + (guild == "111" ? "333" : "334") + "\"}") };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"probe\":true}") };
            }
        }
    }
}
