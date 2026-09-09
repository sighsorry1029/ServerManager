using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Steamworks;

namespace ServerManager
{
    public static class CharacterNamePolicy
    {
        public const int MinimumLength = 3;
        public const int MaximumLength = 64;

        public static string NormalizeAndValidate(string value)
        {
            if (value == null)
            {
                throw new CharacterProtocolException("The character name is missing.");
            }

            string normalized = value.Normalize(NormalizationForm.FormKC);
            if (!string.Equals(normalized, normalized.Trim(), StringComparison.Ordinal))
            {
                throw new CharacterProtocolException(
                    "Character names may not start or end with whitespace.");
            }

            if (normalized.Length < MinimumLength || normalized.Length > MaximumLength)
            {
                throw new CharacterProtocolException(
                    "The normalized character name must contain between " +
                    MinimumLength.ToString(CultureInfo.InvariantCulture) + " and " +
                    MaximumLength.ToString(CultureInfo.InvariantCulture) + " UTF-16 code units.");
            }

            for (int index = 0; index < normalized.Length; ++index)
            {
                char valueCharacter = normalized[index];
                UnicodeCategory category = char.GetUnicodeCategory(valueCharacter);
                bool accepted =
                    char.IsLetterOrDigit(valueCharacter) ||
                    category == UnicodeCategory.NonSpacingMark ||
                    category == UnicodeCategory.SpacingCombiningMark ||
                    valueCharacter == ' ' ||
                    valueCharacter == '\'' ||
                    valueCharacter == '\u2019' ||
                    valueCharacter == '-' ||
                    valueCharacter == '_';

                if (!accepted || char.IsControl(valueCharacter) || char.IsSurrogate(valueCharacter))
                {
                    throw new CharacterProtocolException(
                        "The character name contains an unsupported character.");
                }
            }

            return normalized;
        }
    }

    internal static class CharacterSteamIdentity
    {
        internal const string AccountPrefix = "steamworks:";

        internal static bool TryParseCanonicalAccountId(
            string accountId,
            out ulong steamIdValue)
        {
            steamIdValue = 0;
            return !string.IsNullOrEmpty(accountId) &&
                   accountId.StartsWith(AccountPrefix, StringComparison.Ordinal) &&
                   TryParseCanonicalSteam64(
                       accountId.Substring(AccountPrefix.Length),
                       out steamIdValue);
        }

        internal static bool TryParseCanonicalSteam64(
            string steamIdText,
            out ulong steamIdValue)
        {
            steamIdValue = 0;
            if (string.IsNullOrEmpty(steamIdText) ||
                steamIdText.Length > 20 ||
                (steamIdText.Length > 1 && steamIdText[0] == '0'))
            {
                return false;
            }

            for (int index = 0; index < steamIdText.Length; ++index)
            {
                if (steamIdText[index] < '0' || steamIdText[index] > '9')
                {
                    return false;
                }
            }

            if (!ulong.TryParse(
                    steamIdText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out steamIdValue) ||
                !string.Equals(
                    steamIdValue.ToString(CultureInfo.InvariantCulture),
                    steamIdText,
                    StringComparison.Ordinal))
            {
                steamIdValue = 0;
                return false;
            }

            CSteamID steamId = new CSteamID(steamIdValue);
            if (!steamId.IsValid() ||
                steamId.GetEAccountType() !=
                    EAccountType.k_EAccountTypeIndividual)
            {
                steamIdValue = 0;
                return false;
            }

            return true;
        }
    }

    public sealed class CharacterPeerIdentityResolver
    {
        public CharacterIdentity ResolveServerPeer(ZNetPeer peer)
        {
            if (peer == null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            ZNet znet = ZNet.instance;
            if (znet == null || !znet.IsServer())
            {
                throw new InvalidOperationException(
                    "Character identities may only be opened by the authoritative server.");
            }

            if (ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
            {
                throw new CharacterProtocolException(
                    "ServerManager character identities require the Steamworks backend.");
            }

            if (peer.m_rpc == null)
            {
                throw new CharacterProtocolException("The peer has no active RPC socket.");
            }

            if (!peer.IsReady())
            {
                throw new CharacterProtocolException(
                    "The peer identity is not ready. Open character sessions after RPC_PeerInfo.");
            }

            // Revalidate the current, final-authenticated reservation rather
            // than inspecting replaceable ServerSync/other mod socket wrappers.
            if (!ServerManagerRuntime.TryResolveActiveDetectionPeer(
                    znet, peer.m_rpc, out ServerPeerIdentity authenticated, out _) ||
                !ReferenceEquals(authenticated.Peer, peer))
            {
                throw new CharacterProtocolException(
                    "ServerManager character identities require the current final-authenticated Steam connection.");
            }

            string accountId = CreateCanonicalAccountId(authenticated.HostId, ZNet.m_onlineBackend);
            string characterName = CharacterNamePolicy.NormalizeAndValidate(authenticated.PlayerName);
            return new CharacterIdentity(accountId, characterName);
        }

        internal static string CreateCanonicalAccountId(
            string transportIdentity,
            OnlineBackendType backend)
        {
            if (backend != OnlineBackendType.Steamworks)
            {
                throw new CharacterProtocolException(
                    "ServerManager account identities require the Steamworks backend.");
            }

            if (string.IsNullOrWhiteSpace(transportIdentity))
            {
                throw new CharacterProtocolException(
                    "The authenticated transport did not expose a stable account identity.");
            }

            string normalized = transportIdentity.Normalize(NormalizationForm.FormKC);
            if (!CharacterSteamIdentity.TryParseCanonicalSteam64(
                    normalized,
                    out ulong steamIdValue))
            {
                throw new CharacterProtocolException(
                    "The authenticated transport did not expose a canonical " +
                    "individual Steam64 identity.");
            }

            return CharacterSteamIdentity.AccountPrefix +
                   steamIdValue.ToString(CultureInfo.InvariantCulture);
        }
    }

    public sealed class CharacterStorageLayout
    {
        internal static readonly StringComparer StorageKeyComparer =
            Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        private static readonly StringComparison PathComparison =
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        public CharacterStorageLayout(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("A character storage root is required.", nameof(rootDirectory));
            }

            RootDirectory = NormalizeDirectory(rootDirectory);
            WriterLeasePath = EnsureContained(
                Path.GetFullPath(Path.Combine(RootDirectory, ".writer-lock-v1")),
                RootDirectory);
        }

        public string RootDirectory { get; }

        internal string WriterLeasePath { get; }

        public void EnsureDirectories()
        {
            EnsureRegularDirectory(RootDirectory, "character storage root");
        }

        public string GetProfilePath(string storageKey)
        {
            ValidateStorageKey(storageKey);
            string fullPath = Path.GetFullPath(
                Path.Combine(GetAccountDirectory(storageKey), storageKey + ".fch"));
            return EnsureContained(fullPath, RootDirectory);
        }

        internal string GetPendingProfilePath(string storageKey)
        {
            ValidateStorageKey(storageKey);
            string fullPath = Path.GetFullPath(
                Path.Combine(GetAccountDirectory(storageKey), storageKey + ".fch.pending"));
            return EnsureContained(fullPath, RootDirectory);
        }

        public string GetAccountDirectory(string storageKey)
        {
            ValidateStorageKey(storageKey);
            int separator = storageKey.IndexOf('_', "Steam_".Length);
            return GetAccountDirectoryForAccount(CharacterSteamIdentity.AccountPrefix +
                storageKey.Substring("Steam_".Length, separator - "Steam_".Length));
        }

        public string GetAccountDirectoryForAccount(string accountId)
        {
            if (!CharacterSteamIdentity.TryParseCanonicalAccountId(accountId, out ulong steamId))
                throw new CharacterStorageException("The account directory requires a canonical individual Steamworks identity.");
            return EnsureContained(Path.GetFullPath(Path.Combine(RootDirectory,
                steamId.ToString(CultureInfo.InvariantCulture))), RootDirectory);
        }

        public static void ValidateStorageKey(string storageKey)
        {
            const string prefix = "Steam_";
            if (string.IsNullOrEmpty(storageKey) ||
                !storageKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new CharacterStorageException(
                    "The storage key must use Steam_<Steam64>_<character-name>.");
            }

            int separator = storageKey.IndexOf('_', prefix.Length);
            if (separator < 0 ||
                !CharacterSteamIdentity.TryParseCanonicalSteam64(
                    storageKey.Substring(prefix.Length, separator - prefix.Length),
                    out _))
            {
                throw new CharacterStorageException(
                    "The storage key must contain a canonical individual Steam64.");
            }

            string name = storageKey.Substring(separator + 1);
            try
            {
                if (string.Equals(
                        CharacterNamePolicy.NormalizeAndValidate(name),
                        name,
                        StringComparison.Ordinal) &&
                    string.Equals(name, name.ToLowerInvariant(), StringComparison.Ordinal))
                    return;
            }
            catch (Exception exception) when (
                exception is CharacterProtocolException || exception is ArgumentException)
            {
                throw new CharacterStorageException(
                    "The storage key contains an invalid character name.", exception);
            }

            throw new CharacterStorageException(
                "The storage key character name must be canonical NFKC text in invariant lowercase.");
        }

        private static string NormalizeDirectory(string directory)
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

        private static string EnsureContained(string fullPath, string rootDirectory)
        {
            string canonicalRoot = NormalizeDirectory(rootDirectory);
            string canonicalPath = Path.GetFullPath(fullPath);
            string rootPrefix = GetDirectoryPrefix(canonicalRoot);
            if (string.Equals(canonicalPath, canonicalRoot, PathComparison) ||
                !canonicalPath.StartsWith(rootPrefix, PathComparison))
            {
                throw new CharacterStorageException(
                    "A generated character storage path escaped the configured root.");
            }

            return canonicalPath;
        }

        private static void EnsureRegularDirectory(string path, string description)
        {
            if (File.Exists(path))
            {
                throw new CharacterStorageException(
                    "The " + description + " is a file instead of a directory.");
            }

            Directory.CreateDirectory(path);
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new CharacterStorageException(
                    "The " + description + " is not a regular directory.");
            }
        }

        private static string GetDirectoryPrefix(string directory)
        {
            if (directory.Length != 0 &&
                IsDirectorySeparator(directory[directory.Length - 1]))
            {
                return directory;
            }

            return directory + Path.DirectorySeparatorChar;
        }

        private static bool IsDirectorySeparator(char value)
        {
            return value == Path.DirectorySeparatorChar ||
                   value == Path.AltDirectorySeparatorChar;
        }
    }

    public sealed class CharacterStorageKeyProvider : IDisposable
    {
        private readonly object _lifetimeLock = new object();
        private readonly FileStream _writerLease;
        private bool _disposed;

        public CharacterStorageKeyProvider(CharacterStorageLayout layout)
        {
            if (layout == null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            layout.EnsureDirectories();
            _writerLease = AcquireWriterLease(layout.WriterLeasePath);
        }

        public string DeriveStorageKey(CharacterIdentity identity)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            lock (_lifetimeLock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(
                        nameof(CharacterStorageKeyProvider));
                }

                return FormatStorageKey(identity);
            }
        }

        // Also used to reserve bounded incoming bytes before a character
        // session exists. Formatting itself does not acquire a writer lease.
        internal static string FormatStorageKey(CharacterIdentity identity)
        {
            ValidateCanonicalIdentity(identity);
            return "Steam_" +
                   identity.AccountId.Substring(CharacterSteamIdentity.AccountPrefix.Length) +
                   "_" + identity.CharacterName.ToLowerInvariant();
        }

        public void Dispose()
        {
            lock (_lifetimeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _writerLease.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        private static FileStream AcquireWriterLease(string path)
        {
            EnsureRegularWriterLeaseIfPresent(path);
            try
            {
                FileStream lease = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);
                try
                {
                    EnsureRegularWriterLeaseIfPresent(path);
                    return lease;
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is NotSupportedException)
            {
                throw new CharacterStorageException(
                    "The character storage root is already owned by another server " +
                    "process or its writer lock cannot be acquired.",
                    exception);
            }
        }

        private static void ValidateCanonicalIdentity(CharacterIdentity identity)
        {
            ValidateCanonicalSteamworksAccount(identity.AccountId);

            string canonicalName;
            try
            {
                canonicalName =
                    CharacterNamePolicy.NormalizeAndValidate(identity.CharacterName);
            }
            catch (CharacterProtocolException exception)
            {
                throw new CharacterStorageException(
                    "The character storage identity has an invalid character name.",
                    exception);
            }
            catch (ArgumentException exception)
            {
                throw new CharacterStorageException(
                    "The character storage identity has invalid Unicode text.",
                    exception);
            }

            if (!string.Equals(
                    canonicalName,
                    identity.CharacterName,
                    StringComparison.Ordinal))
            {
                throw new CharacterStorageException(
                    "The character storage identity name is not canonical NFKC text.");
            }
        }

        private static void ValidateCanonicalSteamworksAccount(string accountId)
        {
            if (!CharacterSteamIdentity.TryParseCanonicalAccountId(
                    accountId,
                    out _))
            {
                throw new CharacterStorageException(
                    "The character storage identity is not a canonical " +
                    "individual Steamworks account.");
            }
        }

        private static void EnsureRegularWriterLeaseIfPresent(string path)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new CharacterStorageException(
                    "The character writer lock path is not a regular file.");
            }
        }

    }

    internal static class CharacterCrypto
    {
        private static readonly char[] HexAlphabet = "0123456789abcdef".ToCharArray();

        public static byte[] Sha256(byte[] value)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(value);
            }
        }

        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; ++index)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        public static byte[] Clone(byte[] value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            byte[] copy = new byte[value.Length];
            Buffer.BlockCopy(value, 0, copy, 0, value.Length);
            return copy;
        }

        public static string ToLowerHex(byte[] value)
        {
            char[] characters = new char[value.Length * 2];
            for (int index = 0; index < value.Length; ++index)
            {
                int byteValue = value[index];
                characters[index * 2] = HexAlphabet[byteValue >> 4];
                characters[index * 2 + 1] = HexAlphabet[byteValue & 15];
            }

            return new string(characters);
        }
    }
}
