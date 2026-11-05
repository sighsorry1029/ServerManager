using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerManager.Configuration;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ServerManager;

// Sink selection only: these immutable snapshots never change event publication,
// commands, moderation, character validation, Discord or schedule execution.
internal sealed class LogSettings
{
    internal const string FileName = "log.yml";
    internal const int MaximumFileBytes = 128 * 1024;
    private static readonly string[] AuditSelectors =
    {
        "server.started", "server.status", "server.saved", "server.announcement",
        "player.connection", "player.death", "boss.killed", "raid.status",
        "command.executed", "moderation.action", "cron.executed", "cron.failed",
        "security.alert", "security.admin_bypass", "character.validation",
        "character.shadow_stalled", "character.revision_observed", "connection.rejected"
    };
    private static readonly string[] PlayerSelectors =
    {
        "player.connection", "player.position", "player.death", "combat.damage",
        "inventory.changed", "inventory.snapshot", "skill.changed", "character.save_rejected"
    };
    private static readonly string[] ChatSelectors =
        { "chat.shout", "chat.normal", "chat.whisper", "chat.clan", "discord.shout" };
    private readonly HashSet<string> _audit, _player, _chat;

    public static LogSettings Defaults { get; } = CreateDefaults();

    private LogSettings(IEnumerable<string> audit, IEnumerable<string> player, IEnumerable<string> chat)
    {
        _audit = new HashSet<string>(audit, StringComparer.Ordinal);
        _player = new HashSet<string>(player, StringComparer.Ordinal);
        _chat = new HashSet<string>(chat, StringComparer.Ordinal);
    }

    private static LogSettings CreateDefaults()
    {
        HashSet<string> audit = new(AuditSelectors, StringComparer.Ordinal);
        audit.Remove("cron.executed");
        return new LogSettings(audit, PlayerSelectors, ChatSelectors);
    }

    public bool AllowsPlayer(string selector) => selector != null && _player.Contains(selector);
    public bool AllowsChat(string kind) => kind != null && _chat.Contains(kind);

    public bool AllowsAudit(string kind, string source, string command, bool failed)
    {
        if (kind == "command.executed")
        {
            if (source != "cron") return _audit.Contains("command.executed");
            // Per-command cron records are not job summaries, even on failure.
            return (command == "schedule" || command == "maintenance") &&
                (_audit.Contains("cron.executed") || failed && _audit.Contains("cron.failed"));
        }
        string? selector = kind switch
        {
            "server.ready" or "server.shutdown" => "server.status",
            "player.first_join" or "player.login" or "player.leave" => "player.connection",
            "raid.started" or "raid.ended" => "raid.status",
            "player.death" or "combat.pvp_kill" => "player.death",
            "character.save_rejected" or "character.validation_observed" => "character.validation",
            "security.detection" or "security.response" => "security.alert",
            "server.started" or "server.saved" or "server.announcement" or "boss.killed" or
                "moderation.action" or "security.admin_bypass" or "character.shadow_stalled" or
                "character.revision_observed" or "connection.rejected" => kind,
            _ => null // Group selectors are not event kinds; unknown events stay out.
        };
        return selector != null && _audit.Contains(selector);
    }

    public static LogSettings Parse(string yaml)
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
            stream.Load(new BoundedYamlParser(reader, 256, 4, Invalid));
        }
        catch (YamlException exception)
        {
            // Do not echo untrusted YAML tokens or parser exception messages.
            throw Invalid("invalid YAML syntax or duplicate key at line " +
                exception.Start.Line.ToString(CultureInfo.InvariantCulture) + ", column " +
                exception.Start.Column.ToString(CultureInfo.InvariantCulture));
        }
        catch (ArgumentException) { throw Invalid("invalid YAML structure or duplicate key"); }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            throw Invalid("exactly one root mapping is required");

        Dictionary<string, YamlNode> root = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            if (entry.Key is not YamlScalarNode scalar ||
                (scalar.Value != "audit" && scalar.Value != "player" && scalar.Value != "chat") ||
                root.ContainsKey(scalar.Value))
                throw Invalid("root contains an unknown or duplicate key");
            root.Add(scalar.Value, entry.Value);
        }
        return new LogSettings(
            ReadSelectors(root, "audit", AuditSelectors, Defaults._audit),
            ReadSelectors(root, "player", PlayerSelectors, Defaults._player),
            ReadSelectors(root, "chat", ChatSelectors, Defaults._chat));
    }

    private static IEnumerable<string> ReadSelectors(Dictionary<string, YamlNode> root, string key,
        string[] allowed, HashSet<string> defaults)
    {
        if (!root.TryGetValue(key, out YamlNode node)) return defaults;
        if (node is not YamlSequenceNode sequence || sequence.Children.Count > allowed.Length)
            throw Invalid(key + " must be a list of supported selectors");
        HashSet<string> supported = new(allowed, StringComparer.Ordinal);
        HashSet<string> selected = new(StringComparer.Ordinal);
        foreach (YamlNode item in sequence.Children)
        {
            if (item is not YamlScalarNode scalar || scalar.Value == null ||
                !supported.Contains(scalar.Value) || !selected.Add(scalar.Value))
                throw Invalid(key + " contains an unknown, duplicate or non-string selector");
        }
        return selected;
    }

    internal static string ReadFile(string root, bool createIfMissing)
    {
        root = Path.GetFullPath(root);
        string path = Path.Combine(root, FileName);
        RejectLinks(root, path);
        if (createIfMissing && !File.Exists(path))
        {
            Directory.CreateDirectory(root);
            RejectLinks(root, path);
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
        RejectLinks(root, path);
        byte[] bytes = new byte[MaximumFileBytes + 1];
        int count = 0;
        using (FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read;
            while (count < bytes.Length && (read = input.Read(bytes, count, bytes.Length - count)) > 0)
                count += read;
        }
        RejectLinks(root, path);
        if (count > MaximumFileBytes) throw Invalid("file exceeds 128 KiB");
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes, 0, count);
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }
        catch (DecoderFallbackException) { throw Invalid("file must be valid UTF-8"); }
    }

    private static void RejectLinks(string root, string file)
    {
        for (DirectoryInfo? directory = new(root); directory != null; directory = directory.Parent)
            Reject(directory.FullName);
        Reject(file);
        static void Reject(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw Invalid("symbolic links and reparse points are not supported");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static InvalidDataException Invalid(string reason) => new("log.yml: " + reason + ".");

    internal const string DefaultYaml = @"# Server-only file-log selection; does not change events, Discord or game behavior.
# UTF-8. Omitted sections use defaults; [] disables that log stream.
# Exact selectors only: no wildcards, duplicates, nested maps, aliases or tags.
# Valid edits reload after two matching reads (normally 2-4 seconds).
# Invalid edits keep the last valid settings; invalid startup uses defaults.
audit:
  - server.started
  - server.status # ready, shutdown
  - server.saved
  - server.announcement
  - player.connection # first join, login, leave
  - player.death # includes PvP kills
  - boss.killed
  - raid.status # start, end
  - command.executed # non-cron commands only
  - moderation.action
  - cron.failed # failed final schedule/maintenance results
  # - cron.executed # all final schedule/maintenance results, including failures
  - security.alert # detection, response
  - security.admin_bypass
  - character.validation # save rejection, validation observation
  - character.shadow_stalled
  - character.revision_observed
  - connection.rejected

player:
  - player.connection
  - player.position
  - player.death
  - combat.damage
  - inventory.changed
  - inventory.snapshot
  - skill.changed
  - character.save_rejected

chat:
  - chat.shout
  - chat.normal
  - chat.whisper
  - chat.clan
  - discord.shout
";
}

// Lifecycle methods run on the server thread. Workers only read/parse captured
// values; publication is atomic and happens in Tick, never in a stale worker.
internal static class LogSettingsRuntime
{
    private static readonly long PollIntervalTicks = 2 * Stopwatch.Frequency;
    private static LogSettings _current = LogSettings.Defaults;
    private static string? _root, _candidateText, _processedText, _lastReadError;
    private static Action<string>? _info, _warning;
    private static Task<ReadResult>? _read;
    private static long _nextPoll;
    private static int _generation;
    public static LogSettings Current => Volatile.Read(ref _current);

    internal static void Start(string root, Action<string> info, Action<string> warning)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A server data root is required.", nameof(root));
        if (info == null) throw new ArgumentNullException(nameof(info));
        if (warning == null) throw new ArgumentNullException(nameof(warning));
        if (string.Equals(_root, root, StringComparison.Ordinal)) return;
        Stop();
        _root = root;
        _info = info;
        _warning = warning;
        _nextPoll = Stopwatch.GetTimestamp() + PollIntervalTicks;
        // Bounded synchronous load precedes startup records. Logging errors must
        // not turn into a server admission policy: defaults remain available.
        Consume(Read(root, _generation, true, null, null), true);
    }

    internal static void Tick()
    {
        if (_root == null) return;
        Task<ReadResult>? pending = _read;
        if (pending != null)
        {
            if (!pending.IsCompleted) return;
            _read = null;
            ReadResult result = pending.GetAwaiter().GetResult();
            if (result.Generation == _generation) Consume(result, false);
        }
        long now = Stopwatch.GetTimestamp();
        if (now < _nextPoll) return;
        _nextPoll = now + PollIntervalTicks;
        string root = _root!;
        string? candidate = _candidateText, processed = _processedText;
        int generation = _generation;
        _read = Task.Run(() => Read(root, generation, false, candidate, processed));
    }

    private static void Consume(ReadResult result, bool initial)
    {
        if (result.Text == null)
        {
            _candidateText = _processedText = null;
            if (_lastReadError != result.Error) Warn(result.Error!, initial);
            _lastReadError = result.Error;
            return;
        }
        _lastReadError = null;
        if (!string.Equals(_candidateText, result.Text, StringComparison.Ordinal)) _processedText = null;
        _candidateText = result.Text;
        if (!result.Parsed) return;
        _processedText = result.Text;
        if (result.Error != null) { Warn(result.Error, initial); return; }
        Volatile.Write(ref _current, result.Settings!);
        _info!(initial ? "log.yml settings loaded." : "log.yml settings reloaded.");
    }

    private static void Warn(string reason, bool initial) => _warning!(reason + (initial
        ? " Using default file-log settings; the server can continue. The existing file was not rewritten."
        : " Keeping all active file-log settings; the file was not rewritten."));

    private static ReadResult Read(string root, int generation, bool initial, string? candidate, string? processed)
    {
        string? text = null;
        bool parsed = false;
        try
        {
            text = LogSettings.ReadFile(root, initial);
            parsed = initial || string.Equals(candidate, text, StringComparison.Ordinal) &&
                !string.Equals(processed, text, StringComparison.Ordinal);
            return new ReadResult(generation, text, parsed, parsed ? LogSettings.Parse(text) : null, null);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            string error = exception is InvalidDataException ? exception.Message :
                "log.yml could not be read (" + exception.GetType().Name + ").";
            return new ReadResult(generation, text, parsed, null, error);
        }
    }

    internal static void Stop()
    {
        ++_generation;
        _root = _candidateText = _processedText = _lastReadError = null;
        _info = _warning = null;
        _read = null; // Retired bounded workers cannot publish or log by themselves.
        _nextPoll = 0;
        Volatile.Write(ref _current, LogSettings.Defaults);
    }

    private static bool IsFatal(Exception exception) => exception is OutOfMemoryException ||
        exception is StackOverflowException || exception is AccessViolationException || exception is ThreadAbortException;

    private sealed class ReadResult
    {
        internal readonly int Generation;
        internal readonly string? Text, Error;
        internal readonly bool Parsed;
        internal readonly LogSettings? Settings;
        internal ReadResult(int generation, string? text, bool parsed, LogSettings? settings, string? error)
        { Generation = generation; Text = text; Parsed = parsed; Settings = settings; Error = error; }
    }
}
