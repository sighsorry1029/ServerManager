using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ServerManager;

// The server-only configuration is a single immutable, validated snapshot.
// No cfg import, game-object access, runtime type deserialization or partial apply.
internal sealed class ServerSettings
{
    internal const int MaximumFileBytes = 128 * 1024;
    internal const int MaximumCollectionEntries = 256;
    internal const int MaximumStartItemAmount = 1000000;
    internal const string FileName = "ServerManager.yml";

    public int MaxPlayers { get; }
    public int MaxCharactersPerAccount { get; }
    public int BackupsPerProfile { get; }
    public bool LoadServerCharacterOnJoin { get; }
    public string ForbiddenItemPrefabs { get; }
    public DetectionAction CheatDetectionResponse { get; }
    public DetectionAction StatLimitResponse { get; }
    public float MaximumHealth { get; }
    public float MaximumStamina { get; }
    public float MaximumEitr { get; }
    public float MaximumCarryWeight { get; }
    public float MaximumDamage { get; }
    public IReadOnlyDictionary<string, int> StartItems { get; }

    public static ServerSettings Defaults { get; } = new(24, 3, 30, true, string.Empty,
        DetectionAction.Kick, DetectionAction.Log, 800f, 800f, 500f, 2000f, 50000f,
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["HelmetMidsummerCrown"] = 1,
            ["ArmorRagsChest"] = 1,
            ["ArmorRagsLegs"] = 1,
            ["Torch"] = 1
        });

    private ServerSettings(int players, int characters, int backups, bool loadServerCharacterOnJoin, string forbidden,
        DetectionAction cheatAction, DetectionAction statAction, float health,
        float stamina, float eitr, float weight, float damage, Dictionary<string, int> startItems)
    {
        MaxPlayers = players;
        MaxCharactersPerAccount = characters;
        BackupsPerProfile = backups;
        LoadServerCharacterOnJoin = loadServerCharacterOnJoin;
        ForbiddenItemPrefabs = forbidden;
        CheatDetectionResponse = cheatAction;
        StatLimitResponse = statAction;
        MaximumHealth = health;
        MaximumStamina = stamina;
        MaximumEitr = eitr;
        MaximumCarryWeight = weight;
        MaximumDamage = damage;
        StartItems = new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(startItems, StringComparer.Ordinal));
    }

    public static ServerSettings Parse(string yaml)
    {
        if (yaml == null) throw Invalid("YAML text is missing");
        if (yaml.Length > MaximumFileBytes) throw Invalid("file exceeds 128 KiB");
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(yaml) > MaximumFileBytes)
                throw Invalid("file exceeds 128 KiB");
        }
        catch (EncoderFallbackException) { throw Invalid("file must be valid UTF-8"); }

        YamlStream stream = new();
        try
        {
            using StringReader reader = new(yaml);
            stream.Load(new BoundedParser(reader));
        }
        catch (YamlException exception)
        {
            // Never echo the parser's raw tokens or exception messages into logs.
            throw Invalid("invalid YAML syntax or duplicate key at line " +
                exception.Start.Line.ToString(CultureInfo.InvariantCulture) + ", column " +
                exception.Start.Column.ToString(CultureInfo.InvariantCulture));
        }
        catch (ArgumentException) { throw Invalid("invalid YAML structure or duplicate key"); }
        if (stream.Documents.Count != 1) throw Invalid("exactly one YAML document is required");
        Dictionary<string, YamlNode> root = Map(stream.Documents[0].RootNode, "root",
            "serverSettings", "forbiddenItems", "cheatDetection", "statCaps", "startItems");
        Dictionary<string, YamlNode> server = Section(root, "serverSettings",
            "maxPlayers", "maxCharactersPerAccount", "backupsPerProfile", "loadServerCharacterOnJoin");
        Dictionary<string, YamlNode> cheat = Section(root, "cheatDetection", "action");
        Dictionary<string, YamlNode> caps = Section(root, "statCaps",
            "action", "health", "stamina", "eitr", "weight", "damage");
        return new ServerSettings(
            Integer(server, "maxPlayers", Defaults.MaxPlayers, 64),
            Integer(server, "maxCharactersPerAccount", Defaults.MaxCharactersPerAccount, 128),
            Integer(server, "backupsPerProfile", Defaults.BackupsPerProfile, 50),
            Boolean(server, "loadServerCharacterOnJoin", Defaults.LoadServerCharacterOnJoin),
            ForbiddenItems(root),
            Action(cheat, Defaults.CheatDetectionResponse, "cheatDetection"),
            Action(caps, Defaults.StatLimitResponse, "statCaps"),
            Limit(caps, "health", Defaults.MaximumHealth, 1000000f),
            Limit(caps, "stamina", Defaults.MaximumStamina, 1000000f),
            Limit(caps, "eitr", Defaults.MaximumEitr, 1000000f),
            Limit(caps, "weight", Defaults.MaximumCarryWeight, 1000000f),
            Limit(caps, "damage", Defaults.MaximumDamage, 1000000000f),
            ReadStartItems(root));
    }

    private static Dictionary<string, YamlNode> Section(Dictionary<string, YamlNode> parent,
        string key, params string[] allowed) => parent.TryGetValue(key, out YamlNode node)
        ? Map(node, key, allowed) : new Dictionary<string, YamlNode>(StringComparer.Ordinal);

    private static Dictionary<string, YamlNode> Map(YamlNode node, string location, params string[] allowed)
    {
        if (node is not YamlMappingNode mapping) throw Invalid(location + " must be a mapping");
        if (mapping.Children.Count > MaximumCollectionEntries)
            throw Invalid(location + " exceeds 256 entries");
        Dictionary<string, YamlNode> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode scalar || scalar.Value == null ||
                !allowed.Contains(scalar.Value) || result.ContainsKey(scalar.Value))
                throw Invalid(location + " contains an unknown or duplicate key");
            result.Add(scalar.Value, pair.Value);
        }
        return result;
    }

    private static int Integer(Dictionary<string, YamlNode> map, string key, int fallback, int maximum)
    {
        if (!map.TryGetValue(key, out YamlNode node)) return fallback;
        return PositiveInteger(node, key, maximum);
    }

    private static int PositiveInteger(YamlNode node, string location, int maximum)
    {
        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain ||
            !int.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
            number < 1 || number > maximum)
            throw Invalid(location + " must be an integer from 1 to " + maximum.ToString(CultureInfo.InvariantCulture));
        return number;
    }

    private static bool Boolean(Dictionary<string, YamlNode> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out YamlNode node)) return fallback;
        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain ||
            scalar.Value != "true" && scalar.Value != "false")
            throw Invalid("serverSettings." + key + " must be true or false (lower-case, unquoted)");
        return scalar.Value == "true";
    }

    private static float Limit(Dictionary<string, YamlNode> map, string key, float fallback, float maximum)
    {
        if (!map.TryGetValue(key, out YamlNode node)) return fallback;
        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain ||
            !double.TryParse(scalar.Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out double number) || double.IsNaN(number) ||
            double.IsInfinity(number) || number < 1d || number > maximum)
            throw Invalid("statCaps." + key + " must be a finite number from 1 to " +
                maximum.ToString(CultureInfo.InvariantCulture));
        return (float)number;
    }

    private static DetectionAction Action(Dictionary<string, YamlNode> map, DetectionAction fallback, string location)
    {
        if (!map.TryGetValue("action", out YamlNode node)) return fallback;
        string value = StringValue(node, location + ".action");
        return value switch
        {
            "log" => DetectionAction.Log,
            "kick" => DetectionAction.Kick,
            "ban" => DetectionAction.Ban,
            _ => throw Invalid(location + ".action must be log, kick or ban (lower-case)")
        };
    }

    private static string StringValue(YamlNode node, string location)
    {
        if (node is not YamlScalarNode scalar || string.IsNullOrEmpty(scalar.Value) ||
            (scalar.Style == ScalarStyle.Plain &&
                (scalar.Value == "~" || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(scalar.Value, "true", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(scalar.Value, "false", StringComparison.OrdinalIgnoreCase) ||
                 double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))))
            throw Invalid(location + " must be a nonempty string");
        return scalar.Value!;
    }

    private static string Prefab(YamlNode node, string location) =>
        PrefabText(StringValue(node, location), location);

    private static string PrefabText(string value, string location)
    {
        if (value.Length == 0 || value.Length > 128 || value != value.Trim() || value.Any(char.IsControl) ||
            value.IndexOfAny(new[] { ',', ';' }) >= 0)
            throw Invalid(location + " must be a prefab name of at most 128 characters without controls, commas or semicolons");
        return value;
    }

    private static string ForbiddenItems(Dictionary<string, YamlNode> root)
    {
        if (!root.TryGetValue("forbiddenItems", out YamlNode node)) return string.Empty;
        if (node is not YamlSequenceNode sequence || sequence.Children.Count > MaximumCollectionEntries)
            throw Invalid("forbiddenItems must be a list of at most 256 prefab names");
        SortedSet<string> values = new(StringComparer.Ordinal);
        foreach (YamlNode item in sequence.Children)
            if (!values.Add(Prefab(item, "forbiddenItems entry")))
                throw Invalid("forbiddenItems contains a duplicate prefab");
        return string.Join("\n", values);
    }

    private static Dictionary<string, int> ReadStartItems(Dictionary<string, YamlNode> root)
    {
        Dictionary<string, int> values = new(StringComparer.Ordinal);
        if (!root.TryGetValue("startItems", out YamlNode node))
        {
            foreach (KeyValuePair<string, int> item in Defaults.StartItems)
                values.Add(item.Key, item.Value);
            return values;
        }
        if (node is not YamlSequenceNode sequence || sequence.Children.Count > MaximumCollectionEntries)
            throw Invalid("startItems must be a list of at most 256 prefab names with optional comma-separated amounts");
        foreach (YamlNode item in sequence.Children)
        {
            string entry = StringValue(item, "startItems entry");
            int comma = entry.IndexOf(',');
            if (entry.Any(value => char.IsControl(value) && value != '\t') || comma != entry.LastIndexOf(','))
                throw Invalid("startItems entries must be single-line strings containing at most one comma");
            string prefab = PrefabText((comma < 0 ? entry : entry.Substring(0, comma)).Trim(), "startItems prefab");
            int amount = 1;
            if (comma >= 0 &&
                (!int.TryParse(entry.Substring(comma + 1).Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out amount) || amount < 1 || amount > MaximumStartItemAmount))
                throw Invalid("startItems amount must be an integer from 1 to 1000000 after the comma");
            if (values.ContainsKey(prefab)) throw Invalid("startItems contains a duplicate prefab");
            values.Add(prefab, amount);
        }
        return values;
    }

    internal static string ReadFile(string dataRoot, bool createIfMissing)
    {
        string path = Path.Combine(dataRoot, FileName);
        if (createIfMissing && !File.Exists(path))
        {
            Directory.CreateDirectory(dataRoot);
            FileStream? created = null;
            try { created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { }
            if (created != null)
            {
                using (created)
                {
                    byte[] example = new UTF8Encoding(false).GetBytes(DefaultYaml);
                    created.Write(example, 0, example.Length);
                }
            }
        }
        byte[] bytes = new byte[MaximumFileBytes + 1];
        int count = 0;
        using (FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read;
            while (count < bytes.Length && (read = input.Read(bytes, count, bytes.Length - count)) > 0)
                count += read;
        }
        if (count > MaximumFileBytes) throw Invalid("file exceeds 128 KiB");
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes, 0, count);
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }
        catch (DecoderFallbackException) { throw Invalid("file must be valid UTF-8"); }
    }

    internal static InvalidDataException Invalid(string reason) => new("ServerManager.yml: " + reason + ".");

    private sealed class BoundedParser : IParser
    {
        private readonly Parser _inner;
        private int _events, _depth, _documents;
        internal BoundedParser(TextReader reader) => _inner = new Parser(reader);
        public ParsingEvent? Current => _inner.Current;
        public bool MoveNext()
        {
            if (!_inner.MoveNext()) return false;
            if (++_events > 4096) throw Invalid("YAML event limit exceeded");
            ParsingEvent? value = Current;
            if (value is AnchorAlias || value is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw Invalid("YAML anchors, aliases and explicit tags are not supported");
            if (value is DocumentStart && ++_documents > 1) throw Invalid("multiple YAML documents are not supported");
            if (value is MappingStart || value is SequenceStart)
            {
                if (++_depth > 8) throw Invalid("YAML nesting exceeds 8 levels");
            }
            else if (value is MappingEnd || value is SequenceEnd) --_depth;
            return true;
        }
    }

    internal const string DefaultYaml = @"# ServerManager server settings. Keep this file on the server only.
# UTF-8 YAML. Existing files/comments are never rewritten or imported from cfg.
# Valid edits reload automatically after two matching reads, normally 2-4 seconds.
# An invalid edit keeps ALL last valid settings. Invalid startup waits for correction.
# Omitted keys use these defaults. Exact keys only; duplicate/unknown keys are errors.
# Anchors, aliases, explicit tags and multiple documents are not supported.
serverSettings:
  maxPlayers: 24 # 1-64 concurrent players, including the listen host. Lowering does not kick existing players.
  maxCharactersPerAccount: 3 # 1-128; limits new registrations, not existing characters. Listen host and verified adminlist admins are exempt.
  backupsPerProfile: 30 # 1-50; the new count is used for future backup rotation.
  # true applies the latest server profile on connection (or the normal new-profile rules).
  # false accepts the selected local character; existing profiles can be updated by local data.
  # Server saves and backups continue in both modes. Changes affect new
  # connections only; active sessions keep the mode they joined with.
  loadServerCharacterOnJoin: true # true or false (lower-case, unquoted).

forbiddenItems: [] # Prefab-name list; for example [SwordCheat].

cheatDetection:
  action: kick # log, kick or ban (lower-case; off is not supported).

statCaps:
  action: log # log, kick or ban; only these five caps are configurable.
  health: 800 # 1-1000000
  stamina: 800 # 1-1000000
  eitr: 500 # 1-1000000
  weight: 2000 # 1-1000000
  damage: 50000 # 1-1000000000

# Complete starting inventory for NEW server profiles, replacing game defaults.
# startItems: [] starts naked with no items. Omission uses the four defaults below.
# Existing/imported profiles, reconnects and local-character capture never receive these items.
# Wearables start equipped (one per slot, prefab-name order); weapons/shields/torches do not.
# Use exact item prefab names; a name alone means 1, or add a comma and amount.
# Amounts must be integers from 1 to 1000000; inventory capacity is also checked.
# Invalid/non-item prefabs reject the entire edit. Mapping syntax is not supported.
startItems:
  - HelmetMidsummerCrown
  - ArmorRagsChest
  - ArmorRagsLegs
  - Torch
";
}

// All public entry points are called on the game thread. Workers only read/parse
// immutable captured values, and never mutate a world, policy or this service.
internal sealed class ServerSettingsReloadService : IDisposable
{
    private static readonly long PollIntervalTicks = 2 * Stopwatch.Frequency;
    private readonly Action<string> _logInfo, _logWarning;
    private string? _dataRoot, _candidateText, _processedText, _lastReadError;
    private ServerSettings? _deferred;
    private Task<ReadResult>? _read;
    private long _nextPoll;
    private int _generation;
    private bool _disposed;
    public ServerSettings? Current { get; private set; }

    internal ServerSettingsReloadService(Action<string> logInfo, Action<string> logWarning)
    {
        _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
        _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
    }

    // First load is deliberately synchronous and bounded: a server must not admit
    // profiles under defaults while its real startup policy is still being read.
    internal bool EnsureLoaded(string dataRoot, Func<ServerSettings, bool> apply)
    {
        Tick(dataRoot, apply);
        return Current != null;
    }

    internal void Tick(string dataRoot, Func<ServerSettings, bool> apply)
    {
        if (_disposed) return;
        if (apply == null) throw new ArgumentNullException(nameof(apply));
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("A server data root is required.", nameof(dataRoot));
        if (!string.Equals(_dataRoot, dataRoot, StringComparison.Ordinal))
        {
            Reset();
            _dataRoot = dataRoot;
            _nextPoll = Stopwatch.GetTimestamp() + PollIntervalTicks;
            Consume(Read(dataRoot, _generation, true, null, null), apply);
            return;
        }

        Task<ReadResult>? pending = _read;
        if (pending != null)
        {
            if (!pending.IsCompleted) return;
            _read = null;
            ReadResult result = pending.GetAwaiter().GetResult();
            if (result.Generation == _generation) Consume(result, apply);
        }
        long now = Stopwatch.GetTimestamp();
        if (now < _nextPoll) return;
        _nextPoll = now + PollIntervalTicks;
        string root = _dataRoot!;
        string? candidate = _candidateText, processed = _processedText;
        int generation = _generation;
        _read = Task.Run(() => Read(root, generation, false, candidate, processed));
    }

    private void Consume(ReadResult result, Func<ServerSettings, bool> apply)
    {
        if (result.Text == null)
        {
            _candidateText = null;
            _processedText = null;
            _deferred = null;
            if (_lastReadError != result.Error) Warn(result.Error!);
            _lastReadError = result.Error;
            return;
        }
        _lastReadError = null;
        if (!string.Equals(_candidateText, result.Text, StringComparison.Ordinal))
        {
            _deferred = null;
            _processedText = null;
        }
        _candidateText = result.Text;
        if (result.Parsed)
        {
            _processedText = result.Text;
            _deferred = result.Settings;
            if (result.Error != null) { Warn(result.Error); return; }
        }
        if (_deferred == null) return;
        try
        {
            // false defers (e.g. ObjectDB not ready); throwing rejects this text.
            // The caller must validate everything before changing any live state.
            if (!apply(_deferred)) return;
            bool initial = Current == null;
            Current = _deferred;
            _deferred = null;
            _logInfo(initial ? "ServerManager.yml settings loaded." : "ServerManager.yml settings reloaded.");
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            _deferred = null;
            Warn("ServerManager.yml candidate could not be applied (" + exception.GetType().Name + ").");
        }
    }

    private void Warn(string reason) => _logWarning(reason + (Current == null
        ? " Server configuration is not ready; waiting for a valid file."
        : " Keeping all active server settings; the file was not rewritten."));

    private static ReadResult Read(string root, int generation, bool initial, string? candidate, string? processed)
    {
        string? text = null;
        bool parsed = false;
        try
        {
            text = ServerSettings.ReadFile(root, initial);
            parsed = initial || (string.Equals(candidate, text, StringComparison.Ordinal) &&
                !string.Equals(processed, text, StringComparison.Ordinal));
            return new ReadResult(generation, text, parsed, parsed ? ServerSettings.Parse(text) : null, null);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            string error = exception is InvalidDataException ? exception.Message :
                "ServerManager.yml could not be read (" + exception.GetType().Name + ").";
            return new ReadResult(generation, text, parsed, null, error);
        }
    }

    internal void Reset()
    {
        ++_generation;
        _dataRoot = _candidateText = _processedText = _lastReadError = null;
        _deferred = Current = null;
        _read = null; // A stale bounded worker owns only captured values, not service state.
        _nextPoll = 0;
    }

    public void Dispose()
    {
        _disposed = true;
        Reset();
    }

    private static bool IsFatal(Exception exception) => exception is OutOfMemoryException ||
        exception is StackOverflowException || exception is AccessViolationException || exception is ThreadAbortException;

    private sealed class ReadResult
    {
        internal readonly int Generation;
        internal readonly string? Text, Error;
        internal readonly bool Parsed;
        internal readonly ServerSettings? Settings;
        internal ReadResult(int generation, string? text, bool parsed, ServerSettings? settings, string? error)
        { Generation = generation; Text = text; Parsed = parsed; Settings = settings; Error = error; }
    }
}
