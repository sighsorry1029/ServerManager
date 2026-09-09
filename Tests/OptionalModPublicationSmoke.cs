using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

// Loads the built plugin, but replaces every publication/native operation with
// delegates. No game process, Steam initialization, or live policy files are used.
internal static class OptionalModPublicationSmoke
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static;
    private static Assembly _plugin = null!;
    private static Type _publisher = null!;
    private static long _check;
    private static long _refresh;
    private static string _header = null!;
    private static string _missing = null!;
    private static string _available = null!;
    private static int _checks;

    private sealed class Fake
    {
        internal readonly object Publisher;
        internal readonly List<KeyValuePair<string, string>> Writes = new();
        internal readonly Dictionary<string, string> Rules = new(StringComparer.Ordinal)
        {
            ["game_tag"] = "vanilla-owned", ["other_mod"] = "other-owned"
        };
        internal bool Ready = true;
        internal bool Fail;
        internal bool FailLog;
        internal int Probes;
        internal int Warnings;
        internal Fake()
        {
            Publisher = Construct(_publisher, (Func<bool>)(() => { ++Probes; return Ready; }),
                (Action<string, string>)((key, value) =>
                {
                    Writes.Add(new KeyValuePair<string, string>(key, value));
                    Rules[key] = value;
                    if (Fail && key != _header) throw new IOException("Injected payload failure");
                }), (Action<string>)(_ =>
                {
                    ++Warnings;
                    if (FailLog) throw new IOException("Injected logging failure");
                }));
        }
        internal void Begin(object network) => Call(Publisher, "Begin", network);
        internal void Stop(object? network = null) => Call(Publisher, "Stop", network);
        internal void Tick(object? network, object? policy, long now,
            bool server = true, bool dedicated = true, bool steam = true) =>
            Call(Publisher, "Tick", network, server, dedicated, steam, policy, now);
    }

    private sealed class LobbyFake
    {
        internal readonly object Publisher;
        internal readonly object Transport;
        internal readonly List<Tuple<ulong, string, string>> Writes = new();
        internal readonly Dictionary<ulong, Dictionary<string, string>> Lobbies = new();
        internal bool Initialized = true;
        internal ulong Lobby = 100;
        internal ulong User = 77;
        internal ulong Owner = 77;
        internal bool RejectPayload;
        internal int InitializedChecks, LobbyReads, UserReads, OwnerReads, Warnings;

        internal LobbyFake()
        {
            Transport = Construct(Type("OptionalModLobbyPublicationTransport"),
                (Func<bool>)(() => { ++InitializedChecks; return Initialized; }),
                (Func<ulong>)(() => { ++LobbyReads; return Lobby; }),
                (Func<ulong>)(() => { ++UserReads; return User; }),
                (Func<ulong, ulong>)(lobby =>
                {
                    ++OwnerReads;
                    Assert(lobby == Lobby, "Owner lookup uses the current server lobby.");
                    return Owner;
                }),
                (Func<ulong, string, string, bool>)((lobby, key, value) =>
                {
                    Writes.Add(Tuple.Create(lobby, key, value));
                    if (RejectPayload && key != _header) return false;
                    GetRules(lobby)[key] = value;
                    return true;
                }));
            Publisher = Construct(_publisher, false,
                (Func<ulong>)(() => (ulong)Call(Transport, "GetReadyTarget")!),
                (Action<ulong, string, string>)((lobby, key, value) =>
                    Call(Transport, "SetKeyValue", lobby, key, value)),
                (Action<string>)(_ => ++Warnings));
        }

        internal Dictionary<string, string> GetRules(ulong lobby)
        {
            if (!Lobbies.TryGetValue(lobby, out Dictionary<string, string> rules))
            {
                rules = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = "Valheim-owned", ["hostID"] = "", ["other_mod"] = "other-owned"
                };
                Lobbies.Add(lobby, rules);
            }
            return rules;
        }
        internal void Begin(object network) => Call(Publisher, "Begin", network);
        internal void Stop(object? network = null) => Call(Publisher, "Stop", network);
        internal void Tick(object? network, object? policy, long now,
            bool server = true, bool dedicated = false, bool steam = true) =>
            Call(Publisher, "Tick", network, server, dedicated, steam, policy, now);
    }

    private static int Main(string[] args)
    {
        try
        {
            string[] roots = { Path.GetDirectoryName(args[0])!,
                Path.Combine(args[1], "BepInEx", "core"),
                Path.Combine(args[1], "valheim_Data", "Managed") };
            AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
            {
                string name = new AssemblyName(request.Name).Name!;
                Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(candidate => candidate.GetName().Name == name);
                if (loaded != null) return loaded;
                foreach (string root in roots)
                {
                    string candidate = Path.Combine(root, name + ".dll");
                    if (File.Exists(candidate)) return Assembly.Load(File.ReadAllBytes(candidate));
                }
                return null;
            };
            _plugin = Assembly.Load(File.ReadAllBytes(args[0]));
            _publisher = Type("OptionalModPublication");
            _check = (long)_publisher.GetField("CheckIntervalTicks", All)!.GetValue(null)!;
            _refresh = (long)_publisher.GetField("RefreshIntervalTicks", All)!.GetValue(null)!;
            Type codec = Type("OptionalModCatalog");
            _header = (string)codec.GetField("HeaderKey", All)!.GetRawConstantValue()!;
            _missing = (string)codec.GetField("UnavailableHeaderValue", All)!.GetRawConstantValue()!;
            _available = (string)codec.GetField("SchemaVersion", All)!.GetRawConstantValue()! + "|available|";
            Assert(_check > 0 && _refresh > _check, "Publication scheduling is bounded.");

            object rules = Rules("mod.optional", "Optional Preview " + new string('x', 90), "1.2.3");
            object policy = Construct(Type("IntegrityPolicySnapshot"), 1L, rules);
            var network = new object();
            GuardTests(network, policy);
            PublicationTests(network, policy);
            LifecycleTests(network, policy);
            RetainedPolicyTests(args[2], network, rules);
            LobbyGuardTests(network, policy);
            LobbyPublicationTests(network, policy);
            LobbyLifecycleTests(network, policy);
            Console.WriteLine("Optional catalog publication smoke passed: " + _checks + " checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void GuardTests(object network, object policy)
    {
        foreach (bool server in new[] { false, true })
        foreach (bool dedicated in new[] { false, true })
        foreach (bool steam in new[] { false, true })
        {
            var fake = new Fake();
            fake.Begin(network);
            fake.Tick(network, policy, 0, server, dedicated, steam);
            Assert(fake.Probes == (server && dedicated && steam ? 1 : 0),
                "Only dedicated Steam servers may probe GameServer readiness.");
        }
        var stopped = new Fake();
        stopped.Tick(network, policy, 0);
        Assert(stopped.Probes == 0, "No publication before network Start.");
        stopped.Begin(network);
        stopped.Tick(new object(), policy, 0);
        Assert(stopped.Probes == 0, "No native calls for a different network.");
        var thread = new Thread(() => stopped.Tick(network, policy, 0));
        thread.Start();
        Assert(thread.Join(3000), "Worker guard check completed.");
        Assert(stopped.Probes == 0, "Worker cannot call any Steam function.");
        stopped.Ready = false;
        stopped.Tick(network, policy, 0);
        Assert(stopped.Probes == 1 && stopped.Writes.Count == 0, "Zero Steam pipe leaves rules untouched.");
        stopped.Tick(network, policy, _check - 1);
        Assert(stopped.Probes == 1, "Not-ready checks are throttled.");
        stopped.Ready = true;
        stopped.Tick(network, policy, _check);
        Assert(stopped.Writes.Count >= 3, "Delayed initialization is retried.");
    }

    private static void PublicationTests(object network, object policy)
    {
        var fake = new Fake();
        fake.Begin(network);
        fake.Tick(network, null, 0);
        Assert(fake.Rules[_header] == _missing, "No initial active policy is unavailable, not empty.");
        fake.Writes.Clear();
        fake.Tick(network, policy, _check);
        Assert(fake.Writes.First().Key == _header && fake.Writes.First().Value == _missing,
            "Publication invalidates its header before any payload.");
        Assert(fake.Writes.Last().Key == _header && fake.Writes.Last().Value.StartsWith(_available),
            "Successful publication commits its available header last.");
        Assert(fake.Writes.All(rule => rule.Key.StartsWith("sm_optional_", StringComparison.Ordinal)),
            "Publisher owns only its namespaced rules.");
        Assert(fake.Rules["game_tag"] == "vanilla-owned" && fake.Rules["other_mod"] == "other-owned",
            "Valheim and other mods keep their metadata.");
        int count = fake.Writes.Count;
        fake.Tick(network, policy, _check * 2);
        Assert(fake.Writes.Count == count, "An unchanged policy is not published each check.");
        fake.Tick(network, policy, _check + _refresh);
        Assert(fake.Writes.Count > count, "Periodic refresh repairs a cleared Steam listing.");
        count = fake.Writes.Count;
        fake.Ready = false;
        fake.Tick(network, policy, _check * 2 + _refresh);
        fake.Ready = true;
        fake.Tick(network, policy, _check * 3 + _refresh);
        Assert(fake.Writes.Count > count, "Pipe disappearance invalidates the successful-publication marker.");

        object empty = Construct(Type("IntegrityPolicySnapshot"), 2L,
            Array.CreateInstance(Type("IntegrityPolicyRule"), 0));
        string[] previousPayload = fake.Rules.Keys.Where(key => key.StartsWith("sm_optional_") && key != _header).ToArray();
        fake.Tick(network, empty, _check * 4 + _refresh);
        var emptyRules = (IReadOnlyDictionary<string, string>)Type("OptionalModCatalog")
            .GetMethod("Encode", All)!.Invoke(null, new[] { empty })!;
        Assert(previousPayload.Any(key => !emptyRules.ContainsKey(key)) &&
            previousPayload.Where(key => !emptyRules.ContainsKey(key)).All(key => fake.Rules[key] == string.Empty),
            "Shrinking catalog blanks old owned chunks.");
        Assert(fake.Rules[_header].StartsWith(_available + "0|"), "A genuine valid empty generation is available.");

        var failing = new Fake { Fail = true, FailLog = true };
        failing.Begin(network);
        failing.Tick(network, policy, 0);
        Assert(failing.Rules[_header] == _missing && failing.Warnings == 1,
            "Partial publication and logging failure cannot escape or commit available.");
        count = failing.Writes.Count;
        failing.Tick(network, policy, _check - 1);
        Assert(failing.Writes.Count == count, "Failed writes do not retry every frame.");
        failing.Tick(network, policy, _check);
        Assert(failing.Warnings == 1, "Repeated publication failures are rate-limited in logs.");
        failing.Fail = false;
        failing.Tick(network, policy, _check * 2);
        Assert(failing.Rules[_header].StartsWith(_available), "Failed generation is retried and committed.");
    }

    private static void LobbyGuardTests(object network, object policy)
    {
        foreach (bool server in new[] { false, true })
        foreach (bool dedicated in new[] { false, true })
        foreach (bool steam in new[] { false, true })
        {
            var host = new LobbyFake();
            var gameServer = new Fake();
            host.Begin(network);
            gameServer.Begin(network);
            host.Tick(network, policy, 0, server, dedicated, steam);
            gameServer.Tick(network, policy, 0, server, dedicated, steam);
            Assert(host.InitializedChecks == (server && !dedicated && steam ? 1 : 0),
                "Only a Steam listen server can enter the lobby readiness path.");
            Assert(gameServer.Probes == (server && dedicated && steam ? 1 : 0),
                "Listen servers and clients never probe GameServer even when both publishers exist.");
        }

        var fake = new LobbyFake { Initialized = false };
        fake.Begin(network);
        fake.Tick(network, policy, 0);
        Assert(fake.InitializedChecks == 1 && fake.LobbyReads == 0 && fake.UserReads == 0 &&
            fake.OwnerReads == 0 && fake.Writes.Count == 0, "Uninitialized SteamAPI is never probed further.");
        fake.Tick(network, policy, _check - 1);
        Assert(fake.InitializedChecks == 1, "Lobby readiness checks are throttled.");
        fake.Initialized = true;
        fake.Lobby = 0;
        fake.Tick(network, policy, _check);
        Assert(fake.UserReads == 0 && fake.OwnerReads == 0 && fake.Writes.Count == 0,
            "Singleplayer/no server lobby never calls native user/owner/data APIs.");
        fake.Lobby = 100;
        fake.User = 0;
        fake.Tick(network, policy, _check * 2);
        Assert(fake.OwnerReads == 0 && fake.Writes.Count == 0, "Missing local Steam identity cannot own a catalog.");
        fake.User = 77;
        fake.Owner = 88;
        fake.Tick(network, policy, _check * 3);
        Assert(fake.OwnerReads == 1 && fake.Writes.Count == 0, "An unowned lobby is never modified.");
        fake.Owner = fake.User;
        fake.Tick(network, policy, _check * 4);
        Assert(fake.GetRules(100)[_header].StartsWith(_available), "Delayed owned lobby readiness publishes normally.");
    }

    private static void LobbyPublicationTests(object network, object policy)
    {
        var fake = new LobbyFake();
        fake.Begin(network);
        fake.Tick(network, null, 0);
        Assert(fake.GetRules(100)[_header] == _missing, "A lobby without active policy advertises unavailable, not empty.");
        fake.Writes.Clear();
        fake.Tick(network, policy, _check);
        Assert(fake.Writes.First().Item2 == _header && fake.Writes.First().Item3 == _missing &&
            fake.Writes.Last().Item2 == _header && fake.Writes.Last().Item3.StartsWith(_available),
            "Lobby writes invalidate first and commit the same digest-bearing codec header last.");
        Assert(fake.Writes.All(write => write.Item1 == 100 && write.Item2.StartsWith("sm_optional_", StringComparison.Ordinal) &&
            write.Item3.Length <= 96 && write.Item3.All(character => character > 0 && character < 128)),
            "Lobby publication keeps exact bounded ASCII codec fields and its own namespace.");
        Assert(fake.GetRules(100)["name"] == "Valheim-owned" && fake.GetRules(100)["hostID"] == "" &&
            fake.GetRules(100)["other_mod"] == "other-owned", "Vanilla and other-mod lobby data stay unchanged.");
        Assert(DecodeStatus(fake.GetRules(100)) == "Available", "Dedicated codec decodes the lobby transport unchanged.");
        int count = fake.Writes.Count;
        fake.Tick(network, policy, _check * 2);
        Assert(fake.Writes.Count == count, "Unchanged lobby data is not republished on every readiness check.");

        fake.Lobby = 200;
        fake.Tick(network, policy, _check * 3);
        Assert(fake.Writes.Count > count && fake.Writes.Skip(count).All(write => write.Item1 == 200 && write.Item3.Length != 0),
            "A recreated lobby immediately gets the catalog at its next check, without old-lobby writes or cleanup keys.");
        Assert(DecodeStatus(fake.GetRules(200)) == "Available", "Replacement lobby contains a complete generation.");
        count = fake.Writes.Count;
        fake.Owner = 88;
        fake.Tick(network, policy, _check * 4);
        Assert(fake.Writes.Count == count, "Ownership loss prevents every metadata write.");
        fake.Owner = fake.User;
        fake.Tick(network, policy, _check * 5);
        Assert(fake.Writes.Count > count, "Reacquired ownership republishes without waiting for periodic refresh.");

        object empty = Construct(Type("IntegrityPolicySnapshot"), 2L, Array.CreateInstance(Type("IntegrityPolicyRule"), 0));
        string[] previous = fake.GetRules(200).Keys.Where(key => key.StartsWith("sm_optional_") && key != _header).ToArray();
        fake.Tick(network, empty, _check * 6);
        Assert(fake.GetRules(200)[_header].StartsWith(_available + "0|") &&
            previous.Any(key => fake.GetRules(200)[key] == string.Empty) && DecodeStatus(fake.GetRules(200)) == "Available",
            "A successful empty lobby generation clears old owned chunks and remains a genuine available empty catalog.");
        count = fake.Writes.Count;
        fake.Tick(network, empty, _check * 6 + _refresh);
        Assert(fake.Writes.Count > count, "Periodic refresh also repairs lobby metadata.");

        var failing = new LobbyFake { RejectPayload = true };
        failing.Begin(network);
        failing.Tick(network, policy, 0);
        Assert(failing.Warnings == 1 && failing.GetRules(100)[_header] == _missing &&
            DecodeStatus(failing.GetRules(100)) == "Missing", "SetLobbyData false cannot commit a partial available catalog.");
        count = failing.Writes.Count;
        failing.Tick(network, policy, _check - 1);
        Assert(failing.Writes.Count == count, "Rejected lobby writes do not retry every frame.");
        failing.Tick(network, policy, _check);
        Assert(failing.Warnings == 1, "Repeated lobby failures use the shared low-rate warning guard.");
        failing.RejectPayload = false;
        failing.Tick(network, policy, _check * 2);
        Assert(DecodeStatus(failing.GetRules(100)) == "Available", "Failed lobby publication retries the complete generation.");

        count = failing.Writes.Count;
        foreach (var field in new[]
        {
            Tuple.Create("name", "do-not-touch"),
            Tuple.Create(_header, new string('x', 97)),
            Tuple.Create(_header, "embedded\0nul"),
            Tuple.Create(_header, "비ASCII")
        })
        {
            bool rejected = false;
            try { Call(failing.Transport, "SetKeyValue", 100UL, field.Item1, field.Item2); }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { rejected = true; }
            Assert(rejected && failing.Writes.Count == count, "Invalid/unowned lobby fields never reach the native boundary.");
        }
    }

    private static void LobbyLifecycleTests(object network, object policy)
    {
        var fake = new LobbyFake();
        fake.Tick(network, policy, 0);
        Assert(fake.InitializedChecks == 0, "No lobby API calls before lifecycle Start.");
        fake.Begin(network);
        fake.Tick(new object(), policy, 0);
        var worker = new Thread(() => fake.Tick(network, policy, 0));
        worker.Start();
        Assert(worker.Join(3000) && fake.InitializedChecks == 0, "Foreign network/worker cannot call the lobby API.");
        fake.Tick(network, policy, 0);
        fake.Stop(network);
        int probes = fake.InitializedChecks, writes = fake.Writes.Count;
        fake.Tick(network, policy, _refresh);
        Assert(fake.InitializedChecks == probes && fake.Writes.Count == writes,
            "Network shutdown is managed-only and prevents all later lobby operations.");
        var nextNetwork = new object();
        fake.Begin(nextNetwork);
        fake.Stop(network);
        fake.Tick(nextNetwork, policy, 0);
        Assert(fake.Writes.Count > writes, "A fresh world republishes even to the same lobby; stale shutdown does not stop it.");
        fake.Stop();
        probes = fake.InitializedChecks;
        fake.Tick(nextNetwork, policy, _refresh);
        Assert(fake.InitializedChecks == probes, "Plugin shutdown closes the lobby publisher without native cleanup.");
    }

    private static string DecodeStatus(IReadOnlyDictionary<string, string> rules)
    {
        object result = Type("OptionalModCatalog").GetMethod("Decode", All)!.Invoke(null, new object[] { rules })!;
        return result.GetType().GetProperty("Status", All)!.GetValue(result)!.ToString()!;
    }

    private static void LifecycleTests(object network, object policy)
    {
        var fake = new Fake();
        fake.Begin(network);
        fake.Tick(network, policy, 0);
        fake.Stop(network);
        int calls = fake.Probes;
        int writes = fake.Writes.Count;
        fake.Tick(network, policy, _refresh);
        Assert(fake.Probes == calls && fake.Writes.Count == writes, "Shutdown never calls GameServer or clears rules.");
        var newerNetwork = new object();
        fake.Begin(newerNetwork);
        fake.Stop(network);
        fake.Tick(newerNetwork, policy, 0);
        Assert(fake.Writes.Count > writes, "New world republishes even the same snapshot; stale shutdown is harmless.");
        fake.Stop();
        calls = fake.Probes;
        fake.Tick(newerNetwork, policy, _refresh);
        Assert(fake.Probes == calls, "Plugin shutdown disables publication without native cleanup.");
    }

    private static void RetainedPolicyTests(string dataRoot, object network, object rules)
    {
        object store = Construct(Type("IntegrityPolicyStore"), dataRoot);
        Type candidateType = Type("IntegrityPolicyStore").GetNestedType("ReloadCandidate", All)!;
        Array noDiagnostics = Array.CreateInstance(Type("IntegrityDiagnostic"), 0);
        object good = Construct(candidateType, rules, noDiagnostics);
        Call(store, "PublishReload", good);
        object current = store.GetType().GetProperty("Current", All)!.GetValue(store)!;
        var fake = new Fake();
        fake.Begin(network);
        fake.Tick(network, current, 0);
        string oldHeader = fake.Rules[_header];
        int writes = fake.Writes.Count;
        object error = Construct(Type("IntegrityDiagnostic"), "fixture.invalid", "Injected invalid reload", "");
        Array diagnostics = Array.CreateInstance(Type("IntegrityDiagnostic"), 1);
        diagnostics.SetValue(error, 0);
        object bad = Construct(candidateType, Array.CreateInstance(Type("IntegrityPolicyRule"), 0), diagnostics);
        object result = Call(store, "PublishReload", bad)!;
        Assert(!(bool)result.GetType().GetProperty("Success")!.GetValue(result)! &&
            (bool)result.GetType().GetProperty("KeptPreviousSnapshot")!.GetValue(result)!,
            "Actual policy store rejects a failed reload transaction.");
        object retained = store.GetType().GetProperty("Current", All)!.GetValue(store)!;
        Assert(ReferenceEquals(current, retained), "Failed reload retains the exact active snapshot.");
        fake.Tick(network, retained, _check);
        Assert(fake.Writes.Count == writes && fake.Rules[_header] == oldHeader,
            "Failed reload never advertises an empty or replacement catalog.");
        fake.Tick(network, retained, _refresh);
        Assert(fake.Rules[_header] == oldHeader, "Periodic refresh retains the last successful policy catalog.");

        object next = Construct(candidateType, Rules("mod.next", "Next Optional", "2.0.0"), noDiagnostics);
        Call(store, "PublishReload", next);
        object replacement = store.GetType().GetProperty("Current", All)!.GetValue(store)!;
        fake.Tick(network, replacement, _refresh + _check);
        Assert(fake.Rules[_header] != oldHeader, "A successful policy generation change publishes new catalog bytes.");
    }

    private static Array Rules(string guid, string name, string version)
    {
        object requirement = Enum.Parse(Type("IntegrityRequirement"), "Optional");
        object rule = Construct(Type("IntegrityPolicyRule"), guid, name, requirement,
            new[] { new string('a', 64) }, new[] { version });
        Array result = Array.CreateInstance(Type("IntegrityPolicyRule"), 1);
        result.SetValue(rule, 0);
        return result;
    }
    private static Type Type(string name) => _plugin.GetType("ServerManager." + name, true)!;
    private static object Construct(Type type, params object?[] args) =>
        type.GetConstructors(All).Single(ctor => ctor.GetParameters().Length == args.Length).Invoke(args);
    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, All)!.Invoke(target, args);
    private static void Assert(bool condition, string message)
    {
        ++_checks;
        if (!condition) throw new InvalidOperationException(message);
    }
}
