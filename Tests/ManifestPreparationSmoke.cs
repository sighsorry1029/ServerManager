using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

// Exercises the actual .NET48 plugin against owned temporary bytes only.
// PluginInfo metadata is constructed without creating Unity plugin instances.
internal static class ManifestPreparationSmoke
{
    private static readonly BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags InstanceAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type _scanner = null!;
    private static Type _pluginInfo = null!;
    private static Type _metadata = null!;
    private static Type _limitsType = null!;
    private static Type _codec = null!;
    private static SemaphoreSlim _gate = null!;
    private static object _limits = null!;
    private static int _checks;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3) throw new ArgumentException("Expected plugin path, game path, and owned temporary fixture directory.");
            string pluginPath = Path.GetFullPath(args[0]);
            string[] roots = { Path.GetDirectoryName(pluginPath)!, Path.Combine(args[1], "BepInEx", "core"), Path.Combine(args[1], "valheim_Data", "Managed") };
            AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
            {
                string shortName = new AssemblyName(request.Name).Name!;
                Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.GetName().Name == shortName);
                if (loaded != null) return loaded;
                string name = shortName + ".dll";
                foreach (string root in roots)
                {
                    string candidate = Path.Combine(root, name);
                    if (File.Exists(candidate)) return Assembly.Load(File.ReadAllBytes(candidate));
                }
                return null;
            };
            // Some installed dependencies retain Windows download-zone metadata.
            // Read-only byte loading avoids changing those user-owned files.
            Assembly bep = Assembly.Load(File.ReadAllBytes(Path.Combine(args[1], "BepInEx", "core", "BepInEx.dll")));
            _pluginInfo = bep.GetType("BepInEx.PluginInfo", true)!;
            _metadata = bep.GetType("BepInEx.BepInPlugin", true)!;
            Assembly assembly = Assembly.LoadFrom(pluginPath);
            _scanner = assembly.GetType("ServerManager.PluginManifestScanner", true)!;
            // Use the exact dependency identity bound to the plugin; a build
            // may carry its own BepInEx copy in a different loader context.
            _pluginInfo = _scanner.GetMethods(StaticAll).Single(method => method.Name == "Build" && method.GetParameters().Length == 2)
                .GetParameters()[0].ParameterType.GetGenericArguments()[0];
            _metadata = _pluginInfo.GetProperty("Metadata", InstanceAll)!.PropertyType;
            _limitsType = assembly.GetType("ServerManager.IntegrityLimits", true)!;
            _codec = assembly.GetType("ServerManager.IntegrityManifestCodec", true)!;
            _limits = Limits();
            // Existing production gate gives deterministic scheduling without a
            // test-only transport, alternative scanner, or filesystem cache.
            _gate = (SemaphoreSlim)_scanner.GetFields(StaticAll).Single(field => field.FieldType == typeof(SemaphoreSlim)).GetValue(null)!;
            Directory.CreateDirectory(args[2]);
            Run(args[2]);
            Console.WriteLine("Manifest preparation smoke passed (" + _checks + " assertions; actual .NET48 plugin, isolated files, no Unity/network).");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Run(string root)
    {
        byte[] bytes = Enumerable.Range(0, 160123).Select(value => (byte)(value % 251)).ToArray();
        string path = Path.Combine(root, "fixture.dll");
        File.WriteAllBytes(path, bytes);
        string expected = Hash(bytes);
        ValidateEquivalentAndSnapshot(path, expected);
        ValidateFreshHash(path, bytes, expected);
        ValidateFailures(root, path);
        ValidateCancellation(path);
        ValidateRegistryFreshness(root, path);
    }

    private static void ValidateEquivalentAndSnapshot(string path, string expected)
    {
        object first = Plugin("tests.manifest.z", "Fixture Z", path);
        object second = Plugin("tests.manifest.a", "Fixture A", path);
        Array inputs = Plugins(first, second);
        object sync = Invoke(_scanner, "Build", new object?[] { inputs, _limits })!;
        Check(Success(sync), "The deterministic synchronous scanner fixture failed.");
        using (var preparation = new OwnedPreparation(Begin(inputs)))
        {
            object result = AwaitResult(preparation.Value);
            Check(Success(result), "Background preparation rejected a valid manifest.");
            Check(Encode(result).SequenceEqual(Encode(sync)), "Background and synchronous manifest bytes differ.");
            object[] entries = Entries(result);
            Check(entries.Length == 2, "Shared DLL path preparation dropped a plugin entry.");
            Check((string)Get(entries[0], "PluginGuid")! == "tests.manifest.a", "Canonical manifest order changed.");
            Check(entries.All(entry => (string)Get(entry, "FileSha256")! == expected), "Manifest hashes do not match the file bytes.");
            preparation.Dispose();
            Check(!TryResult(preparation.Value, out _), "Disposed preparation exposed an already completed result.");
        }

        Check(_gate.Wait(3000), "Could not acquire the scanner's idle worker gate.");
        object? captured = null;
        try
        {
            object original = Plugin("tests.snapshot.original", "Original Name", path);
            Array mutableInputs = Plugins(original);
            var watch = Stopwatch.StartNew();
            captured = Begin(mutableInputs);
            Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Begin blocked on the worker gate instead of returning immediately.");
            Check(!TryResult(captured, out _), "A queued manifest preparation published a result before hashing.");
            // The worker has not acquired the gate, so these mutations cannot
            // race accidentally ahead of a lazy snapshot implementation.
            Set(original, "Metadata", NewMetadata("tests.snapshot.changed", "Changed Name"));
            Set(original, "Location", Path.Combine(Path.GetDirectoryName(path)!, "does-not-exist.dll"));
            mutableInputs.SetValue(Plugin("tests.snapshot.replaced", "Replaced Name", path), 0);
        }
        finally { _gate.Release(); }
        using (var preparation = new OwnedPreparation(captured!))
        {
            object result = AwaitResult(preparation.Value);
            Check(Success(result), "PluginInfo mutation after Begin changed the captured location.");
            object entry = Entries(result).Single();
            Check((string)Get(entry, "PluginGuid")! == "tests.snapshot.original" &&
                (string)Get(entry, "Name")! == "Original Name" && (string)Get(entry, "FileSha256")! == expected,
                "Worker observed live metadata/collection changes instead of immutable captured strings.");
        }
    }

    private static void ValidateFreshHash(string path, byte[] before, string expectedBefore)
    {
        DateTime previousTime = File.GetLastWriteTimeUtc(path);
        byte[] after = (byte[])before.Clone();
        after[0] ^= 0x7f;
        File.WriteAllBytes(path, after);
        File.SetLastWriteTimeUtc(path, previousTime);
        Check(new FileInfo(path).Length == before.Length && File.GetLastWriteTimeUtc(path) == previousTime,
            "The same-size/same-mtime rehash fixture was not established.");
        using (var preparation = new OwnedPreparation(Begin(Plugins(Plugin("tests.fresh", "Fresh", path)))))
        {
            object result = AwaitResult(preparation.Value);
            string actual = (string)Get(Entries(result).Single(), "FileSha256")!;
            Check(actual == Hash(after) && actual != expectedBefore, "A later connection reused a stale size/mtime or process hash cache.");
        }
    }

    private static void ValidateFailures(string root, string path)
    {
        Check(_limitsType.GetProperty("MaxPluginFileBytes", InstanceAll) == null &&
            _limitsType.GetConstructors().Single().GetParameters().Length == 4,
            "Manifest configuration still exposes a DLL file-size restriction.");
        AssertFailure(Plugins(Plugin("tests.missing", "Missing", Path.Combine(root, "missing.dll"))), _limits, "manifest.file_not_found");
        AssertFailure(Plugins(Plugin("tests.duplicate", "One", path), Plugin("tests.duplicate", "Two", path)), _limits, "manifest.duplicate_guid");
        AssertFailure(Plugins(Plugin("tests.one", "One", path), Plugin("tests.two", "Two", path)), Limits(maxCount: 1), "manifest.too_many_entries");
        AssertFailure(Plugins(Plugin("tests.empty", "No location", string.Empty)), _limits, "manifest.missing_location");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            AssertFailure(Plugins(Plugin("tests.locked", "Locked", path)), _limits, "manifest.hash_failed");
        using (var preparation = new OwnedPreparation(Begin(Plugins(Plugin("tests.payload", new string('N', 160), path)))))
        {
            object result = AwaitResult(preparation.Value);
            object encoded = Invoke(_codec, "TryEncode", new object?[] { Get(result, "Manifest"), Limits(maxPayload: 64) })!;
            Check(Success(result) && !Success(encoded), "Prepared manifest bypassed the advertised wire payload limit at encoding.");
        }
    }

    private static void ValidateCancellation(string path)
    {
        var cancelled = new List<OwnedPreparation>();
        Check(_gate.Wait(3000), "A completed preparation leaked the worker gate.");
        object? next = null;
        try
        {
            for (int index = 0; index < 8; index++)
            {
                var preparation = new OwnedPreparation(Begin(Plugins(Plugin("tests.cancel." + index, "Cancelled", path))));
                cancelled.Add(preparation);
                Check(!TryResult(preparation.Value, out _), "Queued cancelled preparation completed without owning the worker gate.");
                preparation.Dispose();
            }
            Task[] tasks = cancelled.Select(value => WorkerTask(value.Value)).ToArray();
            Check(SpinWait.SpinUntil(() => tasks.All(task => task.IsCompleted), 3000), "Disposing a queued preparation did not cancel the semaphore wait promptly.");
            Check(cancelled.All(value => !TryResult(value.Value, out _)), "Cancelled/old connection exposed a completed result.");
            next = Begin(Plugins(Plugin("tests.cancel.survivor", "Survivor", path)));
            Check(!TryResult(next, out _), "A queued next connection bypassed serialization.");
        }
        finally
        {
            _gate.Release();
            foreach (OwnedPreparation value in cancelled) value.Dispose();
        }
        using (var survivor = new OwnedPreparation(next!))
        {
            Check(Success(AwaitResult(survivor.Value)), "Rapid reconnect cancellations blocked the next valid preparation.");
        }
        Check(_gate.Wait(3000), "A cancelled/completed worker retained the scanner gate.");
        _gate.Release();
        Check(_gate.CurrentCount == 1, "Cancellation released the scanner gate more than once.");
    }

    private static void ValidateRegistryFreshness(string root, string path)
    {
        Assembly bep = _pluginInfo.Assembly;
        Type paths = bep.GetType("BepInEx.Paths", true)!;
        // Chainloader's static initializer binds CoreConfig entries. Redirect
        // that test-process-only config before touching Chainloader, so even
        // those incidental writes stay inside our owned temporary directory.
        paths.GetField("<BepInExConfigPath>k__BackingField", StaticAll)!.SetValue(null, Path.Combine(root, "BepInEx-test.cfg"));
        paths.GetField("<ConfigPath>k__BackingField", StaticAll)!.SetValue(null, root);
        Type chainloader = bep.GetType("BepInEx.Bootstrap.Chainloader", true)!;
        IDictionary registry = (IDictionary)chainloader.GetProperty("PluginInfos", StaticAll)!.GetValue(null, null)!;
        var previous = new List<DictionaryEntry>();
        IDictionaryEnumerator originalEntries = registry.GetEnumerator();
        while (originalEntries.MoveNext()) previous.Add(originalEntries.Entry);
        try
        {
            registry.Clear();
            object first = Plugin("tests.registry.first", "First", path);
            object second = Plugin("tests.registry.second", "Second", path);
            registry.Add("first", first);
            registry.Add("second", second);
            using (var preparation = new OwnedPreparation(Invoke(_scanner, "BeginCurrent", new object?[] { _limits })!))
            {
                Check(Success(AwaitResult(preparation.Value)) && MatchesRegistry(preparation.Value), "Unchanged registry failed its post-hash freshness check.");
                registry.Clear();
                registry.Add("second", second);
                registry.Add("first", first);
                Check(MatchesRegistry(preparation.Value), "A registry order change invalidated identical plugin metadata.");
                Set(first, "Metadata", NewMetadata("tests.registry.first", "Renamed"));
                Check(!MatchesRegistry(preparation.Value), "Plugin name mutation after preparation was not detected.");
                Set(first, "Metadata", NewMetadata("tests.registry.other", "First"));
                Check(!MatchesRegistry(preparation.Value), "Plugin GUID mutation after preparation was not detected.");
                Set(first, "Metadata", NewMetadata("tests.registry.first", "First"));
                Set(first, "Location", path + ".different");
                Check(!MatchesRegistry(preparation.Value), "Plugin DLL path mutation after preparation was not detected.");
                Set(first, "Location", path);
                registry.Add("third", Plugin("tests.registry.third", "Third", path));
                Check(!MatchesRegistry(preparation.Value), "A plugin added after the snapshot was omitted without rejecting the manifest.");
                registry.Remove("third");
                registry.Remove("second");
                Check(!MatchesRegistry(preparation.Value), "A removed plugin was not detected by registry freshness.");
                registry.Add("second", second);
                Check(MatchesRegistry(preparation.Value), "Restored identical registry metadata failed freshness validation.");
                preparation.Dispose();
                Check(!MatchesRegistry(preparation.Value), "Disposed preparation still claimed registry freshness.");
            }
        }
        finally
        {
            registry.Clear();
            foreach (DictionaryEntry entry in previous) registry.Add(entry.Key, entry.Value);
        }
    }

    private static bool MatchesRegistry(object preparation) => (bool)Invoke(preparation.GetType(), "MatchesCurrentPlugins", Array.Empty<object?>(), preparation)!;

    private static void AssertFailure(Array plugins, object limits, string expectedCode)
    {
        using (var preparation = new OwnedPreparation(Begin(plugins, limits)))
        {
            object result = AwaitResult(preparation.Value);
            string[] codes = ((IEnumerable)Get(result, "Diagnostics")!).Cast<object>().Select(item => (string)Get(item, "Code")!).ToArray();
            Check(!Success(result) && Get(result, "Manifest") == null && codes.Contains(expectedCode),
                "Scanner failure did not fail closed with " + expectedCode + "; got " + string.Join(", ", codes));
        }
    }

    private static object Begin(Array plugins, object? limits = null) => Invoke(_scanner, "Begin", new object?[] { plugins, limits ?? _limits })!;
    private static object AwaitResult(object preparation)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (TryResult(preparation, out object? result)) return result!;
            Thread.Sleep(1);
        }
        throw new InvalidOperationException("Manifest preparation did not complete within the fixture deadline.");
    }
    private static bool TryResult(object preparation, out object? result)
    {
        object?[] arguments = { null };
        bool completed = (bool)Invoke(preparation.GetType(), "TryGetResult", arguments, preparation)!;
        result = arguments[0];
        return completed;
    }
    private static Task WorkerTask(object preparation) => (Task)preparation.GetType().GetFields(InstanceAll)
        .Single(field => typeof(Task).IsAssignableFrom(field.FieldType)).GetValue(preparation)!;
    private static object[] Entries(object result) => ((IEnumerable)Get(Get(result, "Manifest")!, "Entries")!).Cast<object>().ToArray();
    private static byte[] Encode(object result) => (byte[])Get(Invoke(_codec, "TryEncode", new[] { Get(result, "Manifest"), _limits })!, "Payload")!;
    private static bool Success(object value) => (bool)Get(value, "Success")!;
    private static object Limits(int maxPayload = 262144, int maxCount = 1024) =>
        Activator.CreateInstance(_limitsType, new object[] { maxPayload, maxCount, 256, 512 })!;
    private static Array Plugins(params object[] items)
    {
        Array result = Array.CreateInstance(_pluginInfo, items.Length);
        for (int index = 0; index < items.Length; index++) result.SetValue(items[index], index);
        return result;
    }
    private static object Plugin(string guid, string name, string path)
    {
        object value = Activator.CreateInstance(_pluginInfo, true)!;
        Set(value, "Metadata", NewMetadata(guid, name));
        Set(value, "Location", path);
        return value;
    }
    private static object NewMetadata(string guid, string name) => Activator.CreateInstance(_metadata, new object[] { guid, name, "1.0.0" })!;
    private static object? Get(object value, string name) => value.GetType().GetProperty(name, InstanceAll)!.GetValue(value, null);
    private static void Set(object value, string name, object data) => value.GetType().GetProperty(name, InstanceAll)!.GetSetMethod(true)!.Invoke(value, new[] { data });
    private static string Hash(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }
    private static object? Invoke(Type type, string name, object?[] args, object? instance = null)
    {
        MethodInfo method = type.GetMethods(instance == null ? StaticAll : InstanceAll).Single(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
        try { return method.Invoke(instance, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
    private static void Check(bool condition, string message)
    {
        ++_checks;
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class OwnedPreparation : IDisposable
    {
        internal OwnedPreparation(object value) => Value = value;
        internal object Value { get; }
        public void Dispose() => ((IDisposable)Value).Dispose();
    }
}
