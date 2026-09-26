using System;
using System.Reflection;
using System.Linq;
using System.Text;
using System.Threading;
using HarmonyLib;
using ServerManager.Events;

namespace ServerManager.Commands;

/// <summary>
/// Bounded synchronous console execution for already-authorized server callers.
/// Construct and execute on Unity's main thread; callers own admission and auditing.
/// </summary>
internal sealed class ServerConsoleExecutor : IDisposable
{
    private readonly Action<string> _log;
    private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
    private static readonly object Gate = new();
    private static Harmony? _harmony;
    private static int _owners;
    private static bool _patchFaulted;
    private static Capture? _active;
    private static readonly FieldInfo? CheatField = typeof(Terminal).GetField("m_cheat",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    internal bool IsAvailable { get; private set; }

    internal ServerConsoleExecutor(Action<string> log)
    {
        _log = log;
        lock (Gate)
        {
            try
            {
                if (_patchFaulted) throw new InvalidOperationException("Console capture cleanup failed.");
                if (_harmony == null)
                {
                    MethodInfo? target = AccessTools.Method(typeof(Terminal), nameof(Terminal.AddString), new[] { typeof(string) });
                    if (target == null || CheatField == null || CheatField.FieldType != typeof(bool) || !CheatField.IsStatic)
                        throw new MissingMemberException("Console runtime members unavailable.");
                    _harmony = new Harmony("sighsorry.ServerManager.Console." + Guid.NewGuid().ToString("N"));
                    _harmony.Patch(target, postfix: new HarmonyMethod(typeof(ServerConsoleExecutor), nameof(CaptureOutput)));
                }
                // Scheduler and overlapping Discord reload sessions share one
                // postfix. Installing one per instance would duplicate output.
                _owners++;
                IsAvailable = true;
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                RemoveUnusedPatch();
                SafeLog("Server console execution disabled because output capture is unavailable: " + exception.GetType().Name);
            }
        }
    }

    internal ServerManagerCommandResult Execute(string line, int maximumCharacters) =>
        Execute(line, maximumCharacters, out _);

    // Preserve the Discord adapter's existing formatting for observed save/kick
    // results without making the shared executor depend on Discord presentation.
    internal ServerManagerCommandResult Execute(string line, int maximumCharacters, out bool observedResult)
    {
        observedResult = false;
        if (!IsAvailable || _threadId != Thread.CurrentThread.ManagedThreadId)
            return Result(false, "rcon_unavailable", "The server console is unavailable.");
        global::Console? terminal = global::Console.instance;
        if (terminal == null || ZNet.instance == null || !ZNet.instance.IsServer() || ZNet.World == null)
            return Result(false, "rcon_unavailable", "The server console is unavailable.");
        string trimmed = line.Trim();
        string verb = trimmed.Split(new[] { ' ', '\t' }, 2)[0].ToLowerInvariant();
        if (!ValheimPrivateAccess.GetTerminalCommands().ContainsKey(verb))
            return Result(false, "rcon_unknown", "Unknown server console command.");
        string[] arguments = Array.Empty<string>();
        if ((verb == "save" || verb == "kick") &&
            (!ServerCommands.TryTokenize(trimmed, out arguments, out _) ||
             (verb == "save" ? arguments.Length != 1 :
                 arguments.Length < 2 || arguments[1].Length > 128 ||
                 string.Join(" ", arguments.Skip(2)).Length > 300)))
            return Result(false, "invalid_argument", "Usage: save or kick <name/Steam64> [reason]");
        string previousSaveOperation = verb == "save"
            ? ServerEventRuntime.GetStatusSnapshot().LatestSave?.OperationId ?? string.Empty : string.Empty;
        Capture capture = new(terminal, maximumCharacters);
        lock (Gate)
        {
            if (!IsAvailable) return Result(false, "rcon_unavailable", "The server console is unavailable.");
            if (_active != null) return Result(false, "rcon_busy", "Another RCON command is running.");
            _active = capture;
        }
        try
        {
            ServerManagerCommandResult? observed = null;
            bool previousCheatState = (bool)CheatField!.GetValue(null);
            try
            {
                // Authorization belongs to this invocation, never persistent
                // cheat settings. Restore even when the command throws.
                CheatField.SetValue(null, true);
                if (verb == "kick")
                {
                    observed = ServerEventRuntime.ExecuteConsoleKick(arguments[1],
                        string.Join(" ", arguments.Skip(2)), transportId =>
                        {
                            terminal.TryRunCommand("kick " + transportId, silentFail: false, skipAllowedCheck: true);
                            return !capture.Failed;
                        });
                }
                else terminal.TryRunCommand(verb == "save" ? "save" : trimmed, silentFail: false, skipAllowedCheck: true);
            }
            finally { CheatField.SetValue(null, previousCheatState); }
            if (!capture.Failed && verb == "save")
                observed = ServerEventRuntime.DescribeWorldSaveRequest(previousSaveOperation);
            if (observed != null)
            {
                observedResult = true;
                return observed;
            }
            string output = capture.Text.ToString().Trim();
            if (capture.Truncated)
            {
                const string marker = "\n[output truncated]";
                output = output.Substring(0, Math.Min(output.Length, Math.Max(0, maximumCharacters - marker.Length))) + marker;
            }
            return Result(!capture.Failed, capture.Failed ? "rcon_failed" : "rcon_dispatched",
                output.Length == 0 ? "Command dispatched. No synchronous console output was returned." : output);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            return Result(false, "rcon_failed", "RCON failed: " + exception.GetType().Name);
        }
        finally
        {
            lock (Gate)
            {
                if (ReferenceEquals(_active, capture)) _active = null;
                RemoveUnusedPatch();
            }
        }
    }

    private static ServerManagerCommandResult Result(bool success, string code, string message) =>
        new(success, code, message, string.Empty, null!);

    private static void CaptureOutput(Terminal __instance, string __0)
    {
        lock (Gate)
        {
            Capture? capture = _active;
            if (capture == null || capture.ThreadId != Thread.CurrentThread.ManagedThreadId ||
                !ReferenceEquals(capture.Terminal, __instance)) return;
            string output = __0 ?? string.Empty;
            if (output.IndexOf("Error executing command", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("is not valid in the current context", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("is not a recognized command", StringComparison.OrdinalIgnoreCase) >= 0)
                capture.Failed = true;
            if (capture.Text.Length > 0) Append(capture, '\n');
            foreach (char character in output)
            {
                if (character == '\r') continue;
                if (!Append(capture, char.IsControl(character) && character != '\n' ? ' ' : character)) break;
            }
        }
    }

    private static bool Append(Capture capture, char character)
    {
        if (capture.Text.Length >= capture.Maximum) { capture.Truncated = true; return false; }
        capture.Text.Append(character);
        return true;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (!IsAvailable) return;
            IsAvailable = false;
            _owners--;
            // A retiring Discord session must not clear another caller's
            // in-flight capture. The last execution releases the hook in finally.
            RemoveUnusedPatch();
        }
    }

    // Called only while Gate is held. Never install another hook after uncertain
    // cleanup; disabling raw console must not disable typed commands or the mod.
    private void RemoveUnusedPatch()
    {
        if (_owners != 0 || _active != null || _harmony == null) return;
        try
        {
            _harmony.UnpatchSelf();
            _harmony = null;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            _patchFaulted = true;
            SafeLog("Server console capture cleanup failed: " + exception.GetType().Name);
        }
    }

    private void SafeLog(string message)
    {
        try { _log(message); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { }
    }

    private sealed class Capture
    {
        internal Capture(Terminal terminal, int maximum)
        { Terminal = terminal; Maximum = maximum; ThreadId = Thread.CurrentThread.ManagedThreadId; }
        internal readonly Terminal Terminal;
        internal readonly int Maximum, ThreadId;
        internal readonly StringBuilder Text = new();
        internal bool Truncated, Failed;
    }
}
