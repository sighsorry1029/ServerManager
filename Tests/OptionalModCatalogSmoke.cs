using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Mono.Cecil;
using ServerManager;

// Source-linked production codec/scanner/store; no Unity, Steam, or live files.
internal static class OptionalModCatalogSmoke
{
    private static int _checks;
    private static readonly string Hash = new string('a', 64);

    private static int Main(string[] args)
    {
        try
        {
            Codec();
            Scanner(args[0]);
            Console.WriteLine("Optional catalog smoke passed (" + _checks + " assertions; production sources, isolated DLL fixtures, no Unity/network).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static IntegrityPolicyRule Rule(string guid, string name, params string[] versions) =>
        new IntegrityPolicyRule(guid, name, IntegrityRequirement.Optional, new[] { Hash }, versions);

    private static IntegrityPolicySnapshot Snapshot(params IntegrityPolicyRule[] rules) => new IntegrityPolicySnapshot(1, rules);
    private static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string> rules) =>
        rules.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static string Stable(IReadOnlyDictionary<string, string> rules) =>
        string.Join("\n", rules.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));

    private static void Assert(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static OptionalModCatalogResult Status(IReadOnlyDictionary<string, string> rules, OptionalModCatalogStatus status, string message)
    {
        OptionalModCatalogResult result = OptionalModCatalog.Decode(rules);
        Assert(result.Status == status, message + " (actual " + result.Status + ")");
        Assert(status == OptionalModCatalogStatus.Available || result.Entries.Count == 0, message + ": partial entries leaked");
        return result;
    }

    private static void Codec()
    {
        var optional = Rule("private.guid", "한글 Mod 🚀", "2.0.0", "1.0.0", "2.0.0");
        var second = Rule("second.guid", "Alpha", "5.0");
        var required = new IntegrityPolicyRule("required.guid", "RequiredSecret", IntegrityRequirement.Required, new[] { Hash });
        var library = new IntegrityPolicyRule("assembly:secret", "LibrarySecret", IntegrityRequirement.Optional, new[] { Hash });
        var rules = OptionalModCatalog.Encode(Snapshot(optional, second, required, library));
        var result = Status(rules, OptionalModCatalogStatus.Available, "Valid catalog unavailable");
        Assert(result.Entries.Count == 2 && result.Entries[0].Name == "Alpha" && result.Entries[1].Name == "한글 Mod 🚀", "Optional plugin filtering/order failed");
        Assert(result.Entries[0].PluginGuid == "second.guid" && result.Entries[1].PluginGuid == "private.guid", "Stable GUIDs did not round-trip");
        Assert(rules[OptionalModCatalog.HeaderKey].StartsWith("2|available|", StringComparison.Ordinal) &&
            OptionalModCatalog.UnavailableHeaderValue == "2|missing|0|0|", "Preview schema was not upgraded to version two");
        Assert(result.Entries[1].Versions.SequenceEqual(new[] { "1.0.0", "2.0.0" }), "Versions not sorted distinct");
        Assert(optional.AllowedSha256.SequenceEqual(new[] { Hash }), "Metadata changed allowed hashes");
        Assert(Stable(rules) == Stable(OptionalModCatalog.Encode(Snapshot(library, second, optional, required))), "Output depends on input order");
        Assert(rules.All(pair => pair.Key.StartsWith(OptionalModCatalog.KeyPrefix, StringComparison.Ordinal) && pair.Value.Length <= 96 && pair.Value.All(c => c <= 127)), "Rules not short ASCII values");
        Assert(rules.Sum(pair => pair.Key.Length + pair.Value.Length) <= 16384, "Catalog exceeds aggregate limit");
        string privateCheck = Encoding.UTF8.GetString(Payload(rules));
        foreach (string excluded in new[] { "required.guid", Hash, "RequiredSecret", "LibrarySecret", "assembly:" })
            Assert(!privateCheck.Contains(excluded), "Catalog exposed non-display policy data: " + excluded);
        var sameName = Snapshot(Rule("z.plugin", "Same name", "1"), Rule("a.plugin", "Same name", "1"));
        var sameNameResult = Status(OptionalModCatalog.Encode(sameName), OptionalModCatalogStatus.Available, "Distinct same-name plugins were rejected");
        Assert(sameNameResult.Entries.Select(entry => entry.PluginGuid).SequenceEqual(new[] { "a.plugin", "z.plugin" }),
            "Same-name plugins were combined or lost stable identity ordering");
        Assert(Stable(OptionalModCatalog.Encode(sameName)) == Stable(OptionalModCatalog.Encode(Snapshot(sameName.Rules.Reverse().ToArray()))),
            "Same-name identity ordering depends on input order");
        bool immutable = false;
        try { ((IList<string>)result.Entries[1].Versions).Add("3"); } catch (NotSupportedException) { immutable = true; }
        Assert(immutable, "Decoded version list is mutable");
        Status(OptionalModCatalog.Encode(Snapshot(required, library)), OptionalModCatalogStatus.Available, "Empty optional list is unavailable");
        Assert(OptionalModCatalog.Decode(OptionalModCatalog.Encode(Snapshot(required))).Entries.Count == 0, "Required-only catalog not empty");
        Status(new Dictionary<string, string> { ["other"] = new string('x', 20000) }, OptionalModCatalogStatus.Missing, "Unrelated rules affect catalog");
        Status(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [OptionalModCatalog.HeaderKey.ToUpperInvariant()] = new string('x', 20000)
        }, OptionalModCatalogStatus.Missing, "Caller key comparer bypassed namespace bounds");
        Status(OptionalModCatalog.Encode(null!), OptionalModCatalogStatus.Missing, "Missing snapshot is not explicit");
        Status(OptionalModCatalog.Encode(Snapshot(Rule("missing", "MissingVersion"))), OptionalModCatalogStatus.Invalid, "Missing display version accepted");
        Status(OptionalModCatalog.Encode(Snapshot(Rule("bad", "<b>Fake</b>", "1"))), OptionalModCatalogStatus.Invalid, "Rich text accepted");
        foreach (string guid in new[] { "", "Bad.Case", " leading", "trailing ", "bad guid", "bad/guid", "bad:guid", "bad\nline", "bad\u202eguid", "bad\ud800guid" })
        {
            Status(OptionalModCatalog.Encode(Snapshot(Rule(guid, "Name", "1"))), OptionalModCatalogStatus.Invalid, "Invalid/noncanonical local GUID accepted");
            Status(Wrap(RawPayloadWithGuid(guid, "Name", "1"), 1), OptionalModCatalogStatus.Invalid, "Invalid/noncanonical remote GUID accepted");
        }
        Status(Wrap(RawPayloadWithGuid("assembly:dependency", "Name", "1"), 1), OptionalModCatalogStatus.Invalid, "Library identity entered plugin preview");
        Status(OptionalModCatalog.Encode(Snapshot(Rule(new string('a', 257), "Name", "1"))), OptionalModCatalogStatus.TooLarge, "Oversized local GUID accepted");
        Status(Wrap(RawPayloadWithGuid(new string('a', 257), "Name", "1"), 1), OptionalModCatalogStatus.TooLarge, "Oversized remote GUID accepted");
        Assert(Status(OptionalModCatalog.Encode(Snapshot(Rule(new string('a', 256), "Name", "1"))), OptionalModCatalogStatus.Available,
            "Maximum canonical GUID was rejected").Entries[0].PluginGuid.Length == 256, "Maximum GUID was truncated");
        Status(Wrap(CombinePayloads(RawPayloadWithGuid("duplicate.guid", "Alpha", "1"), RawPayloadWithGuid("duplicate.guid", "Zulu", "2")), 2),
            OptionalModCatalogStatus.Invalid, "Duplicate GUID with different metadata accepted");
        Status(Wrap(CombinePayloads(RawPayloadWithGuid("a.guid", "Same", "1"), RawPayloadWithGuid("A.Guid", "Same", "1")), 2),
            OptionalModCatalogStatus.Invalid, "Case-variant duplicate GUID accepted");
        Status(Wrap(CombinePayloads(RawPayloadWithGuid("z.guid", "Same", "1"), RawPayloadWithGuid("a.guid", "Same", "1")), 2),
            OptionalModCatalogStatus.Invalid, "Noncanonical same-name GUID ordering accepted");
        foreach (string value in new[] { "bad\ntext", "bad\rtext", "bad\ttext", "bad\0text", "bad\u202etext", "bad\u2066text", "bad\u200btext", "bad\u2028text", "bad\ud800text", " leading", "trailing " })
        {
            Status(OptionalModCatalog.Encode(Snapshot(Rule("bad", "Name", value))), OptionalModCatalogStatus.Invalid, "Unsafe local version accepted");
            Status(Wrap(RawPayload("Name", value), 1), OptionalModCatalogStatus.Invalid, "Unsafe remote version accepted");
        }
        Status(OptionalModCatalog.Encode(Snapshot(Rule("big", new string('n', 257), "1"))), OptionalModCatalogStatus.TooLarge, "Oversized name not explicit");
        Status(OptionalModCatalog.Encode(Snapshot(Rule("big", "Name", new string('v', 65)))), OptionalModCatalogStatus.TooLarge, "Oversized version not explicit");
        Status(OptionalModCatalog.Encode(Snapshot(Rule("big", "Name", Enumerable.Range(0, 33).Select(i => i.ToString("D2")).ToArray()))), OptionalModCatalogStatus.TooLarge, "Too many versions not explicit");
        var maxRules = Enumerable.Range(0, 128).Select(i => Rule("guid" + i, "Mod" + i.ToString("D3"), "1")).ToArray();
        var maximum = OptionalModCatalog.Encode(Snapshot(maxRules));
        Assert(Status(maximum, OptionalModCatalogStatus.Available, "128 short entries rejected").Entries.Count == 128, "128 entries silently truncated");
        Status(OptionalModCatalog.Encode(Snapshot(maxRules.Concat(new[] { Rule("extra", "Extra", "1") }).ToArray())), OptionalModCatalogStatus.TooLarge, "129 entries not explicit");
        var largeRules = Enumerable.Range(0, 128).Select(i => Rule("guid" + i, new string('n', 200) + i.ToString("D3"), "1")).ToArray();
        var oversized = OptionalModCatalog.Encode(Snapshot(largeRules));
        Status(oversized, OptionalModCatalogStatus.TooLarge, "Aggregate oversize not explicit");
        Assert(oversized.Count == 1, "Oversize encoded a partial list");

        Dictionary<string, string> modified = Copy(rules);
        modified.Remove(OptionalModCatalog.KeyPrefix + "000");
        Status(modified, OptionalModCatalogStatus.Invalid, "Missing chunk accepted");
        modified = Copy(rules);
        modified[OptionalModCatalog.HeaderKey] = modified[OptionalModCatalog.HeaderKey].Replace("2|available", "3|available");
        Status(modified, OptionalModCatalogStatus.Invalid, "Unknown schema accepted");
        modified = Copy(rules);
        modified[OptionalModCatalog.HeaderKey] = modified[OptionalModCatalog.HeaderKey].Replace("2|available", "1|available");
        Status(modified, OptionalModCatalogStatus.Invalid, "Legacy identity-free preview schema accepted");
        Status(new Dictionary<string, string> { [OptionalModCatalog.HeaderKey] = "1|missing|0|0|" }, OptionalModCatalogStatus.Invalid,
            "Legacy unavailable header bypassed schema check");
        modified = Copy(rules);
        modified[OptionalModCatalog.HeaderKey] = "2|available|129|1|" + new string('a', 64);
        Status(modified, OptionalModCatalogStatus.TooLarge, "Remote oversized count accepted");
        modified[OptionalModCatalog.HeaderKey] = "2|available|1|147|" + new string('a', 64);
        Status(modified, OptionalModCatalogStatus.TooLarge, "Remote oversized chunk count accepted");
        modified[OptionalModCatalog.HeaderKey] = "2|available|01|1|" + new string('a', 64);
        Status(modified, OptionalModCatalogStatus.Invalid, "Noncanonical count accepted");
        modified = Copy(rules);
        modified[OptionalModCatalog.HeaderKey] = modified[OptionalModCatalog.HeaderKey].Substring(0, modified[OptionalModCatalog.HeaderKey].Length - 1) + "z";
        Status(modified, OptionalModCatalogStatus.Invalid, "Invalid digest accepted");
        modified = Copy(rules);
        string originalChunk = modified[OptionalModCatalog.KeyPrefix + "000"];
        modified[OptionalModCatalog.KeyPrefix + "000"] = (originalChunk[0] == 'A' ? "B" : "A") + originalChunk.Substring(1);
        Status(modified, OptionalModCatalogStatus.Invalid, "Mixed/corrupt data accepted");
        modified = Copy(rules);
        modified[OptionalModCatalog.KeyPrefix + "000"] = new string('a', 97);
        Status(modified, OptionalModCatalogStatus.TooLarge, "Oversized rule value accepted");
        modified = Copy(rules);
        modified[OptionalModCatalog.KeyPrefix + "145"] = "";
        Status(modified, OptionalModCatalogStatus.Available, "Blank stale chunk broke shrinkage");
        modified[OptionalModCatalog.KeyPrefix + "145"] = "obsolete";
        Status(modified, OptionalModCatalogStatus.Available, "Unused old chunk broke digest framing");
        modified[OptionalModCatalog.HeaderKey] = OptionalModCatalog.UnavailableHeaderValue;
        Status(modified, OptionalModCatalogStatus.Missing, "Publication staging header ignored");
        modified = Copy(rules);
        modified[OptionalModCatalog.KeyPrefix + "unknown"] = "";
        Status(modified, OptionalModCatalogStatus.TooLarge, "Unbounded own key accepted");
        Status(Wrap(RawPayload("Name", "2", "1"), 1), OptionalModCatalogStatus.Invalid, "Unsorted remote versions accepted");
        Status(Wrap(RawPayload("Name", "1", "1"), 1), OptionalModCatalogStatus.Invalid, "Duplicate remote versions accepted");
        Status(Wrap(RawPayload("Name"), 1), OptionalModCatalogStatus.Invalid, "Remote missing versions accepted");
        Status(Wrap(RawPayload("Name", "1"), 2), OptionalModCatalogStatus.Invalid, "Count mismatch accepted");
        Status(Wrap(RawPayload("Name", "1").Concat(new byte[] { 0 }).ToArray(), 1), OptionalModCatalogStatus.Invalid, "Trailing data accepted");
        Status(Wrap(new byte[] { 1, 0, 5, 0, 65 }, 1), OptionalModCatalogStatus.Invalid, "Truncated text accepted");
        Status(Wrap(new byte[] { 1, 0, 1, 0, 0xff, 1, 1, 0, 49 }, 1), OptionalModCatalogStatus.Invalid, "Invalid UTF8 accepted");
        modified = Wrap(new byte[] { 0, 0 }, 0);
        modified[OptionalModCatalog.KeyPrefix + "000"] = "AAB=";
        Status(modified, OptionalModCatalogStatus.Invalid, "Noncanonical Base64 padding accepted");
        var random = new Random(1938);
        for (int index = 0; index < 500; index++)
        {
            byte[] fuzz = new byte[random.Next(0, 300)];
            random.NextBytes(fuzz);
            OptionalModCatalogResult fuzzResult = OptionalModCatalog.Decode(Wrap(fuzz, random.Next(0, 129)));
            Assert(fuzzResult.Status != OptionalModCatalogStatus.Available && fuzzResult.Entries.Count == 0, "Random malformed framing accepted");
        }
    }

    private static void Scanner(string root)
    {
        Directory.CreateDirectory(root);
        var store = new IntegrityPolicyStore(root);
        Fixture(Path.Combine(root, "optional", "z-two.dll"), "OptionalTwo", "MoD.Example", "Optional", "2.0.0");
        Fixture(Path.Combine(root, "optional", "a-one.dll"), "OptionalOne", "mod.example", "Optional", "1.0.0");
        Fixture(Path.Combine(root, "optional", "duplicate-one.dll"), "OptionalDuplicate", "mod.example", "Optional", "1.0.0");
        Fixture(Path.Combine(root, "required", "required.dll"), "Required", "mod.required", "Required", "9.0.0");
        Fixture(Path.Combine(root, "optional", "library.dll"), "Dependency", null, null, null);
        Assert(store.TryReload().Success, "Valid reference fixtures did not load");
        IntegrityPolicySnapshot original = store.Current!;
        IntegrityPolicyRule rule = original.Rules.Single(item => item.PluginGuid == "mod.example");
        Assert(rule.AllowedVersions.SequenceEqual(new[] { "1.0.0", "2.0.0" }), "Scanner/store discarded or duplicated version metadata");
        Assert(rule.AllowedSha256.Count == 3, "Version deduplication altered allowed content hashes");
        Assert(original.Rules.Single(item => item.PluginGuid == "mod.required").AllowedVersions.SequenceEqual(new[] { "9.0.0" }), "Required reference metadata absent");
        var preview = Status(OptionalModCatalog.Encode(original), OptionalModCatalogStatus.Available, "Scanned metadata catalog unavailable");
        Assert(preview.Entries.Count == 1 && preview.Entries[0].PluginGuid == "mod.example" &&
            preview.Entries[0].Versions.SequenceEqual(new[] { "1.0.0", "2.0.0" }), "Catalog did not follow canonical plugin-only policy identity/metadata");
        string previous = Stable(OptionalModCatalog.Encode(original));
        Fixture(Path.Combine(root, "optional", "three.dll"), "OptionalThree", "mod.example", "Optional", "3.0.0");
        string broken = Path.Combine(root, "optional", "broken.dll");
        File.WriteAllBytes(broken, new byte[] { 1, 2, 3 });
        IntegrityPolicyReloadResult failed = store.TryReload();
        Assert(!failed.Success && failed.KeptPreviousSnapshot && ReferenceEquals(store.Current, original), "Failed reload replaced last-good snapshot");
        Assert(Stable(OptionalModCatalog.Encode(store.Current!)) == previous, "Failed reload changed preview metadata");
        File.Delete(broken);
        Assert(store.TryReload().Success && store.Current!.Generation == original.Generation + 1, "Recovery reload not published");
        Assert(store.Current!.Rules.Single(item => item.PluginGuid == "mod.example").AllowedVersions.SequenceEqual(new[] { "1.0.0", "2.0.0", "3.0.0" }), "Recovery reload metadata stale");
        Fixture(Path.Combine(root, "optional", "invalid-display.dll"), "InvalidDisplay", "mod.invalid", "Invalid display", "bad\u202eversion");
        Assert(store.TryReload().Success, "Preview-only version validation changed admission policy loading");
        Status(OptionalModCatalog.Encode(store.Current!), OptionalModCatalogStatus.Invalid, "Unsafe scan metadata reached preview");
    }

    private static void Fixture(string path, string assemblyName, string? guid, string? name, string? version)
    {
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(assemblyName, new Version(1, 0)), assemblyName + ".dll", ModuleKind.Dll))
        {
            if (guid != null)
            {
                ModuleDefinition module = assembly.MainModule;
                var type = new TypeDefinition("Fixtures", "Plugin", TypeAttributes.Public, module.TypeSystem.Object);
                module.Types.Add(type);
                var attribute = new CustomAttribute(module.ImportReference(typeof(BepInPlugin).GetConstructor(new[] { typeof(string), typeof(string), typeof(string) })));
                foreach (string? value in new[] { guid, name, version })
                    attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, value));
                type.CustomAttributes.Add(attribute);
            }
            assembly.Write(path);
        }
    }

    private static byte[] Payload(IReadOnlyDictionary<string, string> rules) => Convert.FromBase64String(string.Concat(rules
        .Where(pair => pair.Key != OptionalModCatalog.HeaderKey).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value)));

    private static byte[] RawPayload(string name, params string[] versions) => RawPayloadWithGuid("mod.fixture", name, versions);

    private static byte[] RawPayloadWithGuid(string pluginGuid, string name, params string[] versions)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write((ushort)1);
            RawText(writer, pluginGuid);
            RawText(writer, name);
            writer.Write((byte)versions.Length);
            foreach (string version in versions) RawText(writer, version);
            return stream.ToArray();
        }
    }

    private static byte[] CombinePayloads(params byte[][] payloads)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write((ushort)payloads.Length);
            foreach (byte[] payload in payloads) writer.Write(payload, 2, payload.Length - 2);
            return stream.ToArray();
        }
    }

    private static void RawText(BinaryWriter writer, string value)
    {
        // Deliberately supports invalid metadata fixtures; an unpaired surrogate
        // is emitted as invalid UTF8, not replacement text that becomes valid.
        byte[] bytes = value.Contains("\ud800") ? new byte[] { 0xed, 0xa0, 0x80 } : Encoding.UTF8.GetBytes(value);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static Dictionary<string, string> Wrap(byte[] payload, int count)
    {
        string encoded = Convert.ToBase64String(payload);
        int chunks = (encoded.Length + 95) / 96;
        string digest;
        using (SHA256 sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
        var rules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OptionalModCatalog.HeaderKey] = "2|available|" + count + "|" + chunks + "|" + digest
        };
        for (int index = 0; index < chunks; index++)
            rules[OptionalModCatalog.KeyPrefix + index.ToString("D3")] = encoded.Substring(index * 96, Math.Min(96, encoded.Length - index * 96));
        return rules;
    }
}
