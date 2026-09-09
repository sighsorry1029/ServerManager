using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;

namespace ServerManager;

internal enum OptionalModQueryState { Loading, Available, Unavailable, Unsupported, TooLarge }

// A deliberately small boundary: tests replace Steam and DNS without opening a
// socket, initializing Steam, or needing a Unity scene.
internal interface IOptionalModQueryTransport
{
    bool IsAvailable { get; }
    Task<IPAddress[]> Resolve(string host);
    HServerQuery Ping(uint address, ushort port, ISteamMatchmakingPingResponse response);
    HServerQuery Rules(uint address, ushort port, ISteamMatchmakingRulesResponse response);
    void Cancel(HServerQuery handle);
}

/// <summary>
/// Main-menu-owned, advisory Steam rules query. Construct, Tick, Pause and dispose
/// only on the Unity main thread. Native callbacks only capture bounded data;
/// the next Tick advances the request and publishes a display revision.
/// </summary>
internal sealed class OptionalModQuery : IDisposable
{
    internal const double RefreshSeconds = 60;
    internal const double RequestTimeoutSeconds = 15;
    internal const double CacheLifetimeSeconds = 300;
    internal const int MaximumCachedEndpoints = 8;
    internal const int MaximumRules = 512;
    internal const int MaximumRuleKeyCharacters = 128;
    internal const int MaximumRuleValueCharacters = 2048;
    internal const int MaximumRuleCharacters = 128 * 1024;
    internal const uint ValheimAppId = 892970;

    private sealed class SteamTransport : IOptionalModQueryTransport
    {
        public bool IsAvailable => SteamManager.Initialized;
        // Only name resolution runs on a worker. No Steam or Unity call is made
        // by it or by any task continuation.
        public Task<IPAddress[]> Resolve(string host) => Task.Run(() => Dns.GetHostAddresses(host));
        public HServerQuery Ping(uint address, ushort port, ISteamMatchmakingPingResponse response) =>
            SteamMatchmakingServers.PingServer(address, port, response);
        public HServerQuery Rules(uint address, ushort port, ISteamMatchmakingRulesResponse response) =>
            SteamMatchmakingServers.ServerRules(address, port, response);
        public void Cancel(HServerQuery handle) => SteamMatchmakingServers.CancelServerQuery(handle);
    }

    private sealed class CachedEndpoint
    {
        internal IReadOnlyList<OptionalModCatalogEntry> Entries = Array.Empty<OptionalModCatalogEntry>();
        internal bool HasValue;
        internal double ReceivedAt;
        internal double NextAttempt;
        internal long LastUse;
        internal OptionalModQueryState State = OptionalModQueryState.Unavailable;
    }

    private enum Phase { Resolving, Ping, Rules }

    private sealed class Request
    {
        internal readonly IOptionalModQueryTransport Transport;
        internal readonly string Host;
        internal readonly ushort QueryPort;
        internal readonly double Deadline;
        internal readonly Dictionary<string, string> Rules = new(StringComparer.Ordinal);
        internal Phase Phase;
        internal uint Address;
        internal HServerQuery Handle = HServerQuery.Invalid;
        internal ISteamMatchmakingPingResponse? PingCallback;
        internal ISteamMatchmakingRulesResponse? RulesCallback;
        internal gameserveritem_t? Server;
        internal bool Complete;
        internal bool Failed;
        internal bool Invalid;
        internal bool TooLarge;
        internal bool Stopped;
        internal int RuleCharacters;
        internal double NextCancelAttempt;

        internal Request(IOptionalModQueryTransport transport, string host, ushort queryPort, double deadline)
        {
            Transport = transport;
            Host = host;
            QueryPort = queryPort;
            Deadline = deadline;
        }

        internal void AddRule(string key, string value)
        {
            if (Phase != Phase.Rules || Stopped || Complete || Invalid || TooLarge) return;
            if (key == null || value == null) { Invalid = true; return; }
            if (key.Length > MaximumRuleKeyCharacters || value.Length > MaximumRuleValueCharacters ||
                Rules.Count >= MaximumRules ||
                key.Length + value.Length > MaximumRuleCharacters - RuleCharacters)
            {
                TooLarge = true;
                return;
            }
            if (Rules.ContainsKey(key)) { Invalid = true; return; }
            Rules.Add(key, value);
            RuleCharacters += key.Length + value.Length;
        }
    }

    // Strongly root the native callback objects until CancelServerQuery succeeds.
    // This also prevents a new menu owner from starting another native request
    // if Steam disappeared before the old owner could cancel during teardown.
    // Stopped callbacks retain only their Request, never a menu or Unity object.
    private static Request? _nativeRequest;

    private readonly IOptionalModQueryTransport _transport;
    private readonly Func<double> _seconds;
    private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
    private readonly Dictionary<string, CachedEndpoint> _cache = new(StringComparer.Ordinal);
    private string? _endpoint;
    private string? _input;
    private string _parsedHost = string.Empty;
    private string _parsedKey = string.Empty;
    private ushort _parsedPort;
    private bool _parsedValid;
    private bool _parsedSupported;
    private CachedEndpoint? _current;
    private Request? _request;
    // DNS also has a single process-wide slot: disposing/recreating menu owners
    // cannot fan out uncancellable OS work. The worker captures only its host.
    private static Task<IPAddress[]>? _dnsTask;
    private static string? _dnsHost;
    private long _useSequence;
    private bool _disposed;

    internal OptionalModQuery() : this(new SteamTransport(), () => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency) { }

    internal OptionalModQuery(IOptionalModQueryTransport transport, Func<double> seconds)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _seconds = seconds ?? throw new ArgumentNullException(nameof(seconds));
    }

    internal long DisplayRevision { get; private set; }
    internal OptionalModQueryState State { get; private set; } = OptionalModQueryState.Unavailable;
    internal IReadOnlyList<OptionalModCatalogEntry> Entries { get; private set; } = Array.Empty<OptionalModCatalogEntry>();
    internal bool IsStale { get; private set; }

    internal void Tick(string endpoint)
    {
        AssertOwnerThread();
        if (_disposed) return;
        double now = _seconds();
        try
        {
            // Configuration usually remains unchanged for thousands of frames.
            // Parse/canonicalize only when its exact input changes.
            if (!string.Equals(_input, endpoint, StringComparison.Ordinal))
            {
                _input = endpoint;
                _parsedValid = ClientMenuBranding.TryParseDedicatedEndpoint(endpoint, out _parsedHost, out _parsedPort, out _);
                _parsedKey = _parsedValid ? _parsedHost.ToLowerInvariant() + ":" + _parsedPort : string.Empty;
                _parsedSupported = _parsedValid && _parsedPort != ushort.MaxValue &&
                    (!IPAddress.TryParse(_parsedHost, out IPAddress literal) || literal.AddressFamily == AddressFamily.InterNetwork);
            }
            if (!_parsedValid)
            {
                Pause();
                _endpoint = null;
                _current = null;
                Display(OptionalModQueryState.Unsupported, now);
                return;
            }
            string key = _parsedKey;
            if (!string.Equals(_endpoint, key, StringComparison.Ordinal))
            {
                StopRequest();
                _endpoint = key;
                if (!_cache.TryGetValue(key, out _current))
                {
                    EvictOldestEndpoint();
                    _current = new CachedEndpoint();
                    _cache.Add(key, _current);
                }
            }
            _current!.LastUse = ++_useSequence;
            ExpireValues(now);
            if (!_parsedSupported)
            {
                StopRequest();
                Display(OptionalModQueryState.Unsupported, now);
                return;
            }
            if (!_transport.IsAvailable)
            {
                StopRequest();
                Display(OptionalModQueryState.Unsupported, now);
                return;
            }
            if (_nativeRequest != null && _nativeRequest.Stopped && !ReleaseNative(_nativeRequest, now))
            {
                Display(OptionalModQueryState.Unavailable, now);
                return;
            }
            if (_request == null)
            {
                if (now < _current.NextAttempt)
                {
                    Display(_current.State, now);
                    return;
                }
                _current.NextAttempt = now + RefreshSeconds;
                _request = new Request(_transport, _parsedHost, (ushort)(_parsedPort + 1), now + RequestTimeoutSeconds);
            }
            Display(OptionalModQueryState.Loading, now);
            if (now >= _request.Deadline)
            {
                Finish(OptionalModQueryState.Unavailable, now);
                return;
            }
            Advance(_request, now);
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
        {
            Finish(OptionalModQueryState.Unavailable, now);
        }
    }

    private void Advance(Request request, double now)
    {
        if (request.Phase == Phase.Resolving)
        {
            if (!IPAddress.TryParse(request.Host, out IPAddress address))
            {
                // OS DNS cannot be forcibly cancelled. Keep its one worker even
                // across endpoint changes/Pause/menu recreation, and never fan
                // out more workers while that resolution is outstanding.
                if (_dnsTask != null && !_dnsTask.IsCompleted) return;
                if (_dnsTask == null || !string.Equals(_dnsHost, request.Host, StringComparison.OrdinalIgnoreCase))
                {
                    _dnsHost = request.Host;
                    _dnsTask = _transport.Resolve(request.Host);
                    _ = _dnsTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    return;
                }
                Task<IPAddress[]> completedDns = _dnsTask;
                _dnsTask = null;
                IPAddress[] addresses = completedDns.GetAwaiter().GetResult(); // Completed only: never blocks the UI.
                address = null!;
                // Use just one returned IPv4 address, not an address/port scan.
                for (int i = 0; i < addresses.Length && i < 32; ++i)
                    if (addresses[i].AddressFamily == AddressFamily.InterNetwork) { address = addresses[i]; break; }
                if (address == null) { Finish(OptionalModQueryState.Unsupported, now); return; }
            }
            if (_nativeRequest != null) return; // At most one native request, including a retired menu owner.
            request.Address = ToHostOrderIPv4(address);
            request.Phase = Phase.Ping;
            request.PingCallback = new ISteamMatchmakingPingResponse(
                server => { if (request.Phase == Phase.Ping && !request.Stopped && !request.Complete) { request.Server = server; request.Complete = true; } },
                () => { if (request.Phase == Phase.Ping && !request.Stopped && !request.Complete) { request.Failed = true; request.Complete = true; } });
            _nativeRequest = request;
            request.Handle = _transport.Ping(request.Address, request.QueryPort, request.PingCallback);
            // Synchronous callbacks only set flags above. The handle assignment
            // always precedes any cancellation or next-phase native call.
            if (request.Handle == HServerQuery.Invalid) Finish(OptionalModQueryState.Unavailable, now);
            return;
        }
        if (request.Phase == Phase.Ping)
        {
            if (!request.Complete) return;
            if (!ReleaseNative(request, now)) { Finish(OptionalModQueryState.Unavailable, now); return; }
            if (request.Failed) { Finish(OptionalModQueryState.Unavailable, now); return; }
            if (request.Server == null || request.Server.m_nAppID != ValheimAppId)
            {
                Finish(OptionalModQueryState.Unsupported, now);
                return;
            }
            // The advertised connection/query ports can be private NAT mappings.
            // Do not reject those or follow them: retain the configured external
            // address and game-port-plus-one which actually answered the ping.
            request.Server = null;
            request.Phase = Phase.Rules;
            request.Stopped = request.Complete = request.Failed = false;
            request.RulesCallback = new ISteamMatchmakingRulesResponse(request.AddRule,
                () => { if (request.Phase == Phase.Rules && !request.Stopped && !request.Complete) { request.Failed = true; request.Complete = true; } },
                () => { if (request.Phase == Phase.Rules && !request.Stopped) request.Complete = true; });
            _nativeRequest = request;
            request.Handle = _transport.Rules(request.Address, request.QueryPort, request.RulesCallback);
            if (request.Handle == HServerQuery.Invalid) Finish(OptionalModQueryState.Unavailable, now);
            return;
        }
        if (request.TooLarge) { Finish(OptionalModQueryState.TooLarge, now); return; }
        if (request.Invalid) { Finish(OptionalModQueryState.Unavailable, now); return; }
        if (!request.Complete) return;
        if (request.Failed) { Finish(OptionalModQueryState.Unavailable, now); return; }
        OptionalModCatalogResult result = OptionalModCatalog.Decode(request.Rules);
        if (result.Status == OptionalModCatalogStatus.Available)
        {
            _current!.HasValue = true;
            _current.Entries = result.Entries;
            _current.ReceivedAt = now;
            Finish(OptionalModQueryState.Available, now);
        }
        else Finish(result.Status == OptionalModCatalogStatus.TooLarge ? OptionalModQueryState.TooLarge :
            result.Status == OptionalModCatalogStatus.Missing ? OptionalModQueryState.Unsupported : OptionalModQueryState.Unavailable, now);
    }

    internal static uint ToHostOrderIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Steam server queries require an IPv4 address.", nameof(address));
        byte[] bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private void Finish(OptionalModQueryState state, double now)
    {
        StopRequest();
        if (_current != null)
        {
            _current.State = state;
            _current.NextAttempt = now + RefreshSeconds;
        }
        Display(state, now);
    }

    private static bool ReleaseNative(Request request, double now)
    {
        request.Stopped = true;
        try
        {
            if (request.Handle != HServerQuery.Invalid)
            {
                if (now < request.NextCancelAttempt) return false;
                request.NextCancelAttempt = now + 1;
                if (!request.Transport.IsAvailable) return false;
                request.Transport.Cancel(request.Handle);
            }
        }
        catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception)) { return false; }
        request.Handle = HServerQuery.Invalid;
        request.NextCancelAttempt = 0;
        request.PingCallback = null;
        request.RulesCallback = null;
        if (ReferenceEquals(_nativeRequest, request)) _nativeRequest = null;
        return true;
    }

    private void StopRequest()
    {
        if (_request == null) return;
        ReleaseNative(_request, _seconds());
        _request = null;
    }

    private void ExpireValues(double now)
    {
        foreach (CachedEndpoint item in _cache.Values)
        {
            if (!item.HasValue || now - item.ReceivedAt < CacheLifetimeSeconds) continue;
            item.HasValue = false;
            item.Entries = Array.Empty<OptionalModCatalogEntry>();
            if (item.State == OptionalModQueryState.Available) item.State = OptionalModQueryState.Unavailable;
        }
    }

    private void EvictOldestEndpoint()
    {
        if (_cache.Count < MaximumCachedEndpoints) return;
        string? oldest = null;
        long lastUse = long.MaxValue;
        foreach (KeyValuePair<string, CachedEndpoint> item in _cache)
            if (item.Value.LastUse < lastUse) { oldest = item.Key; lastUse = item.Value.LastUse; }
        if (oldest != null) _cache.Remove(oldest);
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
        if (_request == null) return;
        StopRequest();
        if (_current != null) _current.State = OptionalModQueryState.Unavailable;
        Display(OptionalModQueryState.Unavailable, _seconds());
    }

    internal void Reset()
    {
        AssertOwnerThread();
        StopRequest();
        _endpoint = null;
        _current = null;
        _cache.Clear();
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
            throw new InvalidOperationException("Optional-mod queries must be owned by the Unity main thread.");
    }
}
