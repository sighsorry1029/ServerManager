// Source-links the production Discord adapter. Recording boundaries keep this
// fixture independent of the game, Discord network and live credentials.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ServerManager.Commands;
using ServerManager.Discord;
using ServerManager.Events;

internal static class DiscordCommonCommandsSmoke
{
    private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static int _checks;
    private static readonly string[] RetiredFlatNames = { "playerinfo", "skilladd", "skillreset" };
    private static readonly string[] RetiredHyphenNames =
    {
        "player-info", "admin-list", "admin-add", "admin-remove", "access-list", "access-add", "access-remove",
        "ban-list", "key-list", "key-add", "key-remove", "event-start", "event-stop", "character-list", "character-info",
        "import-status", "skill-get", "skill-set", "skill-add", "skill-reset", "mods-status", "mods-reload", "discord-status", "discord-test"
    };
    private static Dictionary<string, string> A(params string[] values)
    {
        var result = new Dictionary<string, string>();
        for (int i = 0; i < values.Length; i += 2) result.Add(values[i], values[i + 1]);
        return result;
    }
    private sealed class Example
    {
        internal Example(string name, string expected, params string[] values)
        { Name = name; Line = expected; Args = A(values); }
        internal readonly string Name, Line;
        internal readonly Dictionary<string, string> Args;
    }
    private static readonly Example[] Examples =
    {
        new("status", "status"), new("players", "players"),
        new("announce", "announce \"Hello all\"", "message", "Hello all"),
        new("chat", "chat \"TeLl everyone Hello\"", "message", "TeLl everyone Hello"),
        new("players", "players \"Player Name\"", "player", "Player Name"),
        new("banlist", "banlist"),
        new("adminlist", "adminlist"),
        new("adminadd", "adminadd \"76561198000000001\"", "steam-id", "76561198000000001"),
        new("adminremove", "adminremove \"76561198000000001\"", "steam-id", "76561198000000001"),
        new("accesslist", "accesslist"),
        new("accessadd", "accessadd \"76561198000000001\"", "steam-id", "76561198000000001"),
        new("accessremove", "accessremove \"76561198000000001\"", "steam-id", "76561198000000001"),
        new("keylist", "keylist"), new("keyadd", "keyadd \"defeated_eikthyr\"", "key", "defeated_eikthyr"),
        new("keyremove", "keyremove \"defeated_eikthyr\"", "key", "defeated_eikthyr"),
        new("eventstart", "eventstart \"army_eikthyr\" \"1\" \"2\" \"3\"", "event", "army_eikthyr", "x", "1", "y", "2", "z", "3"),
        new("eventstop", "eventstop"), new("characterlist", "characterlist"),
        new("characterinfo", "characterinfo \"Player Name\"", "player", "Player Name"),
        new("characterbackups", "characterbackups \"Player Name\" \"2\"", "player", "Player Name", "page", "2"),
        new("characterrestore", "characterrestore \"Player Name\" \"0123456789abcdef0123456789abcdef\"", "player", "Player Name", "backup_id", "0123456789abcdef0123456789abcdef"),
        new("giveitem", "giveitem \"Player Name\" \"Wood\" \"3\" \"2\"", "player", "Player Name", "prefab", "Wood", "amount", "3", "quality", "2"),
        new("teleport", "teleport \"Player Name\" \"1\" \"2\" \"3\"", "player", "Player Name", "x", "1", "y", "2", "z", "3"),
        new("skillget", "skillget \"Player Name\"", "player", "Player Name"),
        new("skillset", "skillset \"Player Name\" \"Run\" \"50\"", "player", "Player Name", "skill", "Run", "value", "50"),
        new("heal", "heal \"Player Name\" \"0.5\"", "player", "Player Name", "amount", "0.5"),
        new("damage", "damage \"Player Name\" \"25\"", "player", "Player Name", "amount", "25"),
        new("modsstatus", "modsstatus"), new("modsreload", "modsreload"),
        new("discordstatus", "discordstatus"), new("discordtest", "discordtest"),
        new("cronstatus", "cronstatus"), new("cronack", "cronack \"morning-maintenance\"", "job", "morning-maintenance"), new("help", "help")
    };
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("The built ServerManager DLL path is required.");
            ServerCommands.BindIdentifierPredicates(args[0]);
            FixedExecutionLimits(); AdapterArguments(); RegistrationAndAdmins(); MultiGuildIsolation(); SharedExecution(); TypedParsing();
            Reauthorization(); RestoreRejectionAudit(); ItemPresetArguments(); ItemPresetRejectionAudit(); RconRouting(); DeferredRevocationAndTimeout();
            Console.WriteLine("PASS: Discord flat command adapter (" + _checks + " assertions)."); return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static JObject Definition(string name) => (JObject)typeof(DiscordCommands).GetMethod("Definition", Static)!.Invoke(null, new object[] { name })!;
    private static string[] Names => (string[])typeof(DiscordCommands).GetField("Names", Static)!.GetValue(null)!;
    private static void FixedExecutionLimits()
    {
        foreach (var limit in new Dictionary<string, int> { ["RconMinimumIntervalSeconds"] = 1,
            ["MaximumRconOutputCharacters"] = 1800, ["CommandTimeoutSeconds"] = 15 })
        {
            Check(typeof(DiscordSettings).GetProperty(limit.Key) == null, "Removed execution-limit setting " + limit.Key);
            FieldInfo field = typeof(DiscordCommands).GetField(limit.Key, Static)!;
            Check(field.IsLiteral && (int)field.GetRawConstantValue()! == limit.Value, "Immutable execution limit " + limit.Key);
        }
        using var fixture = new Fixture();
        object pending = fixture.Parse("status", A());
        Check((long)Get(pending, "Deadline") - (long)Get(pending, "ReceivedAt") == 15L * Stopwatch.Frequency,
            "Every parsed command receives the fixed 15-second deadline");
    }
    private static void AdapterArguments()
    {
        Check(Names.Length == 34 && Names.Distinct().Count() == 34, "Exactly 34 unique flat commands");
        Check(Names.All(name => !name.Contains("-")), "Every public command name is hyphen-free");
        Check(new HashSet<string>(Names.Where(name => name != "rcon")).SetEquals(ServerCommands.FlatCommandNames),
            "Discord leaf names match the shared public catalog exactly");
        Check(DiscordCommands.ValidateArguments("cronack", A("job", new string('a', 64))) &&
            !DiscordCommands.ValidateArguments("cronack", A("job", new string('a', 65))) &&
            !DiscordCommands.ValidateArguments("cronack", A("job", "bad\njob")),
            "Cron acknowledgement IDs have a 64-character single-line boundary");
        foreach (Example example in Examples)
        {
            Check(DiscordCommands.ValidateArguments(example.Name, example.Args), "Valid typed arguments " + example.Name);
            Check(DiscordCommands.SharedLine(example.Name, example.Args) == example.Line, "Exact mapping " + example.Name);
            var extra = new Dictionary<string, string>(example.Args) { ["unexpected"] = "save" };
            Check(!DiscordCommands.ValidateArguments(example.Name, extra), "Unknown options rejected " + example.Name);
            JObject definition = Definition(example.Name);
            Check((string?)definition["name"] == example.Name && (int?)definition["type"] == 1, "Flat definition " + example.Name);
            Check(((JArray)definition["options"]!).All(option => new[] { 3, 4, 10 }.Contains((int)option["type"]!)), "No subcommand groups " + example.Name);
            foreach (JObject option in ((JArray)definition["options"]!).OfType<JObject>().Where(option => (bool)option["required"]!))
            {
                var missing = new Dictionary<string, string>(example.Args); missing.Remove((string)option["name"]!);
                Check(!DiscordCommands.ValidateArguments(example.Name, missing), "Missing required option " + example.Name + "/" + option["name"]);
            }
        }
        foreach (string obsolete in new[] { "sm", "save", "kick", "ban", "unban", "tell", "item give", "skill set", "ban list", "sm:status" })
            Check(!Names.Contains(obsolete) && !DiscordCommands.ValidateArguments(obsolete, A()), "No retired slash " + obsolete);
        foreach (string obsolete in RetiredHyphenNames)
            Check(!Names.Contains(obsolete) && !DiscordCommands.ValidateArguments(obsolete, A()), "No legacy hyphenated slash alias " + obsolete);
        foreach (string obsolete in RetiredFlatNames)
        {
            Check(!Names.Contains(obsolete) && !DiscordCommands.ValidateArguments(obsolete, A("player", "P")),
                "Removed flat command cannot validate " + obsolete);
            bool rejected = false;
            try { _ = DiscordCommands.SharedLine(obsolete, A("player", "P")); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Removed flat command has no shared-line alias " + obsolete);
        }
        Check(DiscordCommands.SharedLine("announce", A("message", "A \"quoted\" \\ notice; ban 123")) ==
            "announce \"A \\\"quoted\\\" \\\\ notice; ban 123\"", "Quotes/backslashes/semicolons stay in one literal argument");
        Check(DiscordCommands.SharedLine("teleport", A("player", "A B", "to", "C \"D\"")) ==
            "teleport \"A B\" to \"C \\\"D\\\"\"", "Target teleport uses the shared to keyword");
        Check(DiscordCommands.SharedLine("giveitem", A("player", "Halla-One", "prefab", "custom-item", "amount", "2")) ==
            "giveitem \"Halla-One\" \"custom-item\" \"2\"", "Removing command-name hyphens must not change player or prefab arguments");
        Check(DiscordCommands.SharedLine("keyadd", A("key", "custom-key")) == "keyadd \"custom-key\"", "Hyphenated key argument remains literal");
        Check(DiscordCommands.SharedLine("teleport", A("player", "Halla-One", "x", "-5", "y", "0", "z", "1")) ==
            "teleport \"Halla-One\" \"-5\" \"0\" \"1\"", "Negative numeric arguments preserve their minus sign");
        Check(DiscordCommands.SharedLine("players", A("player", "76561198000000001/A \"B\"")) ==
            "players \"76561198000000001/A \\\"B\\\"\"", "Optional player selector stays one literal shared argument");
        foreach (string value in new[] { "", " ", new string('p', 129), "P\nsave" })
            Check(!DiscordCommands.ValidateArguments("players", A("player", value)), "Invalid optional player selector rejected");
        Check(!DiscordCommands.ValidateArguments("players", A("target", "P")), "Players accepts player, not an alternate target option");
        Check(!DiscordCommands.ValidateArguments("teleport", A("player", "A", "--to", "B")), "Legacy teleport flag rejected");
        Check(!DiscordCommands.ValidateArguments("teleport", A("player", "A", "to", "B", "x", "1", "y", "2", "z", "3")), "Teleport target/coordinates mutually exclusive");
        Check(!DiscordCommands.ValidateArguments("teleport", A("player", "A", "x", "1", "y", "2")), "Partial coordinates rejected");
        Check(!DiscordCommands.ValidateArguments("teleport", A("player", "A")), "Teleport requires destination");
        Check(DiscordCommands.SharedLine("giveitem", A("player", "P", "prefab", "Wood", "amount", "1")) == "giveitem \"P\" \"Wood\" \"1\"", "Optional item quality");
        Check(DiscordCommands.SharedLine("skillget", A("player", "P", "skill", "Run")) == "skillget \"P\" \"Run\"", "Optional skill name");
        Check(DiscordCommands.SharedLine("characterbackups", A("player", "P")) == "characterbackups \"P\"", "Backup page defaults in shared runtime");
        foreach (string value in new[] { "1", "1000" })
            Check(DiscordCommands.ValidateArguments("characterbackups", A("player", "P", "page", value)), "Valid backup page " + value);
        foreach (string value in new[] { "0", "1001", "-1", "+1", "1.5", "NaN", "1e2", " 1", "1\n" })
            Check(!DiscordCommands.ValidateArguments("characterbackups", A("player", "P", "page", value)), "Invalid backup page " + value);
        foreach (string value in new[] { "0123456789ABCDEF0123456789ABCDEF", "01234567-89ab-cdef-0123-456789abcdef",
            "../backup", "C:\\backup.fch", new string('a', 31), new string('a', 33), new string('ａ', 32),
            "0123456789abcdef0123456789abcdeg", "0123456789abcdef0123456789abcdef\n" })
            Check(!DiscordCommands.ValidateArguments("characterrestore", A("player", "P", "backup_id", value)), "Invalid backup ID " + value);
        foreach (string value in new[] { "0", "1001", "1.5", "NaN", "-1" })
            Check(!DiscordCommands.ValidateArguments("giveitem", A("player", "P", "prefab", "Wood", "amount", value)), "Invalid quantity " + value);
        foreach (string value in new[] { "NaN", "Infinity", "-Infinity", "1000001", "1,000", "1\n2" })
            Check(!DiscordCommands.ValidateArguments("eventstart", A("event", "event", "x", value, "y", "0", "z", "0")), "Invalid coordinate " + value);
        Check(!DiscordCommands.ValidateArguments("heal", A("player", "P", "amount", "0")), "Heal lower bound");
        Check(!DiscordCommands.ValidateArguments("skillset", A("player", "P", "skill", "Run", "value", "-1")), "Skill lower bound");
        Check(!DiscordCommands.ValidateArguments("adminadd", A("steam-id", "Player Name")), "Steam64 required for admin list");
        Check(!DiscordCommands.ValidateArguments("keyadd", A("key", "x\";save")), "Token option cannot inject grammar");
        Check(!DiscordCommands.ValidateArguments("announce", A("message", "hello\nsave")), "Controls rejected");
        foreach (string line in new[] { "sm", "SM status", "  sm help", "\"sm\" status", "'sm' status",
            "sm:", "sm:status", "SM:giveitem P Wood 1", "  sm:banlist", "\"sm:status\"", "'sm:teleport' P to Q" })
            Check(!DiscordCommands.ValidateArguments("rcon", A("command", line)), "No ServerManager RCON prefix " + line);
        Check(DiscordCommands.ValidateArguments("rcon", A("command", new string('a', 1000))), "RCON bound accepted");
        Check(!DiscordCommands.ValidateArguments("rcon", A("command", new string('a', 1001))), "RCON overlong rejected");
        Check(!DiscordCommands.ValidateArguments("rcon", A("command", "save\nkick P")), "RCON multiple lines rejected");
    }
    private static void RegistrationAndAdmins()
    {
        using var fixture = new Fixture();
        foreach (string name in Names)
        {
            Check(fixture.Commands.IsAuthorized(name, "111", "222", "444"), "Global admin includes " + name);
            Check(!fixture.Commands.IsAuthorized(name, "111", "222", "999"), "Non-admin denied " + name);
        }
        Check(!fixture.Commands.IsAuthorized("rcon", "999", "222", "444"), "Foreign guild denied");
        Check(!fixture.Commands.IsAuthorized("rcon", "111", "999", "444"), "Foreign channel denied");
        Check(typeof(DiscordCommands).GetNestedType("Pending", BindingFlags.NonPublic)!.GetField("Roles", Instance) == null, "No role IDs retained");
        JObject existingStatus = Definition("status"); existingStatus["id"] = "888"; fixture.Http.Existing.Add(existingStatus);
        JObject oldGiveItem = Definition("giveitem"); oldGiveItem["id"] = "887";
        ((JArray)oldGiveItem["options"]!).Last!.Remove(); fixture.Http.Existing.Add(oldGiveItem);
        JObject oldPlayers = Definition("players"); oldPlayers["id"] = "886";
        oldPlayers["options"] = new JArray(); fixture.Http.Existing.Add(oldPlayers);
        int old = 900;
        string[] retiredNames = new[] { "sm", "save", "kick", "ban", "unban" }.Concat(RetiredHyphenNames).Concat(RetiredFlatNames).ToArray();
        var retiredIds = new HashSet<string>();
        foreach (string name in retiredNames)
        {
            string id = (++old).ToString(); retiredIds.Add(id);
            fixture.Http.Existing.Add(new JObject { ["id"] = id, ["type"] = 1, ["name"] = name });
            Check(!fixture.Commands.IsAuthorized(name, "111", "222", "444"), "Administrator cannot invoke a retired slash name " + name);
        }
        fixture.Http.Existing.Add(new JObject { ["id"] = "999", ["type"] = 1, ["name"] = "unrelated" });
        fixture.Http.Existing.Add(new JObject { ["id"] = "998", ["type"] = 2, ["name"] = "sm" });
        fixture.Http.Existing.Add(new JObject { ["id"] = "997", ["type"] = 2, ["name"] = "player-info" });
        fixture.Http.Existing.Add(new JObject { ["id"] = "996", ["type"] = 1, ["name"] = "player-info-extra" });
        fixture.Http.Existing.Add(new JObject { ["id"] = "995", ["type"] = 1, ["name"] = "other-plugin" });
        fixture.Http.Existing.Add(new JObject { ["id"] = "994", ["type"] = 2, ["name"] = "playerinfo" });
        fixture.Http.Existing.Add(new JObject { ["id"] = "993", ["type"] = 1, ["name"] = "skilladd-extra" });
        fixture.Commands.HandleDispatchAsync("READY", JObject.Parse("{\"application\":{\"id\":\"777\"}}")).Wait();
        Wait(() => (int)Get(fixture.Commands, "_registering") == 0 && fixture.Http.Definitions.Count == Names.Length - 1, "Flat commands reconciled");
        Check(fixture.Http.Deleted.Count == retiredIds.Count && retiredIds.SetEquals(fixture.Http.Deleted), "Only exact retired slash records are deleted; unrelated names and same-name context menus survive");
        Check(fixture.Http.Definitions.All(item => !retiredNames.Contains((string)item["name"]!)), "Retired records not re-registered");
        Check(fixture.Http.Definitions.Any(item => (string?)item["name"] == "rcon") && fixture.Ids["111:status"] == "888", "RCON registered and unchanged current record reused");
        Check(fixture.Http.RegistrationRequests.Any(request => request.StartsWith("PATCH ", StringComparison.Ordinal) &&
            request.EndsWith("/applications/777/guilds/111/commands/887", StringComparison.Ordinal)) &&
            ((JArray)fixture.Http.Definitions.Single(item => (string?)item["name"] == "giveitem")["options"]!).Count == 5,
            "Existing giveitem registration gains data_id through its scoped update");
        Check(fixture.Http.RegistrationRequests.Any(request => request.StartsWith("PATCH ", StringComparison.Ordinal) &&
            request.EndsWith("/applications/777/guilds/111/commands/886", StringComparison.Ordinal)) &&
            ((JArray)fixture.Http.Definitions.Single(item => (string?)item["name"] == "players")["options"]!).Count == 1,
            "Existing players registration gains its optional selector through a scoped update");
        Check(!fixture.Http.BulkOverwrite, "Unrelated commands never bulk-overwritten");
        int calls = ServerCommands.Calls.Count;
        fixture.Commands.HandleDispatchAsync("MESSAGE_CREATE", JObject.Parse("{\"content\":\"save\"}")).Wait();
        Check(ServerCommands.Calls.Count == calls, "Channel text not interpreted as admin commands");
    }
    private static void MultiGuildIsolation()
    {
        using (var fixture = new Fixture(multipleGuilds: true))
        {
            fixture.Ids.Clear();
            JObject first = Definition("status"); first["id"] = "888";
            JObject second = Definition("status"); second["id"] = "889";
            fixture.Http.ExistingByGuild["111"] = new JArray(first);
            fixture.Http.ExistingByGuild["112"] = new JArray(second);
            fixture.Commands.HandleDispatchAsync("READY", JObject.Parse("{\"application\":{\"id\":\"777\"}}")).Wait();
            Wait(() => (int)Get(fixture.Commands, "_registering") == 0 && fixture.Ids.Count == 2 * Names.Length,
                "Both guild catalogs finish registration independently");
            Check(fixture.Ids["111:status"] == "888" && fixture.Ids["112:status"] == "889" &&
                fixture.Http.Definitions.Count == 2 * (Names.Length - 1), "Guild-specific verified IDs cannot overwrite each other");
            Check(fixture.Http.RegistrationRequests.Count(value => value.StartsWith("GET ")) == 2 &&
                fixture.Http.RegistrationRequests.Any(value => value.Contains("/guilds/111/commands")) &&
                fixture.Http.RegistrationRequests.Any(value => value.Contains("/guilds/112/commands")),
                "Registration addresses both guild endpoints without a global command overwrite");
            foreach (string name in Names)
                Check(fixture.Commands.IsAuthorized(name, "111", "222", "444") && fixture.Commands.IsAuthorized(name, "112", "222", "444") &&
                    !fixture.Commands.IsAuthorized(name, "999", "222", "444"), "Global administrator requires a selected guild for " + name);
            int calls = ServerCommands.Calls.Count;
            foreach (JObject rejected in new[] { fixture.Payload("status", A(), "112", "888"),
                fixture.Payload("status", A(), "111", "889"), fixture.Payload("status", A(), "999", "888") })
            {
                fixture.Commands.HandleDispatchAsync("INTERACTION_CREATE", rejected).Wait();
                Wait(() => (int)Get(fixture.Commands, "_inFlight") == 0, "Foreign guild or cross-guild command ID rejected");
                Check(((System.Collections.ICollection)Get(fixture.Commands, "_queue")).Count == 0 && ServerCommands.Calls.Count == calls,
                    "A foreign guild ID never enters the game command queue");
            }
            JObject accepted = fixture.Payload("status", A(), "112");
            fixture.Commands.HandleDispatchAsync("INTERACTION_CREATE", accepted).Wait();
            Wait(() => ((System.Collections.ICollection)Get(fixture.Commands, "_queue")).Count == 1, "Second guild's own verified command is deferred");
            fixture.Commands.Tick(); var call = ServerCommands.Calls.Last();
            Check(call.Caller.IsAuthorized(), "Second guild's own registration remains authorized at execution");
            call.Completion.SetResult(Result("ok"));
            Wait(() => (int)Get(fixture.Commands, "_inFlight") == 0, "Second guild request completed");
            int responses = fixture.Http.Responses.Count;
            JObject duplicate = (JObject)accepted.DeepClone(); duplicate["guild_id"] = "111"; duplicate["data"]!["id"] = "888";
            fixture.Commands.HandleDispatchAsync("INTERACTION_CREATE", duplicate).Wait();
            Check(fixture.Http.Responses.Count == responses && ServerCommands.Calls.Count == calls + 1,
                "Changing guild does not replay an already-seen interaction ID");
            ((Dictionary<string, long>)Get(fixture.Commands, "_userRate"))["444"] = Stopwatch.GetTimestamp();
            fixture.Commands.HandleDispatchAsync("INTERACTION_CREATE", fixture.Payload("status", A(), "111")).Wait();
            Wait(() => (int)Get(fixture.Commands, "_inFlight") == 0, "Cross-guild user-rate rejection completed");
            Check(((System.Collections.ICollection)Get(fixture.Commands, "_queue")).Count == 0 && ServerCommands.Calls.Count == calls + 1,
                "Switching guilds cannot bypass the shared user command rate");
        }
        foreach (string failedGuild in new[] { "111", "112" })
        {
            using var fixture = new Fixture(multipleGuilds: true); fixture.Ids.Clear(); fixture.Http.FailedGuild = failedGuild;
            fixture.Commands.HandleDispatchAsync("READY", JObject.Parse("{\"application\":{\"id\":\"777\"}}")).Wait();
            Wait(() => (int)Get(fixture.Commands, "_registering") == 0 && fixture.Http.RegistrationRequests.Count >= 2,
                "Registration failure retires only its own guild");
            string healthy = failedGuild == "111" ? "112" : "111";
            Check(fixture.Ids.Count == Names.Length && fixture.Ids.Keys.All(key => key.StartsWith(healthy + ":")) &&
                fixture.Http.Definitions.Count == Names.Length, "A failed guild neither blocks nor authorizes another guild's catalog");
        }
        using (var fixture = new Fixture(multipleGuilds: true))
        {
            fixture.Ids.Clear(); fixture.Http.BeforeRegistration = () => fixture.Commands.Dispose();
            fixture.Commands.HandleDispatchAsync("READY", JObject.Parse("{\"application\":{\"id\":\"777\"}}")).Wait();
            Wait(() => (int)Get(fixture.Commands, "_registering") == 0 && fixture.Http.RegistrationRequests.Count != 0,
                "Disposal during registration retires the outer worker");
            Check(fixture.Http.RegistrationRequests.Count == 1 && fixture.Http.RegistrationRequests[0].Contains("/guilds/111/") &&
                fixture.Http.Definitions.Count == 0 && fixture.Ids.Count == 0,
                "Cancellation stops the whole guild loop rather than treating cancellation as an isolated registration failure");
        }
        using (var fixture = new Fixture(multipleGuilds: true))
        {
            Task executing = fixture.Execute(fixture.Parse("status", A(), "112")); var removed = ServerCommands.Calls.Last();
            fixture.Settings.GuildIds.Remove("112");
            Check(!removed.Caller.IsAuthorized() && fixture.Commands.IsAuthorized("status", "111", "222", "444"),
                "Removing a guild revokes queued backend authority without revoking remaining guilds");
            removed.Completion.SetResult(Result("cancelled", false)); executing.Wait();
        }
        using (var fixture = new Fixture(multipleGuilds: true))
        {
            Task executing = fixture.Execute(fixture.Parse("rcon", A("command", "ban 76561198000000001"), "111"));
            var ban = ServerCommands.Calls.Last(); int captures = DiscordRconCapture.Executions;
            fixture.Execute(fixture.Parse("rcon", A("command", "save"), "112")).Wait();
            Check(DiscordRconCapture.Executions == captures && fixture.Audits.Last().Fields["result_code"] == "rcon_cooldown",
                "In-flight protected RCON excludes raw execution from every guild");
            ban.Completion.SetResult(Result("ok")); executing.Wait();
            fixture.Execute(fixture.Parse("rcon", A("command", "save"), "112")).Wait();
            Check(DiscordRconCapture.Executions == captures && fixture.Audits.Last().Fields["result_code"] == "rcon_cooldown",
                "RCON completion cooldown is shared across guilds");
            ((Dictionary<string, long>)Get(fixture.Commands, "_seen"))["9876"] = Stopwatch.GetTimestamp();
            using var replacement = new Fixture(); replacement.Commands.InheritRecentState(fixture.Commands);
            Check(!replacement.Commands.IsAuthorized("rcon", "112", "222", "444") &&
                replacement.Commands.IsAuthorized("rcon", "111", "222", "444") &&
                ((Dictionary<string, long>)Get(replacement.Commands, "_seen")).ContainsKey("9876"),
                "Reload removal applies its own guild authority while retaining cross-guild no-replay history");
            replacement.Execute(replacement.Parse("rcon", A("command", "save"))).Wait();
            Check(DiscordRconCapture.Executions == captures && replacement.Audits.Last().Fields["result_code"] == "rcon_cooldown",
                "Guild-list reload cannot reset the shared RCON cooldown");
        }
    }

    private static void SharedExecution()
    {
        using var fixture = new Fixture();
        foreach (Example example in Examples.Where(example => example.Name != "help"))
        {
            Task executing = fixture.Execute(fixture.Parse(example.Name, example.Args));
            var call = ServerCommands.Calls.Last();
            Check(!executing.IsCompleted && call.Line == example.Line, "Final common task retained " + example.Name);
            Check(call.Caller.Source == "discord" && call.Caller.Id == "444" && call.Caller.Name == "Operator Name", "Actor retained " + example.Name);
            if (example.Name == "chat")
                Check(call.Caller.Name != "Admin" && call.Caller.IsAuthorized() && call.Line == "chat \"TeLl everyone Hello\"",
                    "Administrative /chat keeps the real audit actor and literal message; fixed display title is a separate runtime concern");
            Check(call.Caller.Cancellation.CanBeCanceled && call.Caller.IsAuthorized(), "Delayed authority retained " + example.Name);
            call.Completion.SetResult(Result("ok")); executing.GetAwaiter().GetResult();
            Check(fixture.Audits.Count == 0, "Common audit not duplicated " + example.Name);
        }
        int before = ServerCommands.Calls.Count;
        object helpPending = fixture.Parse("help", A()); fixture.Execute(helpPending).Wait();
        var help = ((TaskCompletionSource<DiscordCommands.Result>)Get(helpPending, "Completion")).Task.Result;
        Check(ServerCommands.Calls.Count == before && help.Success && help.Code == "help", "Slash help uses local flat catalog");
        foreach (string name in Names) Check(help.Message.Contains("/" + name), "Help includes /" + name);
        Check(!help.Message.Contains("sm ") && !help.Message.Contains("/sm") && help.Message.Length <= 1999, "Help fits one message without legacy syntax");
        foreach (string verb in new[] { "save", "kick", "ban", "unban" })
            Check(help.Message.Contains("command:\"" + verb), "Help shows RCON example " + verb);
        Check(fixture.Audits.Count == 1 && fixture.Audits[0].Fields["command"] == "help", "Help audits once");
    }
    private static void TypedParsing()
    {
        using var fixture = new Fixture();
        Check((int)Definition("giveitem")["options"]![2]!["type"]! == 4, "Quantity Discord integer type");
        Check((int)Definition("heal")["options"]![1]!["type"]! == 10, "Amount Discord numeric type");
        Check((int)Definition("adminadd")["options"]![0]!["type"]! == 3, "Steam64 lossless text type");
        JObject playerOption = (JObject)Definition("players")["options"]![0]!;
        Check((string?)playerOption["name"] == "player" && (int)playerOption["type"]! == 3 &&
            !(bool)playerOption["required"]! && (int)playerOption["max_length"]! == 128,
            "Players selector is optional bounded lossless text");
        Check(fixture.ParsePayload(fixture.Payload("players", A())) != null &&
            fixture.ParsePayload(fixture.Payload("players", A("player", "P"))) != null,
            "Players accepts both list and single-player interactions");
        JObject numericPlayer = fixture.Payload("players", A("player", "76561198000000001"));
        numericPlayer["data"]!["options"]![0]!["value"] = 76561198000000001L;
        Check(fixture.ParsePayload(numericPlayer) == null, "Numeric value cannot spoof player ID text");
        JObject page = (JObject)Definition("characterbackups")["options"]![1]!;
        Check((int)page["type"]! == 4 && !(bool)page["required"]! && (int)page["min_value"]! == 1 &&
            (int)page["max_value"]! == 1000, "Backup page is an optional bounded Discord integer");
        JObject backup = (JObject)Definition("characterrestore")["options"]![1]!;
        Check((string?)backup["name"] == "backup_id" && (int)backup["type"]! == 3 && (bool)backup["required"]! &&
            (int)backup["min_length"]! == 32 && (int)backup["max_length"]! == 32, "Restore backup ID is required lossless fixed-length text");
        JObject backupPage = fixture.Payload("characterbackups", A("player", "P", "page", "1"));
        backupPage["data"]!["options"]![1]!["value"] = "1";
        Check(fixture.ParsePayload(backupPage) == null, "String value cannot spoof backup page integer");
        JObject backupId = fixture.Payload("characterrestore", A("player", "P", "backup_id", "0123456789abcdef0123456789abcdef"));
        backupId["data"]!["options"]![1]!["value"] = 123;
        Check(fixture.ParsePayload(backupId) == null, "Numeric value cannot spoof backup ID text");
        JObject wrong = fixture.Payload("giveitem", Examples.Single(e => e.Name == "giveitem").Args);
        wrong["data"]!["options"]![2]!["type"] = 3; wrong["data"]!["options"]![2]!["value"] = "3";
        Check(fixture.ParsePayload(wrong) == null, "String cannot spoof integer option");
        JObject unknown = fixture.Payload("status", A());
        unknown["data"]!["options"] = new JArray(new JObject { ["name"] = "unknown", ["type"] = 3, ["value"] = "save" });
        Check(fixture.ParsePayload(unknown) == null, "Unknown payload option dropped");
        JObject repeated = fixture.Payload("announce", A("message", "a"));
        ((JArray)repeated["data"]!["options"]!).Add(repeated["data"]!["options"]![0]!.DeepClone());
        Check(fixture.ParsePayload(repeated) == null, "Duplicate payload option rejected");
        JObject roles = fixture.Payload("status", A()); roles["member"]!["roles"] = new JObject { ["ignored"] = true };
        Check(fixture.ParsePayload(roles) != null, "Role payload has no parsing/authorization role");
        foreach (string name in new[] { "sm", "save", "kick", "ban", "unban" }.Concat(RetiredHyphenNames).Concat(RetiredFlatNames))
        {
            JObject retired = fixture.Payload("status", A()); retired["data"]!["name"] = name;
            Check(fixture.ParsePayload(retired) == null, "Retired slash payload ignored " + name);
        }
    }
    private static void Reauthorization()
    {
        using var fixture = new Fixture();
        Task executing = fixture.Execute(fixture.Parse("status", A())); var call = ServerCommands.Calls.Last();
        fixture.Settings.AdminUserIds.Clear(); Check(!call.Caller.IsAuthorized(), "Admin revocation denied"); fixture.Settings.AdminUserIds.Add("444");
        fixture.Settings.CommandChannelIds.Clear(); Check(!call.Caller.IsAuthorized(), "Channel revocation denied"); fixture.Settings.CommandChannelIds.Add("222");
        fixture.Settings.GuildIds.Clear(); fixture.Settings.GuildIds.Add("999"); Check(!call.Caller.IsAuthorized(), "Guild replacement denied");
        fixture.Settings.GuildIds.Clear(); fixture.Settings.GuildIds.Add("111");
        Set(fixture.Commands, "_applicationId", "999"); Check(!call.Caller.IsAuthorized(), "Application generation denied"); Set(fixture.Commands, "_applicationId", "777");
        fixture.Ids["111:status"] = "999"; Check(!call.Caller.IsAuthorized(), "Registration replacement denied"); fixture.Ids["111:status"] = "333";
        fixture.Settings.BotEnabled = false; Check(!call.Caller.IsAuthorized(), "Bot disabled denied"); fixture.Settings.BotEnabled = true;
        Set(fixture.Commands, "_retired", true); Check(!call.Caller.IsAuthorized(), "Retired world denied"); Set(fixture.Commands, "_retired", false);
        Check(call.Caller.IsAuthorized(), "Current authorization accepted");
        ZNet.World = new object(); fixture.Commands.Tick(); Check(!call.Caller.IsAuthorized(), "World replacement retires generation");
        fixture.Commands.Dispose(); Check(call.Caller.Cancellation.IsCancellationRequested && !call.Caller.IsAuthorized(), "Reload/disposal revokes work");
        call.Completion.SetResult(Result("cancelled", false)); executing.GetAwaiter().GetResult();
    }
    private static void ItemPresetArguments()
    {
        using var fixture = new Fixture();
        JObject definition = Definition("giveitem");
        Check(string.Join(",", ((JArray)definition["options"]!).Select(option => (string)option["name"]!)) ==
            "player,prefab,amount,quality,data_id", "Giveitem registers exactly the ordered typed options, never raw metadata");
        JObject dataOption = (JObject)definition["options"]![4]!;
        Check((int)dataOption["type"]! == 3 && !(bool)dataOption["required"]! &&
            (int)dataOption["min_length"]! == 1 && (int)dataOption["max_length"]! == 64,
            "Preset ID is optional bounded lossless Discord text");
        foreach (string id in new[] { "A", "Mixed-Preset_01", new string('a', 64) })
        {
            var args = A("data_id", id, "amount", "2", "prefab", "Wood", "player", "Player Name");
            string expected = "giveitem \"Player Name\" \"Wood\" \"2\" \"1\" \"" + id + "\"";
            Check(DiscordCommands.ValidateArguments("giveitem", args) && DiscordCommands.SharedLine("giveitem", args) == expected,
                "Preset without quality projects default quality before the exact ID");
            Check(!args.ContainsKey("quality"), "Projection must not mutate supplied Discord arguments");
            Task executing = fixture.Execute(fixture.Parse("giveitem", args));
            var call = ServerCommands.Calls.Last();
            Check(!executing.IsCompleted && call.Line == expected, "Preset-only selection follows the shared execution queue");
            call.Completion.SetResult(Result("ok")); executing.Wait();
            args["quality"] = "100";
            Check(DiscordCommands.SharedLine("giveitem", args) == expected.Replace("\"1\"", "\"100\"") &&
                fixture.ParsePayload(fixture.Payload("giveitem", args)) != null,
                "All five options preserve explicit quality and canonical ordering");
        }
        Check(fixture.Audits.Count == 0, "Preset commands use one shared audit, not duplicate Discord audit");
        foreach (string id in new[] { "", "@preset", "preset.json", "../preset", "C:\\preset", "a/b", "a:b", "a;b", "a=b",
            "with space", "한글", "ａ", new string('a', 65), "preset\n", "{\"key\":\"RAW_SECRET\"}" })
        {
            var args = A("player", "P", "prefab", "Wood", "amount", "1", "data_id", id);
            Check(!DiscordCommands.ValidateArguments("giveitem", args), "Malformed preset ID is rejected by argument validation");
            Check(fixture.ParsePayload(fixture.Payload("giveitem", args)) == null, "Malformed preset ID is rejected by payload parsing");
        }
        foreach (string quality in new[] { "0", "101", "1.5", "-1" })
            Check(!DiscordCommands.ValidateArguments("giveitem", A("player", "P", "prefab", "Wood", "amount", "1",
                "quality", quality, "data_id", "Preset")), "Preset never bypasses quality bounds");
        foreach (string key in new[] { "data", "custom_data", "metadata", "dataId" })
        {
            var args = A("player", "P", "prefab", "Wood", "amount", "1", key, "RAW_SECRET");
            Check(!DiscordCommands.ValidateArguments("giveitem", args), "Unregistered metadata option rejected");
            JObject payload = fixture.Payload("giveitem", A("player", "P", "prefab", "Wood", "amount", "1"));
            ((JArray)payload["data"]!["options"]!).Add(new JObject { ["name"] = key, ["type"] = 3, ["value"] = "RAW_SECRET" });
            Check(fixture.ParsePayload(payload) == null, "Unregistered metadata payload rejected");
        }
        var selection = A("player", "P", "prefab", "Wood", "amount", "1", "data_id", "Preset");
        JObject duplicate = fixture.Payload("giveitem", selection);
        ((JArray)duplicate["data"]!["options"]!).Add(duplicate["data"]!["options"]![3]!.DeepClone());
        Check(fixture.ParsePayload(duplicate) == null, "Duplicate data_id is rejected before dispatch");
        JObject wrongType = fixture.Payload("giveitem", selection);
        wrongType["data"]!["options"]![3]!["type"] = 4;
        Check(fixture.ParsePayload(wrongType) == null, "Preset ID cannot spoof its registered option type");
        wrongType["data"]!["options"]![3]!["type"] = 3;
        wrongType["data"]!["options"]![3]!["value"] = 123;
        Check(fixture.ParsePayload(wrongType) == null, "Numeric payload cannot spoof text preset ID");
    }
    private static void ItemPresetRejectionAudit()
    {
        using var fixture = new Fixture();
        object pending = fixture.Parse("giveitem", A("player", "PRIVATE_TARGET", "prefab", "PRIVATE_PREFAB", "amount", "1", "data_id", "Mixed-Preset_01"));
        int calls = ServerCommands.Calls.Count;
        fixture.Settings.AdminUserIds.Clear();
        fixture.Execute(pending).Wait();
        Check(ServerCommands.Calls.Count == calls && fixture.Audits.Count == 1, "Revoked preset command never reaches shared mutation");
        ServerManagerEvent audit = fixture.Audits.Single();
        Check(audit.Fields["command"] == "giveitem" && audit.Fields["result_code"] == "unauthorized" &&
            audit.Fields["data_id"] == "Mixed-Preset_01" && audit.Fields["message"].Contains("data_id=Mixed-Preset_01"),
            "Early item audit retains only the validated selected preset ID");
        Check(audit.Actor!.Id == "444" && !string.Join("|", audit.Fields.Values).Contains("PRIVATE_"),
            "Preset audit retains authenticated actor without raw player or prefab arguments");
        var args = (Dictionary<string, string>)Get(pending, "Arguments");
        args["data_id"] = "{\"key\":\"RAW_SECRET\"}"; args["custom_data"] = "VALUE_SECRET";
        typeof(DiscordCommands).GetMethod("Audit", Instance)!.Invoke(fixture.Commands, new object[] { pending, false, "invalid_argument" });
        audit = fixture.Audits.Last();
        Check(!audit.Fields.ContainsKey("data_id") && !string.Join("|", audit.Fields.Values).Contains("SECRET"),
            "Malformed preset IDs and custom metadata never enter audit fields or messages");
    }
    private static void RestoreRejectionAudit()
    {
        using var fixture = new Fixture();
        object pending = fixture.Parse("characterrestore", A("player", "PRIVATE_TARGET", "backup_id", "0123456789abcdef0123456789abcdef"));
        int calls = ServerCommands.Calls.Count;
        fixture.Settings.AdminUserIds.Clear();
        fixture.Execute(pending).Wait();
        Check(ServerCommands.Calls.Count == calls && fixture.Audits.Count == 1, "Revoked restore never reaches shared mutation");
        ServerManagerEvent audit = fixture.Audits.Single();
        Check(audit.Fields["command"] == "characterrestore" && audit.Fields["result_code"] == "unauthorized" &&
            audit.Fields["backup_id"] == "0123456789abcdef0123456789abcdef" &&
            audit.Fields["message"].Contains("backup_id=0123456789abcdef0123456789abcdef"),
            "Early restore audit keeps validated backup ID in structured fields and human log message");
        Check(audit.Actor!.Id == "444" && !string.Join("|", audit.Fields.Values).Contains("PRIVATE_TARGET"),
            "Early restore keeps original caller without exposing unvalidated target or raw arguments");
    }
    private static void RconRouting()
    {
        foreach (string line in new[] { "ban 76561198000000001 reason", "unban 76561198000000001", "ban malformed\"" })
        {
            using var fixture = new Fixture(captureAvailable: false); int prior = DiscordRconCapture.Executions;
            Task executing = fixture.Execute(fixture.Parse("rcon", A("command", line))); var call = ServerCommands.Calls.Last();
            Check(!executing.IsCompleted && call.Line == line && DiscordRconCapture.Executions == prior, "Protected RCON bypasses failed capture: " + line);
            Check(call.Caller.Source == "discord" && call.Caller.Id == "444", "Protected RCON actor retained");
            int calls = ServerCommands.Calls.Count;
            fixture.Execute(fixture.Parse("rcon", A("command", "ban 76561198000000001"))).Wait();
            Check(ServerCommands.Calls.Count == calls, "Protected RCON retains in-flight exclusion");
            fixture.Settings.AdminUserIds.Clear(); Check(!call.Caller.IsAuthorized(), "RCON live global-admin authorization"); fixture.Settings.AdminUserIds.Add("444");
            call.Completion.SetResult(Result("ok")); executing.GetAwaiter().GetResult();
            Check(fixture.Audits.Count == 1 && fixture.Audits[0].Fields["result_code"] == "rcon_cooldown", "Only adapter rejection audits locally");
        }
        foreach (string line in new[] { "save", "kick \"Player Name\" reason", "help", "event army_eikthyr", "heal", "status", "unknown-console-command" })
        {
            using var raw = new Fixture(); int prior = DiscordRconCapture.Executions, calls = ServerCommands.Calls.Count;
            raw.Execute(raw.Parse("rcon", A("command", line))).Wait();
            Check(DiscordRconCapture.Executions == prior + 1 && DiscordRconCapture.LastLine == line && ServerCommands.Calls.Count == calls, "Raw console semantics: " + line);
            Check(DiscordRconCapture.LastMaximum == 1800, "Raw capture receives the fixed 1800-character bound");
            Check(raw.Audits.Count == 1, "Raw console audit exactly once");
            using var replacement = new Fixture(); replacement.Commands.InheritRecentState(raw.Commands);
            replacement.Execute(replacement.Parse("rcon", A("command", "save"))).Wait();
            Check(DiscordRconCapture.Executions == prior + 1 && replacement.Audits.Last().Fields["result_code"] == "rcon_cooldown", "Reload retains RCON cooldown");
            Set(replacement.Commands, "_lastRconCompleted", Stopwatch.GetTimestamp() - 2L * Stopwatch.Frequency);
            replacement.Execute(replacement.Parse("rcon", A("command", "save"))).Wait();
            Check(DiscordRconCapture.Executions == prior + 2, "Fixed one-second cooldown permits execution after elapsed completion history");
        }
        using var unavailable = new Fixture(captureAvailable: false); int count = DiscordRconCapture.Executions;
        unavailable.Execute(unavailable.Parse("rcon", A("command", "save"))).Wait();
        Check(DiscordRconCapture.Executions == count && unavailable.Audits.Last().Fields["result_code"] == "rcon_unavailable", "Failed capture blocks raw only");
        Task feature = unavailable.Execute(unavailable.Parse("status", A()));
        ServerCommands.Calls.Last().Completion.SetResult(Result("ok")); feature.Wait();
        Check(unavailable.Audits.Count == 1, "Failed capture never disables direct features");
    }
    private static void DeferredRevocationAndTimeout()
    {
        using (var fixture = new Fixture())
        {
            JObject payload = fixture.Payload("status", A()); fixture.Commands.HandleDispatchAsync("INTERACTION_CREATE", payload).Wait();
            Wait(() => ((System.Collections.ICollection)Get(fixture.Commands, "_queue")).Count == 1, "Deferred request queued");
            int calls = ServerCommands.Calls.Count; fixture.Settings.AdminUserIds.Clear(); fixture.Commands.Tick();
            Wait(() => (int)Get(fixture.Commands, "_inFlight") == 0, "Revoked deferred request completed");
            Check(ServerCommands.Calls.Count == calls && fixture.Audits.Count == 1, "Revoked request never executes");
        }
        using (var fixture = new Fixture())
        {
            JObject payload = fixture.Payload("status", A());
            object pending = fixture.ProcessWithShortDeadline(payload);
            Wait(() => ((System.Collections.ICollection)Get(fixture.Commands, "_queue")).Count == 1, "Timeout request deferred");
            int calls = ServerCommands.Calls.Count;
            Wait(() => (int)Get(fixture.Commands, "_inFlight") == 0, "Unstarted timeout responded");
            Check((bool)Get(pending, "Expired") && !(bool)Get(pending, "Started"), "Unstarted expired request remains non-executable");
            fixture.Commands.Tick();
            Check(ServerCommands.Calls.Count == calls, "Expired queued command cannot execute on a later frame");
        }
        using (var fixture = new Fixture())
        {
            JObject payload = fixture.Payload("status", A());
            object pending = fixture.ProcessWithShortDeadline(payload);
            Wait(() => ((System.Collections.ICollection)Get(fixture.Commands, "_queue")).Count == 1, "Started-timeout request deferred");
            fixture.Commands.Tick(); var call = ServerCommands.Calls.Last(); int calls = ServerCommands.Calls.Count;
            Wait(() => (int)Get(fixture.Commands, "_inFlight") == 0, "Dispatched timeout responded");
            Check((bool)Get(pending, "Started") && !call.Caller.IsAuthorized(), "Started expired request loses further authority");
            Check(fixture.Http.Responses.Any(item => ((string?)item["content"] ?? "").Contains("즉시 재시도하지")), "Timeout warns about possible execution");
            fixture.Commands.HandleDispatchAsync("INTERACTION_CREATE", payload).Wait(); fixture.Commands.Tick();
            Check(ServerCommands.Calls.Count == calls, "Duplicate timeout delivery never retries mutation");
            Check(!call.Completion.Task.IsCompleted, "Response timeout does not cancel or roll back an already dispatched backend operation");
            int responses = fixture.Http.Responses.Count;
            call.Completion.SetResult(Result("committed"));
            Check(call.Completion.Task.Result.Success && fixture.Http.Responses.Count == responses,
                "An already dispatched operation may finish without replaying the timed-out response");
        }
    }
    internal static ServerManagerCommandResult Result(string code, bool success = true) => new(success, code, "Test result", "", new Dictionary<string, string>());
    private static object Get(object target, string name) => target.GetType().GetField(name, Instance)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Instance)!.SetValue(target, value);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); ++_checks; }
    private static void Wait(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > 5000) throw new Exception(message); Thread.Sleep(5); }
        ++_checks;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly DiscordSettings Settings = new();
        internal readonly DiscordHttp Http = new();
        internal readonly List<ServerManagerEvent> Audits = new();
        internal readonly DiscordCommands Commands;
        internal readonly Dictionary<string, string> Ids;
        private int _id = 1000;
        internal Fixture(bool captureAvailable = true, bool multipleGuilds = false)
        {
            ZNet.instance = new ZNet(); ZNet.World = new object();
            Settings.BotEnabled = true; Settings.GuildIds.Add("111"); Settings.BotToken = "test-token";
            if (multipleGuilds) Settings.GuildIds.Add("112");
            Settings.CommandChannelIds.Add("222"); Settings.AdminUserIds.Add("444");
            DiscordRconCapture.NextAvailable = captureAvailable;
            Commands = new DiscordCommands(Settings, Http, _ => { }, value => { lock (Audits) Audits.Add(value); }, CancellationToken.None);
            Set(Commands, "_applicationId", "777"); Ids = (Dictionary<string, string>)Get(Commands, "_commandIds");
            foreach (string guild in Settings.GuildIds)
                foreach (string name in Names) Ids[guild + ":" + name] = guild == "111" ? "333" : "334";
            Commands.Tick();
        }
        internal JObject Payload(string name, Dictionary<string, string> args, string guild = "111", string? commandId = null)
        {
            JArray options = new();
            foreach (var pair in args)
            {
                JObject spec = ((JArray)Definition(name)["options"]!).OfType<JObject>().Single(option => (string?)option["name"] == pair.Key);
                int type = (int)spec["type"]!;
                JToken value = type == 3 ? new JValue(pair.Value) : JToken.Parse(pair.Value);
                options.Add(new JObject { ["name"] = pair.Key, ["type"] = type, ["value"] = value });
            }
            return new JObject
            {
                ["id"] = (++_id).ToString(), ["application_id"] = "777", ["type"] = 2, ["token"] = "interaction-token",
                ["guild_id"] = guild, ["channel_id"] = "222",
                ["member"] = new JObject { ["nick"] = "Operator Name", ["user"] = new JObject { ["id"] = "444" } },
                ["data"] = new JObject { ["id"] = commandId ?? (Ids.TryGetValue(guild + ":" + name, out string id) ? id : "333"),
                    ["type"] = 1, ["name"] = name, ["options"] = options }
            };
        }
        internal object? ParsePayload(JObject payload) => typeof(DiscordCommands).GetMethod("Parse", Instance)!.Invoke(Commands, new object[] { payload });
        internal object Parse(string name, Dictionary<string, string> args, string guild = "111") => ParsePayload(Payload(name, args, guild)) ?? throw new Exception("Unexpected parse rejection " + name);
        internal object ProcessWithShortDeadline(JObject payload)
        {
            // Admission/duplicates are covered separately. Age only this in-memory
            // admitted request before its timeout task is created; do not weaken
            // the fixed production timeout or introduce a production clock hook.
            object pending = ParsePayload(payload)!;
            Set(pending, "Deadline", Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            ((Dictionary<string, long>)Get(Commands, "_seen")).Add((string)payload["id"]!, Stopwatch.GetTimestamp());
            Set(Commands, "_inFlight", 1);
            _ = (Task)typeof(DiscordCommands).GetMethod("ProcessInteractionAsync", Instance)!.Invoke(Commands, new[] { pending })!;
            return pending;
        }
        internal Task Execute(object pending) => (Task)typeof(DiscordCommands).GetMethod("ExecuteAndCompleteAsync", Instance)!.Invoke(Commands, new[] { pending })!;
        public void Dispose() => Commands.Dispose();
    }
}
internal sealed class ZNet
{
    internal static ZNet? instance;
    internal static object? World;
    internal bool IsServer() => true;
}
namespace ServerManager
{
    internal static class IntegrityCanonical
    {
        internal static bool IsFatal(Exception exception) => exception is OutOfMemoryException || exception is StackOverflowException || exception is AccessViolationException;
    }
    internal static class ServerManagerRuntime
    {
        internal static bool TryBroadcastDiscordShout(string userId, string userName, string message, bool adminChannel) => throw new Exception("Slash fixture must not relay ordinary channel messages.");
    }
    internal static class ServerManagerPlugin
    {
        internal const string ModVersion = "test";
        internal static readonly TestLog Log = new();
    }
    internal sealed class TestLog { public void LogWarning(string value) { } public void LogDebug(string value) { } }
}
namespace ServerManager.Events
{
    internal enum ServerEventCommandKind { Save, Announce, Kick, Ban, Unban }
    internal static class ServerEventRuntime
    {
        internal static ServerManagerStatusSnapshot GetStatusSnapshot() => new(true, true, true, true, true, false, "Test", "World", Array.Empty<ServerManagerPlayerSnapshot>(), null!);
        internal static Task<ServerManagerCommandResult> EnqueueCommand(ServerEventCommandKind kind, string first, string second) =>
            throw new Exception("Discord must use the common executor rather than divergent integration dispatch.");
    }
}
namespace ServerManager.Commands
{
    internal sealed class CommandCaller
    {
        internal CommandCaller(string source, string id, string name, Func<bool> isAuthorized, CancellationToken cancellation)
        { Source = source; Id = id; Name = name; IsAuthorized = isAuthorized; Cancellation = cancellation; }
        internal string Source { get; }
        internal string Id { get; }
        internal string Name { get; }
        internal Func<bool> IsAuthorized { get; }
        internal CancellationToken Cancellation { get; }
    }
    internal static class ServerCommands
    {
        private static Func<string, bool> _isBackupId = null!;
        private static Func<string, bool> _isItemDataId = null!;
        internal static void BindIdentifierPredicates(string assemblyPath)
        {
            // Run the production predicates while retaining the fixture's inert
            // execution queue; do not maintain a second identifier grammar here.
            Type production = Assembly.LoadFrom(assemblyPath).GetType("ServerManager.Commands.ServerCommands", true)!;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            _isBackupId = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>),
                production.GetMethod(nameof(IsBackupId), flags)!);
            _isItemDataId = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>),
                production.GetMethod(nameof(IsItemDataId), flags)!);
        }
        internal static bool IsBackupId(string value) => _isBackupId(value);
        internal static bool IsItemDataId(string value) => _isItemDataId(value);

        // This inert shared-boundary catalog is deliberately independent of the
        // Discord descriptors: adapter initialization fails on naming drift.
        internal static IReadOnlyList<string> FlatCommandNames { get; } = Array.AsReadOnly(new[]
        {
            "status", "players", "announce", "chat", "banlist",
            "adminlist", "adminadd", "adminremove", "accesslist", "accessadd", "accessremove",
            "keylist", "keyadd", "keyremove", "eventstart", "eventstop", "characterlist",
            "characterinfo", "characterbackups", "characterrestore", "giveitem", "teleport", "skillget", "skillset",
            "heal", "damage", "modsstatus", "modsreload", "discordstatus", "discordtest", "cronstatus", "cronack", "help"
        });
        internal sealed class Call
        {
            internal Call(string line, CommandCaller caller) { Line = line; Caller = caller; }
            internal readonly string Line;
            internal readonly CommandCaller Caller;
            internal readonly TaskCompletionSource<ServerManagerCommandResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal static readonly List<Call> Calls = new();
        internal static Task<ServerManagerCommandResult> ExecuteAsync(string line, CommandCaller caller)
        { var call = new Call(line, caller); Calls.Add(call); return call.Completion.Task; }
        internal static bool IsRconManagedLine(string line)
        {
            string verb = line.TrimStart().Split(new[] { ' ', '\t' }, 2)[0].ToLowerInvariant();
            return verb == "ban" || verb == "unban";
        }
    }
}
namespace ServerManager.Discord
{
    internal sealed class DiscordHttp
    {
        internal readonly JArray Existing = new();
        internal readonly Dictionary<string, JArray> ExistingByGuild = new();
        internal readonly List<string> RegistrationRequests = new();
        internal string FailedGuild = "";
        internal Action? BeforeRegistration;
        internal readonly List<JObject> Definitions = new();
        internal readonly List<JObject> Responses = new();
        internal readonly List<string> Deleted = new();
        internal bool BulkOverwrite;
        internal Task<JToken?> SendAsync(HttpMethod method, string uri, JObject? body, bool bot, CancellationToken token, bool retry = true)
        {
            token.ThrowIfCancellationRequested();
            if (uri.Contains("/commands"))
            {
                string guild = uri.Split(new[] { "/guilds/" }, StringSplitOptions.None)[1].Split('/')[0];
                lock (RegistrationRequests) RegistrationRequests.Add(method.Method + " " + uri);
                BeforeRegistration?.Invoke(); token.ThrowIfCancellationRequested();
                if (guild == FailedGuild) throw new DiscordHttpException();
                if (method == HttpMethod.Get) return Task.FromResult<JToken?>((ExistingByGuild.TryGetValue(guild, out JArray existing) ? existing : Existing).DeepClone());
                if (method == HttpMethod.Delete) { lock (Deleted) Deleted.Add(uri.Substring(uri.LastIndexOf('/') + 1)); return Task.FromResult<JToken?>(null); }
                if (method == HttpMethod.Put) BulkOverwrite = true;
                lock (Definitions) Definitions.Add((JObject)body!.DeepClone());
                return Task.FromResult<JToken?>(new JObject { ["id"] = guild == "111" ? "333" : "334" });
            }
            if (body != null) lock (Responses) Responses.Add((JObject)body.DeepClone());
            return Task.FromResult<JToken?>(new JObject());
        }
    }
    internal sealed class DiscordHttpException : Exception { internal int StatusCode { get; } }
    internal sealed class DiscordRconCapture : IDisposable
    {
        internal static int Executions;
        internal static int LastMaximum;
        internal static string LastLine = "";
        internal static bool NextAvailable = true;
        internal DiscordRconCapture(Action<string> log) { IsAvailable = NextAvailable; }
        internal bool IsAvailable { get; }
        internal DiscordCommands.Result Execute(string line, int maximum)
        { ++Executions; LastLine = line; LastMaximum = maximum; return DiscordCommands.Result.Ok("raw output"); }
        public void Dispose() { }
    }
}
