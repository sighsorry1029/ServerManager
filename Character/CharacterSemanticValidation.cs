using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;

namespace ServerManager
{
    public enum CharacterSemanticPolicyMode
    {
        Disabled = 0,
        Observe = 1,
        Enforce = 2
    }

    public sealed class CharacterSemanticSkillState
    {
        public CharacterSemanticSkillState(
            int skillType,
            float level,
            float accumulator)
        {
            SkillType = skillType;
            Level = level;
            Accumulator = accumulator;
        }

        public int SkillType { get; }

        public float Level { get; }

        public float Accumulator { get; }
    }

    public sealed class CharacterSemanticItemState
    {
        private readonly ReadOnlyCollection<KeyValuePair<string, string>>
            _customData;

        public CharacterSemanticItemState(
            string prefabName,
            int stack,
            int quality,
            int worldLevel,
            IEnumerable<string> customDataKeys)
            : this(
                prefabName,
                stack,
                quality,
                worldLevel,
                positionX: -1,
                positionY: -1,
                customData: CreateKeyOnlyCustomData(customDataKeys))
        {
        }

        internal CharacterSemanticItemState(
            string prefabName,
            int stack,
            int quality,
            int worldLevel,
            int positionX,
            int positionY,
            IEnumerable<KeyValuePair<string, string>> customData)
        {
            PrefabName = prefabName ??
                throw new ArgumentNullException(nameof(prefabName));
            Stack = stack;
            Quality = quality;
            WorldLevel = worldLevel;
            PositionX = positionX;
            PositionY = positionY;
            _customData = CopyCustomData(
                customData,
                nameof(customData));
        }

        public string PrefabName { get; }

        public int Stack { get; }

        public int Quality { get; }

        public int WorldLevel { get; }

        public int PositionX { get; }

        public int PositionY { get; }

        /// <summary>
        /// Original item custom-data entries in ordinal key order.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, string>> CustomData
        {
            get { return _customData; }
        }

        private static IEnumerable<KeyValuePair<string, string>>
            CreateKeyOnlyCustomData(IEnumerable<string> keys)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            List<KeyValuePair<string, string>> entries =
                new List<KeyValuePair<string, string>>();
            foreach (string key in keys)
            {
                string nonNullKey = key ??
                    throw new ArgumentException(
                        "A semantic string collection contains null.",
                        nameof(keys));
                entries.Add(
                    new KeyValuePair<string, string>(
                        nonNullKey,
                        string.Empty));
            }

            return entries;
        }

        private static ReadOnlyCollection<KeyValuePair<string, string>>
            CopyCustomData(
            IEnumerable<KeyValuePair<string, string>> values,
            string parameterName)
        {
            if (values == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            HashSet<string> keys =
                new HashSet<string>(StringComparer.Ordinal);
            List<KeyValuePair<string, string>> copy =
                new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> entry in values)
            {
                if (entry.Key == null || entry.Value == null)
                {
                    throw new ArgumentException(
                        "A semantic custom-data entry contains null.",
                        parameterName);
                }

                if (!keys.Add(entry.Key))
                {
                    throw new ArgumentException(
                        "A semantic custom-data collection contains a duplicate key.",
                        parameterName);
                }

                copy.Add(entry);
            }

            copy.Sort(
                (left, right) => StringComparer.Ordinal.Compare(
                    left.Key,
                    right.Key));

            return copy.AsReadOnly();
        }
    }

    public sealed class CharacterSemanticSnapshot
    {
        private readonly ReadOnlyDictionary<int, CharacterSemanticSkillState>
            _skills;
        private readonly ReadOnlyCollection<CharacterSemanticItemState> _items;
        private readonly ReadOnlyCollection<string> _playerCustomDataKeys;
        private readonly ReadOnlyDictionary<string, long> _itemTotalsByPrefab;

        public CharacterSemanticSnapshot(
            bool hasPlayerData,
            float maximumHealth,
            float maximumStamina,
            float maximumEitr,
            IEnumerable<CharacterSemanticSkillState> skills,
            IEnumerable<CharacterSemanticItemState> items,
            IEnumerable<string> playerCustomDataKeys)
        {
            HasPlayerData = hasPlayerData;
            MaximumHealth = maximumHealth;
            MaximumStamina = maximumStamina;
            MaximumEitr = maximumEitr;

            if (skills == null)
            {
                throw new ArgumentNullException(nameof(skills));
            }

            Dictionary<int, CharacterSemanticSkillState> skillCopy =
                new Dictionary<int, CharacterSemanticSkillState>();
            foreach (CharacterSemanticSkillState skill in skills)
            {
                if (skill == null)
                {
                    throw new ArgumentException(
                        "The semantic skill collection contains null.",
                        nameof(skills));
                }

                if (skillCopy.ContainsKey(skill.SkillType))
                {
                    throw new ArgumentException(
                        "The semantic skill collection contains a duplicate type.",
                        nameof(skills));
                }

                skillCopy.Add(skill.SkillType, skill);
            }

            _skills =
                new ReadOnlyDictionary<int, CharacterSemanticSkillState>(
                    skillCopy);

            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            List<CharacterSemanticItemState> itemCopy =
                new List<CharacterSemanticItemState>();
            Dictionary<string, long> totals =
                new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (CharacterSemanticItemState item in items)
            {
                if (item == null)
                {
                    throw new ArgumentException(
                        "The semantic item collection contains null.",
                        nameof(items));
                }

                itemCopy.Add(item);
                long current;
                totals.TryGetValue(item.PrefabName, out current);
                totals[item.PrefabName] = checked(current + item.Stack);
            }

            _items = itemCopy.AsReadOnly();
            _itemTotalsByPrefab =
                new ReadOnlyDictionary<string, long>(totals);

            if (playerCustomDataKeys == null)
            {
                throw new ArgumentNullException(nameof(playerCustomDataKeys));
            }

            List<string> keyCopy = new List<string>();
            foreach (string key in playerCustomDataKeys)
            {
                keyCopy.Add(key ??
                    throw new ArgumentException(
                        "The player custom-data key collection contains null.",
                        nameof(playerCustomDataKeys)));
            }

            _playerCustomDataKeys = keyCopy.AsReadOnly();
        }

        private CharacterSemanticSnapshot(CharacterSemanticSnapshot source, bool usedCheats)
        {
            HasPlayerData = source.HasPlayerData;
            MaximumHealth = source.MaximumHealth;
            MaximumStamina = source.MaximumStamina;
            MaximumEitr = source.MaximumEitr;
            _skills = source._skills;
            _items = source._items;
            _playerCustomDataKeys = source._playerCustomDataKeys;
            _itemTotalsByPrefab = source._itemTotalsByPrefab;
            UsedCheats = usedCheats;
        }

        // Outer-profile metadata is a policy finding, not corrupt serialization.
        // Share immutable collections without mutating the common Empty instance.
        internal CharacterSemanticSnapshot WithUsedCheats(bool usedCheats) =>
            UsedCheats == usedCheats ? this : new CharacterSemanticSnapshot(this, usedCheats);

        public bool UsedCheats { get; }

        public bool HasPlayerData { get; }

        public float MaximumHealth { get; }

        public float MaximumStamina { get; }

        public float MaximumEitr { get; }

        public IReadOnlyDictionary<int, CharacterSemanticSkillState> Skills
        {
            get { return _skills; }
        }

        public IReadOnlyList<CharacterSemanticItemState> Items
        {
            get { return _items; }
        }

        public IReadOnlyList<string> PlayerCustomDataKeys
        {
            get { return _playerCustomDataKeys; }
        }

        public IReadOnlyDictionary<string, long> ItemTotalsByPrefab
        {
            get { return _itemTotalsByPrefab; }
        }

        internal CharacterSemanticSnapshot CreateSkillObservationBaseline()
        {
            if (!HasPlayerData)
            {
                return Empty;
            }

            return new CharacterSemanticSnapshot(
                true,
                0f,
                0f,
                0f,
                _skills.Values,
                Array.Empty<CharacterSemanticItemState>(),
                Array.Empty<string>());
        }

        internal static CharacterSemanticSnapshot Empty { get; } =
            new CharacterSemanticSnapshot(
                false,
                0f,
                0f,
                0f,
                Array.Empty<CharacterSemanticSkillState>(),
                Array.Empty<CharacterSemanticItemState>(),
                Array.Empty<string>());
    }

    public sealed class CharacterSemanticPolicy
    {
        private const int MaximumPolicyEntries = 512;
        private const int MaximumPolicyTokenLength = 256;
        private static readonly char[] PolicySeparators =
            { ',', ';', '\n' };

        private readonly HashSet<string> _forbiddenItemPrefabs;

        internal static CharacterSemanticPolicy FromSettings(
            ServerSettings settings, CharacterSemanticPolicyMode mode)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new CharacterSemanticPolicy(
                mode, settings.ForbiddenItemPrefabs,
                settings.MaximumHealth, settings.MaximumStamina, settings.MaximumEitr,
                skillLevelBurstAllowance: 2f, skillLevelsPerMinute: 10f);
        }

        public CharacterSemanticPolicy(
            CharacterSemanticPolicyMode mode,
            string forbiddenItemPrefabs,
            float maximumHealth,
            float maximumStamina,
            float maximumEitr,
            float skillLevelBurstAllowance,
            float skillLevelsPerMinute)
        {
            if (!Enum.IsDefined(typeof(CharacterSemanticPolicyMode), mode))
            {
                throw new CharacterProtocolException(
                    "The character semantic policy mode is invalid.");
            }

            EnsurePositiveFinite(maximumHealth, "Maximum Health");
            EnsurePositiveFinite(maximumStamina, "Maximum Stamina");
            EnsurePositiveFinite(maximumEitr, "Maximum Eitr");
            EnsureNonNegativeFinite(
                skillLevelBurstAllowance,
                "Skill Level Burst Allowance");
            EnsureNonNegativeFinite(
                skillLevelsPerMinute,
                "Skill Levels Per Minute");
            Mode = mode;
            MaximumHealth = maximumHealth;
            MaximumStamina = maximumStamina;
            MaximumEitr = maximumEitr;
            SkillLevelBurstAllowance = skillLevelBurstAllowance;
            SkillLevelsPerMinute = skillLevelsPerMinute;

            _forbiddenItemPrefabs = ParseSet(
                forbiddenItemPrefabs,
                "Forbidden Item Prefabs");
        }

        public CharacterSemanticPolicyMode Mode { get; }

        public float MaximumHealth { get; }

        public float MaximumStamina { get; }

        public float MaximumEitr { get; }

        public float SkillLevelBurstAllowance { get; }

        public float SkillLevelsPerMinute { get; }

        public int ForbiddenItemPrefabCount
        {
            get { return _forbiddenItemPrefabs.Count; }
        }

        internal bool IsForbiddenItemPrefab(string prefabName)
        {
            return _forbiddenItemPrefabs.Contains(prefabName);
        }

        private static HashSet<string> ParseSet(
            string value,
            string fieldName)
        {
            HashSet<string> parsed =
                new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(value))
            {
                return parsed;
            }

            string[] tokens = SplitPolicyEntries(value);
            if (tokens.Length > MaximumPolicyEntries)
            {
                throw new CharacterProtocolException(
                    fieldName + " contains too many entries.");
            }

            for (int index = 0; index < tokens.Length; ++index)
            {
                string token = ValidatePolicyToken(tokens[index], fieldName);
                if (!parsed.Add(token))
                {
                    throw new CharacterProtocolException(
                        fieldName + " contains a duplicate entry.");
                }
            }

            return parsed;
        }

        private static string ValidatePolicyToken(
            string value,
            string fieldName)
        {
            string token = (value ?? string.Empty).Trim();
            if (token.Length == 0 ||
                token.Length > MaximumPolicyTokenLength)
            {
                throw new CharacterProtocolException(
                    fieldName + " contains an empty or overlong entry.");
            }

            for (int index = 0; index < token.Length; ++index)
            {
                char current = token[index];
                UnicodeCategory category =
                    char.GetUnicodeCategory(current);
                if (char.IsControl(current) ||
                    char.IsSurrogate(current) ||
                    category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator ||
                    category == UnicodeCategory.ParagraphSeparator)
                {
                    throw new CharacterProtocolException(
                        fieldName + " contains an invalid character.");
                }
            }

            return token;
        }

        private static string[] SplitPolicyEntries(string value)
        {
            string normalizedNewlines =
                value.Replace("\r\n", "\n").Replace('\r', '\n');
            return normalizedNewlines.Split(
                PolicySeparators,
                StringSplitOptions.None);
        }

        private static void EnsurePositiveFinite(float value, string name)
        {
            if (value <= 0f || float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new CharacterProtocolException(
                    name + " must be a positive finite number.");
            }
        }

        private static void EnsureNonNegativeFinite(float value, string name)
        {
            if (value < 0f || float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new CharacterProtocolException(
                    name + " must be a non-negative finite number.");
            }
        }
    }

    public sealed class CharacterStatLimitFinding
    {
        internal CharacterStatLimitFinding(string code, float value, float limit)
        {
            if (code != "maximum_health" &&
                code != "maximum_stamina" &&
                code != "maximum_eitr")
            {
                throw new ArgumentException("The character stat code is invalid.", nameof(code));
            }

            if (float.IsNaN(value) || float.IsInfinity(value) ||
                float.IsNaN(limit) || float.IsInfinity(limit) ||
                limit <= 0f || value <= limit)
            {
                throw new ArgumentException("A stat-limit finding requires a finite value above its positive limit.");
            }

            Code = code;
            Value = value;
            Limit = limit;
        }

        public string Code { get; }

        public float Value { get; }

        public float Limit { get; }
    }

    internal enum CharacterAuditObservationKind
    {
        ValidationObserved,
        AdminBypass,
        RevisionObserved
    }

    // Internal metadata is generated with the finding, never recovered from its
    // human-readable detail. Existing public strings remain the display contract.
    internal sealed class CharacterAuditObservation
    {
        internal CharacterAuditObservation(CharacterAuditObservationKind kind,
            string reasonCode, string dedupeKey, string detail)
        {
            Kind = kind;
            ReasonCode = reasonCode;
            DedupeKey = dedupeKey;
            Detail = detail;
        }

        internal CharacterAuditObservationKind Kind { get; }
        internal string ReasonCode { get; }
        internal string DedupeKey { get; }
        internal string Detail { get; }
    }

    public sealed class CharacterSemanticValidationResult
    {
        private readonly ReadOnlyCollection<string> _violations;
        private readonly ReadOnlyCollection<string> _observations;
        private readonly ReadOnlyCollection<CharacterStatLimitFinding> _statLimitFindings;

        internal CharacterSemanticValidationResult(
            IEnumerable<string> violations,
            IEnumerable<string> observations,
            IEnumerable<CharacterStatLimitFinding>? statLimitFindings = null)
            : this(violations, observations, statLimitFindings,
                Array.Empty<CharacterAuditObservation>(), "character_validation_failed")
        {
        }

        internal CharacterSemanticValidationResult(
            IEnumerable<string> violations,
            IEnumerable<string> observations,
            IEnumerable<CharacterStatLimitFinding>? statLimitFindings,
            IEnumerable<CharacterAuditObservation> auditObservations,
            string rejectionReasonCode)
        {
            _violations = Copy(violations, nameof(violations));
            _observations = Copy(observations, nameof(observations));
            _statLimitFindings = Copy(
                statLimitFindings ?? Array.Empty<CharacterStatLimitFinding>(),
                nameof(statLimitFindings));
            AuditObservations = Copy(auditObservations, nameof(auditObservations));
            RejectionReasonCode = rejectionReasonCode;
        }

        internal IReadOnlyList<CharacterAuditObservation> AuditObservations { get; }
        internal string RejectionReasonCode { get; }

        public IReadOnlyList<string> Violations
        {
            get { return _violations; }
        }

        public IReadOnlyList<string> Observations
        {
            get { return _observations; }
        }

        public IReadOnlyList<CharacterStatLimitFinding> StatLimitFindings
        {
            get { return _statLimitFindings; }
        }

        public bool Rejected
        {
            get { return _violations.Count != 0; }
        }

        public string RejectionReason
        {
            get
            {
                if (_violations.Count == 0)
                {
                    return string.Empty;
                }

                return "The character state policy rejected this revision: " +
                       string.Join("; ", _violations);
            }
        }

        internal static CharacterSemanticValidationResult Empty { get; } =
            new CharacterSemanticValidationResult(
                Array.Empty<string>(),
                Array.Empty<string>());

        private static ReadOnlyCollection<T> Copy<T>(
            IEnumerable<T> values,
            string parameterName) where T : class
        {
            if (values == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            List<T> copy = new List<T>();
            foreach (T value in values)
            {
                copy.Add(value ??
                    throw new ArgumentException(
                        "A semantic finding collection contains null.",
                        parameterName));
            }

            return copy.AsReadOnly();
        }
    }

    public sealed class CharacterSemanticEvaluator
    {
        private const int MaximumFindingsPerDisposition = 16;
        private readonly CharacterSemanticPolicy _policy;

        public CharacterSemanticEvaluator(CharacterSemanticPolicy policy)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        public CharacterSemanticValidationResult Evaluate(
            CharacterIdentity identity,
            CharacterSemanticSnapshot previous,
            CharacterSemanticSnapshot candidate,
            TimeSpan serverElapsed)
        {
            if (previous == null)
            {
                throw new ArgumentNullException(nameof(previous));
            }

            return EvaluateCore(
                identity,
                previous,
                candidate,
                serverElapsed,
                includeTransitionObservations: true,
                bypassAdminPolicy: false);
        }

        internal CharacterSemanticValidationResult EvaluateRevision(
            CharacterIdentity identity,
            CharacterSemanticSnapshot skillObservationBaseline,
            CharacterSemanticSnapshot candidate,
            TimeSpan skillObservationElapsed,
            bool bypassAdminPolicy = false)
        {
            return EvaluateCore(
                identity,
                skillObservationBaseline,
                candidate,
                skillObservationElapsed,
                includeTransitionObservations: true,
                bypassAdminPolicy);
        }

        internal CharacterSemanticValidationResult EvaluateAbsolute(
            CharacterIdentity identity,
            CharacterSemanticSnapshot candidate)
        {
            return EvaluateCore(
                identity,
                candidate,
                candidate,
                TimeSpan.Zero,
                includeTransitionObservations: false,
                bypassAdminPolicy: false);
        }

        internal CharacterSemanticValidationResult EvaluateBackupBaseline(
            CharacterIdentity identity, CharacterSemanticSnapshot candidate,
            bool bypassAdminPolicy)
        {
            return EvaluateCore(identity, candidate, candidate, TimeSpan.Zero,
                includeTransitionObservations: false, bypassAdminPolicy,
                allowUnmaterialized: true);
        }

        private CharacterSemanticValidationResult EvaluateCore(
            CharacterIdentity identity,
            CharacterSemanticSnapshot skillObservationBaseline,
            CharacterSemanticSnapshot candidate,
            TimeSpan skillObservationElapsed,
            bool includeTransitionObservations,
            bool bypassAdminPolicy,
            bool allowUnmaterialized = false)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (skillObservationBaseline == null)
            {
                throw new ArgumentNullException(
                    nameof(skillObservationBaseline));
            }

            if (candidate == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            List<string> violations = new List<string>();
            List<string> observations = new List<string>();
            List<CharacterAuditObservation> auditObservations = new List<CharacterAuditObservation>();
            bool forbiddenViolation = false;
            List<CharacterStatLimitFinding> statLimitFindings =
                new List<CharacterStatLimitFinding>(3);
            HashSet<string> findingKeys =
                new HashSet<string>(StringComparer.Ordinal);

            if (!candidate.HasPlayerData && !allowUnmaterialized)
            {
                AddInvariantFinding(
                    "missing_player_data",
                    "the candidate has no inner Player data",
                    violations,
                    findingKeys);
            }

            if (candidate.UsedCheats)
            {
                const string message = "the PlayerProfile is marked as having used cheats";
                if (bypassAdminPolicy && _policy.Mode == CharacterSemanticPolicyMode.Enforce)
                {
                    AddObservation(observations, auditObservations,
                        CharacterAuditObservationKind.AdminBypass, "used_cheats",
                        "[admin_bypass:used_cheats]", message);
                }
                else
                {
                    AddHardFinding("used_cheats", message, violations, observations, auditObservations, findingKeys);
                }
            }

            AddMaximumFinding(
                "maximum_health",
                candidate.MaximumHealth,
                _policy.MaximumHealth,
                statLimitFindings);
            AddMaximumFinding(
                "maximum_stamina",
                candidate.MaximumStamina,
                _policy.MaximumStamina,
                statLimitFindings);
            AddMaximumFinding(
                "maximum_eitr",
                candidate.MaximumEitr,
                _policy.MaximumEitr,
                statLimitFindings);

            for (int itemIndex = 0;
                 itemIndex < candidate.Items.Count;
                 ++itemIndex)
            {
                CharacterSemanticItemState item = candidate.Items[itemIndex];
                string safePrefab = SafeDiagnosticToken(item.PrefabName);

                if (_policy.IsForbiddenItemPrefab(item.PrefabName))
                {
                    string findingKey = "forbidden_prefab:" + item.PrefabName;
                    string message =
                        "forbidden item prefab '" + safePrefab + "' is present";
                    // Only policy rules use the trusted incoming-session
                    // exemption. Structural/identity validation remains mandatory.
                    if (bypassAdminPolicy &&
                        _policy.Mode == CharacterSemanticPolicyMode.Enforce)
                    {
                        if (findingKeys.Add("hard:" + findingKey))
                        {
                            AddObservation(observations, auditObservations,
                                CharacterAuditObservationKind.AdminBypass, "forbidden_prefab",
                                "[admin_bypass:" + SafeDiagnosticToken(findingKey) + "]", message);
                        }

                        continue;
                    }

                    int previousViolationCount = violations.Count;
                    AddHardFinding(
                        findingKey,
                        message,
                        violations,
                        observations,
                        auditObservations,
                        findingKeys);
                    forbiddenViolation |= violations.Count > previousViolationCount;
                }
            }

            // Skill progress is always observed. This never selects an
            // enforcement action and is independent of prefab/admin policy.
            if (candidate.HasPlayerData)
            {
                ObserveCandidateSkillAccumulators(
                    candidate,
                    observations,
                    auditObservations,
                    findingKeys);
            }

            if (includeTransitionObservations && candidate.HasPlayerData &&
                skillObservationBaseline.HasPlayerData)
            {
                ObserveSkillGains(
                    skillObservationBaseline,
                    candidate,
                    skillObservationElapsed,
                    observations,
                    auditObservations,
                    findingKeys);
            }

            return new CharacterSemanticValidationResult(
                violations,
                observations,
                statLimitFindings,
                auditObservations,
                forbiddenViolation ? "forbidden_prefab" : "character_validation_failed");
        }

        private static void AddMaximumFinding(
            string code,
            float value,
            float maximum,
            List<CharacterStatLimitFinding> statLimitFindings)
        {
            if (value <= maximum)
            {
                return;
            }

            // Numeric limits select an independent runtime response. They do
            // not reject, clamp or rewrite an otherwise valid snapshot.
            statLimitFindings.Add(new CharacterStatLimitFinding(code, value, maximum));
        }

        private void AddHardFinding(
            string findingKey,
            string message,
            List<string> violations,
            List<string> observations,
            List<CharacterAuditObservation> auditObservations,
            HashSet<string> findingKeys)
        {
            if (_policy.Mode == CharacterSemanticPolicyMode.Disabled ||
                !findingKeys.Add("hard:" + findingKey))
            {
                return;
            }

            if (_policy.Mode == CharacterSemanticPolicyMode.Enforce)
            {
                AddBounded(
                    violations,
                    "[" + SafeDiagnosticToken(findingKey) + "] " + message);
                return;
            }

            AddObservation(observations, auditObservations,
                CharacterAuditObservationKind.ValidationObserved, "stored_policy_violation",
                "[would_reject:" + SafeDiagnosticToken(findingKey) + "]", message);
        }

        private static void AddInvariantFinding(
            string findingKey,
            string message,
            List<string> violations,
            HashSet<string> findingKeys)
        {
            if (!findingKeys.Add("invariant:" + findingKey))
            {
                return;
            }

            AddBounded(
                violations,
                "[" + SafeDiagnosticToken(findingKey) + "] " + message);
        }

        private static void ObserveCandidateSkillAccumulators(
            CharacterSemanticSnapshot candidate,
            List<string> observations,
            List<CharacterAuditObservation> auditObservations,
            HashSet<string> findingKeys)
        {
            foreach (KeyValuePair<int, CharacterSemanticSkillState> pair in
                     candidate.Skills)
            {
                // Custom skill IDs and modded levels outside the vanilla range
                // do not share the game's XP formula or its level-100 ceiling.
                if (!UsesVanillaSkillProgress(pair.Value)) continue;
                double requirement =
                    GetNextSkillLevelRequirement(pair.Value.Level);
                bool outsideVanillaEnvelope =
                    pair.Value.Level >= 100f
                        ? pair.Value.Accumulator > 0.001f
                        : pair.Value.Accumulator >
                          requirement + 0.001d;
                if (!outsideVanillaEnvelope)
                {
                    continue;
                }

                string findingKey = "skill_accumulator:" +
                    pair.Key.ToString(CultureInfo.InvariantCulture);
                if (!findingKeys.Add("observe:" + findingKey))
                {
                    continue;
                }

                AddObservation(observations, auditObservations,
                    CharacterAuditObservationKind.RevisionObserved, "skill_accumulator",
                    "[" + findingKey + "]", "skill " +
                    pair.Key.ToString(CultureInfo.InvariantCulture) +
                    " has accumulator " +
                    pair.Value.Accumulator.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " outside the vanilla next-level envelope " +
                    requirement.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture));
            }
        }

        private void ObserveSkillGains(
            CharacterSemanticSnapshot previous,
            CharacterSemanticSnapshot candidate,
            TimeSpan serverElapsed,
            List<string> observations,
            List<CharacterAuditObservation> auditObservations,
            HashSet<string> findingKeys)
        {
            double elapsedMinutes =
                Math.Max(0d, serverElapsed.TotalMinutes);
            double allowedGain =
                _policy.SkillLevelBurstAllowance +
                _policy.SkillLevelsPerMinute * elapsedMinutes;

            foreach (KeyValuePair<int, CharacterSemanticSkillState> pair in
                     candidate.Skills)
            {
                CharacterSemanticSkillState previousSkill;
                double previousProgress =
                    previous.Skills.TryGetValue(pair.Key, out previousSkill)
                        ? GetSkillProgress(previousSkill)
                        : 0d;
                double gain =
                    GetSkillProgress(pair.Value) - previousProgress;
                if (gain <= allowedGain + 0.001d)
                {
                    continue;
                }

                string findingKey = "skill_gain:" +
                    pair.Key.ToString(CultureInfo.InvariantCulture);
                if (!findingKeys.Add("observe:" + findingKey))
                {
                    continue;
                }

                AddObservation(observations, auditObservations,
                    CharacterAuditObservationKind.RevisionObserved, "skill_gain",
                    "[" + findingKey + "]", "skill " +
                    pair.Key.ToString(CultureInfo.InvariantCulture) +
                    " gained " +
                    gain.ToString("0.###", CultureInfo.InvariantCulture) +
                    " levels in " +
                    Math.Max(0d, serverElapsed.TotalSeconds)
                        .ToString("0.###", CultureInfo.InvariantCulture) +
                    " server-observed seconds; allowed burst/rate envelope " +
                    allowedGain.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }

        private static double GetSkillProgress(
            CharacterSemanticSkillState skill)
        {
            if (!UsesVanillaSkillProgress(skill) || skill.Accumulator < 0f)
                return skill.Level;
            if (skill.Level >= 100f)
            {
                return skill.Level;
            }

            double requirement =
                GetNextSkillLevelRequirement(skill.Level);
            return skill.Level + skill.Accumulator / requirement;
        }

        private static bool UsesVanillaSkillProgress(CharacterSemanticSkillState skill)
        {
            return Enum.IsDefined(typeof(Skills.SkillType), skill.SkillType) &&
                   skill.SkillType != (int)Skills.SkillType.None &&
                   skill.SkillType != (int)Skills.SkillType.All &&
                   skill.Level >= 0f && skill.Level <= 100f;
        }

        private static double GetNextSkillLevelRequirement(float level)
        {
            return Math.Pow(Math.Floor(level + 1d), 1.5d) * 0.5d + 0.5d;
        }

        private static void AddObservation(List<string> observations,
            List<CharacterAuditObservation> auditObservations,
            CharacterAuditObservationKind kind, string reasonCode, string dedupeKey, string message)
        {
            if (observations.Count >= MaximumFindingsPerDisposition) return;
            string detail = dedupeKey + " " + message;
            observations.Add(detail);
            auditObservations.Add(new CharacterAuditObservation(kind, reasonCode, dedupeKey, detail));
        }

        private static void AddBounded(List<string> findings, string value)
        {
            if (findings.Count < MaximumFindingsPerDisposition)
            {
                findings.Add(value);
            }
        }

        private static string SafeDiagnosticToken(string value)
        {
            const int maximumLength = 96;
            int length = Math.Min(value.Length, maximumLength);
            char[] safe = new char[length];
            for (int index = 0; index < length; ++index)
            {
                char current = value[index];
                UnicodeCategory category =
                    char.GetUnicodeCategory(current);
                safe[index] =
                    char.IsControl(current) ||
                    char.IsSurrogate(current) ||
                    category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator ||
                    category == UnicodeCategory.ParagraphSeparator
                        ? '?'
                        : current;
            }

            string result = new string(safe);
            return value.Length > maximumLength ? result + "..." : result;
        }
    }

    internal sealed class CharacterValidatedSnapshot
    {
        internal CharacterValidatedSnapshot(
            long playerId,
            CharacterSemanticSnapshot semanticSnapshot)
        {
            if (playerId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(playerId));
            }

            PlayerId = playerId;
            SemanticSnapshot = semanticSnapshot ??
                throw new ArgumentNullException(nameof(semanticSnapshot));
        }

        internal long PlayerId { get; }

        internal CharacterSemanticSnapshot SemanticSnapshot { get; }
    }

    internal interface ICharacterRevisionValidator
    {
        CharacterSemanticValidationResult EvaluateAuthoritative(
            CharacterIdentity identity,
            CharacterSemanticSnapshot authoritativeSnapshot);

        CharacterSemanticValidationResult Evaluate(
            CharacterIdentity identity,
            CharacterEnvelope current,
            CharacterEnvelope candidate,
            long expectedPlayerId,
            CharacterSemanticSnapshot? currentSemanticSnapshot,
            CharacterSemanticSnapshot candidateSnapshot,
            CharacterSemanticSnapshot skillObservationBaseline,
            TimeSpan skillObservationElapsed);
    }

    internal sealed class CharacterSemanticRevisionValidator :
        ICharacterRevisionValidator
    {
        private readonly ValheimPlayerProfileCodec _profileCodec;
        private CharacterSemanticEvaluator _evaluator;
        private readonly Func<CharacterIdentity, bool>? _isCharacterPolicyAdmin;

        internal CharacterSemanticRevisionValidator(
            ValheimPlayerProfileCodec profileCodec,
            CharacterSemanticEvaluator evaluator)
            : this(profileCodec, evaluator, null)
        {
        }

        internal CharacterSemanticRevisionValidator(
            ValheimPlayerProfileCodec profileCodec,
            CharacterSemanticEvaluator evaluator,
            Func<CharacterIdentity, bool>? isCharacterPolicyAdmin)
        {
            _profileCodec = profileCodec ??
                throw new ArgumentNullException(nameof(profileCodec));
            _evaluator = evaluator ??
                throw new ArgumentNullException(nameof(evaluator));
            _isCharacterPolicyAdmin = isCharacterPolicyAdmin;
        }

        internal void ApplyEvaluator(CharacterSemanticEvaluator evaluator)
        {
            Volatile.Write(ref _evaluator, evaluator ??
                throw new ArgumentNullException(nameof(evaluator)));
        }

        public CharacterSemanticValidationResult EvaluateAuthoritative(
            CharacterIdentity identity,
            CharacterSemanticSnapshot authoritativeSnapshot)
        {
            if (authoritativeSnapshot == null)
            {
                throw new ArgumentNullException(
                    nameof(authoritativeSnapshot));
            }

            return Volatile.Read(ref _evaluator).EvaluateAbsolute(
                identity,
                authoritativeSnapshot);
        }

        internal CharacterSemanticValidationResult EvaluateBackupBaseline(
            CharacterIdentity identity, CharacterSemanticSnapshot candidate)
        {
            return Volatile.Read(ref _evaluator).EvaluateBackupBaseline(
                identity, candidate, IsCharacterPolicyAdmin(identity));
        }

        public CharacterSemanticValidationResult Evaluate(
            CharacterIdentity identity,
            CharacterEnvelope current,
            CharacterEnvelope candidate,
            long expectedPlayerId,
            CharacterSemanticSnapshot? currentSemanticSnapshot,
            CharacterSemanticSnapshot candidateSnapshot,
            CharacterSemanticSnapshot skillObservationBaseline,
            TimeSpan skillObservationElapsed)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            if (candidate == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            if (skillObservationBaseline == null)
            {
                throw new ArgumentNullException(
                    nameof(skillObservationBaseline));
            }

            if (currentSemanticSnapshot == null)
            {
                CharacterValidatedSnapshot currentValidated =
                    _profileCodec.ExtractValidatedSnapshot(
                        identity,
                        current.PayloadUnsafe);
                if (currentValidated.PlayerId != expectedPlayerId)
                {
                    return new CharacterSemanticValidationResult(
                        new[]
                        {
                            "[player_id_changed] the authoritative PlayerProfile ID " +
                            "changed while its server session was active"
                        },
                        Array.Empty<string>());
                }
            }

            return Volatile.Read(ref _evaluator).EvaluateRevision(
                identity,
                skillObservationBaseline,
                candidateSnapshot,
                skillObservationElapsed,
                bypassAdminPolicy: IsCharacterPolicyAdmin(identity));
        }

        private bool IsCharacterPolicyAdmin(CharacterIdentity identity)
        {
            try
            {
                // Re-resolve on every incoming evaluation so revocation takes
                // effect immediately; stored-file validation never calls this.
                return _isCharacterPolicyAdmin?.Invoke(identity) == true;
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                return false;
            }
        }
    }
}
