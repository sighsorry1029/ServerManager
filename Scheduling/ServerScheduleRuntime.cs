using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Bootstrap;
using ServerManager.Commands;
using ServerManager.Events;

namespace ServerManager;

// Optional server-world service. File workers own bytes/settings only; commands,
// permissions, time capture, publication and lifecycle all stay on Unity's thread.
internal static class ServerScheduleRuntime
{
    private static Session? _session;
    private static readonly FieldInfo? ConsoleAction = typeof(Terminal.ConsoleCommand).GetField("action", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ConsoleActionFailable = typeof(Terminal.ConsoleCommand).GetField("actionFailable", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static void Tick()
    {
        ZNet? network = ZNet.instance;
        ServerManagerStatusSnapshot status = ServerEventRuntime.GetStatusSnapshot();
        if (network == null || !network.IsServer() || !status.WorldReady || status.ShutdownStarted)
        { Stop(); return; }
        long epoch = ServerEventRuntime.CommandWorldEpoch;
        if (_session == null || !_session.Owns(network, epoch))
        {
            Stop();
            _session = new Session(network, epoch, ServerManagerPlugin.DataRoot);
        }
        Session session = _session;
        try { session.Tick(); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            session.Suspend();
            string reason = exception is InvalidDataException
                ? ServerCommands.BoundedText(exception.Message, 300) : exception.GetType().Name;
            ServerManagerPlugin.Log.LogError("Cron stopped for this world after an unexpected error (" +
                reason + "). Other ServerManager features remain active.");
        }
    }

    internal static void Stop()
    {
        Session? previous = _session;
        _session = null;
        previous?.Dispose();
    }

    internal static ServerManagerCommandResult GetStatus() => _session?.Status() ??
        Result(false, "cron_unavailable", "Scheduling is not active in this server world.");

    internal static Task<ServerManagerCommandResult> Acknowledge(string jobId, Func<bool> authorized,
        CancellationToken cancellation) => _session?.Acknowledge(jobId, authorized, cancellation) ??
        Task.FromResult(Result(false, "cron_unavailable", "Scheduling is not active in this server world."));

    // Structural maintenance restrictions belong to the YAML parser; the
    // optional bridge validates arguments and the installed UW contract.
    internal static bool IsSupportedCommand(string command)
    {
        return ServerCommands.TryTokenize(command, out string[] arguments, out _) && arguments.Length != 0;
    }

    // Registry inspection stays on the game thread. UW ownership alone cannot
    // imply safe maintenance semantics, and a known name may be replaced by a mod.
    private static string? ValidateUpgradeWorldCommand(string line)
    {
        UpgradeWorldCommandKind kind = UpgradeWorldScheduleCommands.GetKind(line);
        if (kind == UpgradeWorldCommandKind.Control || kind == UpgradeWorldCommandKind.Unsupported)
            return "uw_command_not_schedulable";
        bool known = kind != UpgradeWorldCommandKind.None;
        if (!Chainloader.PluginInfos.TryGetValue("upgrade_world", out var plugin) || plugin.Instance == null)
            return known ? "uw_missing" : null;
        try
        {
            if (ConsoleAction == null || ConsoleActionFailable == null ||
                !typeof(Delegate).IsAssignableFrom(ConsoleAction.FieldType) ||
                !typeof(Delegate).IsAssignableFrom(ConsoleActionFailable.FieldType)) return "uw_command_registry_unavailable";
            if (!ValheimPrivateAccess.GetTerminalCommands().TryGetValue(FirstVerb(line), out Terminal.ConsoleCommand command))
                return known ? "uw_command_unavailable" : null;
            Assembly assembly = plugin.Instance.GetType().Assembly;
            bool owned = false, foreign = false;
            foreach (FieldInfo field in new[] { ConsoleAction, ConsoleActionFailable })
                if (field.GetValue(command) is Delegate callback)
                    foreach (Delegate part in callback.GetInvocationList())
                    {
                        if (part.Method.Module.Assembly == assembly) owned = true;
                        else foreign = true;
                    }
            if (known) return owned && !foreign ? null : "uw_command_owner_mismatch";
            return owned ? "uw_unknown_command" : null;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { return "uw_command_registry_unavailable"; }
    }

    private static bool CanInterleave(string line) => FirstVerb(line) == "announce" || FirstVerb(line) == "chat" ||
        UpgradeWorldScheduleCommands.GetKind(line) == UpgradeWorldCommandKind.Query;

    private sealed class Run
    {
        internal readonly ServerScheduleOccurrence Occurrence;
        internal readonly bool QuietCenterBroadcasts;
        internal readonly CancellationTokenSource Cancellation = new();
        internal int Index;
        internal Task<ServerManagerCommandResult>? Pending;
        internal bool Common, SaveRequested, WaitingSave, Retired;
        internal bool PendingDurable, Started, Finishing, WaitingUpgrade, Skip, PreflightDone;
        internal string[] Commands;
        internal bool UntrackedUpgrade;
        internal bool UpgradeTimedOut;
        internal string Verb = "", BaselineSave = "", SaveId = "";
        internal string SaveFailureCode = "";
        internal long Deadline;
        internal FreshWorldScheduleBridge? FreshWorld;
        internal bool FreshWorldChecked;
        internal long NextFreshWorldPoll;
        internal string FreshWorldSession = "", LeaseToken = "", LeaseOwner = "";
        internal bool SaveTimedOut, AdmissionTimedOut, SaveDrainOnly;
        internal long AdmissionDeadline;
        internal bool TerminalPending, TerminalSuccess, ReleasePending;
        internal string TerminalCode = "";
        internal Run(ServerScheduleOccurrence occurrence)
        {
            Occurrence = occurrence;
            List<string> commands = occurrence.Job.Commands.ToList();
            // Only commands explicitly configured by the operator are dispatched.
            Commands = commands.ToArray();
            // Only a complete, display-only job can opt out of successful
            // local audit entries. Mixed jobs and maintenance remain auditable.
            QuietCenterBroadcasts = !occurrence.Job.Log && !occurrence.Job.Maintenance &&
                commands.Count > 0 && commands.All(command =>
                    ServerScheduleSettings.TryGetCenterBroadcast(command, out string message) &&
                    !string.IsNullOrWhiteSpace(message));
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly ZNet _network;
        private readonly long _epoch;
        private readonly string _root;
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private string WorldId => _network.GetWorldUID().ToString(CultureInfo.InvariantCulture);
        private readonly ServerScheduleEngine _engine = new();
        private readonly List<Run> _runs = new();
        private readonly Random _random = new();
        private ServerConsoleExecutor? _console;
        private UpgradeWorldScheduleBridge? _upgradeBridge;
        private Run? _maintenanceOwner;
        private ServerScheduleSettings? _settings;
        private ServerScheduleSettings? _pendingSettings;
        private Task<ServerScheduleJournal>? _open;
        private ServerScheduleJournal? _journal;
        private Task? _writeTask;
        private Action? _afterWrite;
        private TaskCompletionSource<ServerManagerCommandResult>? _ack;
        private Task<ReadResult>? _read;
        private string? _candidate, _processed, _lastError;
        private long _nextRead, _nextPoll;
        private bool _disposed, _suspended;

        internal Session(ZNet network, long epoch, string root)
        {
            _network = network; _epoch = epoch; _root = root;
            if (Chainloader.PluginInfos.ContainsKey("blizz.NewCron") ||
                Chainloader.PluginInfos.ContainsKey("cron_job"))
            {
                _suspended = true;
                Warn("Another Cron plugin is loaded. ServerManager scheduling is disabled for this world to avoid duplicate jobs. Remove that plugin before using cron.yml.");
            }
            else
            {
                string worldId = network.GetWorldUID().ToString(CultureInfo.InvariantCulture);
                _open = Task.Run(() => ServerScheduleJournal.Open(root, worldId));
                StartRead(true);
            }
        }

        internal bool Owns(ZNet network, long epoch) => ReferenceEquals(_network, network) && _epoch == epoch;
        private bool Current => !_disposed && !_suspended && ReferenceEquals(_network, ZNet.instance) &&
            _network.IsServer() && _epoch == ServerEventRuntime.CommandWorldEpoch &&
            ServerEventRuntime.GetStatusSnapshot().WorldReady && !ServerEventRuntime.GetStatusSnapshot().ShutdownStarted;

        internal void Tick()
        {
            if (!Current) return;
            PollFile();
            if (_open != null)
            {
                if (!_open.IsCompleted) return;
                _journal = _open.GetAwaiter().GetResult();
                _open = null;
            }
            if (_writeTask != null)
            {
                if (!_writeTask.IsCompleted) return;
                _writeTask.GetAwaiter().GetResult(); // Failure never releases a waiting command.
                _writeTask = null;
                Action? after = _afterWrite;
                _afterWrite = null;
                after?.Invoke();
                if (!Current) return;
            }
            if (_journal == null) return;
            if (_pendingSettings != null && _runs.Count == 0)
            {
                ServerScheduleSettings next = _pendingSettings;
                DateTime applyUtc = DateTime.UtcNow, applyGame = CaptureGameTime(_network);
                BeginWrite(() => _journal.Reconcile(next, applyUtc, applyGame), () =>
                {
                    _engine.Apply(next, applyUtc, applyGame, _journal.GetResumeCursors(next));
                    _settings = next;
                    _nextPoll = 0;
                    if (ReferenceEquals(_pendingSettings, next)) _pendingSettings = null;
                    ServerManagerPlugin.Log.LogInfo("cron.yml loaded: " + next.Jobs.Count + " jobs; progress uses " + ServerScheduleJournal.FileName + ".");
                    foreach (string id in _journal.ReviewRequiredJobs)
                    {
                        Warn("Job '" + id + "' needs review. Inspect the world, then use cronack to skip the interrupted occurrence.");
                        ServerEventRuntime.RecordCommand("cron", id, id, "maintenance", "", "cron_needs_review", false);
                    }
                });
                return;
            }
            // Global dispatch budget, not a separate budget for every job.
            int budget = 2;
            foreach (Run run in _runs.ToArray())
            {
                if (!Current) return;
                if (run.Finishing)
                {
                    if (run.ReleasePending) Finish(run);
                    continue;
                }
                if (run.TerminalPending)
                {
                    EndRun(run, run.TerminalSuccess, run.TerminalCode);
                    if (_writeTask != null) return;
                    continue;
                }
                if (run.Retired && !run.Started)
                {
                    EndRun(run, false, "cron_cancelled");
                    if (_writeTask != null) return;
                    continue;
                }
                if (!run.PendingDurable)
                {
                    BeginWrite(() => _journal.RecordPending(run.Occurrence.Job,
                        run.Occurrence.OccurrenceUtc, run.Occurrence.CoveredUntilUtc), () => run.PendingDurable = true);
                    return;
                }
                if (run.Skip) { EndRun(run, true, "cron_skipped"); return; }
                if (!DrainAdmissionTimeout(run))
                { if (_writeTask != null) return; continue; }
                if (!run.Started)
                {
                    if (run.Occurrence.Job.Maintenance)
                    {
                        // Resolve optional compatibility before any command, but
                        // only acquire when UW and existing saves are idle.
                        if (!PrepareFreshWorld(run, acquire: false))
                        { if (_writeTask != null) return; continue; }
                        if (_journal.ReviewRequiredJobs.Count != 0) continue;
                        if (_maintenanceOwner != null && !ReferenceEquals(_maintenanceOwner, run)) continue;
                        if (!run.PreflightDone)
                        {
                            foreach (string preflightLine in run.Commands)
                            {
                                if (FirstVerb(preflightLine) == "save") continue;
                                string? error = ValidateUpgradeWorldCommand(preflightLine);
                                if (error != null) { EndRun(run, false, error); return; }
                            }
                        }
                        if (!run.UntrackedUpgrade && (!run.PreflightDone || _upgradeBridge == null))
                        {
                            string unavailable = "uw_available";
                            bool supported = _upgradeBridge != null || UpgradeWorldScheduleBridge.TryCreate(out _upgradeBridge, out unavailable);
                            if (supported)
                                foreach (string command in run.Occurrence.Job.Commands)
                                    if (FirstVerb(command) != "save" && !_upgradeBridge!.SupportsCommand(command, out unavailable))
                                    { supported = false; break; }
                            if (!supported)
                            {
                                // Check this job's capabilities before any explicit save. An
                                // unrelated unsupported command must not disable other jobs.
                                if (run.FreshWorld != null || unavailable != "uw_unsupported_contract" || run.Occurrence.Job.Commands.Count != 1)
                                { EndRun(run, false, unavailable); return; }
                                _upgradeBridge?.Dispose();
                                _upgradeBridge = null;
                                if (UpgradeWorldScheduleBridge.ObserverCleanupFailed)
                                { EndRun(run, false, "uw_observer_cleanup_failed"); return; }
                                run.UntrackedUpgrade = true;
                                // No implicit pre/post saves: completion and ordering cannot be observed.
                                run.Commands = run.Occurrence.Job.Commands.ToArray();
                            }
                        }
                        run.PreflightDone = true;
                        if (!run.UntrackedUpgrade)
                        {
                            if (!_upgradeBridge!.CanRun(out string denied)) { EndRun(run, false, denied); return; }
                            if (!_upgradeBridge.TryGetIdle(out bool idle, out string observationError))
                            { EndRun(run, false, observationError); return; }
                            if (!idle) continue;
                        }
                        if (_network.IsSaving() || _runs.Any(other => other.Pending != null || other.WaitingSave || other.SaveRequested)) continue;
                        if (!PrepareFreshWorld(run, acquire: true))
                        { if (_writeTask != null) return; continue; }
                        _maintenanceOwner = run;
                    }
                    else if (_maintenanceOwner != null && !CanInterleave(run.Commands[run.Index])) continue;
                    BeginWrite(() => _journal.MarkRunning(run.Occurrence.Job), () => run.Started = true);
                    return;
                }
                if (run.Pending != null)
                {
                    if (!run.Pending.IsCompleted) continue;
                    ServerManagerCommandResult result;
                    try { result = run.Pending.GetAwaiter().GetResult(); }
                    catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                    { result = Result(false, "command_failed", "The scheduled command threw an exception."); }
                    run.Pending = null;
                    if (run.SaveRequested && run.Occurrence.Job.Maintenance && result.Code == "save_busy")
                    {
                        // Admission lost a race with vanilla autosave. No save was
                        // dispatched, so only this request (not a mutation) may retry.
                        run.SaveRequested = false;
                        continue;
                    }
                    if (run.SaveRequested && result.Success)
                    {
                        run.SaveId = result.OperationId;
                        run.WaitingSave = true;
                    }
                    else if (run.SaveRequested && run.LeaseToken.Length != 0 && _network.IsSaving())
                    {
                        // A synchronous entry exception may follow native worker
                        // creation. A failed request is not permission to release
                        // the gate while that worker is still running.
                        run.SaveId = result.OperationId;
                        run.WaitingSave = true;
                        run.SaveDrainOnly = true;
                        HoldUnconfirmedSave(run, "save_dispatch_unconfirmed");
                        return;
                    }
                    else CompleteCommand(run, result);
                }
                if (_writeTask != null) return;
                if (!_runs.Contains(run)) continue;
                if (run.TerminalPending) continue;
                if (run.WaitingSave) { PollSave(run); if (_writeTask != null) return; continue; }
                if (run.WaitingUpgrade) { PollUpgrade(run); if (_writeTask != null) return; continue; }
                // Consume even retired tasks and retain a dispatched save's
                // claim until its checkpoint finishes, not just its request.
                if (run.Retired) { EndRun(run, false, "cron_cancelled"); return; }
                if (run.Pending != null || run.Index >= run.Commands.Length || budget == 0) continue;
                // Never overlap a requested save with another observed save.
                string line = run.Commands[run.Index];
                // Maintenance owns mutations/save ordering. Ordinary announcements
                // can continue; other scheduled commands wait for its completion.
                if (_maintenanceOwner != null && !ReferenceEquals(_maintenanceOwner, run) &&
                    !CanInterleave(line)) continue;
                if (FirstVerb(line) == "save" && (_network.IsSaving() || _runs.Any(other => other.WaitingSave || other.SaveRequested)))
                    continue;
                --budget;
                StartCommand(run, line);
                if (_writeTask != null) return;
            }
            if (!Current) return;
            long now = Stopwatch.GetTimestamp();
            if (_settings == null || _pendingSettings != null || now < _nextPoll) return;
            _nextPoll = now + (long)Math.Ceiling(_settings.IntervalSeconds * Stopwatch.Frequency);
            DateTime utc = DateTime.UtcNow;
            DateTime game = CaptureGameTime(_network);
            foreach (ServerScheduleOccurrence occurrence in _engine.ClaimDue(utc, game))
            {
                ServerScheduleJob job = occurrence.Job;
                if (_journal.IsBlocked(job.Id)) { _engine.Complete(occurrence); continue; }
                bool keysPass = ZoneSystem.instance != null &&
                    job.GlobalKeys.All(ZoneSystem.instance.GetGlobalKey) &&
                    !job.BannedGlobalKeys.Any(ZoneSystem.instance.GetGlobalKey);
                _runs.Add(new Run(occurrence) { Skip = !keysPass || job.Chance <= 0 ||
                    (job.Chance < 1 && _random.NextDouble() >= job.Chance) });
            }
        }

        private bool PrepareFreshWorld(Run run, bool acquire)
        {
            if (!run.FreshWorldChecked)
            {
                if (!FreshWorldScheduleBridge.TryCreate(out run.FreshWorld, out string error) && error != "fw_not_installed")
                { EndRun(run, false, error); return false; }
                run.FreshWorldChecked = true;
            }
            if (run.FreshWorld == null) return true;
            if (run.LeaseToken.Length != 0)
            {
                if (CoordinationLeaseValid(run)) return true;
                EndRun(run, false, "fw_lease_lost"); return false;
            }
            if (!acquire) return true;
            if (Stopwatch.GetTimestamp() < run.NextFreshWorldPoll) return false;
            run.NextFreshWorldPoll = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            run.LeaseOwner = "ServerManager:" + _sessionId + ":" + run.Occurrence.Job.Id;
            if (!run.FreshWorld.TryAcquire(WorldId, run.LeaseOwner, out run.FreshWorldSession,
                    out run.LeaseToken, out bool busy, out string denied))
            {
                // Busy/cleanup is a transient admission state, not proof of a
                // failed reset. No occurrence, date or success-history matching.
                if (busy) return false;
                EndRun(run, false, denied); return false;
            }
            _maintenanceOwner = run;
            run.AdmissionDeadline = Stopwatch.GetTimestamp() + 120 * Stopwatch.Frequency;
            return true;
        }

        private bool DrainAdmissionTimeout(Run run)
        {
            if (run.LeaseToken.Length == 0 || run.WaitingSave || run.WaitingUpgrade ||
                Stopwatch.GetTimestamp() < run.AdmissionDeadline) return true;
            if (!run.AdmissionTimedOut)
            {
                run.AdmissionTimedOut = true;
                run.Cancellation.Cancel();
                Warn("Job '" + run.Occurrence.Job.Id + "' timed out before its next maintenance dispatch. No further command will start; waiting for queued work/observed busy work to settle before release.");
            }
            // Consume an already-queued request first. Its guard/cancellation
            // prevents a still-unstarted request from entering the native save.
            if (run.Pending != null) return true;
            if (_network.IsSaving() || _upgradeBridge == null || !_upgradeBridge.TryGetIdle(out bool idle, out _) || !idle)
                return false;
            EndRun(run, false, "fw_admission_timeout");
            return false;
        }

        private bool CoordinationLeaseValid(Run run) =>
            !run.Occurrence.Job.Maintenance ||
            (run.FreshWorld == null ? !FreshWorldScheduleBridge.IsInstalled :
                run.LeaseToken.Length != 0 && run.FreshWorld.Validate(WorldId, run.FreshWorldSession,
                    run.LeaseOwner, run.LeaseToken));

        private static DateTime CaptureGameTime(ZNet network)
        {
            if (EnvMan.instance == null) return ServerScheduleEngine.GameEpoch;
            double day = EnvMan.instance.m_dayLengthSec;
            double seconds = network.GetTimeSeconds();
            if (double.IsNaN(day) || double.IsInfinity(day) || day <= 0 ||
                double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                throw new InvalidDataException("Invalid Valheim clock.");
            return ServerScheduleEngine.GameEpoch.AddDays(seconds / day);
        }

        private void StartCommand(Run run, string line)
        {
            if (!Current || run.Cancellation.IsCancellationRequested) { EndRun(run, false, "cron_cancelled"); return; }
            if (!CoordinationLeaseValid(run)) { EndRun(run, false, "fw_lease_lost"); return; }
            run.Verb = FirstVerb(line);
            bool prefixed = line.StartsWith("sm:", StringComparison.OrdinalIgnoreCase);
            if (prefixed) line = line.Substring(3);
            run.Common = prefixed || ServerCommands.FlatCommandNames.Contains(run.Verb, StringComparer.OrdinalIgnoreCase) ||
                run.Verb == "ban" || run.Verb == "unban";
            run.SaveRequested = false;
            run.Deadline = Stopwatch.GetTimestamp() + 120 * Stopwatch.Frequency;
            if (run.Occurrence.Job.AllowClientHide && !prefixed &&
                ServerScheduleSettings.TryGetCenterBroadcast(line, out string centerMessage))
            {
                // This opted-in display is owned by ServerManager. Never also invoke
                // the external console handler, which would send an unfilterable duplicate.
                run.Verb = "broadcast";
                run.Common = false;
                bool accepted = ServerManagerRuntime.TryBroadcastOptionalCenterMessage(centerMessage);
                CompleteCommand(run, Result(accepted, accepted ? "message_sent" : "send_failed",
                    accepted ? "Optional center broadcast accepted; client delivery is not acknowledged."
                        : "The optional center broadcast could not be sent."));
                return;
            }
            if (run.Verb != "save" && (!run.Common || run.Occurrence.Job.Maintenance))
            {
                string? validation = ValidateUpgradeWorldCommand(line);
                if (validation != null) { CompleteCommand(run, Result(false, validation, "The scheduled Upgrade World command is not supported by the active registry.")); return; }
            }
            if (run.UntrackedUpgrade)
            {
                Warn("Job '" + run.Occurrence.Job.Id + "' is dispatch-only: Upgrade World completion cannot be tracked. " +
                    "No automatic saves or retries; overlap with later scheduled operations cannot be ruled out.");
                _console ??= new ServerConsoleExecutor(Warn);
                ServerManagerCommandResult dispatched = _console.Execute(line, ServerCommands.MaximumOutputLength);
                CompleteCommand(run, dispatched.Success
                    ? Result(true, "cron_dispatched_untracked", "Dispatched; completion not tracked.") : dispatched);
                return;
            }
            if (run.Occurrence.Job.Maintenance)
            {
                if (_upgradeBridge == null || !_upgradeBridge.CanRun(out _))
                { CompleteCommand(run, Result(false, "uw_unavailable", "Upgrade World is not available for this maintenance step.")); return; }
                if (!_upgradeBridge.TryGetIdle(out bool idle, out string code))
                { CompleteCommand(run, Result(false, code, "Cannot observe Upgrade World.")); return; }
                if (!idle) return;
                if (run.Verb != "save")
                {
                    if (_network.IsSaving()) return;
                    _console ??= new ServerConsoleExecutor(Warn);
                    UpgradeWorldScheduleObservation observed = _upgradeBridge.Begin(line,
                        () => _console.Execute(line, ServerCommands.MaximumOutputLength));
                    if (!Current || !_runs.Contains(run)) return;
                    run.Common = false;
                    run.WaitingUpgrade = true;
                    run.UpgradeTimedOut = false;
                    run.Deadline = Stopwatch.GetTimestamp() + 60 * 60 * Stopwatch.Frequency;
                    ApplyUpgradeObservation(run, observed);
                    return;
                }
            }
            if (run.Verb == "save")
            {
                if (!ServerCommands.TryTokenize(line, out string[] args, out _) || args.Length != 1)
                { CompleteCommand(run, Result(false, "invalid_argument", "Use save without arguments.")); return; }
                run.Common = false;
                run.SaveRequested = true;
                run.SaveTimedOut = false;
                run.SaveDrainOnly = false;
                run.SaveFailureCode = "";
                run.SaveId = "";
                run.BaselineSave = ServerEventRuntime.GetStatusSnapshot().LatestSave?.OperationId ?? "";
                run.Pending = run.Occurrence.Job.Maintenance
                    ? ServerEventRuntime.EnqueueMaintenanceSave(_network, run.Cancellation.Token,
                        () => Current && !run.Retired && CoordinationLeaseValid(run) &&
                            (run.LeaseToken.Length == 0 || Stopwatch.GetTimestamp() < run.AdmissionDeadline))
                    : ServerEventRuntime.EnqueueCommand(ServerEventCommandKind.Save, "", "",
                        run.Cancellation.Token, () => Current && !run.Retired);
                return;
            }
            if (run.Common)
            {
                run.Pending = ServerCommands.ExecuteAsync(line, new CommandCaller("cron", run.Occurrence.Job.Id,
                    run.Occurrence.Job.Id, () => Current && !run.Retired, run.Cancellation.Token));
                return;
            }
            _console ??= new ServerConsoleExecutor(message => Warn(message));
            CompleteCommand(run, _console.Execute(line, ServerCommands.MaximumOutputLength));
        }

        private void PollUpgrade(Run run)
        {
            if (_upgradeBridge == null) { CompleteCommand(run, Result(false, "uw_unavailable", "Upgrade World observation was lost.")); return; }
            if (!run.UpgradeTimedOut && Stopwatch.GetTimestamp() >= run.Deadline)
            {
                run.UpgradeTimedOut = true;
                _upgradeBridge.MarkTimedOut();
                Warn("Job '" + run.Occurrence.Job.Id + "' exceeded the observation deadline. Upgrade World was not cancelled; waiting for it to become idle before releasing scheduled mutations. Review is required.");
                Audit(run, Result(false, "uw_unconfirmed", "Completion deadline exceeded; still observing Upgrade World."));
            }
            ApplyUpgradeObservation(run, _upgradeBridge.Poll());
        }

        private void ApplyUpgradeObservation(Run run, UpgradeWorldScheduleObservation observed)
        {
            if (observed.State == UpgradeWorldScheduleState.Pending) return;
            // Failure stays failure. Do not insert a save, execute remaining
            // commands, or replay partially completed world mutations.
            CompleteCommand(run, Result(observed.State == UpgradeWorldScheduleState.Succeeded,
                observed.Code, "Upgrade World scheduled operation observation."));
        }

        private void PollSave(Run run)
        {
            ServerManagerSaveOperationSnapshot? save = ServerEventRuntime.GetStatusSnapshot().LatestSave;
            if (run.SaveDrainOnly && !_network.IsSaving())
            {
                // Failed dedicated dispatch: native worker quiescence permits
                // cleanup, never a successful checkpoint or subsequent mutation.
                CompleteCommand(run, Result(false, run.SaveFailureCode, "The unconfirmed save is now idle; review is required."));
                return;
            }
            // Listen-host saves begin in a next-frame vanilla coroutine. Observe
            // the next checkpoint after admission; do not call request acceptance success.
            if (string.IsNullOrEmpty(run.SaveId) && save != null && save.OperationId != run.BaselineSave)
                run.SaveId = save.OperationId;
            if (save != null && !string.IsNullOrEmpty(run.SaveId))
            {
                if (save.OperationId != run.SaveId)
                {
                    if (run.LeaseToken.Length != 0)
                    { HoldUnconfirmedSave(run, "save_observation_lost"); return; }
                    CompleteCommand(run, Result(false, "save_observation_lost", "A newer save replaced the observed checkpoint.")); return;
                }
                if (save.State == ServerManagerSaveState.Failed)
                {
                    // The event tracker can label an operation Failed after its
                    // own observation timeout. That does not terminate a worker.
                    if (run.LeaseToken.Length != 0 && _network.IsSaving())
                    { HoldUnconfirmedSave(run, "save_failed_worker_active"); return; }
                    CompleteCommand(run, Result(false, "save_failed", "The observed checkpoint failed.")); return;
                }
                if (save.State == ServerManagerSaveState.CheckpointCompleted)
                {
                    bool complete = save.CharacterCommitScope == ServerManagerCharacterCommitScope.AllRetainedShadowsAtCutoff &&
                        save.PendingCharacterCount == 0;
                    if (run.SaveTimedOut)
                    { CompleteCommand(run, Result(false, "save_unconfirmed", "The checkpoint ended after its deadline; review is required.")); return; }
                    CompleteCommand(run, new ServerManagerCommandResult(complete,
                        complete ? "checkpoint_completed" : "checkpoint_partial",
                        complete ? "World and retained-character checkpoint completed." : "Some retained characters were not committed.",
                        save.OperationId, new Dictionary<string, string>()));
                    return;
                }
            }
            if (Stopwatch.GetTimestamp() >= run.Deadline)
            {
                if (run.LeaseToken.Length != 0) HoldUnconfirmedSave(run, "save_unconfirmed");
                else CompleteCommand(run, Result(false, "save_unconfirmed", "Checkpoint completion was not confirmed; no retry was issued."));
            }
        }

        private void HoldUnconfirmedSave(Run run, string code)
        {
            // A known-started save need not prove success to release exclusion.
            // Once its native worker is idle, fail the job without continuing it.
            // An empty-ID deferred host request still needs evidence of its start.
            run.SaveDrainOnly |= run.SaveId.Length != 0 || _network.IsSaving();
            if (run.SaveTimedOut) return;
            run.SaveTimedOut = true;
            run.SaveFailureCode = code;
            Warn("Job '" + run.Occurrence.Job.Id + "' cannot confirm its checkpoint (" + code +
                "). The FreshWorld lease is retained until native save termination is known; no mutation or save retry. Review is required.");
            Audit(run, Result(false, code, "Checkpoint unresolved; coordination lease retained."));
        }

        private void CompleteCommand(Run run, ServerManagerCommandResult result)
        {
            // A raw command can synchronously close the current world.
            if (!Current || !_runs.Contains(run)) return;
            if (!run.Common) Audit(run, result);
            run.SaveRequested = run.WaitingSave = run.WaitingUpgrade = false;
            run.Pending = null;
            if (!result.Success || run.Retired)
            {
                if (!result.Success) Warn("Job '" + run.Occurrence.Job.Id + "' stopped: " + result.Code + ". No automatic retry.");
                EndRun(run, false, result.Code);
                return;
            }
            ++run.Index;
            if (run.LeaseToken.Length != 0) run.AdmissionDeadline = Stopwatch.GetTimestamp() + 120 * Stopwatch.Frequency;
            if (run.Index >= run.Commands.Length) EndRun(run, true, run.UntrackedUpgrade ? "cron_dispatched_untracked" : "cron_completed");
        }

        private static void Audit(Run run, ServerManagerCommandResult result) =>
            ServerEventRuntime.RecordCommand("cron", run.Occurrence.Job.Id, run.Occurrence.Job.Id,
                run.Verb, "", result.Code, result.Success,
                writeAuditLog: !run.QuietCenterBroadcasts || !result.Success || run.Retired);

        private void EndRun(Run run, bool success, string code)
        {
            if (!Current || run.Finishing || !_runs.Contains(run)) return;
            if (!run.PendingDurable) { Finish(run); return; }
            // Every terminal path, including a cancelled queued request, obeys
            // the same lease cleanup boundary. A failure result is not evidence
            // that native saves or the observed UW executor have become idle.
            if (run.LeaseToken.Length != 0 && (_network.IsSaving() || _upgradeBridge == null ||
                !_upgradeBridge.TryGetIdle(out bool idle, out _) || !idle))
            {
                if (!run.TerminalPending)
                    Warn("Job '" + run.Occurrence.Job.Id + "' ended (" + code +
                        ") but native work is not confirmed idle. Retaining the FreshWorld lease until cleanup is safe.");
                run.TerminalPending = true; run.TerminalSuccess = success; run.TerminalCode = code;
                run.Cancellation.Cancel();
                return;
            }
            run.TerminalPending = false;
            run.Finishing = true;
            bool review = run.Occurrence.Job.Maintenance &&
                run.Started && !success;
            DateTime coveredThrough = run.Occurrence.Job.UseGameTime ? CaptureGameTime(_network) : DateTime.UtcNow;
            BeginWrite(() => _journal!.MarkFinished(run.Occurrence.Job, success, review, coveredThrough), () =>
            {
                ServerEventRuntime.RecordCommand("cron", run.Occurrence.Job.Id, run.Occurrence.Job.Id,
                    run.Occurrence.Job.Maintenance ? "maintenance" : "schedule", "",
                    review ? "cron_needs_review" : code, success, CronResultData(run),
                    writeAuditLog: !run.QuietCenterBroadcasts || !success || code != "cron_completed");
                if (review) Warn("Job '" + run.Occurrence.Job.Id + "' needs review (" + code + "). No automatic replay.");
                else if (!success) Warn("Job '" + run.Occurrence.Job.Id + "' stopped (" + code + ").");
                else if (run.Occurrence.Job.Log && (code != "cron_skipped" || _settings!.LogSkipped))
                    ServerManagerPlugin.Log.LogInfo("Cron: Job '" + run.Occurrence.Job.Id + "' " +
                        (code == "cron_skipped" ? "skipped (chance or global-key conditions)." :
                            code == "cron_dispatched_untracked" ? "dispatched; completion not tracked." : "completed."));
                Finish(run);
            });
        }

        private static IReadOnlyDictionary<string, string> CronResultData(Run run)
        {
            ServerScheduleJob job = run.Occurrence.Job;
            bool multiple = job.Commands.Count > 1;
            string summary = string.Join(" → ", job.Commands.Select(command => CronCommandSummary(command, multiple, job.AllowClientHide)));
            Dictionary<string, string> data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cron_schedule"] = job.Cron,
                ["cron_command_count"] = job.Commands.Count.ToString(CultureInfo.InvariantCulture),
                ["cron_summary"] = ServerCommands.BoundedText(summary, 1000)
            };
            if (!multiple) data["cron_verb"] = job.AllowClientHide &&
                ServerScheduleSettings.TryGetCenterBroadcast(job.Commands[0], out _) ? "broadcast" : FirstVerb(job.Commands[0]);
            return data;
        }

        private static string CronCommandSummary(string line, bool includeVerb, bool allowClientHide)
        {
            if (allowClientHide && ServerScheduleSettings.TryGetCenterBroadcast(line, out string centerMessage))
                return includeVerb ? "broadcast: " + centerMessage : centerMessage;
            if (!ServerCommands.TryTokenize(line, out string[] args, out _) || args.Length == 0)
                return string.Empty;
            string verb = FirstVerb(line);
            int content = verb == "broadcast" ? 2 : verb == "announce" || verb == "chat" ? 1 : args.Length;
            if (content >= args.Length) return verb;
            string text = string.Join(" ", args.Skip(content));
            return includeVerb ? verb + ": " + text : text;
        }

        private void Finish(Run run)
        {
            if (run.LeaseToken.Length != 0)
            {
                // Native work can start while the terminal journal write is on
                // its worker. Recheck quiescence at the actual release boundary.
                if (_network.IsSaving() || _upgradeBridge == null ||
                    !_upgradeBridge.TryGetIdle(out bool idle, out _) || !idle)
                {
                    if (!run.ReleasePending) Warn("The terminal cron result is durable, but native work became busy before lease release. Retaining the FreshWorld lease until idle.");
                    run.ReleasePending = true;
                    return;
                }
                if (run.FreshWorld == null || !run.FreshWorld.Release(WorldId,
                        run.FreshWorldSession, run.LeaseOwner, run.LeaseToken))
                {
                    Warn("FreshWorld lease release could not be confirmed. Scheduling is suspended; the lease is not force-cleared. Inspect the server log before restarting the world.");
                    Suspend(); return;
                }
                run.LeaseToken = "";
            }
            if (!_runs.Remove(run)) return;
            _engine.Complete(run.Occurrence);
            run.Cancellation.Cancel();
            run.Cancellation.Dispose();
            if (ReferenceEquals(_maintenanceOwner, run) || _maintenanceOwner == null)
            {
                _maintenanceOwner = null;
                _upgradeBridge?.Dispose();
                _upgradeBridge = null;
            }
        }

        private void BeginWrite(Action mutate, Action after)
        {
            if (_writeTask != null) throw new InvalidOperationException("Concurrent cron journal write.");
            ServerScheduleJournal journal = _journal!;
            _afterWrite = after;
            _writeTask = Task.Run(() => { mutate(); journal.Flush(); });
        }

        private void PollFile()
        {
            if (_read != null && _read.IsCompleted)
            {
                ReadResult result = _read.GetAwaiter().GetResult();
                _read = null;
                if (result.Error != null)
                {
                    if (result.Error != _lastError) Warn(result.Error + " Keeping the last valid schedule.");
                    _lastError = result.Error;
                    if (result.Text == null) _candidate = _processed = null;
                }
                else _lastError = null;
                if (result.Text != null)
                {
                    _candidate = result.Text;
                    if (result.Parsed) _processed = result.Text;
                    if (result.Settings != null)
                    {
                        _pendingSettings = result.Settings;
                        foreach (Run run in _runs)
                        {
                            // A started maintenance batch completes its captured
                            // definition before new settings/journal fingerprints apply.
                            if (run.Started && run.Occurrence.Job.Maintenance) continue;
                            run.Retired = true; run.Cancellation.Cancel();
                        }
                    }
                }
            }
            if (_read == null && Stopwatch.GetTimestamp() >= _nextRead) StartRead(false);
        }

        private void StartRead(bool initial)
        {
            _nextRead = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
            string root = _root;
            string? candidate = _candidate, processed = _processed;
            _read = Task.Run(() => Read(root, initial, candidate, processed));
        }

        internal void Suspend()
        {
            _suspended = true;
            Dispose();
        }

        internal ServerManagerCommandResult Status()
        {
            if (!Current) return Result(false, "cron_suspended", "Scheduling is suspended; inspect the server log and " + ServerScheduleJournal.FileName + ".");
            if (_journal == null || _settings == null || _writeTask != null)
                return Result(true, "cron_busy", "Scheduling is loading or committing state. Retry shortly.");
            string review = string.Join(", ", _journal.ReviewRequiredJobs);
            string text = "Jobs: " + _settings.Jobs.Count + "; active: " + _runs.Count +
                "; settings pending: " + (_pendingSettings != null) + "\nNeeds review: " + (review.Length == 0 ? "none" : review) +
                "\n" + _journal.GetStatus();
            foreach (Run run in _runs.Where(run => run.FreshWorld != null))
                text += "\n" + run.Occurrence.Job.Id + ": " +
                    (run.LeaseToken.Length == 0 ? "waiting for maintenance admission" : "FreshWorld lease held") +
                    (run.SaveTimedOut || run.UpgradeTimedOut || run.AdmissionTimedOut ? "; timeout/unconfirmed: waiting for native work, review required" : "");
            return Result(true, "cron_status", ServerCommands.BoundedText(text, ServerCommands.MaximumOutputLength));
        }

        internal Task<ServerManagerCommandResult> Acknowledge(string jobId, Func<bool> authorized, CancellationToken cancellation)
        {
            if (!Current || _journal == null || _settings == null)
                return Task.FromResult(Result(false, "cron_unavailable", "Scheduling is not ready."));
            if (cancellation.IsCancellationRequested || !authorized())
                return Task.FromResult(Result(false, "cron_ack_denied", "The administrator session is no longer authorized."));
            if (_writeTask != null || _runs.Any(run => run.Started || run.Finishing) || _pendingSettings != null)
                return Task.FromResult(Result(false, "cron_busy", "Wait for active jobs/state writes before acknowledgement."));
            if (!_journal.ReviewRequiredJobs.Contains(jobId, StringComparer.Ordinal))
                return Task.FromResult(Result(false, "cron_not_blocked", "That job does not require review."));
            // Never clear a review marker while Upgrade World is still mutating.
            if (!UpgradeWorldScheduleBridge.TryCreate(out UpgradeWorldScheduleBridge? probe, out string unavailable))
                return Task.FromResult(Result(false, unavailable, "Cannot confirm Upgrade World is idle. Restore the supported plugin before acknowledgement."));
            using (probe)
            {
                if (!probe!.TryGetIdle(out bool idle, out _) || !idle)
                    return Task.FromResult(Result(false, "cron_busy", "Upgrade World is still active."));
            }
            DateTime utc = DateTime.UtcNow, game = CaptureGameTime(_network);
            TaskCompletionSource<ServerManagerCommandResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ack = completion;
            BeginWrite(() => _journal.Acknowledge(jobId, utc, game), () =>
            {
                _engine.SkipThrough(jobId, utc, game);
                _ack = null;
                completion.TrySetResult(Result(true, "cron_acknowledged", "Interrupted occurrence skipped. No command was rerun or rolled back."));
            });
            return completion.Task;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Run run in _runs)
            {
                run.Cancellation.Cancel();
                // A pre-dispatch journal error must not strand an idle provider.
                // Deferred/queued requests and unobservable native work still
                // retain ownership; world shutdown resets the provider's session.
                if (run.LeaseToken.Length != 0 && run.Pending == null &&
                    (!(run.SaveRequested || run.WaitingSave) || run.SaveId.Length != 0 || run.SaveDrainOnly))
                {
                    try
                    {
                        if (ReferenceEquals(_network, ZNet.instance) && _epoch == ServerEventRuntime.CommandWorldEpoch &&
                            !_network.IsSaving() && _upgradeBridge != null && _upgradeBridge.TryGetIdle(out bool idle, out _) && idle &&
                            run.FreshWorld != null && run.FreshWorld.Release(WorldId, run.FreshWorldSession, run.LeaseOwner, run.LeaseToken))
                            run.LeaseToken = "";
                    }
                    catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { }
                }
                if (run.LeaseToken.Length != 0)
                    Warn("Coordinated job '" + run.Occurrence.Job.Id + "' stopped with a retained FreshWorld lease. Review is required; no automatic replay or force release.");
                // Consume eventual faults without touching retired Unity state.
                run.Pending?.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                run.Cancellation.Dispose();
            }
            _runs.Clear();
            _engine.Reset();
            _ack?.TrySetResult(Result(false, "cron_ack_unconfirmed", "The world stopped before acknowledgement was confirmed. Check status after restart."));
            _upgradeBridge?.Dispose();
            _upgradeBridge = null;
            _console?.Dispose();
            if (_open != null)
                _open.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); else _ = task.Exception; }, TaskScheduler.Default);
            ServerScheduleJournal? journal = _journal;
            if (_writeTask != null)
                _writeTask.ContinueWith(task => { _ = task.Exception; journal?.Dispose(); }, TaskScheduler.Default);
            else journal?.Dispose();
        }
    }

    private static string FirstVerb(string line)
    {
        if (!ServerCommands.TryTokenize(line, out string[] args, out _) || args.Length == 0) return "";
        string verb = args[0].ToLowerInvariant();
        return verb.StartsWith("sm:", StringComparison.Ordinal) ? verb.Substring(3) : verb;
    }

    private static ServerManagerCommandResult Result(bool success, string code, string message) =>
        new(success, code, message, "", new Dictionary<string, string>());
    private static void Warn(string message) => ServerManagerPlugin.Log.LogWarning("Cron: " + message);

    private sealed class ReadResult
    {
        internal string? Text, Error;
        internal bool Parsed;
        internal ServerScheduleSettings? Settings;
    }

    private static ReadResult Read(string root, bool initial, string? candidate, string? processed)
    {
        ReadResult result = new();
        try
        {
            result.Text = ReadFile(root, initial);
            result.Parsed = initial || (result.Text == candidate && result.Text != processed);
            if (result.Parsed) result.Settings = ServerScheduleSettings.Parse(result.Text, IsSupportedCommand);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { result.Error = exception is InvalidDataException ? exception.Message : "cron.yml read failed (" + exception.GetType().Name + ")."; }
        return result;
    }

    internal static string ReadFile(string root, bool create)
    {
        root = Path.GetFullPath(root);
        string path = ResolveSettingsPath(root);
        if (create && !File.Exists(path))
        {
            Directory.CreateDirectory(root);
            RejectLinks(root, path);
            FileStream? stream = null;
            try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { }
            if (stream != null)
            {
                using (stream)
                {
                    byte[] example = Encoding.UTF8.GetBytes(ServerScheduleSettings.DefaultYaml);
                    stream.Write(example, 0, example.Length);
                }
            }
        }
        if (ResolveSettingsPath(root) != path)
            throw new InvalidDataException("Cron settings filename changed during a read; retry the edit.");
        using MemoryStream bytes = new();
        byte[] buffer = new byte[4096];
        using (FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytes.Length + read > ServerScheduleSettings.MaximumFileBytes)
                    throw new InvalidDataException("cron.yml exceeds 128 KiB.");
                bytes.Write(buffer, 0, read);
            }
        }
        string text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        if (ResolveSettingsPath(root) != path)
            throw new InvalidDataException("Cron settings filename changed during a read; retry the edit.");
        return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
    }

    private static string ResolveSettingsPath(string root)
    {
        string primary = Path.Combine(root, ServerScheduleSettings.FileName);
        string alternate = Path.Combine(root, ServerScheduleSettings.AlternateFileName);
        RejectLinks(root, primary); RejectLinks(root, alternate);
        if (Directory.Exists(primary) || Directory.Exists(alternate))
            throw new InvalidDataException("A cron settings path is a directory.");
        bool primaryExists = File.Exists(primary), alternateExists = File.Exists(alternate);
        if (primaryExists && alternateExists)
            throw new InvalidDataException("Both " + ServerScheduleSettings.FileName + " and " +
                ServerScheduleSettings.AlternateFileName + " exist; keep only one settings file.");
        return alternateExists ? alternate : primary;
    }

    private static void RejectLinks(string root, string file)
    {
        for (DirectoryInfo? directory = new(root); directory != null; directory = directory.Parent) Reject(directory.FullName);
        Reject(file);
        static void Reject(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("cron.yml cannot use symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
