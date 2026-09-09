using System;
using System.IO;
using BepInEx.Logging;

namespace ServerManager;

/// <summary>
/// Resolves and owns the server-side persistent data root. Server state is bound
/// only after Valheim has applied -savedir. Clients may resolve the same local
/// root for expendable logo caches without binding server settings or storage.
/// </summary>
internal static class ServerDataRoot
{
    private static readonly StringComparison PathComparison =
        Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    private static readonly object Gate = new();
    private static string? _activePath;

    internal static string ActivePath
    {
        get
        {
            lock (Gate)
            {
                return _activePath ?? throw new InvalidOperationException(
                    "The ServerManager server data root has not been bound yet.");
            }
        }
    }

    internal static string ResolveCurrentPath()
    {
        string saveRoot = Utils.GetSaveDataPath(FileHelpers.FileSource.Local);
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            throw new InvalidOperationException(
                "Valheim did not expose a local persistent save-data path.");
        }

        string fullSaveRoot = NormalizeDirectoryPath(saveRoot);
        string dataRoot = NormalizeDirectoryPath(
            Path.Combine(fullSaveRoot, ServerManagerPlugin.ModName));
        if (!IsStrictDescendant(dataRoot, fullSaveRoot))
        {
            throw new InvalidDataException(
                "The resolved ServerManager data root escaped Valheim's save-data path.");
        }

        return dataRoot;
    }

    internal static void BindAndPrepare(ManualLogSource log)
    {
        if (log == null)
        {
            throw new ArgumentNullException(nameof(log));
        }

        string resolved = ResolveCurrentPath();
        lock (Gate)
        {
            if (_activePath != null)
            {
                if (!PathsEqual(_activePath, resolved))
                {
                    throw new InvalidOperationException(
                        "Valheim's save-data path changed while a ServerManager data root " +
                        "was already active.");
                }

                return;
            }

            EnsureRootDirectory(resolved);
            _activePath = resolved;
        }

        log.LogInfo("ServerManager data root: " + resolved);
    }

    internal static void Release()
    {
        lock (Gate)
        {
            _activePath = null;
        }
    }

    private static void EnsureRootDirectory(string path)
    {
        if (File.Exists(path))
        {
            throw new InvalidDataException(
                "The ServerManager data-root path is a file: " + path);
        }

        Directory.CreateDirectory(path);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The ServerManager data root is not a regular directory: " + path);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            NormalizeDirectoryPath(left),
            NormalizeDirectoryPath(right),
            PathComparison);
    }

    private static bool IsStrictDescendant(string candidate, string parent)
    {
        string canonicalCandidate = NormalizeDirectoryPath(candidate);
        string parentPrefix = GetDirectoryPrefix(parent);
        return canonicalCandidate.StartsWith(parentPrefix, PathComparison);
    }

    private static string GetDirectoryPrefix(string directory)
    {
        string normalized = NormalizeDirectoryPath(directory);
        if (normalized.Length != 0 &&
            IsDirectorySeparator(normalized[normalized.Length - 1]))
        {
            return normalized;
        }

        return normalized + Path.DirectorySeparatorChar;
    }

    private static string NormalizeDirectoryPath(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        string pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
        int length = fullPath.Length;
        while (length > pathRoot.Length &&
               IsDirectorySeparator(fullPath[length - 1]))
        {
            length--;
        }

        return length == fullPath.Length
            ? fullPath
            : fullPath.Substring(0, length);
    }

    private static bool IsDirectorySeparator(char value)
    {
        return value == Path.DirectorySeparatorChar ||
               value == Path.AltDirectorySeparatorChar;
    }
}
