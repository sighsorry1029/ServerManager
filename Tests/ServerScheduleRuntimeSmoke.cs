using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerManager;
using ServerManager.Commands;
using ServerManager.Events;
using YamlDotNet.RepresentationModel;

// Production parser, engine, durable journal and runtime are source-linked. Only
// game, logger, plugin registry and command/Upgrade World boundaries are inert. Reflection advances private
// polling deadlines; the actual lifecycle, file reads and dispatch code execute.
internal static class ServerScheduleRuntimeSmoke
{
    private static int _checks, _case;
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string reason)
    { ++_checks; if (!condition) throw new Exception(reason); }
    private static object? Session => typeof(ServerScheduleRuntime).GetField("_session", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
    private static object? Get(object value, string field) => value.GetType().GetField(field, Hidden)!.GetValue(value);
    private static void Set(object value, string field, object content) => value.GetType().GetField(field, Hidden)!.SetValue(value, content);
    private static IList Runs => (IList)Get(Session!, "_runs")!;
    private static int Claims => ((IDictionary)Get(Get(Session!, "_engine")!, "_claims")!).Count;
    private static string FilePath => Path.Combine(ServerManagerPlugin.DataRoot, ServerScheduleSettings.FileName);
    private static string JobId(int index = 0) => ((ServerScheduleSettings)Get(Session!, "_settings")!).Jobs[index].Id;
    private static string Config(params string[] commands) => "timezone: UTC\njobs:\n" + Job("test", commands);
    private static string Maintenance(params string[] commands) => Config(commands);
    private static string Job(string label, string[] commands, string extra = "") =>
        "  # Fixture " + label + "\n  - schedule: '* * * * * *'\n    useGameTime: true\n    commands: [" +
        string.Join(", ", commands.Select(line => "'" + line.Replace("'", "''") + "'")) + "]\n" + extra;

    private static int Main()
    {
        try
        {
            Lifecycle(); GuardsAndFiles(); AlternateConfiguration(); CanonicalJournalStartup(); RoutingAndBudget(); Gates(); PollingAndLogging(); Saves(); ReloadAndRetirement(); ReentrancyAndSuspension();
            DurableDispatchAndCatchup(); AutomaticUpgradeWorldPolicies(); UpgradeWorldOwnership(); UntrackedUpgrade(); SynchronousMutationAbort(); MaintenanceSequence(); MaintenanceFailureAndRecovery(); MaintenanceReload(); MaintenanceReviewBarrier(); MaintenanceAdmission();
            Console.WriteLine("ServerScheduleRuntimeSmoke: PASS (" + _checks +
                " assertions; source-linked runtime/parser/engine/journal; stub game/command/Upgrade World boundaries, no native game execution).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { ServerScheduleRuntime.Stop(); }
    }

    private static void Reset()
    {
        ServerScheduleRuntime.Stop();
        ServerManagerPlugin.DataRoot = Path.Combine(Environment.CurrentDirectory, "case-" + (++_case));
        ServerManagerPlugin.Log.Messages.Clear();
        BepInEx.Bootstrap.Chainloader.PluginInfos.Clear();
        BepInEx.Bootstrap.Chainloader.PluginInfos["upgrade_world"] = new BepInEx.PluginInfo();
        ValheimPrivateAccess.Commands.Clear();
        foreach (string verb in new[] { "chests_reset", "locations_add", "locations_remove", "locations_reset", "objects_edit", "objects_refresh", "objects_remove", "objects_swap",
            "temple_gen", "world_gen", "world_reset", "vegetation_add", "vegetation_remove", "vegetation_reset", "zones_generate", "zones_reset", "zones_restore", "upgrade",
            "world_clean", "clean_chests", "clean_dungeons", "clean_duplicates", "clean_health", "clean_locations", "clean_objects", "clean_spawns", "clean_stands",
            "location_register", "locations_fix", "locations_swap", "time_change", "time_change_day", "time_set", "time_set_day", "biomes_count", "chests_search",
            "locations_count", "locations_list", "objects_count", "objects_list", "uw_check", "start", "stop", "save_disable", "save_enable", "verbose" })
            ValheimPrivateAccess.Commands[verb] = new Terminal.ConsoleCommand(FixtureUpgradeWorld.NoOp);
        ServerConsoleExecutor.Lines.Clear(); ServerConsoleExecutor.Disposals = 0;
        ServerConsoleExecutor.NextResult = null; ServerConsoleExecutor.OnExecute = null;
        ServerCommands.Calls.Clear(); ServerCommands.NextTask = null;
        ServerEventRuntime.Audits.Clear(); ServerEventRuntime.Saves.Clear(); ServerEventRuntime.NextSave = null;
        ServerEventRuntime.CommandWorldEpoch++;
        UpgradeWorldScheduleBridge.Reset();
        ServerEventRuntime.Status = new ServerManagerStatusSnapshot { WorldReady = true };
        ZNet.instance = new ZNet(); EnvMan.instance = new EnvMan(); ZoneSystem.instance = new ZoneSystem();
    }

    private static void Activate(string yaml)
    {
        Reset(); Directory.CreateDirectory(ServerManagerPlugin.DataRoot);
        File.WriteAllText(FilePath, yaml, new UTF8Encoding(false));
        Tick(); FinishRead();
        Check(Session != null && Get(Session!, "_settings") != null, "Valid schedule is installed from the real file worker.");
    }

    private static void Frame()
    {
        if (Session != null) Set(Session!, "_nextRead", long.MaxValue);
        int before = ServerConsoleExecutor.Lines.Count + ServerCommands.Calls.Count + ServerEventRuntime.Saves.Count;
        ServerScheduleRuntime.Tick();
        int after = ServerConsoleExecutor.Lines.Count + ServerCommands.Calls.Count + ServerEventRuntime.Saves.Count;
        Check(after - before <= 2, "Per-frame command dispatch stays bounded at two.");
    }

    private static void Tick()
    {
        string previous = "";
        for (int index = 0; index < 160; ++index)
        {
            if (Session != null)
                foreach (string field in new[] { "_open", "_read", "_writeTask" })
                    if (Get(Session!, field) is Task work)
                    { try { Check(work.Wait(5000), "Bounded file worker completes."); } catch (AggregateException) { /* Runtime observes fault. */ } }
            Frame();
            if (Session == null) return;
            string current = Session == null ? "none" :
                string.Join("|", new[] { "_open", "_read", "_writeTask" }.Select(field => (Get(Session!, field) as Task)?.Id.ToString() ?? "-")) + ":" +
                string.Join(";", Runs.Cast<object>().Select(run => string.Join(",", new[] { "Index", "Started", "PendingDurable", "Finishing", "Retired", "WaitingSave", "WaitingUpgrade", "SaveRequested" }.Select(field => Get(run, field))) +
                    ":" + (Get(run, "Pending") as Task)?.Status));
            if (current == previous) return;
            previous = current;
        }
        throw new Exception("Runtime did not settle within bounded fixture frames.");
    }

    private static void FinishRead()
    {
        Task? pending = Session == null ? null : Get(Session!, "_read") as Task;
        if (pending != null) Check(pending.Wait(5000), "Bounded configuration read completes.");
        Tick();
    }

    private static void ObserveFile()
    {
        Set(Session!, "_nextRead", 0L);
        ServerScheduleRuntime.Tick();
        FinishRead();
    }

    private static void Reload(string yaml)
    {
        File.WriteAllText(FilePath, yaml, new UTF8Encoding(false));
        ObserveFile(); ObserveFile();
    }

    private static void Due(bool dispatch = true)
    {
        ZNet.instance!.Seconds += 1;
        Set(Session!, "_nextPoll", 0L); Frame();
        if (dispatch) Tick();
    }

    private static void Lifecycle()
    {
        Reset(); ZNet.instance = null; Tick();
        Check(Session == null && !Directory.Exists(ServerManagerPlugin.DataRoot), "Menu without network starts no file work.");
        ZNet.instance = new ZNet { Server = false }; Tick();
        Check(Session == null && !Directory.Exists(ServerManagerPlugin.DataRoot), "Remote client starts no schedule or cron file.");
        ZNet.instance.Server = true; ServerEventRuntime.Status.WorldReady = false; Tick();
        Check(Session == null, "Server must finish loading its world.");
        ServerEventRuntime.Status.WorldReady = true; ServerEventRuntime.Status.ShutdownStarted = true; Tick();
        Check(Session == null, "Shutting-down server cannot start schedules.");
        foreach (string conflict in new[] { "blizz.NewCron", "cron_job" })
        {
            Reset(); BepInEx.Bootstrap.Chainloader.PluginInfos[conflict] = new BepInEx.PluginInfo(); Tick();
            Check(Session != null && (bool)Get(Session!, "_suspended")!, "Duplicate scheduler suspends only this service.");
            Check(!Directory.Exists(ServerManagerPlugin.DataRoot) && ServerManagerPlugin.Log.Messages.Any(s => s.Contains("Another Cron")),
                "Duplicate scheduler warns before creating or reading cron.yml.");
        }
        Activate(Config("echo listen")); Due();
        Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { "echo listen" }), "Ordinary listen/server world can execute schedules.");
        object original = Session!;
        ServerEventRuntime.Status.WorldReady = false; Tick();
        Check(Session == null && (bool)Get(original, "_disposed")! && ServerConsoleExecutor.Disposals == 1,
            "Leaving ready world disposes the session and console owner.");
        ServerScheduleRuntime.Stop();
        Check(ServerConsoleExecutor.Disposals == 1, "Repeated shutdown is idempotent.");
    }

    private static void GuardsAndFiles()
    {
        foreach (string verb in new[] { "zones_reset", "vegetation_reset", "locations_reset", "objects_remove", "world_clean", "time_set" })
        foreach (string line in new[] { verb, verb.ToUpperInvariant() + " anything", "\"" + verb + "\"" })
        {
            Check(ServerScheduleRuntime.IsSupportedCommand(line), "Reviewed UW mutation reaches parser admission: " + line);
            var job = ServerScheduleSettings.Parse(Config(line), ServerScheduleRuntime.IsSupportedCommand).Jobs.Single();
            Check(job.Maintenance && job.CatchUp, "Reviewed mutations automatically get maintenance and catch-up: " + line);
        }
        foreach (string field in new[] { "maintenance", "catchUp", "id", "cron", "enabled" })
        foreach (string value in new[] { "true", "false" })
        foreach (string command in new[] { "echo ordinary", "world_clean" })
            Throws<InvalidDataException>(() => ServerScheduleSettings.Parse("jobs:\n" + Job("test", new[] { command }, "    " + field + ": " + value + "\n"),
                ServerScheduleRuntime.IsSupportedCommand), "Removed YAML policy switches are rejected, not silently ignored: " + field + "=" + value);
        foreach (string command in new[] { "start", "stop", "save_disable", "save_enable", "verbose", "uw_check", "upgrade onions", "upgrade future", "sm:world_clean" })
            Throws<InvalidDataException>(() => ServerScheduleSettings.Parse(Config(command), ServerScheduleRuntime.IsSupportedCommand),
                "UW controls and unreviewed variants are not admitted as scheduled commands: " + command);
        Check(ServerScheduleRuntime.IsSupportedCommand("echo zones_reset") && ServerScheduleRuntime.IsSupportedCommand("sm:announce hello"),
            "Guard checks command verb, not innocent argument substrings.");
        Check(!ServerScheduleRuntime.IsSupportedCommand(" ") && !ServerScheduleRuntime.IsSupportedCommand("\"unterminated"), "Empty/malformed commands fail admission.");
        Reset();
        string text = ServerScheduleRuntime.ReadFile(ServerManagerPlugin.DataRoot, true);
        Check(text == ServerScheduleSettings.DefaultYaml, "First read creates the disabled default exactly.");
        Check(ServerScheduleSettings.Parse(text).Jobs.Count == 0, "Generated default remains inert despite its commented maintenance example.");
        byte[] existing = new UTF8Encoding(false).GetBytes("# Existing operator settings must survive template changes.\n" + Config("echo existing") + "# retained tail\n");
        File.WriteAllBytes(FilePath, existing);
        Check(ServerScheduleRuntime.ReadFile(ServerManagerPlugin.DataRoot, true) == Encoding.UTF8.GetString(existing) &&
            File.ReadAllBytes(FilePath).SequenceEqual(existing), "Create-if-missing never replaces or appends examples to existing operator cron.yml bytes.");
        File.WriteAllText(FilePath, "jobs: []", new UTF8Encoding(true));
        Check(ServerScheduleRuntime.ReadFile(ServerManagerPlugin.DataRoot, true) == "jobs: []", "Existing file is not overwritten; UTF8 BOM is stripped.");
        File.WriteAllBytes(FilePath, new byte[] { 0xC3, 0x28 });
        Throws<DecoderFallbackException>(() => ServerScheduleRuntime.ReadFile(ServerManagerPlugin.DataRoot, false), "Invalid UTF8 bytes rejected.");
        File.WriteAllBytes(FilePath, Enumerable.Repeat((byte)' ', ServerScheduleSettings.MaximumFileBytes).ToArray());
        Check(ServerScheduleRuntime.ReadFile(ServerManagerPlugin.DataRoot, false).Length == ServerScheduleSettings.MaximumFileBytes, "Exact file byte bound accepted.");
        File.WriteAllBytes(FilePath, new byte[ServerScheduleSettings.MaximumFileBytes + 1]);
        Throws<InvalidDataException>(() => ServerScheduleRuntime.ReadFile(ServerManagerPlugin.DataRoot, false), "Oversized file rejected.");
        string link = Path.Combine(Environment.CurrentDirectory, "reparse-root");
        if (Directory.Exists(link))
        {
            Throws<InvalidDataException>(() => ServerScheduleRuntime.ReadFile(link, true), "Reparse root rejected before file creation.");
            Throws<InvalidDataException>(() => ServerScheduleRuntime.ReadFile(Path.Combine(link, "child"), true), "Reparse ancestor rejected before directory creation.");
            Check(!File.Exists(Path.Combine(Environment.CurrentDirectory, "reparse-target", "cron.yml")), "Rejected link did not write its target.");
        }
        else Console.WriteLine("SKIP: optional directory reparse fixture was unavailable.");
    }

    private static void AlternateConfiguration()
    {
        Reset(); Directory.CreateDirectory(ServerManagerPlugin.DataRoot);
        string alternate = Path.Combine(ServerManagerPlugin.DataRoot, "cron.yaml");
        byte[] configured = Encoding.UTF8.GetBytes(Config("echo alternate"));
        File.WriteAllBytes(alternate, configured);
        Tick(); FinishRead();
        Check(Session != null && Get(Session!, "_settings") != null && !File.Exists(FilePath) &&
            File.ReadAllBytes(alternate).SequenceEqual(configured), "Alternate-only cron.yaml loads in place without migration or default-file creation.");
        Due(); Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { "echo alternate" }), "Alternate-only configuration dispatches its actual commands.");
        object settings = Get(Session!, "_settings")!;
        File.Copy(alternate, FilePath);
        Throws<InvalidDataException>(() => ServerScheduleRuntime.ReadFile(ServerManagerPlugin.DataRoot, true), "Both config suffixes reject even when identical.");
        ObserveFile(); ObserveFile();
        Check(ReferenceEquals(settings, Get(Session!, "_settings")) && File.ReadAllBytes(FilePath).SequenceEqual(configured) &&
            File.ReadAllBytes(alternate).SequenceEqual(configured), "Ambiguous suffix reload preserves last-good settings and both operator files.");
        Due(); Check(ServerConsoleExecutor.Lines.Count == 2, "Last-good configuration continues after ambiguous suffix rejection.");
        File.Delete(FilePath);
        File.WriteAllText(alternate, Config("echo updated-alternate"), new UTF8Encoding(false));
        ObserveFile(); ObserveFile(); Due();
        Check(!File.Exists(FilePath) && !ReferenceEquals(settings, Get(Session!, "_settings")) && ServerConsoleExecutor.Lines.Last() == "echo updated-alternate",
            "Valid alternate-only edits hot reload after ambiguity is removed.");
    }

    private static void CanonicalJournalStartup()
    {
        Reset(); Tick(); FinishRead();
        string canonical = Path.Combine(ServerManagerPlugin.DataRoot, "cron_last.yml");
        string alternate = Path.Combine(ServerManagerPlugin.DataRoot, "cron_last.yaml");
        Check(Session != null && ((ServerScheduleSettings)Get(Session!, "_settings")!).Jobs.Count == 0 &&
            File.ReadAllText(FilePath) == ServerScheduleSettings.DefaultYaml, "Fresh runtime installs the exact default with zero jobs.");
        Check(ServerScheduleJournal.FileName == "cron_last.yml" && File.Exists(canonical) && !File.Exists(alternate),
            "Fresh runtime creates the canonical .yml journal and no .yaml journal.");
        Check(ServerConsoleExecutor.Lines.Count == 0 && ServerCommands.Calls.Count == 0 && ServerEventRuntime.Saves.Count == 0 &&
            UpgradeWorldScheduleBridge.Begun.Count == 0, "Default startup dispatches neither maintenance commands nor saves.");
        byte[] configuration = File.ReadAllBytes(FilePath);
        ServerScheduleRuntime.Stop();
        string marker = Path.Combine(ServerManagerPlugin.DataRoot, ".cron-writer.lock");
        byte[] initialized = File.ReadAllBytes(marker), progress = File.ReadAllBytes(canonical);
        Check(initialized.Length != 0, "Runtime fixture retains an initialized writer marker.");
        File.Move(canonical, alternate);
        Tick(); FinishRead();
        Check(Session != null && !(bool)Get(Session!, "_suspended")! && !File.Exists(canonical) && File.Exists(alternate),
            "Initialized alternate-only journal is reopened in place without losing history or creating a second suffix.");
        ServerScheduleRuntime.Stop();
        progress = File.ReadAllBytes(alternate);
        File.Copy(alternate, canonical);
        Tick(); FinishRead();
        Check(ServerScheduleRuntime.GetStatus().Code == "cron_suspended", "Both journal suffixes suspend startup instead of selecting a history.");
        Check(File.ReadAllBytes(canonical).SequenceEqual(progress) && File.ReadAllBytes(alternate).SequenceEqual(progress) &&
            File.ReadAllBytes(marker).SequenceEqual(initialized) && File.ReadAllBytes(FilePath).SequenceEqual(configuration),
            "Ambiguous startup preserves both journals, initialized marker and configuration bytes.");
        Check(ServerConsoleExecutor.Lines.Count == 0 && ServerCommands.Calls.Count == 0 && ServerEventRuntime.Saves.Count == 0 &&
            UpgradeWorldScheduleBridge.Begun.Count == 0, "Fail-closed startup cannot execute any scheduled work.");
        ServerScheduleRuntime.Stop(); File.Delete(canonical); File.Delete(alternate);
        Tick(); FinishRead();
        Check(ServerScheduleRuntime.GetStatus().Code == "cron_suspended" && !File.Exists(canonical) && !File.Exists(alternate),
            "Initialized startup with neither journal fails closed rather than resetting durable progress.");
    }

    private static void RoutingAndBudget()
    {
        Activate(Config("echo \"hello world\"", "announce hello", "sm:status", "ban someone", "unban someone"));
        Due();
        for (int index = 0; index < 7; ++index) Tick();
        Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { "echo \"hello world\"" }), "Raw console routing preserves full quoted command.");
        Check(ServerCommands.Calls.Select(call => call.Line).SequenceEqual(new[] { "announce hello", "status", "ban someone", "unban someone" }),
            "Known, explicitly sm-prefixed and ban/unban commands use the common queue.");
        Check(ServerCommands.Calls.All(call => call.Caller.Source == "cron" && call.Caller.Id == JobId()), "Common commands carry generated cron job identity.");
        Check(ServerEventRuntime.Audits.Count(a => a.Verb == "echo") == 1 &&
            ServerEventRuntime.Audits.All(a => a.Verb == "echo" || a.Verb == "schedule"), "Runtime audits raw command plus job outcome; common queue owns common command audits.");
        ServerEventRuntime.Audit cron = ServerEventRuntime.Audits.Single(a => a.Verb == "schedule");
        Check(cron.Data["cron_schedule"] == "* * * * * *" && cron.Data["cron_command_count"] == "5" &&
            cron.Data["cron_summary"] == "echo → announce: hello → status → ban → unban" &&
            !cron.Data.ContainsKey("cron_verb"),
            "Final cron audit carries one bounded display summary while hiding non-announcement command arguments.");
        Check(Runs.Count == 0 && Claims == 0, "Successful command chain releases its occurrence claim.");

        Activate(Config("echo private-fixture-argument")); Due();
        Check(ServerEventRuntime.Audits.All(a => a.Source == "cron" && a.Id == JobId() && a.Name == JobId() &&
            a.Target.Length == 0 && !string.Join(" ", a.Source, a.Id, a.Name, a.Verb, a.Code, a.Target).Contains("private-fixture-argument")),
            "command.executed audit forwards bounded job identity/verb/result, not raw command arguments.");
        cron = ServerEventRuntime.Audits.Single(a => a.Verb == "schedule");
        Check(cron.Data["cron_verb"] == "echo" && cron.Data["cron_summary"] == "echo" &&
            !string.Join(" ", cron.Data.Values).Contains("private-fixture-argument"),
            "Single-command cron display metadata retains only a private command's verb.");

        Activate("jobs:\n" + string.Concat(Enumerable.Range(0, 5).Select(i => Job("j" + i, new[] { "echo " + i }))));
        Due(); Check(ServerConsoleExecutor.Lines.Count == 5 && Claims == 0, "Durable jobs drain across bounded frames (each Frame checks global budget).");

        Activate(Config("echo first", "echo forbidden-tail"));
        ServerConsoleExecutor.NextResult = new(false, "rcon_failed", "failed"); Due(); Tick(); Tick();
        Check(ServerConsoleExecutor.Lines.Count == 1 && Claims == 0 && ServerManagerPlugin.Log.Messages.Any(s => s.Contains("No automatic retry")),
            "Failure stops the chain and does not replay the failed occurrence.");

        Activate(Config("announce pending", "echo after"));
        var pending = new TaskCompletionSource<ServerManagerCommandResult>(); ServerCommands.NextTask = pending.Task;
        Due(); Due(); Due();
        Check(ServerCommands.Calls.Count == 1 && Runs.Count == 1 && Claims == 1, "Unfinished common command prevents same-ID overlap.");
        pending.SetException(new InvalidOperationException("expected fixture")); Tick();
        Check(Runs.Count == 0 && ServerConsoleExecutor.Lines.Count == 0, "Task fault stops chain without executing tail.");
    }

    private static void Gates()
    {
        Activate("jobs:\n" + Job("test", new[] { "echo gated" }, "    globalKeys: required\n    bannedGlobalKeys: banned\n"));
        Due(); Check(ServerConsoleExecutor.Lines.Count == 0 && Claims == 0, "Missing global key consumes and skips occurrence.");
        ZoneSystem.instance!.Keys.Add("required"); ZoneSystem.instance.Keys.Add("banned"); Due();
        Check(ServerConsoleExecutor.Lines.Count == 0 && Claims == 0, "Banned key consumes and skips occurrence.");
        ZoneSystem.instance.Keys.Remove("banned"); Due(); Check(ServerConsoleExecutor.Lines.Count == 1, "Allowed key set dispatches.");
        Activate("jobs:\n" + Job("test", new[] { "echo chance" }, "    chance: 0\n")); Due();
        Check(ServerConsoleExecutor.Lines.Count == 0 && Claims == 0, "Zero chance skips without retaining claim.");
        Activate(Config("echo missing-zone")); ZoneSystem.instance = null; Due();
        Check(ServerConsoleExecutor.Lines.Count == 0 && Claims == 0, "Missing ZoneSystem fails key gate safely.");
    }

    private static void PollingAndLogging()
    {
        Activate("interval: 30\n" + Config("echo interval"));
        long initialDeadline = (long)Get(Session!, "_nextPoll")!;
        ZNet.instance!.Seconds += 1; Frame();
        Check(ServerConsoleExecutor.Lines.Count == 0 && (long)Get(Session!, "_nextPoll")! == initialDeadline,
            "Game-clock advancement does not bypass the configured real-time polling interval.");
        long before = Stopwatch.GetTimestamp();
        Set(Session!, "_nextPoll", 0L); Frame();
        long after = Stopwatch.GetTimestamp(), deadline = (long)Get(Session!, "_nextPoll")!;
        Check(deadline >= before + 30 * Stopwatch.Frequency && deadline <= after + 30 * Stopwatch.Frequency,
            "The next poll is scheduled using the configured thirty-second interval.");
        Tick();
        Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { "echo interval" }), "An admitted poll executes the pending occurrence once.");
        Reload("interval: 2\n" + Config("echo updated-interval"));
        double remaining = ((long)Get(Session!, "_nextPoll")! - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
        Check(((ServerScheduleSettings)Get(Session!, "_settings")!).IntervalSeconds == 2 && remaining > 0 && remaining <= 2.1,
            "Hot reload replaces the old poll deadline with the updated interval.");
        Due(); Check(ServerConsoleExecutor.Lines.Last() == "echo updated-interval", "The reloaded polling configuration remains executable.");

        foreach (bool logJobs in new[] { false, true })
        foreach (bool? jobLog in new bool?[] { null, false, true })
        {
            string extra = jobLog.HasValue ? "    log: " + jobLog.Value.ToString().ToLowerInvariant() + "\n" : "";
            Activate("logJobs: " + logJobs.ToString().ToLowerInvariant() + "\njobs:\n" + Job("logging", new[] { "echo logged" }, extra));
            ServerManagerPlugin.Log.Messages.Clear(); Due();
            Check(ServerManagerPlugin.Log.Messages.Any(line => line.Contains("completed.")) == (jobLog ?? logJobs),
                "Per-job log override or root default controls only successful completion console messages.");
            Check(ServerEventRuntime.Audits.Any(a => a.Code == "cron_completed" && a.Success), "Success audit remains enabled independently of console logging.");
        }
        foreach (bool logSkipped in new[] { false, true })
        foreach (bool jobLog in new[] { false, true })
        {
            Activate("logJobs: false\nlogSkipped: " + logSkipped.ToString().ToLowerInvariant() + "\njobs:\n" +
                Job("skipped", new[] { "echo skipped" }, "    chance: 0\n    log: " + jobLog.ToString().ToLowerInvariant() + "\n"));
            ServerManagerPlugin.Log.Messages.Clear(); Due();
            Check(ServerManagerPlugin.Log.Messages.Any(line => line.Contains("skipped (")) == (jobLog && logSkipped),
                "Skipped console messages require both effective job logging and root logSkipped.");
            Check(ServerEventRuntime.Audits.Any(a => a.Code == "cron_skipped" && a.Success) && ServerConsoleExecutor.Lines.Count == 0,
                "Skipped jobs remain audited and never execute when console logging is disabled.");
        }
        Activate("logJobs: false\nlogSkipped: false\n" + Config("echo failure"));
        ServerManagerPlugin.Log.Messages.Clear();
        ServerConsoleExecutor.NextResult = new(false, "fixture_failed", "failed"); Due();
        Check(ServerManagerPlugin.Log.Messages.Any(line => line.Contains("stopped (fixture_failed)")) &&
            ServerEventRuntime.Audits.Any(a => a.Code == "fixture_failed" && !a.Success),
            "Logging flags cannot hide failed-command warnings or failure audit records.");
    }

    private static void Saves()
    {
        Activate(Config("save", "echo after-save"));
        ServerEventRuntime.Status.LatestSave = Save("old", ServerManagerSaveState.CheckpointCompleted);
        Due();
        Check(ServerEventRuntime.Saves.Count == 1 && ServerCommands.Calls.Count == 0 && ServerConsoleExecutor.Lines.Count == 0,
            "Save uses the typed event queue, never raw console or common command routing.");
        Tick(); Check(Runs.Count == 1 && ServerEventRuntime.Audits.Count == 0, "Request acceptance is not completion and baseline checkpoint is ignored.");
        ServerEventRuntime.Status.LatestSave = Save("new", ServerManagerSaveState.InProgress); Tick();
        Check(Runs.Count == 1 && ServerConsoleExecutor.Lines.Count == 0, "In-progress save holds its job before next command.");
        ServerEventRuntime.Status.LatestSave = Save("new", ServerManagerSaveState.CheckpointCompleted); Tick(); Tick();
        Check(ServerEventRuntime.Audits.Any(a => a.Code == "checkpoint_completed" && a.Success) &&
            ServerConsoleExecutor.Lines.SequenceEqual(new[] { "echo after-save" }), "Full world/retained-character checkpoint releases next command.");

        foreach (string outcome in new[] { "partial", "pending", "failed", "replaced", "timeout" })
        {
            Activate(Config("save", "echo must-not-run"));
            ServerEventRuntime.NextSave = Task.FromResult(new ServerManagerCommandResult(true, "save_requested", "accepted", "current"));
            Due(); Tick();
            if (outcome == "timeout") Set(Runs[0]!, "Deadline", 0L);
            else
            {
                var snapshot = Save(outcome == "replaced" ? "different" : "current",
                    outcome == "failed" ? ServerManagerSaveState.Failed : ServerManagerSaveState.CheckpointCompleted);
                if (outcome == "partial") snapshot.CharacterCommitScope = ServerManagerCharacterCommitScope.Partial;
                if (outcome == "pending") snapshot.PendingCharacterCount = 1;
                ServerEventRuntime.Status.LatestSave = snapshot;
            }
            Tick(); Tick();
            string code = outcome == "timeout" ? "save_unconfirmed" : outcome == "failed" ? "save_failed" :
                outcome == "replaced" ? "save_observation_lost" : "checkpoint_partial";
            Check(ServerEventRuntime.Audits.Any(a => a.Code == code && !a.Success), "Save outcome is truthfully audited: " + outcome);
            Check(ServerEventRuntime.Saves.Count == 1 && ServerConsoleExecutor.Lines.Count == 0 && Claims == 0,
                "Unconfirmed/failed/partial save never retries or runs its tail: " + outcome);
        }
        Activate(Config("save extra", "echo no")); Due(); Tick();
        Check(ServerEventRuntime.Saves.Count == 0 && ServerConsoleExecutor.Lines.Count == 0 && Claims == 0, "Malformed save is rejected before enqueue.");
        Activate("jobs:\n" + Job("a", new[] { "save", "echo first-save-tail" }) + Job("b", new[] { "save", "echo second-save-tail" }));
        ZNet.instance!.Saving = true; Due(); Check(ServerEventRuntime.Saves.Count == 0, "Observed vanilla save delays schedule save admission.");
        ZNet.instance.Saving = false; Tick(); Tick();
        Check(ServerEventRuntime.Saves.Count == 1, "Two save jobs cannot request overlapping checkpoints.");
    }

    private static void ReloadAndRetirement()
    {
        Activate(Config("echo old")); object settings = Get(Session!, "_settings")!;
        File.WriteAllText(FilePath, Config("echo new"), new UTF8Encoding(false)); ObserveFile();
        Check(ReferenceEquals(settings, Get(Session!, "_settings")), "One changed-file observation does not replace good snapshot.");
        ObserveFile(); Check(!ReferenceEquals(settings, Get(Session!, "_settings")), "Second stable observation installs validated settings.");
        Due(); Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { "echo new" }), "Updated schedule dispatches new command.");
        settings = Get(Session!, "_settings")!; Reload("jobs: [broken");
        Check(ReferenceEquals(settings, Get(Session!, "_settings")), "Malformed YAML retains last valid runtime snapshot.");
        Due(); Check(ServerConsoleExecutor.Lines.Count == 2 && ServerConsoleExecutor.Lines.Last() == "echo new", "Good schedule continues after malformed edit.");
        File.WriteAllBytes(FilePath, new byte[] { 0xC3, 0x28 }); ObserveFile();
        Check(ReferenceEquals(settings, Get(Session!, "_settings")), "Read/UTF8 error also retains good snapshot.");

        Activate(Config("announce pending", "echo retired-tail"));
        var pending = new TaskCompletionSource<ServerManagerCommandResult>(); ServerCommands.NextTask = pending.Task;
        Due(); var oldCaller = ServerCommands.Calls.Single().Caller;
        Reload(Config("echo replacement"));
        Check(oldCaller.Cancellation.IsCancellationRequested && !oldCaller.IsAuthorized(), "Reload immediately cancels and deauthorizes old common work.");
        Due(); Check(ServerConsoleExecutor.Lines.Count == 0 && Claims == 1, "Retired incomplete task retains its claim and blocks replacement settings even when generated identity changes.");
        pending.SetResult(new(true, "ok", "done")); Tick(); Due();
        Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { "echo replacement" }), "Retired task completion never dispatches old tail; future replacement runs.");

        Activate(Config("save", "echo retired-save-tail"));
        var savePending = new TaskCompletionSource<ServerManagerCommandResult>(); ServerEventRuntime.NextSave = savePending.Task;
        Due(); var saveCall = ServerEventRuntime.Saves.Single(); Reload(Config("echo after-retired-save"));
        Check(saveCall.Cancellation.IsCancellationRequested && !saveCall.Authorized(), "Reload retires queued save authorization.");
        savePending.SetResult(new(true, "save_requested", "accepted", "retired-save")); Tick(); Due();
        Check(Claims == 1 && ServerConsoleExecutor.Lines.Count == 0, "Accepted retired save retains claim until observed checkpoint, even if acceptance completes after reload.");
        ServerEventRuntime.Status.LatestSave = Save("retired-save", ServerManagerSaveState.CheckpointCompleted); Tick(); Due();
        Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { "echo after-retired-save" }) &&
            ServerEventRuntime.Audits.Any(a => a.Code == "checkpoint_completed"), "Retired save is observed/audited without executing retired tail.");

        Activate(Config("announce old-world", "echo stale"));
        pending = new TaskCompletionSource<ServerManagerCommandResult>(); ServerCommands.NextTask = pending.Task; Due();
        oldCaller = ServerCommands.Calls.Single().Caller; object oldSession = Session!;
        ServerEventRuntime.CommandWorldEpoch++; Tick(); FinishRead();
        Check(!ReferenceEquals(oldSession, Session) && oldCaller.Cancellation.IsCancellationRequested && !oldCaller.IsAuthorized(),
            "Epoch replacement cancels old work and makes captured callbacks stale.");
        pending.SetException(new InvalidOperationException("retired fixture fault")); Tick();
        Check(ServerConsoleExecutor.Lines.Count == 0, "Late old-world completion cannot mutate new-world dispatch.");
        ServerScheduleRuntime.Stop(); Check(Session == null, "Explicit stop releases session after pending callback retirement.");
    }

    private static ServerManagerSaveOperationSnapshot Save(string id, ServerManagerSaveState state) => new()
    { OperationId = id, State = state, CharacterCommitScope = ServerManagerCharacterCommitScope.AllRetainedShadowsAtCutoff };

    private static string JournalPath => Path.Combine(ServerManagerPlugin.DataRoot, ServerScheduleJournal.FileName);
    private static string JournalState(string? id = null, long? world = null)
    {
        id ??= JobId();
        YamlStream yaml = new(); using (var reader = File.OpenText(JournalPath)) yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var worlds = (YamlMappingNode)root.Children[new YamlScalarNode("worlds")];
        var jobs = (YamlMappingNode)worlds.Children[new YamlScalarNode((world ?? ZNet.instance!.WorldUid).ToString(System.Globalization.CultureInfo.InvariantCulture))];
        var job = (YamlMappingNode)jobs.Children[new YamlScalarNode(id)];
        return ((YamlScalarNode)job.Children[new YamlScalarNode("state")]).Value!;
    }
    private static void Restart(double seconds, long? world = null)
    {
        long uid = world ?? ZNet.instance!.WorldUid;
        object? old = Session;
        ServerScheduleRuntime.Stop();
        if (old != null && Get(old, "_writeTask") is Task write)
        { try { write.Wait(5000); } catch (AggregateException) { } }
        ServerEventRuntime.CommandWorldEpoch++;
        ServerEventRuntime.Status = new ServerManagerStatusSnapshot { WorldReady = true };
        ZNet.instance = new ZNet { WorldUid = uid, Seconds = seconds };
        Tick(); FinishRead();
    }

    private static void DurableDispatchAndCatchup()
    {
        Activate(Config("echo first"));
        bool durable = false;
        ServerConsoleExecutor.OnExecute = () => durable = JournalState() == "running";
        Due();
        Check(durable && JournalState() == "ready", "Running marker is durable before first command; completed cursor follows execution.");

        Activate(Config("echo must-not-run"));
        using (new FileStream(JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { Due(false); Tick(); }
        Check(ServerConsoleExecutor.Lines.Count == 0 && ServerScheduleRuntime.GetStatus().Code == "cron_suspended",
            "Journal read/write failure suspends dispatch before the first command.");

        foreach (string command in new[] { "echo offline", "objects_count" })
        {
            Activate(Config(command));
            Restart(60);
            Check(ServerConsoleExecutor.Lines.Count == 0 && ServerEventRuntime.Saves.Count == 0,
                "Ordinary and query jobs skip missed offline occurrences without extra saves: " + command);
            Tick(); Check(ServerConsoleExecutor.Lines.Count == 0, "Skipped offline occurrence never becomes a replay backlog.");
        }

        Activate(Maintenance("world_clean")); Restart(60);
        Check(ServerEventRuntime.Saves.Count == 1 && JournalState() == "running", "Automatically classified mutation coalesces its offline backlog once into pre-save.");
        Tick(); Check(ServerEventRuntime.Saves.Count == 1 && UpgradeWorldScheduleBridge.Begun.Count == 0,
            "Automatic mutation catch-up neither duplicates the occurrence nor bypasses its checkpoint.");

        Activate(Maintenance("zones_reset start")); UpgradeWorldScheduleBridge.Idle = false; Due();
        Check(JournalState() == "pending" && ServerEventRuntime.Saves.Count == 0, "Foreign Upgrade World work keeps occurrence pending before any dispatch.");
        UpgradeWorldScheduleBridge.Idle = true; Restart(60);
        Check(ServerEventRuntime.Saves.Count == 1 && JournalState() == "running", "Never-started pending maintenance can resume once after restart.");

        Activate(Config("echo baseline")); long firstWorld = ZNet.instance!.WorldUid;
        Restart(60, firstWorld + 1);
        Check(ServerConsoleExecutor.Lines.Count == 0 && JournalState(world: firstWorld) == "ready" && JournalState() == "ready",
            "A second world receives its own baseline and does not replay the first world's jobs.");
    }

    private static void AutomaticUpgradeWorldPolicies()
    {
        foreach (string command in new[] { "objects_remove start", "objects_edit data=x,1,int start", "chests_reset start",
            "locations_add start", "vegetation_add start", "zones_generate start", "world_reset start", "world_gen mistlands start",
            "temple_gen ashlands start", "upgrade tarpits start", "clean_chests", "world_clean", "locations_swap old,new",
            "location_register StartTemple 1,2,3", "locations_fix", "time_set 1000" })
        {
            Activate(Config(command)); Due();
            Check(ServerEventRuntime.Saves.Count == 1 && UpgradeWorldScheduleBridge.Begun.Count == 0 && ServerConsoleExecutor.Lines.Count == 0,
                "Every reviewed queued/immediate mutation waits for its automatic pre-save: " + command);
            ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted); Tick();
            Check(UpgradeWorldScheduleBridge.Begun.SequenceEqual(new[] { command }) && ServerConsoleExecutor.Lines.SequenceEqual(new[] { command }),
                "Reviewed mutation routes exactly once through the UW boundary: " + command);
            UpgradeWorldScheduleBridge.Observation = new(UpgradeWorldScheduleState.Succeeded, "uw_operation_completed"); Tick();
            Check(ServerEventRuntime.Saves.Count == 2 && JournalState() == "running", "Mutation requests a single post-save without claiming completion: " + command);
            ServerEventRuntime.Status.LatestSave = Save("post", ServerManagerSaveState.CheckpointCompleted); Tick();
            Check(JournalState() == "ready" && Claims == 0 && Runs.Count == 0, "Full post-save durably completes reviewed mutation: " + command);
        }
        foreach (string command in new[] { "biomes_count 100", "objects_count", "objects_list", "locations_count", "locations_list", "chests_search Wood" })
        {
            Activate(Config(command));
            var job = ((ServerScheduleSettings)Get(Session!, "_settings")!).Jobs.Single();
            Check(!job.Maintenance && !job.CatchUp, "Queries derive no maintenance or offline catch-up: " + command);
            Due();
            Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { command }) && UpgradeWorldScheduleBridge.Begun.Count == 0 && ServerEventRuntime.Saves.Count == 0,
                "Read-only UW query dispatches without implicit saves or mutation lifecycle: " + command);
            Restart(60);
            Check(ServerConsoleExecutor.Lines.Count == 1 && ServerEventRuntime.Saves.Count == 0, "Read-only UW query does not replay its missed offline backlog: " + command);
        }
    }

    private static void UntrackedUpgrade()
    {
        Activate(Maintenance("zones_generate start"));
        BepInEx.Bootstrap.Chainloader.PluginInfos["upgrade_world"].Metadata.Version = new Version(1, 82);
        UpgradeWorldScheduleBridge.Available = false;
        UpgradeWorldScheduleBridge.FailureCode = "uw_unsupported_contract";
        Due();
        Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { "zones_generate start" }) &&
            ServerEventRuntime.Saves.Count == 0 && UpgradeWorldScheduleBridge.Begun.Count == 0,
            "Single incompatible-contract command dispatches once without implicit saves or observer.");
        Check(Runs.Count == 0 && JournalState() == "ready" && Claims == 0,
            "Dispatch-only occurrence finishes durably without a retry claim.");
        Check(ServerEventRuntime.Audits.Last().Code == "cron_dispatched_untracked" && ServerEventRuntime.Audits.Last().Success,
            "Durable final result reports dispatch, never operation completion.");
        Tick(); Check(ServerConsoleExecutor.Lines.Count == 1, "No retry after untracked dispatch.");
        foreach (string[] commands in new[] { new[] { "zones_generate start", "save" }, new[] { "zones_generate start", "zones_reset start" } })
        {
            Activate(Maintenance(commands));
            UpgradeWorldScheduleBridge.Available = false;
            UpgradeWorldScheduleBridge.FailureCode = "uw_unsupported_contract";
            Due();
            Check(ServerConsoleExecutor.Lines.Count == 0 && ServerEventRuntime.Saves.Count == 0,
                "Unobservable sequences stop before any command or implicit save.");
        }
        Activate(Maintenance("zones_generate start"));
        UpgradeWorldScheduleBridge.Available = false;
        UpgradeWorldScheduleBridge.FailureCode = "uw_observer_cleanup_failed";
        Due();
        Check(ServerConsoleExecutor.Lines.Count == 0, "Unsafe observer failures never enable fallback.");
    }

    private static void MaintenanceSequence()
    {
        Activate(Maintenance("zones_reset start", "vegetation_reset start"));
        Due();
        Check(ServerEventRuntime.Saves.Count == 1 && UpgradeWorldScheduleBridge.Begun.Count == 0, "Implicit pre-save precedes all resets.");
        ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted);
        bool markerObserved = false;
        UpgradeWorldScheduleBridge.BeforeBegin = () => markerObserved = JournalState() == "running";
        Tick();
        Check(markerObserved && UpgradeWorldScheduleBridge.Begun.SequenceEqual(new[] { "zones_reset start" }),
            "First reset starts only after full pre-save and durable running marker.");
        Tick(); Check(UpgradeWorldScheduleBridge.Begun.Count == 1, "Pending operation excludes the next destructive step.");
        UpgradeWorldScheduleBridge.Observation = new(UpgradeWorldScheduleState.Succeeded, "uw_operation_completed");
        Tick();
        Check(UpgradeWorldScheduleBridge.Begun.SequenceEqual(new[] { "zones_reset start", "vegetation_reset start" }) &&
            ServerEventRuntime.Saves.Count == 2 && JournalState() == "running", "Sequential resets complete before post-save; journal is not complete at request acceptance.");
        ServerEventRuntime.Status.LatestSave = Save("post", ServerManagerSaveState.CheckpointCompleted); Tick();
        Check(JournalState() == "ready" && Runs.Count == 0 && ServerEventRuntime.Audits.Any(a => a.Verb == "maintenance" && a.Code == "cron_completed" && a.Success),
            "Only completed post-save releases the maintenance marker and publishes completion.");

        Activate(Maintenance("save", "zones_reset start", "save")); Due();
        Check(((string[])Get(Runs[0]!, "Commands")!).Length == 3, "Explicit leading/trailing saves are not duplicated.");
        ServerEventRuntime.Status.LatestSave = Save("pre-failed", ServerManagerSaveState.Failed); Tick();
        Check(UpgradeWorldScheduleBridge.Begun.Count == 0 && JournalState() == "needs_review", "Failed pre-save prevents resets and never reports maintenance success.");
    }

    private static void UpgradeWorldOwnership()
    {
        foreach (string command in new[] { "objects_remove start", "world_clean", "objects_count" })
        foreach (string failure in new[] { "missing_plugin", "missing_command", "foreign_handler", "mixed_handler", "no_handler" })
        {
            Activate(Config(command));
            string verb = command.Split(' ')[0];
            switch (failure)
            {
                case "missing_plugin": BepInEx.Bootstrap.Chainloader.PluginInfos.Remove("upgrade_world"); break;
                case "missing_command": ValheimPrivateAccess.Commands.Remove(verb); break;
                case "foreign_handler": ValheimPrivateAccess.Commands[verb] = new Terminal.ConsoleCommand(GC.Collect); break;
                case "mixed_handler": ValheimPrivateAccess.Commands[verb] = new Terminal.ConsoleCommand((Action)FixtureUpgradeWorld.NoOp + GC.Collect); break;
                case "no_handler": ValheimPrivateAccess.Commands[verb] = new Terminal.ConsoleCommand((Action?)null); break;
            }
            Due();
            Check(ServerConsoleExecutor.Lines.Count == 0 && UpgradeWorldScheduleBridge.Begun.Count == 0 && ServerEventRuntime.Saves.Count == 0,
                "Known UW command ownership/version preflight prevents dispatch and unnecessary saves: " + command + "/" + failure);
            Check(JournalState() == "ready" && Claims == 0, "Denied ownership does not leave an automatic retry or claimed mutation: " + command + "/" + failure);
        }
        Activate(Config("uw_future_mutation"));
        ValheimPrivateAccess.Commands["uw_future_mutation"] = new Terminal.ConsoleCommand(FixtureUpgradeWorld.NoOp);
        Due();
        Check(ServerConsoleExecutor.Lines.Count == 0 && ServerEventRuntime.Saves.Count == 0 && JournalState() == "ready",
            "Unreviewed command actually owned by UW cannot bypass catalog classification as an ordinary raw command.");

        Activate(Config("ordinary_plugin_command"));
        ValheimPrivateAccess.Commands["ordinary_plugin_command"] = new Terminal.ConsoleCommand(GC.Collect);
        Due();
        Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { "ordinary_plugin_command" }) && ServerEventRuntime.Saves.Count == 0,
            "A genuinely non-UW raw command remains an ordinary scheduled command.");

        Activate(Config("world_clean")); Due();
        ValheimPrivateAccess.Commands["world_clean"] = new Terminal.ConsoleCommand(GC.Collect);
        ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted); Tick();
        Check(ServerConsoleExecutor.Lines.Count == 0 && ServerEventRuntime.Saves.Count == 1 && JournalState() == "needs_review",
            "Handler replacement after preflight is detected again before the mutation, with no post-save or false success.");

        Activate(Config("objects_count"));
        ValheimPrivateAccess.Commands["objects_count"] = new Terminal.ConsoleCommand(FixtureUpgradeWorld.True);
        Due();
        Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { "objects_count" }) && ServerEventRuntime.Saves.Count == 0,
            "Owned failable UW callback is recognized through the second registry delegate field.");
    }

    private static void SynchronousMutationAbort()
    {
        Activate(Config("world_clean", "clean_chests")); Due();
        ServerConsoleExecutor.NextResult = new(false, "rcon_failed", "failed");
        ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted); Tick();
        Check(UpgradeWorldScheduleBridge.Begun.SequenceEqual(new[] { "world_clean" }) && ServerEventRuntime.Saves.Count == 1 && JournalState() == "needs_review",
            "Synchronous mutation failure blocks its next mutation and post-save behind durable review.");

        Activate(Config("world_clean", "clean_chests")); Due();
        var capturedSave = ServerEventRuntime.Saves.Single();
        ServerConsoleExecutor.OnExecute = ServerScheduleRuntime.Stop;
        ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted); Tick();
        Check(Session == null && UpgradeWorldScheduleBridge.Begun.SequenceEqual(new[] { "world_clean" }) &&
            ServerConsoleExecutor.Lines.SequenceEqual(new[] { "world_clean" }) && ServerEventRuntime.Saves.Count == 1,
            "Synchronous world stop during UW dispatch cannot resume a disposed run, execute a tail, or enqueue a post-save.");
        Check(capturedSave.Cancellation.IsCancellationRequested && !capturedSave.Authorized() &&
            !ServerEventRuntime.Audits.Any(a => a.Verb == "maintenance" && a.Success) &&
            !ServerManagerPlugin.Log.Messages.Any(s => s.Contains("unexpected error")),
            "Synchronous abort revokes captured authority without auditing false completion or throwing on disposed cancellation state.");
        Restart(60);
        Check(JournalState() == "needs_review" && ServerEventRuntime.Saves.Count == 1 && UpgradeWorldScheduleBridge.Begun.Count == 1,
            "A synchronously aborted mutation becomes review-required after restart and is never replayed automatically.");
    }

    private static void MaintenanceFailureAndRecovery()
    {
        foreach (var failure in new[] {
            new UpgradeWorldScheduleObservation(UpgradeWorldScheduleState.Failed, "uw_partial_failure"),
            new UpgradeWorldScheduleObservation(UpgradeWorldScheduleState.Indeterminate, "uw_stopped"),
            new UpgradeWorldScheduleObservation(UpgradeWorldScheduleState.Indeterminate, "uw_foreign_operation") })
        {
            Activate(Maintenance("zones_reset start", "locations_reset start")); Due();
            ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted); Tick();
            UpgradeWorldScheduleBridge.Observation = failure; Tick();
            Check(UpgradeWorldScheduleBridge.Begun.Count == 1 && ServerEventRuntime.Saves.Count == 1 && JournalState() == "needs_review",
                "Failed/stopped/foreign operation prevents tail and records needs_review: " + failure.Code);
            Due(); Check(UpgradeWorldScheduleBridge.Begun.Count == 1 && ServerScheduleRuntime.GetStatus().Message.Contains(JobId()),
                "Review marker suppresses future automatic execution and is visible in cronstatus.");
        }

        Activate(Maintenance("zones_reset start")); Due();
        ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted); Tick();
        int calls = ServerEventRuntime.Saves.Count;
        Restart(60);
        Check(JournalState() == "needs_review" && ServerEventRuntime.Saves.Count == calls && UpgradeWorldScheduleBridge.Begun.Count == 1,
            "Restart never replays a maintenance job that was already running.");
        Task<ServerManagerCommandResult> denied = ServerScheduleRuntime.Acknowledge(JobId(), () => false, CancellationToken.None);
        Check(!denied.Result.Success && JournalState() == "needs_review", "Revoked operator cannot clear the marker.");
        UpgradeWorldScheduleBridge.Idle = false;
        denied = ServerScheduleRuntime.Acknowledge(JobId(), () => true, CancellationToken.None);
        Check(denied.Result.Code == "cron_busy" && JournalState() == "needs_review", "Acknowledgement waits for foreign Upgrade World operations to stop.");
        UpgradeWorldScheduleBridge.Idle = true;
        int thread = Thread.CurrentThread.ManagedThreadId, guardCalls = 0;
        Task<ServerManagerCommandResult> ack = ServerScheduleRuntime.Acknowledge(JobId(), () =>
        { ++guardCalls; Check(Thread.CurrentThread.ManagedThreadId == thread, "Authority callback remains on the game thread."); return true; }, CancellationToken.None);
        Tick();
        Check(ack.Result.Success && guardCalls > 0 && JournalState() == "ready" && ServerEventRuntime.Saves.Count == calls,
            "Explicit acknowledgement clears after durable write without rerunning the interrupted command.");
    }

    private static void MaintenanceReload()
    {
        Activate(Maintenance("zones_reset start", "locations_reset start")); Due();
        ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted); Tick();
        object settings = Get(Session!, "_settings")!;
        Reload(Config("echo replacement"));
        Check(ReferenceEquals(settings, Get(Session!, "_settings")) && Get(Session!, "_pendingSettings") != null &&
            !(bool)Get(Runs[0]!, "Retired")!, "A valid reload waits while started maintenance finishes its captured definition.");
        UpgradeWorldScheduleBridge.Observation = new(UpgradeWorldScheduleState.Succeeded, "uw_operation_completed"); Tick();
        Check(UpgradeWorldScheduleBridge.Begun.SequenceEqual(new[] { "zones_reset start", "locations_reset start" }) &&
            ReferenceEquals(settings, Get(Session!, "_settings")), "Reload neither swaps an in-flight destructive tail nor skips its post-save.");
        ServerEventRuntime.Status.LatestSave = Save("post", ServerManagerSaveState.CheckpointCompleted); Tick();
        Check(!ReferenceEquals(settings, Get(Session!, "_settings")) && JournalState() == "ready", "New settings install after durable maintenance completion.");
        Due(); Check(ServerConsoleExecutor.Lines.Last() == "echo replacement", "Future occurrences use the reloaded definition.");

        Activate(Maintenance("zones_reset start")); Due();
        ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted); Tick();
        UpgradeWorldScheduleBridge.Observation = new(UpgradeWorldScheduleState.Succeeded, "uw_operation_completed"); Tick();
        var partial = Save("post", ServerManagerSaveState.CheckpointCompleted); partial.PendingCharacterCount = 1;
        ServerEventRuntime.Status.LatestSave = partial; Tick();
        Check(JournalState() == "needs_review" && ServerEventRuntime.Audits.Any(a => a.Code == "checkpoint_partial" && !a.Success) &&
            !ServerEventRuntime.Audits.Any(a => a.Verb == "maintenance" && a.Success), "Partial post-save never advances maintenance as a success.");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); int guards = 0;
            var denied = ServerScheduleRuntime.Acknowledge(JobId(), () => { ++guards; return true; }, canceled.Token);
            Check(!denied.Result.Success && guards == 0 && JournalState() == "needs_review", "Pre-cancelled acknowledgement never consults authority or mutates disk state.");
        }
        UpgradeWorldScheduleBridge.Available = false;
        var unsupported = ServerScheduleRuntime.Acknowledge(JobId(), () => true, CancellationToken.None);
        Check(!unsupported.Result.Success && JournalState() == "needs_review", "Unknown Upgrade World observer cannot claim idle and clear the review barrier.");
    }

    private static void MaintenanceReviewBarrier()
    {
        Activate("jobs:\n" + Job("a", new[] { "zones_reset start" }) + Job("b", new[] { "locations_reset start" })); Due();
        ServerEventRuntime.Status.LatestSave = Save("a-pre", ServerManagerSaveState.CheckpointCompleted); Tick();
        UpgradeWorldScheduleBridge.Observation = new(UpgradeWorldScheduleState.Failed, "uw_partial_failure"); Tick();
        Check(JournalState(JobId(0)) == "needs_review" && JournalState(JobId(1)) == "pending" && ServerEventRuntime.Saves.Count == 1 &&
            UpgradeWorldScheduleBridge.Begun.Count == 1, "One failed maintenance job blocks other pending maintenance in the same world.");
        UpgradeWorldScheduleBridge.Observation = new(UpgradeWorldScheduleState.Pending, "uw_pending");
        Task<ServerManagerCommandResult> ack = ServerScheduleRuntime.Acknowledge(JobId(0), () => true, CancellationToken.None); Tick();
        Check(ack.Result.Success && JournalState(JobId(0)) == "ready" && JournalState(JobId(1)) == "running" && Runs.Count == 1 && Claims == 1,
            "Acknowledgement can release review with another unstarted pending job, preserving its unique claim.");
        Check(ServerEventRuntime.Saves.Count == 2 && UpgradeWorldScheduleBridge.Begun.Count == 1,
            "Pending maintenance still starts with its own checkpoint after acknowledgement.");
        ServerEventRuntime.Status.LatestSave = Save("b-pre", ServerManagerSaveState.CheckpointCompleted); Tick();
        Check(UpgradeWorldScheduleBridge.Begun.SequenceEqual(new[] { "zones_reset start", "locations_reset start" }),
            "Acknowledgement never replays failed job A or duplicates pending job B.");
    }

    private static void MaintenanceAdmission()
    {
        Activate(Maintenance("zones_reset start")); UpgradeWorldScheduleBridge.Allowed = false; Due();
        Check(Runs.Count == 0 && JournalState() == "ready" && UpgradeWorldScheduleBridge.Disposals == 1 &&
            Get(Session!, "_upgradeBridge") == null && ServerEventRuntime.Saves.Count == 0,
            "Denied pre-start maintenance releases its unowned bridge without claiming execution.");

        Activate(Maintenance("zones_reset start")); UpgradeWorldScheduleBridge.Idle = false; Due();
        Check(Get(Session!, "_upgradeBridge") != null && JournalState() == "pending", "Busy Upgrade World retains only an unstarted pending job.");
        Reload("jobs: []");
        Check(Runs.Count == 0 && UpgradeWorldScheduleBridge.Disposals == 1 && Get(Session!, "_upgradeBridge") == null,
            "Reload cancellation releases a bridge acquired before maintenance ownership.");

        Activate("jobs:\n" + Job("ordinary", new[] { "announce Before upkeep" }) +
            Job("maintenance", new[] { "zones_reset start" }));
        var pending = new TaskCompletionSource<ServerManagerCommandResult>(); ServerCommands.NextTask = pending.Task; Due();
        Check(ServerCommands.Calls.Count == 1 && ServerEventRuntime.Saves.Count == 0 && JournalState(JobId(1)) == "pending",
            "Maintenance pre-save waits for previously admitted asynchronous scheduled commands.");
        pending.SetResult(new ServerManagerCommandResult(true, "ok", "completed")); Tick();
        Check(ServerEventRuntime.Saves.Count == 1 && JournalState(JobId(1)) == "running",
            "Maintenance admission resumes after the previous command completes.");

        Activate("jobs:\n" + Job("upkeep", new[] { "zones_reset start" }) + Job("query", new[] { "objects_count" }) +
            Job("ordinary", new[] { "echo after-upkeep" })); Due();
        Check(ServerEventRuntime.Saves.Count == 1 && ServerConsoleExecutor.Lines.SequenceEqual(new[] { "objects_count" }),
            "Owned read-only UW query interleaves with maintenance pre-save, while unrelated raw commands wait.");
        ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted); Tick();
        Check(UpgradeWorldScheduleBridge.Begun.SequenceEqual(new[] { "zones_reset start" }) &&
            !ServerConsoleExecutor.Lines.Contains("echo after-upkeep"), "Pending UW mutation retains exclusive ownership over other mutation-capable commands.");
        UpgradeWorldScheduleBridge.Observation = new(UpgradeWorldScheduleState.Succeeded, "uw_operation_completed"); Tick();
        Check(ServerEventRuntime.Saves.Count == 2 && !ServerConsoleExecutor.Lines.Contains("echo after-upkeep"),
            "Ownership lasts until the post-save checkpoint, not just UW queue completion.");
        ServerEventRuntime.Status.LatestSave = Save("post", ServerManagerSaveState.CheckpointCompleted); Tick();
        Check(ServerConsoleExecutor.Lines.Last() == "echo after-upkeep" && ServerEventRuntime.Saves.Count == 2 && Claims == 0,
            "Completed post-save releases the ordinary job without adding saves to the query.");
    }

    private static void ReentrancyAndSuspension()
    {
        Activate(Config("echo stop-world", "echo stale-tail"));
        ServerConsoleExecutor.OnExecute = ServerScheduleRuntime.Stop;
        Due();
        Check(Session == null && ServerConsoleExecutor.Disposals == 1 && ServerConsoleExecutor.Lines.Count == 1,
            "Raw command can synchronously stop its own world without double-disposal or tail dispatch.");
        Check(ServerEventRuntime.Audits.Count == 0 && !ServerManagerPlugin.Log.Messages.Any(s => s.Contains("unexpected error")),
            "Post-disposal command completion does not audit retired world state or spuriously fail runtime.");

        foreach (bool mutation in new[] { false, true })
        foreach (bool success in new[] { false, true })
        foreach (string transition in new[] { "shutdown", "epoch", "not_ready" })
        {
            string first = mutation ? "world_clean" : "echo invalidated-world";
            Activate(Config(first, mutation ? "clean_chests" : "echo invalidated-tail"));
            object prior = Session!;
            if (mutation) Due();
            ServerConsoleExecutor.NextResult = new(success, success ? "rcon_dispatched" : "rcon_failed", "fixture result");
            ServerConsoleExecutor.OnExecute = () =>
            {
                if (transition == "shutdown") ServerEventRuntime.Status.ShutdownStarted = true;
                else if (transition == "epoch") ServerEventRuntime.CommandWorldEpoch++;
                else ServerEventRuntime.Status.WorldReady = false;
            };
            if (mutation)
            {
                ServerEventRuntime.Status.LatestSave = Save("pre", ServerManagerSaveState.CheckpointCompleted);
                Tick();
            }
            else Due();
            Check(ServerConsoleExecutor.Lines.SequenceEqual(new[] { first }) && ServerEventRuntime.Saves.Count == (mutation ? 1 : 0),
                "Synchronous state invalidation suppresses tails/post-saves without requiring Stop: " + mutation + "/" + success + "/" + transition);
            Check(ServerEventRuntime.Audits.All(a => a.Verb == "save" || a.Code == "cron_needs_review") &&
                !ServerManagerPlugin.Log.Messages.Any(s => s.Contains("unexpected error")),
                "Neither successful nor failed stale dispatch publishes old-world command/job completion: " + mutation + "/" + success + "/" + transition);
            Frame();
            Check((bool)Get(prior, "_disposed")!, "The invalidated session is retired by the next frame: " + transition);
            if (transition == "epoch") { Tick(); FinishRead(); }
            ServerConsoleExecutor.OnExecute = null;
            Restart(60);
            Check(ServerConsoleExecutor.Lines.Count == 1 && JournalState() == (mutation ? "needs_review" : "ready"),
                "Invalidated occurrence remains non-replayable on restart with review for mutations: " + mutation + "/" + success + "/" + transition);
        }

        Activate(Config("announce pending"));
        var pending = new TaskCompletionSource<ServerManagerCommandResult>(); ServerCommands.NextTask = pending.Task;
        Due(); var caller = ServerCommands.Calls.Single().Caller;
        EnvMan.instance!.m_dayLengthSec = double.NaN; Set(Session!, "_nextPoll", 0L); Tick();
        Check((bool)Get(Session!, "_suspended")! && (bool)Get(Session!, "_disposed")! && Runs.Count == 0 && Claims == 0,
            "Invalid game clock suspends/disposes only scheduling and releases bounded state.");
        Check(caller.Cancellation.IsCancellationRequested && !caller.IsAuthorized() &&
            ServerManagerPlugin.Log.Messages.Count(s => s.Contains("unexpected error")) == 1,
            "Suspension cancels queued authority and emits one scoped error.");
        pending.SetException(new InvalidOperationException("suspended fixture fault"));
        EnvMan.instance.m_dayLengthSec = 86400; Tick();
        Check(ServerCommands.Calls.Count == 1 && ServerManagerPlugin.Log.Messages.Count(s => s.Contains("unexpected error")) == 1,
            "Suspended session does not repeatedly restart or replay commands in the same world.");
    }

    private static void Throws<T>(Action action, string reason) where T : Exception
    { try { action(); } catch (T) { Check(true, reason); return; } throw new Exception(reason); }
}

public sealed class ZNet
{
    public static ZNet? instance;
    public bool Server = true, Saving;
    public double Seconds;
    public long WorldUid = 123456789L;
    public long GetWorldUID() => WorldUid;
    public bool IsServer() => Server;
    public bool IsSaving() => Saving;
    public double GetTimeSeconds() => Seconds;
}
public sealed class EnvMan { public static EnvMan? instance; public double m_dayLengthSec = 86400; }
public sealed class ZoneSystem
{
    public static ZoneSystem? instance;
    public readonly HashSet<string> Keys = new();
    public bool GetGlobalKey(string key) => Keys.Contains(key);
}
namespace BepInEx.Bootstrap
{ internal static class Chainloader { internal static readonly Dictionary<string, BepInEx.PluginInfo> PluginInfos = new(); } }
namespace BepInEx
{
    internal sealed class PluginInfo
    {
        internal readonly PluginMetadata Metadata = new();
        internal object Instance = new FixtureUpgradeWorld();
    }
    internal sealed class PluginMetadata { internal Version Version = new(1, 80); }
}
internal sealed class FixtureUpgradeWorld { internal static void NoOp() { } internal static bool True() => true; }
public class Terminal
{
    public sealed class ConsoleCommand
    {
        private readonly Action? action;
        private readonly Func<bool>? actionFailable;
        internal ConsoleCommand(Action? callback) { action = callback; }
        internal ConsoleCommand(Func<bool> callback) { actionFailable = callback; }
    }
}
namespace ServerManager
{
    internal static class ValheimPrivateAccess
    {
        internal static readonly Dictionary<string, Terminal.ConsoleCommand> Commands = new(StringComparer.OrdinalIgnoreCase);
        internal static Dictionary<string, Terminal.ConsoleCommand> GetTerminalCommands() => Commands;
    }
    // The real bridge has its own source-linked reflection/coroutine harness.
    // This boundary lets the actual scheduler exercise pending, failed and
    // interrupted operations without loading Unity or resetting a real world.
    internal enum UpgradeWorldScheduleState { Pending, Succeeded, Failed, Indeterminate }
    internal readonly struct UpgradeWorldScheduleObservation
    {
        internal UpgradeWorldScheduleState State { get; }
        internal string Code { get; }
        internal UpgradeWorldScheduleObservation(UpgradeWorldScheduleState state, string code)
        { State = state; Code = code; }
    }
    internal sealed class UpgradeWorldScheduleBridge : IDisposable
    {
        internal static bool Available, Allowed, Idle;
        internal static string FailureCode = "uw_unavailable";
        internal static readonly List<string> Begun = new();
        internal static UpgradeWorldScheduleObservation Observation;
        internal static Action? BeforeBegin;
        internal static int Disposals;
        internal static void Reset()
        {
            Available = Allowed = Idle = true; Begun.Clear(); BeforeBegin = null; Disposals = 0;
            FailureCode = "uw_unavailable";
            Observation = new(UpgradeWorldScheduleState.Pending, "uw_pending");
        }
        internal static bool IsMaintenanceCommand(string command) => UpgradeWorldScheduleCommands.IsMaintenance(command);
        internal static bool TryCreate(out UpgradeWorldScheduleBridge? bridge, out string code)
        { bridge = Available ? new UpgradeWorldScheduleBridge() : null; code = Available ? "uw_ready" : FailureCode; return Available; }
        internal bool TryGetIdle(out bool idle, out string code)
        { idle = Idle; code = "uw_idle"; return true; }
        internal bool CanRun(out string code)
        { code = Allowed ? "uw_ready" : "uw_saving_disabled"; return Allowed; }
        internal UpgradeWorldScheduleObservation Begin(string command, Func<ServerManagerCommandResult> dispatch)
        {
            BeforeBegin?.Invoke(); Begun.Add(command);
            ServerManagerCommandResult result = dispatch();
            if (!result.Success) return new(UpgradeWorldScheduleState.Failed, "uw_dispatch_failed");
            return Observation.State == UpgradeWorldScheduleState.Pending &&
                UpgradeWorldScheduleCommands.GetKind(command) == UpgradeWorldCommandKind.ImmediateMutation
                ? new(UpgradeWorldScheduleState.Succeeded, "uw_operation_completed") : Observation;
        }
        internal UpgradeWorldScheduleObservation Poll() => Observation;
        public void Dispose() => ++Disposals;
    }
    internal static class IntegrityCanonical
    { internal static bool IsFatal(Exception value) => value is OutOfMemoryException || value is StackOverflowException || value is AccessViolationException; }
    internal sealed class FixtureLog
    {
        internal readonly List<string> Messages = new();
        internal void LogError(string text) => Messages.Add(text);
        internal void LogWarning(string text) => Messages.Add(text);
        internal void LogInfo(string text) => Messages.Add(text);
    }
    internal static class ServerManagerPlugin
    {
        internal static string DataRoot = "";
        internal static readonly FixtureLog Log = new();
    }
}
namespace ServerManager.Commands
{
    internal sealed class CommandCaller
    {
        internal readonly string Source, Id, Name;
        internal readonly Func<bool> IsAuthorized;
        internal readonly CancellationToken Cancellation;
        internal CommandCaller(string source, string id, string name, Func<bool> authorized, CancellationToken cancellation)
        { Source = source; Id = id; Name = name; IsAuthorized = authorized; Cancellation = cancellation; }
    }
    internal static class ServerCommands
    {
        internal const int MaximumOutputLength = 1800;
        internal static readonly string[] FlatCommandNames = { "announce", "status", "help" };
        internal sealed class Call { internal string Line = ""; internal CommandCaller Caller = null!; }
        internal static readonly List<Call> Calls = new();
        internal static Task<ServerManagerCommandResult>? NextTask;
        internal static string BoundedText(string value, int maximum) => value.Length <= maximum ? value : value.Substring(0, maximum);
        internal static Task<ServerManagerCommandResult> ExecuteAsync(string line, CommandCaller caller)
        {
            Calls.Add(new Call { Line = line, Caller = caller });
            return NextTask ?? Task.FromResult(new ServerManagerCommandResult(true, "ok", "completed"));
        }
        // Inert tokenizer boundary handles the quoted fixture inputs. Production
        // token validation is covered by the common-command smoke, not copied here.
        internal static bool TryTokenize(string line, out string[] arguments, out string error)
        {
            var values = new List<string>(); var value = new StringBuilder(); bool quoted = false;
            foreach (char character in line)
            {
                if (character == '"') { quoted = !quoted; continue; }
                if (char.IsWhiteSpace(character) && !quoted)
                { if (value.Length > 0) { values.Add(value.ToString()); value.Clear(); } }
                else value.Append(character);
            }
            if (value.Length > 0) values.Add(value.ToString());
            arguments = values.ToArray(); error = quoted ? "unclosed quote" : ""; return !quoted;
        }
    }
    internal sealed class ServerConsoleExecutor : IDisposable
    {
        internal static readonly List<string> Lines = new();
        internal static ServerManagerCommandResult? NextResult;
        internal static Action? OnExecute;
        internal static int Disposals;
        internal ServerConsoleExecutor(Action<string> log) { }
        internal ServerManagerCommandResult Execute(string line, int maximum)
        { Lines.Add(line); OnExecute?.Invoke(); return NextResult ?? new(true, "rcon_dispatched", "completed"); }
        public void Dispose() => ++Disposals;
    }
}
namespace ServerManager.Events
{
    internal enum ServerEventCommandKind { Save }
    internal enum ServerManagerSaveState { InProgress, Failed, CheckpointCompleted }
    internal enum ServerManagerCharacterCommitScope { Partial, AllRetainedShadowsAtCutoff }
    internal sealed class ServerManagerSaveOperationSnapshot
    {
        internal string OperationId = "";
        internal ServerManagerSaveState State;
        internal ServerManagerCharacterCommitScope CharacterCommitScope;
        internal int PendingCharacterCount;
    }
    internal sealed class ServerManagerStatusSnapshot
    {
        internal bool WorldReady, ShutdownStarted;
        internal ServerManagerSaveOperationSnapshot? LatestSave;
    }
    internal sealed class ServerManagerCommandResult
    {
        internal readonly bool Success;
        internal readonly string Code, Message, OperationId;
        // Production's five-argument constructor; the overload below is only
        // fixture shorthand when arranging boundary results.
        internal ServerManagerCommandResult(bool success, string code, string message, string operationId, IDictionary<string, string>? data)
        { Success = success; Code = code; Message = message; OperationId = operationId; }
        internal ServerManagerCommandResult(bool success, string code, string message, string operationId = "")
            : this(success, code, message, operationId, null) { }
    }
    internal static class ServerEventRuntime
    {
        internal sealed class SaveCall { internal CancellationToken Cancellation; internal Func<bool> Authorized = null!; }
        internal sealed class Audit { internal string Source = "", Id = "", Name = "", Verb = "", Target = "", Code = ""; internal bool Success; internal Dictionary<string, string> Data = new(); }
        internal static long CommandWorldEpoch;
        internal static ServerManagerStatusSnapshot Status = new();
        internal static readonly List<SaveCall> Saves = new();
        internal static readonly List<Audit> Audits = new();
        internal static Task<ServerManagerCommandResult>? NextSave;
        internal static ServerManagerStatusSnapshot GetStatusSnapshot() => Status;
        internal static Task<ServerManagerCommandResult> EnqueueCommand(ServerEventCommandKind kind, string first, string second,
            CancellationToken cancellation, Func<bool> authorized)
        { Saves.Add(new SaveCall { Cancellation = cancellation, Authorized = authorized }); return NextSave ?? Task.FromResult(new ServerManagerCommandResult(true, "save_requested", "accepted")); }
        internal static void RecordCommand(string source, string id, string name, string verb, string target, string code, bool success,
            IReadOnlyDictionary<string, string>? data = null)
        { Audits.Add(new Audit { Source = source, Id = id, Name = name, Verb = verb, Target = target, Code = code, Success = success,
            Data = data == null ? new Dictionary<string, string>() : data.ToDictionary(pair => pair.Key, pair => pair.Value) }); }
    }
}
