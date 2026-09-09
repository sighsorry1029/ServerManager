using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;

internal static class DependencyManifestSmoke
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type _scanner = null!;
    private static object _limits = null!;
    private static int _checks;

    private static int Main(string[] args)
    {
        try
        {
            string[] search = { Path.GetDirectoryName(args[0])!, Path.Combine(args[1], "BepInEx", "core"), Path.Combine(args[1], "valheim_Data", "Managed") };
            AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
            {
                string name = new AssemblyName(request.Name).Name!;
                Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => value.GetName().Name == name);
                if (loaded != null) return loaded;
                foreach (string root in search)
                {
                    string path = Path.Combine(root, name + ".dll");
                    if (File.Exists(path)) return Assembly.Load(File.ReadAllBytes(path));
                }
                return null;
            };
            return Run(args);
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int Run(string[] args)
    {
        Assembly plugin = Assembly.LoadFrom(args[0]);
        _scanner = plugin.GetType("ServerManager.DependencyManifestScanner", true)!;
        _limits = Activator.CreateInstance(plugin.GetType("ServerManager.IntegrityLimits", true)!, 262144, 1024, 256, 512)!;
        string root = args[2];
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "renamed.dll");
        using (ModuleDefinition module = ModuleDefinition.CreateModule("DependencyFixture", ModuleKind.Dll)) module.Write(first);
        string key = "assembly:dependencyfixture";
        File.WriteAllText(Path.Combine(root, "unrelatedNative.dll"), "not a managed assembly");
        using (var p = Begin(new[] { key }, Array.Empty<Assembly>(), new[] { root }))
        {
            object result = Wait(p.Value);
            Check(Success(result) && Entries(result).Length == 1, "Unloaded renamed dependency was not found by static assembly identity.");
            Check(p.Matches(Array.Empty<Assembly>()), "Unloaded dependency preparation does not match unchanged assembly set.");
            Check(p.Matches(new[] { typeof(object).Assembly }), "An unrelated framework load invalidated targeted preparation.");
        }
        using (var p = Begin(new[] { "assembly:missing" }, Array.Empty<Assembly>(), new[] { root }))
            Check(Success(Wait(p.Value)) && Entries(Wait(p.Value)).Length == 0, "A missing selected library must be omitted for server required-policy validation.");
        string copy = Path.Combine(root, "copy.dll");
        File.Copy(first, copy);
        using (var p = Begin(new[] { key }, Array.Empty<Assembly>(), new[] { root }))
            Check(Success(Wait(p.Value)), "Same-hash unused dependency copies were rejected.");
        using (ModuleDefinition module = ModuleDefinition.CreateModule("DependencyFixture", ModuleKind.Dll))
        {
            module.Assembly.Name.Version = new Version(2, 0, 0, 0);
            module.Write(copy);
        }
        using (var p = Begin(new[] { key }, Array.Empty<Assembly>(), new[] { root }))
            Check(!Success(Wait(p.Value)), "Ambiguous different-hash dependency copies were accepted.");

        Assembly actual = Assembly.LoadFile(first);
        Assembly dynamicUnrelated = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("UnrelatedDynamicFixture"), AssemblyBuilderAccess.Run);
        using (var p = Begin(new[] { key }, new[] { actual }, new[] { root }))
        {
            object result = Wait(p.Value);
            Check(Success(result), "Loaded dependency did not override unused different-hash disk copies.");
            Check((string)Get(Entries(result).Single(), "FileSha256") == Hash(first), "Loaded dependency hash came from an unused disk copy.");
            Check(p.Matches(new[] { actual }), "Actual loaded identity is not fresh after hashing.");
            Check(p.Matches(new[] { actual, dynamicUnrelated }), "Unrelated dynamic assembly load invalidated a selected library snapshot.");
            Check(!p.Matches(Array.Empty<Assembly>()), "Removing a selected loaded identity was not detected.");
            p.Dispose();
            Check(!TryResult(p.Value, out _), "Disposed dependency preparation exposed its result.");
        }
        using (var p = Begin(new[] { key }, Array.Empty<Assembly>(), Array.Empty<string>()))
        {
            Check(Success(Wait(p.Value)), "Empty candidate search failed.");
            Check(!p.Matches(new[] { actual }), "A newly loaded selected dependency did not invalidate the captured set.");
        }
        Assembly memory = Assembly.Load(File.ReadAllBytes(copy));
        Check(string.IsNullOrEmpty(memory.Location), "The in-memory fixture has an unexpected backing file.");
        using (var p = Begin(new[] { key }, new[] { memory }, new[] { root }))
            Check(!Success(Wait(p.Value)), "An in-memory selected dependency was masked by a disk candidate.");
        Assembly actualSecond = Assembly.LoadFile(copy);
        using (var p = Begin(new[] { key }, new[] { actualSecond }, new[] { root }))
        {
            object result = Wait(p.Value);
            Check(Success(result) && (string)Get(Entries(result).Single(), "FileSha256") == Hash(copy),
                "Loaded alternative dependency was hidden by a different clean installed copy.");
        }
        using (var p = Begin(new[] { key }, new[] { actual, actualSecond }, new[] { root }))
            Check(!Success(Wait(p.Value)), "Conflicting actually loaded dependencies were accepted.");
        string brokenRoot = Path.Combine(root, "broken");
        Directory.CreateDirectory(brokenRoot);
        File.WriteAllText(Path.Combine(brokenRoot, "DependencyFixture.dll"), "not a managed DLL");
        using (var p = Begin(new[] { key }, Array.Empty<Assembly>(), new[] { brokenRoot }))
            Check(!Success(Wait(p.Value)), "Native bytes named after a selected library were silently accepted.");

        SemaphoreSlim gate = (SemaphoreSlim)_scanner.GetField("ScanGate", All)!.GetValue(null)!;
        Check(gate.Wait(3000), "The dependency worker leaked its scan gate.");
        try
        {
            using (var p = Begin(new[] { key }, new[] { actual }, new[] { root }))
            {
                Check(!TryResult(p.Value, out _), "Dependency Begin blocked or bypassed its worker gate.");
                p.Dispose();
                Task task = (Task)p.Value.GetType().GetField("_task", All)!.GetValue(p.Value)!;
                Check(SpinWait.SpinUntil(() => task.IsCompleted, 3000), "Disposed queued dependency work did not cancel promptly.");
            }
        }
        finally { gate.Release(); }
        ValidateYamlDotNetPolicy(plugin, Path.GetDirectoryName(args[0])!, root);
        Console.WriteLine("Dependency manifest smoke passed (" + _checks + " assertions, actual .NET48 scanner, isolated files).");
        return 0;
    }

    private static void ValidateYamlDotNetPolicy(Assembly plugin, string buildRoot, string root)
    {
        string source = Path.Combine(buildRoot, "YamlDotNet.dll");
        Check(File.Exists(source), "The real build dependency YamlDotNet.dll fixture is missing.");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(value => value.GetName().Name == "YamlDotNet"),
            "YamlDotNet was already loaded; this test must exercise metadata-only lazy dependency discovery.");
        string serverRoot = Path.Combine(root, "yaml-server");
        string clientRoot = Path.Combine(root, "yaml-client");
        Directory.CreateDirectory(Path.Combine(serverRoot, "required"));
        Directory.CreateDirectory(clientRoot);
        File.Copy(source, Path.Combine(serverRoot, "required", "YamlDotNet.dll"));
        string candidate = Path.Combine(clientRoot, "YamlDotNet.dll");
        File.Copy(source, candidate);
        Type storeType = plugin.GetType("ServerManager.IntegrityPolicyStore", true)!;
        object store = Activator.CreateInstance(storeType, serverRoot, _limits)!;
        object reload = storeType.GetMethod("TryReload", All)!.Invoke(store, null)!;
        Check(Success(reload), "The reference policy rejected the actual YamlDotNet managed dependency.");
        object snapshot = Get(reload, "ActiveSnapshot");
        object[] rules = ((IEnumerable)Get(snapshot, "Rules")).Cast<object>().ToArray();
        Check(rules.Length == 1 && (string)Get(rules[0], "PluginGuid") == "assembly:yamldotnet" &&
            Get(rules[0], "Requirement").ToString() == "Required",
            "The actual YamlDotNet reference did not produce a required assembly:yamldotnet rule.");
        Type codec = plugin.GetType("ServerManager.IntegrityManifestCodec", true)!;
        Type validator = plugin.GetType("ServerManager.IntegrityValidator", true)!;
        MethodInfo encode = codec.GetMethods(All).Single(method => method.Name == "TryEncode" && method.GetParameters().Length == 2);
        MethodInfo decode = codec.GetMethods(All).Single(method => method.Name == "TryDecode" && method.GetParameters().Length == 2);
        MethodInfo validate = validator.GetMethods(All).Single(method => method.Name == "Validate" &&
            method.GetParameters().Length == 2 && method.GetParameters()[1].ParameterType.Name == "IntegrityManifestDecodeResult");

        object ValidateCandidate()
        {
            using (var preparation = Begin(new[] { "assembly:yamldotnet" }, Array.Empty<Assembly>(), new[] { clientRoot }))
            {
                object build = Wait(preparation.Value);
                Check(Success(build), "The real YamlDotNet dependency could not be prepared without loading it.");
                object encoded = encode.Invoke(null, new[] { Get(build, "Manifest"), _limits })!;
                Check(Success(encoded), "The YamlDotNet manifest failed bounded wire encoding.");
                object decoded = decode.Invoke(null, new[] { Get(encoded, "Payload"), _limits })!;
                Check(Success(decoded), "The YamlDotNet manifest failed bounded wire decoding.");
                return validate.Invoke(null, new[] { snapshot, decoded })!;
            }
        }

        object accepted = ValidateCandidate();
        Check((bool)Get(accepted, "Allowed"), "Identical real YamlDotNet reference/client DLLs failed end-to-end policy validation.");
        // An owned PE overlay changes the full-file hash without executing or
        // breaking the dependency's managed metadata or modifying the real DLL.
        using (FileStream changed = new(candidate, FileMode.Append, FileAccess.Write)) changed.WriteByte(0x42);
        object rejected = ValidateCandidate();
        string[] codes = ((IEnumerable)Get(rejected, "Diagnostics")).Cast<object>().Select(value => (string)Get(value, "Code")).ToArray();
        Check(!(bool)Get(rejected, "Allowed") && codes.Contains("validation.hash_not_allowed"),
            "The actual YamlDotNet dependency with different bytes was accepted under the required hash rule.");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(value => value.GetName().Name == "YamlDotNet"),
            "The dependency was executed/loaded while scanning, encoding, or validating static metadata.");
    }

    private static Owned Begin(string[] keys, Assembly[] assemblies, string[] roots) =>
        new Owned(_scanner.GetMethods(All).Single(method => method.Name == "Begin" && method.GetParameters().Length == 4)
            .Invoke(null, new object[] { keys, _limits, assemblies, roots })!);
    private static object Wait(object preparation)
    {
        object? result = null;
        Check(SpinWait.SpinUntil(() => TryResult(preparation, out result), 10000), "Dependency fixture worker timed out.");
        return result!;
    }
    private static bool TryResult(object preparation, out object? result)
    {
        object?[] values = { null };
        bool completed = (bool)preparation.GetType().GetMethod("TryGetResult", All)!.Invoke(preparation, values)!;
        result = values[0];
        return completed;
    }
    private static object Get(object value, string name) => value.GetType().GetProperty(name, All)!.GetValue(value, null)!;
    private static string Hash(string path)
    {
        using (SHA256 hash = SHA256.Create()) using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static bool Success(object value) => (bool)Get(value, "Success");
    private static object[] Entries(object result) => ((IEnumerable)Get(Get(result, "Manifest"), "Entries")).Cast<object>().ToArray();
    private static void Check(bool condition, string message) { ++_checks; if (!condition) throw new InvalidOperationException(message); }
    private sealed class Owned : IDisposable
    {
        internal readonly object Value;
        internal Owned(object value) { Value = value; }
        internal bool Matches(Assembly[] assemblies) => (bool)Value.GetType().GetMethods(All)
            .Single(method => method.Name == "MatchesCurrentAssemblies" && method.GetParameters().Length == 1).Invoke(Value, new object[] { assemblies })!;
        public void Dispose() => ((IDisposable)Value).Dispose();
    }
}
