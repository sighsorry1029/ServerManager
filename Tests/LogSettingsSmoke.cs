// Source-linked parser/reloader. All writes stay in an owned disposable temp root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerManager;

internal static class LogSettingsSmoke
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly FieldInfo NextPoll = typeof(LogSettingsRuntime)
        .GetField("_nextPoll", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PendingRead = typeof(LogSettingsRuntime)
        .GetField("_read", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly string[] PlayerSelectors =
    {
        "player.connection", "player.position", "player.death", "combat.damage",
        "inventory.changed", "inventory.snapshot", "skill.changed", "character.save_rejected"
    };
    private static readonly string[] ChatSelectors =
        { "chat.shout", "chat.normal", "chat.whisper", "chat.clan", "discord.shout" };
    private static int _checks, _mainThread;
    private static string _root = string.Empty;

    private static int Main(string[] args)
    {
        try
        {
            _root = Path.GetFullPath(args.Single());
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!_root.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !_root.Contains("ServerManager-LogSettings-"))
                throw new InvalidOperationException("The harness requires an owned temporary root.");
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            Parser(); Classifiers(); Reload(); StartupAndLifecycle();
            Console.WriteLine("PASS: log.yml parser/reload (" + _checks +
                " assertions; source-linked production code, no game/network/user-data access).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { LogSettingsRuntime.Stop(); }
    }

    private static void Parser()
    {
        foreach (LogSettings settings in new[] { LogSettings.Defaults, LogSettings.Parse("{}"),
            LogSettings.Parse(LogSettings.DefaultYaml) })
        {
            Check(PlayerSelectors.All(settings.AllowsPlayer) && ChatSelectors.All(settings.AllowsChat),
                "Generated and omitted settings enable every approved player/chat selector");
            Check(settings.AllowsAudit("server.started", "", "", false) &&
                settings.AllowsAudit("command.executed", "console", "save", false) &&
                settings.AllowsAudit("command.executed", "cron", "schedule", true) &&
                !settings.AllowsAudit("command.executed", "cron", "schedule", false),
                "Default audit retains startup/manual commands and failed cron finals, not successful cron finals");
        }
        LogSettings disabled = LogSettings.Parse("audit: []\nplayer: []\nchat: []");
        Check(!PlayerSelectors.Any(disabled.AllowsPlayer) && !ChatSelectors.Any(disabled.AllowsChat) &&
            !disabled.AllowsAudit("server.ready", "", "", false) &&
            !disabled.AllowsAudit("command.executed", "cron", "maintenance", true),
            "Explicit empty sequences disable only file selections, including failed commands");
        LogSettings narrow = LogSettings.Parse("player: [player.position]\nchat: ['chat.normal']");
        Check(narrow.AllowsPlayer("player.position") && !narrow.AllowsPlayer("player.connection") &&
            narrow.AllowsChat("chat.normal") && !narrow.AllowsChat("chat.shout") &&
            narrow.AllowsAudit("server.ready", "", "", false),
            "Each explicit section replaces defaults while an omitted section uses defaults");
        Check(!LogSettings.Defaults.AllowsPlayer("unknown") && !LogSettings.Defaults.AllowsChat("unknown") &&
            !LogSettings.Defaults.AllowsPlayer(null!) && !LogSettings.Defaults.AllowsChat(null!),
            "Unknown and null event names are not implicitly logged");
        string[] invalid =
        {
            "", "# comments only", "[]", "null", "~", "true", "audit:", "audit: null", "audit: ~",
            "audit: {}", "audit: {events: []}", "player: {}", "chat: {}", "chat: chat.shout",
            "events: []", "Audit: []", "audit: []\naudit: []", "player: []\nplayer: []",
            "chat: []\nunknown: []", "audit: [server.ready]", "audit: [unknown]",
            "audit: [player.login]", "player: [player.login]", "chat: [player.connection]",
            "chat: [chat.shout, chat.shout]", "player: [player.position, player.position]",
            "audit: [cron.failed, cron.failed]", "chat: ['chat.*']", "chat: ['*']", "audit: ['*']",
            "chat: [null]", "chat: [~]", "chat: [true]", "chat: [12]", "chat: ['']",
            "chat: [' chat.shout']", "chat: ['chat.shout ']", "chat: [Chat.Shout]",
            "chat: [[]]", "chat: [{}]", "chat: [{chat.shout: true}]", "chat: [\"chat.shout\\n\"]",
            "chat: &events [chat.shout]", "chat: *events", "chat: [!!str chat.shout]",
            "!!map {}", "{}\n---\n{}", "audit: " + new string('[', 5) + new string(']', 5),
            "#" + new string('x', LogSettings.MaximumFileBytes), "#" + new string('\uAC00', 45000), "#\uD800"
        };
        foreach (string yaml in invalid) Reject(yaml);
        Reject(null!);
    }

    private static void Classifiers()
    {
        Group("server.status", "server.ready", "server.shutdown");
        Group("player.connection", "player.first_join", "player.login", "player.leave");
        Group("player.death", "player.death", "combat.pvp_kill");
        Group("raid.status", "raid.started", "raid.ended");
        Group("security.alert", "security.detection", "security.response");
        Group("character.validation", "character.save_rejected", "character.validation_observed");
        foreach (string kind in new[]
        {
            "server.started", "server.saved", "server.announcement", "boss.killed", "moderation.action",
            "security.admin_bypass", "character.shadow_stalled", "character.revision_observed", "connection.rejected"
        }) Group(kind, kind);
        foreach (string synthetic in new[]
        {
            "server.status", "player.connection", "raid.status", "security.alert", "character.validation",
            "cron.executed", "cron.failed", "unknown", "chat.shout", "player.position", ""
        }) Check(!LogSettings.Defaults.AllowsAudit(synthetic, "", "", true),
            "Only actual supported audit kinds match: " + synthetic);
        Check(!LogSettings.Defaults.AllowsAudit(null!, "", "", false), "Null audit kind is not logged");

        LogSettings commands = LogSettings.Parse("audit: [command.executed]");
        LogSettings finals = LogSettings.Parse("audit: [cron.executed]");
        LogSettings failures = LogSettings.Parse("audit: [cron.failed]");
        LogSettings both = LogSettings.Parse("audit: [command.executed, cron.executed, cron.failed]");
        foreach (string source in new[] { "console", "ingame", "discord", "" })
            foreach (bool failed in new[] { false, true })
                Check(commands.AllowsAudit("command.executed", source, "schedule", failed) &&
                    !finals.AllowsAudit("command.executed", source, "schedule", failed) &&
                    !failures.AllowsAudit("command.executed", source, "schedule", failed),
                    "Non-cron commands are controlled only by command.executed");
        foreach (string command in new[] { "schedule", "maintenance" })
            foreach (bool failed in new[] { false, true })
                Check(!commands.AllowsAudit("command.executed", "cron", command, failed) &&
                    finals.AllowsAudit("command.executed", "cron", command, failed) &&
                    failures.AllowsAudit("command.executed", "cron", command, failed) == failed &&
                    both.AllowsAudit("command.executed", "cron", command, failed),
                    "Cron selectors apply only to their final summary and preserve failure semantics");
        foreach (string command in new[] { "broadcast", "save", "kick", "restart", "", "schedule_extra" })
            foreach (bool failed in new[] { false, true })
                Check(!both.AllowsAudit("command.executed", "cron", command, failed),
                    "Even failed cron per-command records are excluded from final-summary selectors");
    }

    private static void Group(string selector, params string[] kinds)
    {
        LogSettings settings = LogSettings.Parse("audit: [" + selector + "]");
        Check(kinds.All(kind => settings.AllowsAudit(kind, "", "", false)) &&
            !settings.AllowsAudit("command.executed", "console", "save", false),
            "Audit selection maps the approved underlying events: " + selector);
    }

    private static void Reload()
    {
        string root = NewRoot("reload"), path = Path.Combine(root, LogSettings.FileName);
        List<string> info = new(), warnings = new();
        void LogInfo(string message) { OnMainThread(); info.Add(message); }
        void Warn(string message) { OnMainThread(); warnings.Add(message); }
        LogSettingsRuntime.Start(root, LogInfo, Warn);
        Check(File.ReadAllText(path, Utf8) == LogSettings.DefaultYaml && info.Count == 1 && warnings.Count == 0,
            "Missing file is created once and synchronously loaded before startup records");
        LogSettings initial = LogSettingsRuntime.Current;
        for (int index = 0; index < 100; ++index) LogSettingsRuntime.Tick();
        Check(info.Count == 1 && PendingRead.GetValue(null) == null, "Ordinary frames perform no file read before poll interval");
        string edit = "# keep operator comments\naudit: []\nplayer: []\nchat: [chat.shout]\n";
        File.WriteAllText(path, edit, Utf8);
        Poll();
        Check(ReferenceEquals(initial, LogSettingsRuntime.Current), "One changed sample does not publish");
        Poll();
        Check(info.Count == 2 && !LogSettingsRuntime.Current.AllowsPlayer("player.position") &&
            !LogSettingsRuntime.Current.AllowsAudit("server.ready", "", "", false) &&
            LogSettingsRuntime.Current.AllowsChat("chat.shout") && !LogSettingsRuntime.Current.AllowsChat("chat.normal") &&
            initial.AllowsPlayer("player.position"), "Two matching samples atomically publish an immutable whole snapshot");
        Check(File.ReadAllText(path, Utf8) == edit, "Reload preserves the exact operator text and comments");

        DateTime stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, edit.Replace("chat.shout", "chat.clan "), Utf8);
        File.SetLastWriteTimeUtc(path, stamp);
        Poll(); Poll();
        Check(LogSettingsRuntime.Current.AllowsChat("chat.clan") && !LogSettingsRuntime.Current.AllowsChat("chat.shout"),
            "Same-length edits with unchanged timestamp are detected by content");
        foreach (string bad in new[] { "chat: [UNTRUSTED_SECRET]", "audit: {}", "player: null", "chat: [chat.shout, chat.shout]" })
        {
            LogSettings previous = LogSettingsRuntime.Current;
            File.WriteAllText(path, bad, Utf8);
            Poll(); Poll();
            int warned = warnings.Count;
            Poll(); Poll();
            Check(ReferenceEquals(previous, LogSettingsRuntime.Current) && warnings.Count == warned &&
                File.ReadAllText(path, Utf8) == bad, "Invalid stable edits retain the whole snapshot, warn once, and remain untouched");
        }
        Check(!string.Join(" ", warnings).Contains("UNTRUSTED_SECRET"), "Warnings do not echo YAML input values");
        LogSettings beforeDeletion = LogSettingsRuntime.Current;
        File.Delete(path);
        Poll(); int missingWarnings = warnings.Count; Poll();
        Check(ReferenceEquals(beforeDeletion, LogSettingsRuntime.Current) && !File.Exists(path) && warnings.Count == missingWarnings,
            "A deleted active file is not recreated and keeps last-good selection without repeated warnings");
        File.WriteAllBytes(path, new byte[] { 0xff, 0xfe, 0x61, 0 }); Poll();
        Check(ReferenceEquals(beforeDeletion, LogSettingsRuntime.Current), "Invalid UTF-8 does not change current selection");
        File.WriteAllBytes(path, new byte[LogSettings.MaximumFileBytes + 1]); Poll();
        Check(ReferenceEquals(beforeDeletion, LogSettingsRuntime.Current), "Oversize reads stop at the bound and retain current selection");
        File.WriteAllText(path, "{}", Utf8); Poll(); Poll();
        Check(PlayerSelectors.All(LogSettingsRuntime.Current.AllowsPlayer) && ChatSelectors.All(LogSettingsRuntime.Current.AllowsChat) &&
            LogSettingsRuntime.Current.AllowsAudit("command.executed", "cron", "schedule", true) &&
            !LogSettingsRuntime.Current.AllowsAudit("command.executed", "cron", "schedule", false),
            "Restored valid empty mapping resets every section to defaults");
        LogSettingsRuntime.Stop();
    }

    private static void StartupAndLifecycle()
    {
        string root = NewRoot("startup"), path = Path.Combine(root, LogSettings.FileName);
        File.WriteAllText(path, "chat: null", Utf8);
        List<string> warnings = new();
        LogSettingsRuntime.Start(root, _ => { }, warnings.Add);
        Check(ReferenceEquals(LogSettings.Defaults, LogSettingsRuntime.Current) && warnings.Count == 1 &&
            File.ReadAllText(path, Utf8) == "chat: null", "Invalid startup falls back to defaults without overwriting the file or gating admission");
        File.WriteAllText(path, "chat: []", Utf8); Poll(); Poll();
        Check(!ChatSelectors.Any(LogSettingsRuntime.Current.AllowsChat), "Corrected invalid startup reloads normally");
        File.WriteAllText(path, "chat: [chat.shout]", Utf8);
        Poll(); // The retired second read is eligible to parse and publish.
        NextPoll.SetValue(null, 0L); LogSettingsRuntime.Tick();
        Task? stale = PendingRead.GetValue(null) as Task;
        LogSettingsRuntime.Stop();
        Check(ReferenceEquals(LogSettings.Defaults, LogSettingsRuntime.Current), "Stop resets the shared snapshot to defaults");
        string replacement = NewRoot("replacement");
        File.WriteAllText(Path.Combine(replacement, LogSettings.FileName), "player: []\nchat: []", Utf8);
        LogSettingsRuntime.Start(replacement, _ => { }, _ => { });
        LogSettings replacementSettings = LogSettingsRuntime.Current;
        Check(stale != null && stale.Wait(5000), "Retired worker completes using only captured state");
        Poll();
        Check(ReferenceEquals(replacementSettings, LogSettingsRuntime.Current) &&
            !LogSettingsRuntime.Current.AllowsChat("chat.shout"), "A retired worker never publishes into the replacement session");
        LogSettingsRuntime.Stop();
        NextPoll.SetValue(null, 0L); LogSettingsRuntime.Tick();
        Check(PendingRead.GetValue(null) == null, "Stopped runtime schedules no worker");

        string readOnly = NewRoot("readonly"), readOnlyPath = Path.Combine(readOnly, LogSettings.FileName);
        File.WriteAllText(readOnlyPath, "# retain\nchat: []", Utf8);
        File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
        try
        {
            LogSettingsRuntime.Start(readOnly, _ => { }, _ => { });
            Check(!LogSettingsRuntime.Current.AllowsChat("chat.shout") && File.ReadAllText(readOnlyPath, Utf8) == "# retain\nchat: []",
                "Existing read-only settings load without rewriting comments");
        }
        finally { LogSettingsRuntime.Stop(); File.SetAttributes(readOnlyPath, FileAttributes.Normal); }
    }

    private static void Poll()
    {
        NextPoll.SetValue(null, 0L); LogSettingsRuntime.Tick();
        Task? task = PendingRead.GetValue(null) as Task;
        Check(task != null && task.Wait(5000), "Bounded background sample completed");
        NextPoll.SetValue(null, long.MaxValue); LogSettingsRuntime.Tick();
    }

    private static void OnMainThread() => Check(Thread.CurrentThread.ManagedThreadId == _mainThread,
        "Reload diagnostics run only on the caller thread");
    private static string NewRoot(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }
    private static void Reject(string yaml)
    {
        bool rejected = false;
        try { LogSettings.Parse(yaml); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid YAML/type/structure/selector is rejected");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        ++_checks;
    }
}
