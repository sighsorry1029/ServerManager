using System;
using System.IO;
using System.Linq;
using ServerManager;

internal static class ServerScheduleHarness
{
    private static int _checks;
    private static readonly DateTime Now = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Game = ServerScheduleEngine.GameEpoch;
    private static int Main()
    {
        try
        {
            Parser(); Identity(); DefaultMaintenanceExample(); UpgradeWorldCatalog(); Scheduling(); Reload(); Zones(); AcknowledgeTimer();
            Console.WriteLine("PASS: cron parser/engine " + _checks + " assertions; no Unity, commands, network or user data.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Check(bool condition, string reason)
    { ++_checks; if (!condition) throw new Exception(reason); }

    private static void AcknowledgeTimer()
    {
        ServerScheduleSettings settings = ServerScheduleSettings.Parse("timezone: UTC\njobs:\n" + Job("announce review") + Job("announce other"));
        ServerScheduleEngine engine = new(); engine.Apply(settings, Now, Game);
        var first = engine.ClaimDue(Now.AddSeconds(10), Game);
        ServerScheduleOccurrence review = first.Single(item => item.Job.Id == settings.Jobs[0].Id);
        ServerScheduleOccurrence other = first.Single(item => item.Job.Id == settings.Jobs[1].Id);
        engine.Complete(review);
        engine.SkipThrough(review.Job.Id, Now.AddSeconds(35), Game);
        Check(engine.ClaimDue(Now.AddSeconds(35), Game).Count == 0, "Acknowledgement skips reviewed overdue work and preserves unrelated active claim");
        engine.Complete(other);
        Check(engine.ClaimDue(Now.AddSeconds(40), Game).Count == 2, "Preserved original completion releases other claim; reviewed next occurrence remains scheduled");
        engine.SkipThrough("removed", Now.AddSeconds(40), Game);
        Check(engine.ClaimDue(Now.AddSeconds(50), Game).Count == 0, "Acknowledging removed ID cannot reset existing active claims");
    }

    private static void Bad(string yaml)
    {
        try { ServerScheduleSettings.Parse(yaml); }
        catch (InvalidDataException) { ++_checks; return; }
        throw new Exception("Invalid schedule accepted: " + yaml.Substring(0, Math.Min(100, yaml.Length)));
    }

    private static string Job(string command = "save", string cron = "*/10 * * * * *", string extra = "") =>
        "  - command: '" + command + "'\n    schedule: '" + cron + "'\n" + extra;
    private static ServerScheduleSettings Config(string cron = "*/10 * * * * *", string extra = "") =>
        ServerScheduleSettings.Parse("timezone: UTC\njobs:\n" + Job(cron: cron, extra: extra));

    private static void Parser()
    {
        Check(ServerScheduleSettings.FileName == "cron.yml" && ServerScheduleSettings.AlternateFileName == "cron.yaml", "Canonical and alternate settings filenames");
        Check(ServerScheduleSettings.Parse(ServerScheduleSettings.DefaultYaml).Jobs.Count == 0, "Default disables jobs");
        ServerScheduleSettings empty = ServerScheduleSettings.Parse("{}");
        Check(empty.Jobs.Count == 0 && empty.IntervalSeconds == 10 && empty.LogSkipped, "Root defaults");
        Check(empty.TimeZone.Id == TimeZoneInfo.Local.Id, "Omitted zone uses local");
        ServerScheduleJob job = Config().Jobs.Single();
        Check(job.Id.StartsWith("save-", StringComparison.Ordinal) && job.Id.Length <= 64 && job.Log && job.Commands.Single() == "save" && job.Enabled && job.Chance == 1 &&
            !job.UseGameTime && job.GlobalKeys.Count == 0 && job.BannedGlobalKeys.Count == 0, "Defaults");
        job = Config(extra: "    log: false\n    chance: 0.25\n    useGameTime: true\n    globalKeys: 'b, a, a'\n    bannedGlobalKeys: c\n").Jobs.Single();
        Check(job.Enabled && !job.Log && job.Chance == .25 && job.UseGameTime && job.GlobalKeys.SequenceEqual(new[] { "a", "b" }) &&
            job.BannedGlobalKeys.Single() == "c", "Optional fields and canonical key sets");
        Check(Config(extra: "    globalKeys: ''\n    bannedGlobalKeys: ', , '\n").Jobs.Single().GlobalKeys.Count == 0,
            "Empty key strings are supported");
        ServerScheduleSettings logging = ServerScheduleSettings.Parse("interval: 1.5\nlogJobs: false\nlogSkipped: false\ndiscordConnector: false\nzone: []\njoin: []\njobs:\n" + Job());
        Check(logging.IntervalSeconds == 1.5 && !logging.LogSkipped && !logging.Jobs.Single().Log, "Root interval/logging and empty unsupported hooks");
        Check(ServerScheduleSettings.Parse("logJobs: false\njobs:\n" + Job(extra: "    log: true\n")).Jobs.Single().Log,
            "Per-job logging overrides root default");
        Check(ServerScheduleSettings.Parse("interval: 1").IntervalSeconds == 1 && ServerScheduleSettings.Parse("interval: 3600").IntervalSeconds == 3600,
            "Interval inclusive bounds");
        job = ServerScheduleSettings.Parse("jobs: [{schedule: '* * * * *', command: 'quit', commands: [save, 'announce ready']}]").Jobs.Single();
        Check(job.Commands.SequenceEqual(new[] { "save", "announce ready" }), "Commands sequence takes precedence over scalar command");
        Check(Config(extra: "    chance: 0\n").Jobs.Single().Chance == 0 &&
            Config(extra: "    chance: 1\n").Jobs.Single().Chance == 1, "Chance inclusive bounds");
        Check(!Config().Jobs[0].CatchUp && !Config().Jobs[0].Maintenance, "Ordinary jobs skip offline history");
        foreach (string reset in new[] { "zones_reset", "vegetation_reset", "locations_reset", "world_clean" })
        {
            string yaml = "jobs: [{schedule: '0 3 * * *', command: '" + reset + " start'}]";
            ServerScheduleJob upkeep = ServerScheduleSettings.Parse(yaml).Jobs[0];
            Check(upkeep.Maintenance && upkeep.CatchUp, "Supported operation automatically derives maintenance and catch-up: " + reset);
        }
        Bad("jobs: [{schedule: '0 3 * * *', commands: ['announce wrong'], maintenance: true}]");
        Bad("jobs: [{schedule: '0 3 * * *', commands: ['zones_reset start', 'quit']}]");
        Bad("jobs: [{schedule: '0 3 * * *', commands: ['save'], maintenance: true}]");
        Bad("jobs:\n" + Job(extra: "    catchUp: 'true'\n"));
        foreach (string removed in new[] { "id: old", "cron: '* * * * *'", "enabled: true", "enabled: false", "catchUp: true", "catchUp: false", "maintenance: true", "maintenance: false" })
            Bad("jobs:\n" + Job(extra: "    " + removed + "\n"));
        int calls = 0;
        ServerScheduleSettings.Parse("jobs:\n" + Job(), command => { ++calls; return command == "save"; });
        Check(calls == 1, "Pure command admission callback");
        try { ServerScheduleSettings.Parse("jobs:\n" + Job(), _ => false); throw new Exception("Forbidden command admitted"); }
        catch (InvalidDataException) { ++_checks; }

        foreach (string invalid in new[] {
            "", "[]", "jobs: null", "timezone: null", "timezone: not-a-real-zone", "unknown: true",
            "jobs: {}", "jobs: [null]", "jobs: [{schedule: '* * * * *'}]", "jobs: [{command: save}]",
            "jobs: [{schedule: '* * * * *', commands: []}]",
            "jobs: [{schedule: '* * * * *', command: save, commands: []}]",
            "jobs: [{schedule: '* * * * *', commands: save}]",
            "jobs: [{schedule: '* * * * *', command: [save]}]",
            "jobs: [{schedule: '* * * * *', commands: [null]}]",
            "jobs: [{schedule: '* * * * *', command: null}]",
            "jobs: [{schedule: '* * * * *', commands: ['']}]",
            "jobs: [{schedule: '* * * * *', commands: [\"save\\nquit\"]}]",
            "jobs: [{schedule: '* * * * *', commands: [save], other: true}]",
            "discordConnector: true", "zone: [test]", "join: [test]", "zone: {}", "join: null",
            "jobs: []\njobs: []", "timezone: UTC\ntimezone: local", "---\njobs: []\n---\njobs: []",
            "jobs: &jobs []", "jobs: *jobs", "jobs: !!seq []", "jobs: !anything []",
            "jobs: [[[[[[[[[[]]]]]]]]]]" }) Bad(invalid);
        foreach (string interval in new[] { "0", "-1", "3601", ".nan", ".inf", "'10'", "true", "null", "1e9999" })
            Bad("interval: " + interval);
        foreach (string cron in new[] { "", "* *", "* * * * * * *", "61 * * * * *", "bad * * * *" })
            Bad("jobs:\n" + Job(cron: cron));
        foreach (string chance in new[] { "-0.1", "1.1", ".nan", ".inf", "'0.5'", "true" })
            Bad("jobs:\n" + Job(extra: "    chance: " + chance + "\n"));
        foreach (string flag in new[] { "yes", "True", "'true'", "1", "null" })
        {
            foreach (string name in new[] { "log", "useGameTime" }) Bad("jobs:\n" + Job(extra: "    " + name + ": " + flag + "\n"));
            foreach (string name in new[] { "logJobs", "logSkipped", "discordConnector" }) Bad(name + ": " + flag);
        }
        Bad("jobs:\n" + Job() + Job());
        Bad("jobs:\n" + Job(extra: "    schedule: '* * * * *'\n"));
        foreach (string key in new[] { "[]", "[a,b]", "{}", "null", "~", "", "'" + new string('x', 129) + "'",
            "'" + string.Join(",", Enumerable.Range(0, 65).Select(i => "key" + i)) + "'" })
            Bad("jobs:\n" + Job(extra: "    globalKeys: " + key + "\n"));
        Check(Config(extra: "    globalKeys: '" + string.Join(",", Enumerable.Range(0, 64).Select(i => i.ToString().PadLeft(128, 'x'))) + "'\n")
            .Jobs.Single().GlobalKeys.Count == 64, "64 keys of 128 characters are accepted");
        string command = new('x', 2048);
        string manyCommands = "jobs: [{schedule: '* * * * *', commands: [" +
            string.Join(",", Enumerable.Repeat("'" + command + "'", 16)) + "]}]";
        Check(ServerScheduleSettings.Parse(manyCommands).Jobs.Single().Commands.Count == 16, "Command bounds accepted");
        Bad(manyCommands.Replace(command, command + "x"));
        Bad(manyCommands.Replace("]}]", ",'save']}]"));
        string many = "jobs:\n" + string.Concat(Enumerable.Range(0, 64).Select(i => Job("announce " + i)));
        Check(ServerScheduleSettings.Parse(many).Jobs.Count == 64, "64 job limit inclusive");
        Bad(many + Job("announce 64"));
        Check(ServerScheduleSettings.Parse("#" + new string('x', ServerScheduleSettings.MaximumFileBytes - 4) + "\n{}").Jobs.Count == 0,
            "Exact UTF8 file limit accepted");
        Bad("#" + new string('x', ServerScheduleSettings.MaximumFileBytes) + "\n{}");
        Bad("#" + new string('한', ServerScheduleSettings.MaximumFileBytes / 2) + "\n{}");
        Bad("#\ud800\n{}");
    }

    private static void Identity()
    {
        ServerScheduleJob original = Config(extra: "    globalKeys: 'a,b'\n    bannedGlobalKeys: c\n").Jobs.Single();
        ServerScheduleJob normalized = Config("  */10  * * * * *  ", "    globalKeys: ' b, a, a '\n    bannedGlobalKeys: ' c '\n    log: false\n").Jobs.Single();
        Check(original.Id == normalized.Id && original.SameAs(normalized), "Whitespace/key ordering/logging do not change execution identity");
        Check(Config("*/10\t* * * * *").Jobs.Single().Id == Config().Jobs.Single().Id, "Schedule tabs normalize to spaces");
        Check(Config(extra: "    chance: -0\n").Jobs.Single().Id == Config(extra: "    chance: 0\n").Jobs.Single().Id,
            "Equivalent numeric zero has stable identity");
        Check(ServerScheduleSettings.Parse("timezone: UTC\njobs: [{schedule: '*/10 * * * * *', commands: [save]}]").Jobs.Single().Id == Config().Jobs.Single().Id,
            "Scalar command and one-item commands share execution identity");
        Check(ServerScheduleSettings.Parse("timezone: UTC\ninterval: 60\nlogJobs: false\nlogSkipped: false\njobs:\n" + Job()).Jobs.Single().Id == Config().Jobs.Single().Id,
            "Root polling/logging settings do not change a job's execution identity");
        Check(ServerScheduleSettings.Parse("timezone: 'Korea Standard Time'\njobs:\n" + Job()).Jobs.Single().Id != Config().Jobs.Single().Id,
            "Wall-clock timezone changes execution identity");
        Check(ServerScheduleSettings.Parse("timezone: 'Korea Standard Time'\njobs:\n" + Job(extra: "    useGameTime: true\n")).Jobs.Single().Id ==
            Config(extra: "    useGameTime: true\n").Jobs.Single().Id, "Game-time identity is independent of wall-clock timezone");
        foreach (string extra in new[] { "    chance: 0.5\n", "    useGameTime: true\n", "    globalKeys: key\n", "    bannedGlobalKeys: key\n" })
            Check(Config(extra: extra).Jobs.Single().Id != Config().Jobs.Single().Id, "Execution condition changes identity: " + extra.Trim());
        Check(Config("*/20 * * * * *").Jobs.Single().Id != Config().Jobs.Single().Id, "Schedule changes identity");
        Check(ServerScheduleSettings.Parse("timezone: UTC\njobs:\n" + Job("announce save")).Jobs.Single().Id != Config().Jobs.Single().Id,
            "Command changes identity");
        Bad("jobs:\n" + Job(extra: "    globalKeys: 'a,b'\n") + Job(extra: "    globalKeys: 'b,a,a'\n    log: false\n"));
        Bad("jobs:\n" + Job() + "  - schedule: '*/10  * * * * *'\n    commands: [save]\n");
        Check(ServerScheduleSettings.Parse("jobs:\n" + Job("한글")).Jobs.Single().Id.StartsWith("job-", StringComparison.Ordinal),
            "Non-ASCII command verbs still generate bounded ASCII identities");
    }

    private static void DefaultMaintenanceExample()
    {
        string[] lines = ServerScheduleSettings.DefaultYaml.Replace("\r\n", "\n").Split('\n');
        Check(lines.Count(line => line == "timezone: local") == 1 && lines.Count(line => line == "jobs: []") == 1,
            "Generated default retains one active local timezone and empty jobs list");
        Check(ServerScheduleSettings.Parse(ServerScheduleSettings.DefaultYaml).TimeZone.Id == TimeZoneInfo.Local.Id,
            "Generated default resolves the server-local timezone");
        int jobLine = Array.IndexOf(lines, "#   - schedule: '35 5 * * *'");
        Check(jobLine > 0 && lines[jobLine - 1] == "# jobs:", "Maintenance example is a fully commented standalone jobs block");
        string[] exampleLines = lines.Skip(jobLine - 1)
            .TakeWhile(line => line == "# jobs:" || line.StartsWith("#   ", StringComparison.Ordinal)).ToArray();
        string example = "timezone: local\n" + string.Join("\n", exampleLines.Select(line => line.Substring(2))) + "\n";
        ServerScheduleSettings settings = ServerScheduleSettings.Parse(example);
        Check(settings.Jobs.Count == 1, "Uncommenting the actual maintenance example produces exactly one job");
        ServerScheduleJob job = settings.Jobs.Single();
        Check(job.Id.StartsWith("world_clean-", StringComparison.Ordinal) && job.Cron == "35 5 * * *" && job.Enabled && !job.UseGameTime,
            "Uncommented example is scheduled at 05:35 on the local wall clock");
        Check(job.Maintenance && job.CatchUp, "Example automatically derives verified maintenance and offline catch-up policies");
        Check(job.Commands.SequenceEqual(new[] {
            "world_clean",
            "zones_reset safeZones=1 start",
            "vegetation_reset rock4_copper,silvervein terrain=20 safeZones=0 start",
            "locations_reset Hildir_crypt,Hildir_cave,Hildir_plainsfortress,SunkenCrypt4,Crypt2,Crypt3,Crypt4,MountainCave02,Mistlands_Giant1,Mistlands_Excavation1,Mistlands_DvergrTownEntrance1,Mistlands_DvergrTownEntrance2,Mistlands_DvergrBossEntrance1,CharredFortress force start"
        }), "Actual template preserves the four exact command lines, argument order, and command order");
        Check(!lines.Any(line => line.Contains("enabled:")) && lines.Any(line => line.Contains("before uncommenting")),
            "Template explains commenting/removing instead of a legacy enabled flag");

        DateTime before = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 9, 7, 5, 34, 59, DateTimeKind.Unspecified), settings.TimeZone);
        ServerScheduleEngine engine = new(); engine.Apply(ServerScheduleSettings.Parse(ServerScheduleSettings.DefaultYaml), before, Game);
        Check(engine.ClaimDue(before.AddDays(2), Game).Count == 0, "Untouched default never makes maintenance due");
        engine.Reset(); engine.Apply(settings, before, Game);
        Check(engine.ClaimDue(before, Game).Count == 0, "Uncommented example is not due before 05:35");
        ServerScheduleOccurrence occurrence = engine.ClaimDue(before.AddSeconds(1), Game).Single();
        Check(occurrence.OccurrenceUtc == before.AddSeconds(1) && occurrence.Job.Maintenance && occurrence.Job.CatchUp,
            "Uncommenting the extracted example creates the expected 05:35 maintenance claim without executing any command");
    }

    private static void UpgradeWorldCatalog()
    {
        var groups = new[] {
            (UpgradeWorldCommandKind.QueuedMutation, "chests_reset locations_add locations_remove locations_reset objects_edit objects_refresh objects_remove objects_swap world_reset vegetation_add vegetation_remove vegetation_reset zones_generate zones_reset zones_restore"),
            (UpgradeWorldCommandKind.ImmediateMutation, "world_clean clean_chests clean_dungeons clean_duplicates clean_health clean_locations clean_objects clean_spawns clean_stands location_register locations_fix locations_swap time_change time_change_day time_set time_set_day"),
            (UpgradeWorldCommandKind.Query, "biomes_count chests_search locations_count locations_list objects_count objects_list"),
            (UpgradeWorldCommandKind.Control, "start stop save_disable save_enable verbose uw_check"),
            (UpgradeWorldCommandKind.Unsupported, "location_unregister")
        };
        foreach (var group in groups)
        foreach (string command in group.Item2.Split(' '))
        {
            Check(UpgradeWorldScheduleCommands.GetKind(command) == group.Item1, "Reviewed UW catalog entry: " + command);
            string yaml = "jobs: [{schedule: '0 3 * * *', commands: ['" + command + "']}]";
            if (group.Item1 == UpgradeWorldCommandKind.Control || group.Item1 == UpgradeWorldCommandKind.Unsupported) Bad(yaml);
            else
            {
                ServerScheduleJob job = ServerScheduleSettings.Parse(yaml).Jobs[0];
                bool maintenance = UpgradeWorldScheduleCommands.IsMaintenance(group.Item1);
                Check(job.Maintenance == maintenance && job.CatchUp == maintenance, "Role-derived offline policy: " + command);
                Check(UpgradeWorldScheduleCommands.GetKind("  " + command.ToUpperInvariant() + " argument ") == group.Item1,
                    "Command classification normalizes case and whitespace: " + command);
            }
            Check(UpgradeWorldScheduleCommands.GetKind("sm:" + command) == UpgradeWorldCommandKind.Unsupported,
                "No implicit ServerManager alias: " + command);
        }
        foreach (string type in new[] { "tarpits", "mistlands", "mistlands_worldgen", "hh_worldgen", "legacy_worldgen",
            "mountain_caves", "hildir", "ashlands", "deepnorth", "bogwitch", "combatruins" })
        {
            string line = "upgrade " + type + " start";
            Check(UpgradeWorldScheduleCommands.GetKind(line) == UpgradeWorldCommandKind.QueuedMutation &&
                UpgradeWorldScheduleCommands.GetUpgradeType(line) == type, "Reviewed upgrade recipe: " + type);
            Check(ServerScheduleSettings.Parse("jobs: [{schedule: '* * * * *', commands: ['" + line + "']}]").Jobs[0].CatchUp,
                "Queued recipes automatically catch up");
        }
        foreach (string line in new[] { "temple_gen mistlands start", "temple_gen ashlands start",
            "world_gen legacy start", "world_gen hh start", "world_gen mistlands start" })
        {
            Check(UpgradeWorldScheduleCommands.GetKind(line) == UpgradeWorldCommandKind.QueuedMutation,
                "Explicit reviewed generation version: " + line);
            Check(ServerScheduleSettings.Parse("jobs: [{schedule: '* * * * *', commands: ['" + line + "']}]").Jobs[0].Maintenance,
                "Explicit version is maintenance");
        }
        foreach (string line in new[] { "upgrade", "upgrade onions start", "upgrade unknown start", "upgrade tarpits onions start",
            "upgrade tarpits hildir start", "upgrade tarpits tarpits start", "sm:world_clean", "\"sm:zones_reset\" start",
            "temple_gen", "temple_gen start", "temple_gen wrong start", "temple_gen mistlands ashlands start", "temple_gen mistlands mistlands start",
            "temple_gen mistlands wrong start", "world_gen", "world_gen start", "world_gen wrong start", "world_gen legacy hh start",
            "world_gen legacy legacy start", "world_gen legacy wrong start", "sm:world_gen legacy start",
            "temple_gen MISTLANDS start", "temple_gen \"mistlands\" start", "world_gen HH start", "world_gen \"legacy\" start" })
            Bad("jobs: [{schedule: '* * * * *', commands: ['" + line + "']}]");
        Check(UpgradeWorldScheduleCommands.GetKind("upgrade start tarpits") == UpgradeWorldCommandKind.QueuedMutation,
            "Upgrade recipe flag position matches upstream scanner");
        Check(UpgradeWorldScheduleCommands.GetKind("zones_future") == UpgradeWorldCommandKind.None &&
            UpgradeWorldScheduleCommands.GetKind("mod_world_clean") == UpgradeWorldCommandKind.None,
            "Name prefix alone is not proof of mod ownership");
        Check(UpgradeWorldScheduleCommands.GetKind("") == UpgradeWorldCommandKind.None &&
            ServerScheduleSettings.CommandVerb("   ") == "", "Empty classifier input is harmless");
        ServerScheduleJob combined = ServerScheduleSettings.Parse(
            "jobs: [{schedule: '* * * * *', commands: [save, 'objects_remove start', world_clean, save]}]").Jobs[0];
        Check(combined.Maintenance && combined.CatchUp, "Queued and immediate changes share one verified maintenance batch");
        foreach (string other in new[] { "announce test", "quit", "objects_count", "uw_check", "sm:save", "save extra" })
            Bad("jobs: [{schedule: '* * * * *', commands: ['objects_remove start', '" + other + "']}]");
        Check(!ServerScheduleSettings.Parse("jobs: [{schedule: '* * * * *', commands: [objects_count, 'announce done']}]").Jobs[0].Maintenance,
            "Queries can remain ordinary scheduled commands without pre/post saves");
    }

    private static void Scheduling()
    {
        ServerScheduleEngine engine = new();
        engine.Apply(Config(), Now, Game);
        Check(engine.ClaimDue(Now, Game).Count == 0, "Activation excludes current boundary/history");
        Check(engine.ClaimDue(Now.AddSeconds(9), Game).Count == 0, "Not early");
        ServerScheduleOccurrence first = engine.ClaimDue(Now.AddSeconds(10), Game).Single();
        Check(first.OccurrenceUtc == Now.AddSeconds(10), "Six-field seconds occurrence");
        Check(engine.ClaimDue(Now.AddSeconds(10), Game).Count == 0, "Same occurrence never claimed twice");
        Check(engine.ClaimDue(Now.AddSeconds(30), Game).Count == 0, "Busy job skips all elapsed occurrences");
        engine.Complete(first);
        Check(engine.ClaimDue(Now.AddSeconds(30), Game).Count == 0, "No pending backlog after completion");
        ServerScheduleOccurrence second = engine.ClaimDue(Now.AddSeconds(40), Game).Single();
        engine.Complete(first);
        Check(engine.ClaimDue(Now.AddSeconds(50), Game).Count == 0, "Old completion cannot release newer claim");
        engine.Complete(second);
        Check(engine.ClaimDue(Now.AddSeconds(20), Game).Count == 0, "Wall clock rollback retains future boundary");
        Check(engine.ClaimDue(Now.AddSeconds(59), Game).Count == 0, "Rollback does not replay elapsed work");
        ServerScheduleOccurrence late = engine.ClaimDue(Now.AddHours(1), Game).Single();
        Check(late.OccurrenceUtc == Now.AddSeconds(60), "Delayed poll coalesces to one occurrence");
        engine.Complete(late);
        Check(engine.ClaimDue(Now.AddHours(1), Game).Count == 0, "Late poll does not replay remainder");
        engine.Reset();
        engine.Apply(Config(), Now.AddHours(2), Game);
        Check(engine.ClaimDue(Now.AddHours(2), Game).Count == 0, "Restart skips offline occurrences");
        engine.Reset();
        engine.Apply(Config("* * * * *"), Now, Game);
        Check(engine.ClaimDue(Now.AddSeconds(59), Game).Count == 0 &&
            engine.ClaimDue(Now.AddMinutes(1), Game).Count == 1, "Five-field minute occurrence");
        engine.Reset();
        engine.Apply(ServerScheduleSettings.Parse("jobs: []"), Now, Game);
        Check(engine.ClaimDue(Now.AddDays(1), Game).Count == 0, "Removed/commented jobs never due");
        try { engine.Apply(Config(), DateTime.SpecifyKind(Now, DateTimeKind.Local), Game); throw new Exception("Local clock accepted"); }
        catch (ArgumentException) { ++_checks; }
        try { engine.ClaimDue(Now, Game.AddTicks(-1)); throw new Exception("Invalid game epoch accepted"); }
        catch (ArgumentException) { ++_checks; }
    }

    private static void Reload()
    {
        ServerScheduleEngine engine = new();
        engine.Apply(Config(), Now, Game);
        engine.Apply(Config(), Now.AddSeconds(10), Game);
        ServerScheduleOccurrence old = engine.ClaimDue(Now.AddSeconds(10), Game).Single();
        Check(old.Job.Id == Config().Jobs.Single().Id, "Unchanged reload keeps due occurrence");
        engine.Apply(ServerScheduleSettings.Parse("jobs: []"), Now.AddSeconds(11), Game);
        Check(engine.ClaimDue(Now.AddSeconds(20), Game).Count == 0, "Deleted job removed");
        engine.Apply(Config(), Now.AddSeconds(20), Game);
        Check(engine.ClaimDue(Now.AddSeconds(30), Game).Count == 0, "Re-added ID cannot overlap old active work");
        engine.Complete(old);
        ServerScheduleOccurrence current = engine.ClaimDue(Now.AddSeconds(40), Game).Single();
        engine.Reset();
        engine.Apply(Config(), Now, Game);
        ServerScheduleOccurrence reset = engine.ClaimDue(Now.AddSeconds(10), Game).Single();
        engine.Complete(current);
        Check(engine.ClaimDue(Now.AddSeconds(20), Game).Count == 0, "Old-session completion cannot clear current claim");
        ServerScheduleEngine foreign = new();
        foreign.Apply(Config(), Now, Game);
        foreign.Complete(reset);
        ServerScheduleOccurrence other = foreign.ClaimDue(Now.AddSeconds(10), Game).Single();
        engine.Complete(other);
        Check(engine.ClaimDue(Now.AddSeconds(30), Game).Count == 0, "Another engine completion cannot release claim");
        engine.Complete(reset);
        engine.Reset();
        engine.Apply(Config(), Now, Game);
        engine.Apply(Config("*/20 * * * * *"), Now.AddSeconds(20), Game);
        Check(engine.ClaimDue(Now.AddSeconds(20), Game).Count == 0 &&
            engine.ClaimDue(Now.AddSeconds(40), Game).Count == 1, "Changed schedule begins after reload time");
        engine.Reset();
        string a = "timezone: UTC\njobs:\n" + Job("a") + Job("b");
        string b = "timezone: UTC\njobs:\n" + Job("b") + Job("a");
        engine.Apply(ServerScheduleSettings.Parse(a), Now, Game);
        engine.Apply(ServerScheduleSettings.Parse(b), Now.AddSeconds(10), Game);
        Check(engine.ClaimDue(Now.AddSeconds(10), Game).Count == 2, "Reordering uses stable IDs, not index");
        engine.Reset();
        ServerScheduleSettings logged = Config(), quiet = Config(extra: "    log: false\n");
        engine.Apply(logged, Now, Game);
        engine.Apply(quiet, Now.AddSeconds(10), Game);
        ServerScheduleOccurrence changedLog = engine.ClaimDue(Now.AddSeconds(10), Game).Single();
        Check(changedLog.OccurrenceUtc == Now.AddSeconds(10) && !changedLog.Job.Log && ReferenceEquals(changedLog.Job, quiet.Jobs.Single()),
            "Logging-only reload publishes new logging without resetting due boundary");
        engine.Apply(logged, Now.AddSeconds(11), Game);
        Check(!changedLog.Job.Log && engine.ClaimDue(Now.AddSeconds(20), Game).Count == 0,
            "Logging-only reload retains active claims and captured occurrence definition");
        engine.Complete(changedLog);
        Check(engine.ClaimDue(Now.AddSeconds(30), Game).Single().Job.Log, "New occurrences use the latest logging preference");
    }

    private static void Zones()
    {
        ServerScheduleEngine engine = new();
        engine.Apply(Config(extra: "    useGameTime: true\n"), Now, Game);
        Check(engine.ClaimDue(Now.AddDays(1), Game.AddSeconds(9)).Count == 0, "Real time does not advance game jobs");
        ServerScheduleOccurrence game = engine.ClaimDue(Now.AddDays(1), Game.AddSeconds(10)).Single();
        Check(game.OccurrenceUtc == Game.AddSeconds(10), "Game occurrence uses virtual UTC");
        engine.Complete(game);
        Check(engine.ClaimDue(Now.AddDays(1), Game.AddSeconds(1)).Count == 0, "Game clock rollback does not replay");
        engine.Reset();
        const string zone = "Korea Standard Time";
        TimeZoneInfo korea = TimeZoneInfo.FindSystemTimeZoneById(zone);
        DateTime before = new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        ServerScheduleSettings settings = ServerScheduleSettings.Parse("timezone: '" + zone + "'\njobs:\n" + Job(cron: "0 10 * * *"));
        engine.Apply(settings, before, Game);
        Check(engine.ClaimDue(before.AddMinutes(59), Game).Count == 0 &&
            engine.ClaimDue(before.AddHours(1), Game).Count == 1, "Installed real timezone shifts occurrence");
        engine.Reset();
        settings = ServerScheduleSettings.Parse("timezone: '" + zone + "'\njobs:\n" + Job(cron: "0 10 * * *", extra: "    useGameTime: true\n"));
        engine.Apply(settings, before, Game);
        Check(engine.ClaimDue(before, Game.AddHours(1)).Count == 0 &&
            engine.ClaimDue(before, Game.AddHours(10)).Count == 1, "Game cron ignores real timezone/DST");
    }
}
