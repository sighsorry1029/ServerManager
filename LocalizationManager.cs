// Adapted from AzumattDev LocalizationManager 1.4.0: embedded English fallback,
// game-language selection, and language/setup refresh hooks. ServerManager keeps
// its own bounded, validated dictionaries and external-file discovery instead
// of changing game translations or creating a separate Harmony patch owner.
// https://github.com/AzumattDev/LocalizationManager
// Upstream LICENSE.txt:
// Copyright 2022 Tykea
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
// of the Software, and to permit persons to whom the Software is furnished to do so.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ServerManager;

internal static class PlayerLocalizer
{
    private const string English = "English";
    private const string ResourcePrefix = "ServerManager.translations.";
    private const int MaximumFileBytes = 256 * 1024;
    private const int MaximumArguments = 32;
    private const int MaximumDirectories = 8192;
    private const int MaximumDirectoryEntries = 100000;
    private const int MaximumDirectoryDepth = 64;
    private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\'
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, MessageTemplate>> Languages = new(StringComparer.Ordinal);
    private static Dictionary<string, MessageTemplate> _english = new(StringComparer.Ordinal);
    private static bool _englishLoaded;

    // The existing plugin PatchAll owns the hooks below. Initialization never
    // installs patches and must be called after the plugin logger is available.
    internal static void Initialize() => ReloadLanguage(English, Paths.BepInExRootPath, Paths.ConfigPath);

    // Presentation callers use this on the game thread. The language-explicit
    // counterpart and formatting do not access any Unity/game singleton.
    internal static string Text(string key, params string[] args) => TextForLanguage(CurrentLanguage(), key, args);

    internal static string TextForLanguage(string language, string key, params string[] args)
    {
        MessageTemplate? template;
        lock (Gate)
        {
            EnsureEnglishLocked();
            language = NormalizeLanguage(language);
            if (!Languages.TryGetValue(language, out Dictionary<string, MessageTemplate>? messages))
            {
                ReloadLanguageLocked(language, Paths.BepInExRootPath, Paths.ConfigPath);
                messages = Languages[language];
            }
            if (key == null || !messages.TryGetValue(key, out template)) return key ?? string.Empty;
        }
        return Render(template.Text, args ?? Array.Empty<string>());
    }

    // Wire messages are checked against the canonical English schema, never an
    // external file. Indices must be contiguous and the supplied arity exact.
    internal static bool IsKnownMessage(string key, int argumentCount)
    {
        lock (Gate)
        {
            EnsureEnglishLocked();
            return key != null && !key.StartsWith("sm_menu_", StringComparison.Ordinal) &&
                !key.StartsWith("sm_event_", StringComparison.Ordinal) &&
                !key.StartsWith("sm_discord_", StringComparison.Ordinal) && argumentCount >= 0 &&
                _english.TryGetValue(key, out MessageTemplate? template) && template.ArgumentCount == argumentCount;
        }
    }

    private static string CurrentLanguage() => NormalizeLanguage(Localization.instance?.GetSelectedLanguage());

    private static void ReloadCurrentLanguage() => ReloadLanguage(CurrentLanguage(), Paths.BepInExRootPath, Paths.ConfigPath);

    // Explicit roots keep tests isolated. Discovery runs only on first use of
    // a language or an existing refresh hook, never on a cached message lookup.
    private static bool ReloadLanguage(string language, string bepinexRoot, string configurationDirectory)
    {
        lock (Gate)
        {
            EnsureEnglishLocked();
            return ReloadLanguageLocked(NormalizeLanguage(language), bepinexRoot, configurationDirectory);
        }
    }

    private static bool ReloadLanguageLocked(string language, string bepinexRoot, string configurationDirectory)
    {
        Dictionary<string, MessageTemplate> merged = new(_english, StringComparer.Ordinal);
        if (!string.Equals(language, English, StringComparison.Ordinal))
        {
            try
            {
                using Stream? resource = typeof(PlayerLocalizer).Assembly.GetManifestResourceStream(ResourcePrefix + language + ".yml");
                if (resource != null) Overlay(merged, ParseTranslations(ReadBoundedText(resource), _english));
            }
            catch (Exception ex) when (IsTranslationError(ex))
            {
                Warn(language, "embedded translation rejected; using English", ex);
            }
        }

        string failure = "external translation rejected; retaining last valid translation";
        try
        {
            string root = Path.GetFullPath(bepinexRoot);
            string config = Path.GetFullPath(configurationDirectory);
            string fileName = "ServerManager." + language + ".yml";
            string overridePath = Path.Combine(config, fileName);
            string? distributed = null;
            bool overrideObserved = false;
            foreach (string path in FindTranslationFiles(root, fileName))
            {
                if (string.Equals(path, overridePath, PathComparison))
                {
                    overrideObserved = true;
                    continue;
                }
                if (distributed != null)
                {
                    failure = "multiple distributed translation files found; keep only one ServerManager." +
                        language + ".yml outside the direct config override; retaining last valid translation";
                    throw new InvalidDataException("Ambiguous external translation.");
                }
                distributed = path;
            }

            // Keep the embedded baseline separate: a bad config after a good
            // pack file must not publish a partially merged first-load table.
            Dictionary<string, MessageTemplate> candidate = new(merged, StringComparer.Ordinal);
            if (distributed != null) Overlay(candidate, ReadTranslationFile(distributed, root));
            if (overrideObserved || TryGetAttributes(overridePath, out _))
            {
                // BepInEx may explicitly configure a separate config root.
                string boundary = IsWithin(overridePath, root) ? root : config;
                // An observed regular file must not silently disappear or turn
                // into a link between discovery and the final candidate read.
                if (overrideObserved || !IsLinkedPath(overridePath, boundary))
                    Overlay(candidate, ReadTranslationFile(overridePath, boundary));
            }
            Languages[language] = candidate;
            return true;
        }
        catch (Exception ex) when (IsTranslationError(ex))
        {
            // No per-key updates escape a failed parse. On first use the
            // fallback is embedded locale + English; later failures keep the
            // complete last successfully loaded dictionary.
            if (!Languages.ContainsKey(language)) Languages.Add(language, merged);
            Warn(language, failure, ex);
            return false;
        }
    }

    private static List<string> FindTranslationFiles(string root, string fileName)
    {
        List<string> matches = new();
        if (!TryGetAttributes(root, out FileAttributes rootAttributes)) return matches;
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0) return matches;
        if ((rootAttributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException("Translation root is not a directory.");
        Stack<(DirectoryInfo Directory, int Depth)> pending = new();
        pending.Push((new DirectoryInfo(root), 0));
        int directories = 1, entries = 0;
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            // Recheck queued directories rather than following a link installed
            // since enumeration. Selected files are checked again before opening.
            if ((File.GetAttributes(current.Directory.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Translation directory became a link during discovery.");
            foreach (FileSystemInfo entry in current.Directory.EnumerateFileSystemInfos())
            {
                if (++entries > MaximumDirectoryEntries)
                    throw new InvalidDataException("Translation discovery entry limit exceeded.");
                if (!IsWithin(entry.FullName, root))
                    throw new InvalidDataException("Translation path escaped its root.");
                FileAttributes attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (++directories > MaximumDirectories || current.Depth >= MaximumDirectoryDepth)
                        throw new InvalidDataException("Translation discovery directory limit exceeded.");
                    pending.Push((new DirectoryInfo(entry.FullName), current.Depth + 1));
                }
                else if (string.Equals(entry.Name, fileName, PathComparison)) matches.Add(entry.FullName);
            }
        }
        return matches;
    }

    private static bool IsWithin(string path, string root) => path.StartsWith(
        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
        PathComparison);

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        attributes = default;
        return false;
    }

    private static bool IsLinkedPath(string path, string root)
    {
        if (!IsWithin(path, root)) throw new InvalidDataException("Translation path escaped its root.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
        DirectoryInfo? directory = new FileInfo(path).Directory;
        while (directory != null)
        {
            if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0) return true;
            if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison)) return false;
            directory = directory.Parent;
        }
        throw new InvalidDataException("Translation path escaped its root.");
    }

    private static Dictionary<string, MessageTemplate> ReadTranslationFile(string path, string root)
    {
        if (IsLinkedPath(path, root)) throw new InvalidDataException("Translation file became a link during reload.");
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ParseTranslations(ReadBoundedText(file), _english);
    }

    private static void EnsureEnglishLocked()
    {
        if (_englishLoaded) return;
        _englishLoaded = true;
        try
        {
            using Stream? resource = typeof(PlayerLocalizer).Assembly.GetManifestResourceStream(ResourcePrefix + English + ".yml");
            if (resource == null) throw new InvalidDataException("Embedded English translation is missing.");
            _english = ParseTranslations(ReadBoundedText(resource), null);
            if (_english.Count == 0) throw new InvalidDataException("Embedded English translation is empty.");
        }
        catch (Exception ex) when (IsTranslationError(ex))
        {
            _english = new Dictionary<string, MessageTemplate>(StringComparer.Ordinal);
            Warn(English, "embedded English translation unavailable", ex);
        }
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language!.Length > 64) return English;
        foreach (char ch in language)
        {
            // Game language identifiers may contain spaces, '-' or '_'. Do
            // not allow a selected language to become a path or resource suffix.
            if (ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z' ||
                ch >= '0' && ch <= '9' || ch == ' ' || ch == '-' || ch == '_') continue;
            return English;
        }
        return language;
    }

    private static string ReadBoundedText(Stream stream)
    {
        byte[] bytes = new byte[MaximumFileBytes + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumFileBytes) throw new InvalidDataException("Translation exceeds 256 KiB.");
        string text = new UTF8Encoding(false, true).GetString(bytes, 0, count);
        return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
    }

    private static Dictionary<string, MessageTemplate> ParseTranslations(string yaml, Dictionary<string, MessageTemplate>? canonical)
    {
        YamlStream stream = new();
        using StringReader reader = new(yaml);
        stream.Load(new TranslationParser(reader));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            throw new InvalidDataException("Translation must be one flat YAML mapping.");

        Dictionary<string, MessageTemplate> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            if (entry.Key is not YamlScalarNode keyNode || !IsTranslationKey(keyNode.Value) ||
                entry.Value is not YamlScalarNode valueNode || !IsStringValue(valueNode))
                throw new InvalidDataException("Translation keys and values must be supported strings.");
            string key = keyNode.Value!;
            MessageTemplate template = ParseTemplate(valueNode.Value!);
            if (canonical != null)
            {
                if (!canonical.TryGetValue(key, out MessageTemplate? expected))
                    throw new InvalidDataException("Translation contains an unknown message key.");
                for (int i = 0; i < MaximumArguments; ++i)
                    if (template.PlaceholderCounts[i] != expected.PlaceholderCounts[i])
                        throw new InvalidDataException("Translation placeholders do not match English.");
            }
            if (result.ContainsKey(key)) throw new InvalidDataException("Translation contains a duplicate key.");
            result.Add(key, template);
        }
        return result;
    }

    private static bool IsTranslationKey(string? key)
    {
        if (key == null || !key.StartsWith("sm_", StringComparison.Ordinal) || key.Length <= 3 || key.Length > 128) return false;
        for (int i = 3; i < key.Length; ++i)
        {
            char ch = key[i];
            if (!(ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' || ch == '_')) return false;
        }
        return true;
    }

    private static bool IsStringValue(YamlScalarNode value)
    {
        if (string.IsNullOrWhiteSpace(value.Value)) return false;
        if (value.Style != ScalarStyle.Plain) return true;
        string text = value.Value!;
        return text != "~" && !string.Equals(text, "null", StringComparison.OrdinalIgnoreCase) &&
            !bool.TryParse(text, out _) && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static MessageTemplate ParseTemplate(string text)
    {
        int[] counts = new int[MaximumArguments];
        int argumentCount = 0;
        for (int i = 0; i < text.Length; ++i)
        {
            char ch = text[i];
            if (ch != '{' && ch != '}') continue;
            if (i + 1 < text.Length && text[i + 1] == ch) { ++i; continue; }
            if (ch == '}') throw new InvalidDataException("Translation contains an unmatched brace.");
            int index = ReadPlaceholder(text, ref i);
            ++counts[index];
            argumentCount = Math.Max(argumentCount, index + 1);
        }
        for (int i = 0; i < argumentCount; ++i)
            if (counts[i] == 0) throw new InvalidDataException("Translation placeholder indices must be contiguous from zero.");
        return new MessageTemplate(text, counts, argumentCount);
    }

    private static int ReadPlaceholder(string text, ref int position)
    {
        int start = ++position;
        int index = 0;
        while (position < text.Length && text[position] >= '0' && text[position] <= '9')
        {
            index = index * 10 + text[position++] - '0';
            if (index >= MaximumArguments || position - start > 2)
                throw new InvalidDataException("Translation placeholder index is unsupported.");
        }
        if (position == start || position >= text.Length || text[position] != '}' ||
            position - start > 1 && text[start] == '0')
            throw new InvalidDataException("Translation placeholders must use simple {0} indices.");
        return index;
    }

    private static string Render(string template, string[] args)
    {
        StringBuilder result = new(template.Length);
        for (int i = 0; i < template.Length; ++i)
        {
            char ch = template[i];
            if (ch == '{' || ch == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == ch)
                {
                    result.Append(ch);
                    ++i;
                    continue;
                }
                int start = i;
                int index = ReadPlaceholder(template, ref i);
                // Arguments are appended once, never parsed for braces, game
                // localization tokens, or rich text. Each sink owns escaping.
                if (index < args.Length) result.Append(args[index] ?? string.Empty);
                else result.Append(template, start, i - start + 1);
            }
            else result.Append(ch);
        }
        return result.ToString();
    }

    private static void Overlay(Dictionary<string, MessageTemplate> target, Dictionary<string, MessageTemplate> overlay)
    {
        foreach (KeyValuePair<string, MessageTemplate> entry in overlay) target[entry.Key] = entry.Value;
    }

    private static bool IsTranslationError(Exception ex) => ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException ||
        ex is YamlException || ex is DecoderFallbackException || ex is ArgumentException || ex is NotSupportedException;

    private static void Warn(string language, string reason, Exception exception) =>
        ServerManagerPlugin.Log?.LogWarning("Player localization (" + language + "): " + reason + " (" + exception.GetType().Name + ").");

    private sealed class MessageTemplate
    {
        internal readonly string Text;
        internal readonly int[] PlaceholderCounts;
        internal readonly int ArgumentCount;
        internal MessageTemplate(string text, int[] placeholderCounts, int argumentCount)
        {
            Text = text;
            PlaceholderCounts = placeholderCounts;
            ArgumentCount = argumentCount;
        }
    }

    private sealed class TranslationParser : IParser
    {
        private readonly Parser _inner;
        private int _events, _depth, _documents;
        internal TranslationParser(TextReader reader) => _inner = new Parser(reader);
        public ParsingEvent? Current => _inner.Current;
        public bool MoveNext()
        {
            if (!_inner.MoveNext()) return false;
            if (++_events > 8192) throw new InvalidDataException("Translation YAML event limit exceeded.");
            ParsingEvent? value = Current;
            if (value is AnchorAlias || value is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw new InvalidDataException("Translation anchors, aliases and explicit tags are unsupported.");
            if (value is DocumentStart && ++_documents > 1)
                throw new InvalidDataException("Translation must contain only one YAML document.");
            if (value is MappingStart || value is SequenceStart)
            {
                if (++_depth > 1) throw new InvalidDataException("Translation must be a flat YAML mapping.");
            }
            else if (value is MappingEnd || value is SequenceEnd) --_depth;
            return true;
        }
    }

    [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
    private static class LanguageChangedPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Localization __instance) => ReloadLanguage(__instance.GetSelectedLanguage(), Paths.BepInExRootPath, Paths.ConfigPath);
    }

    [HarmonyPatch(typeof(FejdStartup), "SetupGui")]
    private static class MenuSetupPatch
    {
        [HarmonyPostfix]
        private static void Postfix() => ReloadCurrentLanguage();
    }
}
