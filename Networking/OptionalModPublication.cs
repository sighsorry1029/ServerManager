using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Steamworks;

namespace ServerManager;

/// <summary>
/// Main-thread, best-effort publication of the active admission policy's public
/// optional-plugin projection. This never reads folders or changes admission.
/// </summary>
internal sealed class OptionalModPublication
{
    internal static readonly long CheckIntervalTicks = 5L * Stopwatch.Frequency;
    internal static readonly long RefreshIntervalTicks = 60L * Stopwatch.Frequency;
    private readonly bool _dedicated;
    private readonly Func<ulong> _getReadyTarget;
    private readonly Action<ulong, string, string> _setKeyValue;
    private readonly Action<string> _warn;
    private readonly int _mainThreadId;
    private readonly HashSet<string> _writtenPayloadKeys = new(StringComparer.Ordinal);
    private object? _network;
    private IntegrityPolicySnapshot? _encodedPolicy;
    private IReadOnlyDictionary<string, string>? _encodedRules;
    private IntegrityPolicySnapshot? _publishedPolicy;
    private ulong _target;
    private bool _published;
    private long _nextCheck;
    private long _nextRefresh;
    private long _nextWarning;

    internal OptionalModPublication(Func<bool> isReady,
        Action<string, string> setKeyValue, Action<string> warn)
        : this(true,
            isReady == null ? throw new ArgumentNullException(nameof(isReady)) :
                () => isReady() ? 1UL : 0UL,
            (_, key, value) => setKeyValue(key, value), warn)
    {
        if (setKeyValue == null) throw new ArgumentNullException(nameof(setKeyValue));
    }

    internal OptionalModPublication(bool dedicated, Func<ulong> getReadyTarget,
        Action<ulong, string, string> setKeyValue, Action<string> warn)
    {
        _dedicated = dedicated;
        _getReadyTarget = getReadyTarget ?? throw new ArgumentNullException(nameof(getReadyTarget));
        _setKeyValue = setKeyValue ?? throw new ArgumentNullException(nameof(setKeyValue));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    internal void Begin(object network)
    {
        Stop();
        _network = network ?? throw new ArgumentNullException(nameof(network));
    }

    // Managed cleanup only: Steam can already be shut down here. Keep the bounded
    // owned-key set so a later world can blank stale payloads without touching
    // Valheim's tags/rules or any other mod's metadata.
    internal void Stop(object? network = null)
    {
        if (network != null && !ReferenceEquals(_network, network)) return;
        _network = null;
        _encodedPolicy = null;
        _encodedRules = null;
        _publishedPolicy = null;
        _published = false;
        _nextCheck = 0;
        _nextRefresh = 0;
        _nextWarning = 0;
    }

    internal void Tick(object? network, bool isServer, bool isDedicated,
        bool steamBackend, IntegrityPolicySnapshot? policy, long now)
    {
        // A transport belongs to exactly one role. In particular, never probe
        // GameServer on a listen host or SteamAPI on a dedicated server/client.
        // Start/Stop and the thread guard also precede every native operation.
        if (_network == null || !ReferenceEquals(_network, network) ||
            !isServer || isDedicated != _dedicated || !steamBackend ||
            Thread.CurrentThread.ManagedThreadId != _mainThreadId || now < _nextCheck)
            return;
        _nextCheck = now + CheckIntervalTicks;

        try
        {
            ulong target = _getReadyTarget();
            if (target == 0)
            {
                _published = false;
                return;
            }
            if (_target != target)
            {
                // Valheim may recreate a listen lobby without replacing ZNet.
                // Publish immediately on this check, never wait for refresh or
                // write the old lobby's cleanup keys into the replacement.
                _target = target;
                _writtenPayloadKeys.Clear();
                _published = false;
            }

            // Valheim can clear its rules while registering/re-registering.
            // Refresh even an unchanged generation, with bounded native traffic.
            if (_published && ReferenceEquals(_publishedPolicy, policy) && now < _nextRefresh)
                return;
            if (_encodedRules == null || !ReferenceEquals(_encodedPolicy, policy))
            {
                _encodedRules = policy == null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [OptionalModCatalog.HeaderKey] = OptionalModCatalog.UnavailableHeaderValue
                    }
                    : OptionalModCatalog.Encode(policy);
                _encodedPolicy = policy;
            }

            // Invalidate first, commit the digest-bearing header last. A rules
            // query crossing these writes is unavailable/invalid, never a mixed
            // generation displayed as a successful empty or partial catalog.
            _setKeyValue(target, OptionalModCatalog.HeaderKey, OptionalModCatalog.UnavailableHeaderValue);
            foreach (string key in _writtenPayloadKeys)
                if (!_encodedRules.ContainsKey(key)) _setKeyValue(target, key, string.Empty);
            foreach (KeyValuePair<string, string> rule in _encodedRules)
            {
                if (rule.Key == OptionalModCatalog.HeaderKey) continue;
                _writtenPayloadKeys.Add(rule.Key);
                _setKeyValue(target, rule.Key, rule.Value);
            }
            _setKeyValue(target, OptionalModCatalog.HeaderKey, _encodedRules[OptionalModCatalog.HeaderKey]);
            _publishedPolicy = policy;
            _published = true;
            _nextRefresh = now + RefreshIntervalTicks;
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            _published = false;
            if (now < _nextWarning) return;
            _nextWarning = now + RefreshIntervalTicks;
            try
            {
                _warn("Optional mod catalog advertisement could not be updated; " +
                    "the active integrity policy is unchanged and publication will retry. " +
                    exception.GetType().Name + ": " + exception.Message);
            }
            catch (Exception loggingException) when (!IntegrityCanonical.IsFatal(loggingException))
            {
                // Neither unavailable Steam metadata nor unavailable logging may
                // stop a server, roll back its policy, or alter admission.
            }
        }
    }
}

// No owned Steam callbacks, lobby joins or networking state. All operations run
// only inside the listen-role publisher's main-thread/readiness/cadence guards.
internal sealed class OptionalModLobbyPublicationTransport
{
    private readonly Func<bool> _initialized;
    private readonly Func<ulong> _getLobby;
    private readonly Func<ulong> _getUser;
    private readonly Func<ulong, ulong> _getOwner;
    private readonly Func<ulong, string, string, bool> _setData;

    internal OptionalModLobbyPublicationTransport() : this(
        () => SteamManager.Initialized,
        () => ZSteamMatchmaking.instance == null ? 0UL :
            ValheimPrivateAccess.GetSteamServerLobby(ZSteamMatchmaking.instance).m_SteamID,
        () => SteamUser.GetSteamID().m_SteamID,
        lobby => SteamMatchmaking.GetLobbyOwner((CSteamID)lobby).m_SteamID,
        (lobby, key, value) => SteamMatchmaking.SetLobbyData((CSteamID)lobby, key, value)) { }

    internal OptionalModLobbyPublicationTransport(Func<bool> initialized, Func<ulong> getLobby,
        Func<ulong> getUser, Func<ulong, ulong> getOwner, Func<ulong, string, string, bool> setData)
    {
        _initialized = initialized ?? throw new ArgumentNullException(nameof(initialized));
        _getLobby = getLobby ?? throw new ArgumentNullException(nameof(getLobby));
        _getUser = getUser ?? throw new ArgumentNullException(nameof(getUser));
        _getOwner = getOwner ?? throw new ArgumentNullException(nameof(getOwner));
        _setData = setData ?? throw new ArgumentNullException(nameof(setData));
    }

    internal ulong GetReadyTarget()
    {
        if (!_initialized()) return 0;
        ulong lobby = _getLobby();
        if (lobby == 0) return 0; // Ordinary singleplayer has no server lobby.
        ulong user = _getUser();
        return user != 0 && _getOwner(lobby) == user ? lobby : 0;
    }

    internal void SetKeyValue(ulong lobby, string key, string value)
    {
        // Steam's limits are 255 bytes/key and 8192 bytes/value. Reuse the much
        // smaller codec budget (96-byte ASCII values, at most 147 owned keys),
        // never truncate or overwrite vanilla lobby metadata to make space.
        if (lobby == 0 || !key.StartsWith(OptionalModCatalog.KeyPrefix, StringComparison.Ordinal) ||
            key.Length > OptionalModCatalog.HeaderKey.Length || value.Length > OptionalModCatalog.MaximumRuleValueBytes)
            throw new ArgumentException("Invalid optional catalog lobby metadata.");
        foreach (char character in key)
            if (character == 0 || character > 127) throw new ArgumentException("Invalid optional catalog lobby key.");
        foreach (char character in value)
            if (character == 0 || character > 127) throw new ArgumentException("Invalid optional catalog lobby value.");
        if (!_setData(lobby, key, value))
            throw new InvalidOperationException("Steam rejected optional catalog lobby metadata.");
    }
}

internal static partial class ServerManagerRuntime
{
    private static OptionalModPublication? _optionalModPublication;
    private static OptionalModPublication? _optionalModLobbyPublication;

    private static void BeginOptionalModPublication(ZNet network)
    {
        if (!_initialized || _shuttingDown) return;
        _optionalModPublication ??= new OptionalModPublication(
            () => GameServer.GetHSteamPipe() != (HSteamPipe)0,
            SteamGameServer.SetKeyValue,
            message => ServerManagerPlugin.Log.LogWarning(message));
        if (_optionalModLobbyPublication == null)
        {
            OptionalModLobbyPublicationTransport lobby = new();
            _optionalModLobbyPublication = new OptionalModPublication(false,
                lobby.GetReadyTarget, lobby.SetKeyValue,
                message => ServerManagerPlugin.Log.LogWarning(message));
        }
        _optionalModPublication.Begin(network);
        _optionalModLobbyPublication.Begin(network);
    }

    private static void TickOptionalModPublication()
    {
        ZNet? server = ZNet.instance;
        if (!_initialized || _shuttingDown || server == null || !server.IsServer()) return;
        bool dedicated = server.IsDedicated();
        (dedicated ? _optionalModPublication : _optionalModLobbyPublication)?.Tick(server, true, dedicated,
            ZNet.m_onlineBackend == OnlineBackendType.Steamworks,
            _integrityService?.CurrentPolicy, Stopwatch.GetTimestamp());
    }

    private static void StopOptionalModPublication(ZNet? network = null)
    {
        _optionalModPublication?.Stop(network);
        _optionalModLobbyPublication?.Stop(network);
    }
}
