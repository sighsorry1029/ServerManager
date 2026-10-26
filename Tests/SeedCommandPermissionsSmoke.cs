// The production permission hooks are source-linked and patched with real
// Harmony against managed-only Terminal fixtures. No Unity/game is executed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ServerManager;

internal static class SeedCommandPermissionsSmoke
{
    private const string Owner = "ServerManager.Tests.SeedPermissions";
    private const string ForeignOwner = "ServerManager.Tests.SeedPermissions.Foreign";
    private static int _checks;
    private static int _foreignCalls;

    private static int Main()
    {
        try
        {
            Run();
            System.Console.WriteLine("SeedCommandPermissionsSmoke: PASS (" + _checks +
                " assertions; source-linked permissions, real Harmony, managed Terminal fixture; no live Unity).");
            return 0;
        }
        catch (Exception exception)
        {
            System.Console.Error.WriteLine(exception);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var harmony = new Harmony(Owner);
        var foreign = new Harmony(ForeignOwner);
        MethodInfo runCommand = AccessTools.Method(typeof(Terminal), nameof(Terminal.TryRunCommand));
        MethodInfo showCommand = AccessTools.Method(typeof(Terminal.ConsoleCommand), nameof(Terminal.ConsoleCommand.ShowCommand));
        try
        {
            harmony.PatchAll(typeof(ClientCommandPermissions).Assembly);
            foreign.Patch(runCommand, prefix: new HarmonyMethod(typeof(SeedCommandPermissionsSmoke), nameof(ObserveForeign)));
            foreign.Patch(showCommand, postfix: new HarmonyMethod(typeof(SeedCommandPermissionsSmoke), nameof(HideForeign)));
            Check(Harmony.GetPatchInfo(runCommand).Owners.Contains(Owner), "Actual Harmony execution hook installed.");

            var terminal = new Terminal();
            var chat = new Chat();
            Terminal.Commands.Clear();
            var seed = new Terminal.ConsoleCommand("printseeds");
            var unrelated = new Terminal.ConsoleCommand("other");
            var hidden = new Terminal.ConsoleCommand("foreign_hidden");
            new Terminal.ConsoleCommand("help");
            ZNet.instance = new ZNet { Server = false };

            ClientCommandPermissions.Reset();
            terminal.TryRunCommand("printseeds");
            Check(terminal.Executions == 1 && seed.ShowCommand(terminal), "Non-ServerManager remote servers unchanged.");
            ClientCommandPermissions.ApplyAdminStatus(true, 5000);
            Check(!ClientCommandPermissions.HasActiveAdminStatus(), "Grant outside a managed session cannot survive into one.");
            terminal.Pump("pri");
            Check(terminal.Search.Contains("printseeds"), "Initial vanilla autocomplete contains seed command.");

            ClientCommandPermissions.BeginSession();
            Check(!ClientCommandPermissions.HasActiveAdminStatus(), "New session starts without authority.");
            terminal.TryRunCommand("printseeds");
            terminal.TryRunCommand("PRINTSEEDS  arguments", false, true);
            Check(terminal.Executions == 1 && terminal.Messages.Count == 2,
                "F5 uppercase/arguments and skipAllowedCheck cannot bypass permissions.");
            Check(terminal.Messages.All(message => message == "sm_command_admin_required"),
                "Refusal is only the localized permission message.");
            chat.Enter("/printseeds");
            Check(chat.Executions == 0 && chat.Messages.Count == 1,
                "Slash chat command is blocked with refusal even when Chat supplies silentFail=true.");
            Check(!seed.ShowCommand(terminal) && unrelated.ShowCommand(terminal), "Help filter targets only printseeds.");
            Check(!hidden.ShowCommand(terminal), "Another owner's visibility restriction remains in place.");
            terminal.TryRunCommand("help");
            Check(!terminal.LastHelp.Contains("printseeds") && terminal.LastHelp.Contains("other"),
                "Help enumeration really excludes the command.");

            terminal.Pump("pri");
            Check(!terminal.Search.Contains("printseeds") && !terminal.TabOptions.Contains("printseeds"),
                "Beginning a session invalidates stale same-length search and tab candidates.");
            int rebuilds = terminal.Rebuilds;
            terminal.Pump("pri");
            Check(terminal.Rebuilds == rebuilds, "Steady permission state does not rebuild cached command lists per frame.");
            terminal.TryRunCommand("other");
            Check(terminal.LastExecuted == "other", "Unrelated command execution remains available.");

            Check(ClientCommandPermissions.IsSeedCommand("printseeds") &&
                  ClientCommandPermissions.IsSeedCommand("PRINTSEEDS x") &&
                  ClientCommandPermissions.IsSeedCommand("printseeds "), "Vanilla space/case forms match.");
            foreach (string candidate in new[] { " printseeds", "/printseeds", "\tprintseeds", "printseeds\tx", "printseedsx", "", "say printseeds" })
                Check(!ClientCommandPermissions.IsSeedCommand(candidate), "Not a vanilla seed command: " + candidate);
            Check(!ClientCommandPermissions.IsSeedCommand(null!), "Null parser input is safe.");
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                Check(!ClientCommandPermissions.IsSeedCommand("PRINTSEEDS"), "Current-culture lowercasing exactly follows Terminal, including Turkish I.");
                Check(ClientCommandPermissions.IsSeedCommand("printseeds"), "Lowercase command remains valid in Turkish culture.");
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }

            ClientCommandPermissions.ApplyAdminStatus(true, 5000);
            Check(ClientCommandPermissions.HasActiveAdminStatus() && seed.ShowCommand(terminal),
                "Validated admin grant allows seed permissions without a cheat-policy flag.");
            terminal.TryRunCommand("printseeds");
            chat.Enter("/printseeds");
            Check(terminal.LastExecuted == "printseeds" && chat.Executions == 1, "Admin F5/chat command execution unchanged.");
            terminal.Pump("pri");
            Check(terminal.Search.Contains("printseeds") && terminal.TabOptions.Contains("printseeds"),
                "Grant restores cached autocomplete even at the same input length.");
            rebuilds = terminal.Rebuilds;
            ClientCommandPermissions.ApplyAdminStatus(true, 5000);
            terminal.Pump("pri");
            Check(terminal.Rebuilds == rebuilds, "Grant refresh does not churn UI caches.");
            seed.Visible = false;
            Check(!seed.ShowCommand(terminal), "Admin grant never overrides an existing vanilla visibility restriction.");
            seed.Visible = true;

            FieldInfo expiry = typeof(ClientCommandPermissions).GetField("_adminEntitlementExpiryTimestamp", BindingFlags.Static | BindingFlags.NonPublic)!;
            expiry.SetValue(null, 1L);
            terminal.Pump("pri");
            Check(!ClientCommandPermissions.HasActiveAdminStatus() && !terminal.Search.Contains("printseeds"),
                "Expired grant removes unchanged-input autocomplete without receiving another packet.");
            int executions = terminal.Executions;
            terminal.TryRunCommand("printseeds");
            Check(terminal.Executions == executions, "Expired authority blocks execution.");
            ClientCommandPermissions.ApplyAdminStatus(true, 5000);
            terminal.Pump("pri");
            ClientCommandPermissions.ApplyAdminStatus(false, 0);
            terminal.Pump("pri");
            Check(!terminal.Search.Contains("printseeds"), "Explicit revocation invalidates autocomplete.");
            AssertInvalidGrant(0);
            AssertInvalidGrant(999);
            AssertInvalidGrant(10001);
            ClientCommandPermissions.ApplyAdminStatus(true, 1000);
            Check(ClientCommandPermissions.HasActiveAdminStatus(), "Minimum accepted grant works.");
            ClientCommandPermissions.ApplyAdminStatus(true, 10000);
            Check(ClientCommandPermissions.HasActiveAdminStatus(), "Maximum accepted grant works.");

            ClientCommandPermissions.BeginSession();
            Check(!ClientCommandPermissions.HasActiveAdminStatus(), "Reconnect clears previous authority.");
            terminal.Pump("pri");
            rebuilds = terminal.Rebuilds;
            ClientCommandPermissions.Reset();
            ClientCommandPermissions.BeginSession();
            terminal.Pump("pri");
            Check(terminal.Rebuilds == rebuilds + 1, "Reconnect invalidates caches even if effective role stayed non-admin.");

            seed.IsCheat = true;
            seed.OnlyAdmin = true;
            seed.RemoteCommand = true;
            terminal.TryRunCommand("printseeds");
            Check(seed.IsCheat && seed.OnlyAdmin && seed.RemoteCommand, "Permission hooks never mutate foreign command flags.");
            Check(!seed.ShowCommand(terminal), "Foreign remote-command flag cannot expose denied seed command.");
            ZNet.instance.Server = true;
            terminal.TryRunCommand("printseeds");
            Check(terminal.LastExecuted == "printseeds" && seed.ShowCommand(terminal),
                "Local host, single-player, and dedicated console server paths are unchanged.");
            ZNet.instance = null;
            terminal.TryRunCommand("printseeds");
            Check(seed.ShowCommand(terminal), "No-network context remains unchanged.");
            ZNet.instance = new ZNet { Server = false };
            ClientCommandPermissions.Reset();
            terminal.Pump("pri");
            Check(terminal.Search.Contains("printseeds"), "Reset restores vanilla autocomplete for later unmanaged sessions.");

            Check(_foreignCalls > 0 && Harmony.GetPatchInfo(runCommand).Owners.Contains(ForeignOwner),
                "Other owner's execution patch is preserved.");
            harmony.UnpatchSelf();
            Check(Harmony.GetPatchInfo(runCommand).Owners.Contains(ForeignOwner), "Removing only our hooks preserves other owners.");
        }
        finally { ClientCommandPermissions.Reset(); harmony.UnpatchSelf(); foreign.UnpatchSelf(); }
    }

    private static void AssertInvalidGrant(int milliseconds)
    {
        bool threw = false;
        try { ClientCommandPermissions.ApplyAdminStatus(true, milliseconds); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "Invalid grant rejected: " + milliseconds);
    }

    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void ObserveForeign() => _foreignCalls++;
    private static void HideForeign(Terminal.ConsoleCommand __instance, ref bool __result)
    { if (__instance.Command == "foreign_hidden") __result = false; }
}

public class Terminal
{
    public static readonly Dictionary<string, ConsoleCommand> Commands = new Dictionary<string, ConsoleCommand>();
    // Private access levels intentionally match original 1.0.20 metadata.
    private readonly List<string> m_commandList = new List<string>();
    private readonly List<string> m_tabOptions = new List<string>();
    private readonly List<string> m_lastSearch = new List<string>();
    private int m_tabCaretPosition = -1;
    private int m_lastSearchLength;
    private string _input = "";
    public readonly List<string> Messages = new List<string>();
    public readonly List<string> LastHelp = new List<string>();
    public int Executions, Rebuilds;
    public string LastExecuted = "";
    public List<string> Search => m_lastSearch;
    public List<string> TabOptions => m_tabOptions;
    public void AddString(string message) => Messages.Add(message);
    public void Pump(string input) { _input = input; UpdateInput(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void TryRunCommand(string text, bool silentFail = false, bool skipAllowedCheck = false)
    {
        if (!Commands.TryGetValue(text.Split(' ')[0].ToLower(), out ConsoleCommand command)) return;
        Executions++; LastExecuted = command.Command;
        if (command.Command == "help")
        {
            LastHelp.Clear();
            foreach (var entry in Commands.Values)
                if (entry.ShowCommand(this)) LastHelp.Add(entry.Command);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void UpdateInput()
    {
        if (m_commandList.Count == 0)
        {
            Rebuilds++;
            foreach (var command in Commands.Values)
                if (command.ShowCommand(this)) m_commandList.Add(command.Command);
        }
        if (m_tabCaretPosition == -1)
        {
            m_tabOptions.Clear();
            m_tabOptions.AddRange(m_commandList.Where(command => command.StartsWith(_input, StringComparison.Ordinal)));
            m_tabCaretPosition = _input.Length;
        }
        if (m_lastSearchLength == _input.Length) return;
        m_lastSearchLength = _input.Length;
        m_lastSearch.Clear();
        m_lastSearch.AddRange(m_commandList.Where(command => command.Contains(_input)));
    }

    public class ConsoleCommand
    {
        public readonly string Command;
        public bool IsCheat, OnlyAdmin, RemoteCommand;
        public bool Visible = true;
        public ConsoleCommand(string command) { Command = command; Commands.Add(command.ToLower(), this); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ShowCommand(Terminal context) => Visible;
    }
}

public sealed class Chat : Terminal
{
    public void Enter(string input) => TryRunCommand(input[0] == '/' ? input.Substring(1) : "say " + input, true);
}

public sealed class ZNet
{
    public static ZNet? instance;
    public bool Server;
    public bool IsServer() => Server;
}

namespace ServerManager
{
    internal static class AdminCommandEntitlementCodec
    {
        internal const int MinimumGrantMilliseconds = 1000;
        internal const int MaximumGrantMilliseconds = 10000;
    }
    internal static class PlayerLocalizer
    {
        internal static string Text(string key, params string[] args) => key;
    }
}
