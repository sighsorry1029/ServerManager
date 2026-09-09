using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ServerManager
{
    /// <summary>
    /// Persists raw PlayerProfile payloads as checksum-validated vanilla .fch
    /// files below one canonical root. Runtime envelopes remain in memory only;
    /// paths come exclusively from validated Steam64/name storage keys.
    /// </summary>
    public sealed class CharacterRepository
    {
        private const int AccountLockStripeCount = 64;
        private const int MaximumStoredEntriesToInspect = 100000;
        // One extra entry is a valid crash/prune residue when the configured
        // retention is at its maximum. It must remain inspectable so the next
        // operation can prune back to the configured bound.
        private const int MaximumAdminBackupsToInspect =
            CharacterStorageOptions.MaximumBackupsPerProfile + 1;
        private const string CharacterFilenameSuffix = ".fch";
        private const string CharacterPendingFilenameSuffix = ".fch.pending";
        private const string CharacterBackupFilenameSuffix = ".fch";
        private const string CharacterBackupMarker = ".";
        private const string CharacterBackupTimestampFormat = "yyyy-MM-dd_HH-mm-ss";
        private const int CharacterBackupTimestampLength = 19;
        private const int MaximumBackupFilenameCollision = 9999;
        private const string WriterLockFilename = ".writer-lock-v1";
        private const string RestoreInstructionsFilename = "HowToRestore.txt";
        private const string RestoreInstructionsText = @"ServerManager - Character Restore
=================================
Paths below are relative to ServerManager. Replace mrdot with your character name.
This guide is refreshed at startup. Keep personal notes outside this folder.

To apply restored/imported saves on login, set serverSettings.loadServerCharacterOnJoin
to true in ServerManager.yml before the player reconnects.
Restore changes the CHARACTER ONLY, not the world. Items may be duplicated or lost.
Never replace character files while the server is running: in-memory data takes priority.

1. In-game or server console (recommended; server stays online)
-------------------------------------------------------------
Use the server console or an administrator's F5 console.
Remote in-game administrators must be listed in the server's adminlist.txt.

1) Have the target player log out and stay offline.
2) Run save in the server console. Wait for THIS save's server-log message:
   WorldCharacterCheckpointCompleted ... pending=0
   A save-request reply alone does not mean saving has finished.
3) List backups, then restore the one you want:
   sm:characterbackups mrdot
   sm:characterrestore mrdot <backupId>
4) Wait for restore success, then let the player reconnect.

Copy backupId from the list, without < >. It is not a filename or list number.
For more results: sm:characterbackups mrdot 2
For duplicate names, use Steam64/name: 76561198000000001/mrdot
Command restore requires a valid current save and a backup of the same character.
The replaced save is backed up; normal backup retention still applies.

2. Discord (same preparation as above)
-------------------------------------
Use the connected bot in a configured admin channel, as a listed admin_user_ids user.
These are Discord user IDs, not Steam IDs.

After the player logs out, request a save and wait for the checkpoint message above:
   /rcon command:save
Then use the dedicated slash commands, not RCON, to restore:
   /characterbackups player:mrdot
   /characterrestore player:mrdot backup_id:<backupId>
Let the player reconnect only after restore success.

3. Manual file replacement (server MUST be stopped)
-------------------------------------------------
Use this for a missing/damaged current save, or the local host's own character.

1) Fully stop the server. For a local host, close the host's game too.
2) Copy the entire ServerManager folder to a safe location outside the active folder.
3) In characters/<Steam64>/, COPY a backup of the same account, character and Player ID.
   Rename the copy to the current save's name and replace that file. Example:
   Steam_76561198000000001_mrdot.2026-09-04_23-04-27.fch
   becomes Steam_76561198000000001_mrdot.fch
   Remove the date/time and any .02-style suffix. Keep .fch and the original backup.
4) Start the server, check for storage errors, then let the player connect.

Do not edit file contents, use .fch.pending as a backup, or put extra files in characters.

4. Adding a ServerCharacters or vanilla .fch
------------------------------------------
Stop the server first and back up existing files as in section 3.
Confirm the owner's Steam64: a .fch does not prove account ownership.
Copy the native .fch into characters/<Steam64>/ (create that folder if needed).
Use Steam_<Steam64>_<lowercase-character-name>.fch; there is no import folder.
Example for the character MyHero, owned by 76561198000000001:
   characters/76561198000000001/Steam_76561198000000001_myhero.fch
Match the name inside the file, using language-independent lowercase for the filename.
Do not change the internal name or Player ID. Start the server and check for storage errors.

If restore fails
----------------
- character_busy, shadow_pending or checkpoint_pending: nothing was changed.
  Keep the player offline, finish the save/checkpoint, then retry.
- A timeout does NOT prove the restore failed. Check sm:characterinfo mrdot
  (Discord: /characterinfo player:mrdot) and logs/events-audit.log before retrying.
- restore_unconfirmed: do not retry or let the player reconnect. Preserve files and
  audit logs, investigate, then restart the server to validate storage.

More details: README, character backup and restore sections.
";
        private static readonly StringComparison StoragePathComparison =
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        private static readonly object[] AccountLockStripes =
            CreateAccountLockStripes();

        private readonly CharacterStorageLayout _layout;
        private readonly CharacterStorageOptions _options;
        private readonly ValheimPlayerProfileCodec _profileCodec;
        private readonly ICharacterRevisionValidator _revisionValidator;
        private readonly Func<CharacterIdentity, bool>? _isCharacterCreationAdmin;
        private readonly ConcurrentDictionary<string, object> _profileLocks =
            new ConcurrentDictionary<string, object>(CharacterStorageLayout.StorageKeyComparer);

        internal CharacterRepository(
            CharacterStorageLayout layout,
            CharacterStorageOptions options,
            ValheimPlayerProfileCodec profileCodec,
            ICharacterRevisionValidator revisionValidator,
            Func<CharacterIdentity, bool>? isCharacterCreationAdmin)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _profileCodec =
                profileCodec ?? throw new ArgumentNullException(nameof(profileCodec));
            _revisionValidator = revisionValidator ??
                throw new ArgumentNullException(nameof(revisionValidator));
            _isCharacterCreationAdmin = isCharacterCreationAdmin;

            _options.Validate();
            _layout.EnsureDirectories();
            EnsureRegularNonReparseDirectory(
                _layout.RootDirectory,
                "character storage root");
        }

        public CharacterStoredSnapshot LoadOrCreate(
            CharacterIdentity identity,
            string storageKey,
            Guid sessionId,
            Func<byte[]> createInitialPayload,
            int valheimProfileVersion)
        {
            CharacterEnvelope envelope = LoadOrPrepareInitialCore(
                identity,
                storageKey,
                sessionId,
                createInitialPayload,
                valheimProfileVersion,
                persistIfMissing: true,
                out bool wasMissing);
            return new CharacterStoredSnapshot(envelope, wasMissing);
        }

        internal void ApplyServerSettings(
            ServerSettings settings, CharacterSemanticEvaluator incomingEvaluator)
        {
            if (_revisionValidator is CharacterSemanticRevisionValidator validator)
                validator.ApplyEvaluator(incomingEvaluator);
            _options.MaxCharactersPerAccount = settings.MaxCharactersPerAccount;
            _options.MaxBackups = settings.BackupsPerProfile;
        }

        // No cached entitlement: prepare and final commit independently consult
        // the current server-owned authentication/admin-list state. This only
        // exempts the creation count, never identity or storage validation.
        internal bool HasCharacterCreationQuotaExemption(CharacterIdentity identity)
        {
            if (identity == null) return false;
            try { return _isCharacterCreationAdmin?.Invoke(identity) == true; }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { return false; }
        }

        internal CharacterInitialSnapshotPreparation PrepareInitialSnapshot(
            CharacterIdentity identity,
            string storageKey,
            Guid sessionId,
            Func<byte[]> createInitialPayload,
            int valheimProfileVersion)
        {
            CharacterEnvelope envelope = LoadOrPrepareInitialCore(
                identity,
                storageKey,
                sessionId,
                createInitialPayload,
                valheimProfileVersion,
                persistIfMissing: false,
                out bool wasMissing);
            return new CharacterInitialSnapshotPreparation(
                envelope,
                wasMissing);
        }

        internal CharacterStoredSnapshot FinalizePreparedInitialSnapshot(
            CharacterIdentity identity,
            string storageKey,
            CharacterEnvelope prepared)
        {
            return FinalizePreparedInitialSnapshot(identity, storageKey, prepared, establishedCapture: false);
        }

        internal CharacterStoredSnapshot FinalizePreparedInitialSnapshot(
            CharacterIdentity identity,
            string storageKey,
            CharacterEnvelope prepared,
            bool establishedCapture)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (prepared == null)
            {
                throw new ArgumentNullException(nameof(prepared));
            }

            ValidateAdminStorageTarget(identity, storageKey);
            if (prepared.Kind != CharacterEnvelopeKind.Snapshot ||
                prepared.Revision != 1 ||
                prepared.BaseRevision != 0 ||
                prepared.SessionId == Guid.Empty ||
                !string.Equals(
                    prepared.AccountId,
                    identity.AccountId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    prepared.CharacterName,
                    identity.CharacterName,
                    StringComparison.Ordinal))
            {
                throw new CharacterStorageException(
                    "The prepared initial character snapshot was invalid.");
            }

            object accountLock = GetAccountLock(identity.AccountId);
            lock (accountLock)
            {
                object profileLock =
                    _profileLocks.GetOrAdd(storageKey, _ => new object());
                lock (profileLock)
                {
                    EnsureRegularNonReparseDirectory(
                        _layout.RootDirectory,
                        "character storage root");
                    TryRequireAccountDirectory(_layout.GetAccountDirectory(storageKey));
                    string profilePath = _layout.GetProfilePath(storageKey);
                    string pendingPath = _layout.GetPendingProfilePath(storageKey);
                    if (File.Exists(profilePath) || File.Exists(pendingPath))
                    {
                        throw new CharacterStorageException(
                            "A character primary or pending primary appeared before the prepared " +
                            "first join could be committed.");
                    }

                    if (Directory.Exists(profilePath) || Directory.Exists(pendingPath))
                    {
                        throw new CharacterStorageException(
                            "A character primary path is a directory.");
                    }

                    if (HasBackup(storageKey))
                    {
                        throw new CharacterStorageException(
                            "A character backup exists without its primary. Restore or " +
                            "explicitly remove it before completing the first join.")
                            .WithPlayerMessage("sm_character_recovery_required");
                    }

                    int existingProfileCount =
                        CountProfilesForAccount(identity.AccountId);
                    if (existingProfileCount >= _options.MaxCharactersPerAccount &&
                        !HasCharacterCreationQuotaExemption(identity))
                    {
                        throw new CharacterStorageException(
                            "The account has reached the server character profile quota.")
                            .WithPlayerMessage("sm_character_profile_limit", _options.MaxCharactersPerAccount.ToString(CultureInfo.InvariantCulture));
                    }

                    byte[] encoded = VanillaCharacterFileCodec.Encode(
                        prepared.PayloadUnsafe,
                        _options.MaxPayloadBytes);

                    if (HasBackup(storageKey))
                    {
                        throw new CharacterStorageException(
                            "A character backup appeared before the prepared first join " +
                            "could be committed.")
                            .WithPlayerMessage("sm_character_recovery_required");
                    }

                    string destinationPath = establishedCapture ? profilePath : pendingPath;
                    WriteInitialAtomically(destinationPath, encoded);
                    CharacterEnvelope verified = ReadAndValidateSnapshot(
                        destinationPath,
                        identity,
                        requiresFreshLocalCharacter: !establishedCapture);
                    if (!SnapshotPayloadMatches(verified, prepared))
                    {
                        throw new CharacterStorageException(
                            "The pending first-join character failed disk readback.");
                    }

                    return new CharacterStoredSnapshot(prepared, true);
                }
            }
        }

        private CharacterEnvelope LoadOrPrepareInitialCore(
            CharacterIdentity identity,
            string storageKey,
            Guid sessionId,
            Func<byte[]> createInitialPayload,
            int valheimProfileVersion,
            bool persistIfMissing,
            out bool wasMissing)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (createInitialPayload == null)
            {
                throw new ArgumentNullException(nameof(createInitialPayload));
            }

            ValidateAdminStorageTarget(identity, storageKey);
            if (sessionId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A non-empty session ID is required.",
                    nameof(sessionId));
            }

            object accountLock = GetAccountLock(identity.AccountId);
            lock (accountLock)
            {
                object profileLock =
                    _profileLocks.GetOrAdd(storageKey, _ => new object());
                lock (profileLock)
                {
                    EnsureRegularNonReparseDirectory(
                        _layout.RootDirectory,
                        "character storage root");
                    string profilePath = ResolveExistingProfilePath(
                        identity,
                        storageKey,
                        cleanupCompletedPending: true,
                        out bool requiresFreshLocalCharacter);
                    if (!string.IsNullOrEmpty(profilePath))
                    {
                        wasMissing = false;
                        return ReadAndValidateSnapshot(
                            profilePath,
                            identity,
                            requiresFreshLocalCharacter);
                    }

                    string finalPath = _layout.GetProfilePath(storageKey);
                    string pendingPath = _layout.GetPendingProfilePath(storageKey);
                    if (Directory.Exists(finalPath) || Directory.Exists(pendingPath))
                    {
                        throw new CharacterStorageException(
                            "The character primary path is a directory.");
                    }

                    if (HasBackup(storageKey))
                    {
                        throw new CharacterStorageException(
                            "The character primary is missing while a backup exists. " +
                            "Restore or explicitly remove the backup before creating a profile.")
                            .WithPlayerMessage("sm_character_recovery_required");
                    }

                    int existingProfileCount =
                        CountProfilesForAccount(identity.AccountId);
                    if (existingProfileCount >= _options.MaxCharactersPerAccount &&
                        !HasCharacterCreationQuotaExemption(identity))
                    {
                        throw new CharacterStorageException(
                            "The account has reached the server character profile quota.")
                            .WithPlayerMessage("sm_character_profile_limit", _options.MaxCharactersPerAccount.ToString(CultureInfo.InvariantCulture));
                    }

                    byte[] initialPayload = createInitialPayload();
                    if (initialPayload == null || initialPayload.Length == 0)
                    {
                        throw new CharacterStorageException(
                            "The initial character payload factory returned no data.");
                    }

                    CharacterEnvelope initial = CharacterEnvelope.CreateWithOrigin(
                        CharacterEnvelopeKind.Snapshot,
                        1,
                        0,
                        sessionId,
                        identity,
                        DateTime.UtcNow,
                        valheimProfileVersion,
                        initialPayload,
                        requiresFreshLocalCharacter: !persistIfMissing);
                    if (persistIfMissing)
                    {
                        byte[] encoded = VanillaCharacterFileCodec.Encode(
                            initial.PayloadUnsafe,
                            _options.MaxPayloadBytes);
                        if (HasBackup(storageKey))
                        {
                            throw new CharacterStorageException(
                                "A character backup appeared before initial profile creation.");
                        }

                        WriteInitialAtomically(finalPath, encoded);
                        CharacterEnvelope verified = ReadAndValidateSnapshot(
                            finalPath,
                            identity,
                            requiresFreshLocalCharacter: false);
                        if (!SnapshotPayloadMatches(verified, initial))
                        {
                            throw new CharacterStorageException(
                                "The initial imported character failed disk readback.");
                        }
                    }

                    wasMissing = true;
                    return initial;
                }
            }
        }

        internal void ValidateStorageKeyMappings(CharacterStorageKeyProvider storageKeyProvider)
        {
            if (storageKeyProvider == null) throw new ArgumentNullException(nameof(storageKeyProvider));
            int inspected = 0;
            List<string> completedPendingPaths = new List<string>();
            List<string> completedTemporaryPaths = new List<string>();
            HashSet<string> pendingStorageKeys = new HashSet<string>(CharacterStorageLayout.StorageKeyComparer);
            Dictionary<long, string> primaryStorageKeyByPlayerId = new Dictionary<long, string>();
            Dictionary<string, string> canonicalStorageKeys =
                new Dictionary<string, string>(CharacterStorageLayout.StorageKeyComparer);
            try
            {
                foreach (string accountDirectory in EnumerateAccountDirectories())
                {
                    CountStoredEntry(ref inspected);
                    foreach (string entryPath in EnumerateAccountFiles(accountDirectory))
                    {
                        CountStoredEntry(ref inspected);
                        string filename = Path.GetFileName(entryPath);
                        bool pending = TryParsePendingFilename(filename, out string storageKey);
                        if (pending || TryParsePrimaryFilename(filename, out storageKey))
                        {
                            RegisterCanonicalStorageKey(canonicalStorageKeys, storageKey);
                            long playerId = ValidateStorageKeyMapping(entryPath, storageKeyProvider,
                                storageKey, isBackup: false, requiresFreshLocalCharacter: pending);
                            RegisterStoredPlayerId(primaryStorageKeyByPlayerId, playerId, storageKey);
                            if (!pending) continue;
                            pendingStorageKeys.Add(storageKey);
                            string finalPath = _layout.GetProfilePath(storageKey);
                            if (File.Exists(finalPath))
                            {
                                CharacterIdentity identity = ReadStorageIdentity(finalPath, storageKey);
                                CharacterEnvelope completed = ReadAndValidateSnapshot(finalPath, identity);
                                CharacterEnvelope prepared = ReadAndValidateSnapshot(entryPath, identity, true);
                                ValidateCompletedPendingPair(identity, completed, prepared);
                                completedPendingPaths.Add(entryPath);
                            }
                            else if (Directory.Exists(finalPath))
                                throw new CharacterStorageException("The completed character primary path is a directory.");
                            continue;
                        }
                        if (TryParseBackupFilename(filename, out storageKey))
                        {
                            RegisterCanonicalStorageKey(canonicalStorageKeys, storageKey);
                            ValidateStorageKeyMapping(entryPath, storageKeyProvider, storageKey,
                                isBackup: true, requiresFreshLocalCharacter: false);
                            continue;
                        }
                        if (TryParseManagedTemporaryFilename(filename, out storageKey))
                        {
                            string primaryPath = _layout.GetProfilePath(storageKey);
                            string pendingPath = _layout.GetPendingProfilePath(storageKey);
                            bool targetIsPending = !File.Exists(primaryPath);
                            string target = targetIsPending ? pendingPath : primaryPath;
                            if (!File.Exists(target) || Directory.Exists(target))
                                throw new CharacterStorageException("A managed character temporary file exists without its canonical primary. The temporary file was retained for manual recovery and the store was not opened.");
                            CharacterIdentity identity = ReadStorageIdentity(target, storageKey);
                            ReadAndValidateSnapshot(target, identity, targetIsPending);
                            completedTemporaryPaths.Add(entryPath);
                        }
                    }
                }
                foreach (string storageKey in pendingStorageKeys)
                    if (!File.Exists(_layout.GetProfilePath(storageKey)) && HasBackup(storageKey))
                        throw new CharacterStorageException("A pending character primary cannot coexist with backups without an established primary.");

                // Only clean crash residue after every account/file has passed
                // validation. A failure never deletes potential recovery data.
                foreach (string path in completedPendingPaths)
                    TryDeleteCompletedStorageArtifact(path, "completed pending character primary");
                foreach (string path in completedTemporaryPaths)
                    TryDeleteCompletedStorageArtifact(path, "stale completed character temporary file");
            }
            catch (CharacterStorageException) { throw; }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                throw new CharacterStorageException("The character storage key mappings could not be verified.", exception);
            }
        }

        private IEnumerable<string> EnumerateAccountDirectories()
        {
            EnsureRegularNonReparseDirectory(_layout.RootDirectory, "character storage root");
            int inspected = 0;
            foreach (string path in Directory.EnumerateFileSystemEntries(_layout.RootDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                CountStoredEntry(ref inspected);
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new CharacterStorageException("Character storage entries cannot be links or reparse points.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    ValidateAccountDirectoryPath(path);
                    EnsureRegularNonReparseDirectory(path, "character account directory");
                    yield return path;
                }
                else
                {
                    EnsureRegularNonReparseFile(path, "character root metadata");
                    string filename = Path.GetFileName(path);
                    if (!string.Equals(filename, WriterLockFilename, StringComparison.Ordinal) &&
                        !string.Equals(filename, RestoreInstructionsFilename, StringComparison.Ordinal))
                        throw new CharacterStorageException("The character storage root contains an unexpected file: " + filename + ".");
                }
            }
        }

        private IEnumerable<string> EnumerateAccountFiles(string directory)
        {
            if (!TryRequireAccountDirectory(directory)) yield break;
            int inspected = 0;
            foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                CountStoredEntry(ref inspected);
                EnsureRegularNonReparseFile(path, "character account file");
                ValidateAccountFilePath(path);
                yield return path;
            }
        }

        private void ValidateAccountDirectoryPath(string directory)
        {
            string fullPath = Path.GetFullPath(directory);
            string name = Path.GetFileName(fullPath);
            if (!CharacterSteamIdentity.TryParseCanonicalSteam64(name, out _) ||
                !StoragePathsEqual(Path.GetDirectoryName(fullPath) ?? string.Empty, _layout.RootDirectory) ||
                !StoragePathsEqual(fullPath, _layout.GetAccountDirectoryForAccount(CharacterSteamIdentity.AccountPrefix + name)))
                throw new CharacterStorageException("A character account directory is not a canonical Steam64 child of the storage root.");
        }

        private bool TryRequireAccountDirectory(string directory)
        {
            EnsureRegularNonReparseDirectory(_layout.RootDirectory, "character storage root");
            ValidateAccountDirectoryPath(directory);
            try { File.GetAttributes(directory); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            EnsureRegularNonReparseDirectory(directory, "character account directory");
            return true;
        }

        private string ValidateAccountFilePath(string path)
        {
            string filename = Path.GetFileName(path);
            if (!TryParsePrimaryFilename(filename, out string storageKey) &&
                !TryParsePendingFilename(filename, out storageKey) &&
                !TryParseBackupFilename(filename, out storageKey) &&
                !TryParseManagedTemporaryFilename(filename, out storageKey))
                throw new CharacterStorageException("A character account directory contains an unexpected file: " + filename + ".");
            string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
            if (!StoragePathsEqual(directory, _layout.GetAccountDirectory(storageKey)))
                throw new CharacterStorageException("The character filename Steam64 does not match its account directory.");
            return storageKey;
        }

        // Called once after strict storage validation, while the runtime owns
        // the writer lease. This non-authoritative document needs no temporary
        // files or backup rotation; interrupted writes are repaired next start.
        internal void EnsureRestoreInstructions()
        {
            EnsureRegularNonReparseDirectory(_layout.RootDirectory, "character storage root");
            string path = Path.Combine(_layout.RootDirectory, RestoreInstructionsFilename);
            bool exists;
            try { File.GetAttributes(path); exists = true; }
            catch (FileNotFoundException) { exists = false; }
            byte[] expected = new UTF8Encoding(false, true).GetBytes(RestoreInstructionsText);
            if (exists)
            {
                EnsureRegularNonReparseFile(path, "character restore instructions");
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length == expected.Length)
                    {
                        int offset = 0;
                        byte[] current = new byte[expected.Length];
                        while (offset < current.Length)
                        {
                            int read = stream.Read(current, offset, current.Length - offset);
                            if (read == 0) break;
                            offset += read;
                        }
                        if (offset == expected.Length && CharacterCrypto.FixedTimeEquals(current, expected))
                            return;
                    }
                }
                EnsureRegularNonReparseFile(path, "character restore instructions");
                // Replace the directory entry, not the existing file contents:
                // even an ordinary-file hard link must not let documentation
                // refresh truncate a character or another external file.
                File.Delete(path);
            }
            using (FileStream stream = new FileStream(path,
                       FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(expected, 0, expected.Length);
            }
        }

        private static void CountStoredEntry(ref int inspected)
        {
            if (++inspected > MaximumStoredEntriesToInspect)
            {
                throw new CharacterStorageException(
                    "The character storage contains too many entries to verify safely.");
            }
        }

        private static bool TryParsePrimaryFilename(
            string filename,
            out string storageKey)
        {
            storageKey = string.Empty;
            if (filename == null ||
                filename.Length <= CharacterFilenameSuffix.Length ||
                !filename.EndsWith(
                    CharacterFilenameSuffix,
                    StringComparison.Ordinal))
            {
                return false;
            }

            string candidate = filename.Substring(
                0, filename.Length - CharacterFilenameSuffix.Length);
            if (candidate.IndexOf('.') >= 0) return false;
            try
            {
                CharacterStorageLayout.ValidateStorageKey(candidate);
            }
            catch (CharacterStorageException)
            {
                return false;
            }

            storageKey = candidate;
            return true;
        }

        private static bool TryParsePendingFilename(
            string filename,
            out string storageKey)
        {
            storageKey = string.Empty;
            if (filename == null ||
                filename.Length <= CharacterPendingFilenameSuffix.Length ||
                !filename.EndsWith(
                    CharacterPendingFilenameSuffix,
                    StringComparison.Ordinal))
            {
                return false;
            }

            string candidate = filename.Substring(
                0,
                filename.Length - CharacterPendingFilenameSuffix.Length);
            if (candidate.IndexOf('.') >= 0) return false;
            try
            {
                CharacterStorageLayout.ValidateStorageKey(candidate);
            }
            catch (CharacterStorageException)
            {
                return false;
            }

            storageKey = candidate;
            return true;
        }

        private static bool TryParseBackupFilename(string filename, out string storageKey)
        {
            storageKey = string.Empty;
            if (string.IsNullOrEmpty(filename)) return false;
            int separator = filename.IndexOf('.');
            if (separator <= 0 || filename.Length < separator + 1 +
                CharacterBackupTimestampLength + CharacterBackupFilenameSuffix.Length) return false;
            string candidate = filename.Substring(0, separator);
            try
            {
                ParseAdminBackupFilename(filename, candidate);
                storageKey = candidate;
                return true;
            }
            catch (CharacterStorageException) { return false; }
        }

        private static bool TryParseManagedTemporaryFilename(
            string filename,
            out string storageKey)
        {
            storageKey = string.Empty;
            if (string.IsNullOrEmpty(filename) || filename[0] != '.')
            {
                return false;
            }

            const int guidHexLength = 32;
            int separator = filename.Length - guidHexLength - 1;
            if (separator <= 1 || filename[separator] != '.' ||
                !IsLowerHex(filename.Substring(separator + 1), guidHexLength))
            {
                return false;
            }

            string candidate = filename.Substring(1, separator - 1);
            try
            {
                CharacterStorageLayout.ValidateStorageKey(candidate);
            }
            catch (CharacterStorageException)
            {
                return false;
            }

            storageKey = candidate;
            return true;
        }

        private static bool IsLowerHex(string value, int expectedLength)
        {
            if (value == null || value.Length != expectedLength)
            {
                return false;
            }

            for (int index = 0; index < value.Length; ++index)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StoragePathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StoragePathComparison);
        }

        private static void EnsureRegularNonReparseDirectory(
            string path,
            string description)
        {
            if (File.Exists(path))
            {
                throw new CharacterStorageException(
                    "The " + description + " path is a file.");
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new CharacterStorageException(
                    "The " + description + " is not a directory.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new CharacterStorageException(
                    "The " + description + " cannot be a link or reparse point.");
            }
        }

        private static void EnsureRegularNonReparseFile(
            string path,
            string description)
        {
            if (Directory.Exists(path))
            {
                throw new CharacterStorageException(
                    "The " + description + " path is a directory.");
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new CharacterStorageException(
                    "The " + description + " is not a regular file.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new CharacterStorageException(
                    "The " + description + " cannot be a link or reparse point.");
            }
        }

        private static void EnsureDirectoryForStorageUse(
            string path,
            string description)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new CharacterStorageException(
                    "The " + description + " has no canonical path.");
            }

            if (File.Exists(path))
            {
                throw new CharacterStorageException(
                    "The " + description + " path is a file.");
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            EnsureRegularNonReparseDirectory(path, description);
        }

        /// <summary>Read-only, bounded primary-file enumeration for explicit administration.</summary>
        internal CharacterIdentity[] GetAdminStoredIdentities(
            CharacterStorageKeyProvider storageKeyProvider)
        {
            return GetAdminStoredIdentities(storageKeyProvider, cleanupCompletedPending: true);
        }

        internal CharacterIdentity[] GetAdminStoredIdentities(
            CharacterStorageKeyProvider storageKeyProvider, bool cleanupCompletedPending)
        {
            List<CharacterIdentity> identities = new List<CharacterIdentity>();
            HashSet<string> storageKeys = new HashSet<string>(CharacterStorageLayout.StorageKeyComparer);
            Dictionary<string, string> canonicalKeys = new Dictionary<string, string>(CharacterStorageLayout.StorageKeyComparer);
            int inspected = 0;
            foreach (string directory in EnumerateAccountDirectories())
            {
                CountStoredEntry(ref inspected);
                lock (GetAccountLock(CharacterSteamIdentity.AccountPrefix + Path.GetFileName(directory)))
                {
                    foreach (string path in EnumerateAccountFiles(directory))
                    {
                        CountStoredEntry(ref inspected);
                        string filename = Path.GetFileName(path);
                        bool pending = TryParsePendingFilename(filename, out string storageKey);
                        if (!pending && !TryParsePrimaryFilename(filename, out storageKey)) continue;
                        RegisterCanonicalStorageKey(canonicalKeys, storageKey);
                        CharacterIdentity identity = ReadStorageIdentity(path, storageKey);
                        if (!storageKeys.Add(storageKey))
                        {
                            ResolveExistingProfilePath(identity, storageKey, cleanupCompletedPending, out _);
                            continue;
                        }
                        ValidateStorageKeyMapping(path, storageKeyProvider, storageKey,
                            isBackup: false, requiresFreshLocalCharacter: pending);
                        identities.Add(identity);
                    }
                }
            }
            return identities.ToArray();
        }

        /// <summary>Lists bounded, canonical backup metadata without reading payloads.</summary>
        internal CharacterBackupRecord[] GetAdminBackups(
            CharacterIdentity identity,
            string storageKey)
        {
            ValidateAdminStorageTarget(identity, storageKey);
            lock (GetAccountLock(identity.AccountId))
            {
                object profileLock = _profileLocks.GetOrAdd(storageKey, _ => new object());
                lock (profileLock)
                {
                    return GetAdminBackupsCore(storageKey);
                }
            }
        }

        internal CharacterEnvelope ReadAdminBackup(
            CharacterIdentity identity,
            string storageKey,
            string backupId)
        {
            ValidateAdminStorageTarget(identity, storageKey);
            if (!IsAdminBackupId(backupId))
            {
                throw new CharacterStorageException(
                    "A backup ID must be a 32-character lowercase hexadecimal identifier.");
            }

            lock (GetAccountLock(identity.AccountId))
            {
                object profileLock = _profileLocks.GetOrAdd(storageKey, _ => new object());
                lock (profileLock)
                {
                    try
                    {
                        CharacterBackupRecord? selected = null;
                        foreach (CharacterBackupRecord backup in GetAdminBackupsCore(storageKey))
                        {
                            if (!string.Equals(backup.BackupId, backupId, StringComparison.Ordinal))
                                continue;
                            if (selected != null)
                                throw new CharacterStorageException("The backup ID is ambiguous.");
                            selected = backup;
                        }

                        if (selected == null)
                            throw new CharacterStorageException("The requested character backup was not found.");

                        string path = Path.Combine(
                            _layout.GetAccountDirectory(storageKey),
                            GetAdminBackupFilename(selected));
                        return ReadAndValidateSnapshot(
                            path,
                            identity,
                            requiresFreshLocalCharacter: false,
                            createdUtcOverride: selected.CreatedUtc);
                    }
                    catch (CharacterStorageException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                    {
                        throw new CharacterStorageException(
                            "The character backup could not be read and verified safely.", exception);
                    }
                }
            }
        }

        /// <summary>
        /// Replaces an unchanged existing primary with an already validated admin
        /// snapshot. An uncertain post-replacement result must quarantine the target.
        /// </summary>
        internal string RestoreAdminSnapshot(
            CharacterIdentity identity,
            string storageKey,
            CharacterEnvelope expectedPrimary,
            CharacterEnvelope restoredSnapshot)
        {
            ValidateAdminStorageTarget(identity, storageKey);
            if (expectedPrimary == null)
                throw new ArgumentNullException(nameof(expectedPrimary));
            if (restoredSnapshot == null)
                throw new ArgumentNullException(nameof(restoredSnapshot));
            if (expectedPrimary.Kind != CharacterEnvelopeKind.Snapshot ||
                expectedPrimary.Revision < 1 ||
                expectedPrimary.Revision == long.MaxValue ||
                expectedPrimary.ValheimProfileVersion !=
                    ValheimPlayerProfileCodec.SupportedPlayerProfileVersion ||
                !EnvelopeMatchesIdentity(expectedPrimary, identity) ||
                restoredSnapshot.Kind != CharacterEnvelopeKind.Snapshot ||
                restoredSnapshot.ValheimProfileVersion !=
                    ValheimPlayerProfileCodec.SupportedPlayerProfileVersion ||
                !EnvelopeMatchesIdentity(restoredSnapshot, identity) ||
                restoredSnapshot.BaseRevision != expectedPrimary.Revision ||
                restoredSnapshot.Revision != expectedPrimary.Revision + 1 ||
                restoredSnapshot.SessionId == Guid.Empty)
            {
                throw new CharacterStorageException(
                    "The administrative restore snapshot is structurally invalid.");
            }

            lock (GetAccountLock(identity.AccountId))
            {
                object profileLock = _profileLocks.GetOrAdd(storageKey, _ => new object());
                lock (profileLock)
                {
                    string profilePath = _layout.GetProfilePath(storageKey);
                    bool replacementAttempted = false;
                    string warning = string.Empty;
                    try
                    {
                        EnsureRegularNonReparseDirectory(_layout.RootDirectory, "character storage root");
                        EnsureRegularNonReparseFile(profilePath, "character primary");
                        CharacterEnvelope current = ReadAndValidateSnapshot(
                            profilePath,
                            identity,
                            requiresFreshLocalCharacter: false);
                        if (!SnapshotPayloadMatches(current, expectedPrimary))
                        {
                            throw new CharacterStorageException(
                                "The durable character primary changed before administrative restore.");
                        }

                        // Validate the complete bounded directory before the replacement
                        // creates another backup or ordinary retention deletes any file.
                        GetAdminBackupsCore(storageKey);
                        byte[] encoded = VanillaCharacterFileCodec.Encode(
                            restoredSnapshot.PayloadUnsafe,
                            _options.MaxPayloadBytes);
                        warning = ReplaceAtomicallyWithBackup(
                            profilePath,
                            storageKey,
                            encoded,
                            out replacementAttempted);
                        CharacterEnvelope verified = ReadAndValidateSnapshot(
                            profilePath,
                            identity,
                            requiresFreshLocalCharacter: false);
                        if (!SnapshotPayloadMatches(verified, restoredSnapshot))
                        {
                            throw new CharacterStorageException(
                                "The administrative restore readback did not match the intended snapshot.");
                        }

                        return warning;
                    }
                    catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                    {
                        if (!replacementAttempted)
                        {
                            throw new CharacterStorageException(
                                "The administrative restore failed before replacing the character primary.",
                                exception);
                        }

                        CharacterEnvelope? verified = null;
                        Exception verificationFailure = exception;
                        try
                        {
                            verified = ReadAndValidateSnapshot(
                                profilePath,
                                identity,
                                requiresFreshLocalCharacter: false);
                        }
                        catch (Exception readException) when (!IntegrityCanonical.IsFatal(readException))
                        {
                            verificationFailure = readException;
                        }

                        if (verified != null && SnapshotPayloadMatches(verified, restoredSnapshot))
                        {
                            return (string.IsNullOrEmpty(warning) ? string.Empty : warning + " ") +
                                   "The administrative restore was verified on disk after an error: " +
                                   exception.Message;
                        }

                        if (verified != null && SnapshotPayloadMatches(verified, expectedPrimary))
                        {
                            throw new CharacterStorageException(
                                "The administrative restore failed; the original character primary is unchanged.",
                                exception);
                        }

                        throw new CharacterRestoreUnconfirmedException(
                            "The administrative restore result could not be confirmed. " +
                            "The target must remain quarantined for recovery.",
                            verificationFailure);
                    }
                }
            }
        }

        private static void ValidateAdminStorageTarget(CharacterIdentity identity, string storageKey)
        {
            if (identity == null)
                throw new ArgumentNullException(nameof(identity));
            CharacterStorageLayout.ValidateStorageKey(storageKey);
            if (!CharacterSteamIdentity.TryParseCanonicalAccountId(identity.AccountId, out ulong steamId) ||
                !string.Equals(
                    storageKey,
                    CharacterStorageKeyProvider.FormatStorageKey(identity),
                    StringComparison.Ordinal))
            {
                throw new CharacterStorageException(
                    "The administrative character identity does not match its canonical storage key.");
            }
        }

        private CharacterBackupRecord[] GetAdminBackupsCore(string storageKey)
        {
            try
            {
                List<CharacterBackupRecord> backups = new List<CharacterBackupRecord>();
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                Dictionary<string, string> canonicalKeys = new Dictionary<string, string>(CharacterStorageLayout.StorageKeyComparer);
                foreach (string path in EnumerateAccountFiles(_layout.GetAccountDirectory(storageKey)))
                {
                    string key = ValidateAccountFilePath(path);
                    RegisterCanonicalStorageKey(canonicalKeys, key);
                    string filename = Path.GetFileName(path);
                    if (!TryParseBackupFilename(filename, out string backupKey) ||
                        !string.Equals(backupKey, storageKey, StringComparison.Ordinal)) continue;
                    if (backups.Count >= MaximumAdminBackupsToInspect)
                        throw new CharacterStorageException("The character backup listing exceeds its inspection limit.");
                    CharacterBackupRecord backup = ParseAdminBackupFilename(filename, storageKey);
                    if (!ids.Add(backup.BackupId))
                        throw new CharacterStorageException("A character backup ID is ambiguous.");
                    backups.Add(backup);
                }
                backups.Sort((left, right) => CompareBackupRecords(right, left));
                return backups.ToArray();
            }
            catch (CharacterStorageException) { throw; }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                throw new CharacterStorageException("The character backup directory could not be inspected safely.", exception);
            }
        }

        private static bool IsAdminBackupId(string backupId)
        {
            return IsLowerHex(backupId, 32);
        }

        private static CharacterBackupRecord ParseAdminBackupFilename(
            string filename,
            string storageKey)
        {
            CharacterStorageLayout.ValidateStorageKey(storageKey);
            string identityPrefix = storageKey + CharacterBackupMarker;
            if (string.IsNullOrEmpty(filename) ||
                !filename.StartsWith(identityPrefix, StringComparison.Ordinal) ||
                !filename.EndsWith(CharacterBackupFilenameSuffix, StringComparison.Ordinal) ||
                filename.Length < identityPrefix.Length + CharacterBackupTimestampLength +
                    CharacterBackupFilenameSuffix.Length)
            {
                throw new CharacterStorageException(
                    "A character backup filename is not canonical.");
            }

            string timeAndCollision = filename.Substring(
                identityPrefix.Length,
                filename.Length - identityPrefix.Length - CharacterBackupFilenameSuffix.Length);
            if (timeAndCollision.Length != CharacterBackupTimestampLength &&
                (timeAndCollision.Length < CharacterBackupTimestampLength + 3 ||
                 timeAndCollision.Length > CharacterBackupTimestampLength + 5))
            {
                throw new CharacterStorageException(
                    "A character backup filename is not canonical.");
            }

            if (!DateTime.TryParseExact(
                    timeAndCollision.Substring(0, CharacterBackupTimestampLength),
                    CharacterBackupTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime createdLocal))
            {
                throw new CharacterStorageException(
                    "A character backup filename has an invalid local timestamp.");
            }

            int collisionNumber = 1;
            if (timeAndCollision.Length != CharacterBackupTimestampLength)
            {
                string collisionText = timeAndCollision.Substring(
                    CharacterBackupTimestampLength);
                if (collisionText[0] != '.' ||
                    !int.TryParse(
                        collisionText.Substring(1),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int collision) ||
                    collision < 2 ||
                    collision > MaximumBackupFilenameCollision ||
                    !string.Equals(
                        collisionText,
                        "." + collision.ToString("D2", CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                {
                    throw new CharacterStorageException(
                        "A character backup filename has an invalid collision suffix.");
                }

                collisionNumber = collision;
            }

            string backupId = CreateBackupId(filename);
            return new CharacterBackupRecord(
                backupId,
                DateTime.SpecifyKind(createdLocal, DateTimeKind.Local).ToUniversalTime(),
                filename,
                collisionNumber);
        }

        private static string GetAdminBackupFilename(CharacterBackupRecord backup)
        {
            if (backup == null || string.IsNullOrEmpty(backup.Filename))
            {
                throw new CharacterStorageException(
                    "A character backup record has no canonical filename.");
            }

            return backup.Filename;
        }

        private static string CreateBackupId(string canonicalFilename)
        {
            byte[] fullHash = CharacterCrypto.Sha256(
                new UTF8Encoding(false, true).GetBytes(canonicalFilename));
            byte[] truncated = new byte[16];
            Buffer.BlockCopy(fullHash, 0, truncated, 0, truncated.Length);
            return CharacterCrypto.ToLowerHex(truncated);
        }

        private static int CompareBackupRecords(CharacterBackupRecord left, CharacterBackupRecord right)
        {
            // The filename is the canonical server-local ordering source. UTC
            // conversion can be ambiguous during a daylight-saving fallback.
            int order = StringComparer.Ordinal.Compare(
                GetBackupTimestamp(left),
                GetBackupTimestamp(right));
            if (order == 0)
                order = left.CollisionNumber.CompareTo(right.CollisionNumber);
            return order == 0
                ? StringComparer.Ordinal.Compare(left.Filename, right.Filename)
                : order;
        }

        private static string GetBackupTimestamp(CharacterBackupRecord record)
        {
            int offset = record.Filename.IndexOf(CharacterBackupMarker, StringComparison.Ordinal) +
                CharacterBackupMarker.Length;
            return record.Filename.Substring(offset, CharacterBackupTimestampLength);
        }

        public CharacterStoredSnapshot? Load(
            CharacterIdentity identity,
            string storageKey)
        {
            return Load(identity, storageKey, cleanupCompletedPending: true);
        }

        internal CharacterStoredSnapshot? Load(
            CharacterIdentity identity,
            string storageKey,
            bool cleanupCompletedPending)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            ValidateAdminStorageTarget(identity, storageKey);
            lock (GetAccountLock(identity.AccountId))
            {
                object profileLock = _profileLocks.GetOrAdd(storageKey, _ => new object());
                lock (profileLock)
                {
                    EnsureRegularNonReparseDirectory(
                        _layout.RootDirectory,
                        "character storage root");
                    string profilePath = ResolveExistingProfilePath(
                        identity,
                        storageKey,
                        cleanupCompletedPending: cleanupCompletedPending,
                        out bool requiresFreshLocalCharacter);
                    if (string.IsNullOrEmpty(profilePath))
                    {
                        if (Directory.Exists(_layout.GetProfilePath(storageKey)) ||
                            Directory.Exists(_layout.GetPendingProfilePath(storageKey)))
                        {
                            throw new CharacterStorageException(
                                "The character primary path is a directory.");
                        }

                        return null;
                    }

                    CharacterEnvelope existing =
                        ReadAndValidateSnapshot(
                            profilePath,
                            identity,
                            requiresFreshLocalCharacter);
                    return new CharacterStoredSnapshot(existing, false);
                }
            }
        }

        /// <summary>
        /// Admits a local backup baseline using incoming absolute policy and
        /// the same administrator exemption as gameplay saves. Comparing the
        /// candidate with itself starts a new progression window; offline skill
        /// gains are the selected baseline, not an in-session skill transition.
        /// </summary>
        internal CharacterSemanticValidationResult EvaluateBackupCapture(
            CharacterIdentity identity,
            CharacterEnvelope candidate,
            CharacterValidatedSnapshot validated)
        {
            if (_revisionValidator is CharacterSemanticRevisionValidator validator)
                return validator.EvaluateBackupBaseline(identity, validated.SemanticSnapshot);
            return _revisionValidator.Evaluate(
                identity, candidate, candidate, validated.PlayerId,
                validated.SemanticSnapshot, validated.SemanticSnapshot,
                validated.SemanticSnapshot, TimeSpan.Zero);
        }

        /// <summary>
        /// Validates one client revision against an explicit live authoritative
        /// base without reading or writing the character repository. This is
        /// the admission seam used by the process-local character overlay.
        /// </summary>
        internal RepositoryCommitOutcome EvaluateLiveCandidate(
            CharacterSession session,
            CharacterEnvelope current,
            CharacterEnvelope saveRequest,
            CharacterSemanticSnapshot candidateSnapshot,
            DateTime serverUtc)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            if (saveRequest == null)
            {
                throw new ArgumentNullException(nameof(saveRequest));
            }

            if (candidateSnapshot == null)
            {
                throw new ArgumentNullException(nameof(candidateSnapshot));
            }

            if (serverUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "The candidate timestamp must be UTC.",
                    nameof(serverUtc));
            }

            if (current.Kind != CharacterEnvelopeKind.Snapshot ||
                !string.Equals(
                    current.AccountId,
                    session.Identity.AccountId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    current.CharacterName,
                    session.Identity.CharacterName,
                    StringComparison.Ordinal))
            {
                throw new CharacterStorageException(
                    "The live character base identity is invalid.");
            }

            if (current.Revision != saveRequest.BaseRevision)
            {
                return RepositoryCommitOutcome.Conflict(
                    current,
                    "The save base revision is stale.");
            }

            if (current.Revision == long.MaxValue ||
                saveRequest.Revision != current.Revision + 1)
            {
                return RepositoryCommitOutcome.Rejected(
                    current,
                    "The requested revision is not the next server revision.");
            }

            CharacterEnvelope accepted = CharacterEnvelope.CreateWithOrigin(
                CharacterEnvelopeKind.Snapshot,
                current.Revision + 1,
                current.Revision,
                session.SessionId,
                session.Identity,
                serverUtc,
                saveRequest.ValheimProfileVersion,
                saveRequest.PayloadUnsafe,
                requiresFreshLocalCharacter: saveRequest.RequiresFreshLocalCharacter);

            CharacterSemanticSnapshot skillObservationBaseline;
            TimeSpan skillObservationElapsed;
            session.GetSkillObservationBaseline(
                current.Revision,
                current.PayloadSha256Unsafe,
                out skillObservationBaseline,
                out skillObservationElapsed);
            CharacterSemanticSnapshot currentSemanticSnapshot;
            bool currentSemanticCached =
                session.TryGetCurrentSemanticSnapshot(
                    current.Revision,
                    current.PayloadSha256Unsafe,
                    out currentSemanticSnapshot);
            CharacterSemanticValidationResult semantic =
                _revisionValidator.Evaluate(
                    session.Identity,
                    current,
                    accepted,
                    session.PlayerId,
                    currentSemanticCached
                        ? currentSemanticSnapshot
                        : null,
                    candidateSnapshot,
                    skillObservationBaseline,
                    skillObservationElapsed);
            if (semantic.Rejected)
            {
                return RepositoryCommitOutcome.Rejected(
                    current,
                    semantic.RejectionReason,
                    semantic.Observations,
                    semantic.StatLimitFindings, semantic);
            }

            return RepositoryCommitOutcome.Accepted(
                accepted,
                semantic.Observations,
                currentSemanticCached
                    ? currentSemanticSnapshot
                    : null,
                candidateSnapshot,
                semantic.StatLimitFindings, semantic);
        }

        /// <summary>
        /// Persists one already-admitted live snapshot under account then
        /// character locks. Account-local scans cannot race sibling writes.
        /// An exact target already on disk is accepted as an idempotent retry.
        /// </summary>
        internal string PersistCheckpointEntry(
            CharacterCheckpointEntry entry,
            CharacterEnvelope expectedDurableBase)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (expectedDurableBase == null)
            {
                throw new ArgumentNullException(nameof(expectedDurableBase));
            }

            CharacterStorageLayout.ValidateStorageKey(entry.StorageKey);
            ValidateCheckpointEntry(entry);
            ValidateCheckpointRebase(entry, expectedDurableBase);
            lock (GetAccountLock(entry.Identity.AccountId))
            {
                object profileLock = _profileLocks.GetOrAdd(
                    entry.StorageKey,
                    _ => new object());
                lock (profileLock)
                {
                    EnsureRegularNonReparseDirectory(
                        _layout.RootDirectory,
                        "character storage root");
                    string profilePath = ResolveExistingProfilePath(
                        entry.Identity,
                        entry.StorageKey,
                        cleanupCompletedPending: true,
                        out bool requiresFreshLocalCharacter);
                    if (string.IsNullOrEmpty(profilePath))
                    {
                        throw new CharacterStorageException(
                            "A character primary disappeared before checkpoint commit.");
                    }

                    CharacterEnvelope current = ReadAndValidateSnapshot(
                        profilePath,
                        entry.Identity,
                        requiresFreshLocalCharacter);
                    if (SnapshotPayloadMatches(current, entry.Snapshot))
                    {
                        return string.Empty;
                    }

                    if (!SnapshotPayloadMatches(current, expectedDurableBase))
                    {
                        throw new CharacterStorageException(
                            "The durable character base changed before checkpoint commit.");
                    }

                    if (entry.Snapshot.Revision <= expectedDurableBase.Revision)
                    {
                        throw new CharacterStorageException(
                            "A checkpoint may not replace a character with a stale revision.");
                    }

                    if (requiresFreshLocalCharacter)
                    {
                        if (entry.Snapshot.RequiresFreshLocalCharacter)
                        {
                            throw new CharacterStorageException(
                                "A pending character may only be promoted by its first accepted full save.");
                        }

                        PromotePendingSnapshot(
                            entry.Identity,
                            entry.StorageKey,
                            expectedDurableBase,
                            entry.Snapshot);
                        return string.Empty;
                    }

                    byte[] encodedSnapshot = VanillaCharacterFileCodec.Encode(
                        entry.Snapshot.PayloadUnsafe,
                        _options.MaxPayloadBytes);
                    string warning = ReplaceAtomicallyWithBackup(
                        profilePath,
                        entry.StorageKey,
                        encodedSnapshot);
                    CharacterEnvelope verified = ReadAndValidateSnapshot(
                        profilePath,
                        entry.Identity,
                        requiresFreshLocalCharacter: false);
                    if (!SnapshotPayloadMatches(verified, entry.Snapshot))
                    {
                        throw new CharacterStorageException(
                            "The checkpoint character failed disk readback.");
                    }

                    return warning;
                }
            }
        }

        private static void ValidateCheckpointRebase(
            CharacterCheckpointEntry entry,
            CharacterEnvelope expectedDurableBase)
        {
            if (expectedDurableBase.Kind != CharacterEnvelopeKind.Snapshot ||
                expectedDurableBase.Revision < entry.DurableBase.Revision ||
                expectedDurableBase.Revision > entry.Snapshot.Revision ||
                expectedDurableBase.ValheimProfileVersion !=
                    ValheimPlayerProfileCodec.SupportedPlayerProfileVersion ||
                !EnvelopeMatchesIdentity(expectedDurableBase, entry.Identity) ||
                (expectedDurableBase.Revision == entry.Snapshot.Revision &&
                 !expectedDurableBase.MatchesSnapshot(entry.Snapshot)))
            {
                throw new CharacterStorageException(
                    "A checkpoint durable rebase is structurally invalid.");
            }
        }

        private static void ValidateCheckpointEntry(
            CharacterCheckpointEntry entry)
        {
            CharacterEnvelope durable = entry.DurableBase;
            CharacterEnvelope snapshot = entry.Snapshot;
            if (durable.Kind != CharacterEnvelopeKind.Snapshot ||
                snapshot.Kind != CharacterEnvelopeKind.Snapshot ||
                durable.Revision < 1 ||
                snapshot.Revision < durable.Revision ||
                durable.ValheimProfileVersion !=
                    ValheimPlayerProfileCodec.SupportedPlayerProfileVersion ||
                snapshot.ValheimProfileVersion !=
                    ValheimPlayerProfileCodec.SupportedPlayerProfileVersion ||
                !EnvelopeMatchesIdentity(durable, entry.Identity) ||
                !EnvelopeMatchesIdentity(snapshot, entry.Identity))
            {
                throw new CharacterStorageException(
                    "A checkpoint character entry is structurally invalid.");
            }
        }

        private static bool EnvelopeMatchesIdentity(
            CharacterEnvelope envelope,
            CharacterIdentity identity)
        {
            return string.Equals(
                       envelope.AccountId,
                       identity.AccountId,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       envelope.CharacterName,
                       identity.CharacterName,
                       StringComparison.Ordinal);
        }

        private CharacterEnvelope ReadAndValidateSnapshot(
            string profilePath,
            CharacterIdentity expectedIdentity,
            bool requiresFreshLocalCharacter = false,
            DateTime? createdUtcOverride = null)
        {
            if (expectedIdentity == null)
            {
                throw new ArgumentNullException(nameof(expectedIdentity));
            }

            return ReadSnapshotEnvelope(
                profilePath,
                expectedIdentity,
                requiresFreshLocalCharacter,
                createdUtcOverride);
        }

        private CharacterEnvelope ReadSnapshotEnvelope(
            string profilePath,
            CharacterIdentity identity,
            bool requiresFreshLocalCharacter,
            DateTime? createdUtcOverride = null)
        {
            byte[] encoded = ReadBoundedFile(profilePath);
            byte[] payload = VanillaCharacterFileCodec.Decode(
                encoded,
                _options.MaxPayloadBytes);
            int profileVersion = ReadRawProfileVersion(payload);
            if (profileVersion !=
                ValheimPlayerProfileCodec.SupportedPlayerProfileVersion)
            {
                throw new CharacterStorageException(
                    "The .fch PlayerProfile version is unsupported.")
                    .WithPlayerMessage("sm_character_stored_unavailable");
            }

            try
            {
                _profileCodec.ValidateStoredIdentityHeader(
                    identity,
                    payload);
            }
            catch (CharacterProtocolException exception)
            {
                throw new CharacterStorageException(
                    "The .fch identity header is invalid.",
                    exception)
                    .WithPlayerMessage("sm_character_stored_unavailable");
            }

            DateTime createdUtc = createdUtcOverride ??
                File.GetLastWriteTimeUtc(profilePath);
            if (createdUtc.Kind != DateTimeKind.Utc)
            {
                createdUtc = DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc);
            }

            return CharacterEnvelope.CreateWithOrigin(
                CharacterEnvelopeKind.Snapshot,
                1,
                0,
                CreateSyntheticSessionId(payload),
                identity,
                createdUtc,
                profileVersion,
                payload,
                requiresFreshLocalCharacter);
        }

        private string ResolveExistingProfilePath(
            CharacterIdentity identity,
            string storageKey,
            bool cleanupCompletedPending,
            out bool requiresFreshLocalCharacter)
        {
            requiresFreshLocalCharacter = false;
            if (!TryRequireAccountDirectory(_layout.GetAccountDirectory(storageKey))) return string.Empty;
            string finalPath = _layout.GetProfilePath(storageKey);
            string pendingPath = _layout.GetPendingProfilePath(storageKey);
            bool hasFinal = IsRegularStorageFilePresent(
                finalPath,
                "character primary");
            bool hasPending = IsRegularStorageFilePresent(
                pendingPath,
                "pending character primary");

            if (hasFinal)
            {
                CharacterEnvelope final = ReadAndValidateSnapshot(
                    finalPath,
                    identity,
                    requiresFreshLocalCharacter: false);
                if (hasPending)
                {
                    CharacterEnvelope pending = ReadAndValidateSnapshot(
                        pendingPath,
                        identity,
                        requiresFreshLocalCharacter: true);
                    ValidateCompletedPendingPair(
                        identity,
                        final,
                        pending);
                    if (cleanupCompletedPending)
                    {
                        TryDeleteCompletedStorageArtifact(
                            pendingPath,
                            "completed pending character primary");
                    }
                }

                requiresFreshLocalCharacter = false;
                return finalPath;
            }

            if (hasPending)
            {
                if (HasBackup(storageKey))
                {
                    throw new CharacterStorageException(
                        "A pending character primary cannot coexist with backups " +
                        "without an established primary.")
                        .WithPlayerMessage("sm_character_recovery_required");
                }

                ReadAndValidateSnapshot(
                    pendingPath,
                    identity,
                    requiresFreshLocalCharacter: true);
                requiresFreshLocalCharacter = true;
                return pendingPath;
            }

            requiresFreshLocalCharacter = false;
            return string.Empty;
        }

        private void ValidateCompletedPendingPair(
            CharacterIdentity identity,
            CharacterEnvelope completed,
            CharacterEnvelope pending)
        {
            try
            {
                CharacterValidatedSnapshot completedProfile =
                    _profileCodec.ExtractValidatedSnapshot(
                        identity,
                        completed.PayloadUnsafe);
                CharacterValidatedSnapshot pendingProfile =
                    _profileCodec.ExtractValidatedSnapshot(
                        identity,
                        pending.PayloadUnsafe);
                if (!completedProfile.SemanticSnapshot.HasPlayerData)
                {
                    throw new CharacterStorageException(
                        "The completed character file has no materialized Player data.");
                }

                if (completedProfile.PlayerId != pendingProfile.PlayerId)
                {
                    throw new CharacterStorageException(
                        "The completed and pending character files have different PlayerProfile IDs.");
                }
            }
            catch (CharacterStorageException)
            {
                throw;
            }
            catch (CharacterProtocolException exception)
            {
                throw new CharacterStorageException(
                    "The completed/pending character pair failed full profile validation.",
                    exception);
            }
        }

        internal void PromotePendingSnapshot(
            CharacterIdentity identity,
            string storageKey,
            CharacterEnvelope expectedPending,
            CharacterEnvelope established)
        {
            if (identity == null)
                throw new ArgumentNullException(nameof(identity));
            if (expectedPending == null)
                throw new ArgumentNullException(nameof(expectedPending));
            if (established == null)
                throw new ArgumentNullException(nameof(established));
            ValidateAdminStorageTarget(identity, storageKey);
            if (!expectedPending.RequiresFreshLocalCharacter ||
                established.RequiresFreshLocalCharacter ||
                !EnvelopeMatchesIdentity(expectedPending, identity) ||
                !EnvelopeMatchesIdentity(established, identity))
            {
                throw new CharacterStorageException(
                    "The pending character promotion is structurally invalid.");
            }

            lock (GetAccountLock(identity.AccountId))
            {
                object profileLock = _profileLocks.GetOrAdd(storageKey, _ => new object());
                lock (profileLock)
                {
                    if (!TryRequireAccountDirectory(_layout.GetAccountDirectory(storageKey)))
                        throw new CharacterStorageException("The pending character account directory disappeared before promotion.");
                    string finalPath = _layout.GetProfilePath(storageKey);
                    string pendingPath = _layout.GetPendingProfilePath(storageKey);
                    bool hasFinal = IsRegularStorageFilePresent(
                        finalPath,
                        "character primary");
                    bool hasPending = IsRegularStorageFilePresent(
                        pendingPath,
                        "pending character primary");

                    if (hasPending)
                    {
                        CharacterEnvelope pending = ReadAndValidateSnapshot(
                            pendingPath,
                            identity,
                            requiresFreshLocalCharacter: true);
                        if (!SnapshotPayloadMatches(pending, expectedPending))
                        {
                            throw new CharacterStorageException(
                                "The pending character changed before first-save promotion.");
                        }
                    }

                    if (hasFinal)
                    {
                        CharacterEnvelope final = ReadAndValidateSnapshot(
                            finalPath,
                            identity,
                            requiresFreshLocalCharacter: false);
                        if (!SnapshotPayloadMatches(final, established))
                        {
                            throw new CharacterStorageException(
                                "A different established character appeared during first-save promotion.");
                        }

                        if (hasPending)
                        {
                            TryDeleteCompletedStorageArtifact(
                                pendingPath,
                                "completed pending character primary");
                        }

                        return;
                    }

                    if (!hasPending)
                    {
                        throw new CharacterStorageException(
                            "The pending character disappeared before first-save promotion.");
                    }

                    byte[] encoded = VanillaCharacterFileCodec.Encode(
                        established.PayloadUnsafe,
                        _options.MaxPayloadBytes);
                    WriteInitialAtomically(finalPath, encoded);
                    CharacterEnvelope verified = ReadAndValidateSnapshot(
                        finalPath,
                        identity,
                        requiresFreshLocalCharacter: false);
                    if (!SnapshotPayloadMatches(verified, established))
                    {
                        throw new CharacterStorageException(
                            "The established character failed first-save disk readback.");
                    }

                    TryDeleteCompletedStorageArtifact(
                        pendingPath,
                        "completed pending character primary");
                }
            }
        }

        private static void TryDeleteCompletedStorageArtifact(
            string path,
            string description)
        {
            try
            {
                EnsureRegularNonReparseFile(path, description);
                File.Delete(path);
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                // The validated final .fch is already authoritative. Cleanup
                // must not turn that durable commit into a client-visible save
                // rejection; a later startup or access can retry safely.
                try
                {
                    ServerManagerPlugin.Log?.LogWarning(
                        "Deferred cleanup of " + description + " '" +
                        Path.GetFileName(path) + "': " + exception.Message);
                }
                catch (Exception loggingException) when (
                    !IntegrityCanonical.IsFatal(loggingException))
                {
                    // Logging is best effort and cannot change disk authority.
                }
            }
        }

        private static bool SnapshotPayloadMatches(
            CharacterEnvelope left,
            CharacterEnvelope right)
        {
            return left != null && right != null &&
                   left.PayloadLength == right.PayloadLength &&
                   CharacterCrypto.FixedTimeEquals(
                       left.PayloadSha256Unsafe,
                       right.PayloadSha256Unsafe) &&
                   CharacterCrypto.FixedTimeEquals(
                       left.PayloadUnsafe,
                       right.PayloadUnsafe);
        }

        private static Guid CreateSyntheticSessionId(byte[] payload)
        {
            byte[] hash = CharacterCrypto.Sha256(payload);
            byte[] guidBytes = new byte[16];
            Buffer.BlockCopy(hash, 0, guidBytes, 0, guidBytes.Length);
            Guid sessionId = new Guid(guidBytes);
            if (sessionId == Guid.Empty)
            {
                guidBytes[0] = 1;
                sessionId = new Guid(guidBytes);
            }

            return sessionId;
        }

        private static int ReadRawProfileVersion(byte[] payload)
        {
            if (payload == null || payload.Length < sizeof(int))
            {
                throw new CharacterStorageException(
                    "The .fch PlayerProfile payload is truncated.");
            }

            return payload[0] |
                   (payload[1] << 8) |
                   (payload[2] << 16) |
                   (payload[3] << 24);
        }

        private CharacterIdentity ReadStorageIdentity(string profilePath, string storageKey)
        {
            CharacterStorageLayout.ValidateStorageKey(storageKey);
            const string prefix = "Steam_";
            int separator = storageKey.IndexOf('_', prefix.Length);
            string steamId = storageKey.Substring(
                prefix.Length,
                separator - prefix.Length);
            byte[] payload = VanillaCharacterFileCodec.Decode(ReadBoundedFile(profilePath), _options.MaxPayloadBytes);
            try
            {
                _profileCodec.ReadStoredIdentityHeader(payload, out string characterName);
                CharacterIdentity identity = new CharacterIdentity(
                    CharacterSteamIdentity.AccountPrefix + steamId, characterName);
                if (!string.Equals(CharacterStorageKeyProvider.FormatStorageKey(identity), storageKey, StringComparison.Ordinal))
                    throw new CharacterStorageException("The embedded PlayerProfile name does not map to its canonical lowercase storage filename.");
                return identity;
            }
            catch (CharacterProtocolException exception)
            {
                throw new CharacterStorageException("The .fch identity header is invalid.", exception);
            }
        }

        private static bool IsRegularStorageFilePresent(
            string path,
            string description)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    throw new CharacterStorageException(
                        "The " + description + " path is a directory.");
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new CharacterStorageException(
                        "The " + description + " cannot be a link or reparse point.");
                }

                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private long ValidateStorageKeyMapping(
            string storedPath,
            CharacterStorageKeyProvider storageKeyProvider,
            string storageKey,
            bool isBackup,
            bool requiresFreshLocalCharacter)
        {
            CharacterStorageLayout.ValidateStorageKey(storageKey);
            CharacterIdentity identity = ReadStorageIdentity(storedPath, storageKey);
            string expectedStorageKey =
                storageKeyProvider.DeriveStorageKey(identity);
            if (!string.Equals(
                    storageKey,
                    expectedStorageKey,
                    StringComparison.Ordinal))
            {
                throw new CharacterStorageException(
                    "A character storage filename is not the canonical key for its identity.");
            }

            string actualFullPath = Path.GetFullPath(storedPath);
            if (!isBackup)
            {
                string expectedFullPath = Path.GetFullPath(
                    requiresFreshLocalCharacter
                        ? _layout.GetPendingProfilePath(expectedStorageKey)
                        : _layout.GetProfilePath(expectedStorageKey));
                if (!string.Equals(
                        actualFullPath,
                        expectedFullPath,
                        StoragePathComparison) ||
                    !string.Equals(Path.GetFileName(actualFullPath),
                        expectedStorageKey +
                        (requiresFreshLocalCharacter
                            ? CharacterPendingFilenameSuffix
                            : CharacterFilenameSuffix),
                        StringComparison.Ordinal))
                {
                    throw new CharacterStorageException(
                        "A character primary path does not match the readable " +
                        "storage key derived from its filename identity.");
                }

                CharacterEnvelope envelope = ReadAndValidateSnapshot(
                    storedPath,
                    identity,
                    requiresFreshLocalCharacter);
                return ReadValidatedStoredPlayerId(identity, envelope);
            }

            string actualDirectory =
                Path.GetFullPath(
                    Path.GetDirectoryName(actualFullPath) ??
                    string.Empty)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
            string expectedDirectory =
                Path.GetFullPath(
                    _layout.GetAccountDirectory(expectedStorageKey))
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
            if (!string.Equals(
                    actualDirectory,
                    expectedDirectory,
                    StoragePathComparison) ||
                !string.Equals(Path.GetFileName(actualDirectory),
                    identity.AccountId.Substring(CharacterSteamIdentity.AccountPrefix.Length), StringComparison.Ordinal))
            {
                throw new CharacterStorageException(
                    "A character backup path does not match the readable " +
                    "storage key derived from its filename identity.");
            }

            CharacterBackupRecord record = ParseAdminBackupFilename(
                Path.GetFileName(actualFullPath),
                expectedStorageKey);
            ReadAndValidateSnapshot(
                storedPath,
                identity,
                requiresFreshLocalCharacter: false,
                createdUtcOverride: record.CreatedUtc);
            return 0;
        }

        private long ReadValidatedStoredPlayerId(
            CharacterIdentity identity,
            CharacterEnvelope envelope)
        {
            try
            {
                return _profileCodec.ValidateStoredIdentityHeader(
                    identity,
                    envelope.PayloadUnsafe);
            }
            catch (CharacterProtocolException exception)
            {
                throw new CharacterStorageException(
                    "The .fch identity header is invalid.",
                    exception);
            }
        }

        private static void RegisterStoredPlayerId(
            IDictionary<long, string> owners,
            long playerId,
            string storageKey)
        {
            if (playerId == 0)
            {
                throw new CharacterStorageException(
                    "A stored PlayerProfile ID must be nonzero.");
            }

            if (owners.TryGetValue(playerId, out string existingStorageKey) &&
                !CharacterStorageLayout.StorageKeyComparer.Equals(
                    existingStorageKey,
                    storageKey))
            {
                throw new CharacterStorageException(
                    "Different character primaries share the same embedded " +
                    "PlayerProfile ID.");
            }

            owners[playerId] = storageKey;
        }

        private static void RegisterCanonicalStorageKey(IDictionary<string, string> observed, string storageKey)
        {
            if (observed.TryGetValue(storageKey, out string existing) &&
                !string.Equals(existing, storageKey, StringComparison.Ordinal))
                throw new CharacterStorageException("Different canonical character names collide on this filesystem.");
            observed[storageKey] = storageKey;
        }

        private int CountProfilesForAccount(string accountId)
        {
            try
            {
                HashSet<string> keys = new HashSet<string>(CharacterStorageLayout.StorageKeyComparer);
                Dictionary<string, string> canonicalKeys = new Dictionary<string, string>(CharacterStorageLayout.StorageKeyComparer);
                foreach (string path in EnumerateAccountFiles(_layout.GetAccountDirectoryForAccount(accountId)))
                {
                    string filename = Path.GetFileName(path);
                    bool pending = TryParsePendingFilename(filename, out string storageKey);
                    if (!pending && !TryParsePrimaryFilename(filename, out storageKey)) continue;
                    RegisterCanonicalStorageKey(canonicalKeys, storageKey);
                    CharacterIdentity identity = ReadStorageIdentity(path, storageKey);
                    ReadAndValidateSnapshot(path, identity, pending);
                    keys.Add(storageKey);
                }
                return keys.Count;
            }
            catch (CharacterStorageException) { throw; }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                throw new CharacterStorageException("Existing profiles could not be inspected safely for the account quota.", exception);
            }
        }

        private bool HasBackup(string storageKey)
        {
            return GetAdminBackupsCore(storageKey).Length != 0;
        }

        private static object GetAccountLock(string accountId)
        {
            int hash = StringComparer.Ordinal.GetHashCode(accountId) & int.MaxValue;
            return AccountLockStripes[hash % AccountLockStripes.Length];
        }

        private static object[] CreateAccountLockStripes()
        {
            object[] stripes = new object[AccountLockStripeCount];
            for (int index = 0; index < stripes.Length; ++index)
            {
                stripes[index] = new object();
            }

            return stripes;
        }

        private byte[] ReadBoundedFile(string path)
        {
            EnsureStorageFileAncestors(path);
            EnsureRegularNonReparseFile(path, "character .fch file");
            using (FileStream stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read | FileShare.Delete))
            {
                long maximumLength = checked(
                    (long)_options.MaxPayloadBytes +
                    VanillaCharacterFileCodec.WrapperOverheadBytes);
                if (stream.Length <
                        VanillaCharacterFileCodec.MinimumPlayerProfileBytes +
                        VanillaCharacterFileCodec.WrapperOverheadBytes ||
                    stream.Length > maximumLength)
                {
                    throw new CharacterStorageException(
                        "The character .fch file has an invalid length.");
                }

                byte[] encoded = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < encoded.Length)
                {
                    int read = stream.Read(encoded, offset, encoded.Length - offset);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException(
                            "The character primary file was truncated while reading.");
                    }

                    offset += read;
                }

                return encoded;
            }
        }

        private void WriteInitialAtomically(string profilePath, byte[] encoded)
        {
            EnsureAccountDirectoryForWrite(profilePath);

            string temporaryPath = CreateTemporaryPath(profilePath);
            try
            {
                WriteDurably(temporaryPath, encoded);
                if (File.Exists(profilePath))
                {
                    throw new CharacterStorageException(
                        "The character primary was created concurrently.");
                }

                File.Move(temporaryPath, profilePath);
            }
            finally
            {
                TryDeleteTemporary(temporaryPath);
            }
        }

        private string ReplaceAtomicallyWithBackup(
            string profilePath,
            string storageKey,
            byte[] encoded)
        {
            return ReplaceAtomicallyWithBackup(
                profilePath, storageKey, encoded,
                out _);
        }

        private string ReplaceAtomicallyWithBackup(
            string profilePath,
            string storageKey,
            byte[] encoded,
            out bool replacementAttempted)
        {
            replacementAttempted = false;
            EnsureAccountDirectoryForWrite(profilePath);
            if (!StoragePathsEqual(profilePath, _layout.GetProfilePath(storageKey)))
                throw new CharacterStorageException("The character replacement path does not match its storage key.");
            string backupDirectory = _layout.GetAccountDirectory(storageKey);
            CharacterBackupRecord[] existingBackups =
                GetAdminBackupsCore(storageKey);
            if (existingBackups.Length > _options.MaxBackups)
            {
                // A crash or a post-commit prune failure can leave exactly one
                // excess rollback copy. Repair that bounded residue before
                // creating another backup so repeated commits do not make the
                // directory permanently exceed its inspection limit.
                PruneBackups(storageKey);
                existingBackups = GetAdminBackupsCore(storageKey);
                if (existingBackups.Length > _options.MaxBackups)
                {
                    throw new CharacterStorageException(
                        "The character backup overflow could not be pruned safely.");
                }
            }
            CharacterBackupRecord backup = CreateNewAdminBackupRecord(
                storageKey,
                backupDirectory,
                existingBackups);
            string backupPath = Path.Combine(
                backupDirectory,
                GetAdminBackupFilename(backup));
            if (File.Exists(backupPath) || Directory.Exists(backupPath))
            {
                throw new CharacterStorageException(
                    "A generated character backup destination already exists.");
            }

            string temporaryPath = CreateTemporaryPath(profilePath);
            try
            {
                WriteDurably(temporaryPath, encoded);
                replacementAttempted = true;
                File.Replace(temporaryPath, profilePath, backupPath, true);
            }
            catch (PlatformNotSupportedException exception)
            {
                throw new CharacterStorageException(
                    "This platform does not support atomic File.Replace for character commits.",
                    exception);
            }
            finally
            {
                TryDeleteTemporary(temporaryPath);
            }

            try
            {
                // Never delete the rollback copy created by this replacement.
                PruneBackups(storageKey, backupPath);
                return string.Empty;
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                return "The character commit succeeded, but backup rotation failed: " +
                       exception.Message;
            }
        }

        private static CharacterBackupRecord CreateNewAdminBackupRecord(
            string storageKey,
            string backupDirectory,
            CharacterBackupRecord[] existingBackups)
        {
            CharacterStorageLayout.ValidateStorageKey(storageKey);
            if (existingBackups == null)
            {
                throw new ArgumentNullException(nameof(existingBackups));
            }

            string timestamp = DateTime.Now.ToString(
                CharacterBackupTimestampFormat,
                CultureInfo.InvariantCulture);
            int firstCollision = 1;
            if (existingBackups.Length != 0)
            {
                CharacterBackupRecord latest = existingBackups[0];
                for (int index = 1; index < existingBackups.Length; ++index)
                {
                    if (CompareBackupRecords(existingBackups[index], latest) > 0)
                    {
                        latest = existingBackups[index];
                    }
                }

                string latestTimestamp = GetBackupTimestamp(latest);
                if (StringComparer.Ordinal.Compare(timestamp, latestTimestamp) <= 0)
                {
                    timestamp = latestTimestamp;
                    firstCollision = checked(latest.CollisionNumber + 1);
                    if (firstCollision > MaximumBackupFilenameCollision)
                    {
                        if (!DateTime.TryParseExact(
                                latestTimestamp,
                                CharacterBackupTimestampFormat,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out DateTime latestLocal) ||
                            latestLocal >= DateTime.MaxValue.AddSeconds(-1))
                        {
                            throw new CharacterStorageException(
                                "The character backup timestamp cannot advance safely.");
                        }

                        timestamp = latestLocal.AddSeconds(1).ToString(
                            CharacterBackupTimestampFormat,
                            CultureInfo.InvariantCulture);
                        firstCollision = 1;
                    }
                }
            }

            for (int collision = firstCollision;
                 collision <= MaximumBackupFilenameCollision;
                 ++collision)
            {
                string collisionSuffix = collision == 1
                    ? string.Empty
                    : "." + collision.ToString("D2", CultureInfo.InvariantCulture);
                string filename = storageKey + CharacterBackupMarker + timestamp +
                                  collisionSuffix + CharacterBackupFilenameSuffix;
                string path = Path.Combine(backupDirectory, filename);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    continue;
                }

                return ParseAdminBackupFilename(filename, storageKey);
            }

            throw new CharacterStorageException(
                "No unique character backup filename was available for the current second.");
        }

        private void PruneBackups(string storageKey, string? preservedBackupPath = null)
        {
            string accountDirectory = _layout.GetAccountDirectory(storageKey);
            // Listing validates the complete account directory before deletion,
            // but returns only this exact character's native backup filenames.
            CharacterBackupRecord[] backups = GetAdminBackupsCore(storageKey);
            Array.Sort(backups, Comparer<CharacterBackupRecord>.Create(CompareBackupRecords));
            int deleteCount = backups.Length - _options.MaxBackups;
            foreach (CharacterBackupRecord backup in backups)
            {
                if (deleteCount <= 0) break;
                string path = Path.Combine(accountDirectory, backup.Filename);
                if (preservedBackupPath != null && StoragePathsEqual(path, preservedBackupPath)) continue;
                EnsureStorageFileAncestors(path);
                EnsureRegularNonReparseFile(path, "character backup");
                ParseAdminBackupFilename(Path.GetFileName(path), storageKey);
                File.Delete(path);
                --deleteCount;
            }
        }

        private void EnsureStorageFileAncestors(string path)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
            if (!TryRequireAccountDirectory(directory))
                throw new CharacterStorageException("The character account directory does not exist.");
            ValidateAccountFilePath(path);
        }

        private void EnsureAccountDirectoryForWrite(string profilePath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(profilePath)) ?? string.Empty;
            EnsureRegularNonReparseDirectory(_layout.RootDirectory, "character storage root");
            ValidateAccountDirectoryPath(directory);
            ValidateAccountFilePath(profilePath);
            EnsureDirectoryForStorageUse(directory, "character account directory");
            EnsureStorageFileAncestors(profilePath);
        }

        private static string CreateTemporaryPath(string profilePath)
        {
            string directory = Path.GetDirectoryName(profilePath);
            string filename = Path.GetFileName(profilePath);
            if (!TryParsePrimaryFilename(filename, out string storageKey) &&
                !TryParsePendingFilename(filename, out storageKey))
            {
                throw new CharacterStorageException(
                    "A character temporary file target is not a canonical primary path.");
            }
            // Leave room for a full-length Unicode name on filesystems with a
            // 255-byte component limit: .<storage-key>.<32-hex GUID>.
            return Path.Combine(
                directory,
                "." + storageKey + "." + Guid.NewGuid().ToString("N"));
        }

        private static void WriteDurably(string path, byte[] encoded)
        {
            using (FileStream stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(encoded, 0, encoded.Length);
                stream.Flush(true);
            }
        }

        private static void TryDeleteTemporary(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (
                !IntegrityCanonical.IsFatal(exception))
            {
                // A stale uniquely named temp file is safer than hiding the commit error.
            }
        }
    }

    internal sealed class CharacterBackupRecord
    {
        internal CharacterBackupRecord(
            string backupId,
            DateTime createdUtc,
            string filename,
            int collisionNumber)
        {
            BackupId = backupId;
            CreatedUtc = createdUtc;
            Filename = filename ?? string.Empty;
            CollisionNumber = collisionNumber;
        }

        internal string BackupId { get; }
        internal DateTime CreatedUtc { get; }

        internal string Filename { get; }

        internal int CollisionNumber { get; }
    }

    internal sealed class CharacterRestoreUnconfirmedException : Exception
    {
        internal CharacterRestoreUnconfirmedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class RepositoryCommitOutcome
    {
        internal CharacterSemanticValidationResult AuditFindings { get; }

        private RepositoryCommitOutcome(
            CharacterCommitStatus status,
            CharacterEnvelope current,
            string error,
            IReadOnlyList<string>? semanticObservations,
            CharacterSemanticSnapshot? previousSemanticSnapshot,
            CharacterSemanticSnapshot? currentSemanticSnapshot,
            IReadOnlyList<CharacterStatLimitFinding>? statLimitFindings,
            CharacterSemanticValidationResult? auditFindings = null)
        {
            AuditFindings = auditFindings ?? CharacterSemanticValidationResult.Empty;
            Status = status;
            Current = current;
            Error = error ?? string.Empty;
            SemanticObservations =
                semanticObservations ?? Array.Empty<string>();
            StatLimitFindings =
                statLimitFindings ?? Array.Empty<CharacterStatLimitFinding>();
            PreviousSemanticSnapshot = previousSemanticSnapshot;
            CurrentSemanticSnapshot = currentSemanticSnapshot;
        }

        public CharacterCommitStatus Status { get; }

        public CharacterEnvelope Current { get; }

        public string Error { get; }

        public IReadOnlyList<string> SemanticObservations { get; }

        public IReadOnlyList<CharacterStatLimitFinding> StatLimitFindings { get; }

        internal CharacterSemanticSnapshot? PreviousSemanticSnapshot { get; }

        internal CharacterSemanticSnapshot? CurrentSemanticSnapshot { get; }

        public static RepositoryCommitOutcome Accepted(
            CharacterEnvelope current,
            IReadOnlyList<string>? semanticObservations = null,
            CharacterSemanticSnapshot? previousSemanticSnapshot = null,
            CharacterSemanticSnapshot? currentSemanticSnapshot = null,
            IReadOnlyList<CharacterStatLimitFinding>? statLimitFindings = null,
            CharacterSemanticValidationResult? auditFindings = null)
        {
            return new RepositoryCommitOutcome(
                CharacterCommitStatus.Accepted,
                current,
                string.Empty,
                semanticObservations,
                previousSemanticSnapshot,
                currentSemanticSnapshot,
                statLimitFindings, auditFindings);
        }

        public static RepositoryCommitOutcome Conflict(
            CharacterEnvelope current,
            string error)
        {
            return new RepositoryCommitOutcome(
                CharacterCommitStatus.Conflict,
                current,
                error,
                null,
                null,
                null,
                null);
        }

        public static RepositoryCommitOutcome Rejected(
            CharacterEnvelope current,
            string error,
            IReadOnlyList<string>? semanticObservations = null,
            IReadOnlyList<CharacterStatLimitFinding>? statLimitFindings = null,
            CharacterSemanticValidationResult? auditFindings = null)
        {
            return new RepositoryCommitOutcome(
                CharacterCommitStatus.Rejected,
                current,
                error,
                semanticObservations,
                null,
                null,
                statLimitFindings, auditFindings);
        }
    }
}
