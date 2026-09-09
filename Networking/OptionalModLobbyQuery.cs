using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Steamworks;

namespace ServerManager;

// The production adapter contains only established Steam read APIs. Tests inject
// all boundaries without Steam initialization, joining, searching or P2P traffic.
internal interface IOptionalModLobbyTransport
{
    bool IsAvailable { get; }
    EFriendRelationship GetFriendRelationship(CSteamID host);
    bool GetFriendGamePlayed(CSteamID host, out FriendGameInfo_t game);
    IDisposable Subscribe(Action<LobbyDataUpdate_t> callback);
    bool RequestLobbyData(CSteamID lobby);
    CSteamID GetLobbyOwner(CSteamID lobby);
    bool GetLobbyGameServer(CSteamID lobby, out uint address, out ushort port, out CSteamID host);
    int GetLobbyDataCount(CSteamID lobby);
    string GetLobbyData(CSteamID lobby, string key);
}

// Menu-owned advisory lookup for an explicitly configured Steam user. Steam's
// friend presence locates one current lobby; there is no global lobby search.
// Every API call and result publication remains on the owning main thread.
internal sealed class OptionalModLobbyQuery : IDisposable
{
    internal const double RefreshSeconds = 60;
    internal const double PresenceCheckSeconds = 5;
    internal const double RequestTimeoutSeconds = 15;
    internal const double CacheLifetimeSeconds = 300;
    internal const int MaximumCachedEndpoints = 8;
    internal const int MaximumLobbyMetadataCount = 512;
    internal const uint ValheimAppId = 892970;

    private sealed class SteamTransport : IOptionalModLobbyTransport
    {
        public bool IsAvailable => SteamManager.Initialized;
        public EFriendRelationship GetFriendRelationship(CSteamID host) => SteamFriends.GetFriendRelationship(host);
        public bool GetFriendGamePlayed(CSteamID host, out FriendGameInfo_t game) => SteamFriends.GetFriendGamePlayed(host, out game);
        public IDisposable Subscribe(Action<LobbyDataUpdate_t> callback) => Callback<LobbyDataUpdate_t>.Create(data => callback(data));
        public bool RequestLobbyData(CSteamID lobby) => SteamMatchmaking.RequestLobbyData(lobby);
        public CSteamID GetLobbyOwner(CSteamID lobby) => SteamMatchmaking.GetLobbyOwner(lobby);
        public bool GetLobbyGameServer(CSteamID lobby, out uint address, out ushort port, out CSteamID host) =>
            SteamMatchmaking.GetLobbyGameServer(lobby, out address, out port, out host);
        public int GetLobbyDataCount(CSteamID lobby) => SteamMatchmaking.GetLobbyDataCount(lobby);
        public string GetLobbyData(CSteamID lobby, string key) => SteamMatchmaking.GetLobbyData(lobby, key);
    }

    private readonly struct LobbyKey : IEquatable<LobbyKey>
    {
        internal readonly ulong Host, Lobby;
        internal LobbyKey(ulong host, ulong lobby) { Host = host; Lobby = lobby; }
        public bool Equals(LobbyKey other) => Host == other.Host && Lobby == other.Lobby;
        public override bool Equals(object? other) => other is LobbyKey key && Equals(key);
        public override int GetHashCode() => unchecked(Host.GetHashCode() * 397 ^ Lobby.GetHashCode());
    }

    private sealed class CachedLobby
    {
        internal IReadOnlyList<OptionalModCatalogEntry> Entries = Array.Empty<OptionalModCatalogEntry>();
        internal OptionalModQueryState State = OptionalModQueryState.Unavailable;
        internal bool HasValue;
        internal double ReceivedAt, NextAttempt;
        internal long LastUse;
    }

    private sealed class Request
    {
        internal readonly LobbyKey Key;
        internal readonly double Deadline;
        internal readonly int ThreadId;
        internal IDisposable? Subscription;
        internal bool Accepting, Complete, Success, Stopped;
        internal double NextDisposeAttempt;
        internal Request(LobbyKey key, double deadline, int threadId) { Key = key; Deadline = deadline; ThreadId = threadId; }
        internal void OnData(LobbyDataUpdate_t data)
        {
            // Member changes, other lobbies, previous requests and callbacks from
            // a retired window cannot complete this request or touch Unity.
            if (!Accepting || Stopped || Complete || Thread.CurrentThread.ManagedThreadId != ThreadId ||
                data.m_ulSteamIDLobby != Key.Lobby || data.m_ulSteamIDMember != Key.Lobby) return;
            Success = data.m_bSuccess == 1;
            Complete = true;
        }
    }

    // Normally disposal only unregisters from Steamworks.NET's managed callback
    // dispatcher, including while Steam is down. Retain the callback if an
    // unexpected disposal exception occurs and never overlap a new subscription.
    private static Request? _subscriptionOwner;
    private readonly IOptionalModLobbyTransport _transport;
    private readonly Func<double> _seconds;
    private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
    private readonly Dictionary<LobbyKey, CachedLobby> _cache = new();
    private string? _input;
    private ulong _host, _lobby;
    private bool _validInput, _disposed;
    private double _nextPresence;
    private long _useSequence;
    private CachedLobby? _current;
    private Request? _request;

    internal OptionalModLobbyQuery() : this(new SteamTransport(), () => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency) { }
    internal OptionalModLobbyQuery(IOptionalModLobbyTransport transport, Func<double> seconds)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _seconds = seconds ?? throw new ArgumentNullException(nameof(seconds));
    }

    internal OptionalModQueryState State { get; private set; } = OptionalModQueryState.Unavailable;
    internal IReadOnlyList<OptionalModCatalogEntry> Entries { get; private set; } = Array.Empty<OptionalModCatalogEntry>();
    internal bool IsStale { get; private set; }
    internal long DisplayRevision { get; private set; }

    internal static bool TryParseHostEndpoint(string? endpoint, out ulong host)
    {
        host = 0;
        if (endpoint == null || endpoint.Length > 128) return false;
        string text = endpoint.Trim();
        if (text.Length != 23 || !text.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return false;
        string number = text.Substring(6);
        if (!ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed) ||
            parsed.ToString(CultureInfo.InvariantCulture) != number) return false;
        CSteamID identity = new(parsed);
        if (!identity.IsValid() || identity.GetEUniverse() != EUniverse.k_EUniversePublic ||
            identity.GetEAccountType() != EAccountType.k_EAccountTypeIndividual || identity.GetUnAccountInstance() != 1) return false;
        host = parsed;
        return true;
    }

    internal void Tick(string endpoint)
    {
        AssertOwnerThread();
        if (_disposed) return;
        double now = _seconds();
        try
        {
            if (!string.Equals(_input, endpoint, StringComparison.Ordinal))
            {
                _input = endpoint;
                bool valid = TryParseHostEndpoint(endpoint, out ulong host);
                if (host != _host || valid != _validInput)
                {
                    StopRequest(now);
                    if (_current != null) _current.State = OptionalModQueryState.Unavailable;
                    _current = null;
                    _host = host;
                    _lobby = 0;
                    _nextPresence = 0;
                }
                _validInput = valid;
            }
            if (!_validInput) { Display(OptionalModQueryState.Unsupported, now); return; }
            if (_subscriptionOwner != null && _subscriptionOwner.Stopped && !ReleaseSubscription(_subscriptionOwner, now))
            { Display(OptionalModQueryState.Unavailable, now); return; }
            if (!_transport.IsAvailable)
            {
                LosePresence(OptionalModQueryState.Unsupported, now);
                _nextPresence = 0; // Re-check promptly when Steam returns, without invoking Steam while down.
                return;
            }
            ExpireValues(now);
            if ((now >= _nextPresence || _request?.Complete == true) && !ObservePresence(now)) return;
            if (_current == null) return;
            _current.LastUse = ++_useSequence;
            if (_request == null)
            {
                if (now < _current.NextAttempt) { Display(_current.State, now); return; }
                if (_subscriptionOwner != null) { Display(OptionalModQueryState.Unavailable, now); return; }
                _current.NextAttempt = now + RefreshSeconds;
                Request request = new(new LobbyKey(_host, _lobby), now + RequestTimeoutSeconds, _threadId);
                _request = _subscriptionOwner = request;
                request.Subscription = _transport.Subscribe(request.OnData);
                request.Accepting = true;
                if (!_transport.RequestLobbyData(new CSteamID(_lobby))) { Finish(OptionalModQueryState.Unavailable, now); return; }
                Display(OptionalModQueryState.Loading, now);
                return; // Even synchronous callbacks are consumed only by the next Tick.
            }
            if (now >= _request.Deadline) { Finish(OptionalModQueryState.Unavailable, now); return; }
            if (!_request.Complete) { Display(OptionalModQueryState.Loading, now); return; }
            if (!_request.Success) { Finish(OptionalModQueryState.Unavailable, now); return; }
            if (!HasMatchingHost()) { ClearCurrent(); Finish(OptionalModQueryState.Unavailable, now); return; }
            OptionalModCatalogResult result = ReadCatalog();
            // Re-check identity after capturing metadata, not only before it.
            // A friend hopping worlds during capture never publishes the prior lobby as fresh.
            if (!ObservePresence(now) || _request == null) return;
            if (!HasMatchingHost()) { ClearCurrent(); Finish(OptionalModQueryState.Unavailable, now); return; }
            if (result.Status == OptionalModCatalogStatus.Available)
            {
                _current.HasValue = true;
                _current.Entries = result.Entries;
                _current.ReceivedAt = now;
                Finish(OptionalModQueryState.Available, now);
            }
            else Finish(result.Status == OptionalModCatalogStatus.TooLarge ? OptionalModQueryState.TooLarge :
                result.Status == OptionalModCatalogStatus.Missing ? OptionalModQueryState.Unsupported : OptionalModQueryState.Unavailable, now);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { Finish(OptionalModQueryState.Unavailable, now); }
    }

    private bool ObservePresence(double now)
    {
        _nextPresence = now + PresenceCheckSeconds;
        CSteamID host = new(_host);
        if (_transport.GetFriendRelationship(host) != EFriendRelationship.k_EFriendRelationshipFriend)
        { LosePresence(OptionalModQueryState.Unsupported, now); return false; }
        if (!_transport.GetFriendGamePlayed(host, out FriendGameInfo_t game))
        { LosePresence(OptionalModQueryState.Unavailable, now); return false; }
        if (game.m_gameID.AppID().m_AppId != ValheimAppId || !game.m_steamIDLobby.IsValid() || !game.m_steamIDLobby.IsLobby())
        { LosePresence(OptionalModQueryState.Unsupported, now); return false; }
        ulong lobby = game.m_steamIDLobby.m_SteamID;
        if (_lobby != lobby)
        {
            StopRequest(now);
            ClearCurrent();
            _lobby = lobby;
            LobbyKey key = new(_host, lobby);
            if (!_cache.TryGetValue(key, out _current))
            {
                EvictOldest();
                _current = new CachedLobby();
                _cache.Add(key, _current);
            }
            _current.State = OptionalModQueryState.Unavailable;
        }
        if (_current!.HasValue && !HasMatchingHost())
        { ClearCurrent(); Finish(OptionalModQueryState.Unavailable, now); return false; }
        return true;
    }

    private bool HasMatchingHost()
    {
        CSteamID lobby = new(_lobby);
        return _transport.GetLobbyOwner(lobby).m_SteamID == _host &&
            _transport.GetLobbyGameServer(lobby, out uint address, out ushort port, out CSteamID host) &&
            address == 0 && port == 0 && host.m_SteamID == _host;
    }

    private OptionalModCatalogResult ReadCatalog()
    {
        CSteamID lobby = new(_lobby);
        int count = _transport.GetLobbyDataCount(lobby);
        if (count < 0) return new OptionalModCatalogResult(OptionalModCatalogStatus.Invalid);
        if (count > MaximumLobbyMetadataCount) return new OptionalModCatalogResult(OptionalModCatalogStatus.TooLarge);
        Dictionary<string, string> rules = new(StringComparer.Ordinal);
        string header = _transport.GetLobbyData(lobby, OptionalModCatalog.HeaderKey);
        if (string.IsNullOrEmpty(header)) return OptionalModCatalog.Decode(rules);
        if (header.Length > OptionalModCatalog.MaximumRuleValueBytes) return new OptionalModCatalogResult(OptionalModCatalogStatus.TooLarge);
        rules.Add(OptionalModCatalog.HeaderKey, header);
        int bytes = OptionalModCatalog.HeaderKey.Length + header.Length;
        for (int index = 0; index < OptionalModCatalog.MaximumChunkCount; ++index)
        {
            string key = OptionalModCatalog.KeyPrefix + index.ToString("D3", CultureInfo.InvariantCulture);
            string value = _transport.GetLobbyData(lobby, key);
            if (value == null) return new OptionalModCatalogResult(OptionalModCatalogStatus.Invalid);
            if (value.Length == 0) continue;
            if (value.Length > OptionalModCatalog.MaximumRuleValueBytes) return new OptionalModCatalogResult(OptionalModCatalogStatus.TooLarge);
            bytes += key.Length + value.Length;
            if (bytes > OptionalModCatalog.MaximumRulesBytes) return new OptionalModCatalogResult(OptionalModCatalogStatus.TooLarge);
            rules.Add(key, value);
        }
        if (_transport.GetLobbyData(lobby, OptionalModCatalog.HeaderKey) != header)
            return new OptionalModCatalogResult(OptionalModCatalogStatus.Invalid);
        return OptionalModCatalog.Decode(rules);
    }

    private void LosePresence(OptionalModQueryState state, double now)
    {
        StopRequest(now);
        ClearCurrent();
        _current = null;
        _lobby = 0;
        _nextPresence = now + PresenceCheckSeconds;
        Display(state, now);
    }

    private void Finish(OptionalModQueryState state, double now)
    {
        StopRequest(now);
        if (_current != null) { _current.State = state; _current.NextAttempt = now + RefreshSeconds; }
        Display(state, now);
    }

    private void ClearCurrent()
    {
        if (_current == null) return;
        _current.HasValue = false;
        _current.Entries = Array.Empty<OptionalModCatalogEntry>();
        _current.State = OptionalModQueryState.Unavailable;
    }

    private void StopRequest(double now)
    {
        Request? request = _request;
        _request = null;
        if (request != null) ReleaseSubscription(request, now);
    }

    private static bool ReleaseSubscription(Request request, double now)
    {
        request.Stopped = true;
        request.Accepting = false;
        if (now < request.NextDisposeAttempt) return false;
        try { request.Subscription?.Dispose(); }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        { request.NextDisposeAttempt = now + 1; return false; }
        request.Subscription = null;
        if (ReferenceEquals(_subscriptionOwner, request)) _subscriptionOwner = null;
        return true;
    }

    private void ExpireValues(double now)
    {
        foreach (CachedLobby value in _cache.Values)
        {
            if (!value.HasValue || now - value.ReceivedAt < CacheLifetimeSeconds) continue;
            value.HasValue = false;
            value.Entries = Array.Empty<OptionalModCatalogEntry>();
            if (value.State == OptionalModQueryState.Available) value.State = OptionalModQueryState.Unavailable;
        }
    }

    private void EvictOldest()
    {
        if (_cache.Count < MaximumCachedEndpoints) return;
        LobbyKey oldest = default;
        long lastUse = long.MaxValue;
        foreach (KeyValuePair<LobbyKey, CachedLobby> item in _cache)
            if (item.Value.LastUse < lastUse) { oldest = item.Key; lastUse = item.Value.LastUse; }
        _cache.Remove(oldest);
    }

    private void Display(OptionalModQueryState state, double now)
    {
        IReadOnlyList<OptionalModCatalogEntry> entries = _current?.Entries ?? Array.Empty<OptionalModCatalogEntry>();
        bool stale = _current != null && _current.HasValue &&
            (state != OptionalModQueryState.Available || now - _current.ReceivedAt >= RefreshSeconds);
        if (State == state && ReferenceEquals(Entries, entries) && IsStale == stale) return;
        State = state;
        Entries = entries;
        IsStale = stale;
        ++DisplayRevision;
    }

    internal void Pause()
    {
        AssertOwnerThread();
        if (_disposed) return;
        double now = _seconds();
        StopRequest(now);
        _nextPresence = 0;
        if (_current != null) _current.State = OptionalModQueryState.Unavailable;
        Display(OptionalModQueryState.Unavailable, now);
    }

    internal void Reset()
    {
        AssertOwnerThread();
        StopRequest(_seconds());
        _current = null;
        _cache.Clear();
        _input = null;
        _host = _lobby = 0;
        _validInput = false;
        _nextPresence = 0;
        Display(OptionalModQueryState.Unavailable, _seconds());
    }

    public void Dispose()
    {
        AssertOwnerThread();
        if (_disposed) return;
        Reset();
        _disposed = true;
    }

    private void AssertOwnerThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _threadId)
            throw new InvalidOperationException("Optional lobby queries must be owned by the Unity main thread.");
    }
}
