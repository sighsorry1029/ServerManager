using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ServerManager;
using Steamworks;

// Production state machine + codec/models, actual managed Steamworks.NET types;
// every transport method is inert. No native Steam or game initialization occurs.
internal static class OptionalModLobbyQuerySmoke
{
    private const ulong Host = 76561198000000001UL;
    private static readonly ulong Lobby = LobbyId(123);
    private static double _now;
    private static int _checks;
    private static string Endpoint(ulong host = Host) => "steam:" + host.ToString(CultureInfo.InvariantCulture);
    private static ulong LobbyId(uint id) => new CSteamID(new AccountID_t(id), 0x40000,
        EUniverse.k_EUniversePublic, EAccountType.k_EAccountTypeChat).m_SteamID;
    private static void Check(bool condition, string message)
    { ++_checks; if (!condition) throw new InvalidOperationException(message); }
    private static OptionalModLobbyQuery Query(Fake transport) { _now = 0; return new OptionalModLobbyQuery(transport, () => _now); }
    private static Dictionary<string, string> Catalog(string name = "Optional", params string[] versions) =>
        OptionalModCatalog.Encode(new IntegrityPolicySnapshot(1, new[]
        {
            new IntegrityPolicyRule("example.optional", name, IntegrityRequirement.Optional, new[] { new string('a', 64) },
                versions.Length == 0 ? new[] { "1.0.0" } : versions)
        })).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static int Main()
    {
        try
        {
            Parser(); FreshAndCached(); CallbackGuards(); PresenceAndOwnership(); Bounds(); Lifetime();
            Console.WriteLine("Optional lobby query smoke passed (" + _checks + " assertions; production sources/managed Steam types; no Steam initialization, network, join or game).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Parser()
    {
        foreach (string text in new[] { Endpoint(), " STEAM:" + Host + " " })
            Check(OptionalModLobbyQuery.TryParseHostEndpoint(text, out ulong parsed) && parsed == Host, "Valid explicit host was rejected");
        foreach (string text in new[] { "", "steam:", "steam:+" + Host, "steam:0" + Host, "steam:" + Host + ":2456", "steam://" + Host,
            "steam:76561198 00000001", "steam:" + Host + "\nwrong", Host.ToString(), "192.0.2.10:2456", "steam:18446744073709551615",
            Endpoint(new CSteamID(new AccountID_t(1), 1, EUniverse.k_EUniverseBeta, EAccountType.k_EAccountTypeIndividual).m_SteamID),
            Endpoint(new CSteamID(new AccountID_t(1), 1, EUniverse.k_EUniversePublic, EAccountType.k_EAccountTypeConsoleUser).m_SteamID),
            Endpoint(new CSteamID(new AccountID_t(1), 0, EUniverse.k_EUniversePublic, EAccountType.k_EAccountTypeIndividual).m_SteamID),
            Endpoint(new CSteamID(new AccountID_t(0), 1, EUniverse.k_EUniversePublic, EAccountType.k_EAccountTypeIndividual).m_SteamID), Endpoint(Lobby) })
            Check(!OptionalModLobbyQuery.TryParseHostEndpoint(text, out ulong parsed) && parsed == 0, "Invalid/non-user/nonpublic host accepted: " + text);
        Check(!OptionalModLobbyQuery.TryParseHostEndpoint(null, out _), "Null host accepted");
        using (var noSteam = new OptionalModLobbyQuery())
        {
            noSteam.Tick(Endpoint());
            Check(noSteam.State == OptionalModQueryState.Unsupported, "Default transport must not initialize Steam or query while unavailable");
        }
    }

    private static void Complete(OptionalModLobbyQuery query, Fake transport, string? endpoint = null)
    {
        endpoint ??= Endpoint();
        query.Tick(endpoint);
        transport.Emit(transport.Lobby);
        query.Tick(endpoint);
        Check(query.State == OptionalModQueryState.Available && transport.Active == 0, "Valid request did not publish and dispose its callback");
    }

    private static void FreshAndCached()
    {
        var transport = new Fake();
        using (var query = Query(transport))
        {
            Check(transport.NativeCalls == 0 && transport.Subscriptions.Count == 0, "Construction started background work");
            Complete(query, transport);
            Check(query.Entries.Count == 1 && query.Entries[0].PluginGuid == "example.optional" && !query.IsStale,
                "Shared codec identity/version result changed or was prematurely locally filtered");
            long revision = query.DisplayRevision;
            int nativeCalls = transport.NativeCalls;
            for (int index = 0; index < 1000; ++index) query.Tick(Endpoint());
            Check(transport.NativeCalls == nativeCalls && transport.Requests == 1 && query.DisplayRevision == revision,
                "Steady frames allocated a new request, polled Steam or repainted the cache");
            _now = 5; query.Tick(Endpoint());
            Check(transport.Requests == 1 && query.DisplayRevision == revision, "Presence recheck incorrectly requested or repainted metadata");
            _now = 59; query.Tick(Endpoint());
            Check(transport.Requests == 1, "Metadata refreshed before sixty seconds");
            _now = 60; query.Tick(Endpoint());
            Check(transport.Requests == 2 && query.State == OptionalModQueryState.Loading && query.IsStale && query.Entries.Count == 1,
                "Refresh must retain only an explicitly stale cached list");
            transport.Emit(transport.Lobby, success: 0); query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Unavailable && query.IsStale, "Failed refresh lost bounded stale status");
            _now = 300; query.Tick(Endpoint());
            Check(query.Entries.Count == 0 && !query.IsStale, "Expired lobby cache stayed visible indefinitely");
        }

        transport = new Fake { Synchronous = true, EmitDuringSubscribe = true };
        using (var query = Query(transport))
        {
            query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Loading && transport.Disposals == 0 && transport.Reads == 0,
                "Synchronous callbacks decoded data or disposed an unassigned subscription");
            query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Available && transport.Disposals == 1, "Synchronous lobby data did not finish next tick");
        }

        transport = new Fake { Metadata = new Dictionary<string, string>() };
        using (var query = Query(transport))
        {
            query.Tick(Endpoint()); transport.Emit(Lobby); query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Unsupported && query.Entries.Count == 0, "Missing catalog did not remain unsupported");
            long revision = query.DisplayRevision;
            query.Tick(Endpoint());
            Check(query.DisplayRevision == revision && transport.Requests == 1, "Cached unsupported status reset or retried per frame");
        }
        transport = new Fake { Metadata = OptionalModCatalog.Encode(new IntegrityPolicySnapshot(1, Array.Empty<IntegrityPolicyRule>()))
            .ToDictionary(pair => pair.Key, pair => pair.Value) };
        using (var query = Query(transport))
        {
            Complete(query, transport);
            Check(query.Entries.Count == 0 && !query.IsStale, "Available empty list must not become missing or unavailable");
        }
    }

    private static void CallbackGuards()
    {
        var transport = new Fake();
        using (var query = Query(transport))
        {
            query.Tick(Endpoint());
            transport.Emit(LobbyId(999)); transport.Emit(Lobby, member: Host);
            query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Loading && transport.Reads == 0, "Foreign/member callback completed lobby-level request");
            transport.Emit(Lobby); query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Available, "Matching lobby-level callback was ignored");
            Action<LobbyDataUpdate_t> old = transport.Subscriptions[0].Callback;
            _now = 60; query.Tick(Endpoint());
            old(Data(Lobby, 0)); query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Loading, "Retained old delegate completed a new request");
            transport.Emit(Lobby); query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Available, "Current callback was broken by old callback");
        }
        transport = new Fake();
        using (var query = Query(transport))
        {
            query.Tick(Endpoint());
            Task.Run(() => transport.Emit(Lobby)).GetAwaiter().GetResult();
            query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Loading && transport.Reads == 0, "Off-owner callback was accepted");
            bool rejected = Task.Run(() => { try { query.Tick(Endpoint()); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult();
            Check(rejected, "Off-owner Tick was allowed to invoke Steam");
            query.Pause();
            transport.Emit(Lobby, includeDisposed: true);
            Check(query.State == OptionalModQueryState.Unavailable && transport.Active == 0 && transport.Reads == 0, "Paused callback performed hidden-menu work");
        }
    }

    private static void PresenceAndOwnership()
    {
        foreach (string failure in new[] { "nonfriend", "offline", "othergame", "notlobby", "owner", "serverhost", "servermissing", "address", "port" })
        {
            var transport = new Fake();
            if (failure == "nonfriend") transport.Friend = false;
            if (failure == "offline") transport.Online = false;
            if (failure == "othergame") transport.AppId = 480;
            if (failure == "notlobby") transport.Lobby = Host;
            if (failure == "owner") transport.Owner = Host + 1;
            if (failure == "serverhost") transport.ServerHost = Host + 1;
            if (failure == "servermissing") transport.ServerKnown = false;
            if (failure == "address") transport.Address = 0x7f000001;
            if (failure == "port") transport.Port = 2456;
            using var query = Query(transport);
            query.Tick(Endpoint()); transport.Emit(transport.Lobby); query.Tick(Endpoint());
            Check(query.State != OptionalModQueryState.Available && query.Entries.Count == 0 && transport.Reads == 0,
                "Invalid friend/lobby/listen-host identity reached metadata: " + failure);
            if (failure == "nonfriend" || failure == "offline" || failure == "othergame" || failure == "notlobby")
                Check(transport.Requests == 0 && transport.Subscriptions.Count == 0, "Unsupported presence initiated a lobby request");
        }
        var fake = new Fake();
        using (var query = Query(fake))
        {
            Complete(query, fake);
            fake.Lobby = LobbyId(124); fake.Metadata = Catalog("New world");
            _now = 5; query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Loading && query.Entries.Count == 0 && !query.IsStale && fake.Requests == 2,
                "Lobby hop represented a previous-world list as fresh or reused its refresh gate");
            fake.Emit(Lobby); query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Loading, "Previous-world callback completed new-world request");
            fake.Emit(fake.Lobby); query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Available && query.Entries[0].Name == "New world", "New world catalog was not isolated by lobby");
            fake.Online = false; _now = 10; query.Tick(Endpoint());
            Check(query.Entries.Count == 0 && !query.IsStale && query.State == OptionalModQueryState.Unavailable, "Offline host retained a fresh previous-world list");
            fake.Online = true; _now = 15; query.Tick(Endpoint());
            Check(query.State != OptionalModQueryState.Available, "Host returning online silently restored stale metadata as fresh");
        }
        fake = new Fake();
        using (var query = Query(fake))
        {
            Complete(query, fake);
            fake.Owner = Host + 1; _now = 5; query.Tick(Endpoint());
            Check(query.Entries.Count == 0 && query.State == OptionalModQueryState.Unavailable, "Cached owner change retained trusted-looking old entries");
        }
        fake = new Fake();
        using (var query = Query(fake))
        {
            query.Tick(Endpoint()); fake.Emit(Lobby);
            fake.OnRead = key => { if (key.EndsWith("000", StringComparison.Ordinal)) fake.Lobby = LobbyId(777); };
            query.Tick(Endpoint());
            Check(query.State != OptionalModQueryState.Available && query.Entries.Count == 0, "World hop during capture published old metadata");
        }
    }

    private static void Bounds()
    {
        foreach (string failure in new[] { "count", "negative", "header", "chunk", "digest", "mixed", "unicode" })
        {
            var fake = new Fake();
            if (failure == "count") fake.CountOverride = OptionalModLobbyQuery.MaximumLobbyMetadataCount + 1;
            if (failure == "negative") fake.CountOverride = -1;
            if (failure == "header") fake.Metadata[OptionalModCatalog.HeaderKey] = new string('x', 97);
            if (failure == "chunk") fake.Metadata[OptionalModCatalog.KeyPrefix + "000"] = new string('x', 97);
            if (failure == "digest") fake.Metadata[OptionalModCatalog.KeyPrefix + "000"] = "AAAA";
            if (failure == "unicode") fake.Metadata[OptionalModCatalog.KeyPrefix + "000"] = "한글";
            if (failure == "mixed") fake.OnRead = key => { if (key.EndsWith("001", StringComparison.Ordinal)) fake.Metadata[OptionalModCatalog.HeaderKey] = "2|missing|0|0|"; };
            using var query = Query(fake);
            query.Tick(Endpoint()); fake.Emit(Lobby); query.Tick(Endpoint());
            Check(query.State == (failure == "count" || failure == "header" || failure == "chunk" ? OptionalModQueryState.TooLarge : OptionalModQueryState.Unavailable) &&
                query.Entries.Count == 0, "Malformed/oversized/mixed metadata was accepted: " + failure);
            Check(fake.ReadKeys.All(key => key.StartsWith(OptionalModCatalog.KeyPrefix, StringComparison.Ordinal)), "Query read unrelated lobby metadata values");
        }
        var transport = new Fake { CountOverride = 512 };
        using (var query = Query(transport))
        {
            Complete(query, transport);
            Check(transport.Reads <= OptionalModCatalog.MaximumChunkCount + 2, "Catalog read performed unbounded metadata enumeration");
        }
        transport = new Fake { Synchronous = true };
        using (var query = Query(transport))
        {
            for (uint index = 0; index < 10; ++index)
            {
                transport.Owner = transport.ServerHost = Host + index;
                transport.Lobby = LobbyId(200 + index);
                query.Tick(Endpoint(Host + index)); query.Tick(Endpoint(Host + index));
                Check(query.State == OptionalModQueryState.Available, "Independent host/lobby fixture failed");
            }
            var cache = (IDictionary)typeof(OptionalModLobbyQuery).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(query)!;
            Check(cache.Count == OptionalModLobbyQuery.MaximumCachedEndpoints, "Host+lobby cache exceeded eight entries");
        }
    }

    private static void Lifetime()
    {
        var transport = new Fake { RequestAccepted = false };
        using (var query = Query(transport))
        {
            query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Unavailable && transport.Active == 0, "Rejected request retained its callback");
            _now = 59; query.Tick(Endpoint()); Check(transport.Requests == 1, "Rejected request retried before cooldown");
            _now = 60; transport.RequestAccepted = true; query.Tick(Endpoint());
            Check(transport.Requests == 2, "Rejected request never recovered after cooldown");
            _now = 75; query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Unavailable && transport.Active == 0, "Timeout failed to unregister callback");
        }
        transport = new Fake();
        using (var query = Query(transport))
        {
            query.Tick(Endpoint()); transport.Available = false; query.Tick(Endpoint());
            Check(query.State == OptionalModQueryState.Unsupported && transport.Active == 0 && transport.Disposals == 1,
                "Steam loss did not safely unregister its managed callback");
            int nativeCalls = transport.NativeCalls;
            for (int index = 0; index < 100; ++index) query.Tick(Endpoint());
            Check(transport.NativeCalls == nativeCalls, "Steam-down frames invoked Steam APIs");
            transport.Available = true; _now = 60; Complete(query, transport);
        }
        transport = new Fake();
        var disposed = Query(transport);
        disposed.Tick(Endpoint()); disposed.Dispose();
        transport.Emit(Lobby, includeDisposed: true); disposed.Tick(Endpoint());
        Check(transport.Active == 0 && transport.Reads == 0 && disposed.Entries.Count == 0, "Disposed menu accepted late lobby data");

        transport = new Fake { ThrowDispose = true };
        var retired = Query(transport);
        retired.Tick(Endpoint()); retired.Dispose();
        var replacement = new OptionalModLobbyQuery(transport, () => _now);
        try
        {
            replacement.Tick(Endpoint());
            Check(transport.Subscriptions.Count == 1 && transport.Active == 1, "Failed disposal overlapped callbacks with a replacement menu");
            transport.ThrowDispose = false; _now = 1; replacement.Tick(Endpoint());
            Check(transport.Active == 1 && transport.Subscriptions.Count == 2 && transport.MaximumActive == 1,
                "Retired callback was not released before replacement subscription");
            transport.Subscriptions[0].Callback(Data(Lobby, 1)); replacement.Tick(Endpoint());
            Check(replacement.State == OptionalModQueryState.Loading, "Retired callback completed replacement menu request");
        }
        finally { replacement.Dispose(); }
    }

    private static LobbyDataUpdate_t Data(ulong lobby, byte success, ulong? member = null) => new()
    { m_ulSteamIDLobby = lobby, m_ulSteamIDMember = member ?? lobby, m_bSuccess = success };

    private sealed class Fake : IOptionalModLobbyTransport
    {
        internal bool Available = true, Friend = true, Online = true, ServerKnown = true, RequestAccepted = true;
        internal bool Synchronous, EmitDuringSubscribe, ThrowDispose;
        internal ulong Lobby = OptionalModLobbyQuerySmoke.Lobby, Owner = Host, ServerHost = Host;
        internal uint AppId = 892970, Address;
        internal ushort Port;
        internal int Requests, Disposals, Active, MaximumActive, Reads, NativeCalls;
        internal int? CountOverride;
        internal Dictionary<string, string> Metadata = Catalog();
        internal readonly List<Subscription> Subscriptions = new();
        internal readonly List<string> ReadKeys = new();
        internal Action<string>? OnRead;
        public bool IsAvailable => Available;
        public EFriendRelationship GetFriendRelationship(CSteamID host)
        { ++NativeCalls; return Friend ? EFriendRelationship.k_EFriendRelationshipFriend : EFriendRelationship.k_EFriendRelationshipNone; }
        public bool GetFriendGamePlayed(CSteamID host, out FriendGameInfo_t game)
        { ++NativeCalls; game = new FriendGameInfo_t { m_gameID = new CGameID(new AppId_t(AppId)), m_steamIDLobby = new CSteamID(Lobby) }; return Online; }
        public IDisposable Subscribe(Action<LobbyDataUpdate_t> callback)
        {
            var subscription = new Subscription(this, callback);
            Subscriptions.Add(subscription); ++Active; MaximumActive = Math.Max(Active, MaximumActive);
            if (EmitDuringSubscribe) callback(Data(Lobby, 0));
            return subscription;
        }
        public bool RequestLobbyData(CSteamID lobby)
        { ++NativeCalls; ++Requests; if (Synchronous) Emit(lobby.m_SteamID); return RequestAccepted; }
        public CSteamID GetLobbyOwner(CSteamID lobby) { ++NativeCalls; return new CSteamID(Owner); }
        public bool GetLobbyGameServer(CSteamID lobby, out uint address, out ushort port, out CSteamID host)
        { ++NativeCalls; address = Address; port = Port; host = new CSteamID(ServerHost); return ServerKnown; }
        public int GetLobbyDataCount(CSteamID lobby) { ++NativeCalls; return CountOverride ?? Metadata.Count; }
        public string GetLobbyData(CSteamID lobby, string key)
        { ++NativeCalls; ++Reads; ReadKeys.Add(key); OnRead?.Invoke(key); return Metadata.TryGetValue(key, out string value) ? value : ""; }
        internal void Emit(ulong lobby, byte success = 1, ulong? member = null, bool includeDisposed = false)
        {
            foreach (Subscription subscription in Subscriptions.ToArray())
                if (!subscription.Disposed || includeDisposed) subscription.Callback(Data(lobby, success, member));
        }
        internal sealed class Subscription : IDisposable
        {
            private readonly Fake _owner;
            internal readonly Action<LobbyDataUpdate_t> Callback;
            internal bool Disposed;
            internal Subscription(Fake owner, Action<LobbyDataUpdate_t> callback) { _owner = owner; Callback = callback; }
            public void Dispose()
            {
                if (Disposed) return;
                if (_owner.ThrowDispose) throw new InvalidOperationException("Inert disposal failure");
                Disposed = true; --_owner.Active; ++_owner.Disposals;
                Callback(Data(_owner.Lobby, 0)); // Disposal itself may coincide with a queued callback.
            }
        }
    }
}

// Only the shared enum and Steam initialization guard are substituted. The real
// query/codec/models and actual managed Steam structs/method signatures compile.
namespace ServerManager { internal enum OptionalModQueryState { Loading, Available, Unavailable, Unsupported, TooLarge } }
internal static class SteamManager { internal static bool Initialized => false; }
