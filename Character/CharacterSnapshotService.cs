using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace ServerManager
{
    /// <summary>
    /// Coordinates character sessions and persistence but deliberately does not register,
    /// invoke, or disconnect any RPC. Plugin integration owns network lifecycle ordering.
    /// </summary>
    public sealed class CharacterSnapshotService : IDisposable
    {
        private static readonly TimeSpan SaveRateLimiterRetention =
            TimeSpan.FromMinutes(10);
        private const int MaximumRetainedSkillObservationWindows = 512;
        private const int MaximumRetainedLiveSnapshots = 4096;
        private const int MaximumRegisteredCheckpoints = 64;
        private const long MaximumRetainedLiveSnapshotPayloadBytes =
            256L * 1024L * 1024L;

        private readonly CharacterStorageOptions _options;
        private readonly CharacterPeerIdentityResolver _identityResolver;
        private readonly CharacterStorageKeyProvider _storageKeyProvider;
        private readonly CharacterEnvelopeCodec _envelopeCodec;
        private readonly ValheimPlayerProfileCodec _profileCodec;
        private readonly CharacterRepository _repository;
        private readonly ICharacterRevisionValidator _semanticValidator;
        private ServerSettings _serverSettings = ServerSettings.Defaults;
        private readonly ConcurrentDictionary<ZRpc, CharacterSession> _serverSessions =
            new ConcurrentDictionary<ZRpc, CharacterSession>();
        private readonly ConcurrentDictionary<Guid, BackupCapturePreparation>
            _preparedBackupCaptures = new ConcurrentDictionary<Guid, BackupCapturePreparation>();
        // Open/close are serialized by _checkpointCommitGate. Reads may use
        // the immutable session ID, but saves still acquire that session's lock.
        private CharacterSession? _localHostSession;
        private readonly ConcurrentDictionary<string, Guid> _activeStorageLeases =
            new ConcurrentDictionary<string, Guid>(CharacterStorageLayout.StorageKeyComparer);
        private readonly object _liveSnapshotGate = new object();
        private readonly Dictionary<string, CharacterLiveSnapshot>
            _liveSnapshots =
                new Dictionary<string, CharacterLiveSnapshot>(
                    CharacterStorageLayout.StorageKeyComparer);
        private readonly Dictionary<byte[], int> _retainedPayloadReferences =
            new Dictionary<byte[], int>(ByteArrayReferenceComparer.Instance);
        private readonly Dictionary<Guid, Dictionary<string, CharacterCheckpointEntry>>
            _registeredCheckpoints =
                new Dictionary<Guid, Dictionary<string, CharacterCheckpointEntry>>();
        private readonly Guid _checkpointOwnerId = Guid.NewGuid();
        private readonly object _checkpointCommitGate = new object();
        // Only ambiguous administrative disk writes enter this set. Never let a
        // connection or another edit guess which state won; restart revalidates disk.
        private readonly HashSet<string> _unconfirmedAdminRestores =
            new HashSet<string>(CharacterStorageLayout.StorageKeyComparer);
        private long _retainedSnapshotPayloadBytes;
        private readonly object _saveRateLimiterGate = new object();
        private readonly Dictionary<string, CharacterSaveRateLimiter> _saveRateLimiters =
            new Dictionary<string, CharacterSaveRateLimiter>(CharacterStorageLayout.StorageKeyComparer);
        private readonly Dictionary<string, CharacterSkillObservationWindow>
            _skillObservationWindows =
                new Dictionary<string, CharacterSkillObservationWindow>(
                    CharacterStorageLayout.StorageKeyComparer);
        private int _saveRateLimiterOpenCount;
        private int _disposeState;

        internal CharacterSnapshotService(
            CharacterStorageOptions options,
            CharacterPeerIdentityResolver identityResolver,
            CharacterStorageKeyProvider storageKeyProvider,
            CharacterEnvelopeCodec envelopeCodec,
            ValheimPlayerProfileCodec profileCodec,
            CharacterRepository repository,
            ICharacterRevisionValidator semanticValidator)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _identityResolver =
                identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
            _storageKeyProvider =
                storageKeyProvider ?? throw new ArgumentNullException(nameof(storageKeyProvider));
            _envelopeCodec =
                envelopeCodec ?? throw new ArgumentNullException(nameof(envelopeCodec));
            _profileCodec =
                profileCodec ?? throw new ArgumentNullException(nameof(profileCodec));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _semanticValidator = semanticValidator ??
                throw new ArgumentNullException(nameof(semanticValidator));
            _options.Validate();
        }

        // Called on the server main thread after complete settings validation.
        // Reuse repositories, sessions, leases and observation windows: a reload
        // changes policy, never the already-authoritative character payload.
        internal void ApplyServerSettings(ServerSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            CharacterSemanticEvaluator stored = CreateSettingsEvaluator(
                settings, CharacterSemanticPolicyMode.Observe);
            CharacterSemanticEvaluator incoming = CreateSettingsEvaluator(
                settings, CharacterSemanticPolicyMode.Enforce);
            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                _repository.ApplyServerSettings(settings, incoming);
                if (_semanticValidator is CharacterSemanticRevisionValidator validator)
                    validator.ApplyEvaluator(stored);
                _options.MaxCharactersPerAccount = settings.MaxCharactersPerAccount;
                _options.MaxBackups = settings.BackupsPerProfile;
                Volatile.Write(ref _serverSettings, settings);
            }
        }

        private static CharacterSemanticEvaluator CreateSettingsEvaluator(
            ServerSettings settings, CharacterSemanticPolicyMode mode)
        {
            return new CharacterSemanticEvaluator(CharacterSemanticPolicy.FromSettings(settings, mode));
        }

        internal CharacterAdminRecord[] GetAdminCharacters()
        {
            ThrowIfDisposed();
            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                Dictionary<string, CharacterAdminRecord> records = new Dictionary<string, CharacterAdminRecord>(CharacterStorageLayout.StorageKeyComparer);
                foreach (CharacterIdentity identity in _repository.GetAdminStoredIdentities(_storageKeyProvider))
                {
                    string key = _storageKeyProvider.DeriveStorageKey(identity);
                    CharacterStoredSnapshot stored = _repository.Load(identity, key) ??
                        throw new CharacterStorageException("A character primary disappeared during listing.");
                    CharacterValidatedSnapshot validated = ValidateSnapshotDetailed(identity, stored.Envelope.PayloadUnsafe);
                    records[key] = new CharacterAdminRecord(identity, validated.PlayerId,
                        stored.Envelope.Revision, stored.Envelope.Revision, _activeStorageLeases.ContainsKey(key), validated.SemanticSnapshot);
                }
                lock (_liveSnapshotGate)
                {
                    foreach (KeyValuePair<string, CharacterLiveSnapshot> pair in _liveSnapshots)
                    {
                        CharacterLiveSnapshot live = pair.Value;
                        records[pair.Key] = new CharacterAdminRecord(live.Identity, live.PlayerId,
                            live.LatestEnvelope.Revision, live.DurableEnvelope.Revision,
                            _activeStorageLeases.ContainsKey(pair.Key), live.SemanticSnapshot);
                    }
                }
                foreach (CharacterSession session in _serverSessions.Values)
                    AddAdminSessionRecord(records, session);
                if (_localHostSession != null) AddAdminSessionRecord(records, _localHostSession);
                List<CharacterAdminRecord> result = new List<CharacterAdminRecord>(records.Values);
                result.Sort((left, right) => string.CompareOrdinal(left.AccountId + "\0" + left.CharacterName, right.AccountId + "\0" + right.CharacterName));
                return result.ToArray();
            }
        }

        private static void AddAdminSessionRecord(Dictionary<string, CharacterAdminRecord> records, CharacterSession session)
        {
            if (records.ContainsKey(session.StorageKey)) return;
            lock (session.SaveLock)
            {
                session.CaptureCurrentSemanticState(out long revision, out CharacterSemanticSnapshot snapshot);
                records[session.StorageKey] = new CharacterAdminRecord(session.Identity, session.PlayerId,
                    revision, session.GetPendingInitialEnvelope() == null ? revision : 0, true, snapshot);
            }
        }

        /// <summary>Edits only offline skills in RAM. The ordinary next world checkpoint owns persistence.</summary>
        internal Events.ServerManagerCommandResult ApplyOfflineSkillAdmin(
            string accountId, string characterName, string operation, string skill, float value)
        {
            ThrowIfDisposed();
            if (!string.Equals(operation, "get", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(operation, "set", StringComparison.OrdinalIgnoreCase))
                return CharacterAdminActions.Result(false, "invalid_skill", "Skill requires get or set.");
            try
            {
                CharacterIdentity identity = new CharacterIdentity(accountId, characterName);
                string storageKey = _storageKeyProvider.DeriveStorageKey(identity);
                lock (_checkpointCommitGate)
                {
                    ThrowIfDisposed();
                    if (_unconfirmedAdminRestores.Contains(storageKey))
                        return CharacterAdminActions.Result(false, "restore_unconfirmed", "An administrative restore needs disk verification. This character is unavailable until the server restarts.");
                    if (_activeStorageLeases.ContainsKey(storageKey))
                        return CharacterAdminActions.Result(false, "character_busy", "The character is active or connecting; use the online action.");
                    CharacterLiveSnapshot? live;
                    lock (_liveSnapshotGate)
                    {
                        foreach (Dictionary<string, CharacterCheckpointEntry> checkpoint in _registeredCheckpoints.Values)
                            if (checkpoint.ContainsKey(storageKey))
                                return CharacterAdminActions.Result(false, "checkpoint_pending", "A retained world checkpoint still owns this character; retry after it completes.");
                        _liveSnapshots.TryGetValue(storageKey, out live);
                    }
                    if (live == null)
                    {
                        CharacterStoredSnapshot? stored = _repository.Load(identity, storageKey);
                        if (stored == null)
                            return CharacterAdminActions.Result(false, "character_not_found", "No stored character exists; administration never creates a profile.");
                        CharacterValidatedSnapshot validated = ValidateSnapshotDetailed(identity, stored.Envelope.PayloadUnsafe);
                        live = new CharacterLiveSnapshot(identity, stored.Envelope, stored.Envelope, validated.SemanticSnapshot, validated.PlayerId);
                    }
                    if (!live.Identity.EqualsIdentity(identity))
                        throw new CharacterStorageException("The retained character identity does not match its storage key.");
                    List<CharacterSemanticSkillState> edited = CharacterAdminActions.EditSkills(live.SemanticSnapshot.Skills.Values, operation, skill, value);
                    if (string.Equals(operation, "get", StringComparison.OrdinalIgnoreCase))
                        return CharacterAdminActions.SkillResult(edited, "skills", skill, "Raw skills from the latest retained character snapshot.");
                    byte[] payload = _profileCodec.ReplaceSkillAdmin(identity, live.LatestEnvelope.PayloadUnsafe,
                        operation, skill, value, out CharacterValidatedSnapshot candidate);
                    // Admin intent replaces the skill-transition check only. Absolute
                    // stat/item/profile policy remains mandatory, with no blanket bypass.
                    CharacterSemanticValidationResult policy = _semanticValidator.EvaluateAuthoritative(identity, candidate.SemanticSnapshot);
                    if (policy.Rejected)
                        return CharacterAdminActions.Result(false, "policy_rejected", "The edited profile violates server policy: " + policy.RejectionReason);
                    CharacterEnvelope next = CharacterEnvelope.Create(CharacterEnvelopeKind.Snapshot,
                        checked(live.LatestEnvelope.Revision + 1), live.LatestEnvelope.Revision,
                        live.LatestEnvelope.SessionId, identity, DateTime.UtcNow,
                        live.LatestEnvelope.ValheimProfileVersion, payload);
                    CharacterLiveSnapshot updated = live.WithLatest(next, candidate.SemanticSnapshot, candidate.PlayerId);
                    lock (_liveSnapshotGate)
                    {
                        if (_liveSnapshots.ContainsKey(storageKey)) SetLiveSnapshotLocked(storageKey, updated);
                        else AddLiveSnapshotLocked(storageKey, updated);
                    }
                    // A future session starts its progression window at this explicit
                    // admin baseline, not at the disconnected pre-edit skill levels.
                    lock (_saveRateLimiterGate) _skillObservationWindows.Remove(storageKey);
                    return CharacterAdminActions.SkillResult(edited, "staged_in_ram", skill,
                        "Offline skill edit staged in RAM at revision " + next.Revision + "; durable revision " + live.DurableEnvelope.Revision + ". The next successful world checkpoint persists it.");
                }
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                return CharacterAdminActions.Result(false, "offline_skill_failed", exception.Message);
            }
        }

        internal Events.ServerManagerCommandResult GetAdminBackups(
            string accountId, string characterName, int page)
        {
            if (page < 1 || page > 1000)
                return CharacterAdminActions.Result(false, "invalid_page", "Page must be between 1 and 1000.");
            try
            {
                CharacterIdentity identity = new CharacterIdentity(accountId, characterName);
                lock (_checkpointCommitGate)
                {
                    ThrowIfDisposed();
                    string key = _storageKeyProvider.DeriveStorageKey(identity);
                    if (_repository.Load(identity, key) == null)
                        return CharacterAdminActions.Result(false, "character_not_found", "No existing primary was found. Backup recovery of missing primaries is not supported by this command.");
                    CharacterBackupRecord[] backups = _repository.GetAdminBackups(identity, key);
                    const int pageSize = 10;
                    int pages = Math.Max(1, (backups.Length + pageSize - 1) / pageSize);
                    if (page > pages)
                        return CharacterAdminActions.Result(false, "backup_page_not_found", "The backup page does not exist. Pages: " + pages.ToString(CultureInfo.InvariantCulture));
                    Dictionary<string, string> data = new Dictionary<string, string>
                    {
                        ["page"] = page.ToString(CultureInfo.InvariantCulture),
                        ["pages"] = pages.ToString(CultureInfo.InvariantCulture),
                        ["count"] = backups.Length.ToString(CultureInfo.InvariantCulture)
                    };
                    StringBuilder text = new StringBuilder();
                    text.Append(identity.AccountId).Append('/').Append(identity.CharacterName)
                        .Append(" backups, page ").Append(page).Append('/').Append(pages)
                        .Append(" (snapshot time, server local):");
                    int start = (page - 1) * pageSize;
                    for (int index = start; index < backups.Length && index < start + pageSize; ++index)
                    {
                        CharacterBackupRecord backup = backups[index];
                        text.Append('\n').Append(backup.BackupId).Append(' ')
                            .Append(backup.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        data["backup_" + (index - start).ToString(CultureInfo.InvariantCulture)] = backup.BackupId;
                    }
                    if (backups.Length == 0) text.Append("\n(no backups)");
                    return new Events.ServerManagerCommandResult(true, "character_backups", text.ToString(), string.Empty, data);
                }
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                return CharacterAdminActions.Result(false, "backup_list_failed", exception.Message);
            }
        }

        /// <summary>Explicit offline rollback to a verified native disk snapshot.
        /// No implicit kick/save, retained-shadow discard, or world rollback.</summary>
        internal Events.ServerManagerCommandResult RestoreAdminBackup(
            string accountId, string characterName, string backupId)
        {
            Dictionary<string, string> data = new Dictionary<string, string>
            {
                ["target_account"] = accountId,
                ["target_character"] = characterName
            };
            Events.ServerManagerCommandResult Result(bool success, string code, string message) =>
                new Events.ServerManagerCommandResult(success, code, message, string.Empty, data);
            if (!IsOpaqueBackupId(backupId))
                return Result(false, "invalid_backup_id", "Use the 32-character backup ID from characterbackups, not a path or list index.");
            data["backup_id"] = backupId;
            try
            {
                CharacterIdentity identity = new CharacterIdentity(accountId, characterName);
                lock (_checkpointCommitGate)
                {
                    ThrowIfDisposed();
                    string key = _storageKeyProvider.DeriveStorageKey(identity);
                    if (_unconfirmedAdminRestores.Contains(key))
                        return Result(false, "restore_unconfirmed", "An earlier restore needs disk verification. This character is unavailable until the server restarts.");
                    if (_activeStorageLeases.ContainsKey(key))
                        return Result(false, "character_busy", "The character is active or connecting. Complete logout before restoring.");
                    lock (_liveSnapshotGate)
                    {
                        foreach (Dictionary<string, CharacterCheckpointEntry> checkpoint in _registeredCheckpoints.Values)
                            if (checkpoint.ContainsKey(key))
                                return Result(false, "checkpoint_pending", "A world checkpoint or disk retry still owns this character. Wait for it to complete.");
                        if (_liveSnapshots.ContainsKey(key))
                            return Result(false, "shadow_pending", "RAM state remains. Run server save after logout and wait for its complete character checkpoint before restoring.");
                    }
                    CharacterStoredSnapshot? stored = _repository.Load(identity, key);
                    if (stored == null)
                        return Result(false, "character_not_found", "A valid existing primary is required. This command does not recover missing or corrupt primaries.");
                    CharacterEnvelope current = stored.Envelope;
                    if (current.ValheimProfileVersion != ValheimPlayerProfileCodec.SupportedPlayerProfileVersion)
                        return Result(false, "unsupported_profile_version", "The current primary has an unsupported profile version. No files were changed.");
                    CharacterValidatedSnapshot currentProfile = ValidateSnapshotDetailed(identity, current.PayloadUnsafe);
                    CharacterEnvelope backup = _repository.ReadAdminBackup(identity, key, backupId);
                    if (backup.ValheimProfileVersion != ValheimPlayerProfileCodec.SupportedPlayerProfileVersion)
                        return Result(false, "unsupported_profile_version", "The selected backup has an unsupported profile version. No files were changed.");
                    CharacterValidatedSnapshot candidate = ValidateSnapshotDetailed(identity, backup.PayloadUnsafe);
                    if (backup.RequiresFreshLocalCharacter || !candidate.SemanticSnapshot.HasPlayerData)
                        return Result(false, "backup_not_materialized", "Initial/new-character backups cannot be restored over an existing character. Select a backup containing player data.");
                    if (candidate.PlayerId != currentProfile.PlayerId)
                        return Result(false, "player_id_mismatch", "The backup belongs to a different PlayerProfile ID.");
                    // Use the same stored-snapshot policy as reconnect, not an
                    // incoming gameplay transition: the rollback is explicit admin intent.
                    CharacterSemanticValidationResult policy = _semanticValidator.EvaluateAuthoritative(identity, candidate.SemanticSnapshot);
                    if (policy.Rejected)
                        return Result(false, "policy_rejected", "The backup violates stored character policy: " + policy.RejectionReason);
                    CharacterEnvelope restored = CharacterEnvelope.Create(
                        CharacterEnvelopeKind.Snapshot, checked(current.Revision + 1), current.Revision,
                        Guid.NewGuid(), identity, DateTime.UtcNow, backup.ValheimProfileVersion, backup.PayloadUnsafe);
                    string warning;
                    try
                    {
                        warning = _repository.RestoreAdminSnapshot(identity, key, current, restored);
                    }
                    catch (CharacterRestoreUnconfirmedException exception)
                    {
                        _unconfirmedAdminRestores.Add(key);
                        return Result(false, "restore_unconfirmed", "The disk result could not be verified. Do not retry automatically; this character is blocked until server restart and disk verification. " + exception.Message);
                    }
                    lock (_saveRateLimiterGate) _skillObservationWindows.Remove(key);
                    string message = "Restored " + identity.AccountId + "/" + identity.CharacterName +
                        " from backup " + backupId +
                        ". The native disk snapshot was verified and the previous primary was backed up. " +
                        "The next session will start from a new process-local revision baseline. " +
                        "The player may reconnect. World state was not changed.";
                    if (policy.Observations.Count != 0)
                        message += " Stored-policy observations: " + policy.Observations.Count.ToString(CultureInfo.InvariantCulture) + ".";
                    if (!string.IsNullOrEmpty(warning)) message += " Warning: " + warning;
                    return Result(true, string.IsNullOrEmpty(warning) ? "restored" : "restored_with_warning", message);
                }
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                return Result(false, "restore_failed", exception.Message);
            }
        }

        private static bool IsOpaqueBackupId(string value)
        {
            if (value == null || value.Length != 32)
            {
                return false;
            }

            for (int index = 0; index < value.Length; ++index)
            {
                char character = value[index];
                if ((character < '0' || character > '9') &&
                    (character < 'a' || character > 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Opens an authoritative session. A missing empty snapshot is prepared
        /// in memory and persisted only after the matching ReadyAck is accepted.
        /// </summary>
        public CharacterSessionOpenResult OpenOrCreateServerSession(ZNetPeer peer)
        {
            ThrowIfDisposed();
            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                CharacterIdentity identity = _identityResolver.ResolveServerPeer(peer);
                ZRpc rpc = peer.m_rpc ?? throw new CharacterProtocolException(
                    "The authenticated peer no longer has an RPC.");
                return OpenOrCreateSessionCore(identity, rpc);
            }
        }

        /// <summary>
        /// Opens the one in-process host session. The runtime must derive this
        /// identity from local Steam and the selected profile, never from RPC data.
        /// </summary>
        internal CharacterSessionOpenResult OpenOrCreateLocalHostSession(
            CharacterIdentity identity)
        {
            ThrowIfDisposed();
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (!CharacterSteamIdentity.TryParseCanonicalAccountId(
                    identity.AccountId,
                    out _))
            {
                throw new CharacterProtocolException(
                    "The local host requires a canonical individual Steam identity.");
            }

            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                if (_localHostSession != null)
                {
                    throw new CharacterProtocolException(
                        "A local host character session is already open.");
                }

                return OpenOrCreateSessionCore(identity, null);
            }
        }

        public CharacterSessionOpenResult OpenBackupServerSession(
            ZNetPeer peer, byte[] localProfile)
        {
            ThrowIfDisposed();
            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                CharacterIdentity identity = _identityResolver.ResolveServerPeer(peer);
                ZRpc rpc = peer.m_rpc ?? throw new CharacterProtocolException(
                    "The authenticated peer no longer has an RPC.");
                return OpenBackupSessionCore(identity, rpc, localProfile);
            }
        }

        internal CharacterSessionOpenResult OpenBackupLocalHostSession(
            CharacterIdentity identity, byte[] localProfile)
        {
            ThrowIfDisposed();
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (!CharacterSteamIdentity.TryParseCanonicalAccountId(identity.AccountId, out _))
                throw new CharacterProtocolException(
                    "The local host requires a canonical individual Steam identity.");
            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                if (_localHostSession != null)
                    throw new CharacterProtocolException("A local host character session is already open.");
                return OpenBackupSessionCore(identity, null, localProfile);
            }
        }

        private CharacterSessionOpenResult OpenBackupSessionCore(
            CharacterIdentity identity, ZRpc? rpc, byte[] localProfile)
        {
            if (localProfile == null || localProfile.Length == 0 ||
                localProfile.Length > _options.MaxPayloadBytes)
                throw new CharacterProtocolException("The local backup profile exceeds the payload limits.");
            string storageKey = _storageKeyProvider.DeriveStorageKey(identity);
            if (_unconfirmedAdminRestores.Contains(storageKey))
                throw new CharacterStorageException("An administrative restore could not be verified. This character is unavailable until server restart and disk verification.")
                    .WithPlayerMessage("sm_character_restore_unavailable");
            Guid sessionId = Guid.NewGuid();
            if (!_activeStorageLeases.TryAdd(storageKey, sessionId))
                throw new CharacterProtocolException("This authoritative server character already has an active session.")
                    .WithPlayerMessage("sm_character_active");

            CharacterSkillObservationWindow? window = null;
            bool completed = false;
            try
            {
                // Clone before validation: neither the caller nor serialization
                // may alter the bytes on which Ready and subsequent saves rely.
                byte[] payload = CharacterCrypto.Clone(localProfile);
                CharacterValidatedSnapshot candidate = ValidateSnapshotDetailed(identity, payload);
                CharacterLiveSnapshot? previous;
                lock (_liveSnapshotGate) _liveSnapshots.TryGetValue(storageKey, out previous);
                if (previous == null)
                {
                    CharacterStoredSnapshot? stored = _repository.Load(identity, storageKey, cleanupCompletedPending: false);
                    if (stored != null)
                    {
                        CharacterValidatedSnapshot validatedStored = ValidateSnapshotDetailed(identity, stored.Envelope.PayloadUnsafe);
                        previous = new CharacterLiveSnapshot(identity, stored.Envelope, stored.Envelope,
                            validatedStored.SemanticSnapshot, validatedStored.PlayerId);
                    }
                }
                if (previous != null)
                {
                    if (!previous.Identity.EqualsIdentity(identity))
                        throw new CharacterStorageException("The retained character identity does not match its storage key.")
                            .WithPlayerMessage("sm_character_stored_mismatch");
                    if (previous.PlayerId != candidate.PlayerId)
                        throw new CharacterProtocolException("The local PlayerProfile ID conflicts with the existing server character.")
                            .WithPlayerMessage("sm_character_local_mismatch");
                    if (previous.SemanticSnapshot.HasPlayerData && !candidate.SemanticSnapshot.HasPlayerData)
                        throw new CharacterProtocolException("An unmaterialized local profile cannot replace an established server character.")
                            .WithPlayerMessage("sm_character_local_mismatch");
                }

                long baseRevision = previous?.LatestEnvelope.Revision ?? 0;
                CharacterEnvelope capture = CharacterEnvelope.CreateWithOrigin(
                    CharacterEnvelopeKind.Snapshot, checked(baseRevision + 1), baseRevision,
                    sessionId, identity, DateTime.UtcNow,
                    ValheimPlayerProfileCodec.SupportedPlayerProfileVersion, payload,
                    requiresFreshLocalCharacter: !candidate.SemanticSnapshot.HasPlayerData);
                CharacterSemanticValidationResult semantic = _repository.EvaluateBackupCapture(identity, capture, candidate);
                if (semantic.Rejected)
                    throw new CharacterProtocolException("The local backup profile violates server policy: " + semantic.RejectionReason);
                ValidateCaptureReservations(identity, storageKey, candidate.PlayerId);

                window = new CharacterSkillObservationWindow(capture.Revision, capture.PayloadSha256Unsafe, candidate.SemanticSnapshot);
                window.BeginSession(capture.Revision, capture.PayloadSha256Unsafe, candidate.SemanticSnapshot);
                CharacterSaveRateLimiter limiter = GetSaveRateLimiter(storageKey);
                CharacterSession session = rpc == null
                    ? CharacterSession.CreateLocalHost(identity, storageKey, sessionId,
                        capture.Revision, capture.PayloadSha256Unsafe, candidate.SemanticSnapshot,
                        candidate.PlayerId, limiter, window, capture, backupOnly: true,
                        backupCaptureCreatesProfile: previous == null)
                    : new CharacterSession(rpc, identity, storageKey, sessionId,
                        capture.Revision, capture.PayloadSha256Unsafe, candidate.SemanticSnapshot,
                        candidate.PlayerId, limiter, window, capture, backupOnly: true,
                        backupCaptureCreatesProfile: previous == null);
                CharacterSessionOpenResult result = new CharacterSessionOpenResult(
                    semantic, capture, _envelopeCodec.ToZPackage(capture), true,
                    semantic.Observations, semantic.StatLimitFindings);

                // An unacknowledged capture is held separately: checkpoints and
                // reconnects can still see only the prior accepted generation.
                RegisterPreparedBackupCapture(identity, storageKey, capture, previous, candidate, window);
                if (rpc == null) Volatile.Write(ref _localHostSession, session);
                else if (!_serverSessions.TryAdd(rpc, session))
                    throw new InvalidOperationException("A character session is already open for this RPC.");
                completed = true;
                return result;
            }
            finally
            {
                if (!completed)
                {
                    RemovePreparedBackupCapture(sessionId);
                    window?.EndSession();
                    ReleaseStorageLease(storageKey, sessionId);
                }
            }
        }

        private void ValidateCaptureReservations(CharacterIdentity identity, string storageKey, long playerId)
        {
            HashSet<string> accountKeys = new HashSet<string>(CharacterStorageLayout.StorageKeyComparer);
            void Inspect(CharacterIdentity owner, string key, long ownedPlayerId)
            {
                if (!CharacterStorageLayout.StorageKeyComparer.Equals(key, storageKey) && ownedPlayerId == playerId)
                    throw new CharacterProtocolException("The local PlayerProfile ID already belongs to another server character.")
                        .WithPlayerMessage("sm_character_local_mismatch");
                if (string.Equals(owner.AccountId, identity.AccountId, StringComparison.Ordinal)) accountKeys.Add(key);
            }
            foreach (CharacterIdentity storedIdentity in _repository.GetAdminStoredIdentities(_storageKeyProvider, cleanupCompletedPending: false))
            {
                string key = _storageKeyProvider.DeriveStorageKey(storedIdentity);
                CharacterStoredSnapshot stored = _repository.Load(storedIdentity, key, cleanupCompletedPending: false) ??
                    throw new CharacterStorageException("A server character disappeared during PlayerProfile ID validation.");
                Inspect(storedIdentity, key, _profileCodec.ValidateStoredIdentityHeader(storedIdentity, stored.Envelope.PayloadUnsafe));
            }
            lock (_liveSnapshotGate)
                foreach (KeyValuePair<string, CharacterLiveSnapshot> pair in _liveSnapshots)
                    Inspect(pair.Value.Identity, pair.Key, pair.Value.PlayerId);
            foreach (CharacterSession active in EnumerateOpenSessions())
                if (!active.IsClosed) Inspect(active.Identity, active.StorageKey, active.PlayerId);
            if (!accountKeys.Contains(storageKey) && accountKeys.Count >= _options.MaxCharactersPerAccount &&
                !_repository.HasCharacterCreationQuotaExemption(identity))
                throw new CharacterStorageException("The account has reached the server character profile quota, including connecting characters.")
                    .WithPlayerMessage("sm_character_profile_limit", _options.MaxCharactersPerAccount.ToString(CultureInfo.InvariantCulture));
        }

        private void RegisterPreparedBackupCapture(CharacterIdentity identity, string storageKey, CharacterEnvelope capture,
            CharacterLiveSnapshot? previous, CharacterValidatedSnapshot candidate, CharacterSkillObservationWindow window)
        {
            List<byte[]> preparedPayloads = new List<byte[]> { capture.PayloadUnsafe };
            if (previous != null)
            {
                preparedPayloads.Add(previous.DurableEnvelope.PayloadUnsafe);
                preparedPayloads.Add(previous.LatestEnvelope.PayloadUnsafe);
            }
            foreach (CharacterSession session in EnumerateOpenSessions())
            {
                CharacterEnvelope? pending = session.GetPendingInitialEnvelope();
                if (pending != null) preparedPayloads.Add(pending.PayloadUnsafe);
            }
            foreach (BackupCapturePreparation preparation in _preparedBackupCaptures.Values)
            {
                if (preparation.Previous == null) continue;
                preparedPayloads.Add(preparation.Previous.DurableEnvelope.PayloadUnsafe);
                preparedPayloads.Add(preparation.Previous.LatestEnvelope.PayloadUnsafe);
            }
            lock (_liveSnapshotGate)
            {
                EnsureLiveSnapshotCapacityLocked(storageKey, new CharacterLiveSnapshot(identity,
                    previous?.DurableEnvelope ?? capture, capture, candidate.SemanticSnapshot, candidate.PlayerId));
                HashSet<string> retainedKeys = new HashSet<string>(_liveSnapshots.Keys, CharacterStorageLayout.StorageKeyComparer);
                retainedKeys.UnionWith(_activeStorageLeases.Keys);
                HashSet<byte[]> distinctPrepared = new HashSet<byte[]>(ByteArrayReferenceComparer.Instance);
                long preparedBytes = 0;
                foreach (byte[] payload in preparedPayloads)
                    if (!_retainedPayloadReferences.ContainsKey(payload) && distinctPrepared.Add(payload))
                        preparedBytes = checked(preparedBytes + payload.LongLength);
                if (preparedBytes + _retainedSnapshotPayloadBytes > MaximumRetainedLiveSnapshotPayloadBytes ||
                    retainedKeys.Count > MaximumRetainedLiveSnapshots)
                    throw new CharacterStorageException("The retained live/connecting character snapshot capacity was exhausted.")
                        .WithPlayerMessage("sm_character_server_busy");
                BackupCapturePreparation preparation = new BackupCapturePreparation(storageKey, previous, capture, window);
                if (!_preparedBackupCaptures.TryAdd(capture.SessionId, preparation))
                    throw new CharacterStorageException("A backup capture is already prepared for this session.");
                // Use the same reference accounting as live/checkpoint payloads
                // so unrelated gameplay saves cannot overrun staged captures.
                AddPayloadReferenceLocked(capture.PayloadUnsafe);
                if (previous != null)
                {
                    AddPayloadReferenceLocked(previous.DurableEnvelope.PayloadUnsafe);
                    AddPayloadReferenceLocked(previous.LatestEnvelope.PayloadUnsafe);
                }
            }
        }

        private void RemovePreparedBackupCapture(Guid sessionId)
        {
            lock (_liveSnapshotGate)
            {
                if (!_preparedBackupCaptures.TryRemove(sessionId, out BackupCapturePreparation preparation)) return;
                RemovePayloadReferenceLocked(preparation.Capture.PayloadUnsafe);
                if (preparation.Previous != null)
                {
                    RemovePayloadReferenceLocked(preparation.Previous.DurableEnvelope.PayloadUnsafe);
                    RemovePayloadReferenceLocked(preparation.Previous.LatestEnvelope.PayloadUnsafe);
                }
            }
        }

        private CharacterSessionOpenResult OpenOrCreateSessionCore(
            CharacterIdentity identity,
            ZRpc? rpc)
        {
            string storageKey = _storageKeyProvider.DeriveStorageKey(identity);
            if (_unconfirmedAdminRestores.Contains(storageKey))
                throw new CharacterStorageException("An administrative restore could not be verified. This character is unavailable until server restart and disk verification.")
                    .WithPlayerMessage("sm_character_restore_unavailable");
            Guid sessionId = Guid.NewGuid();
            if (!_activeStorageLeases.TryAdd(storageKey, sessionId))
            {
                throw new CharacterProtocolException(
                    "This authoritative server character already has an active session.")
                    .WithPlayerMessage("sm_character_active");
            }

            bool sessionRegistered = false;
            bool completed = false;
            CharacterSkillObservationWindow? skillObservationWindow = null;
            try
            {
                CharacterLiveSnapshot? retainedLive;
                lock (_liveSnapshotGate)
                {
                    _liveSnapshots.TryGetValue(storageKey, out retainedLive);
                }

                CharacterEnvelope storedEnvelope;
                CharacterSemanticSnapshot storedSemanticSnapshot;
                long playerId;
                bool pendingInitialCommit;
                bool seedDurableLive = false;
                if (retainedLive != null)
                {
                    if (!retainedLive.Identity.EqualsIdentity(identity))
                    {
                        throw new CharacterStorageException(
                            "The retained live character identity does not match its storage key.")
                            .WithPlayerMessage("sm_character_stored_mismatch");
                    }

                    storedEnvelope = retainedLive.LatestEnvelope;
                    storedSemanticSnapshot = retainedLive.SemanticSnapshot;
                    playerId = retainedLive.PlayerId;
                    pendingInitialCommit = false;
                }
                else
                {
                    CharacterInitialSnapshotPreparation prepared =
                        _repository.PrepareInitialSnapshot(
                            identity,
                            storageKey,
                            sessionId,
                            () => _profileCodec.CreateInitialProfileBytes(
                                identity.CharacterName, Volatile.Read(ref _serverSettings)),
                            ValheimPlayerProfileCodec.SupportedPlayerProfileVersion);
                    storedEnvelope = prepared.Envelope;
                    CharacterValidatedSnapshot validatedStored =
                        ValidateSnapshotDetailed(
                            identity,
                            storedEnvelope.PayloadUnsafe);
                    storedSemanticSnapshot = validatedStored.SemanticSnapshot;
                    playerId = validatedStored.PlayerId;
                    pendingInitialCommit = prepared.PendingInitialCommit;
                    if (pendingInitialCommit && !_preparedBackupCaptures.IsEmpty)
                        ValidateCaptureReservations(identity, storageKey, playerId);
                    if (!pendingInitialCommit)
                    {
                        seedDurableLive = true;
                    }
                }

                IReadOnlyList<string> semanticObservations =
                    Array.Empty<string>();
                CharacterSemanticValidationResult auditFindings = CharacterSemanticValidationResult.Empty;
                IReadOnlyList<CharacterStatLimitFinding> statLimitFindings =
                    Array.Empty<CharacterStatLimitFinding>();
                bool initialEmptyProfile =
                    IsInitialUnmaterializedSnapshot(
                        storedEnvelope,
                        storedSemanticSnapshot);
                if (!initialEmptyProfile)
                {
                    CharacterSemanticValidationResult semantic =
                        _semanticValidator.EvaluateAuthoritative(
                            identity,
                            storedSemanticSnapshot);
                    if (semantic.Rejected)
                    {
                        throw new CharacterProtocolException(
                            "The stored authoritative character does not satisfy " +
                            "the current server policy: " +
                            semantic.RejectionReason);
                    }

                    semanticObservations = semantic.Observations;
                    statLimitFindings = semantic.StatLimitFindings;
                    auditFindings = semantic;
                }

                if (seedDurableLive)
                {
                    lock (_liveSnapshotGate)
                    {
                        if (_liveSnapshots.ContainsKey(storageKey))
                        {
                            throw new CharacterStorageException(
                                "A live character overlay appeared while its disk " +
                                "snapshot was being opened.");
                        }

                        AddLiveSnapshotLocked(
                            storageKey,
                            new CharacterLiveSnapshot(
                                identity,
                                storedEnvelope,
                                storedEnvelope,
                                storedSemanticSnapshot,
                                playerId));
                    }
                }

                CharacterSaveRateLimiter saveRateLimiter =
                    GetSaveRateLimiter(storageKey);
                skillObservationWindow =
                    GetSkillObservationWindow(
                        storageKey,
                        storedEnvelope,
                        storedSemanticSnapshot);
                CharacterEnvelope? pendingEnvelope =
                    pendingInitialCommit ? storedEnvelope : null;
                CharacterSession session = rpc == null
                    ? CharacterSession.CreateLocalHost(
                        identity,
                        storageKey,
                        sessionId,
                        storedEnvelope.Revision,
                        storedEnvelope.PayloadSha256Unsafe,
                        storedSemanticSnapshot,
                        playerId,
                        saveRateLimiter,
                        skillObservationWindow,
                        pendingEnvelope)
                    : new CharacterSession(
                        rpc,
                        identity,
                        storageKey,
                        sessionId,
                        storedEnvelope.Revision,
                        storedEnvelope.PayloadSha256Unsafe,
                        storedSemanticSnapshot,
                        playerId,
                        saveRateLimiter,
                        skillObservationWindow,
                        pendingEnvelope);

                CharacterEnvelope loadResponse = CharacterEnvelope.CreateWithOrigin(
                    CharacterEnvelopeKind.Snapshot,
                    storedEnvelope.Revision,
                    pendingInitialCommit
                        ? storedEnvelope.BaseRevision
                        : storedEnvelope.Revision,
                    sessionId,
                    identity,
                    DateTime.UtcNow,
                    storedEnvelope.ValheimProfileVersion,
                    storedEnvelope.PayloadUnsafe,
                    storedEnvelope.RequiresFreshLocalCharacter);
                ZPackage networkPackage =
                    _envelopeCodec.ToZPackage(loadResponse);
                CharacterSessionOpenResult openResult =
                    new CharacterSessionOpenResult(
                        auditFindings,
                        loadResponse,
                        networkPackage,
                        pendingInitialCommit,
                        semanticObservations,
                        statLimitFindings);

                if (rpc == null)
                {
                    Volatile.Write(ref _localHostSession, session);
                }
                else if (!_serverSessions.TryAdd(rpc, session))
                {
                    throw new InvalidOperationException(
                        "A character session is already open for this RPC.");
                }

                sessionRegistered = true;
                completed = true;
                return openResult;
            }
            finally
            {
                if (!completed)
                {
                    skillObservationWindow?.EndSession();
                    if (sessionRegistered)
                    {
                        if (rpc == null)
                        {
                            Volatile.Write(ref _localHostSession, null);
                        }
                        else
                        {
                            CharacterSession ignored;
                            _serverSessions.TryRemove(rpc, out ignored);
                        }
                    }

                    ReleaseStorageLease(storageKey, sessionId);
                }
            }
        }

        internal bool FinalizePendingInitialSnapshot(ZRpc rpc)
        {
            ThrowIfDisposed();
            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                if (rpc == null)
                {
                    throw new ArgumentNullException(nameof(rpc));
                }

                if (!_serverSessions.TryGetValue(rpc, out CharacterSession session))
                {
                    throw new CharacterProtocolException(
                        "No attested character session is open for initial persistence.");
                }

                return FinalizePendingInitialSnapshotCore(session);
            }
        }

        internal bool FinalizePendingLocalHostSnapshot(Guid sessionId)
        {
            ThrowIfDisposed();
            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                return FinalizePendingInitialSnapshotCore(
                    RequireLocalHostSession(sessionId));
            }
        }

        private bool FinalizePendingInitialSnapshotCore(CharacterSession session)
        {
            lock (session.SaveLock)
            {
                if (session.IsClosed)
                {
                    throw new CharacterProtocolException(
                        "The character session closed before initial persistence.");
                }

                CharacterEnvelope? pending =
                    session.GetPendingInitialEnvelope();
                if (pending == null)
                {
                    return false;
                }

                if (session.PendingBackupCapture)
                    return FinalizeBackupCapture(session, pending);

                CharacterStoredSnapshot committed =
                    _repository.FinalizePreparedInitialSnapshot(
                        session.Identity,
                        session.StorageKey,
                        pending);
                session.CompleteInitialCommit(committed.Envelope);
                session.CaptureCurrentSemanticState(
                    out long currentRevision,
                    out CharacterSemanticSnapshot currentSemanticSnapshot);
                if (currentRevision != committed.Envelope.Revision)
                {
                    throw new CharacterStorageException(
                        "The prepared first-join revision changed before live activation.");
                }

                lock (_liveSnapshotGate)
                {
                    if (_liveSnapshots.ContainsKey(session.StorageKey))
                    {
                        throw new CharacterStorageException(
                            "A live character overlay already exists for the prepared first join.");
                    }

                    AddLiveSnapshotLocked(
                        session.StorageKey,
                        new CharacterLiveSnapshot(
                            session.Identity,
                            committed.Envelope,
                            committed.Envelope,
                            currentSemanticSnapshot,
                            session.PlayerId));
                }

                return true;
            }
        }

        private bool FinalizeBackupCapture(CharacterSession session, CharacterEnvelope pending)
        {
            if (!_preparedBackupCaptures.TryGetValue(session.SessionId, out BackupCapturePreparation prepared))
                throw new CharacterStorageException("The prepared backup capture is no longer available.");
            session.CaptureCurrentSemanticState(out long revision, out CharacterSemanticSnapshot semantic);
            if (revision != pending.Revision)
                throw new CharacterStorageException("The backup capture revision changed before activation.");

            lock (_liveSnapshotGate)
            {
                _liveSnapshots.TryGetValue(session.StorageKey, out CharacterLiveSnapshot current);
                CharacterLiveSnapshot? previous = prepared.Previous;
                if (current != null)
                {
                    if (previous == null || !current.LatestEnvelope.MatchesSnapshot(previous.LatestEnvelope))
                        throw new CharacterStorageException("The server character changed while its backup capture was pending.");
                    // A retained checkpoint may have advanced disk while the
                    // client validated the detached profile. Keep that new CAS base.
                    previous = current;
                }
                CharacterLiveSnapshot accepted = new CharacterLiveSnapshot(session.Identity,
                    previous?.DurableEnvelope ?? pending, pending, semantic, session.PlayerId);
                bool promoteInitial = previous != null && previous.DurableEnvelope.RequiresFreshLocalCharacter &&
                    !pending.RequiresFreshLocalCharacter;
                if (promoteInitial) accepted = accepted.WithDurable(pending);
                EnsureLiveSnapshotCapacityLocked(session.StorageKey, accepted);

                if (previous == null)
                    _repository.FinalizePreparedInitialSnapshot(session.Identity, session.StorageKey, pending,
                        establishedCapture: !pending.RequiresFreshLocalCharacter);
                else if (promoteInitial)
                    _repository.PromotePendingSnapshot(session.Identity, session.StorageKey,
                        previous.DurableEnvelope, pending);

                if (current == null) AddLiveSnapshotLocked(session.StorageKey, accepted);
                else SetLiveSnapshotLocked(session.StorageKey, accepted);
                session.CompleteInitialCommit(pending);
                RemovePreparedBackupCapture(session.SessionId);
            }
            // Publish the new observation window only with the accepted capture.
            // An aborted attempt leaves the prior disconnected window untouched.
            lock (_saveRateLimiterGate)
            {
                PruneStalePerCharacterState();
                if (_skillObservationWindows.ContainsKey(session.StorageKey) ||
                    _skillObservationWindows.Count < MaximumRetainedSkillObservationWindows)
                    _skillObservationWindows[session.StorageKey] = prepared.SkillWindow;
            }
            return true;
        }

        /// <summary>
        /// Parses and accepts a save into the process-local authoritative shadow for
        /// an already opened, transport-bound server session.
        /// The caller decides whether to send ResponsePackage, log, or disconnect.
        /// </summary>
        public CharacterSaveResult HandleSaveRequest(
            ZRpc rpc, ZPackage package, bool requireFullProfile = false)
        {
            ThrowIfDisposed();
            if (rpc == null)
            {
                throw new ArgumentNullException(nameof(rpc));
            }

            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            CharacterSession session;
            if (!_serverSessions.TryGetValue(rpc, out session))
            {
                throw new CharacterProtocolException(
                    "No attested character session is open for this RPC.");
            }

            return HandleSaveRequestCore(
                session,
                () =>
                {
                    CharacterEnvelope request = _envelopeCodec.FromZPackage(package);
                    if (requireFullProfile && request.Kind != CharacterEnvelopeKind.SaveRequest)
                    {
                        throw new CharacterProtocolException(
                            "The operational kick requires one final full-profile snapshot.");
                    }
                    return request;
                });
        }

        internal CharacterSaveResult HandleLocalHostSaveRequest(
            Guid sessionId,
            CharacterEnvelope request)
        {
            ThrowIfDisposed();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            return HandleSaveRequestCore(
                RequireLocalHostSession(sessionId),
                () => request);
        }

        private CharacterSaveResult HandleSaveRequestCore(
            CharacterSession session,
            Func<CharacterEnvelope> readRequest)
        {
            session.RecordSaveRequest();
            lock (session.SaveLock)
            {
                ThrowIfDisposed();
                string terminalError =
                    "The character save request did not complete.";
                bool terminalStateRecorded = false;
                try
                {
                    if (session.PendingInitialCommit)
                    {
                        terminalError =
                            "The first-join snapshot was not persisted before save admission.";
                        return CreateRejectedResult(
                            session,
                            session.CurrentRevision,
                            session.CurrentRevision,
                            terminalError,
                            null);
                    }

                    if (session.IsClosed)
                    {
                        terminalError =
                            "The character session is already closed.";
                        return CreateRejectedResult(
                            session,
                            session.CurrentRevision,
                            session.CurrentRevision,
                            terminalError,
                            null);
                    }

                    CharacterEnvelope request;
                    try
                    {
                        request = readRequest();
                        ValidateSaveRequest(session, request, DateTime.UtcNow);
                    }
                    catch (Exception exception) when (
                        !IntegrityCanonical.IsFatal(exception))
                    {
                        terminalError = exception.Message;
                        return CreateRejectedResult(
                            session,
                            session.CurrentRevision,
                            session.CurrentRevision,
                            exception.Message,
                            null);
                    }

                    if (!session.TryConsumeSaveToken(request.Kind))
                    {
                        terminalError =
                            "The character save rate limit was exceeded.";
                        return CreateRejectedResult(
                            session,
                            session.CurrentRevision,
                            request.BaseRevision,
                            terminalError,
                            null);
                    }

                    session.BeginSaveAttempt();
                    CharacterEnvelope admissionRequest;
                    CharacterValidatedSnapshot validatedRequest;
                    CharacterEnvelope acceptedResponse;
                    ZPackage acceptedResponsePackage;
                    CharacterLiveSnapshot liveBase;
                    try
                    {
                        lock (_liveSnapshotGate)
                        {
                            if (!_liveSnapshots.TryGetValue(
                                    session.StorageKey,
                                    out liveBase) ||
                                !liveBase.Identity.EqualsIdentity(
                                    session.Identity) ||
                                liveBase.LatestEnvelope.Revision !=
                                    session.CurrentRevision)
                            {
                                throw new CharacterStorageException(
                                    "The live character overlay does not match its active session.");
                            }
                        }

                        if (request.Kind ==
                            CharacterEnvelopeKind.InventorySaveRequest)
                        {
                            if (liveBase.LatestEnvelope.RequiresFreshLocalCharacter)
                            {
                                throw new CharacterProtocolException(
                                    "The new character requires an accepted full save before inventory-only saves.");
                            }
                            byte[] materializedProfile =
                                _profileCodec.ReplaceInventorySnapshot(
                                    session.Identity,
                                    liveBase.LatestEnvelope.PayloadUnsafe,
                                    request.PayloadUnsafe,
                                    out validatedRequest);
                            admissionRequest = CharacterEnvelope.Create(
                                CharacterEnvelopeKind.SaveRequest,
                                request.Revision,
                                request.BaseRevision,
                                request.SessionId,
                                session.Identity,
                                request.CreatedUtc,
                                request.ValheimProfileVersion,
                                materializedProfile);
                        }
                        else
                        {
                            admissionRequest = request;
                            validatedRequest = ValidateSnapshotDetailed(
                                session.Identity,
                                admissionRequest.PayloadUnsafe);
                            if (liveBase.LatestEnvelope.RequiresFreshLocalCharacter &&
                                !validatedRequest.SemanticSnapshot.HasPlayerData)
                            {
                                throw new CharacterProtocolException(
                                    "A first full character save must materialize Player data.");
                            }
                        }

                        if (validatedRequest.PlayerId != session.PlayerId)
                        {
                            throw new CharacterProtocolException(
                                "The PlayerProfile ID changed during the server session.");
                        }

                        acceptedResponse = CharacterEnvelope.Create(
                            CharacterEnvelopeKind.SaveAccepted,
                            request.Revision,
                            request.BaseRevision,
                            session.SessionId,
                            session.Identity,
                            DateTime.UtcNow,
                            request.ValheimProfileVersion,
                            Array.Empty<byte>());
                        acceptedResponsePackage =
                            _envelopeCodec.ToZPackage(acceptedResponse);
                    }
                    catch (Exception exception) when (
                        !IntegrityCanonical.IsFatal(exception))
                    {
                        terminalError = exception.Message;
                        return CreateRejectedResult(
                            session,
                            session.CurrentRevision,
                            session.CurrentRevision,
                            exception.Message,
                            null);
                    }

                    RepositoryCommitOutcome outcome;
                    try
                    {
                        outcome = _repository.EvaluateLiveCandidate(
                            session,
                            liveBase.LatestEnvelope,
                            admissionRequest,
                            validatedRequest.SemanticSnapshot,
                            DateTime.UtcNow);
                    }
                    catch (Exception exception) when (
                        !IntegrityCanonical.IsFatal(exception))
                    {
                        terminalError = exception.Message;
                        return CreateRejectedResult(
                            session,
                            session.CurrentRevision,
                            request.BaseRevision,
                            exception.Message,
                            null);
                    }

                    // An inventory-only upload retains the last full profile's
                    // maxima. It is not fresh evidence about current stats.
                    IReadOnlyList<CharacterStatLimitFinding> statLimitFindings =
                        request.Kind == CharacterEnvelopeKind.InventorySaveRequest
                            ? Array.Empty<CharacterStatLimitFinding>()
                            : outcome.StatLimitFindings;
                    if (outcome.Status != CharacterCommitStatus.Accepted)
                    {
                        terminalError = outcome.Error;
                        return CreateRejectedResult(
                            session,
                            outcome.Current.Revision,
                            request.BaseRevision,
                            outcome.Error,
                            outcome.Current.PayloadUnsafe,
                            outcome.SemanticObservations,
                            statLimitFindings, outcome.AuditFindings);
                    }

                    // A new profile's first full save must publish one generation
                    // to both native disk and RAM before it can be acknowledged.
                    // Capacity is checked before the durable action. SaveLock,
                    // the live gate and the session revision barrier then prevent
                    // a successful final .fch from being followed by an ordinary
                    // capacity/CAS rejection.
                    bool firstFullPromotionRequired =
                        liveBase.LatestEnvelope.RequiresFreshLocalCharacter &&
                        !outcome.Current.RequiresFreshLocalCharacter;

                    lock (_liveSnapshotGate)
                    {
                        if (!_liveSnapshots.TryGetValue(
                                session.StorageKey,
                                out CharacterLiveSnapshot currentLive) ||
                            !currentLive.LatestEnvelope.MatchesSnapshot(
                                liveBase.LatestEnvelope))
                        {
                            terminalError =
                                "The live character base changed during save admission.";
                            session.MarkClosed();
                            throw new CharacterStorageException(terminalError);
                        }

                        CharacterLiveSnapshot acceptedLive =
                            currentLive.WithLatest(
                                outcome.Current,
                                validatedRequest.SemanticSnapshot,
                                session.PlayerId);
                        if (firstFullPromotionRequired)
                        {
                            acceptedLive = acceptedLive.WithDurable(
                                outcome.Current);
                        }
                        EnsureLiveSnapshotCapacityLocked(
                            session.StorageKey,
                            acceptedLive);

                        if (!session.TryCommitRevision(
                                request.BaseRevision,
                                outcome.Current.Revision,
                                outcome.Current.PayloadSha256Unsafe,
                                validatedRequest.SemanticSnapshot,
                                firstFullPromotionRequired
                                    ? () => _repository.PromotePendingSnapshot(
                                        session.Identity,
                                        session.StorageKey,
                                        liveBase.DurableEnvelope,
                                        outcome.Current)
                                    : null))
                        {
                            terminalError =
                                "The character shadow was accepted, but the active " +
                                "session revision could not be advanced. Reconnect to " +
                                "reload the authoritative live snapshot.";
                            session.MarkClosed();
                            throw new CharacterStorageException(terminalError);
                        }

                        SetLiveSnapshotLocked(
                            session.StorageKey,
                            acceptedLive);
                    }

                    session.RecordSuccessfulAcceptance(outcome.Current.Revision);
                    session.CompleteSaveSuccess();
                    terminalStateRecorded = true;
                    return new CharacterSaveResult(
                        outcome.AuditFindings,
                        CharacterCommitStatus.Accepted,
                        acceptedResponse,
                        acceptedResponsePackage,
                        string.Empty,
                        outcome.SemanticObservations,
                        outcome.PreviousSemanticSnapshot,
                        outcome.CurrentSemanticSnapshot,
                        outcome.Current.PayloadUnsafe.Length,
                        CharacterCrypto.ToLowerHex(
                            outcome.Current.PayloadSha256Unsafe),
                        statLimitFindings);
                }
                finally
                {
                    if (!terminalStateRecorded)
                    {
                        session.CompleteSaveFailure(terminalError);
                    }
                }
            }
        }

        public bool TryGetServerSession(ZRpc rpc, out CharacterSession? session)
        {
            return _serverSessions.TryGetValue(rpc, out session);
        }

        internal bool TryGetLocalHostSession(
            Guid sessionId,
            out CharacterSession? session)
        {
            CharacterSession? current = Volatile.Read(ref _localHostSession);
            if (Volatile.Read(ref _disposeState) != 0 ||
                sessionId == Guid.Empty || current == null ||
                current.SessionId != sessionId || !current.IsLocalHost ||
                current.IsClosed)
            {
                session = null;
                return false;
            }

            session = current;
            return true;
        }

        private CharacterSession RequireLocalHostSession(Guid sessionId)
        {
            ThrowIfDisposed();
            if (!TryGetLocalHostSession(sessionId, out CharacterSession? session) ||
                session == null)
            {
                throw new CharacterProtocolException(
                    "No matching local host character session is open.");
            }

            return session;
        }

        /// <summary>
        /// Returns a full acknowledged snapshot, rebound to this host session.
        /// It is not a disk-commit receipt; checkpoint persistence is separate.
        /// </summary>
        internal CharacterEnvelope GetLocalHostSnapshot(Guid sessionId)
        {
            CharacterSession session = RequireLocalHostSession(sessionId);
            lock (session.SaveLock)
            {
                ThrowIfDisposed();
                if (session.IsClosed || session.PendingInitialCommit)
                {
                    throw new CharacterProtocolException(
                        "The local host snapshot is not active.");
                }

                lock (_liveSnapshotGate)
                {
                    if (!_liveSnapshots.TryGetValue(
                            session.StorageKey,
                            out CharacterLiveSnapshot live) ||
                        !live.Identity.EqualsIdentity(session.Identity) ||
                        live.PlayerId != session.PlayerId ||
                        live.LatestEnvelope.Revision != session.CurrentRevision)
                    {
                        throw new CharacterStorageException(
                            "The local host live snapshot does not match its session.");
                    }

                    CharacterEnvelope latest = live.LatestEnvelope;
                    return CharacterEnvelope.CreateWithOrigin(
                        CharacterEnvelopeKind.Snapshot,
                        latest.Revision,
                        latest.Revision,
                        session.SessionId,
                        session.Identity,
                        latest.CreatedUtc,
                        latest.ValheimProfileVersion,
                        latest.PayloadUnsafe,
                        latest.RequiresFreshLocalCharacter);
                }
            }
        }

        /// <summary>
        /// Freezes the current full live overlay without blocking later client
        /// revisions. The returned batch is safe to retain until the matching
        /// world disk save completes.
        /// </summary>
        internal CharacterCheckpointBatch BeginCheckpoint()
        {
            ThrowIfDisposed();
            lock (_checkpointCommitGate)
            {
                // Dispose marks the service before waiting for this gate. A
                // caller that passed the optimistic check while Dispose owned
                // the gate must not continue against the cleared repository
                // state afterward.
                ThrowIfDisposed();
                lock (_liveSnapshotGate)
                {
                    List<CharacterCheckpointEntry> entries =
                        new List<CharacterCheckpointEntry>(
                            _liveSnapshots.Count);
                    foreach (KeyValuePair<string, CharacterLiveSnapshot> pair
                             in _liveSnapshots)
                    {
                        CharacterLiveSnapshot live = pair.Value;
                        // A repeated backup connection can select a different
                        // not-yet-spawned local header. Its first full save owns
                        // .pending promotion; world checkpoints retain the prior
                        // durable header until Player data exists.
                        if (live.LatestEnvelope.RequiresFreshLocalCharacter &&
                            live.LatestEnvelope.Revision > live.DurableEnvelope.Revision)
                            continue;
                        entries.Add(
                            new CharacterCheckpointEntry(
                                pair.Key,
                                live.Identity,
                                live.DurableEnvelope,
                                live.LatestEnvelope));
                    }

                    entries.Sort(
                        (left, right) => StringComparer.Ordinal.Compare(
                            left.StorageKey,
                            right.StorageKey));
                    if (entries.Count != 0 &&
                        _registeredCheckpoints.Count >= MaximumRegisteredCheckpoints)
                    {
                        throw new CharacterStorageException(
                            "Too many character checkpoint generations are retained.");
                    }

                    CharacterCheckpointBatch checkpoint =
                        new CharacterCheckpointBatch(
                        _checkpointOwnerId,
                        Guid.NewGuid(),
                        DateTime.UtcNow,
                        entries.AsReadOnly());
                    if (entries.Count == 0)
                    {
                        return checkpoint;
                    }

                    Dictionary<string, CharacterCheckpointEntry> registeredEntries =
                        new Dictionary<string, CharacterCheckpointEntry>(
                            CharacterStorageLayout.StorageKeyComparer);
                    int retainedCount = 0;
                    try
                    {
                        for (int index = 0; index < entries.Count; ++index)
                        {
                            CharacterCheckpointEntry entry = entries[index];
                            registeredEntries.Add(entry.StorageKey, entry);
                            AddCheckpointEntryPayloadReferencesLocked(entry);
                            ++retainedCount;
                        }

                        _registeredCheckpoints.Add(
                            checkpoint.CheckpointId,
                            registeredEntries);
                    }
                    catch
                    {
                        for (int index = retainedCount - 1; index >= 0; --index)
                        {
                            RemoveCheckpointEntryPayloadReferencesLocked(
                                entries[index]);
                        }

                        throw;
                    }

                    return checkpoint;
                }
            }
        }

        /// <summary>
        /// Commits one retained checkpoint entry. The repository base is
        /// safely rebased to this service's latest trusted durable revision so
        /// a later world cutoff can follow an older retry without weakening
        /// on-disk tamper checks.
        /// </summary>
        internal string CommitCheckpointEntry(
            CharacterCheckpointBatch checkpoint,
            CharacterCheckpointEntry entry)
        {
            ThrowIfDisposed();
            ValidateCheckpointOwner(checkpoint);
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                CharacterEnvelope expectedDurableBase;
                lock (_liveSnapshotGate)
                {
                    RequireRegisteredCheckpointEntryLocked(checkpoint, entry);
                    if (!_liveSnapshots.TryGetValue(
                            entry.StorageKey,
                            out CharacterLiveSnapshot current))
                    {
                        expectedDurableBase = entry.DurableBase;
                    }
                    else
                    {
                        CharacterEnvelope durable = current.DurableEnvelope;
                        if (durable.Revision > entry.Snapshot.Revision)
                        {
                            ReleaseCheckpointEntryLocked(checkpoint, entry);
                            return string.Empty;
                        }

                        if (durable.Revision == entry.Snapshot.Revision)
                        {
                            if (!durable.MatchesSnapshot(entry.Snapshot))
                            {
                                throw new CharacterStorageException(
                                    "The live durable character revision conflicts " +
                                    "with the retained checkpoint target.");
                            }

                            if (current.LatestEnvelope.MatchesSnapshot(
                                    entry.Snapshot) &&
                                !_activeStorageLeases.ContainsKey(entry.StorageKey))
                            {
                                RemoveLiveSnapshotLocked(entry.StorageKey);
                            }

                            ReleaseCheckpointEntryLocked(checkpoint, entry);
                            return string.Empty;
                        }

                        if (durable.Revision < entry.DurableBase.Revision ||
                            (durable.Revision == entry.DurableBase.Revision &&
                             !durable.MatchesSnapshot(entry.DurableBase)))
                        {
                            throw new CharacterStorageException(
                                "The live character durable base moved outside " +
                                "the retained checkpoint chain.");
                        }

                        expectedDurableBase = durable;
                    }
                }

                string warning = _repository.PersistCheckpointEntry(
                    entry,
                    expectedDurableBase);
                lock (_liveSnapshotGate)
                {
                    RequireRegisteredCheckpointEntryLocked(checkpoint, entry);
                    if (_liveSnapshots.TryGetValue(
                            entry.StorageKey,
                            out CharacterLiveSnapshot current))
                    {
                        if (current.DurableEnvelope.Revision >
                            entry.Snapshot.Revision)
                        {
                            ReleaseCheckpointEntryLocked(checkpoint, entry);
                            return warning;
                        }

                        if (!current.DurableEnvelope.MatchesSnapshot(
                                entry.Snapshot))
                        {
                            if (!current.DurableEnvelope.MatchesSnapshot(
                                    expectedDurableBase))
                            {
                                throw new CharacterStorageException(
                                    "The live character durable base changed during " +
                                    "checkpoint commit.");
                            }

                            if (current.LatestEnvelope.MatchesSnapshot(
                                    entry.Snapshot) &&
                                !_activeStorageLeases.ContainsKey(entry.StorageKey))
                            {
                                RemoveLiveSnapshotLocked(entry.StorageKey);
                            }
                            else
                            {
                                SetLiveSnapshotLocked(
                                    entry.StorageKey,
                                    current.WithDurable(entry.Snapshot));
                            }
                        }

                        else if (current.LatestEnvelope.MatchesSnapshot(
                                     entry.Snapshot) &&
                                 !_activeStorageLeases.ContainsKey(entry.StorageKey))
                        {
                            RemoveLiveSnapshotLocked(entry.StorageKey);
                        }
                    }

                    ReleaseCheckpointEntryLocked(checkpoint, entry);
                }

                return warning;
            }
        }

        /// <summary>
        /// Releases a frozen generation after its world disk save failed or
        /// was abandoned before a worker could claim it. A pending partial
        /// character commit deliberately keeps the registration until its
        /// exact retry succeeds.
        /// </summary>
        internal void DiscardCheckpoint(CharacterCheckpointBatch checkpoint)
        {
            ThrowIfDisposed();
            ValidateCheckpointOwner(checkpoint);

            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                lock (_liveSnapshotGate)
                {
                    ReleaseCheckpointPayloadsLocked(checkpoint);
                }
            }
        }

        internal void DiscardCheckpointEntry(
            CharacterCheckpointBatch checkpoint,
            CharacterCheckpointEntry entry)
        {
            ThrowIfDisposed();
            ValidateCheckpointOwner(checkpoint);
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            lock (_checkpointCommitGate)
            {
                ThrowIfDisposed();
                lock (_liveSnapshotGate)
                {
                    RequireRegisteredCheckpointEntryLocked(checkpoint, entry);
                    ReleaseCheckpointEntryLocked(checkpoint, entry);
                }
            }
        }

        internal IReadOnlyList<CharacterSessionSaveHealthSnapshot>
            GetOpenSessionSaveHealth()
        {
            long capturedTimestamp = Stopwatch.GetTimestamp();
            List<CharacterSessionSaveHealthSnapshot> snapshots =
                new List<CharacterSessionSaveHealthSnapshot>();
            foreach (CharacterSession session in EnumerateOpenSessions())
            {
                CharacterSessionSaveHealthSnapshot? snapshot;
                if (session.TryCreateOpenSaveHealthSnapshot(
                        capturedTimestamp,
                        out snapshot) &&
                    snapshot != null)
                {
                    snapshots.Add(snapshot);
                }
            }

            return snapshots.AsReadOnly();
        }

        internal IReadOnlyList<CharacterSessionSaveHealthSnapshot>
            ClaimLongUnsavedWarnings(TimeSpan threshold)
        {
            if (threshold <= TimeSpan.Zero)
            {
                return Array.Empty<CharacterSessionSaveHealthSnapshot>();
            }

            long capturedTimestamp = Stopwatch.GetTimestamp();
            List<CharacterSessionSaveHealthSnapshot> claimed =
                new List<CharacterSessionSaveHealthSnapshot>();
            foreach (CharacterSession session in EnumerateOpenSessions())
            {
                CharacterSessionSaveHealthSnapshot? snapshot;
                if (session.TryClaimLongUnsavedWarning(
                        threshold,
                        capturedTimestamp,
                        out snapshot) &&
                    snapshot != null)
                {
                    claimed.Add(snapshot);
                }
            }

            return claimed.AsReadOnly();
        }

        private IEnumerable<CharacterSession> EnumerateOpenSessions()
        {
            foreach (CharacterSession session in _serverSessions.Values)
            {
                yield return session;
            }

            CharacterSession? local = Volatile.Read(ref _localHostSession);
            if (local != null)
            {
                yield return local;
            }
        }

        public void CloseServerSession(ZRpc rpc)
        {
            if (rpc == null)
            {
                return;
            }

            CharacterSession session;
            if (!_serverSessions.TryRemove(rpc, out session))
            {
                return;
            }

            CloseSessionCore(session);
        }

        internal void CloseLocalHostSession(Guid sessionId)
        {
            lock (_checkpointCommitGate)
            {
                CharacterSession? session = _localHostSession;
                if (session == null || session.SessionId != sessionId)
                {
                    return;
                }

                // A stale close cannot retire a replacement host generation.
                Volatile.Write(ref _localHostSession, null);
                CloseSessionCore(session);
            }
        }

        private void CloseSessionCore(CharacterSession session)
        {
            lock (session.SaveLock)
            {
                session.MarkClosed();
                RemovePreparedBackupCapture(session.SessionId);
                session.TouchSaveRateLimit();
                ReleaseStorageLease(session.StorageKey, session.SessionId);
            }
        }

        private void ValidateSaveRequest(
            CharacterSession session,
            CharacterEnvelope request,
            DateTime serverUtc)
        {
            if (request.RequiresFreshLocalCharacter)
            {
                throw new CharacterProtocolException(
                    "Client saves may not set authoritative initial-origin metadata.");
            }

            if (request.Kind != CharacterEnvelopeKind.SaveRequest &&
                request.Kind != CharacterEnvelopeKind.InventorySaveRequest)
            {
                throw new CharacterProtocolException(
                    "The character RPC did not contain a save request.");
            }

            if (request.SessionId != session.SessionId)
            {
                throw new CharacterProtocolException(
                    "The save request session ID is invalid.");
            }

            if (!string.Equals(
                    request.AccountId,
                    session.Identity.AccountId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    request.CharacterName,
                    session.Identity.CharacterName,
                    StringComparison.Ordinal))
            {
                throw new CharacterProtocolException(
                    "The save request identity does not match its character session.");
            }

            if (request.BaseRevision != session.CurrentRevision)
            {
                throw new CharacterProtocolException(
                    "The save request base revision is stale.");
            }

            if (request.ValheimProfileVersion !=
                ValheimPlayerProfileCodec.SupportedPlayerProfileVersion)
            {
                throw new CharacterProtocolException(
                    "The save request PlayerProfile version is unsupported.");
            }

            if (request.PayloadLength == 0)
            {
                throw new CharacterProtocolException(
                    "A character save request may not have an empty payload.");
            }

            if (request.Kind == CharacterEnvelopeKind.InventorySaveRequest &&
                request.PayloadLength >
                    ValheimPlayerProfileCodec.MaximumInventorySnapshotBytes)
            {
                throw new CharacterProtocolException(
                    "The inventory save request exceeds its payload limit.");
            }

            DateTime oldestAccepted = serverUtc - _options.MaxSaveRequestAge;
            DateTime newestAccepted = serverUtc + _options.MaxSaveRequestFutureSkew;
            if (request.CreatedUtc < oldestAccepted ||
                request.CreatedUtc > newestAccepted)
            {
                throw new CharacterProtocolException(
                    "The save request UTC timestamp is outside the accepted window.");
            }
        }

        private CharacterValidatedSnapshot ValidateSnapshotDetailed(
            CharacterIdentity identity,
            byte[] rawPlayerProfile)
        {
            return _profileCodec.ExtractValidatedSnapshot(
                identity,
                rawPlayerProfile);
        }

        internal static bool IsInitialUnmaterializedSnapshot(
            CharacterEnvelope envelope,
            CharacterSemanticSnapshot snapshot)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            return envelope.Kind == CharacterEnvelopeKind.Snapshot &&
                   (envelope.RequiresFreshLocalCharacter ||
                    (envelope.Revision == 1 && envelope.BaseRevision == 0)) &&
                   !snapshot.HasPlayerData;
        }

        private void ReleaseStorageLease(string storageKey, Guid sessionId)
        {
            ICollection<KeyValuePair<string, Guid>> leases =
                _activeStorageLeases;
            leases.Remove(
                new KeyValuePair<string, Guid>(storageKey, sessionId));
        }

        private CharacterSaveRateLimiter GetSaveRateLimiter(string storageKey)
        {
            lock (_saveRateLimiterGate)
            {
                ++_saveRateLimiterOpenCount;
                if (_saveRateLimiterOpenCount >= 64 ||
                    _saveRateLimiters.Count > 4096 ||
                    _skillObservationWindows.Count >
                        MaximumRetainedSkillObservationWindows)
                {
                    PruneStalePerCharacterState();
                    _saveRateLimiterOpenCount = 0;
                }

                CharacterSaveRateLimiter limiter;
                if (!_saveRateLimiters.TryGetValue(storageKey, out limiter))
                {
                    limiter = new CharacterSaveRateLimiter(
                        _options.SaveRequestBurstCapacity,
                        _options.SaveRequestTokenRefillInterval);
                    _saveRateLimiters.Add(storageKey, limiter);
                }

                limiter.Touch();
                return limiter;
            }
        }

        private CharacterSkillObservationWindow
            GetSkillObservationWindow(
            string storageKey,
            CharacterEnvelope current,
            CharacterSemanticSnapshot currentSnapshot)
        {
            lock (_saveRateLimiterGate)
            {
                CharacterSkillObservationWindow window;
                if (!_skillObservationWindows.TryGetValue(
                        storageKey,
                        out window))
                {
                    if (_skillObservationWindows.Count >=
                        MaximumRetainedSkillObservationWindows)
                    {
                        string? evictedKey = null;
                        foreach (string candidateKey in
                                 _skillObservationWindows.Keys)
                        {
                            if (!_activeStorageLeases.ContainsKey(
                                    candidateKey))
                            {
                                evictedKey = candidateKey;
                                break;
                            }
                        }

                        if (evictedKey != null)
                        {
                            _skillObservationWindows.Remove(evictedKey);
                        }
                    }

                    window = new CharacterSkillObservationWindow(
                        current.Revision,
                        current.PayloadSha256Unsafe,
                        currentSnapshot);
                    if (_skillObservationWindows.Count <
                        MaximumRetainedSkillObservationWindows)
                    {
                        _skillObservationWindows.Add(storageKey, window);
                    }
                }

                window.BeginSession(
                    current.Revision,
                    current.PayloadSha256Unsafe,
                    currentSnapshot);
                return window;
            }
        }

        private void PruneStalePerCharacterState()
        {
            List<string> staleKeys = new List<string>();
            foreach (KeyValuePair<string, CharacterSaveRateLimiter> pair
                     in _saveRateLimiters)
            {
                if (!_activeStorageLeases.ContainsKey(pair.Key) &&
                    pair.Value.IsStale(SaveRateLimiterRetention))
                {
                    staleKeys.Add(pair.Key);
                }
            }

            for (int index = 0; index < staleKeys.Count; ++index)
            {
                _saveRateLimiters.Remove(staleKeys[index]);
            }

            staleKeys.Clear();
            foreach (KeyValuePair<string, CharacterSkillObservationWindow>
                     pair in _skillObservationWindows)
            {
                if (!_activeStorageLeases.ContainsKey(pair.Key) &&
                    pair.Value.IsStale(SaveRateLimiterRetention))
                {
                    staleKeys.Add(pair.Key);
                }
            }

            for (int index = 0; index < staleKeys.Count; ++index)
            {
                _skillObservationWindows.Remove(staleKeys[index]);
            }
        }

        private void AddLiveSnapshotLocked(
            string storageKey,
            CharacterLiveSnapshot live)
        {
            if (_liveSnapshots.ContainsKey(storageKey))
            {
                throw new CharacterStorageException(
                    "A live character overlay already exists for this storage key.");
            }

            EnsureLiveSnapshotCapacityLocked(storageKey, live);
            _liveSnapshots.Add(storageKey, live);
            AddLivePayloadReferencesLocked(live);
        }

        private void SetLiveSnapshotLocked(
            string storageKey,
            CharacterLiveSnapshot live)
        {
            if (!_liveSnapshots.TryGetValue(
                    storageKey,
                    out CharacterLiveSnapshot previous))
            {
                throw new CharacterStorageException(
                    "The live character overlay disappeared before replacement.");
            }

            if (!previous.Identity.EqualsIdentity(live.Identity))
            {
                throw new CharacterStorageException(
                    "A different character identity resolves to the same storage path.");
            }

            EnsureLiveSnapshotCapacityLocked(storageKey, live);
            RemoveLivePayloadReferencesLocked(previous);
            AddLivePayloadReferencesLocked(live);
            _liveSnapshots[storageKey] = live;
        }

        private void RemoveLiveSnapshotLocked(string storageKey)
        {
            if (!_liveSnapshots.TryGetValue(
                    storageKey,
                    out CharacterLiveSnapshot removed))
            {
                return;
            }

            RemoveLivePayloadReferencesLocked(removed);
            _liveSnapshots.Remove(storageKey);
        }

        private void EnsureLiveSnapshotCapacityLocked(
            string storageKey,
            CharacterLiveSnapshot candidate)
        {
            bool replacing = _liveSnapshots.TryGetValue(
                storageKey,
                out CharacterLiveSnapshot current);
            int projectedCount = replacing
                ? _liveSnapshots.Count
                : checked(_liveSnapshots.Count + 1);
            foreach (BackupCapturePreparation prepared in _preparedBackupCaptures.Values)
                if (!_liveSnapshots.ContainsKey(prepared.StorageKey) &&
                    !CharacterStorageLayout.StorageKeyComparer.Equals(prepared.StorageKey, storageKey))
                    projectedCount = checked(projectedCount + 1);
            Dictionary<byte[], int> deltas =
                new Dictionary<byte[], int>(ByteArrayReferenceComparer.Instance);
            if (replacing)
            {
                AddLivePayloadDeltas(current, deltas, -1);
            }

            AddLivePayloadDeltas(candidate, deltas, 1);
            long projectedBytes = _retainedSnapshotPayloadBytes;
            foreach (KeyValuePair<byte[], int> delta in deltas)
            {
                _retainedPayloadReferences.TryGetValue(
                    delta.Key,
                    out int existingCount);
                int projectedReferenceCount =
                    checked(existingCount + delta.Value);
                if (projectedReferenceCount < 0)
                {
                    throw new CharacterStorageException(
                        "The retained character payload reference accounting " +
                        "became invalid.");
                }

                if (existingCount == 0 && projectedReferenceCount > 0)
                {
                    projectedBytes = checked(
                        projectedBytes + delta.Key.LongLength);
                }
                else if (existingCount > 0 && projectedReferenceCount == 0)
                {
                    projectedBytes -= delta.Key.LongLength;
                }
            }

            if (projectedCount > MaximumRetainedLiveSnapshots ||
                projectedBytes > MaximumRetainedLiveSnapshotPayloadBytes)
            {
                throw new CharacterStorageException(
                    "The retained live/checkpoint character snapshot capacity " +
                    "was exhausted. " +
                    "No active or uncheckpointed snapshot was evicted.")
                    .WithPlayerMessage("sm_character_server_busy");
            }
        }

        private void AddLivePayloadReferencesLocked(CharacterLiveSnapshot live)
        {
            AddPayloadReferenceLocked(live.DurableEnvelope.PayloadUnsafe);
            if (!ReferenceEquals(
                    live.DurableEnvelope.PayloadUnsafe,
                    live.LatestEnvelope.PayloadUnsafe))
            {
                AddPayloadReferenceLocked(live.LatestEnvelope.PayloadUnsafe);
            }
        }

        private void RemoveLivePayloadReferencesLocked(
            CharacterLiveSnapshot live)
        {
            RemovePayloadReferenceLocked(live.DurableEnvelope.PayloadUnsafe);
            if (!ReferenceEquals(
                    live.DurableEnvelope.PayloadUnsafe,
                    live.LatestEnvelope.PayloadUnsafe))
            {
                RemovePayloadReferenceLocked(live.LatestEnvelope.PayloadUnsafe);
            }
        }

        private static void AddLivePayloadDeltas(
            CharacterLiveSnapshot live,
            IDictionary<byte[], int> deltas,
            int delta)
        {
            AddPayloadDelta(
                deltas,
                live.DurableEnvelope.PayloadUnsafe,
                delta);
            if (!ReferenceEquals(
                    live.DurableEnvelope.PayloadUnsafe,
                    live.LatestEnvelope.PayloadUnsafe))
            {
                AddPayloadDelta(
                    deltas,
                    live.LatestEnvelope.PayloadUnsafe,
                    delta);
            }
        }

        private static void AddPayloadDelta(
            IDictionary<byte[], int> deltas,
            byte[] payload,
            int delta)
        {
            deltas.TryGetValue(payload, out int current);
            deltas[payload] = checked(current + delta);
        }

        private void AddPayloadReferenceLocked(byte[] payload)
        {
            if (_retainedPayloadReferences.TryGetValue(
                    payload,
                    out int current))
            {
                _retainedPayloadReferences[payload] = checked(current + 1);
                return;
            }

            _retainedPayloadReferences.Add(payload, 1);
            _retainedSnapshotPayloadBytes = checked(
                _retainedSnapshotPayloadBytes + payload.LongLength);
        }

        private void RemovePayloadReferenceLocked(byte[] payload)
        {
            if (!_retainedPayloadReferences.TryGetValue(
                    payload,
                    out int current) ||
                current <= 0)
            {
                throw new CharacterStorageException(
                    "The retained character payload reference accounting " +
                    "became invalid.");
            }

            if (current > 1)
            {
                _retainedPayloadReferences[payload] = current - 1;
                return;
            }

            _retainedPayloadReferences.Remove(payload);
            _retainedSnapshotPayloadBytes -= payload.LongLength;
            if (_retainedSnapshotPayloadBytes < 0)
            {
                throw new CharacterStorageException(
                    "The retained character payload byte accounting became invalid.");
            }
        }

        private void ReleaseCheckpointPayloadsLocked(
            CharacterCheckpointBatch checkpoint)
        {
            if (!_registeredCheckpoints.TryGetValue(
                    checkpoint.CheckpointId,
                    out Dictionary<string, CharacterCheckpointEntry>
                        registeredEntries))
            {
                return;
            }

            foreach (CharacterCheckpointEntry entry in registeredEntries.Values)
            {
                RemoveCheckpointEntryPayloadReferencesLocked(entry);
            }

            _registeredCheckpoints.Remove(checkpoint.CheckpointId);
        }

        private void RequireRegisteredCheckpointEntryLocked(
            CharacterCheckpointBatch checkpoint,
            CharacterCheckpointEntry entry)
        {
            if (!_registeredCheckpoints.TryGetValue(
                    checkpoint.CheckpointId,
                    out Dictionary<string, CharacterCheckpointEntry>
                        registeredEntries) ||
                !registeredEntries.TryGetValue(
                    entry.StorageKey,
                    out CharacterCheckpointEntry registeredEntry) ||
                !ReferenceEquals(registeredEntry, entry))
            {
                throw new CharacterStorageException(
                    "The character checkpoint entry is no longer retained.");
            }
        }

        private void ReleaseCheckpointEntryLocked(
            CharacterCheckpointBatch checkpoint,
            CharacterCheckpointEntry entry)
        {
            if (!_registeredCheckpoints.TryGetValue(
                    checkpoint.CheckpointId,
                    out Dictionary<string, CharacterCheckpointEntry>
                        registeredEntries) ||
                !registeredEntries.TryGetValue(
                    entry.StorageKey,
                    out CharacterCheckpointEntry registeredEntry) ||
                !ReferenceEquals(registeredEntry, entry))
            {
                throw new CharacterStorageException(
                    "The character checkpoint entry is no longer retained.");
            }

            registeredEntries.Remove(entry.StorageKey);
            RemoveCheckpointEntryPayloadReferencesLocked(entry);
            if (registeredEntries.Count == 0)
            {
                _registeredCheckpoints.Remove(checkpoint.CheckpointId);
            }
        }

        private void ValidateCheckpointOwner(CharacterCheckpointBatch checkpoint)
        {
            if (checkpoint == null)
            {
                throw new ArgumentNullException(nameof(checkpoint));
            }

            if (checkpoint.OwnerId != _checkpointOwnerId)
            {
                throw new CharacterStorageException(
                    "The character checkpoint belongs to a different service instance.");
            }
        }

        private void AddCheckpointEntryPayloadReferencesLocked(
            CharacterCheckpointEntry entry)
        {
            AddPayloadReferenceLocked(entry.DurableBase.PayloadUnsafe);
            if (!ReferenceEquals(
                    entry.DurableBase.PayloadUnsafe,
                    entry.Snapshot.PayloadUnsafe))
            {
                try
                {
                    AddPayloadReferenceLocked(entry.Snapshot.PayloadUnsafe);
                }
                catch
                {
                    RemovePayloadReferenceLocked(
                        entry.DurableBase.PayloadUnsafe);
                    throw;
                }
            }
        }

        private void RemoveCheckpointEntryPayloadReferencesLocked(
            CharacterCheckpointEntry entry)
        {
            RemovePayloadReferenceLocked(entry.DurableBase.PayloadUnsafe);
            if (!ReferenceEquals(
                    entry.DurableBase.PayloadUnsafe,
                    entry.Snapshot.PayloadUnsafe))
            {
                RemovePayloadReferenceLocked(entry.Snapshot.PayloadUnsafe);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                throw new ObjectDisposedException(nameof(CharacterSnapshotService));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            // A checkpoint that already acquired this gate owns it until its
            // repository writes and live-base advancement are both complete.
            // Waiting here prevents disposal from clearing retained envelopes
            // underneath that final bookkeeping step. Callers recheck the
            // disposed marker after acquiring the gate to close the inverse
            // race where Dispose arrived first.
            lock (_checkpointCommitGate)
            {
                foreach (KeyValuePair<ZRpc, CharacterSession> pair in _serverSessions)
                {
                    CharacterSession session;
                    if (!_serverSessions.TryRemove(pair.Key, out session))
                    {
                        continue;
                    }

                    CloseSessionCore(session);
                }

                CharacterSession? local = Interlocked.Exchange(
                    ref _localHostSession,
                    null);
                if (local != null)
                {
                    CloseSessionCore(local);
                }

                _activeStorageLeases.Clear();
                _preparedBackupCaptures.Clear();
                _unconfirmedAdminRestores.Clear();
                lock (_liveSnapshotGate)
                {
                    _liveSnapshots.Clear();
                    _registeredCheckpoints.Clear();
                    _retainedPayloadReferences.Clear();
                    _retainedSnapshotPayloadBytes = 0;
                }

                lock (_saveRateLimiterGate)
                {
                    _saveRateLimiters.Clear();
                    _skillObservationWindows.Clear();
                }

                _storageKeyProvider.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        private sealed class BackupCapturePreparation
        {
            internal BackupCapturePreparation(string storageKey, CharacterLiveSnapshot? previous,
                CharacterEnvelope capture, CharacterSkillObservationWindow skillWindow)
            {
                StorageKey = storageKey;
                Previous = previous;
                Capture = capture;
                SkillWindow = skillWindow;
            }

            internal string StorageKey { get; }
            internal CharacterLiveSnapshot? Previous { get; }
            internal CharacterEnvelope Capture { get; }
            internal CharacterSkillObservationWindow SkillWindow { get; }
        }

        private sealed class CharacterLiveSnapshot
        {
            internal CharacterLiveSnapshot(
                CharacterIdentity identity,
                CharacterEnvelope durableEnvelope,
                CharacterEnvelope latestEnvelope,
                CharacterSemanticSnapshot semanticSnapshot,
                long playerId)
            {
                Identity = identity ??
                    throw new ArgumentNullException(nameof(identity));
                DurableEnvelope = durableEnvelope ??
                    throw new ArgumentNullException(nameof(durableEnvelope));
                LatestEnvelope = latestEnvelope ??
                    throw new ArgumentNullException(nameof(latestEnvelope));
                SemanticSnapshot = semanticSnapshot ??
                    throw new ArgumentNullException(nameof(semanticSnapshot));
                if (playerId == 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(playerId));
                }

                if (durableEnvelope.Kind != CharacterEnvelopeKind.Snapshot ||
                    latestEnvelope.Kind != CharacterEnvelopeKind.Snapshot ||
                    durableEnvelope.Revision > latestEnvelope.Revision ||
                    !string.Equals(
                        durableEnvelope.AccountId,
                        identity.AccountId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        durableEnvelope.CharacterName,
                        identity.CharacterName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        latestEnvelope.AccountId,
                        identity.AccountId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        latestEnvelope.CharacterName,
                        identity.CharacterName,
                        StringComparison.Ordinal))
                {
                    throw new CharacterStorageException(
                        "A live character overlay is structurally invalid.");
                }

                PlayerId = playerId;
            }

            internal CharacterIdentity Identity { get; }

            internal CharacterEnvelope DurableEnvelope { get; }

            internal CharacterEnvelope LatestEnvelope { get; }

            internal CharacterSemanticSnapshot SemanticSnapshot { get; }

            internal long PlayerId { get; }

            internal CharacterLiveSnapshot WithLatest(
                CharacterEnvelope latestEnvelope,
                CharacterSemanticSnapshot semanticSnapshot,
                long playerId)
            {
                return new CharacterLiveSnapshot(
                    Identity,
                    DurableEnvelope,
                    latestEnvelope,
                    semanticSnapshot,
                    playerId);
            }

            internal CharacterLiveSnapshot WithDurable(
                CharacterEnvelope durableEnvelope)
            {
                return new CharacterLiveSnapshot(
                    Identity,
                    durableEnvelope,
                    LatestEnvelope,
                    SemanticSnapshot,
                    PlayerId);
            }
        }

        private sealed class ByteArrayReferenceComparer : IEqualityComparer<byte[]>
        {
            internal static readonly ByteArrayReferenceComparer Instance =
                new ByteArrayReferenceComparer();

            public bool Equals(byte[]? left, byte[]? right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(byte[] value)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                    value);
            }
        }

        private CharacterSaveResult CreateRejectedResult(
            CharacterSession session,
            long currentRevision,
            long requestBaseRevision,
            string error,
            byte[]? authoritativePayload,
            IReadOnlyList<string>? semanticObservations = null,
            IReadOnlyList<CharacterStatLimitFinding>? statLimitFindings = null,
            CharacterSemanticValidationResult? auditFindings = null)
        {
            byte[] payload = authoritativePayload ?? Array.Empty<byte>();
            CharacterEnvelope rejected = CharacterEnvelope.Create(
                CharacterEnvelopeKind.SaveRejected,
                currentRevision,
                requestBaseRevision,
                session.SessionId,
                session.Identity,
                DateTime.UtcNow,
                ValheimPlayerProfileCodec.SupportedPlayerProfileVersion,
                payload);

            return new CharacterSaveResult(
                auditFindings ?? CharacterSemanticValidationResult.Empty,
                CharacterCommitStatus.Rejected,
                rejected,
                _envelopeCodec.ToZPackage(rejected),
                error,
                semanticObservations,
                statLimitFindings: statLimitFindings);
        }
    }
}
