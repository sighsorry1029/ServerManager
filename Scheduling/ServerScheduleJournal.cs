using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ServerManager;

// This is an execution journal, not a backup or proof of world durability. Call all
// IO on a worker; the runtime must await Flush before starting a command.
internal sealed class ServerScheduleJournal : IDisposable
{
    internal const string FileName = "cron_last.yml";
    internal const string AlternateFileName = "cron_last.yaml";
    internal const int MaximumFileBytes = 1024 * 1024;
    internal const int MaximumWorlds = 32;
    private const int MaximumRecordsPerWorld = 128;
    private sealed class Record
    {
        internal string Id = "", Fingerprint = "", State = "ready";
        internal bool GameTime, Maintenance;
        internal DateTime Cursor;
        internal DateTime? Due, CoveredUntil;
        internal ServerScheduleJob? ActiveJob;
    }
    private readonly object _gate = new();
    private readonly string _root, _path, _worldId;
    private readonly FileStream _lease;
    private readonly Dictionary<string, Dictionary<string, Record>> _worlds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServerScheduleJob> _active = new(StringComparer.Ordinal);
    private string? _fileHash;
    private ServerScheduleSettings? _settings;
    private bool _dirty, _disposed;

    private ServerScheduleJournal(string root, string path, string worldId, FileStream lease)
    { _root = root; _path = path; _worldId = worldId; _lease = lease; }

    internal static ServerScheduleJournal Open(string root, string worldId)
    {
        if (string.IsNullOrWhiteSpace(worldId) || worldId.Length > 80 || worldId.Any(char.IsControl))
            throw new ArgumentException("A bounded stable world ID is required.");
        root = Path.GetFullPath(root);
        RejectLinks(root); Directory.CreateDirectory(root); RejectLinks(root);
        string lockPath = Path.Combine(root, ".cron-writer.lock"); RejectLinks(lockPath);
        FileStream lease = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ServerScheduleJournal? result = null;
        try
        {
            result = new(root, ResolvePath(root), worldId, lease);
            RejectLinks(result._path);
            if (File.Exists(result._path))
            {
                byte[] bytes = ReadBytes(result._path);
                result.Load(bytes); result._fileHash = Hash(bytes);
            }
            else if (lease.Length != 0)
                throw Invalid("journal is missing after prior initialization; restore the journal before scheduling");
            if (!result._worlds.TryGetValue(worldId, out Dictionary<string, Record> records))
            {
                if (result._worlds.Count >= MaximumWorlds) throw Invalid("too many worlds");
                result._worlds.Add(worldId, records = new(StringComparer.Ordinal)); result._dirty = true;
            }
            // Running means dispatch might have changed the world. Never infer that
            // an interrupted process did nothing, even if its command was very short.
            foreach (Record record in records.Values.Where(record => record.State == "running"))
            {
                if (record.Maintenance) record.State = "needs_review";
                else Reset(record, record.CoveredUntil ?? record.Cursor);
                result._dirty = true;
            }
            result.Flush();
            if (lease.Length == 0)
            {
                byte[] marker = Encoding.ASCII.GetBytes("ServerManager cron journal v1\n");
                lease.Write(marker, 0, marker.Length); lease.Flush(true);
            }
            return result;
        }
        catch { if (result != null) result.Dispose(); else lease.Dispose(); throw; }
    }

    // One journal, in place, whichever supported suffix the operator supplied.
    // Never choose between two possible histories or rename a live journal.
    private static string ResolvePath(string root)
    {
        string primary = Path.Combine(root, FileName), alternate = Path.Combine(root, AlternateFileName);
        RejectLinks(primary); RejectLinks(alternate);
        if (Directory.Exists(primary) || Directory.Exists(alternate))
            throw Invalid("a journal path is a directory");
        bool primaryExists = File.Exists(primary), alternateExists = File.Exists(alternate);
        if (primaryExists && alternateExists)
            throw Invalid("both " + FileName + " and " + AlternateFileName + " exist; stop the server and keep only the intended journal");
        return alternateExists ? alternate : primary;
    }

    internal void Reconcile(ServerScheduleSettings settings, DateTime utcNow, DateTime gameNow)
    {
        lock (_gate)
        {
            Check(); Clocks(utcNow, gameNow);
            Dictionary<string, Record> records = Records;
            HashSet<string> present = new(settings.Jobs.Select(job => job.Id), StringComparer.Ordinal);
            foreach (string removed in records.Keys.Where(id => !present.Contains(id) &&
                records[id].State != "running" && records[id].State != "needs_review").ToArray())
            { records.Remove(removed); _dirty = true; }
            _active.Clear();
            foreach (ServerScheduleJob job in settings.Jobs)
            {
                _active.Add(job.Id, job);
                string fingerprint = Fingerprint(settings, job);
                DateTime now = job.UseGameTime ? gameNow : utcNow;
                if (!records.TryGetValue(job.Id, out Record record))
                {
                    if (records.Count >= MaximumRecordsPerWorld) throw Invalid("too many retained jobs for this world");
                    records.Add(job.Id, new Record { Id = job.Id, Fingerprint = fingerprint, GameTime = job.UseGameTime,
                        Maintenance = job.Maintenance, Cursor = now });
                    _dirty = true;
                }
                else if (record.State != "running" && record.State != "needs_review" &&
                    (record.Fingerprint != fingerprint || record.State == "pending" && (!job.CatchUp || !job.Enabled)))
                {
                    record.Fingerprint = fingerprint; record.GameTime = job.UseGameTime; record.Maintenance = job.Maintenance;
                    Reset(record, now); _dirty = true;
                }
            }
            _settings = settings;
        }
    }

    internal IReadOnlyDictionary<string, DateTime> GetResumeCursors(ServerScheduleSettings settings)
    {
        lock (_gate)
        {
            Check(); Dictionary<string, DateTime> result = new(StringComparer.Ordinal);
            foreach (ServerScheduleJob job in settings.Jobs)
            {
                if (!job.Enabled || !job.CatchUp || !Records.TryGetValue(job.Id, out Record record) ||
                    record.Fingerprint != Fingerprint(settings, job)) continue;
                if (record.State == "ready") result.Add(job.Id, record.Cursor);
                else if (record.State == "pending" && record.Due.HasValue)
                    result.Add(job.Id, record.Due.Value.AddTicks(-1));
            }
            return result;
        }
    }

    internal bool IsBlocked(string jobId)
    {
        lock (_gate)
        {
            Check();
            return Records.TryGetValue(jobId, out Record record) &&
                (record.State == "running" || record.State == "needs_review");
        }
    }

    internal IReadOnlyList<string> ReviewRequiredJobs
    {
        get { lock (_gate) { Check(); return Records.Values.Where(record => record.State == "needs_review").Select(record => record.Id).ToArray(); } }
    }

    internal string GetStatus()
    {
        lock (_gate)
        {
            Check();
            return "World " + _worldId + ": " + (Records.Count == 0 ? "no journal jobs" :
                string.Join("; ", Records.Values.OrderBy(record => record.Id, StringComparer.Ordinal)
                    .Select(record => record.Id + "=" + record.State + " (" + Date(record.Cursor) + ")")));
        }
    }

    internal void RecordPending(ServerScheduleJob job, DateTime due, DateTime coveredUntil)
    {
        lock (_gate)
        {
            Record record = Current(job);
            if (record.State != "ready" && record.State != "pending") throw Invalid("job requires review or is already running: " + job.Id);
            if (due.Kind != DateTimeKind.Utc || coveredUntil.Kind != DateTimeKind.Utc || due > coveredUntil ||
                job.UseGameTime && due < ServerScheduleEngine.GameEpoch) throw Invalid("invalid occurrence clock");
            if (record.State == "pending" && record.Due != due) throw Invalid("pending occurrence cannot be replaced");
            record.State = "pending"; record.Due = due; record.CoveredUntil = coveredUntil;
            record.ActiveJob = job; _dirty = true;
        }
    }

    internal void MarkRunning(ServerScheduleJob job)
    {
        lock (_gate)
        {
            Record record = Current(job);
            if (record.State != "pending") throw Invalid("job is not pending: " + job.Id);
            record.State = "running"; _dirty = true;
        }
    }

    internal void MarkFinished(ServerScheduleJob job, bool success, bool needsReview = false, DateTime? coveredThrough = null)
    {
        lock (_gate)
        {
            Record record = Current(job);
            if (record.State != "pending" && record.State != "running") throw Invalid("job is not active: " + job.Id);
            if (coveredThrough.HasValue && (coveredThrough.Value.Kind != DateTimeKind.Utc ||
                record.GameTime && coveredThrough.Value < ServerScheduleEngine.GameEpoch))
                throw Invalid("invalid completion clock");
            if (needsReview || record.Maintenance && record.State == "running" && !success) record.State = "needs_review";
            else
            {
                DateTime cursor = record.CoveredUntil ?? record.Cursor;
                // Occurrences ignored while this job was busy are not an offline
                // backlog. A clock rollback must not move its durable cursor back.
                if (coveredThrough.HasValue && coveredThrough.Value > cursor) cursor = coveredThrough.Value;
                Reset(record, cursor);
            }
            _dirty = true;
        }
    }

    // Explicit operator acknowledgement: this does not rerun or roll back anything.
    internal void Acknowledge(string jobId, DateTime utcNow, DateTime gameNow)
    {
        lock (_gate)
        {
            Check(); Clocks(utcNow, gameNow);
            if (!Records.TryGetValue(jobId, out Record record) || record.State != "needs_review")
                throw Invalid("job is not waiting for review: " + jobId);
            if (_settings != null && _active.TryGetValue(jobId, out ServerScheduleJob job))
            { record.Fingerprint = Fingerprint(_settings, job); record.GameTime = job.UseGameTime; record.Maintenance = job.Maintenance; }
            Reset(record, record.GameTime ? gameNow : utcNow); _dirty = true;
        }
    }

    internal void Flush()
    {
        lock (_gate)
        {
            Check(); RejectLinks(_root); RejectLinks(_path);
            if (ResolvePath(_root) != _path)
                throw Invalid("journal filename changed while the scheduler was running; stop the server before renaming it");
            // Even a no-op flush detects removal or administrator edits. Never silently
            // recreate a lost journal or replace edited state with an in-memory copy.
            if (_fileHash != null)
            {
                if (!File.Exists(_path) || Hash(ReadBytes(_path)) != _fileHash)
                    throw Invalid("file changed or disappeared while the scheduler was running; scheduling is suspended");
            }
            else if (File.Exists(_path)) throw Invalid("file appeared during initialization");
            if (!_dirty && _fileHash != null) return;
            byte[] bytes = Encode();
            if (bytes.Length > MaximumFileBytes) throw Invalid("file exceeds 1 MiB");
            string temporary = Path.Combine(_root, ".cron-last-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                RejectLinks(_path);
                if (ResolvePath(_root) != _path)
                    throw Invalid("journal filename changed during a write");
                if (_fileHash == null) File.Move(temporary, _path);
                else File.Replace(temporary, _path, null);
                _fileHash = Hash(bytes); _dirty = false;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public void Dispose()
    { lock (_gate) { if (_disposed) return; _disposed = true; _lease.Dispose(); } }

    private Dictionary<string, Record> Records => _worlds[_worldId];
    private void Check() { if (_disposed) throw new ObjectDisposedException(nameof(ServerScheduleJournal)); }
    private Record Current(ServerScheduleJob job)
    {
        Check();
        if (_settings == null || !Records.TryGetValue(job.Id, out Record record) ||
            !ReferenceEquals(record.ActiveJob, job) && record.Fingerprint != Fingerprint(_settings, job))
            throw Invalid("job definition does not match the journal: " + job.Id);
        return record;
    }
    private static void Reset(Record record, DateTime cursor)
    { record.State = "ready"; record.Cursor = cursor; record.Due = record.CoveredUntil = null; record.ActiveJob = null; }
    private static void Clocks(DateTime utc, DateTime game)
    {
        if (utc.Kind != DateTimeKind.Utc || game.Kind != DateTimeKind.Utc || game < ServerScheduleEngine.GameEpoch)
            throw new ArgumentException("Journal clocks must be UTC, with game time beginning at 2000-01-01.");
    }

    internal static string Fingerprint(ServerScheduleSettings settings, ServerScheduleJob job)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true))
        {
            writer.Write(job.Id); writer.Write(job.Cron); writer.Write(job.Enabled); writer.Write(job.Chance);
            writer.Write(job.UseGameTime); writer.Write(job.CatchUp); writer.Write(job.Maintenance);
            writer.Write(job.UseGameTime ? "UTC" : settings.TimeZone.Id);
            foreach (IReadOnlyList<string> list in new[] { job.Commands, job.GlobalKeys, job.BannedGlobalKeys })
            { writer.Write(list.Count); foreach (string value in list) writer.Write(value); }
        }
        return Hash(stream.ToArray());
    }

    private byte[] Encode()
    {
        YamlMappingNode worlds = new();
        foreach (KeyValuePair<string, Dictionary<string, Record>> world in _worlds.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            YamlMappingNode jobs = new();
            foreach (Record record in world.Value.Values.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                YamlMappingNode job = new();
                job.Add("fingerprint", record.Fingerprint); job.Add("state", record.State);
                job.Add("gameTime", record.GameTime ? "true" : "false"); job.Add("cursor", Date(record.Cursor));
                job.Add("maintenance", record.Maintenance ? "true" : "false");
                if (record.Due.HasValue) job.Add("due", Date(record.Due.Value));
                if (record.CoveredUntil.HasValue) job.Add("coveredUntil", Date(record.CoveredUntil.Value));
                jobs.Add(record.Id, job);
            }
            worlds.Add(world.Key, jobs);
        }
        YamlMappingNode root = new(); root.Add("version", "1"); root.Add("worlds", worlds);
        StringWriter writer = new(CultureInfo.InvariantCulture);
        writer.WriteLine("# Automatic scheduler journal. Stop the server before reviewing this file.");
        writer.WriteLine("# needs_review blocks automatic retries. Do not delete this file to retry interrupted work.");
        new YamlStream(new YamlDocument(root)).Save(writer, false);
        return new UTF8Encoding(false, true).GetBytes(writer.ToString());
    }

    private void Load(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw Invalid("invalid UTF-8"); }
        YamlStream stream = new();
        try { using StringReader reader = new(text); stream.Load(new SafeParser(reader)); }
        catch (YamlException) { throw Invalid("invalid YAML"); }
        catch (ArgumentException) { throw Invalid("invalid YAML structure"); }
        if (stream.Documents.Count != 1) throw Invalid("one YAML document is required");
        Dictionary<string, YamlNode> root = Map(stream.Documents[0].RootNode, "version", "worlds");
        if (Scalar(Required(root, "version")) != "1") throw Invalid("unsupported journal version");
        if (Required(root, "worlds") is not YamlMappingNode worlds || worlds.Children.Count > MaximumWorlds)
            throw Invalid("invalid or oversized worlds mapping");
        foreach (KeyValuePair<YamlNode, YamlNode> world in worlds.Children)
        {
            string worldId = Scalar(world.Key);
            if (worldId.Length > 80 || world.Value is not YamlMappingNode jobs || jobs.Children.Count > MaximumRecordsPerWorld)
                throw Invalid("invalid or oversized world record");
            Dictionary<string, Record> records = new(StringComparer.Ordinal);
            foreach (KeyValuePair<YamlNode, YamlNode> entry in jobs.Children)
            {
                string id = Scalar(entry.Key);
                if (id.Length > 64 || id.Any(c => !(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' ||
                    c >= '0' && c <= '9' || c == '_' || c == '-' || c == '.'))) throw Invalid("invalid job ID");
                Dictionary<string, YamlNode> fields = Map(entry.Value, "fingerprint", "state", "gameTime", "maintenance", "cursor", "due", "coveredUntil");
                string fingerprint = Scalar(Required(fields, "fingerprint")), state = Scalar(Required(fields, "state"));
                if (fingerprint.Length != 64 || fingerprint.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) ||
                    state != "ready" && state != "pending" && state != "running" && state != "needs_review") throw Invalid("invalid job state or fingerprint");
                string game = Scalar(Required(fields, "gameTime"));
                if (game != "true" && game != "false") throw Invalid("invalid gameTime flag");
                string maintenance = Scalar(Required(fields, "maintenance"));
                if (maintenance != "true" && maintenance != "false") throw Invalid("invalid maintenance flag");
                Record record = new() { Id = id, Fingerprint = fingerprint, State = state, GameTime = game == "true",
                    Maintenance = maintenance == "true", Cursor = ParseDate(Required(fields, "cursor")) };
                if (fields.TryGetValue("due", out YamlNode due)) record.Due = ParseDate(due);
                if (fields.TryGetValue("coveredUntil", out YamlNode covered)) record.CoveredUntil = ParseDate(covered);
                if (state == "ready" ? record.Due.HasValue || record.CoveredUntil.HasValue :
                    !record.Due.HasValue || !record.CoveredUntil.HasValue || record.Due.Value > record.CoveredUntil.Value)
                    throw Invalid("inconsistent occurrence state");
                if (record.GameTime && (record.Cursor < ServerScheduleEngine.GameEpoch || record.Due < ServerScheduleEngine.GameEpoch))
                    throw Invalid("invalid game clock");
                if (records.ContainsKey(id)) throw Invalid("duplicate job ID"); records.Add(id, record);
            }
            if (_worlds.ContainsKey(worldId)) throw Invalid("duplicate world ID"); _worlds.Add(worldId, records);
        }
    }

    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static DateTime ParseDate(YamlNode node)
    {
        if (!DateTime.TryParseExact(Scalar(node), "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime value)) throw Invalid("invalid UTC timestamp");
        return value;
    }
    private static Dictionary<string, YamlNode> Map(YamlNode node, params string[] keys)
    {
        if (node is not YamlMappingNode map) throw Invalid("expected a mapping");
        Dictionary<string, YamlNode> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in map.Children)
        {
            string key = Scalar(entry.Key);
            if (!keys.Contains(key) || result.ContainsKey(key)) throw Invalid("unknown or duplicate field"); result.Add(key, entry.Value);
        }
        return result;
    }
    private static YamlNode Required(Dictionary<string, YamlNode> map, string key) =>
        map.TryGetValue(key, out YamlNode value) ? value : throw Invalid("missing " + key);
    private static string Scalar(YamlNode node)
    {
        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value) ||
            scalar.Value!.Length > 256 || scalar.Value.Any(char.IsControl)) throw Invalid("invalid scalar");
        return scalar.Value;
    }
    private static byte[] ReadBytes(string path)
    {
        RejectLinks(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw Invalid("file exceeds 1 MiB");
        using MemoryStream buffer = new(); byte[] chunk = new byte[8192]; int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) != 0)
        { if (buffer.Length + read > MaximumFileBytes) throw Invalid("file exceeds 1 MiB"); buffer.Write(chunk, 0, read); }
        return buffer.ToArray();
    }
    private static string Hash(byte[] bytes)
    { using SHA256 sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static InvalidDataException Invalid(string reason) => new(FileName + ": " + reason + ".");
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid("symbolic links and junctions are not supported"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private sealed class SafeParser : IParser
    {
        private readonly Parser _inner;
        private int _count, _depth;
        internal SafeParser(TextReader reader) => _inner = new Parser(reader);
        public ParsingEvent? Current => _inner.Current;
        public bool MoveNext()
        {
            if (!_inner.MoveNext()) return false;
            if (++_count > 80000) throw Invalid("too many YAML nodes");
            if (Current is AnchorAlias || Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw Invalid("YAML aliases, anchors and explicit tags are not supported");
            if (Current is MappingStart || Current is SequenceStart)
            { if (++_depth > 5) throw Invalid("YAML nesting is too deep"); }
            else if (Current is MappingEnd || Current is SequenceEnd) --_depth;
            return true;
        }
    }
}
