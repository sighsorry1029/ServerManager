using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerManager.Events;
using UnityEngine;

namespace ServerManager.Commands;

/// <summary>Adapters supply authenticated identity, never identity parsed from command text.
/// The authorization callback is invoked only on Unity's main thread and must recheck
/// the current session and permissions. Cancellation retires an adapter/session.</summary>
internal sealed class CommandCaller
{
    internal CommandCaller(string source, string id, string name,
        Func<bool> isAuthorized, CancellationToken cancellation = default)
    {
        Source = ServerCommands.BoundedText(source, 32);
        Id = ServerCommands.BoundedText(id, 96);
        Name = ServerCommands.BoundedText(name, 96);
        IsAuthorized = isAuthorized ?? throw new ArgumentNullException(nameof(isAuthorized));
        Cancellation = cancellation;
    }

    internal string Source { get; }
    internal string Id { get; }
    internal string Name { get; }
    internal CancellationToken Cancellation { get; }
    internal Func<bool> IsAuthorized { get; }
}

/// <summary>One bounded parser and main-thread executor for terminal, client, and
/// Discord adapters. It never executes arbitrary Valheim console commands.</summary>
internal static class ServerCommands
{
    // A 500-character Discord argument may double when quote-escaped by an adapter.
    internal const int MaximumLineLength = 2048;
    internal const int MaximumArguments = 16;
    internal const int MaximumOutputLength = 1800;
    private const int Capacity = 64;
    private const int PerTick = 8;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly object Gate = new();
    private static readonly Queue<Work> Queue = new();
    private static readonly Queue<Audit> Audits = new();
    private static readonly List<Work> Running = new();
    private static bool _initialized;
    private static int _outstanding;
    private static long _generation;

    // Public names, internal dispatch prefixes, and terminal help share one
    // registry. Hierarchical tokens never enter through the public parser.
    private static readonly CommandSpec[] Catalog =
    {
        new("status", "status"),
        new("players", "players", "[target]", true),
        new("announce", "announce", "<message>"),
        new("chat", "chat", "<message> (global shout)"),
        new("adminlist", "admin list"),
        new("adminadd", "admin add", "<Steam64>"),
        new("adminremove", "admin remove", "<Steam64>"),
        new("accesslist", "access list"),
        new("accessadd", "access add", "<Steam64>"),
        new("accessremove", "access remove", "<Steam64>"),
        new("banlist", "ban list"),
        new("keylist", "key list"),
        new("keyadd", "key add", "<key>"),
        new("keyremove", "key remove", "<key>"),
        new("eventstart", "event start", "<name> <x> <y> <z>"),
        new("eventstop", "event stop"),
        new("characterlist", "character list"),
        new("characterinfo", "character info", "<target>", true),
        new("characterbackups", "character backups", "<target> [page]", true),
        new("characterrestore", "character restore", "<target> <backupId>", true),
        new("giveitem", "item give", "<target> <prefab> <amount> [quality] [dataId]", true),
        new("teleport", "teleport", "<target> (<x> <y> <z> | to <player>)", true),
        new("skillget", "skill get", "<target> [skill]", true),
        new("skillset", "skill set", "<target> <skill> <value>", true),
        new("heal", "heal", "<target> <amount>", true),
        new("damage", "damage", "<target> <amount>", true),
        new("modsstatus", "mods status"),
        new("modsreload", "mods reload"),
        new("discordstatus", "discord status"),
        new("discordtest", "discord test"),
        new("cronstatus", "cronstatus"),
        new("cronack", "cronack", "<jobId>"),
        new("help", "help")
    };
    private static readonly IReadOnlyDictionary<string, CommandSpec> CommandsByName =
        Catalog.ToDictionary(spec => spec.Name, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> FlatCommandNames { get; } =
        Array.AsReadOnly(Catalog.Select(spec => spec.Name).ToArray());

    internal static string GetCommandSyntax(string name) =>
        name != null && CommandsByName.TryGetValue(name, out CommandSpec spec) ? spec.Syntax : "";

    internal static bool HasPlayerFirstArgument(string name) =>
        name != null && CommandsByName.TryGetValue(name, out CommandSpec spec) && spec.PlayerFirst;

    internal static void Initialize()
    {
        Shutdown();
        lock (Gate) _initialized = true;
    }

    internal static void Shutdown()
    {
        lock (Gate)
        {
            _initialized = false;
            ++_generation;
            while (Queue.Count != 0)
                Complete(Queue.Dequeue(), Failure("runtime_stopped", "The command runtime stopped."));
            foreach (Work work in Running)
                Complete(work, Failure("runtime_stopped", "The command runtime stopped."));
            Running.Clear();
            DrainAudits();
        }
    }

    internal static Task<ServerManagerCommandResult> ExecuteAsync(string line, CommandCaller caller)
    {
        if (caller == null) throw new ArgumentNullException(nameof(caller));
        if (!TryTokenize(line, out string[] args, out string error))
            return Reject(caller, "invalid", "", Failure("invalid_argument", error));
        if (!TryPrepareArguments(line, args, out args, out string action, out error))
            return Reject(caller, action == "characterrestore" ? action : "invalid", "",
                WithSelectedDataId(args, Failure("invalid_command", error)));
        string target = Target(args);
        if (caller.Cancellation.IsCancellationRequested)
            return Reject(caller, action, target, WithSelectedDataId(args,
                Failure("cancelled", "The requesting session was retired.")));
        lock (Gate)
        {
            if (!_initialized || _outstanding >= Capacity)
                return Reject(caller, action, target, WithSelectedDataId(args,
                    Failure(_initialized ? "queue_full" : "runtime_unavailable",
                        _initialized ? "The command queue is full." : "The command runtime is unavailable.")));
            Work work = new(args, caller, action, target, _generation, ServerEventRuntime.CommandWorldEpoch);
            Queue.Enqueue(work);
            ++_outstanding;
            return work.Completion.Task;
        }
    }

    // Polling completed tasks here avoids accessing Unity or the event publisher
    // from worker continuations; outstanding queued + running work is bounded.
    internal static void Tick()
    {
        DrainAudits();
        if (!_initialized) return;
        for (int i = Running.Count - 1; i >= 0; --i)
        {
            Work work = Running[i];
            if (work.Pending == null || !work.Pending.IsCompleted) continue;
            Running.RemoveAt(i);
            FinishTask(work);
        }
        for (int i = 0; i < PerTick; ++i)
        {
            Work work;
            lock (Gate)
            {
                if (Queue.Count == 0) break;
                work = Queue.Dequeue();
            }
            try
            {
                ServerManagerCommandResult? denied = Check(work);
                if (denied != null) { Complete(work, denied); continue; }
                work.Pending = Execute(work);
                if (work.Pending.IsCompleted) FinishTask(work);
                else Running.Add(work);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                Complete(work, Failure("command_failed", "The command failed (" + exception.GetType().Name + ")."));
            }
        }
    }

    private static ServerManagerCommandResult? Check(Work work)
    {
        if (work.Caller.Cancellation.IsCancellationRequested)
            return Failure("cancelled", "The requesting session was retired.");
        if (!_initialized || work.Generation != _generation ||
            work.WorldEpoch != ServerEventRuntime.CommandWorldEpoch)
            return Failure("world_changed", "The server world stopped or changed.");
        if (!Authorized(work.Caller))
            return Failure("unauthorized", "The caller is not authorized for this command.");
        if (NeedsWorld(work.Args))
        {
            ServerManagerStatusSnapshot status = ServerEventRuntime.GetStatusSnapshot();
            if (!status.IsServer || !status.ServerStarted || !status.WorldReady || status.ShutdownStarted ||
                ZNet.instance == null || !ZNet.instance.IsServer())
                return Failure("server_not_ready", "The server world is not ready.");
        }
        return null;
    }

    private static bool Authorized(CommandCaller caller)
    {
        if (caller.Cancellation.IsCancellationRequested) return false;
        try { return caller.IsAuthorized(); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { return false; }
    }

    private static Task<ServerManagerCommandResult> Execute(Work work)
    {
        string[] a = work.Args;
        switch (a[0])
        {
            case "help": return Done(Success("help", Help));
            case "status":
                ServerManagerStatusSnapshot status = ServerEventRuntime.GetStatusSnapshot();
                return Done(Success("status", FormatStatus(status)));
            case "players":
                // A selected online player uses the same guarded runtime path
                // as other targeted commands, without reading character files.
                if (a.Length == 2) goto default;
                return Done(Success("players", ListText(ServerEventRuntime.GetStatusSnapshot().Players
                    .Select(p => p.PlayerName + " | " + p.AccountId))));
            case "announce": return Existing(work, ServerEventCommandKind.Announce, Join(a, 1), "");
            case "ban":
                if (a.Length == 2 && a[1] == "list") return Done(EditList(a, "m_bannedList"));
                return Existing(work, ServerEventCommandKind.Ban, a[1], Join(a, 2));
            case "unban": return Existing(work, ServerEventCommandKind.Unban, a[1], "");
            case "admin": return Done(EditList(a, "m_adminList"));
            case "access": return Done(EditList(a, "m_permittedList"));
            case "key": return Done(EditKey(a));
            case "event": return Done(EditEvent(a));
            case "cronstatus": return Done(ServerScheduleRuntime.GetStatus());
            case "cronack":
                if (work.Caller.Source == "cron")
                    return Done(Failure("cron_ack_denied", "Scheduled commands cannot acknowledge interrupted jobs."));
                return ServerScheduleRuntime.Acknowledge(a[1], () => Check(work) == null, work.Caller.Cancellation);
            default:
                // Preserve the original identity/token while preventing a delayed
                // managed-client operation from escaping this world's lifetime.
                CommandCaller guarded = new(work.Caller.Source, work.Caller.Id, work.Caller.Name,
                    () => Check(work) == null, work.Caller.Cancellation);
                return ServerManagerRuntime.ExecuteManagedAdminCommandAsync(a, guarded);
        }
    }

    private static Task<ServerManagerCommandResult> Existing(Work work,
        ServerEventCommandKind kind, string primary, string secondary) =>
        ServerEventRuntime.EnqueueCommand(kind, primary, secondary,
            work.Caller.Cancellation, () => Check(work) == null);

    internal static string FormatStatus(ServerManagerStatusSnapshot status) =>
        "Server: " + BoundedText(status.ServerName, 128) + "\nWorld: " + BoundedText(status.WorldName, 128) +
        "\nPlayers: " + status.ReadyPlayerCount.ToString(CultureInfo.InvariantCulture) +
        "\nReady: " + status.WorldReady + "\nServerManager: " + ServerManagerIntegrationApi.PluginVersion +
        "\n" + FormatSaveStatus(status.LatestSave);

    private static string FormatSaveStatus(ServerManagerSaveOperationSnapshot? save)
    {
        string text = "save_operation_id: " + (save == null ? "none" : BoundedText(save.OperationId, 128)) +
            "\nsave_state: " + (save == null ? "None" : save.State.ToString()) +
            "\nsave_character_commit_scope: " + (save == null ? "NotIncluded" : save.CharacterCommitScope.ToString()) +
            "\nsave_captured_character_count: " + (save?.CapturedCharacterCount ?? 0).ToString(CultureInfo.InvariantCulture) +
            "\nsave_persisted_character_count: " + (save?.PersistedCharacterCount ?? 0).ToString(CultureInfo.InvariantCulture) +
            "\nsave_pending_character_count: " + (save?.PendingCharacterCount ?? 0).ToString(CultureInfo.InvariantCulture);
        if (save != null)
        {
            text += "\nsave_started_at_utc: " + save.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture);
            if (save.CompletedAtUtc.HasValue)
                text += "\nsave_completed_at_utc: " + save.CompletedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(save.Error)) text += "\nsave_error: " + BoundedText(save.Error, 256);
        }
        return text;
    }

    private static ServerManagerCommandResult EditList(string[] a, string fieldName)
    {
        ZNet server = ZNet.instance;
        SyncedList? list = typeof(ZNet).GetField(fieldName, Members)?.GetValue(server) as SyncedList;
        if (list == null) return Failure("list_unavailable", "The server list is unavailable.");
        if (a[1] == "list") return Success(a[0] + "_list", ListText(list.GetList()));
        bool add = a[1] == "add";
        string[] existing = GetSteamListEntries(list.GetList(), a[2]);
        if (add)
        {
            // Either native spelling already grants the same identity. Do not
            // create an alias duplicate or rewrite existing configuration.
            if (existing.Length == 0) list.Add(a[2]);
        }
        else
        {
            // SyncedList.Remove removes only one exact occurrence. Loaded
            // files may contain duplicates and both supported Steam spellings.
            foreach (string entry in existing) list.Remove(entry);
        }
        if ((GetSteamListEntries(list.GetList(), a[2]).Length != 0) != add)
            return Failure("list_update_unconfirmed", "The requested account's list membership could not be confirmed.");
        // SyncedList.Add/Remove already persist the exact literal identity.
        if (a[0] == "admin")
            typeof(ZNet).GetMethod("SendAdminList", Members, null, Type.EmptyTypes, null)?.Invoke(server, null);
        string message = a[0] + " list " + a[1] + " completed for " + a[2] + ".";
        if (a[0] == "access" && list.GetList().Count == 0)
            message += " The access list is empty: Valheim now allows all accounts.";
        return Success(a[0] + "_updated", message);
    }

    // Match only known literal spellings accepted by ZNet.ListContainsId.
    // Preserve every occurrence so callers can remove duplicates one by one;
    // never reinterpret a numeric player name or migrate unrelated list entries.
    internal static string[] GetSteamListEntries(IEnumerable<string> entries, string accountId)
    {
        string bare = accountId.StartsWith("Steam_", StringComparison.Ordinal)
            ? accountId.Substring(6) : accountId;
        bool steam = IsSteam64(bare);
        return entries.Where(entry => string.Equals(entry, accountId, StringComparison.Ordinal) ||
            steam && (string.Equals(entry, bare, StringComparison.Ordinal) ||
                      string.Equals(entry, "Steam_" + bare, StringComparison.Ordinal))).ToArray();
    }

    private static ServerManagerCommandResult EditKey(string[] a)
    {
        ZoneSystem system = ZoneSystem.instance;
        if (system == null) return Failure("world_not_ready", "The global-key system is unavailable.");
        if (a[1] == "list") return Success("key_list", ListText(system.GetGlobalKeys()));
        if (a[1] == "add") system.SetGlobalKey(a[2]); else system.RemoveGlobalKey(a[2]);
        return Success("key_updated", "Global key " + a[1] + " requested for " + a[2] + ".");
    }

    private static ServerManagerCommandResult EditEvent(string[] a)
    {
        RandEventSystem system = RandEventSystem.instance;
        if (system == null) return Failure("event_unavailable", "The event system is unavailable.");
        if (a[1] == "stop")
        {
            MethodInfo? reset = typeof(RandEventSystem).GetMethod("ResetRandomEvent", Members, null, Type.EmptyTypes, null);
            if (reset == null) return Failure("event_unavailable", "The event reset API is unavailable.");
            reset.Invoke(system, null);
            return Success("event_stopped", "The random event was stopped.");
        }
        MethodInfo? lookup = typeof(RandEventSystem).GetMethod("GetEvent", Members, null, new[] { typeof(string) }, null);
        MethodInfo? start = typeof(RandEventSystem).GetMethod("SetRandomEventByName", Members, null,
            new[] { typeof(string), typeof(Vector3) }, null);
        if (lookup == null || start == null)
            return Failure("event_unavailable", "The event start API is unavailable.");
        if (lookup.Invoke(system, new object[] { a[2] }) == null)
            return Failure("event_not_found", "No enabled event has that exact name.");
        Vector3 position = new(ParseFloat(a[3]), ParseFloat(a[4]), ParseFloat(a[5]));
        start.Invoke(system, new object[] { a[2], position });
        return Success("event_started", "Event started: " + a[2] + ".");
    }

    internal static bool TryTokenize(string line, out string[] args, out string error)
    {
        args = Array.Empty<string>();
        error = "";
        if (line == null || line.Length > MaximumLineLength || line.Any(char.IsControl))
        { error = "Command text must contain at most 2048 characters and no control characters."; return false; }
        List<string> tokens = new();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) ++i;
            if (i == line.Length) break;
            if (tokens.Count >= MaximumArguments)
            { error = "A command may contain at most 16 arguments."; return false; }
            StringBuilder token = new();
            bool quoted = line[i] == '"';
            if (quoted)
            {
                ++i;
                bool closed = false;
                while (i < line.Length)
                {
                    char c = line[i++];
                    if (c == '"') { closed = true; break; }
                    if (c == '\\')
                    {
                        if (i == line.Length || (line[i] != '\\' && line[i] != '"'))
                        { error = "Quoted arguments support only escaped quotes and backslashes."; return false; }
                        c = line[i++];
                    }
                    token.Append(c);
                }
                if (!closed || (i < line.Length && !char.IsWhiteSpace(line[i])))
                { error = "Quoted arguments must be closed and separated by spaces."; return false; }
            }
            else
            {
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    if (line[i] == '"')
                    { error = "Quotes must surround an entire argument."; return false; }
                    token.Append(line[i++]);
                }
            }
            if (token.Length == 0)
            { error = "Empty arguments are not allowed."; return false; }
            tokens.Add(token.ToString());
        }
        args = tokens.ToArray();
        return true;
    }

    internal static string Quote(string value) => "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    internal static bool TryParseCommand(string line, out string[] args, out string error)
    {
        if (!TryTokenize(line, out args, out error)) return false;
        return TryPrepareArguments(line, args, out args, out _, out error);
    }

    private static bool TryPrepareArguments(string line, string[] tokens,
        out string[] args, out string action, out string error)
    {
        args = Array.Empty<string>();
        action = "invalid";
        error = "Unknown command or invalid arguments. Use sm:help for the supported syntax.";
        // A quoted command name is data, not an alternate namespace spelling.
        if (tokens.Length == 0 || line.TrimStart().StartsWith("\"", StringComparison.Ordinal)) return false;
        string name = tokens[0];
        bool terminalPrefix = name.StartsWith("sm:", StringComparison.OrdinalIgnoreCase);
        if (terminalPrefix) name = name.Substring(3);
        if (CommandsByName.TryGetValue(name, out CommandSpec spec))
        {
            if ((!spec.AcceptsArguments && tokens.Length != 1) ||
                spec.Prefix.Length + tokens.Length - 1 > MaximumArguments) return false;
            args = spec.Prefix.Concat(tokens.Skip(1)).ToArray();
            action = spec.Name;
        }
        else if (!terminalPrefix && (Is(name, "ban") || Is(name, "unban")))
        {
            // Only protected raw RCON moderation enters here. Listing is the
            // separate banlist feature, never the removed public hierarchy.
            if (Is(name, "ban") && tokens.Length == 2 && Is(tokens[1], "list")) return false;
            args = (string[])tokens.Clone();
            action = name.ToLowerInvariant();
        }
        else return false;
        NormalizeVerbs(args);
        return Validate(args, out error);
    }

    // Route by the first verb, not by successful validation: a malformed ban
    // must fail in the exact-account handler, never fall back to vanilla Ban.
    internal static bool IsRconManagedLine(string line)
    {
        string verb = (line ?? "").TrimStart().Split(new[] { ' ', '\t' }, 2)[0].ToLowerInvariant();
        // Other verbs belong to the actual console. In particular, do not
        // replace vanilla event/heal/help syntax with our targeted variants;
        // Discord exposes mod features as their own typed slash commands.
        return verb == "ban" || verb == "unban";
    }

    private static void NormalizeVerbs(string[] a)
    {
        if (a.Length == 0) return;
        a[0] = a[0].ToLowerInvariant();
        if (a.Length > 1 && new[] { "admin", "access", "key", "event", "mods", "character", "item", "skill", "discord" }.Contains(a[0]))
            a[1] = a[1].ToLowerInvariant();
        if (a[0] == "ban" && a.Length == 2 && Is(a[1], "list")) a[1] = "list";
        if (a[0] == "teleport" && a.Length > 2 && Is(a[2], "to")) a[2] = "to";
    }

    private static bool Validate(string[] a, out string error)
    {
        error = "Unknown command or invalid arguments. Use sm:help for the supported syntax.";
        if (a.Length == 0) return false;
        switch (a[0])
        {
            case "help": case "status": case "cronstatus": return a.Length == 1;
            case "cronack": return a.Length == 2 && a[1].Length <= 64 &&
                a[1].All(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' ||
                    c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.');
            case "players": return a.Length == 1 || (a.Length == 2 && ValidTarget(a[1]));
            case "mods": return a.Length == 1 || (a.Length == 2 && (a[1] == "status" || a[1] == "reload"));
            case "discord": return a.Length == 2 && (a[1] == "status" || a[1] == "test");
            case "announce": case "chat": return a.Length >= 2 && ValidMessage(a, 1, 500);
            case "ban": return a.Length >= 2 && ValidTarget(a[1]) && Join(a, 2).Length <= 300;
            case "unban": return a.Length == 2 && IsSteam64(a[1]);
            case "admin": case "access": return (a.Length == 2 && a[1] == "list") ||
                (a.Length == 3 && (a[1] == "add" || a[1] == "remove") && IsSteam64(a[2]));
            case "key": return (a.Length == 2 && a[1] == "list") ||
                (a.Length == 3 && (a[1] == "add" || a[1] == "remove") && ValidToken(a[2]));
            case "event": return (a.Length == 2 && a[1] == "stop") ||
                (a.Length == 6 && a[1] == "start" && ValidToken(a[2]) && Coordinates(a, 3));
            case "character": return (a.Length == 2 && a[1] == "list") ||
                (a.Length == 3 && a[1] == "info" && ValidTarget(a[2])) ||
                ((a.Length == 3 || a.Length == 4) && a[1] == "backups" && ValidTarget(a[2]) &&
                    (a.Length == 3 || Integer(a[3], 1, 1000))) ||
                (a.Length == 4 && a[1] == "restore" && ValidTarget(a[2]) && IsBackupId(a[3]));
            case "item": return (a.Length >= 5 && a.Length <= 7) && a[1] == "give" &&
                ValidTarget(a[2]) && ValidToken(a[3]) && Integer(a[4], 1, 1000) &&
                (a.Length == 5 || Integer(a[5], 1, 100)) &&
                (a.Length < 7 || IsItemDataId(a[6]));
            case "teleport": return (a.Length == 4 && ValidTarget(a[1]) && a[2] == "to" && ValidTarget(a[3])) ||
                (a.Length == 5 && ValidTarget(a[1]) && Coordinates(a, 2));
            case "skill": return (a.Length == 3 && a[1] == "get" && ValidTarget(a[2])) ||
                (a.Length == 4 && a[1] == "get" && ValidTarget(a[2]) && ValidToken(a[3])) ||
                (a.Length == 5 && a[1] == "set" && ValidTarget(a[2]) &&
                    ValidToken(a[3]) && Number(a[4], 0, 100));
            case "heal": case "damage": return a.Length == 3 && ValidTarget(a[1]) && Number(a[2], 0.001f, 100000);
            default: return false;
        }
    }

    internal static bool IsSteam64(string value) => value.Length == 17 && value.All(c => c >= '0' && c <= '9') &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0;
    internal static bool IsBackupId(string value) => value != null && value.Length == 32 &&
        value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    internal static bool IsItemDataId(string value) => ItemDataPresets.IsValidId(value);
    private static bool ValidTarget(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
    private static bool ValidToken(string value) => value.Length > 0 && value.Length <= 128 &&
        value.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.');
    private static bool ValidMessage(string[] a, int start, int limit) =>
        !string.IsNullOrWhiteSpace(Join(a, start)) && Join(a, start).Length <= limit;
    private static bool Integer(string value, int min, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max;
    private static bool Number(string value, float min, float max) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) &&
        !float.IsNaN(n) && !float.IsInfinity(n) && n >= min && n <= max;
    private static bool Coordinates(string[] a, int start) => a.Skip(start).All(v => Number(v, -1000000, 1000000));
    private static float ParseFloat(string value) => float.Parse(value, CultureInfo.InvariantCulture);
    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Join(string[] a, int start) => string.Join(" ", a.Skip(start));
    private static bool NeedsWorld(string[] a) => !(a[0] == "help" || a[0] == "status");

    // Audit only explicit verb/target, never raw text, chat, reasons, or profile bytes.
    private static string Target(string[] a)
    {
        if (new[] { "players", "ban", "unban", "teleport", "heal", "damage", "cronack" }.Contains(a[0]))
            return a.Length > 1 && !(a[0] == "ban" && a[1] == "list") ? a[1] : "";
        if (new[] { "admin", "access", "key", "event", "character", "item", "skill" }.Contains(a[0]))
            return a.Length > 2 ? a[2] : "";
        return "";
    }

    private static string ListText(IEnumerable<string> values)
    {
        StringBuilder text = new();
        foreach (string value in values)
        {
            string next = BoundedText(value, 200);
            if (text.Length + next.Length + 1 > MaximumOutputLength - 30)
            { text.Append("\n[Output truncated]"); break; }
            if (text.Length != 0) text.Append('\n');
            text.Append(next);
        }
        return text.Length == 0 ? "(empty)" : text.ToString();
    }

    internal static string BoundedText(string? value, int limit)
    {
        if (value == null || value.Length == 0) return "";
        StringBuilder text = new(Math.Min(value.Length, limit));
        for (int i = 0; i < value.Length && i < limit; ++i)
            text.Append(char.IsControl(value[i]) && value[i] != '\n' ? ' ' : value[i]);
        return text.ToString();
    }

    internal static ServerManagerCommandResult Success(string code, string message) =>
        new(true, BoundedText(code, 64), BoundedText(message, MaximumOutputLength), "", new Dictionary<string, string>());
    internal static ServerManagerCommandResult Failure(string code, string message) =>
        new(false, BoundedText(code, 64), BoundedText(message, MaximumOutputLength), "", new Dictionary<string, string>());
    private static Task<ServerManagerCommandResult> Done(ServerManagerCommandResult result) => Task.FromResult(result);
    private static Task<ServerManagerCommandResult> Reject(CommandCaller caller, string action, string target, ServerManagerCommandResult result)
    {
        lock (Gate)
        {
            if (Audits.Count < Capacity * 2) Audits.Enqueue(new Audit(caller, action, target, result));
        }
        return Done(result);
    }

    private static void FinishTask(Work work)
    {
        try { Complete(work, work.Pending!.GetAwaiter().GetResult()); }
        catch (OperationCanceledException) { Complete(work, Failure("cancelled", "The command was cancelled.")); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { Complete(work, Failure("command_failed", "The command failed (" + exception.GetType().Name + ").")); }
    }

    private static void Complete(Work work, ServerManagerCommandResult result)
    {
        result = WithSelectedDataId(work.Args, result);
        // Preserve save operation identity/data while bounding adapters' output.
        ServerManagerCommandResult bounded = new(result.Success, BoundedText(result.Code, 64),
            BoundedText(result.Message, MaximumOutputLength), BoundedText(result.OperationId, 64),
            result.Data.Take(16).ToDictionary(p => BoundedText(p.Key, 64), p => BoundedText(p.Value, 256)));
        Record(new Audit(work.Caller, work.Action, work.Target, bounded));
        if (work.Completion.TrySetResult(bounded))
            lock (Gate) --_outstanding;
    }

    private static void DrainAudits()
    {
        for (int i = 0; i < Capacity * 2; ++i)
        {
            Audit audit;
            lock (Gate) { if (Audits.Count == 0) return; audit = Audits.Dequeue(); }
            Record(audit);
        }
    }

    private static void Record(Audit audit)
    {
        try
        {
            ServerEventRuntime.RecordCommand(audit.Caller.Source, audit.Caller.Id, audit.Caller.Name,
                audit.Action, audit.Target, audit.Result.Code, audit.Result.Success, audit.Result.Data);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { ServerManagerPlugin.Log.LogWarning("Command audit delivery failed (" + exception.GetType().Name + ")."); }
    }

    // Retain only a validated selected backup/preset ID, never raw item metadata
    // or command text, even when the admitted operation fails or is cancelled.
    private static ServerManagerCommandResult WithSelectedDataId(string[] args, ServerManagerCommandResult result)
    {
        string key, value;
        if (args.Length >= 4 && args[0] == "character" && args[1] == "restore" && IsBackupId(args[3]))
        { key = "backup_id"; value = args[3]; }
        else if (args.Length == 7 && args[0] == "item" && args[1] == "give" && IsItemDataId(args[6]))
        { key = "data_id"; value = args[6]; }
        else return result;
        Dictionary<string, string> data = result.Data.Take(15).ToDictionary(pair => pair.Key, pair => pair.Value);
        data[key] = value;
        return new ServerManagerCommandResult(result.Success, result.Code, result.Message, result.OperationId, data);
    }

    private sealed class Work
    {
        internal Work(string[] args, CommandCaller caller, string action, string target, long generation, long worldEpoch)
        { Args = args; Caller = caller; Action = action; Target = target; Generation = generation; WorldEpoch = worldEpoch; }
        internal readonly string[] Args;
        internal readonly CommandCaller Caller;
        internal readonly string Action;
        internal readonly string Target;
        internal readonly long Generation;
        internal readonly long WorldEpoch;
        internal readonly TaskCompletionSource<ServerManagerCommandResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<ServerManagerCommandResult>? Pending;
    }

    private sealed class Audit
    {
        internal Audit(CommandCaller caller, string action, string target, ServerManagerCommandResult result)
        { Caller = caller; Action = action; Target = target; Result = result; }
        internal readonly CommandCaller Caller;
        internal readonly string Action;
        internal readonly string Target;
        internal readonly ServerManagerCommandResult Result;
    }

    private sealed class CommandSpec
    {
        internal CommandSpec(string name, string prefix, string arguments = "", bool playerFirst = false)
        {
            Name = name;
            Prefix = prefix.Split(' ');
            Syntax = "sm:" + name + (arguments.Length == 0 ? "" : " " + arguments);
            PlayerFirst = playerFirst;
            AcceptsArguments = arguments.Length != 0;
        }
        internal readonly string Name;
        internal readonly string[] Prefix;
        internal readonly string Syntax;
        internal readonly bool PlayerFirst;
        internal readonly bool AcceptsArguments;
    }

    private static readonly string Help = string.Join("\n", Catalog.Select(spec => spec.Syntax)) +
        "\nUse vanilla save/kick/ban/unban (Discord: /rcon); sm:status shows checkpoint completion.\n" +
        "Use an exact name or account ID; quote names/messages containing spaces.";
}
