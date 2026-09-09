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
            CanonicalFileName(); LiveAliases(); Resume(); Interrupted(); Mutation(); CompletionCoverage(); Integrity(); ClocksAndBounds();
            Console.WriteLine("PASS: durable cron journal " + _checks + " assertions; isolated temporary files only."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
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
