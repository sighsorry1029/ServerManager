using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ServerManager.Configuration;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ServerManager.Commands;

// Only the server reads YAML. A resolved, bounded JSON object (never a preset ID)
// crosses the existing admin-command transport and is decoded again by the client.
internal sealed class ItemDataPresets
{
    internal const string FileName = "Itemdata.yml";
    internal const int MaximumFileBytes = 256 * 1024;
    internal const int MaximumPresets = 256;
    internal const int MaximumKeys = 128;
    internal const int MaximumIdCharacters = 64;
    internal const int MaximumKeyCharacters = 256;
    internal const int MaximumPayloadCharacters = 4096;
    internal const int MaximumPayloadBytes = 8192;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex BasedNumber = new(@"^[+-]?0[xob][0-9a-fA-F_]+$", RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, string> _payloads;

    private ItemDataPresets(Dictionary<string, string> payloads) =>
        _payloads = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(payloads, StringComparer.Ordinal));

    internal bool TryGetPayload(string id, out string payload)
    {
        payload = string.Empty;
        return id != null && _payloads.TryGetValue(id, out payload);
    }

    internal static ItemDataPresets Parse(string yaml)
    {
        CheckUtf8Size(yaml, MaximumFileBytes, MaximumFileBytes, "file exceeds 256 KiB or is not valid UTF-8");
        YamlStream stream = new();
        try
        {
            using StringReader reader = new(yaml);
            stream.Load(new BoundedYamlParser(
                reader,
                MaximumPresets * (MaximumKeys * 2 + 10) + 8,
                3,
                Invalid));
        }
        catch (YamlException exception)
        {
            // Never echo parser tokens or operator-controlled data into logs.
            throw Invalid("invalid YAML syntax or duplicate key at line " +
                exception.Start.Line.ToString(CultureInfo.InvariantCulture) + ", column " +
                exception.Start.Column.ToString(CultureInfo.InvariantCulture));
        }
        catch (ArgumentException) { throw Invalid("invalid YAML structure or duplicate key"); }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlSequenceNode sequence)
            throw Invalid("exactly one YAML document with a root sequence is required");
        if (sequence.Children.Count > MaximumPresets) throw Invalid("at most 256 presets are allowed");
        Dictionary<string, string> payloads = new(StringComparer.Ordinal);
        foreach (YamlNode entry in sequence.Children)
        {
            if (entry is not YamlMappingNode preset || preset.Children.Count != 2)
                throw Invalid("each preset requires exactly id and CustomData");
            string? id = null;
            Dictionary<string, string>? data = null;
            foreach (KeyValuePair<YamlNode, YamlNode> pair in preset.Children)
            {
                string property = StringValue(pair.Key);
                if (property == "id" && id == null) id = StringValue(pair.Value);
                else if (property == "CustomData" && data == null) data = ReadData(pair.Value);
                else throw Invalid("each preset requires exactly id and CustomData (case-sensitive, without duplicates)");
            }
            if (id == null || data == null || !IsValidId(id))
                throw Invalid("id must contain 1-64 ASCII letters, digits, underscores or hyphens");
            if (payloads.ContainsKey(id)) throw Invalid("duplicate preset id");
            payloads.Add(id, EncodeData(data));
        }
        return new ItemDataPresets(payloads);
    }

    internal static bool IsValidId(string id)
    {
        if (id == null || id.Length == 0 || id.Length > MaximumIdCharacters) return false;
        foreach (char value in id)
            if (!(value >= 'a' && value <= 'z') && !(value >= 'A' && value <= 'Z') &&
                !(value >= '0' && value <= '9') && value != '_' && value != '-') return false;
        return true;
    }

    private static Dictionary<string, string> ReadData(YamlNode node)
    {
        if (node is not YamlMappingNode mapping || mapping.Children.Count > MaximumKeys)
            throw Invalid("CustomData must be a mapping with at most 128 string keys");
        Dictionary<string, string> data = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            string key = StringValue(pair.Key), value = StringValue(pair.Value);
            ValidatePair(key, value);
            if (data.ContainsKey(key)) throw Invalid("duplicate CustomData key");
            data.Add(key, value);
        }
        return data;
    }

    private static string StringValue(YamlNode node)
    {
        if (node is not YamlScalarNode scalar || scalar.Value == null)
            throw Invalid("id, property names and CustomData keys/values must be strings");
        string value = scalar.Value;
        if (scalar.Style == ScalarStyle.Plain &&
            (value.Length == 0 || value == "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
             value.TrimStart('+', '-').Equals(".inf", StringComparison.OrdinalIgnoreCase) ||
             value.Equals(".nan", StringComparison.OrdinalIgnoreCase) ||
             double.TryParse(value.Replace("_", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
             BasedNumber.IsMatch(value)))
            throw Invalid("ambiguous null, boolean or numeric scalars must be quoted as strings");
        return value;
    }

    internal static string EncodeData(IEnumerable<KeyValuePair<string, string>> data)
    {
        if (data == null) throw Invalid("CustomData is missing");
        StringBuilder json = new("{");
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in data)
        {
            if (keys.Count >= MaximumKeys) throw Invalid("CustomData exceeds 128 keys");
            ValidatePair(pair.Key, pair.Value);
            if (!keys.Add(pair.Key)) throw Invalid("duplicate CustomData key");
            if (keys.Count != 1) json.Append(',');
            AppendString(json, pair.Key);
            json.Append(':');
            AppendString(json, pair.Value);
            if (json.Length >= MaximumPayloadCharacters) throw Invalid("encoded CustomData exceeds 4096 characters");
        }
        json.Append('}');
        string payload = json.ToString();
        ValidatePayload(payload);
        return payload;
    }

    // A deliberately small strict JSON grammar: object<string,string> only.
    // General deserializers often accept comments, single quotes, duplicate keys
    // or trailing commas. This path rejects all of those, and never creates types.
    internal static Dictionary<string, string> DecodeData(string payload)
    {
        ValidatePayload(payload);
        return new DataReader(payload).Read();
    }

    private static void ValidatePair(string key, string value)
    {
        if (key == null || value == null) throw Invalid("CustomData keys/values cannot be null");
        if (key.Length > MaximumKeyCharacters) throw Invalid("CustomData keys exceed 256 characters");
        CheckUtf8Size(key, MaximumKeyCharacters, MaximumPayloadBytes, "CustomData key is not valid UTF-8");
        CheckUtf8Size(value, MaximumPayloadCharacters, MaximumPayloadBytes, "CustomData value is too large or is not valid UTF-8");
    }

    private static void ValidatePayload(string payload) => CheckUtf8Size(payload, MaximumPayloadCharacters,
        MaximumPayloadBytes, "encoded CustomData exceeds 4096 characters/8192 UTF-8 bytes or is not valid UTF-8");

    private static void CheckUtf8Size(string text, int maximumCharacters, int maximumBytes, string reason)
    {
        if (text == null || text.Length > maximumCharacters) throw Invalid(reason);
        try { if (Utf8.GetByteCount(text) > maximumBytes) throw Invalid(reason); }
        catch (EncoderFallbackException) { throw Invalid(reason); }
    }

    private static void AppendString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (char item in value)
        {
            switch (item)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    // Keep the transported object single-line, including Unicode
                    // NEL/line/paragraph separators and other control characters.
                    if (char.IsControl(item) || item == '\u2028' || item == '\u2029')
                        json.Append("\\u").Append(((int)item).ToString("x4", CultureInfo.InvariantCulture));
                    else json.Append(item);
                    break;
            }
            if (json.Length >= MaximumPayloadCharacters) throw Invalid("encoded CustomData exceeds 4096 characters");
        }
        json.Append('"');
    }

    private sealed class DataReader
    {
        private readonly string _text;
        private int _offset;
        internal DataReader(string text) => _text = text;
        internal Dictionary<string, string> Read()
        {
            Dictionary<string, string> result = new(StringComparer.Ordinal);
            Require('{');
            if (!Take('}'))
            {
                while (true)
                {
                    if (result.Count >= MaximumKeys) throw Invalid("CustomData exceeds 128 keys");
                    string key = ReadString();
                    Require(':');
                    string value = ReadString();
                    ValidatePair(key, value);
                    if (result.ContainsKey(key)) throw Invalid("duplicate CustomData key");
                    result.Add(key, value);
                    if (Take('}')) break;
                    Require(',');
                }
            }
            WhiteSpace();
            if (_offset != _text.Length) throw Invalid("trailing JSON data is not supported");
            return result;
        }

        private string ReadString()
        {
            Require('"');
            StringBuilder result = new();
            while (_offset < _text.Length)
            {
                char value = _text[_offset++];
                if (value == '"') return result.ToString();
                if (value < ' ') throw Invalid("unescaped JSON control character");
                if (value != '\\') { result.Append(value); continue; }
                if (_offset >= _text.Length) throw Invalid("incomplete JSON escape");
                switch (_text[_offset++])
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (_offset + 4 > _text.Length) throw Invalid("incomplete JSON Unicode escape");
                        int character = 0;
                        for (int index = 0; index < 4; ++index)
                        {
                            char hex = _text[_offset++];
                            int digit = hex >= '0' && hex <= '9' ? hex - '0' :
                                hex >= 'a' && hex <= 'f' ? hex - 'a' + 10 :
                                hex >= 'A' && hex <= 'F' ? hex - 'A' + 10 : -1;
                            if (digit < 0) throw Invalid("invalid JSON Unicode escape");
                            character = character * 16 + digit;
                        }
                        result.Append((char)character);
                        break;
                    default: throw Invalid("unsupported JSON escape");
                }
            }
            throw Invalid("unterminated JSON string");
        }

        private bool Take(char value)
        {
            WhiteSpace();
            if (_offset >= _text.Length || _text[_offset] != value) return false;
            ++_offset;
            return true;
        }
        private void Require(char value)
        { if (!Take(value)) throw Invalid("CustomData must be a strict JSON object of string keys/values"); }
        private void WhiteSpace()
        {
            while (_offset < _text.Length && (_text[_offset] == ' ' || _text[_offset] == '\t' ||
                   _text[_offset] == '\r' || _text[_offset] == '\n')) ++_offset;
        }
    }

    internal static string ReadFile(string dataRoot, bool createIfMissing)
    {
        string root = Path.GetFullPath(dataRoot), path = Path.Combine(root, FileName);
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
                    byte[] example = Utf8.GetBytes(DefaultYaml);
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
            while (count < bytes.Length && (read = input.Read(bytes, count, bytes.Length - count)) > 0) count += read;
        }
        if (count > MaximumFileBytes) throw Invalid("file exceeds 256 KiB");
        try
        {
            string text = Utf8.GetString(bytes, 0, count);
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }
        catch (DecoderFallbackException) { throw Invalid("file must be valid UTF-8"); }
    }

    private static void RejectLinks(string root, string path)
    {
        // Refuse symbolic links/junctions for both the file and existing ancestors.
        // FileMode.CreateNew additionally avoids replacing an operator's file.
        for (DirectoryInfo? directory = new(root); directory != null; directory = directory.Parent)
            RejectLink(directory.FullName);
        RejectLink(path);
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw Invalid("symbolic links and reparse points are not supported");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal static InvalidDataException Invalid(string reason) => new(FileName + ": " + reason + ".");

    internal const string DefaultYaml = @"# ServerManager custom item-data presets. Keep this UTF-8 file on the SERVER only.
# Exact, case-sensitive id and CustomData properties; IDs use 1-64 ASCII letters,
# digits, underscores or hyphens. Duplicate IDs/keys and unknown properties fail.
# CustomData is copied exactly: string keys and values, including empty strings.
# Quote keys/values (especially empty, numeric, true, false or null text).
# Copy a logged item block and replace its first line with '- id: restoringnow'.
# Replace [] with the example list, then edit it; [] defines no presets.
# - id: restoringnow
#   CustomData:
#     ""example.mod/key"": ""example value""
#     ""empty"": """"
# F5/server console: sm:giveitem halla ShieldWood 1 2 restoringnow
# Discord: /giveitem player:halla prefab:ShieldWood amount:1 quality:2 data_id:restoringnow
# Presets overlay ONLY new items. Prefab, amount and quality come from the command.
# Keys such as locks and origin positions are NOT automatically removed/rewritten.
# Requires empty inventory slots; existing stacks are never merged for preset grants.
# Maximum: 256 KiB/file, 256 presets, 128 keys/preset, 256 characters/key.
# Encoded JSON per preset must fit 4096 characters AND 8192 UTF-8 bytes.
# No anchors, aliases, explicit tags, multiple documents or non-string values.
# Valid changes reload after two matching reads, normally 2-4 seconds.
# Invalid edits/deletion preserve the last valid snapshot; existing files are never rewritten.
[]
";
}

// Server/game-thread lifecycle only. Background workers own captured values and
// perform bounded I/O + parsing; logging and snapshot publication stay on caller.
// Initial loading is asynchronous too: missing/invalid optional presets do not
// prevent the server from starting or admitting players.
internal sealed class ItemDataPresetStore : IDisposable
{
    private static readonly long PollIntervalTicks = 2 * Stopwatch.Frequency;
    private readonly Action<string> _logInfo, _logWarning;
    private string? _dataRoot, _candidateText, _processedText, _lastReadError;
    private Task<ReadResult>? _read;
    private long _nextPoll;
    private int _generation;
    private bool _disposed;
    internal ItemDataPresets? Current { get; private set; }

    internal ItemDataPresetStore(Action<string> logInfo, Action<string> logWarning)
    {
        _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
        _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
    }

    internal bool EnsureLoaded(string dataRoot)
    {
        Tick(dataRoot);
        return Current != null;
    }

    internal void Tick(string dataRoot)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("A server data root is required.", nameof(dataRoot));
        if (!string.Equals(_dataRoot, dataRoot, StringComparison.Ordinal))
        {
            Reset();
            _dataRoot = dataRoot;
            StartRead(true);
            return;
        }
        Task<ReadResult>? pending = _read;
        if (pending != null)
        {
            if (!pending.IsCompleted) return;
            _read = null;
            ReadResult result = pending.GetAwaiter().GetResult();
            if (result.Generation == _generation) Consume(result);
        }
        if (Stopwatch.GetTimestamp() >= _nextPoll) StartRead(false);
    }

    private void StartRead(bool initial)
    {
        _nextPoll = Stopwatch.GetTimestamp() + PollIntervalTicks;
        string root = _dataRoot!;
        string? candidate = _candidateText, processed = _processedText;
        int generation = _generation;
        _read = Task.Run(() => Read(root, generation, initial, candidate, processed));
    }

    private void Consume(ReadResult result)
    {
        if (result.Text == null)
        {
            _candidateText = _processedText = null;
            if (_lastReadError != result.Error) Warn(result.Error!);
            _lastReadError = result.Error;
            return;
        }
        _lastReadError = null;
        if (!string.Equals(_candidateText, result.Text, StringComparison.Ordinal)) _processedText = null;
        _candidateText = result.Text;
        if (!result.Parsed) return;
        _processedText = result.Text;
        if (result.Error != null) { Warn(result.Error); return; }
        bool initial = Current == null;
        Current = result.Presets!;
        _logInfo(initial ? "Itemdata.yml presets loaded." : "Itemdata.yml presets reloaded.");
    }

    private void Warn(string reason) => _logWarning(reason + (Current == null
        ? " Item-data presets are unavailable; waiting for a valid file."
        : " Keeping all active item-data presets; the file was not rewritten."));

    private static ReadResult Read(string root, int generation, bool initial, string? candidate, string? processed)
    {
        string? text = null;
        bool parsed = false;
        try
        {
            text = ItemDataPresets.ReadFile(root, initial);
            parsed = initial || (string.Equals(candidate, text, StringComparison.Ordinal) &&
                !string.Equals(processed, text, StringComparison.Ordinal));
            return new ReadResult(generation, text, parsed, parsed ? ItemDataPresets.Parse(text) : null, null);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            string error = exception is InvalidDataException ? exception.Message :
                "Itemdata.yml could not be read (" + exception.GetType().Name + ").";
            return new ReadResult(generation, text, parsed, null, error);
        }
    }

    internal void Reset()
    {
        ++_generation;
        _dataRoot = _candidateText = _processedText = _lastReadError = null;
        Current = null;
        _read = null; // A retired worker cannot publish into this or another world.
        _nextPoll = 0;
    }

    public void Dispose() { _disposed = true; Reset(); }

    private static bool IsFatal(Exception exception) => exception is OutOfMemoryException ||
        exception is StackOverflowException || exception is AccessViolationException || exception is ThreadAbortException;

    private sealed class ReadResult
    {
        internal readonly int Generation;
        internal readonly string? Text, Error;
        internal readonly bool Parsed;
        internal readonly ItemDataPresets? Presets;
        internal ReadResult(int generation, string? text, bool parsed, ItemDataPresets? presets, string? error)
        { Generation = generation; Text = text; Parsed = parsed; Presets = presets; Error = error; }
    }
}
