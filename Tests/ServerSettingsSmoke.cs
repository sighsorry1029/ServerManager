// Source-linked production parser/reloader; all file writes stay in a disposable temp root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerManager;

internal static class ServerSettingsSmoke
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly FieldInfo NextPoll = typeof(ServerSettingsReloadService)
        .GetField("_nextPoll", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PendingRead = typeof(ServerSettingsReloadService)
        .GetField("_read", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static int _checks, _mainThread;
    private static string _root = string.Empty;

    private static int Main(string[] args)
    {
        try
        {
            _root = Path.GetFullPath(args.Single());
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!_root.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !_root.Contains("ServerManager-ServerSettings-"))
                throw new InvalidOperationException("The harness requires an owned temporary root.");
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            Parser(); Reload(); StartupAndLifecycle();
            Console.WriteLine("PASS: ServerManager.yml parser/reload (" + _checks +
                " assertions; source-linked production code, no game/network/user-data access).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Parser()
    {
        ServerSettings defaults = ServerSettings.Parse(ServerSettings.DefaultYaml);
        Check(defaults.MaxPlayers == 24 && defaults.MaxCharactersPerAccount == 3 && defaults.BackupsPerProfile == 30 && defaults.LoadServerCharacterOnJoin &&
            defaults.CheatDetectionResponse == DetectionAction.Kick && defaults.StatLimitResponse == DetectionAction.Log &&
            defaults.MaximumHealth == 800f && defaults.MaximumStamina == 800f && defaults.MaximumEitr == 500f &&
            defaults.MaximumCarryWeight == 2000f && defaults.MaximumDamage == 50000f &&
            defaults.ForbiddenItemPrefabs == "" && HasDefaultStartItems(defaults), "Approved defaults/example agree");
        Check(ServerSettings.Defaults.MaximumDamage == 50000f &&
            ServerSettings.Parse("{}").MaximumDamage == 50000f &&
            ServerSettings.Parse("statCaps: {}").MaximumDamage == 50000f,
            "Omitted damage uses the approved 50000 default");
        Check(ServerSettings.Parse("statCaps: {damage: 9999}").MaximumDamage == 9999f,
            "An existing explicit damage limit is not replaced by the new default");
        ServerSettings omitted = ServerSettings.Parse("{}");
        Check(HasDefaultStartItems(ServerSettings.Defaults) && HasDefaultStartItems(omitted) &&
            HasDefaultStartItems(ServerSettings.Parse("serverSettings: {}")) &&
            !ReferenceEquals(omitted.StartItems, ServerSettings.Defaults.StartItems),
            "Omitted startItems receives a separate immutable copy of the four one-item defaults");
        Check(ServerSettings.Defaults.MaxCharactersPerAccount == 3 &&
            ServerSettings.Parse("{}").MaxCharactersPerAccount == 3 &&
            ServerSettings.Parse("serverSettings: {}").MaxCharactersPerAccount == 3,
            "The renamed character quota defaults to three in every omitted/default path");
        Check(ServerSettings.Defaults.MaxPlayers == 24 &&
            ServerSettings.Parse("{}").MaxPlayers == 24 &&
            ServerSettings.Parse("serverSettings: {}").MaxPlayers == 24,
            "Concurrent player capacity defaults to 24 in every omitted/default path");
        Check(ServerSettings.Parse("serverSettings: {maxPlayers: 1}").MaxPlayers == 1 &&
            ServerSettings.Parse("serverSettings: {maxPlayers: 64}").MaxPlayers == 64 &&
            ServerSettings.Parse("serverSettings: {maxPlayers: 10}").MaxPlayers == 10,
            "Capacity accepts both inclusive bounds and preserves an explicit vanilla-sized limit");
        Check(ServerSettings.Defaults.LoadServerCharacterOnJoin && ServerSettings.Parse("{}").LoadServerCharacterOnJoin &&
            ServerSettings.Parse("serverSettings: {}").LoadServerCharacterOnJoin &&
            ServerSettings.Parse("serverSettings: {loadServerCharacterOnJoin: true}").LoadServerCharacterOnJoin,
            "Server character loading defaults on and accepts an explicit true boolean");
        Check(!ServerSettings.Parse("serverSettings: {loadServerCharacterOnJoin: false}").LoadServerCharacterOnJoin,
            "An explicit false selects local-character capture on the next connection");
        ServerSettings settings = ServerSettings.Parse(@"serverSettings: {maxPlayers: 64, maxCharactersPerAccount: 128, backupsPerProfile: 50, loadServerCharacterOnJoin: false}
forbiddenItems: [Wood, Stone]
cheatDetection: {action: 'ban'}
statCaps: {action: kick, health: 1000000, stamina: 1.5, eitr: 5e2, weight: 1, damage: 1000000000}
startItems:
  - Wood, 20
  - ' Stone , 10 '
  - Resin");
        Check(settings.MaxPlayers == 64 && settings.MaxCharactersPerAccount == 128 && settings.BackupsPerProfile == 50 && !settings.LoadServerCharacterOnJoin &&
            settings.CheatDetectionResponse == DetectionAction.Ban && settings.StatLimitResponse == DetectionAction.Kick &&
            settings.MaximumHealth == 1000000 && settings.MaximumStamina == 1.5f && settings.MaximumEitr == 500 &&
            settings.MaximumCarryWeight == 1 && settings.MaximumDamage == 1000000000 &&
            settings.ForbiddenItemPrefabs == "Stone\nWood" && settings.StartItems["Wood"] == 20 &&
            settings.StartItems["Stone"] == 10 && settings.StartItems["Resin"] == 1 && settings.StartItems.Count == 3,
            "Every schema field parses and explicit starter lists replace rather than merge the defaults");
        Check(ServerSettings.Parse("startItems: []").StartItems.Count == 0 &&
            HasDefaultStartItems(ServerSettings.Defaults) && HasDefaultStartItems(ServerSettings.Parse("{}")),
            "An explicit empty list configures no starting items without changing later omitted-key defaults");
        Check(ServerSettings.Parse("startItems: [Torch]").StartItems.Count == 1 &&
            ServerSettings.Parse("startItems: [Torch]").StartItems["Torch"] == 1,
            "A single explicit default prefab does not implicitly grant the other defaults");
        Check(ServerSettings.Parse("startItems: ['Wood, 1000000']").StartItems["Wood"] == 1000000,
            "Starter amount upper bound matches the detached inventory builder");
        Check(ServerSettings.Parse("startItems: ['\tWood \t, \t2\t']").StartItems["Wood"] == 2,
            "Whitespace around the prefab, comma and amount is allowed");
        Check(ServerSettings.Parse("startItems: [Wood, wood]").StartItems.Count == 2,
            "Prefab duplicate matching is exact and case-sensitive");
        Check(ServerSettings.Parse("startItems: [" + string.Join(",",
            Enumerable.Range(0, 256).Select(i => "Prefab" + i)) + "]").StartItems.Count == 256,
            "The 256-entry sequence boundary remains valid");
        bool immutable = false;
        try { ((IDictionary<string, int>)settings.StartItems).Add("Other", 1); }
        catch (NotSupportedException) { immutable = true; }
        Check(immutable && !settings.StartItems.ContainsKey("Other"), "Start-items mapping is externally immutable");
        foreach (string action in new[] { "log", "kick", "ban" })
            Check(ServerSettings.Parse("cheatDetection: {action: " + action + "}\nstatCaps: {action: " + action + "}")
                .CheatDetectionResponse == ServerSettings.Parse("statCaps: {action: " + action + "}").StatLimitResponse,
                "Common lower-case action: " + action);

        string[] invalid =
        {
            "", "# comments only", "[]", "null", "serverSettings: []", "serverSettings: null",
            "serverSettings: {maxPlayers: 0}", "serverSettings: {maxPlayers: -1}",
            "serverSettings: {maxPlayers: 65}", "serverSettings: {maxPlayers: 2147483648}",
            "serverSettings: {maxPlayers: '24'}", "serverSettings: {maxPlayers: 24.0}",
            "serverSettings: {maxPlayers: 2.4e1}", "serverSettings: {maxPlayers: true}",
            "serverSettings: {maxPlayers: null}", "serverSettings: {maxPlayers: ~}",
            "serverSettings: {maxPlayers: []}", "serverSettings: {maxPlayers: {}}",
            "serverSettings: {maxPlayers: +24}", "serverSettings: {maxPlayers: 1, maxPlayers: 64}",
            "serverSettings: {maxCharactersPerAccount: 0}", "serverSettings: {maxCharactersPerAccount: 129}",
            "serverSettings: {maxCharactersPerAccount: '5'}", "serverSettings: {maxCharactersPerAccount: 5.0}",
            "serverSettings: {maxCharactersPerAccount: true}", "serverSettings: {backupsPerProfile: -1}",
            "serverSettings: {maxProfilesPerAccount: 3}",
            "serverSettings: {maxProfilesPerAccount: 5, maxCharactersPerAccount: 3}",
            "serverSettings: {maxCharactersPerAccount: 3, maxProfilesPerAccount: 5}",
            "serverSettings: {backupsPerProfile: 51}", "serverSettings: {backupsPerProfile: 2147483648}",
            "serverSettings: {loadServerCharacterOnJoin: 'true'}", "serverSettings: {loadServerCharacterOnJoin: \"false\"}",
            "serverSettings: {loadServerCharacterOnJoin: True}", "serverSettings: {loadServerCharacterOnJoin: FALSE}",
            "serverSettings: {loadServerCharacterOnJoin: yes}", "serverSettings: {loadServerCharacterOnJoin: no}",
            "serverSettings: {loadServerCharacterOnJoin: on}", "serverSettings: {loadServerCharacterOnJoin: off}",
            "serverSettings: {loadServerCharacterOnJoin: 0}", "serverSettings: {loadServerCharacterOnJoin: 1}",
            "serverSettings: {loadServerCharacterOnJoin: null}", "serverSettings: {loadServerCharacterOnJoin: ~}",
            "serverSettings: {loadServerCharacterOnJoin: }", "serverSettings: {loadServerCharacterOnJoin: []}",
            "serverSettings: {loadServerCharacterOnJoin: {}}", "serverSettings:\n  loadServerCharacterOnJoin: |\n    true\n",
            "serverSettings: {loadServerCharacterOnJoin: !!bool true}",
            "serverSettings: {loadServerCharacterOnJoin: true, loadServerCharacterOnJoin: false}",
            "serverSettings: {backupOnly: true}", "serverSettings: {backupOnly: false}",
            "serverSettings: {loadServerCharacterOnJoin: true, backupOnly: false}",
            "serverSettings: {loadServerCharacterOnJoin: false, backupOnly: true}",
            "serverSettings: {other: 1}", "serverSettings: {backupsPerProfile: 1, backupsPerProfile: 2}",
            "serverSettings: {}\nserverSettings: {}", "forbiddenItems: Wood", "forbiddenItems: [Wood, Wood]",
            "forbiddenItems: [null]", "forbiddenItems: [true]", "forbiddenItems: [12]", "forbiddenItems: [~]",
            "forbiddenItems: ['']", "forbiddenItems: [' Wood']", "forbiddenItems: ['Wood;Stone']",
            "forbiddenItems: [\"Wood\\nStone\"]", "forbiddenItems: [{}]", "cheatDetection: {action: off}",
            "cheatDetection: {action: Kick}", "cheatDetection: {action: 2}", "cheatDetection: {action: true}",
            "cheatDetection: {action: null}", "cheatDetection: {action: ' kick '}", "statCaps: {action: []}",
            "statCaps: {health: 0}", "statCaps: {health: .nan}", "statCaps: {health: NaN}",
            "statCaps: {health: .inf}", "statCaps: {health: Infinity}", "statCaps: {health: 1e999}",
            "statCaps: {health: '800'}", "statCaps: {health: 1000000.01}", "statCaps: {damage: 1000000001}",
            "statCaps: {weight: -1}", "statCaps: {stamina: false}", "statCaps: {eitr: null}",
            "statCaps: {skill: 1}", "startItems: {}", "startItems: {Wood: 0}", "startItems: {Wood: -1}",
            "startItems: {Wood: 1.5}", "startItems: {Wood: '2'}", "startItems: {Wood: true}",
            "startItems: {Wood: 2147483648}", "startItems: {Wood: 1, Wood: 2}", "startItems: {Wood: null}",
            "startItems: {null: 1}", "startItems: {[]: 1}", "startItems: {Wood: 1}", "startItems: Wood",
            "startItems: null", "startItems: [null]", "startItems: [~]", "startItems: [true]", "startItems: [5]",
            "startItems: [false]", "startItems: [3.5]", "startItems: [[]]", "startItems: [{}]",
            "startItems: [{Wood: 1}]", "startItems: ['']", "startItems: ['   ']",
            "startItems: ['Wood,']", "startItems: ['Wood,   ']", "startItems: [', 5']",
            "startItems: ['Wood, 1, 2']", "startItems: ['Wood,, 2']", "startItems: ['Wood, 0']",
            "startItems: ['Wood, -1']", "startItems: ['Wood, 1.5']", "startItems: ['Wood, 5e2']",
            "startItems: ['Wood, true']", "startItems: ['Wood, null']", "startItems: ['Wood, +1']",
            "startItems: ['Wood, 1000001']", "startItems: ['Wood, 2147483648']", "startItems: ['Wood, NaN']",
            "startItems: ['Wood, .inf']", "startItems: [Wood, Wood]", "startItems: [' Wood ', 'Wood, 2']",
            "startItems: ['Wood, 1', ' Wood , 2']", "startItems: ['Wood;Stone']",
            "startItems: [\"Wood\\nStone\"]", "startItems: [\"Wood,\\n5\"]",
            "startItems: ['" + new string('a', 129) + "']", "spawn: {}", "skills: {}", "templateExtras: {}",
            "cheatDetection: &cheat {action: log}", "cheatDetection: {action: !!str log}",
            "cheatDetection: *cheat", "{}\n---\n{}", "root: " + new string('[', 9) + new string(']', 9),
            "forbiddenItems: [" + string.Join(",", Enumerable.Range(0, 257).Select(i => "Prefab" + i)) + "]",
            "startItems: [" + string.Join(",", Enumerable.Range(0, 257).Select(i => "Prefab" + i)) + "]",
            "#" + new string('a', ServerSettings.MaximumFileBytes),
            "#" + new string('\uAC00', 45000), "#\uD800", "forbiddenItems: ['" + new string('a', 129) + "']"
        };
        foreach (string yaml in invalid) Reject(yaml);
        Reject(null!);
    }

    private static void Reload()
    {
        string root = NewRoot("reload");
        List<string> logs = new();
        int applies = 0;
        using ServerSettingsReloadService service = new(logs.Add, logs.Add);
        bool Apply(ServerSettings settings)
        {
            Check(Thread.CurrentThread.ManagedThreadId == _mainThread, "Apply always runs on caller thread");
            if (settings.StartItems.ContainsKey("UnknownPrefab")) throw new InvalidDataException("UNTRUSTED_SECRET");
            ++applies;
            return true;
        }
        Check(service.EnsureLoaded(root, Apply) && applies == 1 && service.Current!.MaxPlayers == 24 &&
            service.Current.MaxCharactersPerAccount == 3,
            "Missing startup file is created and synchronously applied before readiness");
        ServerSettings initial = service.Current!;
        string path = Path.Combine(root, ServerSettings.FileName);
        string generated = File.ReadAllText(path, Utf8);
        Check(generated == ServerSettings.DefaultYaml && HasDefaultStartItems(ServerSettings.Parse(generated)) &&
            ServerSettings.Parse(generated).MaximumDamage == 50000f &&
            generated.Contains("  - HelmetMidsummerCrown") && generated.Contains("  - ArmorRagsChest") &&
            generated.Contains("  - ArmorRagsLegs") && generated.Contains("  - Torch"),
            "Generated YAML configures exactly the approved four one-item starters and damage default");
        Check(generated.Contains("loadServerCharacterOnJoin: true") && !generated.Contains("backupOnly:") &&
            generated.Contains("active sessions keep the mode"),
            "Generated configuration documents server-loading default and connection-scoped mode changes without a legacy key");
        Check(generated.Contains("maxCharactersPerAccount: 3") && !generated.Contains("maxProfilesPerAccount"),
            "Generated YAML contains only the new three-character quota key; no legacy alias is advertised");
        Check(generated.Contains("maxPlayers: 24") && generated.Contains("including the listen host") &&
            generated.Contains("Lowering does not kick existing players"),
            "Generated YAML documents the inclusive concurrent-player limit and non-kicking decrease policy");
        for (int index = 0; index < 100; ++index) service.Tick(root, Apply);
        Check(applies == 1 && PendingRead.GetValue(service) == null, "Ordinary frames schedule no disk reads before poll interval");

        string edit = "# keep my comments\nserverSettings: {maxPlayers: 32, maxCharactersPerAccount: 7, loadServerCharacterOnJoin: false}\nstartItems:\n  - Wood, 2\n";
        File.WriteAllText(path, edit, Utf8);
        Poll(service, root, Apply);
        Check(applies == 1, "A single changed sample does not apply");
        Poll(service, root, Apply);
        Check(applies == 2 && service.Current!.MaxPlayers == 32 && service.Current.MaxCharactersPerAccount == 7 && !service.Current.LoadServerCharacterOnJoin &&
            service.Current.StartItems.Count == 1 && service.Current.StartItems["Wood"] == 2 &&
            initial.MaxPlayers == 24 && initial.LoadServerCharacterOnJoin && HasDefaultStartItems(initial),
            "Two identical samples apply the complete candidate");
        Check(File.ReadAllText(path, Utf8) == edit, "Reload never rewrites existing contents/comments");
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, edit.Replace(": 7", ": 6"), Utf8);
        File.SetLastWriteTimeUtc(path, stamp);
        Poll(service, root, Apply); Poll(service, root, Apply);
        Check(service.Current!.MaxCharactersPerAccount == 6, "Same-length edits with unchanged timestamps are detected by content");

        foreach (string bad in new[] { "statCaps: {health: .nan}", "startItems:\n  - UnknownPrefab", "startItems: {Wood: 1}",
            "serverSettings: {maxPlayers: 65, maxCharactersPerAccount: 9, loadServerCharacterOnJoin: true}",
            "serverSettings: {maxPlayers: 0, maxCharactersPerAccount: 9}",
            "serverSettings: {maxPlayers: 12, maxCharactersPerAccount: 0}",
            "serverSettings: {maxPlayers: 12}\nstartItems:\n  - UnknownPrefab",
            "serverSettings: {maxCharactersPerAccount: 9, loadServerCharacterOnJoin: 'true'}",
            "serverSettings: {maxProfilesPerAccount: 1, loadServerCharacterOnJoin: true}",
            "serverSettings: {maxCharactersPerAccount: 1, maxProfilesPerAccount: 9, loadServerCharacterOnJoin: true}",
            "serverSettings: {backupOnly: true}" })
        {
            ServerSettings previous = service.Current!;
            File.WriteAllText(path, bad, Utf8);
            Poll(service, root, Apply); Poll(service, root, Apply);
            int warnings = logs.Count;
            Poll(service, root, Apply); Poll(service, root, Apply);
            Check(ReferenceEquals(previous, service.Current) && service.Current!.MaxPlayers == 32 && service.Current.MaxCharactersPerAccount == 6 &&
                !service.Current.LoadServerCharacterOnJoin && logs.Count == warnings,
                "Invalid syntax or apply rejection preserves whole last-good snapshot and warns once");
            Check(File.ReadAllText(path, Utf8) == bad, "Rejected file remains exactly as written by operator");
        }
        ServerSettings largerCapacity = service.Current!;
        File.WriteAllText(path, edit.Replace("maxPlayers: 32", "maxPlayers: 12").Replace(": 7", ": 6"), Utf8);
        Poll(service, root, Apply); Poll(service, root, Apply);
        Check(service.Current!.MaxPlayers == 12 && service.Current.MaxCharactersPerAccount == 6 &&
            largerCapacity.MaxPlayers == 32 && !ReferenceEquals(largerCapacity, service.Current),
            "A valid capacity decrease publishes a new immutable snapshot without changing the prior snapshot");
        File.Delete(path);
        Poll(service, root, Apply);
        int missingWarnings = logs.Count;
        Poll(service, root, Apply);
        Check(service.Current!.MaxCharactersPerAccount == 6 && !File.Exists(path) && logs.Count == missingWarnings,
            "A deleted reload file is never recreated and does not log every poll");
        File.WriteAllBytes(path, new byte[] { 0xff, 0xfe, 0x61, 0 });
        Poll(service, root, Apply);
        Check(service.Current!.MaxCharactersPerAccount == 6, "Non-UTF8 file leaves current policy untouched");
        File.WriteAllText(path, "{}", Utf8);
        Poll(service, root, Apply); Poll(service, root, Apply);
        Check(service.Current!.MaxPlayers == 24 && service.Current.MaxCharactersPerAccount == 3 && service.Current.LoadServerCharacterOnJoin &&
            HasDefaultStartItems(service.Current) && service.Current.MaximumDamage == 50000f,
            "Valid restoration recovers and omitted fields reset to the new defaults");
        File.WriteAllText(path, "serverSettings: {loadServerCharacterOnJoin: false}", Utf8);
        Poll(service, root, Apply); Poll(service, root, Apply);
        ServerSettings backupSessionSettings = service.Current!;
        File.WriteAllText(path, "serverSettings: {loadServerCharacterOnJoin: true}", Utf8);
        Poll(service, root, Apply); Poll(service, root, Apply);
        Check(service.Current!.LoadServerCharacterOnJoin && !backupSessionSettings.LoadServerCharacterOnJoin,
            "Explicitly enabling server loading reloads while previously captured snapshots remain unchanged");
        Check(!string.Join(" ", logs).Contains("UNTRUSTED_SECRET"), "Application exception text is never logged");
    }

    private static void StartupAndLifecycle()
    {
        string root = NewRoot("startup");
        string path = Path.Combine(root, ServerSettings.FileName);
        File.WriteAllText(path, "statCaps: {health: 0}", Utf8);
        int applies = 0;
        bool ready = true;
        bool Apply(ServerSettings _) { ++applies; return ready; }
        using ServerSettingsReloadService service = new(_ => { }, _ => { });
        Check(!service.EnsureLoaded(root, Apply) && service.Current == null && applies == 0,
            "Invalid startup never exposes default policy or calls apply");
        Check(File.ReadAllText(path, Utf8) == "statCaps: {health: 0}",
            "Invalid existing startup file is never overwritten by defaults");
        File.WriteAllText(path, "serverSettings: {maxCharactersPerAccount: 3}", Utf8);
        ready = false;
        Poll(service, root, Apply); Poll(service, root, Apply);
        Check(service.Current == null && applies == 1, "Unavailable ObjectDB can defer without enabling admissions");
        File.Delete(path); Poll(service, root, Apply);
        File.WriteAllText(path, "serverSettings: {maxCharactersPerAccount: 3}", Utf8);
        ready = true;
        Poll(service, root, Apply); Poll(service, root, Apply);
        Check(service.Current?.MaxCharactersPerAccount == 3, "A deferred candidate can recover after a transient missing file");

        File.WriteAllText(path, "serverSettings: {maxCharactersPerAccount: 9}", Utf8);
        NextPoll.SetValue(service, 0L);
        service.Tick(root, Apply); // A captured old-generation read may still be running.
        Task? staleRead = PendingRead.GetValue(service) as Task;
        service.Reset();
        Check(service.Current == null, "Reset clears old-world policy readiness");
        string replacement = NewRoot("replacement");
        Check(service.EnsureLoaded(replacement, Apply) && service.Current?.MaxCharactersPerAccount == 3,
            "Replacement world loads its own policy and cannot adopt stale worker results");
        Check(staleRead != null && staleRead.Wait(5000), "Retired read finishes without touching replacement world");
        Poll(service, replacement, Apply);
        Check(service.Current?.MaxCharactersPerAccount == 3, "Old-generation worker cannot overwrite replacement snapshot");
        service.Dispose();
        int beforeDisposed = applies;
        string absent = Path.Combine(_root, "disposed");
        service.Tick(absent, Apply);
        Check(!service.EnsureLoaded(absent, Apply) && service.Current == null && applies == beforeDisposed &&
            !Directory.Exists(absent), "Disposed service performs no creation/read/apply and remains unready");

        string deferRoot = NewRoot("deferred");
        using ServerSettingsReloadService deferred = new(_ => { }, _ => { });
        ready = false;
        Check(!deferred.EnsureLoaded(deferRoot, Apply), "Valid initial parse waits if main-thread application is deferred");
        ready = true;
        Poll(deferred, deferRoot, Apply);
        Check(deferred.Current != null, "Unchanged deferred candidate retries application on a later poll");

        string readOnlyRoot = NewRoot("readonly");
        string readOnlyPath = Path.Combine(readOnlyRoot, ServerSettings.FileName);
        string readOnlyText = "# preexisting read-only config\nserverSettings: {maxCharactersPerAccount: 4}\n" +
            "statCaps: {damage: 9999}\nstartItems: []\n";
        File.WriteAllText(readOnlyPath, readOnlyText, Utf8);
        File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
        try
        {
            using ServerSettingsReloadService readOnly = new(_ => { }, _ => { });
            Check(readOnly.EnsureLoaded(readOnlyRoot, Apply) && readOnly.Current?.MaxCharactersPerAccount == 4 &&
                readOnly.Current.MaximumDamage == 9999f && readOnly.Current.StartItems.Count == 0 &&
                File.ReadAllText(readOnlyPath, Utf8) == readOnlyText,
                "Existing explicit empty starters, old damage limit and comments load unchanged without any rewrite");
        }
        finally { File.SetAttributes(readOnlyPath, FileAttributes.Normal); }
    }

    private static bool HasDefaultStartItems(ServerSettings settings) =>
        settings.StartItems.Count == 4 &&
        new[] { "HelmetMidsummerCrown", "ArmorRagsChest", "ArmorRagsLegs", "Torch" }
            .All(prefab => settings.StartItems.TryGetValue(prefab, out int amount) && amount == 1);

    private static void Poll(ServerSettingsReloadService service, string root, Func<ServerSettings, bool> apply)
    {
        // Advance only the private schedule, leaving real production read/parse/apply paths intact.
        NextPoll.SetValue(service, 0L);
        service.Tick(root, apply);
        Task? task = PendingRead.GetValue(service) as Task;
        Check(task != null && task.Wait(5000), "Bounded background sample completed");
        NextPoll.SetValue(service, long.MaxValue);
        service.Tick(root, apply);
    }

    private static string NewRoot(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Reject(string yaml)
    {
        bool rejected = false;
        try { ServerSettings.Parse(yaml); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid YAML/type/range/structure is rejected");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        ++_checks;
    }
}

namespace ServerManager
{
    public enum DetectionAction { Off, Log, Kick, Ban }
}
