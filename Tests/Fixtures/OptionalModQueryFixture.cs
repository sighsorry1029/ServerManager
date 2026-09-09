// Compiled together with the production query source by OptionalModQuerySmoke.
// Every external boundary is inert. These stubs intentionally cannot reach Steam,
// DNS, Unity, a configured endpoint, or the running dedicated server.
using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Steamworks
{
    public struct HServerQuery
    {
        public int Value;
        public HServerQuery(int value) { Value = value; }
        public static readonly HServerQuery Invalid = new HServerQuery(-1);
        public static bool operator ==(HServerQuery a, HServerQuery b) => a.Value == b.Value;
        public static bool operator !=(HServerQuery a, HServerQuery b) => a.Value != b.Value;
        public override bool Equals(object value) => value is HServerQuery other && this == other;
        public override int GetHashCode() => Value;
    }
    public sealed class gameserveritem_t { public uint m_nAppID; }
    public sealed class ISteamMatchmakingPingResponse
    {
        public readonly Action<gameserveritem_t> Succeeded;
        public readonly Action Failed;
        public ISteamMatchmakingPingResponse(Action<gameserveritem_t> succeeded, Action failed)
        { Succeeded = succeeded; Failed = failed; }
    }
    public sealed class ISteamMatchmakingRulesResponse
    {
        public readonly Action<string, string> Rule;
        public readonly Action Failed, Complete;
        public ISteamMatchmakingRulesResponse(Action<string, string> rule, Action failed, Action complete)
        { Rule = rule; Failed = failed; Complete = complete; }
    }
    public static class SteamMatchmakingServers
    {
        public static HServerQuery PingServer(uint ip, ushort port, ISteamMatchmakingPingResponse response)
            => throw new InvalidOperationException("Live Steam ping must never be called by a smoke test.");
        public static HServerQuery ServerRules(uint ip, ushort port, ISteamMatchmakingRulesResponse response)
            => throw new InvalidOperationException("Live Steam rules must never be called by a smoke test.");
        public static void CancelServerQuery(HServerQuery handle)
            => throw new InvalidOperationException("Live Steam cancellation must never be called by a smoke test.");
    }
}

public static class SteamManager { public static bool Initialized => false; }

namespace ServerManager
{
    // Endpoint syntax and the real codec have their own production-helper tests.
    // Here their small contracts isolate query ownership and scheduling behavior.
    internal static class ClientMenuBranding
    {
        internal static bool TryParseDedicatedEndpoint(string text, out string host, out ushort port, out string rejection)
        {
            host = text?.Trim() ?? ""; port = 2456; rejection = "";
            if (host.Length == 0 || host.Contains(" ") || host.Contains("://")) return false;
            if (host[0] == '[')
            {
                int end = host.IndexOf(']');
                if (end < 0) return false;
                string suffix = host.Substring(end + 1);
                host = host.Substring(1, end - 1);
                if (suffix.Length > 0 && (!suffix.StartsWith(":") || !ushort.TryParse(suffix.Substring(1), out port))) return false;
            }
            else if (host.IndexOf(':') == host.LastIndexOf(':') && host.IndexOf(':') >= 0)
            {
                int colon = host.IndexOf(':');
                if (!ushort.TryParse(host.Substring(colon + 1), out port)) return false;
                host = host.Substring(0, colon);
            }
            return port != 0 && host.Length != 0;
        }
    }
    internal static class IntegrityCanonical
    { internal static bool IsFatal(Exception error) => error is OutOfMemoryException || error is StackOverflowException; }
    internal enum OptionalModCatalogStatus { Available, Missing, Invalid, TooLarge }
    internal sealed class OptionalModCatalogEntry { internal string Name = "Test optional mod"; }
    internal sealed class OptionalModCatalogResult
    {
        internal OptionalModCatalogStatus Status;
        internal IReadOnlyList<OptionalModCatalogEntry> Entries = Array.Empty<OptionalModCatalogEntry>();
    }
    internal static class OptionalModCatalog
    {
        internal static OptionalModCatalogResult Decode(IReadOnlyDictionary<string, string> rules)
        {
            var result = new OptionalModCatalogResult { Status = OptionalModCatalogStatus.Missing };
            if (!rules.TryGetValue("catalog", out string value)) return result;
            result.Status = value == "valid" || value == "empty" ? OptionalModCatalogStatus.Available :
                value == "large" ? OptionalModCatalogStatus.TooLarge : OptionalModCatalogStatus.Invalid;
            if (value == "valid") result.Entries = new[] { new OptionalModCatalogEntry() };
            return result;
        }
    }

    internal sealed class OptionalQueryFakeTransport : IOptionalModQueryTransport
    {
        internal bool Available = true, ThrowCancel, ThrowPing, InvalidPing, SynchronousPing, SynchronousRules;
        internal int PingCount, RulesCount, CancelCount, ResolveCount, Active, MaximumActive;
        internal uint Address;
        internal ushort Port;
        internal string Host = "";
        internal int NextHandle;
        internal Steamworks.ISteamMatchmakingPingResponse PingCallback;
        internal Steamworks.ISteamMatchmakingRulesResponse RulesCallback;
        internal TaskCompletionSource<IPAddress[]> Dns;
        internal readonly List<string> Events = new List<string>();
        internal Action OnCancel;

        public bool IsAvailable => Available;
        public Task<IPAddress[]> Resolve(string host)
        {
            ++ResolveCount; Host = host;
            return Dns?.Task ?? Task.FromResult(new[] { IPAddress.Parse("192.0.2.41") });
        }
        public Steamworks.HServerQuery Ping(uint address, ushort port, Steamworks.ISteamMatchmakingPingResponse response)
        {
            if (ThrowPing) throw new InvalidOperationException("Inert native failure.");
            ++PingCount; Address = address; Port = port; PingCallback = response;
            Events.Add("ping-start");
            if (SynchronousPing) Success();
            Events.Add("ping-return");
            return InvalidPing ? Steamworks.HServerQuery.Invalid : Allocate();
        }
        public Steamworks.HServerQuery Rules(uint address, ushort port, Steamworks.ISteamMatchmakingRulesResponse response)
        {
            ++RulesCount; Address = address; Port = port; RulesCallback = response;
            Events.Add("rules-start");
            if (SynchronousRules) { response.Rule("catalog", "valid"); response.Complete(); }
            Events.Add("rules-return");
            return Allocate();
        }
        private Steamworks.HServerQuery Allocate()
        {
            ++Active; MaximumActive = Math.Max(MaximumActive, Active);
            return new Steamworks.HServerQuery(++NextHandle);
        }
        public void Cancel(Steamworks.HServerQuery handle)
        {
            if (handle == Steamworks.HServerQuery.Invalid) throw new Exception("Invalid-handle cancellation.");
            if (ThrowCancel) throw new InvalidOperationException("Inert cancellation failure.");
            Events.Add("cancel"); ++CancelCount; --Active;
            if (Active < 0) throw new Exception("Double cancellation.");
            OnCancel?.Invoke();
        }
        internal void Success(uint appId = 892970) => PingCallback.Succeeded(new Steamworks.gameserveritem_t { m_nAppID = appId });
        internal void Complete(string value = "valid") { RulesCallback.Rule("catalog", value); RulesCallback.Complete(); }
    }

    public static class OptionalModQuerySmoke
    {
        private const string Endpoint = "192.0.2.41:2456";
        private static double _now;
        private static int _assertions;
        private static void Check(bool value, string message)
        { ++_assertions; if (!value) throw new Exception(message); }
        private static OptionalModQuery Query(OptionalQueryFakeTransport transport)
        { _now = 0; return new OptionalModQuery(transport, () => _now); }
        private static void BeginRules(OptionalModQuery query, OptionalQueryFakeTransport transport, string endpoint = Endpoint)
        {
            query.Tick(endpoint); transport.Success(); query.Tick(endpoint);
            Check(transport.Active == 1 && transport.RulesCount > 0, "Ping must be cancelled before rules are started.");
        }
        private static object StaticNativeRequest() => typeof(OptionalModQuery).GetField("_nativeRequest", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        public static int Run()
        {
            Check(OptionalModQuery.ToHostOrderIPv4(IPAddress.Loopback) == 0x7f000001U, "IPv4 host order is reversed.");
            Check(OptionalModQuery.ToHostOrderIPv4(IPAddress.Parse("192.0.2.41")) == 0xc0000229U, "IPv4 byte conversion changed.");
            using (var unavailable = new OptionalModQuery())
            {
                unavailable.Tick(Endpoint);
                Check(unavailable.State == OptionalModQueryState.Unsupported, "Uninitialized Steam must be gated internally.");
            }

            var transport = new OptionalQueryFakeTransport();
            using (var query = Query(transport))
            {
                BeginRules(query, transport);
                Check(transport.Address == 0xc0000229U && transport.Port == 2457 && transport.ResolveCount == 0,
                    "Numeric target must use the configured game port plus one without DNS.");
                transport.Complete(); query.Tick(Endpoint);
                Check(query.State == OptionalModQueryState.Available && query.Entries.Count == 1 && !query.IsStale,
                    "Complete valid rules must publish a fresh catalog.");
                long revision = query.DisplayRevision;
                query.Tick(Endpoint); query.Tick("192.0.2.41");
                Check(query.DisplayRevision == revision && transport.PingCount == 1, "Fresh cache should not repaint or re-query each frame.");
                _now = 59; query.Tick(Endpoint);
                Check(transport.PingCount == 1, "Refresh happened before sixty seconds.");
                _now = 60; query.Tick(Endpoint);
                Check(transport.PingCount == 2 && query.IsStale && query.State == OptionalModQueryState.Loading,
                    "Refresh must retain the previous endpoint's catalog explicitly stale.");
                transport.PingCallback.Failed(); query.Tick(Endpoint);
                Check(query.State == OptionalModQueryState.Unavailable && query.Entries.Count == 1 && query.IsStale,
                    "A failed refresh erased the last valid advisory list.");
                _now = 300; query.Tick(Endpoint);
                Check(query.Entries.Count == 0 && !query.IsStale, "Expired cached optional mods were retained indefinitely.");
            }
            Check(transport.Active == 0 && transport.MaximumActive == 1, "Dispose leaked or overlapped native queries.");

            foreach (string value in new[] { "empty", "invalid", "large" })
            {
                transport = new OptionalQueryFakeTransport();
                using var query = Query(transport);
                BeginRules(query, transport); transport.Complete(value); query.Tick(Endpoint);
                var expected = value == "empty" ? OptionalModQueryState.Available :
                    value == "large" ? OptionalModQueryState.TooLarge : OptionalModQueryState.Unavailable;
                Check(query.State == expected && query.Entries.Count == 0 && !query.IsStale, "Empty, invalid and too-large states must remain distinct.");
            }

            transport = new OptionalQueryFakeTransport();
            using (var query = Query(transport))
            {
                query.Tick("[2001:db8::1]:2456");
                Check(query.State == OptionalModQueryState.Unsupported && transport.PingCount == 0, "IPv6 must not enter the IPv4 Steam query API.");
                query.Tick("192.0.2.41:65535");
                Check(query.State == OptionalModQueryState.Unsupported && transport.PingCount == 0, "Query port overflow wrapped to port zero.");
                query.Tick("not an endpoint");
                Check(query.State == OptionalModQueryState.Unsupported && transport.PingCount == 0, "Invalid endpoint reached Steam.");
                query.Tick(Endpoint); transport.Success(480); query.Tick(Endpoint);
                Check(query.State == OptionalModQueryState.Unsupported && transport.RulesCount == 0, "Non-Valheim server reached the rules query.");
            }

            transport = new OptionalQueryFakeTransport { SynchronousPing = true, SynchronousRules = true };
            using (var query = Query(transport))
            {
                query.Tick(Endpoint);
                Check(transport.CancelCount == 0 && transport.RulesCount == 0, "Synchronous ping callback cancelled an unassigned handle.");
                query.Tick(Endpoint);
                Check(transport.CancelCount == 1 && transport.RulesCount == 1 && query.State == OptionalModQueryState.Loading,
                    "Rules completion must be consumed by a later Tick, not its native callback.");
                query.Tick(Endpoint);
                Check(query.State == OptionalModQueryState.Available && transport.CancelCount == 2 && transport.MaximumActive == 1,
                    "Synchronous callback completion lost cancellation or overlapped handles.");
            }

            foreach (string failure in new[] { "duplicate", "key", "value", "count", "total", "missing", "rules-failed" })
            {
                transport = new OptionalQueryFakeTransport();
                using var query = Query(transport);
                BeginRules(query, transport);
                var callback = transport.RulesCallback;
                if (failure == "duplicate") { callback.Rule("catalog", "valid"); callback.Rule("catalog", "valid"); }
                if (failure == "key") callback.Rule(new string('k', OptionalModQuery.MaximumRuleKeyCharacters + 1), "x");
                if (failure == "value") callback.Rule("x", new string('v', OptionalModQuery.MaximumRuleValueCharacters + 1));
                if (failure == "count") for (int i = 0; i <= OptionalModQuery.MaximumRules; ++i) callback.Rule("k" + i, "v");
                if (failure == "total") for (int i = 0; i < 100; ++i) callback.Rule("k" + i, new string('v', OptionalModQuery.MaximumRuleValueCharacters));
                if (failure == "rules-failed") callback.Failed();
                callback.Complete(); query.Tick(Endpoint);
                bool oversized = failure == "key" || failure == "value" || failure == "count" || failure == "total";
                Check(query.State == (oversized ? OptionalModQueryState.TooLarge : failure == "missing" ? OptionalModQueryState.Unsupported : OptionalModQueryState.Unavailable) &&
                    query.Entries.Count == 0 && transport.Active == 0, "Malformed rules escaped the capture boundary: " + failure);
            }

            transport = new OptionalQueryFakeTransport();
            using (var query = Query(transport))
            {
                query.Tick(Endpoint);
                var oldPing = transport.PingCallback;
                query.Pause();
                Check(transport.Active == 0 && query.State == OptionalModQueryState.Unavailable, "Pause must cancel an in-flight query.");
                query.Tick(Endpoint);
                Check(transport.PingCount == 1, "Reopening the menu immediately spammed the same endpoint.");
                query.Tick("192.0.2.42:2456");
                oldPing.Succeeded(new Steamworks.gameserveritem_t { m_nAppID = 892970 });
                query.Tick("192.0.2.42:2456");
                Check(transport.RulesCount == 0 && query.Entries.Count == 0, "A cancelled endpoint callback contaminated its replacement.");
                _now = OptionalModQuery.RequestTimeoutSeconds;
                query.Tick("192.0.2.42:2456");
                Check(query.State == OptionalModQueryState.Unavailable && transport.Active == 0, "A silent ping did not time out.");
                query.Reset(); query.Tick(Endpoint);
                Check(transport.PingCount == 3, "Reset did not clear the retry schedule.");
                query.Dispose(); oldPing.Failed(); query.Tick(Endpoint);
                Check(transport.Active == 0 && query.Entries.Count == 0, "Disposed query restarted or published a late result.");
            }

            transport = new OptionalQueryFakeTransport { Dns = new TaskCompletionSource<IPAddress[]>() };
            using (var query = Query(transport))
            {
                query.Tick("one.example.test");
                Check(transport.ResolveCount == 1 && transport.PingCount == 0, "DNS resolution should precede native queries.");
                query.Pause(); query.Tick("two.example.test"); query.Tick("three.example.test");
                Check(transport.ResolveCount == 1, "Endpoint changes fanned out uncancellable DNS workers.");
                transport.Dns.SetResult(new[] { IPAddress.Parse("192.0.2.1") });
                transport.Dns = new TaskCompletionSource<IPAddress[]>();
                query.Tick("three.example.test");
                Check(transport.ResolveCount == 2 && transport.Host == "three.example.test" && transport.PingCount == 0,
                    "Abandoned DNS result was applied to a different endpoint.");
                transport.Dns.SetResult(new[] { IPAddress.IPv6Loopback, IPAddress.Parse("192.0.2.3") });
                query.Tick("three.example.test");
                Check(transport.Address == 0xc0000203U && transport.PingCount == 1, "Hostname resolution selected the wrong IPv4 result.");
            }

            transport = new OptionalQueryFakeTransport { Dns = new TaskCompletionSource<IPAddress[]>() };
            using (var query = Query(transport))
            {
                query.Tick("slow.example.test"); _now = 15; query.Tick("slow.example.test");
                Check(query.State == OptionalModQueryState.Unavailable && transport.PingCount == 0, "DNS lacked a nonblocking deadline.");
                _now = 75; query.Tick("slow.example.test");
                Check(transport.ResolveCount == 1, "Retry spawned a second DNS worker before the first completed.");
                query.Dispose(); transport.Dns.SetException(new InvalidOperationException("Inert DNS failure after teardown."));
            }

            transport = new OptionalQueryFakeTransport();
            var retired = Query(transport);
            retired.Tick(Endpoint); transport.ThrowCancel = true; retired.Dispose();
            Check(StaticNativeRequest() != null && transport.Active == 1, "Failed cancellation released callback ownership prematurely.");
            transport.ThrowCancel = false;
            using (var replacement = Query(transport))
            {
                _now = 1;
                replacement.Tick("192.0.2.42:2456");
                Check(transport.Active == 1 && transport.CancelCount == 1 && transport.MaximumActive == 1,
                    "Replacement menu failed to retire old callbacks before another native request.");
            }
            Check(StaticNativeRequest() == null, "Successfully cancelled native callbacks remained rooted.");

            foreach (bool invalidHandle in new[] { true, false })
            {
                transport = new OptionalQueryFakeTransport { InvalidPing = invalidHandle, ThrowPing = !invalidHandle };
                using var query = Query(transport);
                query.Tick(Endpoint);
                Check(query.State == OptionalModQueryState.Unavailable && transport.Active == 0 && StaticNativeRequest() == null,
                    "An invalid handle/native exception retained an unowned request.");
            }

            transport = new OptionalQueryFakeTransport();
            using (var query = Query(transport))
            {
                query.Tick(Endpoint);
                var oldPing = transport.PingCallback;
                transport.Success(); query.Tick(Endpoint);
                oldPing.Failed(); oldPing.Succeeded(new Steamworks.gameserveritem_t { m_nAppID = 480 });
                query.Tick(Endpoint);
                Check(query.State == OptionalModQueryState.Loading && transport.Active == 1,
                    "A late ping callback mutated a subsequent rules phase.");
                transport.OnCancel = () => { transport.RulesCallback.Rule("catalog", "invalid"); transport.RulesCallback.Failed(); };
                transport.Complete(); query.Tick(Endpoint);
                Check(query.State == OptionalModQueryState.Available && query.Entries.Count == 1 && transport.Active == 0,
                    "A reentrant cancellation callback overwrote completed rules.");
            }

            transport = new OptionalQueryFakeTransport { Dns = new TaskCompletionSource<IPAddress[]>() };
            using (var query = Query(transport))
            {
                query.Tick("retry.example.test");
                transport.Dns.SetException(new InvalidOperationException("Inert first DNS failure."));
                query.Tick("retry.example.test");
                Check(query.State == OptionalModQueryState.Unavailable && transport.ResolveCount == 1,
                    "A DNS failure did not become a bounded unavailable result.");
                transport.Dns = new TaskCompletionSource<IPAddress[]>();
                transport.Dns.SetResult(new[] { IPAddress.Parse("192.0.2.12") });
                _now = 60; query.Tick("retry.example.test"); query.Tick("retry.example.test");
                Check(transport.ResolveCount == 2 && transport.PingCount == 1 && transport.Address == 0xc000020cU,
                    "A faulted completed DNS task permanently poisoned same-endpoint retries.");
            }

            transport = new OptionalQueryFakeTransport();
            using (var query = Query(transport))
            {
                BeginRules(query, transport); _now = 15; query.Tick(Endpoint);
                Check(query.State == OptionalModQueryState.Unavailable && transport.Active == 0,
                    "Silent rules did not share the bounded request deadline.");
            }

            transport = new OptionalQueryFakeTransport { Dns = new TaskCompletionSource<IPAddress[]>() };
            using (var query = Query(transport)) query.Tick("retired.example.test");
            var nextTransport = new OptionalQueryFakeTransport();
            using (var query = Query(nextTransport))
            {
                query.Tick("replacement.example.test");
                Check(nextTransport.ResolveCount == 0, "Menu recreation fanned out another uncancellable DNS worker.");
                transport.Dns.SetResult(new[] { IPAddress.Parse("192.0.2.9") });
                query.Tick("replacement.example.test"); query.Tick("replacement.example.test");
                Check(nextTransport.ResolveCount == 1 && nextTransport.Address == 0xc0000229U,
                    "Replacement menu used retired-owner DNS data for a different host.");
            }

            transport = new OptionalQueryFakeTransport();
            using (var query = Query(transport))
            {
                for (int i = 1; i <= 12; ++i)
                {
                    string endpoint = "192.0.2." + i + ":2456";
                    BeginRules(query, transport, endpoint); transport.Complete(); query.Tick(endpoint);
                }
                var cache = (System.Collections.IDictionary)typeof(OptionalModQuery).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(query);
                Check(cache.Count == OptionalModQuery.MaximumCachedEndpoints, "Per-endpoint cache grew beyond its RAM bound.");
                query.Tick("192.0.2.1:2456");
                Check(query.Entries.Count == 0 && query.State == OptionalModQueryState.Loading, "Evicted or foreign endpoint data leaked into a new request.");
                Exception crossThread = null;
                var thread = new Thread(() => { try { query.Tick(Endpoint); } catch (Exception error) { crossThread = error; } });
                thread.Start(); thread.Join();
                Check(crossThread is InvalidOperationException, "Query calls were allowed off the owning UI thread.");
            }
            return _assertions;
        }
    }
}
