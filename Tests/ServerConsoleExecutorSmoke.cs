using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ServerManager.Commands;
using ServerManager.Discord;
using ServerManager.Events;

// Production executor/Discord adapter are source-linked. These inert game and
// Harmony boundaries test routing and ownership, not a live Unity detour.
internal static class ServerConsoleExecutorSmoke
{
    private static int _checks;
    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new Exception(message); }

    private static int Main()
    {
        var logs = new List<string>();
        Action<string> log = logs.Add;
        var terminal = new global::Console();
        global::Console.instance = terminal;
        ZNet.instance = new ZNet();
        foreach (string verb in new[] { "help", "echo", "save", "kick" })
            ServerManager.ValheimPrivateAccess.Commands[verb] = new object();

        var first = new ServerConsoleExecutor(log);
        var second = new ServerConsoleExecutor(log);
        Check(first.IsAvailable && second.IsAvailable, "Both callers acquire capture.");
        Check(HarmonyLib.Harmony.Patches == 1, "Overlapping callers share exactly one postfix.");
        terminal.OnExecute = line => { Check(Terminal.m_cheat, "Cheat allowance is invocation-scoped."); terminal.AddString("one line"); };
        Check(first.Execute("help", 1800).Message == "one line", "Output is captured once, not once per owner.");
        Check(!Terminal.m_cheat, "Previous false cheat state restored.");
        Terminal.m_cheat = true;
        second.Execute("help", 1800);
        Check(Terminal.m_cheat, "Previous true cheat state restored.");
        Terminal.m_cheat = false;

        int calls = terminal.Calls;
        var worker = Task.Run(() => first.Execute("help", 1800)).GetAwaiter().GetResult();
        Check(worker.Code == "rcon_unavailable" && terminal.Calls == calls, "Worker rejected before game command execution.");
        ZNet.instance.Server = false;
        Check(!first.Execute("help", 1800).Success, "Remote client cannot invoke console.");
        ZNet.instance.Server = true;
        ZNet.World = null;
        Check(!first.Execute("help", 1800).Success, "World must exist.");
        ZNet.World = new object();
        Check(first.Execute("missing", 1800).Code == "rcon_unknown", "Unregistered command rejected.");
        Check(first.Execute("save extra", 1800).Code == "invalid_argument", "Save requires exact shape.");
        Check(first.Execute("kick", 1800).Code == "invalid_argument", "Kick needs target.");
        Check(first.Execute("kick " + new string('x', 129), 1800).Code == "invalid_argument", "Kick target bounded.");
        Check(first.Execute("kick halla " + new string('x', 301), 1800).Code == "invalid_argument", "Kick reason bounded.");

        terminal.OnExecute = line => { throw new InvalidOperationException("not logged"); };
        Check(first.Execute("help", 1800).Code == "rcon_failed", "Thrown commands fail without replay.");
        Check(!Terminal.m_cheat, "Throw restores cheat state.");
        terminal.OnExecute = line => terminal.AddString("Error executing command.");
        Check(!first.Execute("help", 1800).Success, "Console failure text is not success.");
        terminal.OnExecute = line => terminal.AddString(new string('x', 3000));
        var clipped = first.Execute("help", 1800);
        Check(clipped.Message.Length == 1800 && clipped.Message.EndsWith("[output truncated]"), "Output plus truncation marker bounded.");
        terminal.OnExecute = line =>
        {
            Task.Run(() => terminal.AddString("foreign worker")).GetAwaiter().GetResult();
            new Terminal().AddString("foreign terminal");
            terminal.AddString("ok\r\nnext\tline");
        };
        Check(first.Execute("help", 1800).Message == "ok\nnext line", "Thread/terminal filters and control normalization preserved.");
        terminal.OnExecute = line =>
        {
            Check(second.Execute("help", 1800).Code == "rcon_busy", "Cross-caller reentrancy is rejected.");
            terminal.AddString("outer");
        };
        Check(first.Execute("help", 1800).Message == "outer", "Reentrant rejection does not replace capture.");

        terminal.OnExecute = line => terminal.AddString("done");
        var save = first.Execute("save", 1800, out bool observed);
        Check(observed && save.Code == "save_requested", "Save result describes request, not completion.");
        Check(ServerEventRuntime.PreviousSave == "previous" && terminal.LastLine == "save", "Existing save observation path preserved.");
        var kick = first.Execute("kick halla time to leave", 1800, out observed);
        Check(observed && kick.Code == "kick_requested", "Kick uses existing typed result.");
        Check(ServerEventRuntime.KickTarget == "halla" && ServerEventRuntime.KickReason == "time to leave" &&
            terminal.LastLine == "kick 76561190000000000", "Kick grace resolves target before vanilla dispatch.");
        using (var adapter = new DiscordRconCapture(log))
        {
            Check(adapter.Execute("save", 1800).Message == "observed:save_requested", "Discord observed-result presentation retained.");
            Check(adapter.Execute("help", 1800).Message == "done", "Discord raw output presentation retained.");
            Check(HarmonyLib.Harmony.Patches == 1, "Discord and scheduler do not add duplicate hooks.");
        }
        Check(HarmonyLib.Harmony.Unpatches == 0, "Adapter disposal leaves scheduler hook alive.");
        terminal.OnExecute = line =>
        {
            second.Dispose();
            terminal.AddString("after other dispose");
            Check(HarmonyLib.Harmony.Unpatches == 0, "Other disposal cannot remove active hook.");
            first.Dispose();
            terminal.AddString("after own dispose");
            Check(HarmonyLib.Harmony.Unpatches == 0, "Final owner defers unpatch until command finally.");
        };
        Check(first.Execute("help", 1800).Message == "after other dispose\nafter own dispose", "Disposal preserves current capture.");
        Check(HarmonyLib.Harmony.Unpatches == 1, "Final execution releases hook once.");
        first.Dispose(); second.Dispose();
        Check(HarmonyLib.Harmony.Unpatches == 1, "Disposal is idempotent.");
        Check(first.Execute("help", 1800).Code == "rcon_unavailable", "Disposed executor cannot execute.");

        HarmonyLib.Harmony.FailPatch = true;
        using (var failed = new ServerConsoleExecutor(log))
            Check(!failed.IsAvailable, "Capture setup failure is local and non-throwing.");
        Check(logs.Last().Contains("InvalidOperationException") && !logs.Last().Contains("private detail"), "Setup logs only exception type.");
        HarmonyLib.Harmony.FailPatch = false;
        var recovered = new ServerConsoleExecutor(log);
        Check(recovered.IsAvailable, "Cleanly removed failed hook can be retried by a later owner.");
        HarmonyLib.Harmony.FailUnpatch = true;
        recovered.Dispose();
        int attempts = HarmonyLib.Harmony.Patches;
        using (var uncertain = new ServerConsoleExecutor(log))
            Check(!uncertain.IsAvailable, "Uncertain cleanup disables raw console only.");
        Check(HarmonyLib.Harmony.Patches == attempts, "Uncertain cleanup never installs duplicate hook.");
        System.Console.WriteLine("ServerConsoleExecutorSmoke: PASS (" + _checks + " assertions; source-linked execution/capture ownership, stub game/Harmony boundaries).");
        return 0;
    }
}

public class Terminal
{
    public static bool m_cheat;
    public void AddString(string value) => HarmonyLib.Harmony.Postfix?.Invoke(null, new object[] { this, value });
}

public class Console : Terminal
{
    public static Console? instance;
    public Action<string>? OnExecute;
    public int Calls;
    public string LastLine = "";
    public void TryRunCommand(string line, bool silentFail, bool skipAllowedCheck)
    {
        if (silentFail || !skipAllowedCheck) throw new Exception("Console call flags changed.");
        Calls++; LastLine = line; OnExecute?.Invoke(line);
    }
}

public class ZNet
{
    public static ZNet? instance;
    public static object? World = new object();
    public bool Server = true;
    public bool IsServer() => Server;
}

namespace HarmonyLib
{
    public sealed class Harmony
    {
        public static int Patches, Unpatches;
        public static bool FailPatch, FailUnpatch;
        public static MethodInfo? Postfix;
        public Harmony(string id) { }
        public void Patch(MethodInfo original, HarmonyMethod postfix)
        {
            Patches++;
            if (FailPatch) throw new InvalidOperationException("private detail");
            if (Postfix != null) throw new Exception("Duplicate postfix installation.");
            Postfix = postfix.Method;
        }
        public void UnpatchSelf()
        {
            Unpatches++;
            if (FailUnpatch) throw new InvalidOperationException("private detail");
            Postfix = null;
        }
    }
    public sealed class HarmonyMethod
    {
        public readonly MethodInfo Method;
        public HarmonyMethod(Type type, string name) => Method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    }
    public static class AccessTools
    { public static MethodInfo? Method(Type type, string name, Type[] parameters) => type.GetMethod(name, parameters); }
}

namespace ServerManager
{
    internal static class IntegrityCanonical
    { internal static bool IsFatal(Exception value) => value is OutOfMemoryException || value is StackOverflowException || value is AccessViolationException; }
    internal static class ValheimPrivateAccess
    {
        internal static readonly Dictionary<string, object> Commands = new();
        internal static Dictionary<string, object> GetTerminalCommands() => Commands;
    }
}

namespace ServerManager.Commands
{
    internal static class ServerCommands
    {
        internal static bool TryTokenize(string line, out string[] arguments, out string error)
        { arguments = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); error = ""; return true; }
    }
}

namespace ServerManager.Events
{
    internal sealed class ServerManagerCommandResult
    {
        internal ServerManagerCommandResult(bool success, string code, string message, string operationId, IDictionary<string, string>? data)
        { Success = success; Code = code; Message = message; }
        internal bool Success { get; }
        internal string Code { get; }
        internal string Message { get; }
    }
    internal sealed class Save { internal string OperationId => "previous"; }
    internal sealed class Status { internal Save? LatestSave => new Save(); }
    internal static class ServerEventRuntime
    {
        internal static string PreviousSave = "", KickTarget = "", KickReason = "";
        internal static Status GetStatusSnapshot() => new();
        internal static ServerManagerCommandResult DescribeWorldSaveRequest(string previous)
        { PreviousSave = previous; return new(true, "save_requested", "requested", "", null); }
        internal static ServerManagerCommandResult ExecuteConsoleKick(string target, string reason, Func<string, bool> run)
        { KickTarget = target; KickReason = reason; return new(run("76561190000000000"), "kick_requested", "requested", "", null); }
    }
}

namespace ServerManager.Discord
{
    internal static class DiscordCommands
    {
        internal sealed class Result
        {
            internal Result(bool success, string code, string message) { Success = success; Code = code; Message = message; }
            internal bool Success { get; }
            internal string Code { get; }
            internal string Message { get; }
        }
        internal static Result FormatIntegrationResult(ServerManagerCommandResult result) =>
            new(result.Success, result.Code, "observed:" + result.Code);
    }
}
