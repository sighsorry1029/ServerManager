// Source-linked production parser/codec/store. File writes use an owned temp root only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerManager.Commands;

internal static class ItemDataPresetsSmoke
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly FieldInfo NextPoll = typeof(ItemDataPresetStore)
        .GetField("_nextPoll", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PendingRead = typeof(ItemDataPresetStore)
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
                !_root.Contains("ServerManager-ItemDataPresets-"))
                throw new InvalidOperationException("The harness requires an owned temporary root.");
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            Codec(); Parser(); Reload(); StartupAndLifecycle();
            Console.WriteLine("PASS: Itemdata.yml parser/codec/reload (" + _checks +
                " assertions; source-linked production code, no game/network/user-data access).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Codec()
    {
        Dictionary<string, string> exact = new(StringComparer.Ordinal)
        {
            ["example.mod/key"] = "{\"name\":\"value\"}\\folder\r\n\t\b\f",
            ["Key"] = "Keep case", ["key"] = "different", [""] = "", ["empty"] = "",
            ["  spaced  "] = "  preserve spaces  ", ["한글"] = "가나다 😀",
            ["\u0085\u2028\u2029"] = "\0\u0001\u001f\u007f\u0080\u0085\u009f\u2028\u2029",
            ["number"] = "123", ["bool"] = "true", ["null"] = "null"
        };
        string encoded = ItemDataPresets.EncodeData(exact);
        Check(encoded.Contains("\\u0085") && encoded.Contains("\\u2028") && encoded.Contains("\\u2029") &&
            !encoded.Any(char.IsControl) && !encoded.Contains('\u2028') && !encoded.Contains('\u2029'),
            "Encoder escapes every control and Unicode separator while preserving exact strings");
        Equal(exact, ItemDataPresets.DecodeData(encoded), "Encode/decode exact-string roundtrip");
        Equal(exact, ItemDataPresets.DecodeData(Payload(ItemDataPresets.Parse("- id: restoringnow\n  CustomData: " + encoded))),
            "Copied JSON CustomData pasted as YAML retains escapes, Unicode, whitespace and empty keys/values");
        Equal(new Dictionary<string, string> { ["slash"] = "/", ["emoji"] = "😀" },
            ItemDataPresets.DecodeData(" { \"slash\" : \"\\/\", \"emoji\":\"\\uD83D\\uDE00\" } \r\n"),
            "JSON whitespace, escaped slash and paired surrogate escapes decode exactly");
        Check(ItemDataPresets.EncodeData(new Dictionary<string, string>()) == "{}" &&
            ItemDataPresets.DecodeData("{}").Count == 0, "Empty custom data object is valid");

        string boundary = ItemDataPresets.EncodeData(new Dictionary<string, string> { ["k"] = new('a', 4088) });
        Check(boundary.Length == ItemDataPresets.MaximumPayloadCharacters &&
            ItemDataPresets.DecodeData(boundary)["k"].Length == 4088, "Exact 4096-character payload boundary is accepted");
        boundary = ItemDataPresets.EncodeData(new Dictionary<string, string> { ["k"] = new('\uAC00', 2728) });
        Check(Utf8.GetByteCount(boundary) == ItemDataPresets.MaximumPayloadBytes &&
            ItemDataPresets.DecodeData(boundary)["k"].Length == 2728, "Exact 8192 UTF-8-byte boundary is accepted");
        Reject(() => ItemDataPresets.EncodeData(new Dictionary<string, string> { ["k"] = new('a', 4089) }));
        Reject(() => ItemDataPresets.EncodeData(new Dictionary<string, string> { ["k"] = new('\uAC00', 2729) }));
        Reject(() => ItemDataPresets.EncodeData(new Dictionary<string, string> { ["k"] = new('\n', 2045) }));
        Reject(() => ItemDataPresets.EncodeData(new Dictionary<string, string> { [new string('k', 257)] = "v" }));
        Reject(() => ItemDataPresets.EncodeData(new Dictionary<string, string> { ["key"] = null! }));
        Reject(() => ItemDataPresets.EncodeData(new[] { new KeyValuePair<string, string>(null!, "value") }));
        Reject(() => ItemDataPresets.EncodeData(new[] { new KeyValuePair<string, string>("key", "1"), new KeyValuePair<string, string>("key", "2") }));
        Reject(() => ItemDataPresets.EncodeData(null!));
        Check(ItemDataPresets.DecodeData(ItemDataPresets.EncodeData(Enumerable.Range(0, 128)
            .Select(index => new KeyValuePair<string, string>("k" + index, "")))).Count == 128,
            "Exactly 128 custom-data keys are accepted");
        Reject(() => ItemDataPresets.EncodeData(Enumerable.Range(0, 129)
            .Select(index => new KeyValuePair<string, string>("k" + index, ""))));

        foreach (string invalid in new[]
        {
            "", "null", "[]", "[{}]", "\"text\"", "{\"k\":null}", "{\"k\":1}", "{\"k\":true}",
            "{\"k\":{}}", "{\"k\":[]}", "{\"k\":\"1\",\"k\":\"2\"}", "{\"k\":\"1\",\"\\u006b\":\"2\"}",
            "{}{}", "{} true", "{}\n---\n{}", "{k:\"v\"}", "{'k':'v'}", "{\"k\":\"v\",}",
            "{/*comment*/\"k\":\"v\"}", "{}//comment", "{\"k\":\"v\n\"}", "{\"k\":\"\\x41\"}",
            "{\"k\":\"\\uQQQQ\"}", "{\"k\":\"\\u123\"}", "{\"k\":\"\\uD800\"}", "{\"k\":\"\\uDC00\"}",
            "{\"k\":\"\\uD800x\"}", "{\"k\":\"\uD800\"}", "{\"k\":\"unterminated}",
            "{\"k\":\"" + new string('a', 4089) + "\"}", "{\"k\":\"" + new string('\uAC00', 2729) + "\"}",
            "{\"" + new string('k', 257) + "\":\"v\"}",
            "{" + string.Join(",", Enumerable.Range(0, 129).Select(index => "\"k" + index + "\":\"\"")) + "}"
        }) Reject(() => ItemDataPresets.DecodeData(invalid));
        Reject(() => ItemDataPresets.DecodeData(null!));
    }

    private static void Parser()
    {
        Check(!ItemDataPresets.Parse(ItemDataPresets.DefaultYaml).TryGetPayload("restoringnow", out _),
            "Generated sample comments leave zero enabled presets");
        ItemDataPresets casing = ItemDataPresets.Parse("- id: restoringnow\n  CustomData: {\"Key\": \"1\", \"key\": \"2\"}\n" +
            "- id: RestoringNow\n  CustomData: {}\n");
        Check(casing.TryGetPayload("restoringnow", out string first) && first != "{}" &&
            casing.TryGetPayload("RestoringNow", out string second) && second == "{}" &&
            !casing.TryGetPayload("RESTORINGNOW", out _) && !casing.TryGetPayload(null!, out _),
            "Preset IDs and custom-data keys compare ordinal/case-sensitive");
        Dictionary<string, string> mutable = ItemDataPresets.DecodeData(Payload(casing));
        mutable["Key"] = "changed";
        Check(ItemDataPresets.DecodeData(Payload(casing))["Key"] == "1", "Decoded dictionary cannot mutate snapshot payloads");
        Check(ItemDataPresets.Parse("- id: '" + new string('a', 64) + "'\n  CustomData: {}")
            .TryGetPayload(new string('a', 64), out _), "64-character IDs are accepted");
        Check(ItemDataPresets.IsValidId("Az_09-") && !ItemDataPresets.IsValidId(""), "Identifier alphabet is explicit");
        Check(ItemDataPresets.Parse(string.Join("\n", Enumerable.Range(0, 256)
            .Select(index => "- id: p" + index + "\n  CustomData: {}"))).TryGetPayload("p255", out _),
            "Exactly 256 presets are accepted");
        foreach (string invalid in new[]
        {
            "", "# comments only", "{}", "null", "[null]", "[{}]", "- id: restoringnow", "- CustomData: {}",
            "- Id: restoringnow\n  CustomData: {}", "- id: restoringnow\n  customData: {}",
            "- id: restoringnow\n  CustomData: {}\n  extra: true", "- id: restoringnow\n  id: other\n  CustomData: {}",
            "- id: restoringnow\n  CustomData: {}\n  CustomData: {}",
            "- id: restoringnow\n  CustomData: {}\n- id: restoringnow\n  CustomData: {}",
            "- id: null\n  CustomData: {}", "- id: ''\n  CustomData: {}", "- id: 'has space'\n  CustomData: {}",
            "- id: '../traverse'\n  CustomData: {}", "- id: '한글'\n  CustomData: {}",
            "- id: '" + new string('x', 65) + "'\n  CustomData: {}",
            "- id: restoringnow\n  CustomData: null", "- id: restoringnow\n  CustomData: []",
            "- id: restoringnow\n  CustomData: {k: null}", "- id: restoringnow\n  CustomData: {k: ~}",
            "- id: restoringnow\n  CustomData: {k: }", "- id: restoringnow\n  CustomData: {k: true}",
            "- id: restoringnow\n  CustomData: {k: 123}", "- id: restoringnow\n  CustomData: {k: 0x20}",
            "- id: restoringnow\n  CustomData: {k: .inf}", "- id: restoringnow\n  CustomData: {null: value}",
            "- id: restoringnow\n  CustomData: {k: value, k: value}",
            "- id: restoringnow\n  CustomData: {k: value, \"\\u006b\": value}",
            "- id: restoringnow\n  CustomData: {k: [value]}", "- id: restoringnow\n  CustomData: {k: {nested: value}}",
            "- id: restoringnow\n  CustomData: &data {k: value}", "- id: restoringnow\n  CustomData: *data",
            "- id: restoringnow\n  CustomData: {k: !!str value}", "[]\n---\n[]", "[]\n...\n---\n[]",
            new string('[', 8) + new string(']', 8), "#" + new string('a', ItemDataPresets.MaximumFileBytes),
            "#" + new string('\uAC00', 90000), "#\uD800",
            "- id: restoringnow\n  CustomData: {k: '" + new string('v', 4090) + "'}",
            "- id: restoringnow\n  CustomData: { '" + new string('k', 257) + "': value }",
            string.Join("\n", Enumerable.Range(0, 257).Select(index => "- id: p" + index + "\n  CustomData: {}")),
            "- id: restoringnow\n  CustomData: {" + string.Join(",", Enumerable.Range(0, 129)
                .Select(index => "k" + index + ": ''")) + "}"
        }) Reject(() => ItemDataPresets.Parse(invalid));
        Reject(() => ItemDataPresets.Parse(null!));
    }

    private static void Reload()
    {
        string root = NewRoot("reload");
        List<string> logs = new();
        void Log(string line)
        {
            Check(Thread.CurrentThread.ManagedThreadId == _mainThread, "Logging/publication stays on caller thread");
            logs.Add(line);
        }
        using ItemDataPresetStore store = new(Log, Log);
        Check(!store.EnsureLoaded(root) && store.Current == null, "Initial optional preset load is asynchronous");
        Finish(store, root);
        string path = Path.Combine(root, ItemDataPresets.FileName);
        Check(store.Current != null && File.ReadAllText(path, Utf8) == ItemDataPresets.DefaultYaml,
            "Missing initial file is created once with commented example and empty list");
        for (int index = 0; index < 100; ++index) store.Tick(root);
        Check(PendingRead.GetValue(store) == null, "Ordinary frames do not read disk before poll interval");
        string valid = "# preserve my comments\n- id: restoringnow\n  CustomData: {\"key\":\"v1\"}\n";
        File.WriteAllText(path, valid, Utf8);
        Poll(store, root);
        Check(!store.Current!.TryGetPayload("restoringnow", out _), "One changed sample does not replace the snapshot");
        Poll(store, root);
        Check(ItemDataPresets.DecodeData(Payload(store.Current!))["key"] == "v1" &&
            File.ReadAllText(path, Utf8) == valid, "Two matching samples atomically publish without rewriting comments");
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, valid.Replace("v1", "v2"), Utf8);
        File.SetLastWriteTimeUtc(path, timestamp);
        Poll(store, root); Poll(store, root);
        Check(ItemDataPresets.DecodeData(Payload(store.Current!))["key"] == "v2", "Same-length and same-timestamp changes are detected");
        foreach (string malformed in new[] { "- id: restoringnow\n  CustomData: {key: null}", "[UNTRUSTED_SECRET]" })
        {
            ItemDataPresets previous = store.Current!;
            File.WriteAllText(path, malformed, Utf8);
            Poll(store, root); Poll(store, root);
            int warnings = logs.Count;
            Poll(store, root); Poll(store, root);
            Check(ReferenceEquals(previous, store.Current) && logs.Count == warnings && File.ReadAllText(path, Utf8) == malformed,
                "Malformed replacements preserve entire last-valid snapshot, preserve file and warn once");
        }
        File.Delete(path);
        Poll(store, root);
        int missingWarnings = logs.Count;
        Poll(store, root);
        Check(!File.Exists(path) && logs.Count == missingWarnings && ItemDataPresets.DecodeData(Payload(store.Current!))["key"] == "v2",
            "Deletion retains last-good presets, never recreates and does not spam warnings");
        File.WriteAllBytes(path, new byte[] { 0xff, 0xfe, 0x61, 0 });
        Poll(store, root);
        Check(ItemDataPresets.DecodeData(Payload(store.Current!))["key"] == "v2", "Invalid UTF-8 cannot alter active presets");
        File.WriteAllText(path, "[]", Utf8);
        Poll(store, root); Poll(store, root);
        Check(!store.Current!.TryGetPayload("restoringnow", out _) && !string.Join(" ", logs).Contains("UNTRUSTED_SECRET"),
            "Valid empty restoration recovers; logs never echo parser-controlled tokens");
    }

    private static void StartupAndLifecycle()
    {
        string root = NewRoot("startup"), path = Path.Combine(root, ItemDataPresets.FileName);
        File.WriteAllText(path, "malformed", Utf8);
        using ItemDataPresetStore store = new(_ => { }, _ => { });
        store.EnsureLoaded(root); Finish(store, root);
        Check(store.Current == null && File.ReadAllText(path, Utf8) == "malformed", "Invalid existing startup file is never overwritten");
        File.WriteAllText(path, "- id: restoringnow\n  CustomData: {}", Utf8);
        Poll(store, root); Poll(store, root);
        Check(Payload(store.Current!) == "{}", "Valid correction after invalid startup recovers");
        NextPoll.SetValue(store, 0L); store.Tick(root);
        Task? stale = PendingRead.GetValue(store) as Task;
        store.Reset();
        Check(store.Current == null, "Reset immediately clears old-world snapshot");
        string replacement = NewRoot("replacement");
        store.EnsureLoaded(replacement); Finish(store, replacement);
        Check(stale != null && stale.Wait(5000) && !store.Current!.TryGetPayload("restoringnow", out _),
            "Old-generation worker cannot overwrite the replacement world's presets");
        store.Dispose();
        string absent = Path.Combine(_root, "disposed");
        store.Tick(absent);
        Check(!store.EnsureLoaded(absent) && store.Current == null && !Directory.Exists(absent),
            "Disposed store performs no creation/read/publication");

        string readOnlyRoot = NewRoot("readonly"), readOnlyPath = Path.Combine(readOnlyRoot, ItemDataPresets.FileName);
        const string readOnlyText = "# existing read-only file\n- id: restoringnow\n  CustomData: {}";
        File.WriteAllText(readOnlyPath, readOnlyText, Utf8);
        File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
        try
        {
            using ItemDataPresetStore readOnly = new(_ => { }, _ => { });
            readOnly.EnsureLoaded(readOnlyRoot); Finish(readOnly, readOnlyRoot);
            Check(Payload(readOnly.Current!) == "{}" && File.ReadAllText(readOnlyPath, Utf8) == readOnlyText,
                "Existing read-only file loads without a write attempt");
        }
        finally { File.SetAttributes(readOnlyPath, FileAttributes.Normal); }
    }

    private static string Payload(ItemDataPresets presets)
    {
        Check(presets.TryGetPayload("restoringnow", out string payload), "Expected preset is present");
        return payload;
    }
    private static void Equal(Dictionary<string, string> expected, Dictionary<string, string> actual, string message) =>
        Check(expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out string value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal)), message);
    private static void Poll(ItemDataPresetStore store, string root)
    { NextPoll.SetValue(store, 0L); store.Tick(root); Finish(store, root); }
    private static void Finish(ItemDataPresetStore store, string root)
    {
        Task? task = PendingRead.GetValue(store) as Task;
        Check(task != null && task.Wait(5000), "Bounded background read/parse completes");
        NextPoll.SetValue(store, long.MaxValue);
        store.Tick(root);
    }
    private static string NewRoot(string name)
    { string path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid schema/type/duplicate/limit is rejected in full");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); ++_checks; }
}
