using System;
using System.IO;
using System.Linq;
using ServerManager;

internal static class ServerScheduleJournalSmoke
{
    private static int _checks;
    private static readonly DateTime Now = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Game = ServerScheduleEngine.GameEpoch.AddDays(4);
    private static void Check(bool value, string reason) { ++_checks; if (!value) throw new Exception(reason); }
    private static void Bad(Action action, string reason)
    { try { action(); } catch (Exception error) when (error is IOException || error is InvalidDataException)
      { ++_checks; return; } throw new Exception(reason); }
    private static ServerScheduleSettings Settings(bool maintenance = true, bool game = false, string? command = null) =>
        ServerScheduleSettings.Parse("timezone: UTC\njobs:\n  - schedule: '0 * * * * *'\n" +
            "    commands: ['" + (command ?? (maintenance ? "zones_reset start" : "announce Hello")) + "']" +
            "\n    useGameTime: " + game.ToString().ToLowerInvariant() + "\n");
    private static string Fresh(string name)
    { string path = Path.Combine(Environment.CurrentDirectory, name); Directory.CreateDirectory(path); return path; }
    private static int Main()
    {
        try
        {
            PersistedIdentityCompatibility(); LoggingPolicyPreservesHistory(); CanonicalFileName(); LiveAliases(); Resume(); Interrupted(); Mutation(); CompletionCoverage(); Integrity(); ClocksAndBounds();
            Console.WriteLine("PASS: durable cron journal " + _checks + " assertions; isolated temporary files only."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void PersistedIdentityCompatibility()
    {
        // Captured from the compiled pre-refactor scheduler (c3c937f source).
        // A fingerprint change would reset existing ready/pending journal cursors.
        var cases = new[]
        {
            new { Command = "announce Hello", GameTime = false,
                Id = "announce-d3996d060b6657f12186a3f824f92d10ddc7fc5b",
                Fingerprint = "1ba5cc9d3bbea23e1e9fa379edf83df69381bb5aa0e85e2e5b813e50b0f2548c" },
            new { Command = "zones_reset start", GameTime = false,
                Id = "zones_reset-81d7c16b040cf7569778320d9a1d289b80555a16",
                Fingerprint = "8ddd3efccc00ea31b3f9bb2bd2646bba3eef6a9d525fd206d23da1f3f3ca7614" },
            new { Command = "world_clean", GameTime = true,
                Id = "world_clean-369cf8ae831628bbe94db6aa79070d3540165fca",
                Fingerprint = "bcbe48af61406d59f3e3133167f76ece5540ccf53793339dc2433c8d472fbb4c" }
        };
        foreach (var item in cases)
        {
            ServerScheduleSettings settings = Settings(game: item.GameTime, command: item.Command);
            ServerScheduleJob job = settings.Jobs[0];
            Check(job.Id == item.Id && ServerScheduleJournal.Fingerprint(settings, job) == item.Fingerprint,
                "Persisted job identity must retain the pre-refactor format: " + item.Command);
        }
    }
    private static void LoggingPolicyPreservesHistory()
    {
        const string yaml = "timezone: UTC\njobs:\n  - schedule: '0 * * * * *'\n    command: broadcast center Notice\n";
        ServerScheduleSettings previous = ServerScheduleSettings.Parse("logJobs: true\n" + yaml);
        ServerScheduleSettings current = ServerScheduleSettings.Parse(yaml);
        ServerScheduleJob oldJob = previous.Jobs.Single(), newJob = current.Jobs.Single();
        Check(oldJob.Log && !newJob.Log && oldJob.Id == newJob.Id && oldJob.SameAs(newJob) &&
            ServerScheduleJournal.Fingerprint(previous, oldJob) == ServerScheduleJournal.Fingerprint(current, newJob),
            "New omitted-false logging preserves the prior explicit-true job ID and durable fingerprint");
        string path = Fresh("logging-history");
        using ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world");
        journal.Reconcile(previous, Now, Game);
        journal.RecordPending(oldJob, Now.AddMinutes(1), Now.AddMinutes(1));
        journal.MarkRunning(oldJob); journal.MarkFinished(oldJob, true, coveredThrough: Now.AddMinutes(2)); journal.Flush();
        string file = Path.Combine(path, ServerScheduleJournal.FileName);
        byte[] retained = File.ReadAllBytes(file);
        journal.Reconcile(current, Now.AddHours(1), Game); journal.Flush();
        Check(File.ReadAllBytes(file).SequenceEqual(retained),
            "Changing only effective logging leaves existing cron_last.yml progress bytes untouched");
    }

    private static void CanonicalFileName()
    {
        Check(ServerScheduleJournal.FileName == "cron_last.yml", "The canonical journal filename is cron_last.yml");
        foreach (bool alreadyReviewed in new[] { false, true })
        {
            string path = Fresh(alreadyReviewed ? "canonical-review" : "canonical-running");
            string canonical = Path.Combine(path, "cron_last.yml"), alternate = Path.Combine(path, "cron_last.yaml");
            string marker = Path.Combine(path, ".cron-writer.lock");
            ServerScheduleSettings settings = Settings(); ServerScheduleJob job = settings.Jobs[0];
            using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"))
            {
                journal.Reconcile(settings, Now, Game); journal.Flush();
                Check(File.Exists(canonical) && !File.Exists(alternate), "New journal creation and flush write only the default .yml file");
                journal.RecordPending(job, Now.AddMinutes(1), Now.AddMinutes(1)); journal.MarkRunning(job);
                if (alreadyReviewed) journal.MarkFinished(job, false, true);
                journal.Flush();
                Check(!File.Exists(alternate), "Persisting an active maintenance record does not create a second journal");
            }
            byte[] expected = File.ReadAllBytes(canonical), expectedMarker = File.ReadAllBytes(marker);
            Check(expectedMarker.Length != 0, "Fixture has the existing initialized writer marker");
            // Only isolated fixture files are renamed, while no journal writer is open.
            File.Move(canonical, alternate);
            using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"))
            {
                journal.Reconcile(settings, Now.AddHours(1), Game); journal.Flush();
                Check(journal.IsBlocked(job.Id) && journal.ReviewRequiredJobs.Single() == job.Id && journal.GetResumeCursors(settings).Count == 0,
                    "Alternate-only journal preserves review protection for " + (alreadyReviewed ? "needs_review" : "running") + " maintenance");
                Check(File.Exists(alternate) && !File.Exists(canonical), "Alternate-only journal writes .yaml in place without migration or canonical creation");
            }
            expected = File.ReadAllBytes(alternate);
            File.Copy(alternate, canonical);
            Bad(() => { using ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"); }, "Both journal suffixes must fail closed even when their bytes match");
            Check(File.ReadAllBytes(canonical).SequenceEqual(expected) && File.ReadAllBytes(alternate).SequenceEqual(expected) &&
                File.ReadAllBytes(marker).SequenceEqual(expectedMarker), "Ambiguous startup leaves both histories and initialized marker untouched");
            File.Delete(canonical); File.Delete(alternate);
            Bad(() => { using ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"); }, "Initialized installation with neither journal must not start fresh");
            Check(!File.Exists(canonical) && !File.Exists(alternate), "Missing initialized history stays missing for operator review");
        }
    }
    private static void LiveAliases()
    {
        foreach (bool alternateSelected in new[] { false, true })
        foreach (bool renameSelected in new[] { false, true })
        {
            string path = Fresh("live-alias-" + alternateSelected + "-" + renameSelected);
            string canonical = Path.Combine(path, ServerScheduleJournal.FileName);
            string alternate = Path.Combine(path, ServerScheduleJournal.AlternateFileName);
            ServerScheduleSettings settings = Settings(); ServerScheduleJob job = settings.Jobs[0];
            using (ServerScheduleJournal initialize = ServerScheduleJournal.Open(path, "world"))
            { initialize.Reconcile(settings, Now, Game); initialize.Flush(); }
            if (alternateSelected) File.Move(canonical, alternate);
            string selected = alternateSelected ? alternate : canonical;
            string other = alternateSelected ? canonical : alternate;
            using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"))
            {
                journal.Reconcile(settings, Now, Game); journal.Flush();
                byte[] expected = File.ReadAllBytes(selected);
                journal.RecordPending(job, Now.AddMinutes(1), Now.AddMinutes(1));
                if (renameSelected) File.Move(selected, other);
                else File.Copy(selected, other);
                Bad(journal.Flush, renameSelected
                    ? "A live journal cannot switch suffixes after its selected path is renamed"
                    : "A second suffix created after Open must block even a dirty journal flush");
                Check(File.ReadAllBytes(other).SequenceEqual(expected) &&
                    (renameSelected ? !File.Exists(selected) : File.ReadAllBytes(selected).SequenceEqual(expected)),
                    "Rejected live alias change preserves both histories/absence and never writes pending state to another suffix");
            }
        }
    }
    private static void Resume()
    {
        string path = Fresh("resume"); ServerScheduleSettings settings = Settings(); ServerScheduleJob job = settings.Jobs[0];
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world-1"))
        {
            journal.Reconcile(settings, Now, Game); journal.Flush();
            Check(journal.GetResumeCursors(settings)[job.Id] == Now, "New jobs start now, not before installation");
            Bad(() => { using ServerScheduleJournal duplicate = ServerScheduleJournal.Open(path, "world-2"); }, "Second writer admitted");
        }
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world-1"))
        {
            journal.Reconcile(settings, Now.AddHours(1), Game); journal.Flush();
            ServerScheduleEngine engine = new(); engine.Apply(settings, Now.AddHours(1), Game, journal.GetResumeCursors(settings));
            ServerScheduleOccurrence occurrence = engine.ClaimDue(Now.AddHours(1), Game).Single();
            Check(occurrence.OccurrenceUtc == Now.AddMinutes(1) && occurrence.CoveredUntilUtc == Now.AddHours(1), "Offline missed jobs coalesce once");
            journal.RecordPending(job, occurrence.OccurrenceUtc, occurrence.CoveredUntilUtc); journal.Flush();
        }
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world-1"))
        {
            journal.Reconcile(settings, Now.AddHours(2), Game);
            Check(journal.GetResumeCursors(settings)[job.Id] == Now.AddMinutes(1).AddTicks(-1), "Unstarted pending occurrence resumes");
            journal.RecordPending(job, Now.AddMinutes(1), Now.AddHours(2)); journal.Flush(); journal.MarkRunning(job); journal.Flush();
            journal.MarkFinished(job, true); journal.Flush();
        }
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world-1"))
        {
            journal.Reconcile(settings, Now.AddHours(3), Game);
            Check(!journal.IsBlocked(job.Id) && journal.GetResumeCursors(settings)[job.Id] == Now.AddHours(2), "Completed maintenance resumes after the covered occurrence");
        }
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world-2"))
        {
            journal.Reconcile(settings, Now.AddHours(3), Game); journal.Flush();
            Check(journal.GetResumeCursors(settings)[job.Id] == Now.AddHours(3), "Different world has independent baseline");
        }
        Check(File.ReadAllText(Path.Combine(path, ServerScheduleJournal.FileName)).Contains("2026-09-07T"), "Journal uses readable UTC timestamps");
        string skipped = Fresh("skip"); settings = Settings(false); job = settings.Jobs[0];
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(skipped, "world"))
        { journal.Reconcile(settings, Now, Game); journal.RecordPending(job, Now.AddMinutes(1), Now.AddMinutes(1)); journal.Flush(); }
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(skipped, "world"))
        {
            journal.Reconcile(settings, Now.AddHours(1), Game); journal.Flush();
            Check(journal.GetResumeCursors(settings).Count == 0 && journal.GetStatus().Contains(job.Id + "=ready"), "Ordinary non-catch-up pending is skipped");
            journal.RecordPending(job, Now.AddHours(1).AddMinutes(1), Now.AddHours(1).AddMinutes(1)); journal.MarkRunning(job); journal.Flush();
        }
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(skipped, "world"))
        {
            journal.Reconcile(settings, Now.AddHours(2), Game); journal.Flush();
            Check(!journal.IsBlocked(job.Id) && journal.GetResumeCursors(settings).Count == 0 && journal.GetStatus().Contains(job.Id + "=ready"),
                "Interrupted ordinary command is not automatically replayed");
        }
    }
    private static void Interrupted()
    {
        string path = Fresh("interrupted"); ServerScheduleSettings settings = Settings(); ServerScheduleJob job = settings.Jobs[0];
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"))
        {
            journal.Reconcile(settings, Now, Game); journal.RecordPending(job, Now.AddMinutes(1), Now.AddMinutes(1)); journal.Flush();
            journal.MarkRunning(job); journal.Flush();
        }
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"))
        {
            journal.Reconcile(settings, Now.AddHours(1), Game); journal.Flush();
            Check(journal.IsBlocked(job.Id) && journal.ReviewRequiredJobs.Single() == job.Id && journal.GetResumeCursors(settings).Count == 0,
                "Interrupted maintenance requires review and no replay");
            Bad(() => journal.RecordPending(job, Now.AddHours(1), Now.AddHours(1)), "Blocked maintenance was replaced");
            ServerScheduleSettings edited = Settings(false);
            journal.Reconcile(edited, Now.AddHours(2), Game); journal.Flush();
            Check(journal.IsBlocked(job.Id), "Changing definition cannot bypass interrupted maintenance");
            journal.Reconcile(ServerScheduleSettings.Parse("jobs: []"), Now.AddHours(2), Game); journal.Flush();
            Check(journal.IsBlocked(job.Id), "Removing job retains blocked state");
            journal.Reconcile(edited, Now.AddHours(3), Game);
            journal.Acknowledge(job.Id, Now.AddHours(3), Game); journal.Flush();
            Check(!journal.IsBlocked(job.Id) && journal.GetStatus().Contains(job.Id + "=ready"), "Explicit acknowledgement releases without execution");
        }
    }
    private static void Mutation()
    {
        string path = Fresh("mutation"); ServerScheduleSettings settings = Settings(); ServerScheduleJob old = settings.Jobs[0];
        using ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world");
        journal.Reconcile(settings, Now, Game); journal.RecordPending(old, Now.AddMinutes(1), Now.AddMinutes(1)); journal.MarkRunning(old); journal.Flush();
        ServerScheduleSettings edited = ServerScheduleSettings.Parse("timezone: local\njobs: []");
        journal.Reconcile(edited, Now.AddHours(1), Game);
        journal.MarkFinished(old, false, true); journal.Flush();
        Check(journal.IsBlocked(old.Id), "Running old definition can safely finish as review after reload/removal");
        journal.Acknowledge(old.Id, Now.AddHours(1), Game); journal.Flush();
        journal.Reconcile(settings, Now.AddHours(2), Game);
        journal.RecordPending(old, Now.AddHours(3), Now.AddHours(3)); journal.MarkRunning(old); journal.MarkFinished(old, true); journal.Flush();
        Check(journal.GetResumeCursors(settings)[old.Id] == Now.AddHours(3), "Confirmed completion advances cursor");
        ServerScheduleSettings changed = Settings(command: "world_clean");
        journal.Reconcile(changed, Now.AddHours(4), Game); journal.Flush();
        Check(journal.GetResumeCursors(changed)[changed.Jobs[0].Id] == Now.AddHours(4), "Definition change safely establishes new baseline");
    }
    private static void Integrity()
    {
        string path = Fresh("integrity"); string file = Path.Combine(path, ServerScheduleJournal.FileName);
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"))
        {
            File.AppendAllText(file, "# external edit\n");
            Bad(journal.Flush, "Live administrator edit overwritten");
            Check(File.ReadAllText(file).EndsWith("# external edit\n"), "Failed flush preserves edit");
        }
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"))
        {
            File.Delete(file); Bad(journal.Flush, "Deleted journal recreated while live");
            Check(!File.Exists(file), "Deleted journal stays deleted");
        }
        Bad(() => { using ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"); }, "Deleted journal recreated after restart");
        File.WriteAllText(file, "broken: [");
        Bad(() => { using ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"); }, "Corrupt journal replaced");
        Check(File.ReadAllText(file) == "broken: [", "Corruption preserved for review");
        foreach (string text in new[] { "version: 1\nworlds: &a {}\n", "version: 1\nworlds: *a\n", "version: 2\nworlds: {}\n",
            "version: 1\nworlds: {}\nunknown: true\n", "version: 1\nworlds: {}\nworlds: {}\n", new string('x', ServerScheduleJournal.MaximumFileBytes + 1) })
        {
            File.WriteAllText(file, text);
            Bad(() => { using ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"); }, "Unsafe/oversized journal accepted");
        }
        string extensionPath = Fresh("removed-coordination"), extensionFile = Path.Combine(extensionPath, ServerScheduleJournal.FileName);
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(extensionPath, "world"))
        {
            journal.Reconcile(Settings(), Now, Game); journal.Flush();
        }
        string original = File.ReadAllText(extensionFile);
        Check(!original.Contains("coordination:"), "Simple maintenance retains the existing journal schema without cross-mod phases");
        string removedExtension = original.Replace("      state:", "      coordination: {phase: LeaseAcquired}\n      state:");
        Check(removedExtension != original, "Removed-extension fixture actually adds the unsupported mapping");
        File.WriteAllText(extensionFile, removedExtension);
        Bad(() => { using ServerScheduleJournal journal = ServerScheduleJournal.Open(extensionPath, "world"); },
            "An unsupported coordination journal must be inspected, not silently migrated or reset");
        Check(File.ReadAllText(extensionFile) == removedExtension, "Unsupported journal is preserved byte-for-byte");
    }
    private static void CompletionCoverage()
    {
        string path = Fresh("coverage"); ServerScheduleSettings settings = Settings(); ServerScheduleJob job = settings.Jobs[0];
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"))
        {
            journal.Reconcile(settings, Now, Game); journal.RecordPending(job, Now.AddMinutes(1), Now.AddMinutes(1));
            journal.MarkRunning(job); journal.MarkFinished(job, true, coveredThrough: Now.AddHours(1)); journal.Flush();
            Check(journal.GetResumeCursors(settings)[job.Id] == Now.AddHours(1), "Completion covers skipped busy-time intervals");
        }
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "world"))
        {
            journal.Reconcile(settings, Now.AddHours(1), Game);
            ServerScheduleEngine engine = new(); engine.Apply(settings, Now.AddHours(1), Game, journal.GetResumeCursors(settings));
            Check(engine.ClaimDue(Now.AddHours(1), Game).Count == 0, "Restart does not catch up busy-time intervals");
            Check(engine.ClaimDue(Now.AddHours(1).AddMinutes(1), Game).Count == 1, "Next future scheduled occurrence still runs");
            journal.RecordPending(job, Now.AddHours(2), Now.AddHours(2)); journal.MarkRunning(job);
            journal.MarkFinished(job, true, coveredThrough: Now.AddHours(1)); journal.Flush();
            Check(journal.GetResumeCursors(settings)[job.Id] == Now.AddHours(2), "Completion clock rollback never reduces cursor");
            journal.RecordPending(job, Now.AddHours(3), Now.AddHours(3)); journal.MarkRunning(job);
            journal.MarkFinished(job, false, coveredThrough: Now.AddHours(4)); journal.Flush();
            string text = File.ReadAllText(Path.Combine(path, ServerScheduleJournal.FileName));
            Check(journal.IsBlocked(job.Id) && text.Contains("coveredUntil: 2026-09-07T15:00:00.0000000Z") &&
                !text.Contains("2026-09-07T16:00:00"), "Review preserves original occurrence coverage without advancing to failure time");
        }
        string gamePath = Fresh("game-coverage"); settings = Settings(game: true); job = settings.Jobs[0];
        using (ServerScheduleJournal journal = ServerScheduleJournal.Open(gamePath, "world"))
        {
            journal.Reconcile(settings, Now, Game); journal.RecordPending(job, Game.AddMinutes(1), Game.AddMinutes(1));
            journal.MarkRunning(job); journal.MarkFinished(job, true, coveredThrough: Game.AddHours(1)); journal.Flush();
            Check(journal.GetResumeCursors(settings)[job.Id] == Game.AddHours(1), "Completion uses the selected game-time clock");
        }
    }
    private static void ClocksAndBounds()
    {
        string path = Fresh("world-bound"); ServerScheduleSettings settings = Settings(game: true);
        for (int i = 0; i < ServerScheduleJournal.MaximumWorlds; ++i)
        {
            using ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "w" + i);
            journal.Reconcile(settings, Now, Game); journal.Flush();
            Check(journal.GetResumeCursors(settings)[settings.Jobs[0].Id] == Game, "Game-time cursor remains independent of real clock");
        }
        Bad(() => { using ServerScheduleJournal journal = ServerScheduleJournal.Open(path, "one-too-many"); }, "World bound exceeded");
    }
}

namespace ServerManager.Events
{
    // Inert wire-validation boundary; the production codec has separate wire tests.
    internal static class EventDisplayCodec
    {
        internal const int MaximumMessageBytes = 2000;
        internal static bool IsValidOptionalCenterMessage(string message) =>
            !string.IsNullOrWhiteSpace(message) && !message.Any(char.IsControl) &&
            new System.Text.UTF8Encoding(false, true).GetByteCount(message) <= MaximumMessageBytes;
    }
}
