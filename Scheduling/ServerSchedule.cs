using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Cronos;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ServerManager;

// Server-local data only. Neither parsing nor time calculation invokes commands.
internal sealed class ServerScheduleSettings
{
    internal const int MaximumFileBytes = 128 * 1024;
    internal const int MaximumJobs = 64;
    internal const string FileName = "cron.yml";
    internal const string AlternateFileName = "cron.yaml";
    internal const string DefaultYaml = @"# Server-local scheduled commands. Valid edits reload; invalid edits keep the last valid settings.
# local uses the server computer's timezone. UTC and installed timezone IDs also work.
# Compatible Upgrade World tracking adds verified pre/post saves; maintenance catches up once after downtime.
# Without a compatible tracker, only a single change command is dispatched, without automatic saves or completion confirmation.
# Other missed offline occurrences are skipped. A running job never overlaps itself.
# cron.yml and cron_last.yml are the defaults; .yaml is also accepted. Use only one suffix for each file.
# cron_last.yml is generated ServerManager progress; do not edit or delete it to retry a job.
# Keep Upgrade World changes in their own jobs (only save may accompany them; see README).
timezone: local
interval: 10
logJobs: true
logSkipped: true
jobs: []
# To use an example, replace jobs: [] with its uncommented jobs block.
# jobs:
#   - command: 'announce Good morning!'
#     schedule: '0 9 * * *'
# Optional per job: chance: 1, useGameTime: false, log: true,
# globalKeys: '', bannedGlobalKeys: ''. Keys are comma-separated strings.
# commands: [...] takes precedence over command. Game-time cron uses UTC from year 2000.
# Comment out or remove a job to disable it. Identical job definitions are rejected.
# Discord messages use ServerManager's discord.yml, not discordConnector.

# Upgrade World with compatible completion tracking: daily batch at 05:35.
# Verified saves are added before and after the batch; no separate save job is needed.
# Test on a backed-up world before uncommenting this batch.
# WARNING: safeZones=0 and force disable base protection.
# After zones_reset, later resets target zones still generated, including protected bases.
# jobs:
#   - schedule: '35 5 * * *'
#     commands:
#       - world_clean
#       - zones_reset safeZones=1 start
#       - vegetation_reset rock4_copper,silvervein terrain=20 safeZones=0 start
#       - locations_reset Hildir_crypt,Hildir_cave,Hildir_plainsfortress,SunkenCrypt4,Crypt2,Crypt3,Crypt4,MountainCave02,Mistlands_Giant1,Mistlands_Excavation1,Mistlands_DvergrTownEntrance1,Mistlands_DvergrTownEntrance2,Mistlands_DvergrBossEntrance1,CharredFortress force start
";

    public TimeZoneInfo TimeZone { get; }
    public double IntervalSeconds { get; }
    public bool LogSkipped { get; }
    public IReadOnlyList<ServerScheduleJob> Jobs { get; }

    private ServerScheduleSettings(TimeZoneInfo timeZone, double intervalSeconds, bool logSkipped, List<ServerScheduleJob> jobs)
    {
        TimeZone = timeZone;
        IntervalSeconds = intervalSeconds;
        LogSkipped = logSkipped;
        Jobs = jobs.AsReadOnly();
    }

    public static ServerScheduleSettings Parse(string yaml, Func<string, bool>? commandAllowed = null)
    {
        if (yaml == null || yaml.Length > MaximumFileBytes) throw Invalid("missing or oversized YAML");
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(yaml) > MaximumFileBytes)
                throw Invalid("file exceeds 128 KiB");
        }
        catch (EncoderFallbackException) { throw Invalid("invalid UTF-8 text"); }
        YamlStream stream = new();
        try
        {
            using StringReader reader = new(yaml);
            stream.Load(new BoundedParser(reader));
        }
        catch (YamlException) { throw Invalid("invalid YAML syntax or duplicate key"); }
        catch (ArgumentException) { throw Invalid("invalid YAML structure or duplicate key"); }
        if (stream.Documents.Count != 1) throw Invalid("exactly one document is required");
        Dictionary<string, YamlNode> root = Map(stream.Documents[0].RootNode, "timezone", "interval", "logJobs",
            "logSkipped", "discordConnector", "jobs", "zone", "join");
        string zone = root.TryGetValue("timezone", out YamlNode zoneNode) ? Text(zoneNode, 128) : "local";
        TimeZoneInfo timeZone;
        try
        {
            timeZone = string.Equals(zone, "local", StringComparison.OrdinalIgnoreCase) ? TimeZoneInfo.Local :
                string.Equals(zone, "UTC", StringComparison.OrdinalIgnoreCase) ? TimeZoneInfo.Utc :
                TimeZoneInfo.FindSystemTimeZoneById(zone);
        }
        catch (TimeZoneNotFoundException) { throw Invalid("timezone is not installed on this server"); }
        catch (InvalidTimeZoneException) { throw Invalid("timezone data is invalid"); }
        double interval = Number(root, "interval", 10, 1, 3600);
        bool logJobs = Boolean(root, "logJobs", true), logSkipped = Boolean(root, "logSkipped", true);
        if (Boolean(root, "discordConnector", false))
            throw Invalid("discordConnector is not supported; configure ServerManager's discord.yml instead");
        foreach (string unsupported in new[] { "zone", "join" })
            if (root.TryGetValue(unsupported, out YamlNode hooks) &&
                (hooks is not YamlSequenceNode empty || empty.Children.Count != 0))
                throw Invalid(unsupported + " hooks are not supported; only an empty sequence is accepted");

        List<ServerScheduleJob> jobs = new();
        HashSet<string> ids = new(StringComparer.Ordinal);
        if (root.TryGetValue("jobs", out YamlNode jobsNode))
        {
            if (jobsNode is not YamlSequenceNode sequence || sequence.Children.Count > MaximumJobs)
                throw Invalid("jobs must be a sequence of at most 64 jobs");
            foreach (YamlNode entry in sequence.Children)
            {
                Dictionary<string, YamlNode> job = Map(entry, "command", "commands", "schedule", "chance",
                    "useGameTime", "log", "globalKeys", "bannedGlobalKeys");
                string[] cronFields = Text(Required(job, "schedule"), 256, true)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                string cron = string.Join(" ", cronFields);
                int fields = cronFields.Length;
                if (fields != 5 && fields != 6) throw Invalid("schedule must have five fields, or six including seconds");
                CronExpression expression;
                try { expression = CronExpression.Parse(cron, fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard); }
                catch (CronFormatException) { throw Invalid("invalid schedule expression"); }
                // Match NewCron's precedence without dispatching the unused scalar.
                List<string> commands = job.TryGetValue("commands", out YamlNode commandsNode)
                    ? Strings(commandsNode, 16, 2048) : new() { Text(Required(job, "command"), 2048) };
                if (commands.Count == 0) throw Invalid("commands must contain at least one command");
                UpgradeWorldCommandKind[] kinds = commands.Select(UpgradeWorldScheduleCommands.GetKind).ToArray();
                if (kinds.Any(kind => kind == UpgradeWorldCommandKind.Control || kind == UpgradeWorldCommandKind.Unsupported))
                    throw Invalid("an Upgrade World command is manual-only or is not verified for scheduled execution");
                bool maintenance = kinds.Any(UpgradeWorldScheduleCommands.IsMaintenance);
                if (maintenance && commands.Where((command, index) => !UpgradeWorldScheduleCommands.IsMaintenance(kinds[index]))
                    .Any(command => !string.Equals(command.Trim(), "save", StringComparison.OrdinalIgnoreCase)))
                    throw Invalid("Upgrade World change jobs may only contain reviewed world changes and save");
                if (commandAllowed != null && commands.Any(command => !commandAllowed(command)))
                    throw Invalid("a command is not allowed for scheduled execution");
                double chance = Number(job, "chance", 1, 0, 1);
                bool gameTime = Boolean(job, "useGameTime", false);
                List<string> requiredKeys = job.TryGetValue("globalKeys", out YamlNode keys) ? Keys(keys) : new();
                List<string> bannedKeys = job.TryGetValue("bannedGlobalKeys", out YamlNode banned) ? Keys(banned) : new();
                string id = CreateId(cron, commands, chance, gameTime, gameTime ? "UTC" : timeZone.Id, requiredKeys, bannedKeys);
                if (!ids.Add(id)) throw Invalid("identical job definitions are not supported");
                jobs.Add(new ServerScheduleJob(id, cron, expression, commands,
                    chance, gameTime, requiredKeys, bannedKeys, maintenance, Boolean(job, "log", logJobs)));
            }
        }
        return new ServerScheduleSettings(timeZone, interval, logSkipped, jobs);
    }

    private static YamlNode Required(Dictionary<string, YamlNode> map, string key) =>
        map.TryGetValue(key, out YamlNode node) ? node : throw Invalid("a required job field is missing: " + key);

    private static Dictionary<string, YamlNode> Map(YamlNode node, params string[] allowed)
    {
        if (node is not YamlMappingNode mapping) throw Invalid("expected a mapping");
        Dictionary<string, YamlNode> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode scalar || scalar.Value == null ||
                !allowed.Contains(scalar.Value) || result.ContainsKey(scalar.Value))
                throw Invalid("unknown or duplicate key");
            result.Add(scalar.Value, pair.Value);
        }
        return result;
    }

    private static string Text(YamlNode node, int maximum, bool allowTabs = false)
    {
        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value) ||
            scalar.Value!.Length > maximum || scalar.Value.Any(c => char.IsControl(c) && !(allowTabs && c == '\t')) ||
            scalar.Style == ScalarStyle.Plain && (scalar.Value == "~" ||
                string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("expected a bounded nonempty single-line string");
        return scalar.Value.Trim();
    }

    private static double Number(Dictionary<string, YamlNode> map, string key, double fallback, double minimum, double maximum)
    {
        if (!map.TryGetValue(key, out YamlNode node)) return fallback;
        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain ||
            !double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum)
            throw Invalid(key + " must be an unquoted number from " + minimum.ToString(CultureInfo.InvariantCulture) +
                " to " + maximum.ToString(CultureInfo.InvariantCulture));
        return value == 0 ? 0 : value; // Canonicalize negative zero for stable identities.
    }

    private static List<string> Keys(YamlNode node)
    {
        if (node is not YamlScalarNode scalar || scalar.Value == null || scalar.Value.Length > 64 * 129 ||
            scalar.Value.Any(char.IsControl) || scalar.Style == ScalarStyle.Plain &&
            (scalar.Value.Length == 0 || scalar.Value == "~" || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("global key conditions must be bounded comma-separated strings");
        string[] keys = scalar.Value.Split(',').Select(value => value.Trim()).Where(value => value.Length != 0).ToArray();
        if (keys.Length > 64 || keys.Any(value => value.Length > 128)) throw Invalid("global key conditions exceed their bounds");
        return keys.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    private static string CreateId(string cron, List<string> commands, double chance, bool gameTime, string timeZone,
        List<string> requiredKeys, List<string> bannedKeys)
    {
        using MemoryStream bytes = new();
        using (BinaryWriter writer = new(bytes, Encoding.UTF8, true))
        {
            writer.Write(cron); writer.Write(chance); writer.Write(gameTime); writer.Write(timeZone);
            foreach (List<string> list in new[] { commands, requiredKeys, bannedKeys })
            { writer.Write(list.Count); foreach (string value in list) writer.Write(value); }
        }
        using SHA256 sha = SHA256.Create();
        string hash = BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
        string verb = new(CommandVerb(commands[0]).Where(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' ||
            c == '-' || c == '_' || c == '.').Take(16).ToArray());
        return (verb.Length == 0 ? "job" : verb) + "-" + hash.Substring(0, 40);
    }

    private static bool Boolean(Dictionary<string, YamlNode> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out YamlNode node)) return fallback;
        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain ||
            scalar.Value != "true" && scalar.Value != "false") throw Invalid(key + " must be true or false");
        return scalar.Value == "true";
    }

    private static List<string> Strings(YamlNode node, int maximum, int maximumLength)
    {
        if (node is not YamlSequenceNode sequence || sequence.Children.Count > maximum)
            throw Invalid("expected a bounded string sequence");
        return sequence.Children.Select(value => Text(value, maximumLength)).ToList();
    }

    private static InvalidDataException Invalid(string reason) => new(FileName + ": " + reason + ".");

    internal static string CommandVerb(string command)
    {
        string[] words = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length == 0 ? "" : words[0].Trim('\'', '"').ToLowerInvariant();
        return verb.StartsWith("sm:", StringComparison.Ordinal) ? verb.Substring(3) : verb;
    }

    private sealed class BoundedParser : IParser
    {
        private readonly Parser _inner;
        private int _events, _depth, _documents;
        internal BoundedParser(TextReader reader) => _inner = new Parser(reader);
        public ParsingEvent? Current => _inner.Current;
        public bool MoveNext()
        {
            if (!_inner.MoveNext()) return false;
            if (++_events > 20000) throw Invalid("YAML event limit exceeded");
            ParsingEvent? value = Current;
            if (value is AnchorAlias || value is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw Invalid("anchors, aliases and explicit tags are not supported");
            if (value is DocumentStart && ++_documents > 1) throw Invalid("multiple documents are not supported");
            if (value is MappingStart || value is SequenceStart)
            {
                if (++_depth > 8) throw Invalid("YAML nesting exceeds 8 levels");
            }
            else if (value is MappingEnd || value is SequenceEnd) --_depth;
            return true;
        }
    }
}

internal enum UpgradeWorldCommandKind { None, QueuedMutation, ImmediateMutation, Query, Control, Unsupported }

// Reviewed against Upgrade World 1.80's registered commands and execution paths.
// Classification is deliberately explicit: matching a prefix is not proof of a
// command's ownership or completion semantics. The runtime also verifies the
// registered handler belongs to the optional mod before dispatching it.
internal static class UpgradeWorldScheduleCommands
{
    private static readonly HashSet<string> UpgradeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tarpits", "mistlands", "mistlands_worldgen", "hh_worldgen", "legacy_worldgen",
        "mountain_caves", "hildir", "ashlands", "deepnorth", "bogwitch", "combatruins"
    };

    internal static bool IsMaintenance(string commandLine) => IsMaintenance(GetKind(commandLine));
    internal static bool IsMaintenance(UpgradeWorldCommandKind kind) =>
        kind == UpgradeWorldCommandKind.QueuedMutation || kind == UpgradeWorldCommandKind.ImmediateMutation;

    internal static UpgradeWorldCommandKind GetKind(string commandLine)
    {
        string[] words = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return UpgradeWorldCommandKind.None;
        string verb = words[0].Trim('\'', '"').ToLowerInvariant();
        // sm: is reserved for ServerManager's own commands, not an Upgrade World alias.
        bool alias = verb.StartsWith("sm:", StringComparison.Ordinal);
        UpgradeWorldCommandKind kind = ClassifyVerb(alias ? verb.Substring(3) : verb, commandLine);
        return alias && kind != UpgradeWorldCommandKind.None ? UpgradeWorldCommandKind.Unsupported : kind;
    }

    private static UpgradeWorldCommandKind ClassifyVerb(string verb, string commandLine)
    {
        switch (verb)
        {
            case "chests_reset":
            case "locations_add": case "locations_remove": case "locations_reset":
            case "objects_edit": case "objects_refresh": case "objects_remove": case "objects_swap":
            case "world_reset":
            case "vegetation_add": case "vegetation_remove": case "vegetation_reset":
            case "zones_generate": case "zones_reset": case "zones_restore":
                return UpgradeWorldCommandKind.QueuedMutation;
            case "upgrade":
                return GetUpgradeType(commandLine).Length == 0 ? UpgradeWorldCommandKind.Unsupported : UpgradeWorldCommandKind.QueuedMutation;
            case "temple_gen":
                // Without a known version UW only prints boss-stone positions
                // via its queue; do not treat that query as catch-up maintenance.
                return HasSingleVersion(commandLine, "mistlands", "ashlands")
                    ? UpgradeWorldCommandKind.QueuedMutation : UpgradeWorldCommandKind.Unsupported;
            case "world_gen":
                return HasSingleVersion(commandLine, "legacy", "hh", "mistlands")
                    ? UpgradeWorldCommandKind.QueuedMutation : UpgradeWorldCommandKind.Unsupported;
            case "world_clean":
            case "clean_chests": case "clean_dungeons": case "clean_duplicates": case "clean_health":
            case "clean_locations": case "clean_objects": case "clean_spawns": case "clean_stands":
            case "location_register": case "locations_fix": case "locations_swap":
            case "time_change": case "time_change_day": case "time_set": case "time_set_day":
                return UpgradeWorldCommandKind.ImmediateMutation;
            case "biomes_count": case "chests_search": case "locations_count": case "locations_list":
            case "objects_count": case "objects_list":
                return UpgradeWorldCommandKind.Query;
            // GetInfo invokes OnInit again in 1.80, changing a live operation's
            // filtered targets/counters. This is not a harmless status query.
            case "uw_check":
            case "start": case "stop": case "save_disable": case "save_enable": case "verbose":
                return UpgradeWorldCommandKind.Control;
            case "location_unregister":
                // 1.80 prints a removal without removing the registration.
                return UpgradeWorldCommandKind.Unsupported;
            default:
                return UpgradeWorldCommandKind.None;
        }
    }

    internal static string GetUpgradeType(string commandLine)
    {
        string selected = "";
        foreach (string word in commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            string value = word.Trim('\'', '"').ToLowerInvariant();
            // 1.80's onions branch constructs a reset but never enqueues it.
            if (value == "onions") return "";
            if (!UpgradeTypes.Contains(value)) continue;
            if (selected.Length != 0) return "";
            selected = value;
        }
        return selected;
    }

    private static bool HasSingleVersion(string commandLine, params string[] versions)
    {
        string[] arguments = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        // These two command forms only document a version and optional start.
        // Do not emulate UW's last-known-version-wins behavior for ambiguous input.
        // UW compares these version names case-sensitively, unlike upgrade's
        // recipe flags. Quoting/uppercasing one can turn temple_gen into a query.
        return arguments.Count(word => versions.Contains(word)) == 1 &&
            arguments.All(word => string.Equals(word, "start", StringComparison.OrdinalIgnoreCase) || versions.Contains(word));
    }
}

internal sealed class ServerScheduleJob
{
    public string Id { get; }
    public string Cron { get; }
    internal CronExpression Expression { get; }
    public IReadOnlyList<string> Commands { get; }
    // Parsed jobs are active; commenting out/removing a job disables it.
    // Keep these derived values for the existing journal fingerprint contract.
    public bool Enabled => true;
    public double Chance { get; }
    public bool UseGameTime { get; }
    public bool CatchUp => Maintenance;
    public bool Maintenance { get; }
    public bool Log { get; }
    public IReadOnlyList<string> GlobalKeys { get; }
    public IReadOnlyList<string> BannedGlobalKeys { get; }

    internal ServerScheduleJob(string id, string cron, CronExpression expression, List<string> commands,
        double chance, bool gameTime, List<string> globalKeys, List<string> bannedGlobalKeys,
        bool maintenance = false, bool log = true)
    {
        Id = id; Cron = cron; Expression = expression; Commands = commands.AsReadOnly();
        Chance = chance; UseGameTime = gameTime;
        Maintenance = maintenance;
        Log = log;
        GlobalKeys = globalKeys.AsReadOnly(); BannedGlobalKeys = bannedGlobalKeys.AsReadOnly();
    }

    internal bool SameAs(ServerScheduleJob other) => Id == other.Id && Cron == other.Cron &&
        Chance == other.Chance && UseGameTime == other.UseGameTime &&
        Maintenance == other.Maintenance &&
        Commands.SequenceEqual(other.Commands) && GlobalKeys.SequenceEqual(other.GlobalKeys) &&
        BannedGlobalKeys.SequenceEqual(other.BannedGlobalKeys);
}

internal sealed class ServerScheduleOccurrence
{
    public ServerScheduleJob Job { get; }
    public DateTime OccurrenceUtc { get; }
    public DateTime CoveredUntilUtc { get; }
    internal long Claim { get; }
    internal ServerScheduleEngine Owner { get; }
    internal ServerScheduleOccurrence(ServerScheduleJob job, DateTime occurrence, long claim, ServerScheduleEngine owner,
        DateTime? coveredUntil = null)
    { Job = job; OccurrenceUtc = occurrence; CoveredUntilUtc = coveredUntil ?? occurrence; Claim = claim; Owner = owner; }
}

// Single-owner/main-thread engine. Claims precede chance/key gates and dispatch;
// every claimed occurrence must Complete even on skip/error. The journal supplies
// restart cursors explicitly; the engine alone never replays historical work.
internal sealed class ServerScheduleEngine
{
    internal static readonly DateTime GameEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private sealed class State
    {
        internal ServerScheduleJob Job;
        internal readonly TimeZoneInfo Zone;
        internal DateTime? Next;
        internal State(ServerScheduleJob job, TimeZoneInfo zone, DateTime now)
        { Job = job; Zone = zone; Next = job.Expression.GetNextOccurrence(now, zone); }
    }
    private Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _claims = new(StringComparer.Ordinal);
    private long _sequence;

    public void Apply(ServerScheduleSettings settings, DateTime utcNow, DateTime gameUtcNow,
        IReadOnlyDictionary<string, DateTime>? resumeCursors = null)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        ValidateClocks(utcNow, gameUtcNow);
        Dictionary<string, State> next = new(StringComparer.Ordinal);
        foreach (ServerScheduleJob job in settings.Jobs)
        {
            TimeZoneInfo zone = job.UseGameTime ? TimeZoneInfo.Utc : settings.TimeZone;
            if (_states.TryGetValue(job.Id, out State state) && job.SameAs(state.Job) &&
                zone.Id == state.Zone.Id && zone.HasSameRules(state.Zone))
            {
                // Logging is not execution identity. Publish changed logging while
                // retaining the existing due boundary and any active claim.
                state.Job = job;
                next.Add(job.Id, state);
            }
            else
            {
                DateTime baseline = job.UseGameTime ? gameUtcNow : utcNow;
                if (resumeCursors != null && resumeCursors.TryGetValue(job.Id, out DateTime saved))
                {
                    if (saved.Kind != DateTimeKind.Utc || job.UseGameTime && saved < GameEpoch)
                        throw new ArgumentException("Resume cursors must use valid UTC clocks.");
                    baseline = saved;
                }
                next.Add(job.Id, new State(job, zone, baseline));
            }
        }
        _states = next;
    }

    public IReadOnlyList<ServerScheduleOccurrence> ClaimDue(DateTime utcNow, DateTime gameUtcNow)
    {
        ValidateClocks(utcNow, gameUtcNow);
        List<ServerScheduleOccurrence> due = new();
        foreach (State state in _states.Values)
        {
            DateTime now = state.Job.UseGameTime ? gameUtcNow : utcNow;
            if (!state.Next.HasValue || now < state.Next.Value) continue;
            DateTime occurrence = state.Next.Value;
            // Coalesce a delayed poll into one occurrence, with no replay queue.
            state.Next = state.Job.Expression.GetNextOccurrence(now, state.Zone);
            if (_claims.ContainsKey(state.Job.Id) || _claims.Count >= ServerScheduleSettings.MaximumJobs) continue;
            long claim = checked(++_sequence);
            _claims.Add(state.Job.Id, claim);
            due.Add(new ServerScheduleOccurrence(state.Job, occurrence, claim, this, now));
        }
        return due.AsReadOnly();
    }

    public void Complete(ServerScheduleOccurrence occurrence)
    {
        if (occurrence == null) throw new ArgumentNullException(nameof(occurrence));
        if (!ReferenceEquals(occurrence.Owner, this)) return;
        if (_claims.TryGetValue(occurrence.Job.Id, out long claim) && claim == occurrence.Claim)
            _claims.Remove(occurrence.Job.Id);
    }

    public void SkipThrough(string jobId, DateTime utcNow, DateTime gameUtcNow)
    {
        ValidateClocks(utcNow, gameUtcNow);
        if (!_states.TryGetValue(jobId, out State state)) return;
        DateTime now = state.Job.UseGameTime ? gameUtcNow : utcNow;
        state.Next = state.Job.Expression.GetNextOccurrence(now, state.Zone);
        // Operator acknowledgement affects only the reviewed job's timer. Other
        // queued/running occurrences retain their claims and captured definitions.
    }

    public void Reset()
    {
        _states.Clear();
        _claims.Clear();
        // Keep sequence monotonic: an old session's completion cannot release a new claim.
    }

    private static void ValidateClocks(DateTime utcNow, DateTime gameUtcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc || gameUtcNow.Kind != DateTimeKind.Utc || gameUtcNow < GameEpoch)
            throw new ArgumentException("Scheduler clocks must be UTC; game time starts at 2000-01-01 UTC.");
    }
}
