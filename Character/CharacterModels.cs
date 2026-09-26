using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ServerManager
{
    public enum CharacterEnvelopeKind
    {
        Snapshot = 1,
        SaveRequest = 2,
        SaveAccepted = 3,
        SaveRejected = 4,
        InventorySaveRequest = 5
    }

    public enum CharacterCommitStatus
    {
        Accepted = 1,
        Conflict = 2,
        Rejected = 3
    }

    public sealed class CharacterStorageOptions
    {
        public const int DefaultMaxPayloadBytes = 10 * 1024 * 1024;
        public const int DefaultMaxEnvelopeOverheadBytes = 16 * 1024;
        public const int MaximumBackupsPerProfile = 1000;

        public int MaxPayloadBytes { get; set; } = DefaultMaxPayloadBytes;

        public int MaxEnvelopeBytes { get; set; } =
            DefaultMaxPayloadBytes + DefaultMaxEnvelopeOverheadBytes;

        public int MaxBackups { get; set; } = 30;

        public int MaxAccountIdUtf8Bytes { get; set; } = 512;

        public int MaxCharacterNameUtf8Bytes { get; set; } = 256;

        public int MaxProfileCollectionEntries { get; set; } = 4096;

        public int MaxCharactersPerAccount { get; set; } = 3;

        public int SaveRequestBurstCapacity { get; set; } = 2;

        public TimeSpan SaveRequestTokenRefillInterval { get; set; } =
            TimeSpan.FromSeconds(5);

        public TimeSpan MaxSaveRequestAge { get; set; } = TimeSpan.FromMinutes(15);

        public TimeSpan MaxSaveRequestFutureSkew { get; set; } = TimeSpan.FromMinutes(2);

        public void Validate()
        {
            if (MaxPayloadBytes < 1024 || MaxPayloadBytes > 64 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxPayloadBytes),
                    "The character payload limit must be between 1 KiB and 64 MiB.");
            }

            if (MaxEnvelopeBytes < MaxPayloadBytes + 1024 ||
                MaxEnvelopeBytes > MaxPayloadBytes + 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxEnvelopeBytes),
                    "The envelope limit must leave bounded room for metadata.");
            }

            if (MaxBackups < 1 || MaxBackups > MaximumBackupsPerProfile)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxBackups));
            }

            if (MaxAccountIdUtf8Bytes < 32 || MaxAccountIdUtf8Bytes > 4096)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxAccountIdUtf8Bytes));
            }

            if (MaxCharacterNameUtf8Bytes < 16 || MaxCharacterNameUtf8Bytes > 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxCharacterNameUtf8Bytes));
            }

            if (MaxProfileCollectionEntries < 1 ||
                MaxProfileCollectionEntries > 65536)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxProfileCollectionEntries));
            }

            if (MaxCharactersPerAccount < 1 || MaxCharactersPerAccount > 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxCharactersPerAccount));
            }

            if (SaveRequestBurstCapacity < 1 || SaveRequestBurstCapacity > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(SaveRequestBurstCapacity));
            }

            if (SaveRequestTokenRefillInterval <= TimeSpan.Zero ||
                SaveRequestTokenRefillInterval > TimeSpan.FromMinutes(10))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(SaveRequestTokenRefillInterval));
            }

            if (MaxSaveRequestAge <= TimeSpan.Zero ||
                MaxSaveRequestAge > TimeSpan.FromHours(24))
            {
                throw new ArgumentOutOfRangeException(nameof(MaxSaveRequestAge));
            }

            if (MaxSaveRequestFutureSkew < TimeSpan.Zero ||
                MaxSaveRequestFutureSkew > TimeSpan.FromHours(1))
            {
                throw new ArgumentOutOfRangeException(nameof(MaxSaveRequestFutureSkew));
            }
        }
    }

    public sealed class CharacterIdentity
    {
        public CharacterIdentity(string accountId, string characterName)
        {
            if (string.IsNullOrEmpty(accountId))
            {
                throw new ArgumentException("An account identity is required.", nameof(accountId));
            }

            if (string.IsNullOrEmpty(characterName))
            {
                throw new ArgumentException("A character name is required.", nameof(characterName));
            }

            AccountId = accountId;
            CharacterName = characterName;
        }

        public string AccountId { get; }

        public string CharacterName { get; }

        public bool EqualsIdentity(CharacterIdentity other)
        {
            return other != null &&
                   string.Equals(AccountId, other.AccountId, StringComparison.Ordinal) &&
                   string.Equals(CharacterName, other.CharacterName, StringComparison.Ordinal);
        }

        public override string ToString()
        {
            return AccountId + "/" + CharacterName;
        }
    }

    public sealed class CharacterEnvelope
    {
        private readonly byte[] _payload;
        private readonly byte[] _payloadSha256;

        internal CharacterEnvelope(
            int protocolVersion,
            CharacterEnvelopeKind kind,
            long revision,
            long baseRevision,
            Guid sessionId,
            string accountId,
            string characterName,
            DateTime createdUtc,
            int valheimProfileVersion,
            byte[] payload,
            byte[] payloadSha256,
            bool cloneArrays,
            bool requiresFreshLocalCharacter = false)
        {
            ProtocolVersion = protocolVersion;
            Kind = kind;
            Revision = revision;
            BaseRevision = baseRevision;
            SessionId = sessionId;
            AccountId = accountId;
            CharacterName = characterName;
            CreatedUtc = DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc);
            ValheimProfileVersion = valheimProfileVersion;
            RequiresFreshLocalCharacter = requiresFreshLocalCharacter;
            _payload = cloneArrays ? CharacterCrypto.Clone(payload) : payload;
            _payloadSha256 = cloneArrays ? CharacterCrypto.Clone(payloadSha256) : payloadSha256;
        }

        public int ProtocolVersion { get; }

        public CharacterEnvelopeKind Kind { get; }

        public long Revision { get; }

        public long BaseRevision { get; }

        public Guid SessionId { get; }

        public string AccountId { get; }

        public string CharacterName { get; }

        public DateTime CreatedUtc { get; }

        public int ValheimProfileVersion { get; }

        // Authoritative origin metadata, independent of whether START ITEMS has
        // already materialized the initial inventory. Cleared by a full save.
        public bool RequiresFreshLocalCharacter { get; }

        public int PayloadLength
        {
            get { return _payload.Length; }
        }

        public byte[] GetPayloadCopy()
        {
            return CharacterCrypto.Clone(_payload);
        }

        public byte[] GetPayloadSha256Copy()
        {
            return CharacterCrypto.Clone(_payloadSha256);
        }

        internal byte[] PayloadUnsafe
        {
            get { return _payload; }
        }

        internal byte[] PayloadSha256Unsafe
        {
            get { return _payloadSha256; }
        }

        internal bool MatchesSnapshot(CharacterEnvelope other)
        {
            return other != null &&
                   Kind == other.Kind &&
                   Revision == other.Revision &&
                   BaseRevision == other.BaseRevision &&
                   SessionId == other.SessionId &&
                   CreatedUtc == other.CreatedUtc &&
                   ValheimProfileVersion == other.ValheimProfileVersion &&
                   RequiresFreshLocalCharacter == other.RequiresFreshLocalCharacter &&
                   string.Equals(
                       AccountId,
                       other.AccountId,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       CharacterName,
                       other.CharacterName,
                       StringComparison.Ordinal) &&
                   CharacterCrypto.FixedTimeEquals(
                       PayloadSha256Unsafe,
                       other.PayloadSha256Unsafe);
        }

        public static CharacterEnvelope Create(
            CharacterEnvelopeKind kind,
            long revision,
            long baseRevision,
            Guid sessionId,
            CharacterIdentity identity,
            DateTime createdUtc,
            int valheimProfileVersion,
            byte[] payload)
        {
            return CreateWithOrigin(kind, revision, baseRevision, sessionId, identity,
                createdUtc, valheimProfileVersion, payload, false);
        }

        internal static CharacterEnvelope CreateWithOrigin(
            CharacterEnvelopeKind kind,
            long revision,
            long baseRevision,
            Guid sessionId,
            CharacterIdentity identity,
            DateTime createdUtc,
            int valheimProfileVersion,
            byte[] payload,
            bool requiresFreshLocalCharacter)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            byte[] payloadCopy = CharacterCrypto.Clone(payload);
            byte[] hash = CharacterCrypto.Sha256(payloadCopy);
            return new CharacterEnvelope(
                CharacterEnvelopeCodec.CurrentProtocolVersion,
                kind,
                revision,
                baseRevision,
                sessionId,
                identity.AccountId,
                identity.CharacterName,
                createdUtc,
                valheimProfileVersion,
                payloadCopy,
                hash,
                false,
                requiresFreshLocalCharacter);
        }
    }

    internal sealed class CharacterSaveRateLimiter
    {
        private readonly object _sync = new object();
        private readonly double _tokenCapacity;
        private readonly double _stopwatchTicksPerToken;
        private long _lastRefillTimestamp;
        private long _lastTouchedTimestamp;
        private double _tokens;

        internal CharacterSaveRateLimiter(
            int burstCapacity,
            TimeSpan tokenRefillInterval)
        {
            if (burstCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(burstCapacity));
            }

            if (tokenRefillInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(tokenRefillInterval));
            }

            _tokenCapacity = burstCapacity;
            _tokens = burstCapacity;
            _stopwatchTicksPerToken =
                tokenRefillInterval.TotalSeconds * Stopwatch.Frequency;
            _lastRefillTimestamp = Stopwatch.GetTimestamp();
            _lastTouchedTimestamp = _lastRefillTimestamp;
        }

        internal bool TryConsume()
        {
            lock (_sync)
            {
                long now = Stopwatch.GetTimestamp();
                long elapsed =
                    now >= _lastRefillTimestamp
                        ? now - _lastRefillTimestamp
                        : 0;

                _lastRefillTimestamp = now;
                _lastTouchedTimestamp = now;
                if (elapsed > 0)
                {
                    _tokens = Math.Min(
                        _tokenCapacity,
                        _tokens + elapsed / _stopwatchTicksPerToken);
                }

                if (_tokens < 1d)
                {
                    return false;
                }

                _tokens -= 1d;
                return true;
            }
        }

        internal void Touch()
        {
            lock (_sync)
            {
                _lastTouchedTimestamp = Stopwatch.GetTimestamp();
            }
        }

        internal bool IsStale(TimeSpan retention)
        {
            lock (_sync)
            {
                long now = Stopwatch.GetTimestamp();
                if (now < _lastTouchedTimestamp)
                {
                    _lastTouchedTimestamp = now;
                    return false;
                }

                double elapsedSeconds =
                    (now - _lastTouchedTimestamp) /
                    (double)Stopwatch.Frequency;
                return elapsedSeconds >= retention.TotalSeconds;
            }
        }
    }

    internal sealed class CharacterSkillObservationWindow
    {
        private readonly object _sync = new object();
        private CharacterSemanticSnapshot _baseline;
        private long _latestRevision;
        private byte[] _latestPayloadSha256;
        private double _accumulatedOnlineSeconds;
        private long _sessionStartTimestamp;
        private long _lastTouchedTimestamp;
        private bool _active;

        internal CharacterSkillObservationWindow(
            long currentRevision,
            byte[] currentPayloadSha256,
            CharacterSemanticSnapshot currentSnapshot)
        {
            if (currentRevision < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(currentRevision));
            }

            if (currentPayloadSha256 == null)
            {
                throw new ArgumentNullException(
                    nameof(currentPayloadSha256));
            }

            _baseline = (currentSnapshot ??
                throw new ArgumentNullException(nameof(currentSnapshot)))
                .CreateSkillObservationBaseline();
            _latestRevision = currentRevision;
            _latestPayloadSha256 =
                CharacterCrypto.Clone(currentPayloadSha256);
            _lastTouchedTimestamp = Stopwatch.GetTimestamp();
        }

        internal void BeginSession(
            long currentRevision,
            byte[] currentPayloadSha256,
            CharacterSemanticSnapshot currentSnapshot)
        {
            if (currentPayloadSha256 == null)
            {
                throw new ArgumentNullException(
                    nameof(currentPayloadSha256));
            }

            if (currentSnapshot == null)
            {
                throw new ArgumentNullException(nameof(currentSnapshot));
            }

            lock (_sync)
            {
                long now = Stopwatch.GetTimestamp();
                if (_active)
                {
                    throw new InvalidOperationException(
                        "The skill observation window already has an active session.");
                }

                if (!MatchesCurrent(
                        currentRevision,
                        currentPayloadSha256))
                {
                    ResetBaseline(
                        currentRevision,
                        currentPayloadSha256,
                        currentSnapshot,
                        now);
                }

                _active = true;
                _sessionStartTimestamp = now;
                _lastTouchedTimestamp = now;
            }
        }

        internal void GetBaseline(
            long expectedRevision,
            byte[] expectedPayloadSha256,
            out CharacterSemanticSnapshot baseline,
            out TimeSpan onlineElapsed)
        {
            lock (_sync)
            {
                long now = Stopwatch.GetTimestamp();
                _lastTouchedTimestamp = now;
                if (!_active ||
                    !MatchesCurrent(
                        expectedRevision,
                        expectedPayloadSha256))
                {
                    baseline = CharacterSemanticSnapshot.Empty;
                    onlineElapsed = TimeSpan.Zero;
                    return;
                }

                double currentSessionSeconds =
                    now <= _sessionStartTimestamp
                        ? 0d
                        : (now - _sessionStartTimestamp) /
                          (double)Stopwatch.Frequency;
                baseline = _baseline;
                onlineElapsed = TimeSpan.FromSeconds(
                    _accumulatedOnlineSeconds +
                    currentSessionSeconds);
            }
        }

        internal void CommitAccepted(
            long expectedRevision,
            long newRevision,
            byte[] currentPayloadSha256,
            byte[] newPayloadSha256,
            CharacterSemanticSnapshot candidateSnapshot)
        {
            lock (_sync)
            {
                long now = Stopwatch.GetTimestamp();
                bool stateMatches =
                    _active &&
                    MatchesCurrent(
                        expectedRevision,
                        currentPayloadSha256);

                if (!stateMatches ||
                    (!_baseline.HasPlayerData &&
                     candidateSnapshot.HasPlayerData))
                {
                    ResetBaseline(
                        newRevision,
                        newPayloadSha256,
                        candidateSnapshot,
                        now);
                    _active = true;
                    _sessionStartTimestamp = now;
                    return;
                }

                _latestRevision = newRevision;
                _latestPayloadSha256 =
                    CharacterCrypto.Clone(newPayloadSha256);
                _lastTouchedTimestamp = now;
            }
        }

        internal void EndSession()
        {
            lock (_sync)
            {
                long now = Stopwatch.GetTimestamp();
                if (_active && now > _sessionStartTimestamp)
                {
                    _accumulatedOnlineSeconds +=
                        (now - _sessionStartTimestamp) /
                        (double)Stopwatch.Frequency;
                }

                _active = false;
                _lastTouchedTimestamp = now;
            }
        }

        internal bool IsStale(TimeSpan retention)
        {
            lock (_sync)
            {
                if (_active)
                {
                    return false;
                }

                long now = Stopwatch.GetTimestamp();
                if (now < _lastTouchedTimestamp)
                {
                    _lastTouchedTimestamp = now;
                    return false;
                }

                double elapsedSeconds =
                    (now - _lastTouchedTimestamp) /
                    (double)Stopwatch.Frequency;
                return elapsedSeconds >= retention.TotalSeconds;
            }
        }

        private bool MatchesCurrent(
            long revision,
            byte[] payloadSha256)
        {
            return revision == _latestRevision &&
                   payloadSha256 != null &&
                   CharacterCrypto.FixedTimeEquals(
                       _latestPayloadSha256,
                       payloadSha256);
        }

        private void ResetBaseline(
            long revision,
            byte[] payloadSha256,
            CharacterSemanticSnapshot snapshot,
            long now)
        {
            _baseline =
                snapshot.CreateSkillObservationBaseline();
            _latestRevision = revision;
            _latestPayloadSha256 =
                CharacterCrypto.Clone(payloadSha256);
            _accumulatedOnlineSeconds = 0d;
            _sessionStartTimestamp = now;
            _lastTouchedTimestamp = now;
        }
    }

    internal sealed class CharacterSessionSaveHealthSnapshot
    {
        internal CharacterSessionSaveHealthSnapshot(
            CharacterIdentity identity,
            Guid sessionId,
            long currentRevision,
            long capturedTimestamp,
            long sessionOpenedTimestamp,
            long? lastRequestTimestamp,
            long? lastSaveStartedTimestamp,
            long? lastSuccessfulAcceptanceTimestamp,
            long successfulAcceptanceCount,
            int consecutiveFailureCount,
            string lastError,
            long? activeSaveStartedTimestamp,
            bool longUnsavedWarningClaimed)
        {
            Identity = identity;
            SessionId = sessionId;
            CurrentRevision = currentRevision;
            CapturedTimestamp = capturedTimestamp;
            SessionOpenedTimestamp = sessionOpenedTimestamp;
            LastRequestTimestamp = lastRequestTimestamp;
            LastSaveStartedTimestamp = lastSaveStartedTimestamp;
            LastSuccessfulAcceptanceTimestamp =
                lastSuccessfulAcceptanceTimestamp;
            SuccessfulAcceptanceCount = successfulAcceptanceCount;
            ConsecutiveFailureCount = consecutiveFailureCount;
            LastError = lastError;
            ActiveSaveStartedTimestamp = activeSaveStartedTimestamp;
            LongUnsavedWarningClaimed = longUnsavedWarningClaimed;
        }

        internal CharacterIdentity Identity { get; }

        internal Guid SessionId { get; }

        internal long CurrentRevision { get; }

        internal long CapturedTimestamp { get; }

        internal long SessionOpenedTimestamp { get; }

        internal long? LastRequestTimestamp { get; }

        internal long? LastSaveStartedTimestamp { get; }

        internal long? LastSuccessfulAcceptanceTimestamp { get; }

        internal long SuccessfulAcceptanceCount { get; }

        internal int ConsecutiveFailureCount { get; }

        internal string LastError { get; }

        internal long? ActiveSaveStartedTimestamp { get; }

        internal bool LongUnsavedWarningClaimed { get; }

        internal bool SaveInProgress
        {
            get { return ActiveSaveStartedTimestamp.HasValue; }
        }

        internal TimeSpan SessionAge
        {
            get { return GetElapsed(SessionOpenedTimestamp, CapturedTimestamp); }
        }

        internal TimeSpan ShadowAcceptanceAge
        {
            get
            {
                return GetElapsed(
                    LastSuccessfulAcceptanceTimestamp ?? SessionOpenedTimestamp,
                    CapturedTimestamp);
            }
        }

        internal TimeSpan? TimeSinceLastRequest
        {
            get
            {
                return LastRequestTimestamp.HasValue
                    ? GetElapsed(LastRequestTimestamp.Value, CapturedTimestamp)
                    : (TimeSpan?)null;
            }
        }

        internal TimeSpan? ActiveSaveAge
        {
            get
            {
                return ActiveSaveStartedTimestamp.HasValue
                    ? GetElapsed(
                        ActiveSaveStartedTimestamp.Value,
                        CapturedTimestamp)
                    : (TimeSpan?)null;
            }
        }

        internal static bool HasElapsed(
            long startedTimestamp,
            long capturedTimestamp,
            TimeSpan threshold)
        {
            if (threshold <= TimeSpan.Zero)
            {
                return true;
            }

            long elapsedTicks = capturedTimestamp - startedTimestamp;
            if (elapsedTicks < 0)
            {
                return false;
            }

            double requiredTicks = threshold.TotalSeconds * Stopwatch.Frequency;
            return elapsedTicks >= requiredTicks;
        }

        private static TimeSpan GetElapsed(
            long startedTimestamp,
            long capturedTimestamp)
        {
            long elapsedTicks = capturedTimestamp - startedTimestamp;
            if (elapsedTicks <= 0)
            {
                return TimeSpan.Zero;
            }

            double elapsedSeconds =
                (double)elapsedTicks / Stopwatch.Frequency;
            return TimeSpan.FromSeconds(elapsedSeconds);
        }
    }

    public sealed class CharacterSession
    {
        private const int MaximumSaveHealthErrorCharacters = 2048;
        private readonly object _revisionLock = new object();
        private readonly object _saveLock = new object();
        private readonly object _saveHealthLock = new object();
        private readonly CharacterSaveRateLimiter _saveRateLimiter;
        private readonly CharacterSaveRateLimiter _inventorySaveRateLimiter =
            new CharacterSaveRateLimiter(4, TimeSpan.FromSeconds(1));
        private readonly CharacterSkillObservationWindow
            _skillObservationWindow;
        private readonly long _sessionOpenedTimestamp = Stopwatch.GetTimestamp();
        private long _currentRevision;
        private byte[] _currentPayloadSha256;
        private CharacterSemanticSnapshot _currentSemanticSnapshot;
        private long? _lastRequestTimestamp;
        private long? _lastSaveStartedTimestamp;
        private long? _lastSuccessfulAcceptanceTimestamp;
        private long? _activeSaveStartedTimestamp;
        private long _successfulAcceptanceCount;
        private int _consecutiveFailureCount;
        private string _lastSaveError = string.Empty;
        private long _longUnsavedWarningRevision;
        private bool _longUnsavedWarningClaimed;
        private bool _saveHealthClosed;
        private bool _closed;
        private CharacterEnvelope? _pendingInitialEnvelope;

        internal CharacterSession(
            ZRpc rpc,
            CharacterIdentity identity,
            string storageKey,
            Guid sessionId,
            long currentRevision,
            byte[] currentPayloadSha256,
            CharacterSemanticSnapshot currentSemanticSnapshot,
            long playerId,
            CharacterSaveRateLimiter saveRateLimiter,
            CharacterSkillObservationWindow skillObservationWindow,
            CharacterEnvelope? pendingInitialEnvelope)
            : this(rpc, identity, storageKey, sessionId, currentRevision,
                currentPayloadSha256, currentSemanticSnapshot, playerId,
                saveRateLimiter, skillObservationWindow, pendingInitialEnvelope,
                backupOnly: false, backupCaptureCreatesProfile: false)
        {
        }

        internal CharacterSession(
            ZRpc rpc,
            CharacterIdentity identity,
            string storageKey,
            Guid sessionId,
            long currentRevision,
            byte[] currentPayloadSha256,
            CharacterSemanticSnapshot currentSemanticSnapshot,
            long playerId,
            CharacterSaveRateLimiter saveRateLimiter,
            CharacterSkillObservationWindow skillObservationWindow,
            CharacterEnvelope? pendingInitialEnvelope,
            bool backupOnly = false,
            bool backupCaptureCreatesProfile = false)
            : this(
                rpc ?? throw new ArgumentNullException(nameof(rpc)),
                false,
                identity,
                storageKey,
                sessionId,
                currentRevision,
                currentPayloadSha256,
                currentSemanticSnapshot,
                playerId,
                saveRateLimiter,
                skillObservationWindow,
                pendingInitialEnvelope,
                backupOnly,
                backupCaptureCreatesProfile)
        {
        }

        internal static CharacterSession CreateLocalHost(
            CharacterIdentity identity,
            string storageKey,
            Guid sessionId,
            long currentRevision,
            byte[] currentPayloadSha256,
            CharacterSemanticSnapshot currentSemanticSnapshot,
            long playerId,
            CharacterSaveRateLimiter saveRateLimiter,
            CharacterSkillObservationWindow skillObservationWindow,
            CharacterEnvelope? pendingInitialEnvelope)
        {
            return CreateLocalHost(identity, storageKey, sessionId, currentRevision,
                currentPayloadSha256, currentSemanticSnapshot, playerId,
                saveRateLimiter, skillObservationWindow, pendingInitialEnvelope,
                backupOnly: false, backupCaptureCreatesProfile: false);
        }

        internal static CharacterSession CreateLocalHost(
            CharacterIdentity identity,
            string storageKey,
            Guid sessionId,
            long currentRevision,
            byte[] currentPayloadSha256,
            CharacterSemanticSnapshot currentSemanticSnapshot,
            long playerId,
            CharacterSaveRateLimiter saveRateLimiter,
            CharacterSkillObservationWindow skillObservationWindow,
            CharacterEnvelope? pendingInitialEnvelope,
            bool backupOnly = false,
            bool backupCaptureCreatesProfile = false)
        {
            return new CharacterSession(
                null,
                true,
                identity,
                storageKey,
                sessionId,
                currentRevision,
                currentPayloadSha256,
                currentSemanticSnapshot,
                playerId,
                saveRateLimiter,
                skillObservationWindow,
                pendingInitialEnvelope,
                backupOnly,
                backupCaptureCreatesProfile);
        }

        private CharacterSession(
            ZRpc? rpc,
            bool isLocalHost,
            CharacterIdentity identity,
            string storageKey,
            Guid sessionId,
            long currentRevision,
            byte[] currentPayloadSha256,
            CharacterSemanticSnapshot currentSemanticSnapshot,
            long playerId,
            CharacterSaveRateLimiter saveRateLimiter,
            CharacterSkillObservationWindow skillObservationWindow,
            CharacterEnvelope? pendingInitialEnvelope,
            bool backupOnly,
            bool backupCaptureCreatesProfile)
        {
            if ((rpc == null) != isLocalHost)
            {
                throw new ArgumentException(
                    "Only an explicitly local host session may have no RPC.",
                    nameof(rpc));
            }

            Rpc = rpc;
            IsLocalHost = isLocalHost;
            BackupOnly = backupOnly;
            BackupCaptureCreatesProfile = backupOnly && backupCaptureCreatesProfile;
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            StorageKey = storageKey ?? throw new ArgumentNullException(nameof(storageKey));
            if (sessionId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A non-empty character session ID is required.",
                    nameof(sessionId));
            }

            if (playerId == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(playerId),
                    "The authoritative PlayerProfile ID must be nonzero.");
            }

            SessionId = sessionId;
            PlayerId = playerId;
            _currentRevision = currentRevision;
            _currentPayloadSha256 =
                CharacterCrypto.Clone(
                    currentPayloadSha256 ??
                    throw new ArgumentNullException(
                        nameof(currentPayloadSha256)));
            _currentSemanticSnapshot =
                currentSemanticSnapshot ??
                throw new ArgumentNullException(
                    nameof(currentSemanticSnapshot));
            _saveRateLimiter =
                saveRateLimiter ?? throw new ArgumentNullException(nameof(saveRateLimiter));
            _skillObservationWindow =
                skillObservationWindow ??
                throw new ArgumentNullException(
                    nameof(skillObservationWindow));
            if (pendingInitialEnvelope != null)
            {
                if (pendingInitialEnvelope.Kind !=
                        CharacterEnvelopeKind.Snapshot ||
                    pendingInitialEnvelope.Revision != currentRevision ||
                    (!backupOnly && pendingInitialEnvelope.BaseRevision != 0) ||
                    pendingInitialEnvelope.SessionId != sessionId ||
                    !string.Equals(
                        pendingInitialEnvelope.AccountId,
                        identity.AccountId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        pendingInitialEnvelope.CharacterName,
                        identity.CharacterName,
                        StringComparison.Ordinal) ||
                    !CharacterCrypto.FixedTimeEquals(
                        pendingInitialEnvelope.PayloadSha256Unsafe,
                        _currentPayloadSha256))
                {
                    throw new ArgumentException(
                        "The pending initial snapshot does not match its character session.",
                        nameof(pendingInitialEnvelope));
                }

                _pendingInitialEnvelope = pendingInitialEnvelope;
            }
            _longUnsavedWarningRevision = currentRevision;
        }

        public ZRpc? Rpc { get; }

        internal bool IsLocalHost { get; }

        // Chosen during authenticated connection setup; settings reloads never
        // change the source of an already-open character session.
        internal bool BackupOnly { get; }

        internal bool BackupCaptureCreatesProfile { get; }

        internal bool PendingBackupCapture => BackupOnly && PendingInitialCommit;

        public CharacterIdentity Identity { get; }

        public string StorageKey { get; }

        public Guid SessionId { get; }

        public long PlayerId { get; }

        internal object SaveLock
        {
            get { return _saveLock; }
        }

        internal bool PendingInitialCommit
        {
            get
            {
                lock (_saveLock)
                {
                    return _pendingInitialEnvelope != null;
                }
            }
        }

        internal CharacterEnvelope? GetPendingInitialEnvelope()
        {
            lock (_saveLock)
            {
                return _pendingInitialEnvelope;
            }
        }

        internal void CompleteInitialCommit(CharacterEnvelope committed)
        {
            if (committed == null)
            {
                throw new ArgumentNullException(nameof(committed));
            }

            lock (_saveLock)
            {
                CharacterEnvelope pending =
                    _pendingInitialEnvelope ??
                    throw new InvalidOperationException(
                        "No initial character snapshot is pending persistence.");
                if (committed.Revision != pending.Revision ||
                    committed.BaseRevision != pending.BaseRevision ||
                    committed.SessionId != pending.SessionId ||
                    !string.Equals(
                        committed.AccountId,
                        pending.AccountId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        committed.CharacterName,
                        pending.CharacterName,
                        StringComparison.Ordinal) ||
                    !CharacterCrypto.FixedTimeEquals(
                        committed.PayloadSha256Unsafe,
                        pending.PayloadSha256Unsafe))
                {
                    throw new InvalidOperationException(
                        "The committed initial snapshot did not match the prepared snapshot.");
                }

                _pendingInitialEnvelope = null;
            }
        }

        public long CurrentRevision
        {
            get
            {
                lock (_revisionLock)
                {
                    return _currentRevision;
                }
            }
        }

        internal bool TryCommitRevision(
            long expectedCurrentRevision,
            long newRevision,
            byte[] newPayloadSha256,
            CharacterSemanticSnapshot candidateSnapshot,
            Action? durableAction)
        {
            if (newPayloadSha256 == null)
            {
                throw new ArgumentNullException(nameof(newPayloadSha256));
            }

            if (candidateSnapshot == null)
            {
                throw new ArgumentNullException(nameof(candidateSnapshot));
            }

            // Allocate the only new revision-state buffer before a durable
            // side effect. With SaveLock held by the caller, the revision lock
            // then forms a small commit barrier: a stale revision never runs
            // durableAction, and a successful action is followed only by
            // non-throwing field assignments.
            byte[] committedPayloadSha256 =
                CharacterCrypto.Clone(newPayloadSha256);
            byte[] currentPayloadSha256;
            lock (_revisionLock)
            {
                if (_currentRevision != expectedCurrentRevision)
                {
                    return false;
                }

                durableAction?.Invoke();
                currentPayloadSha256 = _currentPayloadSha256;
                _currentRevision = newRevision;
                _currentPayloadSha256 = committedPayloadSha256;
                _currentSemanticSnapshot = candidateSnapshot;
            }

            _skillObservationWindow.CommitAccepted(
                expectedCurrentRevision,
                newRevision,
                currentPayloadSha256,
                newPayloadSha256,
                candidateSnapshot);
            return true;
        }

        internal bool TryGetCurrentSemanticSnapshot(
            long expectedRevision,
            byte[] expectedPayloadSha256,
            out CharacterSemanticSnapshot snapshot)
        {
            lock (_revisionLock)
            {
                if (_currentSemanticSnapshot != null &&
                    _currentRevision == expectedRevision &&
                    expectedPayloadSha256 != null &&
                    CharacterCrypto.FixedTimeEquals(
                        _currentPayloadSha256,
                        expectedPayloadSha256))
                {
                    snapshot = _currentSemanticSnapshot;
                    return true;
                }
            }

            snapshot = CharacterSemanticSnapshot.Empty;
            return false;
        }

        internal void CaptureCurrentSemanticState(
            out long revision,
            out CharacterSemanticSnapshot snapshot)
        {
            lock (_revisionLock)
            {
                revision = _currentRevision;
                snapshot = _currentSemanticSnapshot;
            }
        }

        internal void GetSkillObservationBaseline(
            long expectedRevision,
            byte[] expectedPayloadSha256,
            out CharacterSemanticSnapshot baseline,
            out TimeSpan onlineElapsed)
        {
            _skillObservationWindow.GetBaseline(
                expectedRevision,
                expectedPayloadSha256,
                out baseline,
                out onlineElapsed);
        }

        internal bool TryConsumeSaveToken(CharacterEnvelopeKind kind)
        {
            lock (_saveLock)
            {
                if (_closed)
                {
                    return false;
                }

                if (kind == CharacterEnvelopeKind.SaveRequest)
                {
                    return _saveRateLimiter.TryConsume();
                }

                if (kind == CharacterEnvelopeKind.InventorySaveRequest)
                {
                    return _inventorySaveRateLimiter.TryConsume();
                }

                throw new CharacterProtocolException(
                    "Only character save requests consume save-rate tokens.");
            }
        }

        internal void TouchSaveRateLimit()
        {
            _saveRateLimiter.Touch();
        }

        internal void RecordSaveRequest()
        {
            long now = Stopwatch.GetTimestamp();
            lock (_saveHealthLock)
            {
                _lastRequestTimestamp = now;
            }
        }

        internal void BeginSaveAttempt()
        {
            long now = Stopwatch.GetTimestamp();
            lock (_saveHealthLock)
            {
                _lastSaveStartedTimestamp = now;
                _activeSaveStartedTimestamp = now;
            }
        }

        internal void RecordSuccessfulAcceptance(long acceptedRevision)
        {
            long now = Stopwatch.GetTimestamp();
            lock (_saveHealthLock)
            {
                _lastSuccessfulAcceptanceTimestamp = now;
                if (_successfulAcceptanceCount < long.MaxValue)
                {
                    ++_successfulAcceptanceCount;
                }

                _longUnsavedWarningRevision = acceptedRevision;
                _longUnsavedWarningClaimed = false;
            }
        }

        internal void CompleteSaveSuccess()
        {
            lock (_saveHealthLock)
            {
                _activeSaveStartedTimestamp = null;
                _consecutiveFailureCount = 0;
                _lastSaveError = string.Empty;
            }
        }

        internal void CompleteSaveFailure(string error)
        {
            string safeError = NormalizeSaveHealthError(error);
            lock (_saveHealthLock)
            {
                _activeSaveStartedTimestamp = null;
                if (_consecutiveFailureCount < int.MaxValue)
                {
                    ++_consecutiveFailureCount;
                }

                _lastSaveError = safeError;
            }
        }

        internal bool TryCreateOpenSaveHealthSnapshot(
            long capturedTimestamp,
            out CharacterSessionSaveHealthSnapshot? snapshot)
        {
            lock (_revisionLock)
            {
                lock (_saveHealthLock)
                {
                    if (_saveHealthClosed)
                    {
                        snapshot = null;
                        return false;
                    }

                    snapshot = CreateSaveHealthSnapshot(
                        _currentRevision,
                        capturedTimestamp);
                    return true;
                }
            }
        }

        internal bool TryClaimLongUnsavedWarning(
            TimeSpan threshold,
            long capturedTimestamp,
            out CharacterSessionSaveHealthSnapshot? snapshot)
        {
            if (threshold <= TimeSpan.Zero)
            {
                snapshot = null;
                return false;
            }

            lock (_revisionLock)
            {
                lock (_saveHealthLock)
                {
                    long baselineTimestamp =
                        _lastSuccessfulAcceptanceTimestamp ??
                        _sessionOpenedTimestamp;
                    if (_saveHealthClosed ||
                        _longUnsavedWarningRevision != _currentRevision ||
                        _longUnsavedWarningClaimed ||
                        !CharacterSessionSaveHealthSnapshot.HasElapsed(
                            baselineTimestamp,
                            capturedTimestamp,
                            threshold))
                    {
                        snapshot = null;
                        return false;
                    }

                    _longUnsavedWarningClaimed = true;
                    snapshot = CreateSaveHealthSnapshot(
                        _currentRevision,
                        capturedTimestamp);
                    return true;
                }
            }
        }

        internal bool IsClosed
        {
            get
            {
                lock (_saveLock)
                {
                    return _closed;
                }
            }
        }

        internal void MarkClosed()
        {
            lock (_saveLock)
            {
                _closed = true;
            }

            lock (_saveHealthLock)
            {
                _saveHealthClosed = true;
                _activeSaveStartedTimestamp = null;
            }

            _skillObservationWindow.EndSession();
        }

        private CharacterSessionSaveHealthSnapshot CreateSaveHealthSnapshot(
            long currentRevision,
            long capturedTimestamp)
        {
            return new CharacterSessionSaveHealthSnapshot(
                Identity,
                SessionId,
                currentRevision,
                capturedTimestamp,
                _sessionOpenedTimestamp,
                _lastRequestTimestamp,
                _lastSaveStartedTimestamp,
                _lastSuccessfulAcceptanceTimestamp,
                _successfulAcceptanceCount,
                _consecutiveFailureCount,
                _lastSaveError,
                _activeSaveStartedTimestamp,
                _longUnsavedWarningClaimed);
        }

        private static string NormalizeSaveHealthError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return "The character save request did not complete.";
            }

            return error.Length <= MaximumSaveHealthErrorCharacters
                ? error
                : error.Substring(0, MaximumSaveHealthErrorCharacters);
        }

    }

    public sealed class CharacterStoredSnapshot
    {
        public CharacterStoredSnapshot(
            CharacterEnvelope envelope,
            bool wasCreated)
        {
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            WasCreated = wasCreated;
        }

        public CharacterEnvelope Envelope { get; }

        public bool WasCreated { get; }
    }

    internal sealed class CharacterInitialSnapshotPreparation
    {
        internal CharacterInitialSnapshotPreparation(
            CharacterEnvelope envelope,
            bool pendingInitialCommit)
        {
            Envelope = envelope ??
                throw new ArgumentNullException(nameof(envelope));
            PendingInitialCommit = pendingInitialCommit;
        }

        internal CharacterEnvelope Envelope { get; }

        internal bool PendingInitialCommit { get; }
    }

    public sealed class CharacterSessionOpenResult
    {
        internal CharacterSemanticValidationResult AuditFindings { get; }

        public CharacterSessionOpenResult(
            CharacterEnvelope snapshot,
            ZPackage networkPackage,
            bool pendingInitialCommit,
            IReadOnlyList<string>? semanticObservations = null,
            IReadOnlyList<CharacterStatLimitFinding>? statLimitFindings = null)
            : this(CharacterSemanticValidationResult.Empty, snapshot, networkPackage,
                pendingInitialCommit, semanticObservations, statLimitFindings)
        {
        }

        internal CharacterSessionOpenResult(CharacterSemanticValidationResult auditFindings,
            CharacterEnvelope snapshot, ZPackage networkPackage, bool pendingInitialCommit,
            IReadOnlyList<string>? semanticObservations,
            IReadOnlyList<CharacterStatLimitFinding>? statLimitFindings)
        {
            AuditFindings = auditFindings ?? throw new ArgumentNullException(nameof(auditFindings));
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            NetworkPackage = networkPackage ?? throw new ArgumentNullException(nameof(networkPackage));
            PendingInitialCommit = pendingInitialCommit;
            SemanticObservations =
                semanticObservations ?? Array.Empty<string>();
            StatLimitFindings =
                statLimitFindings ?? Array.Empty<CharacterStatLimitFinding>();
        }

        public CharacterEnvelope Snapshot { get; }

        public ZPackage NetworkPackage { get; }

        public bool PendingInitialCommit { get; }

        public IReadOnlyList<string> SemanticObservations { get; }

        public IReadOnlyList<CharacterStatLimitFinding> StatLimitFindings { get; }
    }

    public sealed class CharacterSaveResult
    {
        internal CharacterSemanticValidationResult AuditFindings { get; }

        public CharacterSaveResult(
            CharacterCommitStatus status,
            CharacterEnvelope response,
            ZPackage responsePackage,
            string error,
            IReadOnlyList<string>? semanticObservations = null,
            CharacterSemanticSnapshot? previousSemanticSnapshot = null,
            CharacterSemanticSnapshot? currentSemanticSnapshot = null,
            int acceptedPayloadBytes = 0,
            string? acceptedPayloadSha256 = null,
            IReadOnlyList<CharacterStatLimitFinding>? statLimitFindings = null)
            : this(CharacterSemanticValidationResult.Empty, status, response, responsePackage,
                error, semanticObservations, previousSemanticSnapshot, currentSemanticSnapshot,
                acceptedPayloadBytes, acceptedPayloadSha256, statLimitFindings)
        {
        }

        internal CharacterSaveResult(CharacterSemanticValidationResult auditFindings,
            CharacterCommitStatus status, CharacterEnvelope response, ZPackage responsePackage,
            string error, IReadOnlyList<string>? semanticObservations = null,
            CharacterSemanticSnapshot? previousSemanticSnapshot = null,
            CharacterSemanticSnapshot? currentSemanticSnapshot = null,
            int acceptedPayloadBytes = 0, string? acceptedPayloadSha256 = null,
            IReadOnlyList<CharacterStatLimitFinding>? statLimitFindings = null)
        {
            AuditFindings = auditFindings ?? throw new ArgumentNullException(nameof(auditFindings));
            if (acceptedPayloadBytes < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(acceptedPayloadBytes));
            }

            Status = status;
            Response = response ?? throw new ArgumentNullException(nameof(response));
            ResponsePackage = responsePackage ??
                throw new ArgumentNullException(nameof(responsePackage));
            Error = error ?? string.Empty;
            SemanticObservations =
                semanticObservations ?? Array.Empty<string>();
            StatLimitFindings =
                statLimitFindings ?? Array.Empty<CharacterStatLimitFinding>();
            PreviousSemanticSnapshot = previousSemanticSnapshot;
            CurrentSemanticSnapshot = currentSemanticSnapshot;
            AcceptedPayloadBytes = acceptedPayloadBytes;
            AcceptedPayloadSha256 = acceptedPayloadSha256 ?? string.Empty;
        }

        public CharacterCommitStatus Status { get; }

        public CharacterEnvelope Response { get; }

        public ZPackage ResponsePackage { get; }

        public string Error { get; }

        public IReadOnlyList<string> SemanticObservations { get; }

        public IReadOnlyList<CharacterStatLimitFinding> StatLimitFindings { get; }

        internal CharacterSemanticSnapshot? PreviousSemanticSnapshot { get; }

        internal CharacterSemanticSnapshot? CurrentSemanticSnapshot { get; }

        internal int AcceptedPayloadBytes { get; }

        internal string AcceptedPayloadSha256 { get; }

        public bool Accepted
        {
            get { return Status == CharacterCommitStatus.Accepted; }
        }
    }

    /// <summary>
    /// One immutable server-process character state captured at a world-save
    /// boundary. DurableBase is the primary revision that was known to be on
    /// disk when the live overlay was captured; Snapshot is the latest
    /// server-accepted full profile for that same identity.
    /// </summary>
    internal sealed class CharacterCheckpointEntry
    {
        internal CharacterCheckpointEntry(
            string storageKey,
            CharacterIdentity identity,
            CharacterEnvelope durableBase,
            CharacterEnvelope snapshot)
        {
            StorageKey = storageKey ??
                throw new ArgumentNullException(nameof(storageKey));
            Identity = identity ??
                throw new ArgumentNullException(nameof(identity));
            DurableBase = durableBase ??
                throw new ArgumentNullException(nameof(durableBase));
            Snapshot = snapshot ??
                throw new ArgumentNullException(nameof(snapshot));
        }

        internal string StorageKey { get; }

        internal CharacterIdentity Identity { get; }

        internal CharacterEnvelope DurableBase { get; }

        internal CharacterEnvelope Snapshot { get; }

    }

    /// <summary>
    /// Identifies a retained cutoff without retaining any of its payloads.
    /// Per-character retries keep this handle and their own entry only.
    /// </summary>
    internal class CharacterCheckpointHandle
    {
        internal CharacterCheckpointHandle(
            Guid ownerId,
            Guid checkpointId)
        {
            if (ownerId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A checkpoint owner ID is required.",
                    nameof(ownerId));
            }

            if (checkpointId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A checkpoint ID is required.",
                    nameof(checkpointId));
            }

            OwnerId = ownerId;
            CheckpointId = checkpointId;
        }

        internal Guid OwnerId { get; }

        internal Guid CheckpointId { get; }
    }

    /// <summary>
    /// A point-in-time immutable copy of every retained live character overlay.
    /// Later client saves cannot mutate this batch. Long-lived per-character
    /// retries must retain Handle rather than this aggregate.
    /// </summary>
    internal sealed class CharacterCheckpointBatch : CharacterCheckpointHandle
    {
        internal CharacterCheckpointBatch(
            Guid ownerId,
            Guid checkpointId,
            DateTime capturedUtc,
            IReadOnlyList<CharacterCheckpointEntry> entries)
            : base(ownerId, checkpointId)
        {
            if (capturedUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "The checkpoint capture timestamp must be UTC.",
                    nameof(capturedUtc));
            }

            CapturedUtc = capturedUtc;
            Handle = new CharacterCheckpointHandle(ownerId, checkpointId);
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            CharacterCheckpointEntry[] immutableEntries =
                new CharacterCheckpointEntry[entries.Count];
            for (int index = 0; index < entries.Count; ++index)
            {
                immutableEntries[index] = entries[index] ??
                    throw new ArgumentException(
                        "A checkpoint entry may not be null.",
                        nameof(entries));
            }

            Entries = Array.AsReadOnly(immutableEntries);
        }

        internal CharacterCheckpointHandle Handle { get; }

        internal DateTime CapturedUtc { get; }

        internal IReadOnlyList<CharacterCheckpointEntry> Entries { get; }

        internal int Count => Entries.Count;
    }

    public sealed class CharacterClientState
    {
        private readonly object _sync = new object();
        private long _revision;

        public CharacterClientState(CharacterIdentity identity, Guid sessionId, long revision)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            SessionId = sessionId;
            _revision = revision;
        }

        public CharacterIdentity Identity { get; }

        public Guid SessionId { get; }

        public long Revision
        {
            get
            {
                lock (_sync)
                {
                    return _revision;
                }
            }
        }

        internal bool TryAcceptRevision(long expectedBaseRevision, long newRevision)
        {
            lock (_sync)
            {
                if (_revision != expectedBaseRevision ||
                    expectedBaseRevision == long.MaxValue ||
                    newRevision != expectedBaseRevision + 1)
                {
                    return false;
                }

                _revision = newRevision;
                return true;
            }
        }
    }

    public sealed class CharacterProtocolException : Exception
    {
        // Local capture may briefly retry this specific structural error.
        // It remains a protocol rejection on the server; do not match log text.
        internal bool IsInventoryOverlap { get; private set; }
        internal int FirstOverlapPrefabHash { get; private set; }
        internal int SecondOverlapPrefabHash { get; private set; }

        internal CharacterProtocolException WithInventoryOverlap(int firstPrefabHash, int secondPrefabHash)
        {
            IsInventoryOverlap = true;
            FirstOverlapPrefabHash = firstPrefabHash;
            SecondOverlapPrefabHash = secondPrefabHash;
            return this;
        }

        // Optional player-facing metadata; Message remains the original
        // diagnostic for logs and callers that do not render localized UI.
        internal string PlayerMessageKey { get; private set; } = string.Empty;
        internal string[] PlayerMessageArguments { get; private set; } = Array.Empty<string>();

        internal CharacterProtocolException WithPlayerMessage(string key, params string[] args)
        {
            PlayerMessageKey = key ?? string.Empty;
            PlayerMessageArguments = args == null ? Array.Empty<string>() : (string[])args.Clone();
            return this;
        }

        public CharacterProtocolException(string message)
            : base(message)
        {
        }

        public CharacterProtocolException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed class CharacterStorageException : Exception
    {
        internal string PlayerMessageKey { get; private set; } = string.Empty;
        internal string[] PlayerMessageArguments { get; private set; } = Array.Empty<string>();

        internal CharacterStorageException WithPlayerMessage(string key, params string[] args)
        {
            PlayerMessageKey = key ?? string.Empty;
            PlayerMessageArguments = args == null ? Array.Empty<string>() : (string[])args.Clone();
            return this;
        }

        public CharacterStorageException(string message)
            : base(message)
        {
        }

        public CharacterStorageException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
