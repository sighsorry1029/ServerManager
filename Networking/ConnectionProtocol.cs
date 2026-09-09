#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ServerManager
{
    /// <summary>
    /// Public phases of the server-side connection protocol. Rejected is terminal; callers must
    /// remove that session when ZNet disconnects the peer.
    /// </summary>
    public enum ConnectionSessionState
    {
        Connected = 0,
        Challenged = 1,
        ManifestValidated = 2,
        CharacterSent = 3,
        Ready = 4,
        Rejected = 255
    }

    public enum ProtocolPacketKind : byte
    {
        Challenge = 1,
        ManifestResponse = 2,
        ManifestAccepted = 3,
        CharacterFragment = 4,
        ReadyAck = 5,
        FinalSaveBegin = 6,
        FinalSaveReady = 7,
        OperationalKickRequest = 8,
        OperationalKickComplete = 9,
        PolicyUpdate = 10,
        PolicyAck = 11,
        ClientCharacterRejection = 12,
        BackupCaptureRequest = 13,
        BackupCaptureCommitted = 14,
        LibraryManifestUpdate = 15,
        Reject = 255
    }

    /// <summary>
    /// Stable, plugin-specific error values. These are intentionally separate from Valheim's
    /// generic Error RPC integers. Send a Reject packet first, then use LegacyDisconnectError
    /// when closing the peer.
    /// </summary>
    public enum ProtocolRejectCode
    {
        None = 0,

        NotServer = 1000,
        PeerNotFound = 1001,
        PeerIdentityUnavailable = 1002,
        SessionNotFound = 1003,
        DuplicateConnection = 1004,
        SessionAlreadyRejected = 1005,
        HandshakeTimedOut = 1006,

        InvalidTransition = 1100,
        DuplicateMessage = 1101,
        OutOfOrderMessage = 1102,
        UnexpectedMessageType = 1103,
        ProtocolVersionMismatch = 1104,
        SessionIdMismatch = 1105,
        NonceMismatch = 1106,

        MalformedPacket = 1200,
        PayloadTooLarge = 1201,
        TrailingPacketData = 1202,

        ManifestRejected = 1300,
        ManifestValidatorFailed = 1301,

        PeerInfoTooEarly = 1400,
        PeerInfoAlreadyProcessed = 1402,
        PeerInfoAuthenticationIncomplete = 1403,
        WorldReleaseNotReady = 1404,

        CharacterTransferAlreadyPrepared = 1500,
        CharacterTransferNotPrepared = 1501,
        CharacterTransferIdMismatch = 1502,
        CharacterTransferFailed = 1503,
        CharacterSaveRateExceeded = 1504,
        ClientCharacterRejected = 1505,

        FragmentMalformed = 1600,
        FragmentCountExceeded = 1601,
        FragmentOutOfRange = 1602,
        DuplicateFragment = 1603,
        FragmentOutOfOrder = 1604,
        FragmentAssemblyLimitExceeded = 1605,
        FragmentAssemblyTimedOut = 1606,
        FragmentMetadataMismatch = 1607,
        FragmentDigestMismatch = 1608,
        DecompressedPayloadTooLarge = 1609,
        DecompressionFailed = 1610,

        DetectionProtocolViolation = 1700,
        CheatDetected = 1701,

        EventProtocolViolation = 1800,

        InternalError = 1999
    }

    public static class ProtocolSequence
    {
        public const uint Challenge = 1;
        public const uint ManifestResponse = 2;
        public const uint ManifestAccepted = 3;
        public const uint CharacterData = 4;
        public const uint ReadyAck = 5;
        public const uint FinalSaveBegin = 6;
        public const uint FinalSaveReady = 7;
        public const uint OperationalKickRequest = 8;
        public const uint OperationalKickComplete = 9;
        public const uint ClientCharacterRejection = 12;
        public const uint BackupCaptureRequest = 13;
        public const uint BackupCaptureCommitted = 14;
    }

    public sealed class ProtocolRejection
    {
        public ProtocolRejection(
            ProtocolRejectCode code,
            string safeMessage,
            int legacyDisconnectError = 3,
            bool disconnect = true)
        {
            if (code == ProtocolRejectCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            Code = code;
            SafeMessage = safeMessage ?? string.Empty;
            LegacyDisconnectError = legacyDisconnectError;
            Disconnect = disconnect;
        }

        public ProtocolRejectCode Code { get; private set; }

        /// <summary>
        /// A bounded, client-safe explanation. Do not put stack traces, filesystem paths, secrets,
        /// expected hashes, or raw attacker-controlled text here.
        /// </summary>
        public string SafeMessage { get; private set; }

        public int LegacyDisconnectError { get; private set; }

        public bool Disconnect { get; private set; }

        // Presentation metadata, independent of the English diagnostic and audit.
        internal string PlayerMessageKey { get; private set; } = "";
        internal string[] PlayerMessageArguments { get; private set; } = Array.Empty<string>();

        internal ProtocolRejection WithPlayerMessage(string key, params string[] arguments)
        {
            ProtocolRejection copy = WithConnectionAudit(AuditCategory, AuditReasonCode,
                AuditDetail, AuditPluginSummary, AuditStage);
            copy.PlayerMessageKey = key ?? "";
            copy.PlayerMessageArguments = (string[])(arguments ?? Array.Empty<string>()).Clone();
            return copy;
        }

        // Server-local context only. CreateReject serializes none of these fields.
        internal string AuditCategory { get; private set; } = "";
        internal string AuditReasonCode { get; private set; } = "";
        internal string AuditDetail { get; private set; } = "";
        internal string AuditPluginSummary { get; private set; } = "";
        internal string AuditStage { get; private set; } = "";

        internal ProtocolRejection WithConnectionAudit(string category, string reasonCode,
            string detail, string pluginSummary = "", string stage = "") =>
            new ProtocolRejection(Code, SafeMessage, LegacyDisconnectError, Disconnect)
            {
                AuditCategory = category ?? "", AuditReasonCode = reasonCode ?? "",
                AuditDetail = detail ?? "", AuditPluginSummary = pluginSummary ?? "",
                AuditStage = stage ?? "",
                PlayerMessageKey = PlayerMessageKey,
                PlayerMessageArguments = (string[])PlayerMessageArguments.Clone()
            };
    }

    public class ProtocolOperationResult
    {
        protected ProtocolOperationResult(bool succeeded, ConnectionSessionSnapshot session, ProtocolRejection rejection)
        {
            Succeeded = succeeded;
            Session = session;
            Rejection = rejection;
        }

        public bool Succeeded { get; private set; }

        public ConnectionSessionSnapshot Session { get; private set; }

        public ProtocolRejection Rejection { get; private set; }

        public static ProtocolOperationResult Success(ConnectionSessionSnapshot session)
        {
            return new ProtocolOperationResult(true, session, null);
        }

        public static ProtocolOperationResult Reject(ProtocolRejection rejection, ConnectionSessionSnapshot session = null)
        {
            if (rejection == null)
            {
                throw new ArgumentNullException(nameof(rejection));
            }

            return new ProtocolOperationResult(false, session, rejection);
        }
    }

    public sealed class ProtocolOperationResult<T> : ProtocolOperationResult
    {
        private ProtocolOperationResult(
            bool succeeded,
            T value,
            ConnectionSessionSnapshot session,
            ProtocolRejection rejection)
            : base(succeeded, session, rejection)
        {
            Value = value;
        }

        public T Value { get; private set; }

        public static ProtocolOperationResult<T> Success(T value, ConnectionSessionSnapshot session)
        {
            return new ProtocolOperationResult<T>(true, value, session, null);
        }

        public new static ProtocolOperationResult<T> Reject(
            ProtocolRejection rejection,
            ConnectionSessionSnapshot session = null)
        {
            if (rejection == null)
            {
                throw new ArgumentNullException(nameof(rejection));
            }

            return new ProtocolOperationResult<T>(false, default(T), session, rejection);
        }
    }

    public sealed class ManifestValidationDecision
    {
        private ManifestValidationDecision(bool accepted, ProtocolRejection rejection)
        {
            Accepted = accepted;
            Rejection = rejection;
        }

        public bool Accepted { get; private set; }

        public ProtocolRejection Rejection { get; private set; }

        public static ManifestValidationDecision Accept()
        {
            return new ManifestValidationDecision(true, null);
        }

        public static ManifestValidationDecision Reject(
            string safeMessage,
            ProtocolRejectCode code = ProtocolRejectCode.ManifestRejected)
        {
            return new ManifestValidationDecision(
                false,
                new ProtocolRejection(code, safeMessage ?? "The client manifest was rejected."));
        }

        internal ManifestValidationDecision WithConnectionAudit(string category, string reasonCode,
            string detail, string pluginSummary = "") =>
            new ManifestValidationDecision(Accepted,
                Rejection?.WithConnectionAudit(category, reasonCode, detail, pluginSummary));

        internal ManifestValidationDecision WithPlayerMessage(string key, params string[] arguments) =>
            new ManifestValidationDecision(Accepted, Rejection?.WithPlayerMessage(key, arguments));
    }

    public interface IManifestValidator
    {
        /// <summary>
        /// Validate the bounded manifest bytes. Account ownership must come from peerIdentity;
        /// never accept an account ID embedded in manifestPayload as authoritative.
        /// </summary>
        ManifestValidationDecision Validate(ServerPeerIdentity peerIdentity, byte[] manifestPayload);
    }

    public interface IProtocolClock
    {
        long GetTimestamp();

        long Frequency { get; }
    }

    public sealed class StopwatchProtocolClock : IProtocolClock
    {
        public long GetTimestamp()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public long Frequency
        {
            get { return System.Diagnostics.Stopwatch.Frequency; }
        }
    }

    public sealed class ConnectionProtocolLimits
    {
        public const int SessionIdBytes = 16;
        public const int NonceBytes = 32;
        public const int MessageIdBytes = 16;
        public const int DigestBytes = 32;
        public const int AbsoluteMaxPacketBytes = 4 * 1024 * 1024;
        public const int AbsoluteMaxManifestBytes = 1024 * 1024;
        public const int AbsoluteMaxPeerInfoBytes = 1024 * 1024;
        public const int AbsoluteMaxFragmentDataBytes = 256 * 1024;

        public ConnectionProtocolLimits(
            int maxPacketBytes = 512 * 1024,
            int maxManifestBytes = 256 * 1024,
            int maxPeerInfoBytes = 256 * 1024,
            int maxRejectMessageBytes = 512,
            TimeSpan? phaseTimeout = null,
            TimeSpan? overallHandshakeTimeout = null)
        {
            MaxPacketBytes = maxPacketBytes;
            MaxManifestBytes = maxManifestBytes;
            MaxPeerInfoBytes = maxPeerInfoBytes;
            MaxRejectMessageBytes = maxRejectMessageBytes;
            PhaseTimeout = phaseTimeout ?? TimeSpan.FromSeconds(12);
            OverallHandshakeTimeout = overallHandshakeTimeout ?? TimeSpan.FromSeconds(40);
            Validate();
        }

        public int MaxPacketBytes { get; private set; }

        public int MaxManifestBytes { get; private set; }

        public int MaxPeerInfoBytes { get; private set; }

        public int MaxRejectMessageBytes { get; private set; }

        public TimeSpan PhaseTimeout { get; private set; }

        public TimeSpan OverallHandshakeTimeout { get; private set; }

        private void Validate()
        {
            if (MaxPacketBytes < ProtocolPacketCodec.FixedHeaderBytes ||
                MaxPacketBytes > AbsoluteMaxPacketBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxPacketBytes));
            }

            if (MaxManifestBytes < 0 ||
                MaxManifestBytes > AbsoluteMaxManifestBytes ||
                MaxManifestBytes > MaxPacketBytes - ProtocolPacketCodec.FixedHeaderBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxManifestBytes));
            }

            if (MaxPeerInfoBytes <= 0 || MaxPeerInfoBytes > AbsoluteMaxPeerInfoBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxPeerInfoBytes));
            }

            if (MaxRejectMessageBytes < 0 || MaxRejectMessageBytes > 4096)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxRejectMessageBytes));
            }

            if (PhaseTimeout <= TimeSpan.Zero || PhaseTimeout > TimeSpan.FromMinutes(2))
            {
                throw new ArgumentOutOfRangeException(nameof(PhaseTimeout));
            }

            if (OverallHandshakeTimeout < PhaseTimeout ||
                OverallHandshakeTimeout > TimeSpan.FromMinutes(5))
            {
                throw new ArgumentOutOfRangeException(nameof(OverallHandshakeTimeout));
            }
        }
    }

    public sealed class ProtocolPacket
    {
        public ProtocolPacket(
            ProtocolPacketKind kind,
            uint sequence,
            byte[] sessionId,
            byte[] nonce,
            byte[] payload)
        {
            if (sessionId == null || sessionId.Length != ConnectionProtocolLimits.SessionIdBytes)
            {
                throw new ArgumentException("Invalid session ID length.", nameof(sessionId));
            }

            if (nonce == null || nonce.Length != ConnectionProtocolLimits.NonceBytes)
            {
                throw new ArgumentException("Invalid nonce length.", nameof(nonce));
            }

            Kind = kind;
            Sequence = sequence;
            SessionId = ProtocolByteUtil.Clone(sessionId);
            Nonce = ProtocolByteUtil.Clone(nonce);
            Payload = ProtocolByteUtil.Clone(payload ?? ProtocolByteUtil.Empty);
        }

        public ProtocolPacketKind Kind { get; private set; }

        public uint Sequence { get; private set; }

        public byte[] SessionId { get; private set; }

        public byte[] Nonce { get; private set; }

        public byte[] Payload { get; private set; }
    }

    public sealed class ProtocolChallengeOptions
    {
        public const ushort EnforceManifestFlag = 1 << 0;
        public const ushort ServerCharactersFlag = 1 << 1;
        public const ushort CheatEngineDetectionFlag = 1 << 2;
        public const ushort ExternalToolDetectionFlag = 1 << 3;
        public const ushort ValheimToolerDetectionFlag = 1 << 4;
        public const ushort CheatCommandMonitoringFlag = 1 << 5;
        public const ushort CheatCommandBlockingFlag = 1 << 6;
        public const ushort GenericProcessNameDetectionFlag = 1 << 7;
        public const ushort AllowAdminCheatCommandsFlag = 1 << 8;
        public const ushort CarryWeightLimitFlag = 1 << 9;
        public const ushort MaximumDamageLimitFlag = 1 << 10;
        public const float MaximumCarryWeightLimit = 1000000f;
        public const float MaximumDamageLimit = 1000000000f;
        internal const ushort KnownFlags =
            EnforceManifestFlag |
            ServerCharactersFlag |
            CheatEngineDetectionFlag |
            ExternalToolDetectionFlag |
            ValheimToolerDetectionFlag |
            CheatCommandMonitoringFlag |
            CheatCommandBlockingFlag |
            GenericProcessNameDetectionFlag |
            AllowAdminCheatCommandsFlag |
            CarryWeightLimitFlag |
            MaximumDamageLimitFlag;
        internal const int MaximumLibraryKeys = 128;
        internal const int MaximumLibraryKeyUtf8Bytes = 256;
        internal const int EncodedBytes =
            sizeof(ushort) + sizeof(int) + sizeof(ushort) +
            sizeof(float) + sizeof(float) + sizeof(int);
        internal const int MaxEncodedBytes = EncodedBytes +
            MaximumLibraryKeys * (sizeof(int) + MaximumLibraryKeyUtf8Bytes);
        private static readonly Encoding LibraryKeyUtf8 = new UTF8Encoding(false, true);

        public ProtocolChallengeOptions(
            bool enforceManifest,
            bool serverCharactersEnabled,
            int maximumManifestBytes,
            bool detectCheatEngine,
            bool detectExternalTools,
            bool detectGenericProcessNames,
            bool detectValheimTooler,
            bool monitorCheatCommands,
            bool blockCheatCommands,
            bool allowAdminCheatCommands,
            int processScanIntervalSeconds,
            bool enforceCarryWeightLimit,
            float maximumCarryWeight,
            bool enforceMaximumDamageLimit,
            float maximumDamage,
            IEnumerable<string> libraryKeys = null)
        {
            if (maximumManifestBytes < 64 * 1024 ||
                maximumManifestBytes >
                ConnectionProtocolLimits.AbsoluteMaxManifestBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumManifestBytes));
            }

            if (processScanIntervalSeconds < 5 ||
                processScanIntervalSeconds > 300)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(processScanIntervalSeconds));
            }

            if (blockCheatCommands && !monitorCheatCommands)
            {
                throw new ArgumentException(
                    "Cheat command blocking requires command monitoring.",
                    nameof(blockCheatCommands));
            }

            ValidateGameplayLimit(
                maximumCarryWeight,
                MaximumCarryWeightLimit,
                nameof(maximumCarryWeight));
            ValidateGameplayLimit(
                maximumDamage,
                MaximumDamageLimit,
                nameof(maximumDamage));
            EnforceManifest = enforceManifest;
            ServerCharactersEnabled = serverCharactersEnabled;
            MaximumManifestBytes = maximumManifestBytes;
            DetectCheatEngine = detectCheatEngine;
            DetectExternalTools = detectExternalTools;
            DetectGenericProcessNames = detectGenericProcessNames;
            DetectValheimTooler = detectValheimTooler;
            MonitorCheatCommands = monitorCheatCommands;
            BlockCheatCommands = blockCheatCommands;
            AllowAdminCheatCommands = allowAdminCheatCommands;
            ProcessScanIntervalSeconds = processScanIntervalSeconds;
            EnforceCarryWeightLimit = enforceCarryWeightLimit;
            MaximumCarryWeight = maximumCarryWeight;
            EnforceMaximumDamageLimit = enforceMaximumDamageLimit;
            MaximumDamage = maximumDamage;
            List<string> keys = new List<string>();
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            if (libraryKeys != null)
            {
                foreach (string key in libraryKeys)
                {
                    if (keys.Count >= MaximumLibraryKeys ||
                        !IntegrityAssemblyIdentity.IsCanonicalKey(key) ||
                        LibraryKeyUtf8.GetByteCount(key) > MaximumLibraryKeyUtf8Bytes ||
                        !unique.Add(key))
                        throw new ArgumentException("Library identities must be bounded, canonical and unique.", nameof(libraryKeys));
                    keys.Add(key);
                }
            }
            keys.Sort(StringComparer.Ordinal);
            LibraryKeys = keys.AsReadOnly();
        }

        public bool EnforceManifest { get; private set; }

        public bool ServerCharactersEnabled { get; private set; }

        public int MaximumManifestBytes { get; private set; }

        public bool DetectCheatEngine { get; private set; }

        public bool DetectExternalTools { get; private set; }

        public bool DetectGenericProcessNames { get; private set; }

        public bool DetectValheimTooler { get; private set; }

        public bool MonitorCheatCommands { get; private set; }

        public bool BlockCheatCommands { get; private set; }

        public bool AllowAdminCheatCommands { get; private set; }

        public int ProcessScanIntervalSeconds { get; private set; }

        public bool EnforceCarryWeightLimit { get; private set; }

        public float MaximumCarryWeight { get; private set; }

        public bool EnforceMaximumDamageLimit { get; private set; }

        public float MaximumDamage { get; private set; }

        public IReadOnlyList<string> LibraryKeys { get; private set; }

        internal ProtocolChallengeOptions WithGameplayLimits(float carryWeight, float damage)
        {
            return new ProtocolChallengeOptions(EnforceManifest, ServerCharactersEnabled,
                MaximumManifestBytes, DetectCheatEngine, DetectExternalTools,
                DetectGenericProcessNames, DetectValheimTooler, MonitorCheatCommands,
                BlockCheatCommands, AllowAdminCheatCommands, ProcessScanIntervalSeconds,
                EnforceCarryWeightLimit, carryWeight, EnforceMaximumDamageLimit, damage, LibraryKeys);
        }

        private static void ValidateGameplayLimit(
            float value,
            float maximum,
            string parameterName)
        {
            if (float.IsNaN(value) ||
                float.IsInfinity(value) ||
                value < 1f ||
                value > maximum)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    /// <summary>
    /// Fixed binary envelope for pre-PeerInfo protocol messages. Pair it with
    /// RawProtocolRpcTransport so ZRpc does not call ReadPackage before this decoder runs. The
    /// envelope itself deliberately avoids ZPackage.ReadString/ReadByteArray: every forged inner
    /// length is checked before allocation.
    /// </summary>
    public static class ProtocolPacketCodec
    {
        private const int Magic = 0x52474D53; // "SMGR" when viewed as little-endian bytes.
        public const ushort WireVersion = 21;

        private const int MaximumPlayerMessageBytes = 1112; // key + four bounded arguments, including lengths
        private static readonly Encoding RejectUtf8 = new UTF8Encoding(false, true);
        public const int FixedHeaderBytes =
            sizeof(int) + sizeof(ushort) + sizeof(byte) + sizeof(uint) +
            ConnectionProtocolLimits.SessionIdBytes +
            ConnectionProtocolLimits.NonceBytes +
            sizeof(int);

        public static ZPackage Encode(ProtocolPacket packet, ConnectionProtocolLimits limits)
        {
            if (packet == null)
            {
                throw new ArgumentNullException(nameof(packet));
            }

            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            ValidatePayloadForKind(packet.Kind, packet.Payload.Length, limits);
            if (packet.Kind == ProtocolPacketKind.LibraryManifestUpdate && packet.Sequence == 0)
                throw new ArgumentOutOfRangeException(nameof(packet), "Library manifest sequence must be positive.");
            int totalLength = checked(FixedHeaderBytes + packet.Payload.Length);
            if (totalLength > limits.MaxPacketBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(packet), "Packet exceeds the configured limit.");
            }

            using (MemoryStream stream = new MemoryStream(totalLength))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(WireVersion);
                writer.Write((byte)packet.Kind);
                writer.Write(packet.Sequence);
                writer.Write(packet.SessionId);
                writer.Write(packet.Nonce);
                writer.Write(packet.Payload.Length);
                writer.Write(packet.Payload);
                writer.Flush();
                return new ZPackage(stream.ToArray());
            }
        }

        public static bool TryDecode(
            ZPackage package,
            ConnectionProtocolLimits limits,
            out ProtocolPacket packet,
            out ProtocolRejection rejection)
        {
            packet = null;
            rejection = null;

            if (package == null)
            {
                rejection = Malformed("The protocol packet was missing.");
                return false;
            }

            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            int packageSize;
            try
            {
                packageSize = package.Size();
            }
            catch
            {
                rejection = Malformed("The protocol packet size could not be read.");
                return false;
            }

            if (packageSize < FixedHeaderBytes)
            {
                rejection = Malformed("The protocol packet was truncated.");
                return false;
            }

            if (packageSize > limits.MaxPacketBytes)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.PayloadTooLarge,
                    "The protocol packet exceeded the allowed size.");
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = package.GetArray();
            }
            catch
            {
                rejection = Malformed("The protocol packet could not be copied.");
                return false;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(bytes, false))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    if (reader.ReadInt32() != Magic)
                    {
                        rejection = Malformed("The protocol packet magic was invalid.");
                        return false;
                    }

                    ushort version = reader.ReadUInt16();
                    if (version != WireVersion)
                    {
                        rejection = new ProtocolRejection(
                            ProtocolRejectCode.ProtocolVersionMismatch,
                            "The connection protocol version was incompatible.")
                            .WithConnectionAudit("protocol", "protocol_version_mismatch",
                                "Expected wire version " + WireVersion + "; reported " + version + ".");
                        return false;
                    }

                    byte rawKind = reader.ReadByte();
                    if (!Enum.IsDefined(typeof(ProtocolPacketKind), rawKind))
                    {
                        rejection = new ProtocolRejection(
                            ProtocolRejectCode.UnexpectedMessageType,
                            "The protocol message type was unknown.");
                        return false;
                    }

                    ProtocolPacketKind kind = (ProtocolPacketKind)rawKind;
                    uint sequence = reader.ReadUInt32();
                    if (kind == ProtocolPacketKind.LibraryManifestUpdate && sequence == 0)
                    {
                        rejection = Malformed("The library manifest sequence was invalid.");
                        return false;
                    }
                    byte[] sessionId = ProtocolByteUtil.ReadExact(
                        reader,
                        ConnectionProtocolLimits.SessionIdBytes);
                    byte[] nonce = ProtocolByteUtil.ReadExact(
                        reader,
                        ConnectionProtocolLimits.NonceBytes);
                    int payloadLength = reader.ReadInt32();

                    long remaining = stream.Length - stream.Position;
                    if (payloadLength < 0 || payloadLength > remaining)
                    {
                        rejection = Malformed("The protocol payload length was invalid.");
                        return false;
                    }

                    try
                    {
                        ValidatePayloadForKind(kind, payloadLength, limits);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        rejection = new ProtocolRejection(
                            ProtocolRejectCode.PayloadTooLarge,
                            "The protocol payload exceeded the allowed size.");
                        return false;
                    }
                    catch (InvalidDataException)
                    {
                        rejection = Malformed("The protocol payload shape was invalid.");
                        return false;
                    }

                    byte[] payload = ProtocolByteUtil.ReadExact(
                        reader,
                        payloadLength);
                    if (stream.Position != stream.Length)
                    {
                        rejection = new ProtocolRejection(
                            ProtocolRejectCode.TrailingPacketData,
                            "The protocol packet contained trailing data.");
                        return false;
                    }

                    packet = new ProtocolPacket(kind, sequence, sessionId, nonce, payload);
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                rejection = Malformed("The protocol packet was truncated.");
                return false;
            }
            catch (IOException)
            {
                rejection = Malformed("The protocol packet could not be read.");
                return false;
            }
            catch (OverflowException)
            {
                rejection = Malformed("The protocol packet length overflowed.");
                return false;
            }
        }

        public static ZPackage CreateManifestResponse(
            byte[] sessionId,
            byte[] nonce,
            byte[] manifestPayload,
            ConnectionProtocolLimits limits)
        {
            return Encode(
                new ProtocolPacket(
                    ProtocolPacketKind.ManifestResponse,
                    ProtocolSequence.ManifestResponse,
                    sessionId,
                    nonce,
                    manifestPayload),
                limits);
        }

        public static ZPackage CreateReadyAck(
            byte[] sessionId,
            byte[] nonce,
            byte[] characterMessageId,
            ConnectionProtocolLimits limits)
        {
            if (characterMessageId == null ||
                characterMessageId.Length != ConnectionProtocolLimits.MessageIdBytes)
            {
                throw new ArgumentException("Invalid character message ID.", nameof(characterMessageId));
            }

            return Encode(
                new ProtocolPacket(
                    ProtocolPacketKind.ReadyAck,
                    ProtocolSequence.ReadyAck,
                    sessionId,
                    nonce,
                    characterMessageId),
                limits);
        }

        public static ZPackage CreateManifestAccepted(
            byte[] sessionId,
            byte[] nonce,
            ConnectionProtocolLimits limits)
        {
            return Encode(
                new ProtocolPacket(
                    ProtocolPacketKind.ManifestAccepted,
                    ProtocolSequence.ManifestAccepted,
                    sessionId,
                    nonce,
                    ProtocolByteUtil.Empty),
                limits);
        }

        // The only supported client refusal is a fixed fresh-character guard.
        // No name, account ID, diagnostic text, path, or profile bytes are sent.
        internal static ZPackage CreateFreshCharacterRejection(byte[] sessionId, byte[] nonce,
            byte[] characterMessageId, ConnectionProtocolLimits limits)
        {
            if (characterMessageId == null || characterMessageId.Length != ConnectionProtocolLimits.MessageIdBytes)
                throw new ArgumentException("The character transfer ID was invalid.", nameof(characterMessageId));
            byte[] payload = new byte[1 + ConnectionProtocolLimits.MessageIdBytes];
            payload[0] = 1;
            Buffer.BlockCopy(characterMessageId, 0, payload, 1, characterMessageId.Length);
            return Encode(new ProtocolPacket(ProtocolPacketKind.ClientCharacterRejection,
                ProtocolSequence.ClientCharacterRejection, sessionId, nonce, payload), limits);
        }

        public static ZPackage CreateFinalSaveBegin(
            byte[] sessionId,
            byte[] nonce,
            ConnectionProtocolLimits limits)
        {
            return Encode(
                new ProtocolPacket(
                    ProtocolPacketKind.FinalSaveBegin,
                    ProtocolSequence.FinalSaveBegin,
                    sessionId,
                    nonce,
                    ProtocolByteUtil.Empty),
                limits);
        }

        public static ZPackage CreateFinalSaveReady(
            byte[] sessionId,
            byte[] nonce,
            ConnectionProtocolLimits limits)
        {
            return Encode(
                new ProtocolPacket(
                    ProtocolPacketKind.FinalSaveReady,
                    ProtocolSequence.FinalSaveReady,
                    sessionId,
                    nonce,
                    ProtocolByteUtil.Empty),
                limits);
        }

        public static ZPackage CreateOperationalKickRequest(
            byte[] sessionId,
            byte[] nonce,
            ConnectionProtocolLimits limits)
        {
            return Encode(
                new ProtocolPacket(
                    ProtocolPacketKind.OperationalKickRequest,
                    ProtocolSequence.OperationalKickRequest,
                    sessionId,
                    nonce,
                    ProtocolByteUtil.Empty),
                limits);
        }

        public static ZPackage CreateOperationalKickComplete(
            byte[] sessionId,
            byte[] nonce,
            ConnectionProtocolLimits limits)
        {
            return Encode(
                new ProtocolPacket(
                    ProtocolPacketKind.OperationalKickComplete,
                    ProtocolSequence.OperationalKickComplete,
                    sessionId,
                    nonce,
                    ProtocolByteUtil.Empty),
                limits);
        }

        internal static ZPackage CreatePolicyUpdate(byte[] sessionId, byte[] nonce,
            uint generation, float carryWeight, float damage, ConnectionProtocolLimits limits)
        {
            if (generation < 2) throw new ArgumentOutOfRangeException(nameof(generation));
            using (MemoryStream stream = new MemoryStream(sizeof(float) * 2))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(carryWeight);
                writer.Write(damage);
                writer.Flush();
                return Encode(new ProtocolPacket(ProtocolPacketKind.PolicyUpdate,
                    generation, sessionId, nonce, stream.ToArray()), limits);
            }
        }

        internal static ZPackage CreatePolicyAck(byte[] sessionId, byte[] nonce,
            uint generation, ConnectionProtocolLimits limits)
        {
            if (generation < 2) throw new ArgumentOutOfRangeException(nameof(generation));
            return Encode(new ProtocolPacket(ProtocolPacketKind.PolicyAck,
                generation, sessionId, nonce, ProtocolByteUtil.Empty), limits);
        }

        public static bool TryDecodeChallengeOptions(
            ProtocolPacket packet,
            out ProtocolChallengeOptions options,
            out ProtocolRejection rejection)
        {
            options = null;
            rejection = null;
            if (packet == null ||
                packet.Kind != ProtocolPacketKind.Challenge ||
                packet.Sequence != ProtocolSequence.Challenge ||
                packet.Payload == null ||
                packet.Payload.Length < ProtocolChallengeOptions.EncodedBytes ||
                packet.Payload.Length > ProtocolChallengeOptions.MaxEncodedBytes)
            {
                rejection = Malformed("The connection challenge options were invalid.");
                return false;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(packet.Payload, false))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    ushort flags = reader.ReadUInt16();
                    int maximumManifestBytes = reader.ReadInt32();
                    int processScanIntervalSeconds = reader.ReadUInt16();
                    float maximumCarryWeight = reader.ReadSingle();
                    float maximumDamage = reader.ReadSingle();
                    if ((flags & ~ProtocolChallengeOptions.KnownFlags) != 0)
                    {
                        rejection = Malformed(
                            "The connection challenge contained unknown flags.");
                        return false;
                    }

                    int libraryCount = reader.ReadInt32();
                    if (libraryCount < 0 || libraryCount > ProtocolChallengeOptions.MaximumLibraryKeys)
                    {
                        rejection = Malformed("The connection challenge library count was invalid.");
                        return false;
                    }
                    List<string> libraryKeys = new List<string>(libraryCount);
                    string previousKey = null;
                    for (int index = 0; index < libraryCount; index++)
                    {
                        int byteCount = reader.ReadInt32();
                        if (byteCount < 1 || byteCount > ProtocolChallengeOptions.MaximumLibraryKeyUtf8Bytes ||
                            byteCount > stream.Length - stream.Position)
                        {
                            rejection = Malformed("The connection challenge library identity size was invalid.");
                            return false;
                        }
                        string key = RejectUtf8.GetString(ProtocolByteUtil.ReadExact(reader, byteCount));
                        if (!IntegrityAssemblyIdentity.IsCanonicalKey(key) ||
                            (previousKey != null && StringComparer.Ordinal.Compare(previousKey, key) >= 0))
                        {
                            rejection = Malformed("The connection challenge library identities were not canonical and unique.");
                            return false;
                        }
                        libraryKeys.Add(key);
                        previousKey = key;
                    }
                    if (stream.Position != stream.Length)
                    {
                        rejection = Malformed("The connection challenge options contained trailing data.");
                        return false;
                    }

                    options = new ProtocolChallengeOptions(
                        (flags & ProtocolChallengeOptions.EnforceManifestFlag) != 0,
                        (flags & ProtocolChallengeOptions.ServerCharactersFlag) != 0,
                        maximumManifestBytes,
                        (flags & ProtocolChallengeOptions.CheatEngineDetectionFlag) != 0,
                        (flags & ProtocolChallengeOptions.ExternalToolDetectionFlag) != 0,
                        (flags & ProtocolChallengeOptions.GenericProcessNameDetectionFlag) != 0,
                        (flags & ProtocolChallengeOptions.ValheimToolerDetectionFlag) != 0,
                        (flags & ProtocolChallengeOptions.CheatCommandMonitoringFlag) != 0,
                        (flags & ProtocolChallengeOptions.CheatCommandBlockingFlag) != 0,
                        (flags & ProtocolChallengeOptions.AllowAdminCheatCommandsFlag) != 0,
                        processScanIntervalSeconds,
                        (flags & ProtocolChallengeOptions.CarryWeightLimitFlag) != 0,
                        maximumCarryWeight,
                        (flags & ProtocolChallengeOptions.MaximumDamageLimitFlag) != 0,
                        maximumDamage,
                        libraryKeys);
                    return true;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                rejection = Malformed(
                    "The connection challenge limits were invalid.");
                return false;
            }
            catch (ArgumentException)
            {
                rejection = Malformed(
                    "The connection challenge policy combination was invalid.");
                return false;
            }
            catch (IOException)
            {
                rejection = Malformed(
                    "The connection challenge options were truncated.");
                return false;
            }
        }

        internal static ZPackage CreateChallenge(
            byte[] sessionId,
            byte[] nonce,
            ProtocolChallengeOptions options,
            ConnectionProtocolLimits limits)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            ushort flags = 0;
            if (options.EnforceManifest)
            {
                flags |= ProtocolChallengeOptions.EnforceManifestFlag;
            }

            if (options.ServerCharactersEnabled)
            {
                flags |= ProtocolChallengeOptions.ServerCharactersFlag;
            }

            if (options.DetectCheatEngine)
            {
                flags |= ProtocolChallengeOptions.CheatEngineDetectionFlag;
            }

            if (options.DetectExternalTools)
            {
                flags |= ProtocolChallengeOptions.ExternalToolDetectionFlag;
            }

            if (options.DetectGenericProcessNames)
            {
                flags |= ProtocolChallengeOptions.GenericProcessNameDetectionFlag;
            }

            if (options.DetectValheimTooler)
            {
                flags |= ProtocolChallengeOptions.ValheimToolerDetectionFlag;
            }

            if (options.MonitorCheatCommands)
            {
                flags |= ProtocolChallengeOptions.CheatCommandMonitoringFlag;
            }

            if (options.BlockCheatCommands)
            {
                flags |= ProtocolChallengeOptions.CheatCommandBlockingFlag;
            }

            if (options.AllowAdminCheatCommands)
            {
                flags |= ProtocolChallengeOptions.AllowAdminCheatCommandsFlag;
            }

            if (options.EnforceCarryWeightLimit)
            {
                flags |= ProtocolChallengeOptions.CarryWeightLimitFlag;
            }

            if (options.EnforceMaximumDamageLimit)
            {
                flags |= ProtocolChallengeOptions.MaximumDamageLimitFlag;
            }

            byte[] payload;
            using (MemoryStream stream =
                   new MemoryStream(ProtocolChallengeOptions.EncodedBytes))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(flags);
                writer.Write(options.MaximumManifestBytes);
                writer.Write((ushort)options.ProcessScanIntervalSeconds);
                writer.Write(options.MaximumCarryWeight);
                writer.Write(options.MaximumDamage);
                writer.Write(options.LibraryKeys.Count);
                foreach (string key in options.LibraryKeys)
                {
                    byte[] keyBytes = RejectUtf8.GetBytes(key);
                    writer.Write(keyBytes.Length);
                    writer.Write(keyBytes);
                }
                writer.Flush();
                payload = stream.ToArray();
            }

            return Encode(
                new ProtocolPacket(
                    ProtocolPacketKind.Challenge,
                    ProtocolSequence.Challenge,
                    sessionId,
                    nonce,
                    payload),
                limits);
        }

        internal static ZPackage CreateReject(
            byte[] sessionId,
            byte[] nonce,
            ProtocolRejection rejection,
            ConnectionProtocolLimits limits)
        {
            if (rejection == null)
            {
                throw new ArgumentNullException(nameof(rejection));
            }

            byte[] messageBytes = EncodeBoundedUtf8(
                rejection.SafeMessage,
                limits.MaxRejectMessageBytes);

            byte[] payload;
            using (MemoryStream stream = new MemoryStream(sizeof(int) * 3 + messageBytes.Length))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write((int)rejection.Code);
                writer.Write(rejection.LegacyDisconnectError);
                writer.Write(messageBytes.Length);
                writer.Write(messageBytes);
                WritePlayerMessageString(writer, rejection.PlayerMessageKey, 64);
                string[] arguments = rejection.PlayerMessageArguments;
                if (arguments.Length > 4 || !IsPlayerMessageKey(rejection.PlayerMessageKey) ||
                    (rejection.PlayerMessageKey.Length == 0 && arguments.Length != 0))
                    throw new InvalidDataException("Invalid player message metadata.");
                writer.Write(arguments.Length);
                foreach (string argument in arguments)
                    WritePlayerMessageString(writer, argument, 256);
                writer.Flush();
                payload = stream.ToArray();
            }

            return Encode(
                new ProtocolPacket(
                    ProtocolPacketKind.Reject,
                    0,
                    sessionId,
                    nonce,
                    payload),
                limits);
        }

        internal static ProtocolRejection DecodeRejectPayload(ProtocolPacket packet, ConnectionProtocolLimits limits)
        {
            if (packet.Kind != ProtocolPacketKind.Reject ||
                packet.Payload.Length > 12 + limits.MaxRejectMessageBytes + MaximumPlayerMessageBytes)
                throw new InvalidDataException("Invalid rejection payload.");
            using (MemoryStream stream = new MemoryStream(packet.Payload, false))
            using (BinaryReader reader = new BinaryReader(stream, RejectUtf8, true))
            {
                ProtocolRejectCode code = (ProtocolRejectCode)reader.ReadInt32();
                int legacy = reader.ReadInt32();
                if (code == ProtocolRejectCode.None || !Enum.IsDefined(typeof(ProtocolRejectCode), code))
                    throw new InvalidDataException("Invalid rejection code.");
                string diagnostic = ReadPlayerMessageString(reader, limits.MaxRejectMessageBytes);
                string key = ReadPlayerMessageString(reader, 64);
                int count = reader.ReadInt32();
                if (!IsPlayerMessageKey(key) || count < 0 || count > 4 || (key.Length == 0 && count != 0))
                    throw new InvalidDataException("Invalid player message metadata.");
                string[] arguments = new string[count];
                for (int index = 0; index < count; ++index)
                    arguments[index] = ReadPlayerMessageString(reader, 256);
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("Trailing rejection data.");
                return new ProtocolRejection(code, diagnostic, legacy).WithPlayerMessage(key, arguments);
            }
        }

        private static bool IsPlayerMessageKey(string key)
        {
            if (key.Length == 0) return true;
            if (!key.StartsWith("sm_", StringComparison.Ordinal) || key.Length > 64) return false;
            foreach (char value in key)
                if (!(value >= 'a' && value <= 'z') && !(value >= '0' && value <= '9') && value != '_') return false;
            return true;
        }

        private static void WritePlayerMessageString(BinaryWriter writer, string text, int maximumBytes)
        {
            byte[] bytes = RejectUtf8.GetBytes(text ?? "");
            if (bytes.Length > maximumBytes) throw new InvalidDataException("Player message exceeds its byte limit.");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadPlayerMessageString(BinaryReader reader, int maximumBytes)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maximumBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid rejection string length.");
            return RejectUtf8.GetString(reader.ReadBytes(length));
        }

        private static void ValidatePayloadForKind(
            ProtocolPacketKind kind,
            int payloadLength,
            ConnectionProtocolLimits limits)
        {
            switch (kind)
            {
                case ProtocolPacketKind.Challenge:
                    if (payloadLength < ProtocolChallengeOptions.EncodedBytes ||
                        payloadLength > ProtocolChallengeOptions.MaxEncodedBytes)
                    {
                        throw new InvalidDataException(
                            "Challenge payload had an invalid size.");
                    }

                    break;

                case ProtocolPacketKind.ManifestResponse:
                case ProtocolPacketKind.LibraryManifestUpdate:
                    if (payloadLength > limits.MaxManifestBytes)
                    {
                        throw new ArgumentOutOfRangeException(nameof(payloadLength));
                    }

                    break;

                case ProtocolPacketKind.ManifestAccepted:
                    if (payloadLength != 0)
                    {
                        throw new InvalidDataException("Manifest acceptance payload must be empty.");
                    }

                    break;

                case ProtocolPacketKind.FinalSaveBegin:
                case ProtocolPacketKind.FinalSaveReady:
                case ProtocolPacketKind.OperationalKickRequest:
                case ProtocolPacketKind.OperationalKickComplete:
                case ProtocolPacketKind.PolicyAck:
                case ProtocolPacketKind.BackupCaptureRequest:
                case ProtocolPacketKind.BackupCaptureCommitted:
                    if (payloadLength != 0)
                    {
                        throw new InvalidDataException(
                            "Final-save and operational-kick control payloads must be empty.");
                    }

                    break;

                case ProtocolPacketKind.PolicyUpdate:
                    if (payloadLength != sizeof(float) * 2)
                        throw new InvalidDataException("Policy update payload had an invalid size.");
                    break;

                case ProtocolPacketKind.CharacterFragment:
                    int maximumFragmentFrame =
                        ConnectionProtocolLimits.AbsoluteMaxFragmentDataBytes +
                        BoundedFragmentCodec.FrameHeaderBytes;
                    if (payloadLength > maximumFragmentFrame)
                    {
                        throw new ArgumentOutOfRangeException(nameof(payloadLength));
                    }

                    break;

                case ProtocolPacketKind.ReadyAck:
                    if (payloadLength != ConnectionProtocolLimits.MessageIdBytes)
                    {
                        throw new InvalidDataException("Ready ACK payload was invalid.");
                    }

                    break;

                case ProtocolPacketKind.ClientCharacterRejection:
                    if (payloadLength != 1 + ConnectionProtocolLimits.MessageIdBytes)
                        throw new InvalidDataException("Client character refusal payload had an invalid size.");
                    break;

                case ProtocolPacketKind.Reject:
                    if (payloadLength > sizeof(int) * 3 + limits.MaxRejectMessageBytes + MaximumPlayerMessageBytes)
                    {
                        throw new ArgumentOutOfRangeException(nameof(payloadLength));
                    }

                    break;

                default:
                    throw new InvalidDataException("Unknown packet type.");
            }
        }

        private static byte[] EncodeBoundedUtf8(string text, int maximumBytes)
        {
            if (maximumBytes <= 0 || string.IsNullOrEmpty(text))
            {
                return ProtocolByteUtil.Empty;
            }

            Encoder encoder = Encoding.UTF8.GetEncoder();
            char[] chars = text.ToCharArray();
            byte[] output = new byte[maximumBytes];
            int charsUsed;
            int bytesUsed;
            bool completed;
            encoder.Convert(
                chars,
                0,
                chars.Length,
                output,
                0,
                output.Length,
                true,
                out charsUsed,
                out bytesUsed,
                out completed);

            if (bytesUsed == output.Length)
            {
                return output;
            }

            byte[] trimmed = new byte[bytesUsed];
            Buffer.BlockCopy(output, 0, trimmed, 0, bytesUsed);
            return trimmed;
        }

        private static ProtocolRejection Malformed(string message)
        {
            return new ProtocolRejection(ProtocolRejectCode.MalformedPacket, message);
        }
    }

    internal static class ProtocolByteUtil
    {
        internal static readonly byte[] Empty = new byte[0];

        internal static byte[] ReadExact(BinaryReader reader, int length)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            if (length < 0)
            {
                throw new InvalidDataException("Negative read length.");
            }

            byte[] value = reader.ReadBytes(length);
            if (value.Length != length)
            {
                throw new EndOfStreamException();
            }

            return value;
        }

        internal static byte[] Clone(byte[] value)
        {
            if (value == null || value.Length == 0)
            {
                return Empty;
            }

            byte[] clone = new byte[value.Length];
            Buffer.BlockCopy(value, 0, clone, 0, value.Length);
            return clone;
        }

        internal static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            int difference = left.Length ^ right.Length;
            int commonLength = Math.Min(left.Length, right.Length);
            for (int i = 0; i < commonLength; ++i)
            {
                difference |= left[i] ^ right[i];
            }

            // Run through the remainder as well so length differences do not return early.
            for (int i = commonLength; i < left.Length; ++i)
            {
                difference |= left[i] ^ 0;
            }

            for (int i = commonLength; i < right.Length; ++i)
            {
                difference |= right[i] ^ 0;
            }

            return difference == 0;
        }

        internal static byte[] RandomBytes(int length)
        {
            byte[] value = new byte[length];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(value);
            }

            return value;
        }
    }
}
