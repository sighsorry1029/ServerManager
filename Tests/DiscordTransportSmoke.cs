// Offline, isolated contract tests. Compile with Discord/DiscordHttp.cs and
// Discord settings/webhooks and actual localizer/resources; no game/network needed.
// Only game, Harmony, logger and event DTO boundaries are inert.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ServerManager;
using ServerManager.Discord;
using ServerManager.Events;

internal static class DiscordTransportSmoke
{
    private const string Rest = "https://discord.com/api/v10/gateway/bot";
    private const string Webhook = "https://discord.com/api/webhooks/123456/fake_webhook_secret";
    private const string ReloadedWebhook = "https://discord.com/api/webhooks/222222/fake_reloaded_secret";
    private static readonly string[] OperatorKinds =
    {
        "security.detection", "security.response", "security.admin_bypass", "character.save_rejected",
        "character.shadow_stalled", "character.validation_observed", "character.revision_observed", "connection.rejected"
    };
    private static readonly string[] SourceEventKinds = new[]
    {
        "server.ready", "server.saved", "server.shutdown", "server.announcement",
        "player.login", "player.leave", "chat.shout", "raid.started", "raid.ended",
        "player.death", "combat.pvp_kill", "boss.killed", "moderation.action", "command.executed"
    }.Concat(OperatorKinds).ToArray();
    private static int _assertions;

    private static int Main()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("Discord HTTP/webhook smoke passed: " + _assertions + " assertions, zero network requests.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        await AuthenticationAndEndpoints();
        await HttpFailuresAndBounds();
        await RateLimits();
        await RoutesPrivacyAndDedupe();
        await GroupedWebhookFilters();
        await GroupedLifecycleFilters();
        await SteamIdRoutePrivacy();
        await SteamIdTrustedSourceAndBounds();
        await SteamIdSnapshotAndReload();
        await CompactEventCards();
        await CompactCronCards();
        await EnvironmentalDeathCards();
        await LocalizedStoryRoutes();
        await LocalizedPublicFrames();
        await AnonymousStoryIdentityBoundaries();
        await LongRaidTitles();
        await ExternalRaidHeadingBounds();
        await DistributedTranslationLayers();
        await LongSharedStoryCards();
        await CompactTextBoundsAndOutcomes();
        await PartialCharacterSaveWarnings();
        await PlainShoutContent();
        await DiscordShoutRoutes();
        await AnonymousRouteProjection();
        await SimplifiedCommandPrivacy();
        await AnonymousAliasLifetimeAndBounds();
        await AnonymousSafeSummaryAndCapacity();
        await RaidCoordinatePrivacy();
        await OperatorSummariesAndLimits();
        await ExplicitWebhookTestRouting();
        await QueueAndStop();
        await ReloadRoutesAndQueue();
        await InvalidReloadPreservesState();
        await ReloadCancelsInFlightAndLaneWait();
        await ReloadCancelsBackoffAndClearsDisabled();
        await ReloadConcurrencyAndDisposal();
    }

    private static async Task AuthenticationAndEndpoints()
    {
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("fake_bot_secret", delegate { }, handler))
        {
            foreach (string endpoint in new[]
            {
                "/api/v10/gateway", "http://discord.com/api/v10/gateway", "https://example.com/api/v10/gateway",
                "https://discord.com.attacker.test/api/gateway", "https://discord.com:444/api/gateway",
                "https://name:fake_secret@discord.com/api/gateway", "https://discord.com/api/gateway#fake_secret",
                "https://discord.com/not-api/gateway", "https://discord.com/api/v10/webhooks%2f1/fake_secret"
            })
            {
                DiscordHttpException rejected;
                try { rejected = await Failure(() => http.SendAsync(HttpMethod.Get, endpoint, null, true, CancellationToken.None)); }
                catch (Exception exception) { throw new Exception("Invalid test endpoint was not rejected: " + endpoint, exception); }
                Check(!rejected.Message.Contains("fake_secret") && !rejected.Message.Contains(endpoint), "Endpoint rejection redacts input.");
            }
            Check(handler.Requests.Count == 0, "Invalid endpoints never reach the handler.");
            await http.SendAsync(HttpMethod.Get, Rest, null, true, CancellationToken.None);
            await http.SendAsync(HttpMethod.Post, Webhook, new JObject(), true, CancellationToken.None);
            await http.SendAsync(HttpMethod.Post, "https://discord.com/api/v10/interactions/123/fake_secret/callback",
                new JObject(), true, CancellationToken.None);
            await http.SendAsync(HttpMethod.Post, "https://discord.com/api/v10/interactions/123/fake%2Fsecret%3D/callback",
                new JObject(), true, CancellationToken.None);
            Check(handler.Requests[0].Auth == "Bot fake_bot_secret", "Bot auth is used for REST.");
            Check(handler.Requests[1].Auth == null && handler.Requests[2].Auth == null, "Webhook/callback never receive bot token.");
            Check(handler.Requests[3].Auth == null && handler.Requests[3].Url.Contains("%3D"), "Opaque interaction tokens may be safely URI-escaped.");
            Check(handler.Requests[1].Body == "{}", "POST serializes JSON.");
        }
        using (HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = true })
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        {
            Check(!handler.AllowAutoRedirect && !handler.UseCookies, "Production redirects and cookies are disabled.");
        }
    }

    private static async Task HttpFailuresAndBounds()
    {
        foreach (int status in new[] { 301, 400, 401, 403, 404, 500 })
        {
            using (FakeHandler handler = new FakeHandler((_, __) => Response(status, "{\"message\":\"fake_webhook_secret\"}")))
            using (DiscordHttp http = new DiscordHttp("fake_bot_secret", delegate { }, handler))
            {
                DiscordHttpException exception = await Failure(() => http.SendAsync(HttpMethod.Post, Webhook,
                    new JObject(), false, CancellationToken.None, retry: false));
                Check(exception.StatusCode == status && !exception.ToString().Contains("fake_webhook_secret"), "Failure reports only status.");
                Check(handler.Requests.Count == 1, "Ambiguous mutations and redirects never replay.");
            }
        }
        foreach (Exception injected in new Exception[]
        {
            new HttpRequestException("fake_webhook_secret"), new TaskCanceledException("fake_webhook_secret"),
            new IOException("fake_bot_secret")
        })
        {
            using (FakeHandler handler = new FakeHandler((_, __) => throw injected))
            using (DiscordHttp http = new DiscordHttp("fake_bot_secret", delegate { }, handler))
            {
                DiscordHttpException exception = await Failure(() => http.SendAsync(HttpMethod.Post, Webhook,
                    new JObject(), false, CancellationToken.None, retry: false));
                Check(!exception.ToString().Contains("fake_") && exception.InnerException == null, "Network exceptions are sanitized.");
                Check(handler.Requests.Count == 1, "Network failure does not replay unsafe mutation.");
            }
        }
        foreach (string body in new[] { new string('x', 1024 * 1024 + 1),
                     new string('[', 40) + "0" + new string(']', 40), "{}{}" })
        {
            using (FakeHandler handler = new FakeHandler((_, __) => Response(200, body)))
            using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
            {
                await Failure(() => http.SendAsync(HttpMethod.Get, Rest, null, false, CancellationToken.None));
                Check(handler.Requests.Count == 1, "Size/depth/JSON bounds fail without retry.");
            }
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        {
            await Failure(() => http.SendAsync(HttpMethod.Post, Webhook,
                new JObject { ["x"] = new string('x', 256 * 1024) }, false, CancellationToken.None));
            Check(handler.Requests.Count == 0, "Oversized outbound JSON never reaches HTTP.");
            JToken nested = new JValue(0);
            for (int i = 0; i < 40; i++) nested = new JArray(nested);
            await Failure(() => http.SendAsync(HttpMethod.Post, Webhook, nested, false, CancellationToken.None));
            Check(handler.Requests.Count == 0, "Excessively nested outbound JSON never reaches HTTP.");
        }
        using (IgnoreCancellationHandler handler = new IgnoreCancellationHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (CancellationTokenSource stop = new CancellationTokenSource(50))
        {
            Stopwatch watch = Stopwatch.StartNew();
            bool cancelled = false;
            try { await http.SendAsync(HttpMethod.Post, Webhook, new JObject(), false, stop.Token, retry: false); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && watch.Elapsed < TimeSpan.FromSeconds(1), "Cancellation cannot be held hostage by a Mono handler ignoring it.");
            handler.Completion.TrySetResult(Response(204));
        }
    }

    private static async Task RateLimits()
    {
        using (FakeHandler handler = new FakeHandler((number, _) => number == 1
                   ? Response(429, "{\"retry_after\":0.07,\"global\":false}", "0.05") : Response(204)))
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        {
            await http.SendAsync(HttpMethod.Post, Webhook, new JObject(), false, CancellationToken.None, retry: false);
            Check(handler.Requests.Count == 2, "Explicit 429 is safe to retry for a mutation.");
            Check((handler.Requests[1].At - handler.Requests[0].At).TotalMilliseconds >= 60,
                "Fractional JSON Retry-After is honored, not rounded down.");
        }
        using (FakeHandler handler = new FakeHandler((_, __) => Response(429, "{\"retry_after\":0.001}")))
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        {
            DiscordHttpException failure = await Failure(() => http.SendAsync(HttpMethod.Post, Webhook,
                new JObject(), false, CancellationToken.None, retry: false));
            Check(failure.StatusCode == 429 && handler.Requests.Count == 6, "Five-retry ceiling is enforced.");
        }
        using (FakeHandler handler = new FakeHandler((number, _) => number == 1
                   ? Response(429, "{\"retry_after\":0.12,\"global\":true}") : Response(204)))
        using (DiscordHttp http = new DiscordHttp("fake_bot_secret", delegate { }, handler))
        {
            Task first = http.SendAsync(HttpMethod.Get, Rest, null, true, CancellationToken.None);
            await WaitFor(() => handler.Requests.Count >= 1);
            await Task.Delay(10);
            Task second = http.SendAsync(HttpMethod.Get, "https://discord.com/api/v10/users/@me", null, true, CancellationToken.None);
            Task callback = http.SendAsync(HttpMethod.Post, "https://discord.com/api/v10/interactions/1/fake_secret/callback",
                new JObject(), false, CancellationToken.None, retry: false);
            await Task.WhenAll(first, second, callback);
            RequestRecord[] calls = handler.Requests.ToArray();
            Check(calls[1].Url.Contains("interactions"), "Unauthenticated callback can pass a bot-global cooldown.");
            Check(calls.Where(call => call.Auth != null).Skip(1).All(call =>
                    (call.At - calls[0].At).TotalMilliseconds >= 110), "Bot-global cooldown applies to other authenticated routes.");
        }
        using (FakeHandler handler = new FakeHandler((_, __) => Response(429, "{\"retry_after\":86401}")))
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (CancellationTokenSource stop = new CancellationTokenSource(60))
        {
            bool cancelled = false;
            try { await http.SendAsync(HttpMethod.Post, Webhook, new JObject(), false, stop.Token, false); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && handler.Requests.Count == 1, "Long rate-limit cooldown is cancellable without early retry.");
        }
    }

    private static async Task RoutesPrivacyAndDedupe()
    {
        DiscordSettings settings = Settings("server.saved", "chat.normal", "chat.whisper", "chat.clan",
            "server.announcement", "moderation.action", "command.executed", "*");
        settings.WebhookRoutes.Add(new DiscordWebhookRoute { Name = "Exact-only", Url = Webhook,
            Events = new HashSet<string> { "server.*", "*" } });
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
        {
            foreach (string kind in new[] { "chat.normal", "chat.whisper", "chat.clan", "chat.shout", "server.saved.extra", "unknown" })
                Check(!webhooks.Enqueue(Event(kind)), "Private, unselected, and unknown events cannot route: " + kind);
            ServerManagerEvent value = Event("server.saved");
            value.Fields["title"] = "**Saved** @everyone";
            value.Fields["message"] = "@everyone <@12345> " + new string('m', 6000);
            value.Fields["Coordinates"] = "100,200,300";
            value.Fields["world_position"] = "400,500,600";
            value.Fields["inventory_items"] = "SECRET-INVENTORY";
            value.Fields["PluginList"] = "VISIBLE-PLUGINS";
            value.Fields["BotToken"] = "SECRET-TOKEN";
            for (int i = 0; i < 30; i++) value.Fields["field" + i] = new string('f', 700);
            Check(webhooks.Enqueue(value), "Public event is enqueued.");
            Check(!webhooks.Enqueue(value), "Duplicate event ID is rejected.");
            foreach (string kind in new[] { "server.announcement", "moderation.action", "command.executed" })
                Check(webhooks.Enqueue(Event(kind)), "Public moderation/command event is allowed.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(handler.Requests.Count == 4, "Only exact named routes send and each event sends once.");
            JObject payload = JObject.Parse(handler.Requests[0].Body!);
            JObject embed = (JObject)payload["embeds"]![0]!;
            string serialized = payload.ToString();
            Check(((JArray)payload["allowed_mentions"]!["parse"]!).Count == 0, "Mention parsing is disabled.");
            Check(!serialized.Contains("@everyone") && !serialized.Contains("<@12345>"), "User-controlled mention syntax is neutralized.");
            Check(!serialized.Contains("SECRET-") && !serialized.Contains("100,200,300") && !serialized.Contains("400,500,600"),
                "Coordinates, inventory and credentials are always excluded.");
            Check(!serialized.Contains("VISIBLE-PLUGINS"), "Lifecycle cards do not spill arbitrary plugin or diagnostic metadata.");
            CheckCompactEmbed(embed, "World saved", null, "world save ignores raw titles and diagnostic fields");
            Check(handler.Requests.All(call => call.Auth == null && call.Url.EndsWith("?wait=true")), "Webhook-only requests use wait=true without bot auth.");
            Check(!webhooks.Enqueue(Event("server.saved")), "Stopping rejects new work.");
        }
        settings = Settings("chat.shout");
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
        {
            ServerManagerEvent value = Event("chat.shout");
            value.Fields["coordinates"] = "100,200,300";
            value.Fields["inventory"] = "WOOD";
            value.Fields["plugins"] = "PLUGIN";
            value.Fields["secret"] = "NEVER";
            Check(webhooks.Enqueue(value), "Selecting chat.shout alone enables shout delivery.");
            Check(!webhooks.Enqueue(Event("server.saved")), "A shout-only route still rejects unselected event kinds.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            string payload = handler.Requests[0].Body!;
            Check(!payload.Contains("100,200,300") && !payload.Contains("WOOD") && !payload.Contains("PLUGIN")
                && !payload.Contains("NEVER"), "Plain shout delivery excludes all event metadata, including plugins, coordinates, inventory and credentials.");
            CheckPlainShoutPayload(JObject.Parse(payload), "test", "selected shout");
        }
    }

    private static async Task GroupedWebhookFilters()
    {
        string[] retiredSelectors = { "combat.pvp_kill", "character.save_rejected", "character.validation_observed",
            "security.detection", "security.response", "server.ready", "server.shutdown", "player.login", "player.leave",
            "raid.started", "raid.ended" };
        Check(DiscordSettings.PublicEvents.Count == 18 && DiscordSettings.PublicEvents.SetEquals(
                SourceEventKinds.Select(ExpectedWebhookFilter).Concat(new[] { "cron.executed", "discord.shout" })),
            "Eighteen selectable filters cover game, Discord and final cron events.");
        foreach (string kind in SourceEventKinds)
            Check(DiscordSettings.GetWebhookEventFilter(kind) == ExpectedWebhookFilter(kind), "Exact source-to-filter mapping: " + kind);
        foreach (string kind in new[] { "character.validation", "security.alert", "server.status", "player.connection", "raid.status", "cron.executed",
            "player.death.extra", "security.*", "chat.normal", "unknown" })
            Check(DiscordSettings.GetWebhookEventFilter(kind) == null, "Group selectors and unknown kinds are not source events: " + kind);

        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("player.death"), http, delegate { }))
        {
            foreach (string selector in retiredSelectors)
            {
                DiscordSettings invalid = Settings("server.ready");
                invalid.WebhookRoutes[0].Events = new HashSet<string> { selector }; // Deliberately bypass fixture mapping.
                bool rejected = false;
                try { webhooks.Reload(invalid); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Strict dispatcher reload rejects retired selector: " + selector);
                rejected = false;
                try { DiscordSettings.ParseForReload("webhooks:\n  - name: retired\n    url: '" + Webhook + "'\n    events: ['" + selector + "']\n"); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Strict YAML reload rejects retired selector: " + selector);
            }
            Check(webhooks.Enqueue(Event("player.death")) && webhooks.Enqueue(Event("combat.pvp_kill")) &&
                !webhooks.Enqueue(Event("server.ready")), "Rejected selector edits keep the existing grouped route active.");
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == 2, "Normal and PvP deaths survive failed configuration changes without extra deliveries.");
        }

        foreach (string language in new[] { "English", "Korean" })
        foreach (bool anonymous in new[] { false, true })
        {
            const string characterUrl = "https://discord.com/api/webhooks/333333/offline-group-character";
            string prefix = anonymous ? "Guest" : string.Empty;
            DiscordSettings settings = new DiscordSettings { WebhookRoutes = new List<DiscordWebhookRoute>
            {
                new DiscordWebhookRoute { Name = "Deaths", Url = Webhook, Language = language, AnonymousPrefix = prefix,
                    Events = new HashSet<string> { "player.death" } },
                new DiscordWebhookRoute { Name = "Security", Url = ReloadedWebhook, Language = language, AnonymousPrefix = prefix,
                    Events = new HashSet<string> { "security.alert" } },
                new DiscordWebhookRoute { Name = "Character", Url = characterUrl, Language = language, AnonymousPrefix = prefix,
                    Events = new HashSet<string> { "character.validation" } }
            } };
            ServerManagerActor victim = AnonymousActor("steamworks:76561198000000001", "PRIVATE_VICTIM_ID", "Victim");
            ServerManagerEvent death = Event("player.death"); death.Actor = victim; death.Fields["cause"] = "fall";
            death.Reliability = "client_reported";
            ServerManagerEvent pvp = Event("combat.pvp_kill"); pvp.Actor = victim; pvp.Fields["cause"] = "playerhit";
            pvp.Target = AnonymousActor("steamworks:76561198000000002", "PRIVATE_ATTACKER_ID", "Attacker");
            pvp.Reliability = "client_reported";
            ServerManagerEvent detection = OperatorEvent("security.detection"); detection.Actor = victim;
            detection.Fields["response"] = "Ban"; detection.Fields["outcome"] = "banlist_add_returned";
            ServerManagerEvent response = OperatorEvent("security.response"); response.Actor = victim;
            response.Fields["response"] = "Ban"; response.Fields["outcome"] = "banlist_add_returned";
            ServerManagerEvent failed = OperatorEvent("security.response"); failed.Actor = victim;
            failed.Fields["response"] = "Ban"; failed.Fields["outcome"] = "banlist_add_failed";
            ServerManagerEvent observed = OperatorEvent("character.validation_observed"); observed.Actor = victim;
            ServerManagerEvent rejected = OperatorEvent("character.save_rejected"); rejected.Actor = victim;
            ServerManagerEvent[] events = { death, pvp, detection, response, failed, observed, rejected };
            using (FakeHandler handler = new FakeHandler())
            using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
            using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
            {
                foreach (string kind in new[] { "character.validation", "security.alert", "boss.killed", "security.admin_bypass", "character.shadow_stalled" })
                    Check(!webhooks.Enqueue(Event(kind)), "Grouping does not inject synthetic events or select adjacent events: " + kind);
                foreach (ServerManagerEvent value in events)
                {
                    string original = EventFingerprint(value);
                    Check(webhooks.Enqueue(value) && EventFingerprint(value) == original,
                        "Grouping preserves the original event and independently admits its kind/outcome: " + value.Kind);
                    Check(!webhooks.Enqueue(value), "Grouping retains event-ID duplicate suppression: " + value.Kind);
                }
                ServerManagerEvent repeatedResponse = OperatorEvent("security.response"); repeatedResponse.Actor = victim;
                repeatedResponse.Fields["response"] = "Ban"; repeatedResponse.Fields["outcome"] = "banlist_add_returned";
                Check(!webhooks.Enqueue(repeatedResponse), "New IDs still cannot repeat the same security kind/account/reason/outcome.");
                ServerManagerEvent sharedId = Event("combat.pvp_kill"); sharedId.EventId = death.EventId;
                Check(!webhooks.Enqueue(sharedId), "Changing source kind under one event ID cannot bypass duplicate suppression.");
                Task run = webhooks.RunAsync(CancellationToken.None);
                await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
                Check(handler.Requests.Count == events.Length, "Each source event reaches exactly one matching grouped route.");
                Check(handler.Requests.Count(request => request.Url.StartsWith(Webhook, StringComparison.Ordinal)) == 2 &&
                    handler.Requests.Count(request => request.Url.StartsWith(ReloadedWebhook, StringComparison.Ordinal)) == 3 &&
                    handler.Requests.Count(request => request.Url.StartsWith(characterUrl, StringComparison.Ordinal)) == 2,
                    "Death, security and character group routes stay isolated.");
                for (int index = 0; index < events.Length; ++index)
                {
                    JObject payload = JObject.Parse(handler.Requests[index].Body!);
                    JObject card = (JObject)payload["embeds"]![0]!;
                    if (index < 2)
                        CheckCompactEmbed(card, Story(events[index], anonymous ? "Guest 1" : "Victim",
                            index == 1 ? anonymous ? "Guest 2" : "Attacker" : string.Empty, language),
                            PlayerLocalizer.TextForLanguage(language, "sm_event_client_report"),
                            "Grouped death keeps the original language, variant, victim and PvP attacker");
                    if (index == 2) Check(((string)card["title"]!).StartsWith("Security observation", StringComparison.Ordinal), "Detection keeps its observation card.");
                    if (index == 3) Check(((string)card["description"]!).Contains("Ban-list update call completed"), "Response success remains visible after detection.");
                    if (index == 4) Check(((string)card["description"]!).Contains("Ban list update failed"), "Distinct response failure remains visible after success.");
                    if (index == 5) Check(((string)card["description"]!).Contains("observe only"), "Observed character data is not presented as rejected.");
                    if (index == 6) Check(((string)card["title"]!).StartsWith("Character save rejected", StringComparison.Ordinal) &&
                        !((string)card["description"]!).Contains("observe only"), "Save rejection remains distinct from its grouped observation.");
                    if (anonymous) Check(!payload.ToString().Contains("Victim") && !payload.ToString().Contains("Attacker") &&
                        !payload.ToString().Contains("76561198") && !payload.ToString().Contains("PRIVATE"), "Grouped anonymous cards do not leak either player identity.");
                }
            }
        }
    }

    private static async Task GroupedLifecycleFilters()
    {
        const string raidUrl = "https://discord.com/api/webhooks/333333/offline-group-raid";
        DiscordSettings settings = new DiscordSettings { WebhookRoutes = new List<DiscordWebhookRoute>
        {
            new DiscordWebhookRoute { Name = "Server status", Url = Webhook, Events = new HashSet<string> { "server.status" } },
            new DiscordWebhookRoute { Name = "Connections", Url = ReloadedWebhook, Events = new HashSet<string> { "player.connection" } },
            new DiscordWebhookRoute { Name = "Raids", Url = raidUrl, Events = new HashSet<string> { "raid.status" } }
        } };
        string[] kinds = { "server.ready", "server.shutdown", "player.login", "player.login", "player.leave", "raid.started", "raid.ended" };
        string[] titles = { "Server ready", "Server shutdown", "Complete joined", "Complete joined for the first time", "Complete left",
            "Raid started — army\\_bonemass \\[-371, 40, 1591\\]", "Raid ended — army\\_bonemass \\[-371, 40, 1591\\]" };
        int[] colors = { 0x57F287, 0x747F8D, 0x5865F2, 0x5865F2, 0x5865F2, 0xEB459E, 0xEB459E };
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
        {
            foreach (string kind in SourceEventKinds.Except(kinds).Concat(new[] { "server.status", "player.connection", "raid.status",
                "server.status.extra", "player.connection.extra", "raid.status.extra", "server.*", "player.*", "raid.*" }))
                Check(!webhooks.Enqueue(Event(kind)), "New lifecycle groups neither select neighbors nor admit synthetic sources: " + kind);
            for (int index = 0; index < kinds.Length; ++index)
            {
                ServerManagerEvent value = SensitiveEvent(kinds[index]);
                value.Actor = new ServerManagerActor("PRIVATE_ACTOR_ID", "Complete", "player");
                value.Fields["first_join"] = index == 3 ? "true" : "false";
                value.Fields["raid"] = "army_bonemass";
                value.Fields["raid_coordinates"] = "-371, 40, 1591";
                string original = EventFingerprint(value);
                Check(webhooks.Enqueue(value) && original == EventFingerprint(value), "Grouping keeps the source event unchanged: " + kinds[index]);
                Check(!webhooks.Enqueue(value), "Grouping does not bypass per-event deduplication: " + kinds[index]);
                ServerManagerEvent sameId = Event(kinds[index] == "server.ready" ? "server.shutdown" : "server.ready");
                sameId.EventId = value.EventId;
                Check(!webhooks.Enqueue(sameId), "Changing a raw lifecycle kind cannot replay a consumed event ID.");
            }
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == kinds.Length, "Ready/shutdown, normal/first joins, leave and both raid outcomes remain independent deliveries.");
            for (int index = 0; index < kinds.Length; ++index)
            {
                RequestRecord request = handler.Requests[index];
                string destination = index < 2 ? Webhook : index < 5 ? ReloadedWebhook : raidUrl;
                JObject payload = JObject.Parse(request.Body!);
                JObject card = (JObject)payload["embeds"]![0]!;
                Check(request.Url == destination + "?wait=true", "Each grouped lifecycle outcome reaches only its isolated route.");
                CheckCompactEmbed(card, titles[index], null, "grouped lifecycle source " + kinds[index]);
                Check((int?)card["color"] == colors[index], "Grouped selector preserves its raw event color: " + kinds[index]);
                Check(!payload.ToString().Contains("PRIVATE") && !payload.ToString().Contains("100,200,300") &&
                    !payload.ToString().Contains("Steam ID:"), "Grouped lifecycle cards retain fixed metadata exclusions and default identity privacy.");
            }
        }
    }

    private static async Task SteamIdRoutePrivacy()
    {
        const string anonymousUrl = "https://discord.com/api/webhooks/333333/offline-steam-anonymous";
        foreach (bool optedInFirst in new[] { false, true })
        {
            DiscordSettings settings = Settings(SourceEventKinds);
            Check(!settings.WebhookRoutes[0].IncludeSteamId, "A programmatically created route defaults to hiding Steam IDs.");
            DiscordWebhookRoute optedIn = new DiscordWebhookRoute { Name = "Identified", Url = ReloadedWebhook,
                Username = "ServerManager", IncludeSteamId = true, Events = new HashSet<string>(SourceEventKinds.Select(ExpectedWebhookFilter)) };
            if (optedInFirst) settings.WebhookRoutes.Insert(0, optedIn); else settings.WebhookRoutes.Add(optedIn);
            settings.WebhookRoutes.Add(new DiscordWebhookRoute { Name = "Anonymous", Url = anonymousUrl,
                Username = "ServerManager", AnonymousPrefix = "Guest", IncludeSteamId = true,
                Events = new HashSet<string>(SourceEventKinds.Select(ExpectedWebhookFilter)) });
            var events = SourceEventKinds.Select(kind => SteamIdentityEvent(kind, "steamworks:76561198000000001")).ToList();
            ServerManagerEvent firstJoin = SteamIdentityEvent("player.login", "steamworks:76561198000000001");
            firstJoin.Fields["first_join"] = "true"; events.Add(firstJoin);
            events.Add(SteamIdentityEvent("player.leave", "steamworks:76561198000000001", "**Complete** @everyone <@12345>\n\0" + new string('*', 4000)));
            using (FakeHandler handler = new FakeHandler())
            using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
            using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
            {
                foreach (ServerManagerEvent value in events)
                {
                    string original = EventFingerprint(value);
                    Check(webhooks.Enqueue(value) && EventFingerprint(value) == original, "Route-local Steam-ID projection never mutates the source: " + value.Kind);
                    Check(!webhooks.Enqueue(value), "Steam-ID opt-in adds no duplicate delivery: " + value.Kind);
                }
                Task run = webhooks.RunAsync(CancellationToken.None);
                await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
                RequestRecord[] hidden = handler.Requests.Where(request => request.Url.StartsWith(Webhook, StringComparison.Ordinal)).ToArray();
                RequestRecord[] shown = handler.Requests.Where(request => request.Url.StartsWith(ReloadedWebhook, StringComparison.Ordinal)).ToArray();
                RequestRecord[] anonymous = handler.Requests.Where(request => request.Url.StartsWith(anonymousUrl, StringComparison.Ordinal)).ToArray();
                Check(hidden.Length == events.Count && shown.Length == events.Count && anonymous.Length == events.Count,
                    "Every raw source still reaches each matching route once, regardless of identity policy or route order.");
                for (int index = 0; index < events.Count; ++index)
                {
                    bool connection = events[index].Kind == "player.login" || events[index].Kind == "player.leave";
                    JObject off = JObject.Parse(hidden[index].Body!);
                    JObject on = JObject.Parse(shown[index].Body!);
                    JObject anon = JObject.Parse(anonymous[index].Body!);
                    if (connection)
                    {
                        JObject offCard = (JObject)off["embeds"]![0]!;
                        JObject onCard = (JObject)on["embeds"]![0]!;
                        CheckCompactEmbed(onCard, (string?)offCard["title"], "Steam ID: 76561198000000001", "opted-in connection " + events[index].Kind);
                        JObject withoutIdentity = (JObject)on.DeepClone();
                        ((JObject)withoutIdentity["embeds"]![0]!).Remove("description");
                        Check(JToken.DeepEquals(off, withoutIdentity), "The sole normal-route payload change is the dedicated Steam-ID description line.");
                        Check((int?)onCard["color"] == 0x5865F2 && !((string)onCard["title"]!).Contains("\n"),
                            "Steam-ID detail leaves the sanitized compact title and connection color intact.");
                        CheckCompactEmbed((JObject)anon["embeds"]![0]!, index == events.Count - 2 ? "Guest 1 joined for the first time" :
                            events[index].Kind == "player.login" ? "Guest 1 joined" : "Guest 1 left", null,
                            "anonymous prefix overrides explicit Steam-ID opt-in");
                    }
                    else Check(JToken.DeepEquals(off, on), "Steam-ID opt-in has no effect on a non-connection source: " + events[index].Kind);
                    foreach (JObject payload in new[] { off, on, anon })
                    {
                        Check(((JArray)payload["allowed_mentions"]!["parse"]!).Count == 0 &&
                            ((JArray)payload["allowed_mentions"]!["users"]!).Count == 0 &&
                            ((JArray)payload["allowed_mentions"]!["roles"]!).Count == 0 &&
                            (bool?)payload["allowed_mentions"]!["replied_user"] == false,
                            "Steam-ID policy preserves every allowed-mentions restriction.");
                        Check(!payload.ToString().Contains("PRIVATE") && !payload.ToString().Contains("76561198000000002") &&
                            !payload.ToString().Contains("100,200,300") && !payload.ToString().Contains("steamworks:") &&
                            !payload.ToString().Contains("@everyone") && !payload.ToString().Contains("<@12345>"),
                            "The trusted ID exception never opens other account, credential, coordinate, inventory or mention fields: " + events[index].Kind);
                    }
                    Check(!off.ToString().Contains("Steam ID:") && !off.ToString().Contains("76561198000000001") &&
                        !anon.ToString().Contains("Steam ID:") && !anon.ToString().Contains("76561198000000001") &&
                        !anon.ToString().Contains("Complete"), "Default-off and anonymous routes never inherit another route's ID or real player name.");
                }
                Check(handler.Requests.All(request => request.Auth == null), "Identity-bearing cards never receive bot authorization headers.");
            }
        }
    }

    private static async Task SteamIdTrustedSourceAndBounds()
    {
        string[] valid = { "steamworks:76561197960265729", "steamworks:76561198000000001", "steamworks:76561202255233023" };
        string?[] invalid = { null, "", "76561198000000001", "Steamworks:76561198000000001", "STEAMWORKS:76561198000000001",
            " steamworks:76561198000000001", "steamworks:76561198000000001 ", "steamworks:\t76561198000000001",
            "steamworks:+76561198000000001", "steamworks:-76561198000000001", "steamworks:076561198000000001",
            "steamworks:7656119800000000", "steamworks:765611980000000011", "steamworks:76561198000000001x",
            "steamworks:７６５６１１９８００００００００１", "steamworks:76561197960265728", "steamworks:76561202255233024",
            "steamworks:0", "steamworks:99999999999999999", "steamworks:18446744073709551615",
            "steamworks:18446744073709551616", "steamworks:76561198000000001\n", "steamworks:76561198000000001\0",
            "xbox:76561198000000001", "playfab:76561198000000001", "steamworks:STEAM_0:1:123", "steamworks:[U:1:123]" };
        DiscordSettings settings = Settings("player.connection"); settings.WebhookRoutes[0].IncludeSteamId = true;
        var cases = new List<Tuple<ServerManagerEvent, string?>>();
        foreach (string kind in new[] { "player.login", "player.leave" })
        {
            foreach (string account in valid)
                cases.Add(Tuple.Create(SteamIdentityEvent(kind, account), (string?)("Steam ID: " + account.Substring("steamworks:".Length))));
            foreach (string? account in invalid)
                cases.Add(Tuple.Create(SteamIdentityEvent(kind, account!), (string?)null));
            ServerManagerEvent noActor = SteamIdentityEvent(kind, "steamworks:76561198000000001");
            noActor.Actor = null; cases.Add(Tuple.Create(noActor, (string?)null));
            ServerManagerEvent forgedName = SteamIdentityEvent(kind, "", "76561198000000001");
            cases.Add(Tuple.Create(forgedName, (string?)null));
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
        {
            foreach (var test in cases) Check(webhooks.Enqueue(test.Item1), "Missing or invalid trusted identity never drops a connection event.");
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == cases.Count, "All canonical-boundary, malformed and forged-source cases send once.");
            for (int index = 0; index < cases.Count; ++index)
            {
                JObject payload = JObject.Parse(handler.Requests[index].Body!);
                JObject card = (JObject)payload["embeds"]![0]!;
                CheckCompactEmbed(card, null, cases[index].Item2, "Steam64 public individual range and trusted-source validation " + index);
                Check(!payload.ToString().Contains("steamworks:") && !payload.ToString().Contains("76561198000000002") &&
                    !payload.ToString().Contains("PRIVATE"), "Invalid trusted identity never falls back to Actor.Id, target identity or arbitrary fields.");
            }
        }
    }

    private static ServerManagerEvent SteamIdentityEvent(string kind, string account, string name = "Complete")
    {
        ServerManagerEvent value = SensitiveEvent(kind);
        // Raw Actor.Id and fields deliberately contain plausible identifiers.
        // Only the typed account slot can authorize the dedicated ID line.
        value.Actor = new ServerManagerActor("steamworks:76561198000000002", name, "player") { PlayerAccountId = account };
        value.Target = new ServerManagerActor("76561198000000002", "Other player", "player")
            { PlayerAccountId = "steamworks:76561198000000002" };
        value.Fields["account_id"] = "steamworks:76561198000000002";
        value.Fields["steam_id"] = "76561198000000002";
        value.Fields["player_account_id"] = "steamworks:76561198000000002";
        value.Fields["title"] = "PRIVATE_TITLE";
        value.Fields["summary"] = "PRIVATE_SUMMARY";
        value.Fields["text"] = "public words";
        value.Fields["raid"] = "army_bonemass";
        if (kind == "boss.killed") value.Target = new ServerManagerActor("PRIVATE_BOSS_ID", "Eikthyr", "boss");
        if (kind == "player.login" || kind == "player.leave")
        {
            // The exact opted-in number also remains private if duplicated in
            // ordinary event fields; only the separately validated line escapes.
            value.Fields["bot_token"] = "76561198000000001";
            value.Fields["player_name"] = "Fallback player";
        }
        return value;
    }

    private static async Task SteamIdSnapshotAndReload()
    {
        DiscordSettings initial = Settings("player.connection");
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(initial, http, delegate { }))
        {
            initial.WebhookRoutes[0].IncludeSteamId = true;
            Task run = webhooks.RunAsync(CancellationToken.None);
            Check(webhooks.Enqueue(SteamIdentityEvent("player.login", "steamworks:76561198000000001")), "Initial connection route queues normally.");
            await WaitFor(() => handler.Requests.Count == 1);
            Check(!handler.Requests[0].Body!.Contains("Steam ID:"), "Mutating the original DTO cannot turn on IDs in the committed snapshot.");
            DiscordSettings enabled = Settings("player.connection"); enabled.WebhookRoutes[0].IncludeSteamId = true;
            enabled.WebhookRoutes[0].Url = ReloadedWebhook;
            Check(webhooks.Reload(enabled), "Live reload commits the Steam-ID policy with its route generation.");
            enabled.WebhookRoutes[0].IncludeSteamId = false;
            enabled.WebhookRoutes[0].Url = Webhook; enabled.WebhookRoutes[0].Events.Clear();
            Check(webhooks.Enqueue(SteamIdentityEvent("player.leave", "steamworks:76561198000000001")), "Caller mutation cannot detach the committed connection filter.");
            await WaitFor(() => handler.Requests.Count == 2);
            Check(handler.Requests[1].Url == ReloadedWebhook + "?wait=true" && handler.Requests[1].Body!.Contains("Steam ID: 76561198000000001"),
                "Committed opt-in and URL remain immutable after caller-owned DTO mutation.");
            Check(webhooks.Reload(Settings("player.connection")) && webhooks.Enqueue(SteamIdentityEvent("player.login", "steamworks:76561198000000001")),
                "A later live reload can remove the opt-in without recreating the worker.");
            await WaitFor(() => handler.Requests.Count == 3);
            Check(handler.Requests[2].Url == Webhook + "?wait=true" && !handler.Requests[2].Body!.Contains("Steam ID:"),
                "Reload disabling IDs takes effect together with the new URL.");
            DiscordSettings anonymous = AnonymousSettings("Guest", "player.connection"); anonymous.WebhookRoutes[0].IncludeSteamId = true;
            Check(webhooks.Reload(anonymous) && webhooks.Enqueue(SteamIdentityEvent("player.leave", "steamworks:76561198000000001")),
                "A live anonymous route still overrides its enabled ID flag.");
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == 4 && handler.Requests[3].Body!.Contains("Guest 1 left") &&
                !handler.Requests[3].Body!.Contains("Steam ID:") && !handler.Requests[3].Body!.Contains("Complete"),
                "Reloaded anonymity suppresses both typed identity and real player name.");
        }
        DiscordSettings optedIn = Settings("player.connection"); optedIn.WebhookRoutes[0].IncludeSteamId = true;
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(optedIn, http, delegate { }))
        {
            ServerManagerEvent stale = SteamIdentityEvent("player.login", "steamworks:76561198000000001");
            Check(webhooks.Enqueue(stale) && webhooks.Reload(Settings("player.connection")), "Changing identity policy retires queued old-policy payloads.");
            Check(!webhooks.Enqueue(stale) && webhooks.Enqueue(SteamIdentityEvent("player.leave", "steamworks:76561198000000001")),
                "A discarded old event ID is not replayed under the replacement identity policy.");
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == 1 && !handler.Requests[0].Body!.Contains("Steam ID:"), "No queued opted-in card survives an opt-out reload.");
        }
        using (DeferredFirstHandler handler = new DeferredFirstHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(optedIn, http, delegate { }))
        {
            ServerManagerEvent stale = SteamIdentityEvent("player.login", "steamworks:76561198000000001");
            Check(webhooks.Enqueue(stale), "An old opted-in send can already be in flight when policy changes.");
            Task run = webhooks.RunAsync(CancellationToken.None);
            await WaitFor(() => handler.Started.Task.IsCompleted);
            DiscordSettings replacement = Settings("player.connection"); replacement.WebhookRoutes[0].Url = ReloadedWebhook;
            Check(webhooks.Reload(replacement) && handler.FirstToken.IsCancellationRequested && !webhooks.Enqueue(stale),
                "Opt-out reload cancels the old in-flight generation and never replays an ambiguous ID-bearing send.");
            Check(webhooks.Enqueue(SteamIdentityEvent("player.leave", "steamworks:76561198000000001")), "A fresh connection uses the replacement opt-out generation.");
            await WaitFor(() => handler.Requests.Count == 2);
            handler.FirstReply.TrySetResult(Response(401)); await Task.Delay(30);
            Check(webhooks.Enqueue(SteamIdentityEvent("player.login", "steamworks:76561198000000001")), "A late old-policy authorization error cannot disable the replacement route.");
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == 3 && handler.Requests[0].Body!.Contains("Steam ID: 76561198000000001") &&
                handler.Requests.Skip(1).All(request => request.Url == ReloadedWebhook + "?wait=true" && !request.Body!.Contains("Steam ID:")),
                "In-flight cancellation retains generation isolation for the Steam-ID flag, URL and disabled-route state.");
        }
    }

    private static void CheckCompactEmbed(JObject embed, string? title, string? description, string label)
    {
        Check(embed.Properties().All(property => new[] { "title", "description", "color" }.Contains(property.Name)),
            "Compact card has no fields, footer, timestamp or diagnostic metadata: " + label);
        Check(!string.IsNullOrWhiteSpace((string?)embed["title"]) && ((string)embed["title"]!).Length <= 256 &&
            ((string?)embed["description"] ?? "").Length <= 2500, "Compact text stays inside its fixed budgets: " + label);
        if (title != null) Check((string?)embed["title"] == title, "Exact compact title: " + label);
        if (description != "*") Check((string?)embed["description"] == description, "Exact compact body or omitted body: " + label);
    }

    private static async Task CompactEventCards()
    {
        string[] kinds = { "server.ready", "server.shutdown", "server.saved", "player.login", "player.leave",
            "player.death", "combat.pvp_kill", "boss.killed", "server.announcement" };
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings(kinds.Concat(new[] { "server.started", "player.first_join" }).ToArray()), http, delegate { }))
        {
            Check(!webhooks.Enqueue(Event("server.started")) && !webhooks.Enqueue(Event("player.first_join")),
                "Removed lifecycle/first-join kinds cannot enter webhook delivery, even through a directly constructed route DTO.");
            var cases = new List<Tuple<ServerManagerEvent, string, string?>>();
            foreach (var entry in new[] { Tuple.Create("server.ready", "Server ready"), Tuple.Create("server.shutdown", "Server shutdown"),
                Tuple.Create("server.saved", "World saved") })
            {
                ServerManagerEvent value = AnonymousEvent(entry.Item1);
                value.Fields["operation_id"] = "SECRET_OPERATION";
                cases.Add(Tuple.Create(value, entry.Item2, (string?)null));
            }
            foreach (string? first in new string?[] { null, "true", "false", "TRUE", " true", "1", "SECRET_FLAG" })
            {
                ServerManagerEvent value = Event("player.login");
                value.Actor = new ServerManagerActor("RAW_ID", "Complete", "player");
                value.Fields["player_name"] = "SECRET_DUPLICATE_NAME";
                if (first != null) value.Fields["first_join"] = first;
                cases.Add(Tuple.Create(value, first == "true" ? "Complete joined for the first time" : "Complete joined", (string?)null));
            }
            ServerManagerEvent leave = Event("player.leave"); leave.Actor = new ServerManagerActor("RAW_ID", "Complete", "player");
            cases.Add(Tuple.Create(leave, "Complete left", (string?)null));
            ServerManagerEvent death = Event("player.death"); death.Actor = new ServerManagerActor("RAW_ID", "Complete", "player");
            death.Target = new ServerManagerActor("", "Greydwarf", "creature");
            death.Fields["cause"] = "creature";
            cases.Add(Tuple.Create(death, NamedStory(death), (string?)null));
            ServerManagerEvent pvp = Event("combat.pvp_kill");
            pvp.Actor = new ServerManagerActor("VICTIM_RAW_ID", "Complete", "player");
            pvp.Target = new ServerManagerActor("ATTACKER_RAW_ID", "halla", "player");
            cases.Add(Tuple.Create(pvp, NamedStory(pvp), (string?)null));
            ServerManagerEvent boss = Event("boss.killed");
            boss.Actor = new ServerManagerActor("KILLER_RAW_ID", "Complete", "player");
            boss.Target = new ServerManagerActor("BOSS_RAW_ID", "Eikthyr", "boss");
            boss.Fields["boss"] = "Eikthyr";
            cases.Add(Tuple.Create(boss, NamedStory(boss), (string?)null));
            ServerManagerEvent announcement = Event("server.announcement"); announcement.Fields["message"] = "Public announcement";
            cases.Add(Tuple.Create(announcement, "Announcement", (string?)"Public announcement"));
            foreach (var test in cases) Check(webhooks.Enqueue(test.Item1), "Compact event is admitted: " + test.Item2);
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == cases.Count, "Every compact event sends one message, with no duplicate first-join delivery.");
            for (int i = 0; i < cases.Count; i++)
            {
                JObject payload = JObject.Parse(handler.Requests[i].Body!);
                Check(((JArray)payload["embeds"]!).Count == 1, "Ordinary compact events have exactly one card.");
                JObject embed = (JObject)payload["embeds"]![0]!;
                CheckCompactEmbed(embed, cases[i].Item2, cases[i].Item3, cases[i].Item2);
                Check(!payload.ToString().Contains("SECRET") && !payload.ToString().Contains("RAW_ID"),
                    "Compact cards do not append operation IDs, duplicate names or raw actor IDs.");
                if (cases[i].Item1.Kind == "player.death")
                    Check(((string?)embed["title"] ?? "").Contains("Greydwarf") &&
                        !((string?)embed["title"] ?? "").Contains("\n"), "Shared creature-death sentence retains its target without a duplicate cause line.");
            }
        }
    }

    private static async Task CompactCronCards()
    {
        DiscordSettings settings = Settings("command.executed");
        settings.WebhookRoutes.Add(new DiscordWebhookRoute
        {
            Name = "Cron", Url = ReloadedWebhook, Username = "ServerManager",
            Events = new HashSet<string> { "cron.executed" }
        });
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
        {
            ServerManagerEvent manual = Event("command.executed");
            manual.Fields["source"] = "discord";
            manual.Fields["command"] = "players";
            manual.Fields["success"] = "true";
            manual.Fields["result_code"] = "players_listed";
            Check(webhooks.Enqueue(manual), "Manual command remains on command.executed.");

            ServerManagerEvent intermediate = Event("command.executed");
            intermediate.Fields["source"] = "cron";
            intermediate.Fields["command"] = "broadcast";
            intermediate.Fields["success"] = "true";
            intermediate.Fields["result_code"] = "cron_dispatched";
            Check(!webhooks.Enqueue(intermediate), "Intermediate cron command does not create its own Discord card.");

            ServerManagerEvent completed = Event("command.executed");
            completed.Fields["source"] = "cron";
            completed.Fields["command"] = "schedule";
            completed.Fields["success"] = "true";
            completed.Fields["result_code"] = "cron_completed";
            completed.Fields["cron_verb"] = "broadcast";
            completed.Fields["cron_command_count"] = "1";
            completed.Fields["cron_schedule"] = "1-59/30 * * * *";
            completed.Fields["cron_summary"] = "<color=yellow><size=32>AM,PM 05:30에 서버를 재부팅합니다</size></color>";
            Check(webhooks.Enqueue(completed), "Final cron result uses the separate cron.executed selector.");

            ServerManagerEvent failed = Event("command.executed");
            failed.Fields["source"] = "cron";
            failed.Fields["command"] = "maintenance";
            failed.Fields["success"] = "false";
            failed.Fields["result_code"] = "uw_partial_failure";
            failed.Fields["cron_command_count"] = "2";
            failed.Fields["cron_schedule"] = "30 5 * * *";
            failed.Fields["cron_summary"] = "zones_reset → save";
            Check(webhooks.Enqueue(failed), "Failed multi-command cron result uses the same compact final card.");
            ServerManagerEvent untracked = Event("command.executed");
            untracked.Fields["source"] = "cron";
            untracked.Fields["command"] = "maintenance";
            untracked.Fields["success"] = "true";
            untracked.Fields["result_code"] = "cron_dispatched_untracked";
            untracked.Fields["cron_verb"] = "zones_generate";
            untracked.Fields["cron_schedule"] = "37 21 * * *";
            Check(webhooks.Enqueue(untracked), "Dispatch-only cron result is visible.");


            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(handler.Requests.Count == 4 && handler.Requests[0].Url.StartsWith(Webhook, StringComparison.Ordinal) &&
                handler.Requests.Skip(1).All(request => request.Url.StartsWith(ReloadedWebhook, StringComparison.Ordinal)),
                "Manual and cron results reach only their independently selected destinations.");
            CheckCompactEmbed((JObject)JObject.Parse(handler.Requests[0].Body!)["embeds"]![0]!,
                "Command: players", "Result: players\\_listed", "Manual command card remains unchanged");
            CheckCompactEmbed((JObject)JObject.Parse(handler.Requests[1].Body!)["embeds"]![0]!,
                "✅ Cron · broadcast", "AM,PM 05:30에 서버를 재부팅합니다 · 1-59/30 \\* \\* \\* \\*",
                "Successful cron card strips game markup and uses two compact lines");
            CheckCompactEmbed((JObject)JObject.Parse(handler.Requests[2].Body!)["embeds"]![0]!,
                "❌ Cron · 2 commands", "zones\\_reset → save · 30 5 \\* \\* \\* · Failed: uw\\_partial\\_failure",
                "Failed cron card combines the job and result without a second schedule card");
            CheckCompactEmbed((JObject)JObject.Parse(handler.Requests[3].Body!)["embeds"]![0]!,
                "ℹ Cron · zones\\_generate", "37 21 \\* \\* \\* · Dispatched; completion not tracked",
                "Untracked dispatch uses informational status and never claims completion");
        }
    }

    private static async Task EnvironmentalDeathCards()
    {
        foreach (bool anonymous in new[] { false, true })
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(anonymous ? AnonymousSettings("Guest", "player.death") :
            Settings("player.death"), http, delegate { }))
        {
            var cases = new List<Tuple<ServerManagerEvent, string>>();
            foreach (string cause in new[] { "smoke", "freezing" })
            foreach (bool clientReported in new[] { false, true })
            {
                ServerManagerEvent value = Event("player.death");
                value.Actor = AnonymousActor("steamworks:76561198000000001", "VICTIM_PRIVATE_ID", "Complete");
                value.Reliability = clientReported ? "client_reported" : "authoritative";
                // Mirror the existing PublishDeath projection when CreateDeath
                // supplies a typed cause with empty attacker fields, not PvP.
                value.Target = new ServerManagerActor("", cause, "cause");
                value.Fields["cause"] = cause;
                value.Fields["attacker"] = cause;
                value.Fields["attacker_prefab"] = "";
                value.Fields["hit_type"] = cause == "smoke" ? "Smoke" : "Freezing";
                value.Fields["damage_tags"] = "generic,frost";
                value.Fields["message"] = "Complete died from " + cause + ".";
                cases.Add(Tuple.Create(value, anonymous ? Story(value, "Guest 1", "a creature") : NamedStory(value)));
            }
            ServerManagerEvent creature = Event("player.death");
            creature.Actor = cases[0].Item1.Actor;
            creature.Target = new ServerManagerActor("", "Drake", "cause");
            creature.Fields["cause"] = "creature";
            creature.Fields["attacker"] = "Drake";
            creature.Fields["damage_tags"] = "frost";
            creature.Fields["hit_type"] = "EnemyHit";
            cases.Add(Tuple.Create(creature, anonymous ? Story(creature, "Guest 1", "Drake") : NamedStory(creature)));
            foreach (string cause in new[] { "pvp", "playerhit" })
            {
                ServerManagerEvent playerCause = Event("player.death");
                playerCause.Actor = creature.Actor;
                playerCause.Target = new ServerManagerActor("SECRET_ATTACKER_ID", "SECRET_ATTACKER_NAME", "player");
                playerCause.Fields["cause"] = cause;
                playerCause.Fields["attacker"] = "SECRET_ATTACKER_NAME";
                cases.Add(Tuple.Create(playerCause, anonymous ? Story(playerCause, "Guest 1", "Guest player") : NamedStory(playerCause)));
            }
            foreach (string? unknown in new string?[] { null, "Unknown", "" })
            {
                // The route intentionally does not include boss.killed; these
                // are tested separately by the ordinary combat-card fixtures.
                // Here the unknown death target must not invent a named foe.
                ServerManagerEvent unknownTarget = Event("player.death");
                unknownTarget.Actor = creature.Actor;
                unknownTarget.Target = unknown == null ? null : new ServerManagerActor("", unknown, "cause");
                unknownTarget.Fields["cause"] = "UNRECOGNIZED_SECRET_CAUSE";
                unknownTarget.Fields["message"] = "SECRET_OLD_ATTACKER";
                cases.Add(Tuple.Create(unknownTarget, anonymous ? Story(unknownTarget, "Guest 1", "a creature") : NamedStory(unknownTarget)));
            }
            foreach (var test in cases) Check(webhooks.Enqueue(test.Item1), "Environmental/frost death uses the existing player.death route.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == cases.Count, "Each environmental death produces one webhook without a new event kind.");
            for (int index = 0; index < cases.Count; ++index)
            {
                JObject payload = JObject.Parse(handler.Requests[index].Body!);
                JObject embed = (JObject)payload["embeds"]![0]!;
                CheckCompactEmbed(embed, cases[index].Item2, cases[index].Item1.Reliability == "client_reported" ? "Client report" : null,
                    "Smoke/freezing shares its phrase with the game and retains the client-report marker");
                Check(!payload.ToString().Contains("VICTIM_PRIVATE_ID") && !payload.ToString().Contains("steamworks:") &&
                    !payload.ToString().Contains("was defeated by"), "Environmental causes do not expose identifiers or become a PvP notification.");
                if (anonymous) Check(!payload.ToString().Contains("Complete") &&
                    (payload.ToString().Contains("Drake") == (cases[index].Item1.Fields["cause"] == "creature")),
                    "Anonymous environmental cards hide player names while explicit creature death retains its foe.");
                if (anonymous) Check(!payload.ToString().Contains("SECRET"), "Anonymous PvP-cause and unknown-cause variants never recover raw attacker or arbitrary cause text.");
            }
        }
    }

    private static string Story(ServerManagerEvent value, string player, string other, string language = "English")
    {
        Check(EventMessageText.TryFormat(value, player, other, language, out string message), "Shared formatter accepts a combat event.");
        // The transport's established escaping is separate from shared English
        // wording. Verify the formatter output crosses that existing boundary.
        return (string)typeof(DiscordWebhooks).GetMethod("SafeText", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { message, 2000 })!;
    }

    private static string NamedStory(ServerManagerEvent value) => Story(value, EventMessageText.PlayerName(value), EventMessageText.OtherName(value));

    private static async Task LocalizedStoryRoutes()
    {
        string[] kinds = { "player.death", "combat.pvp_kill", "boss.killed", "server.ready", "chat.shout" };
        DiscordSettings settings = Settings(kinds);
        settings.WebhookRoutes.Add(new DiscordWebhookRoute { Name = "Korean", Url = ReloadedWebhook, Language = "Korean", Events = new HashSet<string>(kinds.Select(ExpectedWebhookFilter)) });
        settings.WebhookRoutes.Add(new DiscordWebhookRoute { Name = "Anonymous Korean", Url = "https://discord.com/api/webhooks/333333/offline", Language = "Korean",
            AnonymousPrefix = "Guest", Events = new HashSet<string>(kinds.Select(ExpectedWebhookFilter)) });
        settings.WebhookRoutes.Add(new DiscordWebhookRoute { Name = "Unknown locale", Url = "https://discord.com/api/webhooks/444444/offline", Language = "Unknown_Language",
            Events = new HashSet<string>(kinds.Select(ExpectedWebhookFilter)) });
        var events = new List<ServerManagerEvent>();
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
        {
            foreach (string family in new[] { "creature", "boss", "poisoned", "burning", "drowning", "fall", "tree", "smoke", "freezing", "unknown", "pvp", "victory", "unknown-victory" })
            for (int variant = 0; variant < 3; ++variant)
            {
                string kind = family == "pvp" ? "combat.pvp_kill" : family.EndsWith("victory", StringComparison.Ordinal) ? "boss.killed" : "player.death";
                ServerManagerEvent value = Event(kind);
                value.Actor = family == "unknown-victory" ? null : new ServerManagerActor("RAW_ACTOR_ID", "Complete", "player") { PlayerAccountId = "76561198000000001" };
                value.Target = new ServerManagerActor("RAW_TARGET_ID", kind == "combat.pvp_kill" ? "halla" : family == "boss" || kind == "boss.killed" ? "Eikthyr" : "Greydwarf",
                    kind == "combat.pvp_kill" ? "player" : kind == "boss.killed" ? "boss" : "cause")
                    { PlayerAccountId = kind == "combat.pvp_kill" ? "76561198000000002" : "" };
                value.Fields["cause"] = family;
                value.Fields["boss"] = "Eikthyr";
                value.Reliability = variant == 0 ? "authoritative" : "client_reported";
                string fingerprint = EventFingerprint(value);
                Check(webhooks.Enqueue(value) && EventFingerprint(value) == fingerprint, "Localized projections preserve neutral event data");
                Check(!webhooks.Enqueue(value), "One EventId is deduplicated across all language routes");
                events.Add(value);
            }
            ServerManagerEvent ready = Event("server.ready");
            Check(webhooks.Enqueue(ready), "A non-story event remains routable with language set");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == (events.Count + 1) * 4, "Each event reaches four independent locale routes exactly once");
            for (int index = 0; index < events.Count; ++index)
            {
                ServerManagerEvent value = events[index];
                string english = "";
                for (int route = 0; route < 4; ++route)
                {
                    bool anonymous = route == 2;
                    string language = route == 1 || anonymous ? "Korean" : route == 3 ? "Unknown_Language" : "English";
                    string actor = anonymous ? value.Actor == null ? "Guest player" : "Guest 1" : EventMessageText.PlayerName(value);
                    string target = anonymous && value.Kind == "combat.pvp_kill" ? "Guest 2" : EventMessageText.OtherName(value);
                    string expected = Story(value, actor, target, language);
                    JObject payload = JObject.Parse(handler.Requests[index * 4 + route].Body!);
                    JObject card = (JObject)payload["embeds"]![0]!;
                    string? report = value.Reliability == "client_reported" ? PlayerLocalizer.TextForLanguage(language, "sm_event_client_report") : null;
                    CheckCompactEmbed(card, expected, report, "Localized shared story");
                    if (route == 0) english = expected;
                    if (route == 3) Check(expected == english, "Missing safe locale falls back to the exact English sentence and variant");
                    if (route == 1 || anonymous)
                    {
                        Check(expected.Any(ch => ch >= '\uac00' && ch <= '\ud7a3') && expected != english,
                            "Actual embedded Korean resources render a translated sentence for every family");
                        if (report != null) Check(report.Any(ch => ch >= '\uac00' && ch <= '\ud7a3'), "Client-report label is translated with its route");
                    }
                    if (anonymous) Check(!payload.ToString().Contains("Complete") && !payload.ToString().Contains("halla"),
                        "Translated anonymous route hides player identities while preserving named nonplayer foes");
                    Check(!payload.ToString().Contains("RAW_") && !payload.ToString().Contains("sm_event_"), "No IDs or unresolved localization tokens enter the card");
                }
            }
            for (int route = 0; route < 4; ++route)
                CheckCompactEmbed((JObject)JObject.Parse(handler.Requests[events.Count * 4 + route].Body!)["embeds"]![0]!,
                    route == 1 || route == 2 ? "서버 준비 완료" : "Server ready", null,
                    "Public ready title follows its route language alongside shared stories");
        }

        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("player.death"), http, delegate { }))
        {
            ServerManagerEvent first = events[0], next = events[1];
            Task running = webhooks.RunAsync(CancellationToken.None);
            Check(webhooks.Enqueue(first), "English generation accepts its first event");
            await WaitFor(() => handler.Requests.Count == 1);
            DiscordSettings replacement = Settings("player.death"); replacement.WebhookRoutes[0].Language = "Korean";
            Check(webhooks.Reload(replacement) && !webhooks.Enqueue(first), "Language reload never replays an already-delivered EventId");
            replacement.WebhookRoutes[0].Language = "English";
            Check(webhooks.Enqueue(next), "A new event uses the copied Korean route after the source DTO is changed");
            await WaitFor(() => handler.Requests.Count == 2);
            DiscordSettings invalid = Settings("player.death"); invalid.WebhookRoutes[0].Language = "../English";
            bool rejected = false;
            try { webhooks.Reload(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Programmatic unsafe locale reload is rejected before changing active routes");
            ServerManagerEvent last = events[2];
            Check(webhooks.Enqueue(last), "A rejected language reload preserves the active Korean sender");
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == 3, "Language reload produces no duplicate deliveries");
            for (int index = 1; index < 3; ++index)
                CheckCompactEmbed((JObject)JObject.Parse(handler.Requests[index].Body!)["embeds"]![0]!,
                    Story(events[index], EventMessageText.PlayerName(events[index]), EventMessageText.OtherName(events[index]), "Korean"),
                    PlayerLocalizer.TextForLanguage("Korean", "sm_event_client_report"), "Language snapshot survives later DTO mutation and rejected reload");
        }
    }

    private static async Task LocalizedPublicFrames()
    {
        string[] publicKinds = { "server.ready", "server.shutdown", "server.saved", "player.login", "player.leave", "raid.started", "raid.ended", "server.announcement", "chat.shout" };
        string[] kinds = publicKinds.Concat(OperatorKinds).Concat(new[] { "moderation.action", "command.executed" }).ToArray();
        foreach (string language in new[] { "", "English", "Korean", "Unknown_Public_Language" })
        foreach (bool anonymous in new[] { false, true })
        {
            bool korean = language == "Korean";
            string who = anonymous ? "Guest 1" : "Complete {0}";
            DiscordSettings settings = anonymous ? AnonymousSettings("Guest", kinds) : Settings(kinds);
            if (language.Length > 0) settings.WebhookRoutes[0].Language = language;
            DiscordSettings englishSettings = anonymous ? AnonymousSettings("Guest", kinds) : Settings(kinds);
            using (FakeHandler handler = new FakeHandler())
            using (FakeHandler englishHandler = new FakeHandler())
            using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
            using (DiscordHttp englishHttp = new DiscordHttp("", delegate { }, englishHandler))
            using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
            using (DiscordWebhooks english = new DiscordWebhooks(englishSettings, englishHttp, delegate { }))
            {
                var cases = new List<Tuple<ServerManagerEvent, string, string?>>();
                ServerManagerEvent Create(string kind)
                {
                    ServerManagerEvent value = Event(kind);
                    value.Actor = AnonymousActor("steamworks:76561198000000001", "RAW_ACTOR_ID", "Complete {0}");
                    return value;
                }
                foreach (string kind in new[] { "server.ready", "server.shutdown", "server.saved" })
                {
                    string title = kind == "server.ready" ? korean ? "서버 준비 완료" : "Server ready" :
                        kind == "server.shutdown" ? korean ? "서버 종료 중" : "Server shutdown" : korean ? "월드 저장 완료" : "World saved";
                    cases.Add(Tuple.Create(Create(kind), title, (string?)null));
                }
                foreach (string? first in new string?[] { null, "true", "false", "TRUE", " true", "1" })
                {
                    ServerManagerEvent value = Create("player.login");
                    if (first != null) value.Fields["first_join"] = first;
                    cases.Add(Tuple.Create(value, who + (first == "true" ? korean ? " 첫 접속" : " joined for the first time" : korean ? " 접속" : " joined"), (string?)null));
                }
                cases.Add(Tuple.Create(Create("player.leave"), who + (korean ? " 접속 종료" : " left"), (string?)null));
                foreach (string kind in new[] { "raid.started", "raid.ended" })
                foreach (string evidence in new[] { "verified", "client_reported", "malformed" })
                {
                    ServerManagerEvent value = Create(kind);
                    value.Fields["raid"] = "Forest {0}";
                    value.Fields["raid_coordinates"] = evidence == "malformed" ? "1,2,3" : "-371, 40, 1591";
                    if (evidence == "client_reported") value.Reliability = "client_reported";
                    value.Fields["player_position"] = "901, 902, 903";
                    string title = kind == "raid.started" ? korean ? "습격 시작" : "Raid started" : korean ? "습격 종료" : "Raid ended";
                    cases.Add(Tuple.Create(value, title + " — Forest {0}" +
                        (evidence == "verified" ? " \\[-371, 40, 1591\\]" : ""), (string?)null));
                }
                ServerManagerEvent announcement = Create("server.announcement");
                announcement.Fields["message"] = "Complete {0}: keep this English and 한국어 announcement.";
                cases.Add(Tuple.Create(announcement, korean ? "공지" : "Announcement", (string?)announcement.Fields["message"]));
                ServerManagerEvent saved = Create("server.saved");
                saved.Fields["completion_scope"] = "world_disk_with_partial_retained_characters";
                saved.Fields["character_commit_scope"] = "partial_retained_shadows_at_cutoff";
                saved.Fields["pending_character_count"] = "2";
                cases.Add(Tuple.Create(saved, korean ? "월드 저장 완료" : "World saved", (string?)null));
                foreach (var test in cases)
                {
                    string fingerprint = EventFingerprint(test.Item1);
                    Check(webhooks.Enqueue(test.Item1) && english.Enqueue(test.Item1) && fingerprint == EventFingerprint(test.Item1),
                        "Public localization preserves the factual event and its original free-text fields");
                }
                ServerManagerEvent shout = Create("chat.shout");
                shout.Fields["text"] = "Keep {0}, English words, 한국어";
                shout.Fields["message"] = "Complete {0}: " + shout.Fields["text"];
                Check(webhooks.Enqueue(shout) && english.Enqueue(shout), "Shout remains selected alongside translated public event headings");
                foreach (string kind in kinds.Except(publicKinds))
                {
                    ServerManagerEvent value = AnonymousEvent(kind);
                    Check(webhooks.Enqueue(value) && english.Enqueue(value), "Operator event remains routable without expanding translation scope");
                }
                Task running = webhooks.RunAsync(CancellationToken.None), englishRunning = english.RunAsync(CancellationToken.None);
                await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await english.StopAsync(TimeSpan.FromSeconds(3));
                await running; await englishRunning;
                Check(handler.Requests.Count == cases.Count + 1 + kinds.Except(publicKinds).Count() && handler.Requests.Count == englishHandler.Requests.Count,
                    "Locale routes preserve the exact event count and one message per event");
                for (int index = 0; index < cases.Count; ++index)
                {
                    JObject payload = JObject.Parse(handler.Requests[index].Body!);
                    JArray cards = (JArray)payload["embeds"]!;
                    CheckCompactEmbed((JObject)cards[0]!, cases[index].Item2, cases[index].Item3, "Localized public frame " + language);
                    Check(cards.Count == (index == cases.Count - 1 ? 2 : 1), "Only the verified partial save adds a separate warning card");
                    if (cards.Count == 2) CheckCompactEmbed((JObject)cards[1]!, "Character save pending — 2", null,
                        "Partial-character warning intentionally stays English and never becomes all-characters-complete");
                    Check(!payload.ToString().Contains("RAW_ACTOR_ID") && !payload.ToString().Contains("901, 902, 903"),
                        "Localized frames do not expose actor IDs or unverified player coordinates");
                    if (anonymous && cases[index].Item1.Kind != "server.announcement")
                        Check(!payload.ToString().Contains("Complete"),
                            "Anonymous public framing hides player identities without hiding nonplayer raid names");
                    if (!korean) Check(handler.Requests[index].Body == englishHandler.Requests[index].Body,
                        "Omitted, explicit English and unknown-language fallback preserve existing English payloads exactly");
                }
                CheckPlainShoutPayload(JObject.Parse(handler.Requests[cases.Count].Body!), who + ": " + shout.Fields["text"],
                    "Language never translates or token-expands arbitrary shout text");
                for (int index = cases.Count; index < handler.Requests.Count; ++index)
                    Check(handler.Requests[index].Body == englishHandler.Requests[index].Body,
                        "Shout and all operator payloads remain byte-identical to English regardless of route language");
            }
        }
    }

    private static async Task AnonymousStoryIdentityBoundaries()
    {
        foreach (string language in new[] { "English", "Korean" })
        {
            DiscordSettings settings = AnonymousSettings("Guest", "player.death", "combat.pvp_kill", "boss.killed");
            settings.WebhookRoutes[0].Language = language;
            using (FakeHandler handler = new FakeHandler())
            using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
            using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
            {
                var cases = new List<Tuple<ServerManagerEvent, string, string>>();
                void Add(string kind, string cause, string? targetName, string targetKind, bool account,
                    string attackerField, string expectedOther, bool unknownKiller = false)
                {
                    ServerManagerEvent value = Event(kind);
                    value.Actor = unknownKiller ? null : AnonymousActor("steamworks:76561198000000001", "PRIVATE_VICTIM_ID", "PRIVATE_VICTIM");
                    value.Target = targetName == null ? null : new ServerManagerActor("PRIVATE_TARGET_ID", targetName, targetKind)
                        { PlayerAccountId = account ? "steamworks:76561198000000002" : "" };
                    value.Fields["cause"] = cause;
                    value.Fields["attacker"] = attackerField;
                    value.Fields["boss"] = attackerField;
                    if (unknownKiller)
                    {
                        value.Fields["player"] = "Unknown";
                        value.Fields["player_name"] = "PRIVATE_REPORTER";
                    }
                    cases.Add(Tuple.Create(value, expectedOther, unknownKiller ? "Guest player" : "Guest 1"));
                }
                foreach (string cause in new[] { "creature", "enemyhit", "boss" })
                {
                    Add("player.death", cause, "Forest Troll", "cause", false, "PRIVATE_IGNORED_FALLBACK", "Forest Troll");
                    Add("player.death", cause, "PRIVATE_PLAYER", "player", false, "PRIVATE_PLAYER", "Guest player");
                    Add("player.death", cause, "PRIVATE_PLAYER", "creature", true, "PRIVATE_PLAYER", "Guest 2");
                    Add("player.death", cause, null, "", false, "Forest Troll", "Forest Troll");
                    Add("player.death", cause, null, "", false, "", "");
                }
                foreach (string cause in new[] { "pvp", "playerhit" })
                {
                    Add("player.death", cause, "PRIVATE_PLAYER", "creature", false, "PRIVATE_PLAYER", "Guest player");
                    Add("player.death", cause, "PRIVATE_PLAYER", "boss", true, "PRIVATE_PLAYER", "Guest 2");
                    Add("player.death", cause, null, "", false, "PRIVATE_PLAYER", "Guest player");
                }
                Add("combat.pvp_kill", "creature", "PRIVATE_PLAYER", "boss", false, "PRIVATE_PLAYER", "Guest player");
                Add("combat.pvp_kill", "creature", "PRIVATE_PLAYER", "boss", true, "PRIVATE_PLAYER", "Guest 2");
                Add("combat.pvp_kill", "boss", null, "", false, "PRIVATE_PLAYER", "Guest player");
                Add("boss.killed", "", "Eikthyr", "boss", false, "Eikthyr", "Eikthyr");
                Add("boss.killed", "creature", "PRIVATE_PLAYER", "player", false, "PRIVATE_PLAYER", "Guest player");
                Add("boss.killed", "", "PRIVATE_PLAYER", "boss", true, "PRIVATE_PLAYER", "Guest 2");
                Add("boss.killed", "pvp", "PRIVATE_PLAYER", "boss", false, "PRIVATE_PLAYER", "Guest player");
                Add("boss.killed", "", "Eikthyr", "boss", false, "Eikthyr", "Eikthyr", unknownKiller: true);
                Add("boss.killed", "", null, "", false, "", "", unknownKiller: true);
                foreach (string cause in new[] { "environment", "UNCLASSIFIED", "poisoned", "burning", "smoke", "freezing", "drowning", "fall", "tree" })
                {
                    Add("player.death", cause, "PRIVATE_UNCLASSIFIED_ATTACKER", "cause", false, "PRIVATE_UNCLASSIFIED_ATTACKER", "");
                    Add("player.death", cause, "PRIVATE_PLAYER", "player", true, "PRIVATE_PLAYER", "Guest 2");
                }
                foreach (var test in cases)
                {
                    string fingerprint = EventFingerprint(test.Item1);
                    Check(webhooks.Enqueue(test.Item1) && EventFingerprint(test.Item1) == fingerprint,
                        "Identity-only projection never changes the neutral original event");
                }
                Task running = webhooks.RunAsync(CancellationToken.None);
                await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
                Check(handler.Requests.Count == cases.Count, "Each mixed-identity case is delivered exactly once");
                for (int index = 0; index < cases.Count; ++index)
                {
                    var test = cases[index];
                    JObject payload = JObject.Parse(handler.Requests[index].Body!);
                    string expected = Story(test.Item1, test.Item3, test.Item2, language);
                    CheckCompactEmbed((JObject)payload["embeds"]![0]!, expected, null, "Anonymous player/nonplayer boundary");
                    Check(!payload.ToString().Contains("PRIVATE") && !payload.ToString().Contains("steamworks:") &&
                        !payload.ToString().Contains("76561198000000002"),
                        "Contradictory NPC classifications never disclose player names, identifiers or raw fallback metadata");
                    if (test.Item1.Actor == null) Check(!expected.Contains("Guest"),
                        "A missing boss killer never becomes the reporter or an invented anonymous killer");
                }
            }
        }
    }

    private static async Task LongRaidTitles()
    {
        foreach (string language in new[] { "English", "Korean" })
        foreach (bool anonymous in new[] { false, true })
        {
            DiscordSettings settings = anonymous ? AnonymousSettings("Guest", "raid.started", "raid.ended") : Settings("raid.started", "raid.ended");
            settings.WebhookRoutes[0].Language = language;
            using (FakeHandler handler = new FakeHandler())
            using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
            using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
            {
                var titles = new List<string>();
                foreach (string kind in new[] { "raid.started", "raid.ended" })
                foreach (string raid in new[] { "army_bonemass", new string('*', 95) + "X" })
                {
                    ServerManagerEvent value = Event(kind);
                    value.Fields["raid"] = raid;
                    value.Fields["raid_coordinates"] = "-2147483648, 2147483647, -2147483648";
                    value.Fields["player_position"] = "901, 902, 903";
                    value.Fields["bot_token"] = "PRIVATE_TOKEN";
                    string heading = kind == "raid.started" ? language == "Korean" ? "습격 시작" : "Raid started" : language == "Korean" ? "습격 종료" : "Raid ended";
                    string rawTitle = heading + " — " + raid + " [" + value.Fields["raid_coordinates"] + "]";
                    string escaped = (string)typeof(DiscordWebhooks).GetMethod("SafeText", System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { rawTitle, 2000 })!;
                    titles.Add(escaped);
                    Check(webhooks.Enqueue(value), "Raid title admits a named nonplayer event under either identity policy");
                }
                Task running = webhooks.RunAsync(CancellationToken.None);
                await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
                Check(handler.Requests.Count == titles.Count, "Long and ordinary raid titles remain one delivery each");
                for (int index = 0; index < titles.Count; ++index)
                {
                    JObject payload = JObject.Parse(handler.Requests[index].Body!);
                    JObject card = (JObject)payload["embeds"]![0]!;
                    string expected = titles[index];
                    if (expected.Length > 256)
                        Check(card["title"] == null && (string?)card["description"] == expected,
                            "Escaped oversized raid line moves intact into the body without a duplicate heading or truncation");
                    else CheckCompactEmbed(card, expected, null, "One-line raid title including verified coordinates");
                    Check(!payload.ToString().Contains("901, 902, 903") && !payload.ToString().Contains("PRIVATE_TOKEN") &&
                        !expected.Contains("…") && ((JArray)payload["allowed_mentions"]!["parse"]!).Count == 0,
                        "Raid layout keeps ordinary secret/coordinate redaction and mention protections");
                }
                // Built-in headings plus bounded raid names fit the title cap;
                // an administrator's translated heading may be longer. Exercise
                // that existing renderer boundary without changing live files.
                foreach (string kind in new[] { "raid.started", "raid.ended" })
                {
                    ServerManagerEvent value = Event(kind);
                    value.Fields["raid_coordinates"] = "-371, 40, 1591";
                    string translatedLine = new string('가', 260) + " — army_bonemass [-371, 40, 1591]";
                    string escaped = (string)typeof(DiscordWebhooks).GetMethod("SafeText", System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { translatedLine, 2000 })!;
                    JObject card = (JObject)typeof(DiscordWebhooks).GetMethod("CompactEmbed", System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { value, translatedLine, "" })!;
                    Check(escaped.Length > 256 && card["title"] == null && (string?)card["description"] == escaped &&
                        card.Properties().All(property => property.Name == "description" || property.Name == "color"),
                        "A long translated raid line is shown whole in the body without a duplicate heading or ellipsis");
                }
            }
        }
    }

    private static async Task ExternalRaidHeadingBounds()
    {
        const string language = "OversizedRaidFixture";
        // The parent harness owns/removes its entire disposable executable
        // directory. This unique child never points at a live BepInEx config.
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "raid-translation-" + Guid.NewGuid().ToString("N"));
        string configuration = Path.Combine(root, "config");
        Directory.CreateDirectory(configuration);
        string start = "Start override\n" + new string('*', 2400) + "\nEND OF START";
        string end = "End override\n" + new string('_', 2400) + "\nEND OF END";
        string yaml = "sm_event_raid_started: " + Newtonsoft.Json.JsonConvert.SerializeObject(start) + "\n" +
            "sm_event_raid_ended: " + Newtonsoft.Json.JsonConvert.SerializeObject(end) + "\n";
        string path = Path.Combine(configuration, "ServerManager." + language + ".yml");
        File.WriteAllText(path, yaml, new UTF8Encoding(false));
        Check((bool)typeof(PlayerLocalizer).GetMethod("ReloadLanguage", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { language, root, configuration })!,
            "The actual localizer accepts the isolated oversized/multiline external raid translation");
        Check(PlayerLocalizer.TextForLanguage(language, "sm_event_raid_started") == start &&
            PlayerLocalizer.TextForLanguage(language, "sm_event_raid_ended") == end,
            "The active dictionary really contains the full external headings before webhook rendering");
        foreach (bool anonymous in new[] { false, true })
        {
            DiscordSettings settings = anonymous ? AnonymousSettings("Guest", "raid.started", "raid.ended") : Settings("raid.started", "raid.ended");
            settings.WebhookRoutes[0].Language = language;
            using (FakeHandler handler = new FakeHandler())
            using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
            using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
            {
                foreach (string kind in new[] { "raid.started", "raid.ended" })
                {
                    ServerManagerEvent value = Event(kind);
                    value.Actor = AnonymousActor("steamworks:76561198000000001", "PRIVATE_ACTOR_ID", "PRIVATE_ACTOR_NAME");
                    value.Fields["raid"] = "army_bonemass";
                    value.Fields["raid_coordinates"] = "-371, 40, 1591";
                    value.Fields["player_position"] = "901, 902, 903";
                    value.Fields["bot_token"] = "PRIVATE_TOKEN";
                    Check(webhooks.Enqueue(value), "Oversized external heading does not discard the authoritative raid event");
                }
                Task running = webhooks.RunAsync(CancellationToken.None);
                await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
                Check(handler.Requests.Count == 2, "Each externally translated raid sends exactly once");
                for (int index = 0; index < 2; ++index)
                {
                    JObject payload = JObject.Parse(handler.Requests[index].Body!);
                    JObject card = (JObject)payload["embeds"]![0]!;
                    string description = (string?)card["description"] ?? "";
                    string plain = description.Replace("\\", "");
                    string heading = (index == 0 ? start : end).Replace('\n', ' ').Substring(0, 499) + "…";
                    const string suffix = " — army_bonemass [-371, 40, 1591]";
                    Check(card["title"] == null && plain == heading + suffix && description.Length <= 2000 &&
                        !description.Contains("\n") && !description.Contains("\r"),
                        "The actual route bounds only the heading to one line, then keeps the raid and coordinates intact in the body");
                    Check(plain.IndexOf("army_bonemass", StringComparison.Ordinal) == plain.LastIndexOf("army_bonemass", StringComparison.Ordinal) &&
                        plain.IndexOf("-371, 40, 1591", StringComparison.Ordinal) == plain.LastIndexOf("-371, 40, 1591", StringComparison.Ordinal),
                        "Raid name and verified center occur exactly once after the bounded external heading");
                    Check(!payload.ToString().Contains("PRIVATE") && !payload.ToString().Contains("76561198000000001") &&
                        !payload.ToString().Contains("901, 902, 903"), "Long-heading fallback never reveals actor identities or unrelated private fields");
                }
            }
        }
        Check(File.ReadAllText(path, new UTF8Encoding(false, true)) == yaml,
            "Rendering does not rewrite or shorten the administrator-authored translation file");
    }

    private static async Task DistributedTranslationLayers()
    {
        const string language = "TransportDiscoveryFixture";
        string root = BepInEx.Paths.BepInExRootPath, config = BepInEx.Paths.ConfigPath;
        string pack = Path.Combine(root, "plugins", "FixturePack", "translations");
        string duplicateDirectory = Path.Combine(root, "patchers", "FixturePack");
        Directory.CreateDirectory(pack); Directory.CreateDirectory(config); Directory.CreateDirectory(duplicateDirectory);
        string fileName = "ServerManager." + language + ".yml";
        string distributedFile = Path.Combine(pack, fileName), configFile = Path.Combine(config, fileName);
        string duplicateFile = Path.Combine(duplicateDirectory, fileName);
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(distributedFile, "sm_event_server_ready: Pack ready\nsm_event_server_shutdown: Pack shutdown\n", utf8);
        File.WriteAllText(configFile, "sm_event_server_ready: Config ready\n", utf8);
        bool Reload() => (bool)typeof(PlayerLocalizer).GetMethod("ReloadLanguage", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { language, root, config })!;
        DiscordSettings settings = Settings("server.ready", "server.shutdown"); settings.WebhookRoutes[0].Language = language;
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
        {
            Task running = webhooks.RunAsync(CancellationToken.None);
            int sent = 0;
            async Task Send(string kind, string expected)
            {
                Check(webhooks.Enqueue(Event(kind)), "A webhook can use the discovered external language without touching the game-language singleton");
                int count = ++sent;
                await WaitFor(() => handler.Requests.Count == count && CurrentInFlight(webhooks) == 0);
                CheckCompactEmbed((JObject)JObject.Parse(handler.Requests[count - 1].Body!)["embeds"]![0]!, expected, null,
                    "Actual route uses the atomically layered/cached translation");
            }
            await Send("server.ready", "Config ready");
            await Send("server.shutdown", "Pack shutdown");
            File.WriteAllText(distributedFile, "sm_event_server_ready: Pack changed\nsm_event_server_shutdown: Pack changed shutdown\n", utf8);
            File.WriteAllText(configFile, "sm_event_server_ready: Config changed\n", utf8);
            await Send("server.ready", "Config ready");
            await Send("server.shutdown", "Pack shutdown");
            Check(Reload(), "Explicit reload accepts one nested pack file with the exact config override excluded from duplicate counting");
            await Send("server.ready", "Config changed");
            await Send("server.shutdown", "Pack changed shutdown");
            File.WriteAllText(duplicateFile, "sm_event_server_ready: DUPLICATE_CANDIDATE\n", utf8);
            File.WriteAllText(configFile, "sm_event_server_ready: CONFIG_MUST_NOT_LEAK\n", utf8);
            Check(!Reload(), "Duplicate distributed files reject the whole language reload rather than picking traversal order");
            await Send("server.ready", "Config changed");
            await Send("server.shutdown", "Pack changed shutdown");
            File.Move(duplicateFile, duplicateFile + ".disabled");
            File.WriteAllText(distributedFile, "sm_event_server_ready: [broken\n", utf8);
            Check(!Reload(), "Malformed distributed YAML rejects config and pack changes as one atomic candidate");
            await Send("server.ready", "Config changed");
            await Send("server.shutdown", "Pack changed shutdown");
            File.WriteAllText(distributedFile, "sm_event_server_shutdown: Restored pack shutdown\n", utf8);
            File.WriteAllText(configFile, "sm_event_server_ready: Restored config ready\n", utf8);
            Check(Reload(), "Corrected package/config files recover through the same explicit reload path");
            await Send("server.ready", "Restored config ready");
            await Send("server.shutdown", "Restored pack shutdown");
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == sent && handler.Requests.All(request => !request.Body!.Contains("CANDIDATE") && !request.Body.Contains("MUST_NOT_LEAK")),
                "No rejected candidate reaches a webhook and translation cache changes never replay events");
        }
    }

    private static async Task LongSharedStoryCards()
    {
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("player.death", "combat.pvp_kill", "boss.killed"), http, delegate { }))
        {
            var cases = new List<Tuple<ServerManagerEvent, string>>();
            foreach (string kind in new[] { "player.death", "combat.pvp_kill", "boss.killed" })
            foreach (bool reported in new[] { false, true })
            {
                ServerManagerEvent value = Event(kind);
                value.Actor = new ServerManagerActor("RAW_PLAYER_ID", new string('*', 80), "player");
                value.Target = new ServerManagerActor("RAW_TARGET_ID", new string('_', 80), kind == "boss.killed" ? "boss" : "cause");
                value.Fields["cause"] = "creature";
                value.Reliability = reported ? "client_reported" : "authoritative";
                string story = NamedStory(value);
                Check(story.Length > 256, "Escaped two-name combat sentence exercises Discord's title overflow path.");
                string original = EventFingerprint(value);
                Check(webhooks.Enqueue(value) && original == EventFingerprint(value), "Long-story rendering preserves factual event data.");
                cases.Add(Tuple.Create(value, story + (reported ? "\nClient report" : "")));
            }
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == cases.Count, "Long shared stories produce one message each.");
            for (int index = 0; index < cases.Count; ++index)
            {
                JObject payload = JObject.Parse(handler.Requests[index].Body!);
                JObject embed = (JObject)payload["embeds"]![0]!;
                Check(embed["title"] == null && (string?)embed["description"] == cases[index].Item2 &&
                    embed.Properties().All(property => property.Name == "description" || property.Name == "color"),
                    "An oversized escaped title becomes a full untruncated body, without a duplicate heading.");
                Check(!payload.ToString().Contains("RAW_PLAYER_ID") && !payload.ToString().Contains("RAW_TARGET_ID") &&
                    !((string)embed["description"]!).Contains("…"), "Long story projection loses neither the victim nor final attacker/boss text.");
            }
        }
    }

    private static async Task CompactTextBoundsAndOutcomes()
    {
        foreach (bool anonymous in new[] { false, true })
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(anonymous ? AnonymousSettings("Guest", "player.death", "security.response", "boss.killed") :
            Settings("player.death", "security.response", "boss.killed"), http, delegate { }))
        {
            ServerManagerEvent death = Event("player.death");
            death.Reliability = "client_reported";
            death.Actor = AnonymousActor("steamworks:76561198000000001", "76561198000000001", "Na*me @everyone\r\nnext");
            death.Target = new ServerManagerActor("", "Greydwarf\r\nSECOND_LINE\t" + new string('g', 4000), "creature");
            death.Fields["cause"] = "creature";
            Check(webhooks.Enqueue(death), "Long and multiline death text is safely compacted.");
            foreach (string outcome in new[] { "response_requested", "banlist_add_failed", "kick_call_returned" })
            {
                ServerManagerEvent response = OperatorEvent("security.response"); response.Actor = death.Actor;
                response.Fields["outcome"] = outcome; response.Fields["response"] = "Ban";
                Check(webhooks.Enqueue(response), "A distinct response stage remains separately deliverable: " + outcome);
            }
            ServerManagerEvent boss = Event("boss.killed");
            boss.Actor = AnonymousActor("steamworks:76561198000000002", "KILLER_PRIVATE_ID", "KillerPrivateName");
            boss.Target = new ServerManagerActor("BOSS_PRIVATE_ID", "EikthyrPrivateName", "boss");
            boss.Fields["boss"] = "EikthyrPrivateName";
            Check(webhooks.Enqueue(boss), "Boss event with a separate killer principal is admitted.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == 5, "Compact privacy and response-stage probes send exactly once each.");
            foreach (RequestRecord request in handler.Requests)
            {
                JObject payload = JObject.Parse(request.Body!); JObject embed = (JObject)payload["embeds"]![0]!;
                CheckCompactEmbed(embed, null, "*", "bounded dynamic compact text");
                Check(!request.Body!.Contains("@everyone") && !request.Body.Contains("76561198000000001"),
                    "Compact dynamic text never enables mentions or appends redundant raw account IDs.");
            }
            JObject deathCard = (JObject)JObject.Parse(handler.Requests[0].Body!)["embeds"]![0]!;
            Check(!((string)deathCard["title"]!).Contains("\n") && (string?)deathCard["description"] == "Client report",
                "Player name and death cause are single-line while client-reported evidence is explicit.");
            string requested = JObject.Parse(handler.Requests[1].Body!).ToString().ToLowerInvariant();
            string failed = JObject.Parse(handler.Requests[2].Body!).ToString().ToLowerInvariant();
            Check(requested.Contains("requested") && !requested.Contains("banned") && failed.Contains("failed") && !failed.Contains("banned"),
                "A requested or failed response never claims that the player was successfully banned.");
            JObject bossCard = (JObject)JObject.Parse(handler.Requests[4].Body!)["embeds"]![0]!;
            Check((string?)bossCard["title"] == (anonymous ? Story(boss, "Guest 2", "EikthyrPrivateName") : NamedStory(boss)),
                "Boss notification assigns the killer's alias from Actor, never from the boss Target.");
            if (anonymous) Check(handler.Requests[0].Body!.Contains("Greydwarf") && handler.Requests[4].Body!.Contains("EikthyrPrivateName") &&
                !handler.Requests[4].Body!.Contains("KillerPrivateName"),
                "Anonymous death/boss rendering preserves nonplayer names but never the killer's identity.");
        }
    }

    private static async Task PartialCharacterSaveWarnings()
    {
        var cases = new List<Tuple<string, string, string?, bool>>();
        foreach (string count in new[] { "1", "2", "2147483647" })
            cases.Add(Tuple.Create("authoritative", "partial_retained_shadows_at_cutoff", (string?)count, true));
        foreach (string? count in new string?[] { null, "", "0", "-1", "+1", "01", "1.0", "2147483648", " 1", "1 ", "NaN", "SECRET_COUNT" })
            cases.Add(Tuple.Create("authoritative", "partial_retained_shadows_at_cutoff", count, false));
        foreach (string reliability in new[] { "observed", "client_reported", "Authoritative", "" })
            cases.Add(Tuple.Create(reliability, "partial_retained_shadows_at_cutoff", (string?)"1", false));
        foreach (string scope in new[] { "all_retained_shadows_at_cutoff", "not_included", "", "SECRET_SCOPE" })
            cases.Add(Tuple.Create("authoritative", scope, (string?)"1", false));
        foreach (bool anonymous in new[] { false, true })
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(anonymous ? AnonymousSettings("Guest", "server.saved") : Settings("server.saved"), http, delegate { }))
        {
            foreach (var test in cases)
            {
                ServerManagerEvent value = AnonymousEvent("server.saved"); value.Reliability = test.Item1;
                value.Fields["character_commit_scope"] = test.Item2;
                value.Fields["completion_scope"] = "world_disk_with_partial_retained_characters";
                value.Fields["captured_character_count"] = "SECRET_CAPTURED";
                value.Fields["persisted_character_count"] = "SECRET_PERSISTED";
                if (test.Item3 != null) value.Fields["pending_character_count"] = test.Item3;
                Check(webhooks.Enqueue(value), "World-save card remains deliverable independently of pending-warning metadata.");
                Check(!webhooks.Enqueue(value), "Two-card partial-save payload has one event-level dedupe decision.");
            }
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == cases.Count, "Partial character warning stays in the original HTTP message and never creates another delivery.");
            for (int i = 0; i < cases.Count; i++)
            {
                JObject payload = JObject.Parse(handler.Requests[i].Body!);
                JArray cards = (JArray)payload["embeds"]!;
                Check(cards.Count == (cases[i].Item4 ? 2 : 1), "Only authoritative, exact partial scope and canonical positive Int32 create a pending warning.");
                CheckCompactEmbed((JObject)cards[0]!, "World saved", null, "primary save title never claims all characters committed");
                if (cases[i].Item4) CheckCompactEmbed((JObject)cards[1]!, "Character save pending — " + cases[i].Item3, null, "partial save warning");
                Check(!payload.ToString().Contains("SECRET"), "Partial save does not expose other counts or diagnostic details.");
            }
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.saved"), http, delegate { }))
        {
            foreach (string? completion in new string?[] { null, "", "world_disk_only", "world_disk_and_retained_characters", "SECRET_COMPLETION" })
            {
                ServerManagerEvent value = Event("server.saved");
                value.Fields["character_commit_scope"] = "partial_retained_shadows_at_cutoff";
                value.Fields["pending_character_count"] = "1";
                if (completion != null) value.Fields["completion_scope"] = completion;
                Check(webhooks.Enqueue(value), "Missing or contradictory completion scope does not suppress the basic saved notification.");
            }
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == 5 && handler.Requests.All(request => ((JArray)JObject.Parse(request.Body!)["embeds"]!).Count == 1),
                "Pending warning additionally requires the producer's exact partial world completion scope.");
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.saved"), http, delegate { }))
        {
            for (int i = 0; i < 256; i++)
            {
                ServerManagerEvent value = Event("server.saved"); value.Fields["character_commit_scope"] = "partial_retained_shadows_at_cutoff";
                value.Fields["completion_scope"] = "world_disk_with_partial_retained_characters";
                value.Fields["pending_character_count"] = "1";
                Check(webhooks.Enqueue(value), "Each two-card save consumes exactly one bounded queue slot.");
            }
            Check(!webhooks.Enqueue(Event("server.saved")), "Two-card queue still enforces the 256-delivery limit.");
        }
    }

    private static async Task PlainShoutContent()
    {
        DiscordSettings settings = Settings("chat.shout", "server.ready");
        settings.WebhookRoutes[0].Username = "Relay sender";
        settings.WebhookRoutes[0].AvatarUrl = "https://example.test/avatar.png";
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
        {
            ServerManagerEvent simple = SensitiveEvent("chat.shout");
            simple.Fields["message"] = "Complete: asdf";
            simple.Fields["title"] = "SHOUT_TITLE_NOT_VISIBLE";
            simple.Fields["player_name"] = "ALTERNATIVE_NAME_NOT_VISIBLE";
            simple.Fields["text"] = "ALTERNATIVE_TEXT_NOT_VISIBLE";
            simple.Fields["shout"] = "ALTERNATIVE_SHOUT_NOT_VISIBLE";
            simple.Fields["plugin_details"] = "PLUGIN_DETAILS_NOT_VISIBLE";
            simple.Fields["arbitrary_field"] = "ARBITRARY_METADATA_NOT_VISIBLE";
            Check(webhooks.Enqueue(simple), "Canonical player-name/message shout is queued.");
            ServerManagerEvent hostile = Event("chat.shout");
            hostile.Fields["message"] = "N*me: **bold** @everyone <@12345> \"quoted\"\0\r\nnext";
            Check(webhooks.Enqueue(hostile), "Literal user content remains deliverable with escaping.");
            ServerManagerEvent longText = Event("chat.shout");
            longText.Fields["message"] = new string('x', 1998) + "\ud83d\ude03" + new string('z', 2000);
            Check(webhooks.Enqueue(longText), "Overlong shout is bounded without discarding the event.");
            foreach (string blank in new[] { "", " \r\n\t", "\0\r" })
            {
                ServerManagerEvent empty = Event("chat.shout");
                empty.Fields["message"] = blank;
                empty.Fields["text"] = "DO_NOT_FALL_BACK_TO_ALTERNATE_FIELDS";
                Check(!webhooks.Enqueue(empty), "Empty canonical shout content cannot enqueue an invalid Discord message.");
            }
            ServerManagerEvent missing = Event("chat.shout");
            missing.Fields.Remove("message");
            missing.Fields["shout"] = "DO_NOT_FALL_BACK_TO_ALTERNATE_FIELDS";
            Check(!webhooks.Enqueue(missing), "Missing canonical message does not expose alternate event fields.");
            Check(webhooks.Enqueue(SensitiveEvent("server.ready")), "Other event types remain deliverable alongside plain shouts.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == 4, "Only three nonempty shouts and the ordinary event were sent.");
            JObject first = JObject.Parse(handler.Requests[0].Body!);
            CheckPlainShoutPayload(first, "Complete: asdf", "canonical shout");
            Check((string?)first["username"] == "Relay sender" &&
                (string?)first["avatar_url"] == "https://example.test/avatar.png", "Plain shouts retain route-level webhook username and avatar.");
            Check(!first.ToString().Contains("NOT_VISIBLE") && !first.ToString().Contains("PRIVATE-"),
                "Plain shout body cannot append title, plugin, actor alternatives, privacy fields, reliability or diagnostics.");
            JObject second = JObject.Parse(handler.Requests[1].Body!);
            CheckPlainShoutPayload(second, null, "escaped shout");
            string escaped = (string)second["content"]!;
            Check(escaped.Contains("N\\*me") && escaped.Contains("\\*\\*bold\\*\\*") && escaped.Contains("\"quoted\"") &&
                !escaped.Contains("@everyone") && !escaped.Contains("<@12345>") && !escaped.Contains("\0") &&
                escaped.Contains("\nnext"), "Plain content preserves literal text/newlines while escaping markdown, stripping unsafe controls and neutralizing mentions.");
            JObject third = JObject.Parse(handler.Requests[2].Body!);
            CheckPlainShoutPayload(third, null, "bounded shout");
            string bounded = (string)third["content"]!;
            Check(bounded.Length <= 2000 && bounded.EndsWith("…", StringComparison.Ordinal) &&
                new UTF8Encoding(false, true).GetByteCount(bounded) > 0, "Discord content length is bounded without splitting a surrogate pair.");
            JObject ordinary = JObject.Parse(handler.Requests[3].Body!);
            Check(ordinary["content"] == null && ordinary["flags"] == null && ordinary["embeds"] is JArray &&
                !ordinary.ToString().Contains("PRIVATE-PLUGINS"), "Non-shout events use compact, event-specific embeds without raw metadata.");
            CheckCompactEmbed((JObject)ordinary["embeds"]![0]!, "Server ready", null, "ready alongside shout");
        }
    }

    private static async Task DiscordShoutRoutes()
    {
        const string userId = "123456789012345678";
        ServerManagerEvent DiscordMessage(string text)
        {
            ServerManagerEvent value = Event("discord.shout");
            value.Actor = new ServerManagerActor("discord:" + userId, "[Discord] Alice", "discord");
            value.Fields["source"] = "discord";
            value.Fields["text"] = text;
            value.Fields["message"] = "[Discord] Alice (ID: " + userId + "): " + text;
            return value;
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("chat.shout"), http, delegate { }))
        {
            ServerManagerEvent value = DiscordMessage("hello");
            Check(!webhooks.Enqueue(value), "Existing game shout routes do not opt into Discord echoes.");
            Check(webhooks.Reload(Settings("discord.shout")), "Discord shout selection can be enabled live.");
            Check(!webhooks.Enqueue(Event("chat.shout")), "Discord-only routes exclude game shouts.");
            Check(webhooks.Enqueue(value) && !webhooks.Enqueue(value), "One accepted Discord event queues once, with event-ID deduplication.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await WaitFor(() => handler.Requests.Count == 1 && CurrentInFlight(webhooks) == 0);
            JObject payload = JObject.Parse(handler.Requests[0].Body!);
            CheckPlainShoutPayload(payload, null, "Discord chat");
            Check(((string)payload["content"]!).Replace("\\", "") == "[Discord] Alice: hello" &&
                !payload.ToString().Contains(userId), "Discord chat preserves the author and text without the local audit ID.");
            Check(value.Fields["message"].Contains(userId), "Webhook projection does not mutate the local audit event.");

            foreach (string prefix in new[] { "", "Guest" })
            {
                Check(webhooks.Reload(AnonymousSettings(prefix, "discord.shout")), "Discord anonymity can be reloaded.");
                foreach (string blank in new[] { "", " \r\n\t" })
                    Check(!webhooks.Enqueue(DiscordMessage(blank)), "Empty Discord text cannot fall back to the ID-bearing audit line.");
                ServerManagerEvent missing = DiscordMessage("private fallback");
                missing.Fields.Remove("text");
                Check(!webhooks.Enqueue(missing), "Missing Discord text cannot expose the audit line.");
            }
            Check(webhooks.Enqueue(DiscordMessage("hello again")), "Anonymous Discord text is accepted.");
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == 2, "Only selected, nonempty, unique Discord events were delivered.");
            JObject anonymous = JObject.Parse(handler.Requests[1].Body!);
            CheckPlainShoutPayload(anonymous, "Guest player: hello again", "Discord without a verified Steam account");
            Check(!anonymous.ToString().Contains("Alice") && !anonymous.ToString().Contains(userId),
                "Anonymous Discord text exposes neither the author nor the Discord ID.");
        }
    }

    private static void CheckPlainShoutPayload(JObject payload, string? expected, string label)
    {
        Check(payload["content"]?.Type == JTokenType.String && payload["embeds"] == null &&
            (int?)payload["flags"] == 4, "Shout uses content plus suppressed link previews, never embeds: " + label);
        Check(payload.Properties().All(property => new[] { "content", "flags", "allowed_mentions", "username", "avatar_url" }.Contains(property.Name)),
            "Shout payload contains no title, fields, footer, timestamp or other event metadata: " + label);
        Check(((JArray)payload["allowed_mentions"]!["parse"]!).Count == 0 &&
            ((JArray)payload["allowed_mentions"]!["users"]!).Count == 0 &&
            ((JArray)payload["allowed_mentions"]!["roles"]!).Count == 0 &&
            (bool?)payload["allowed_mentions"]!["replied_user"] == false, "Plain shout mention parsing stays disabled: " + label);
        if (expected != null) Check((string?)payload["content"] == expected, "Plain shout content is exactly the canonical name/message: " + label);
    }

    private static async Task AnonymousRouteProjection()
    {
        // The normal-only dispatcher is a byte-level oracle for legacy routes.
        // Reversing route order catches either projection mutating the event or
        // a rendered object and contaminating the other route.
        foreach (bool anonymousFirst in new[] { true, false })
        {
            string[] kinds = SourceEventKinds.OrderBy(kind => kind, StringComparer.Ordinal).ToArray();
            Check(kinds.Length == 22, "Anonymous projection still covers all 22 source kinds behind 16 webhook filters.");
            DiscordSettings paired = Settings(kinds);
            DiscordWebhookRoute anonymous = new DiscordWebhookRoute
            {
                Name = "Public", Url = ReloadedWebhook, AnonymousPrefix = "Guest",
                Username = "Public relay", AvatarUrl = "https://example.test/public.png",
                Events = new HashSet<string>(kinds.Select(ExpectedWebhookFilter), StringComparer.Ordinal)
            };
            if (anonymousFirst) paired.WebhookRoutes.Insert(0, anonymous);
            else paired.WebhookRoutes.Add(anonymous);
            using (FakeHandler handler = new FakeHandler())
            using (FakeHandler baselineHandler = new FakeHandler())
            using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
            using (DiscordHttp baselineHttp = new DiscordHttp("", delegate { }, baselineHandler))
            using (DiscordWebhooks webhooks = new DiscordWebhooks(paired, http, delegate { }))
            using (DiscordWebhooks baseline = new DiscordWebhooks(Settings(kinds), baselineHttp, delegate { }))
            {
                foreach (string kind in kinds)
                {
                    ServerManagerEvent value = AnonymousEvent(kind);
                    string original = EventFingerprint(value);
                    Check(baseline.Enqueue(value) && webhooks.Enqueue(value), "Both projections admit public kind " + kind);
                    Check(!webhooks.Enqueue(value), "Paired routes keep event deduplication: " + kind);
                    Check(EventFingerprint(value) == original, "Rendering does not change actor, target or event fields: " + kind);
                }
                Task run = webhooks.RunAsync(CancellationToken.None);
                Task baselineRun = baseline.RunAsync(CancellationToken.None);
                await webhooks.StopAsync(TimeSpan.FromSeconds(3));
                await baseline.StopAsync(TimeSpan.FromSeconds(3));
                await Task.WhenAll(run, baselineRun);
                RequestRecord[] publicRequests = handler.Requests.Where(request => request.Url.StartsWith(ReloadedWebhook, StringComparison.Ordinal)).ToArray();
                RequestRecord[] adminRequests = handler.Requests.Where(request => request.Url.StartsWith(Webhook, StringComparison.Ordinal)).ToArray();
                Check(publicRequests.Length == kinds.Length && adminRequests.Length == kinds.Length && baselineHandler.Requests.Count == kinds.Length,
                    "Every public event reaches exactly its anonymous and normal destinations.");
                for (int i = 0; i < kinds.Length; i++)
                {
                    Check(adminRequests[i].Body == baselineHandler.Requests[i].Body, "Normal route JSON remains byte-for-byte identical: " + kinds[i]);
                    JObject payload = JObject.Parse(publicRequests[i].Body!);
                    string body = payload.ToString();
                    foreach (string secret in new[] { "SECRET", "76561198000000001", "76561198000000002", "steamworks:", "9223372036854775001" })
                        Check(!body.Contains(secret), "Anonymous payload excludes raw metadata anywhere in JSON: " + kinds[i] + "/" + secret);
                    Check((string?)payload["username"] == "Public relay" && (string?)payload["avatar_url"] == "https://example.test/public.png",
                        "Anonymous projection retains route branding: " + kinds[i]);
                    Check(((JArray)payload["allowed_mentions"]!["parse"]!).Count == 0 &&
                        ((JArray)payload["allowed_mentions"]!["users"]!).Count == 0 &&
                        ((JArray)payload["allowed_mentions"]!["roles"]!).Count == 0 &&
                        (bool?)payload["allowed_mentions"]!["replied_user"] == false && publicRequests[i].Auth == null,
                        "Anonymous route keeps disabled mentions and token-free transport: " + kinds[i]);
                    if (kinds[i] == "chat.shout")
                    {
                        CheckPlainShoutPayload(payload, null, "anonymous shout");
                        string content = (string)payload["content"]!;
                        Check(content.Contains("Guest") && content.EndsWith(": public words", StringComparison.Ordinal),
                            "Anonymous shout composes the verified alias with canonical text, never the original named message.");
                    }
                    else
                    {
                        Check(payload["content"] == null && payload["embeds"] is JArray &&
                            !string.IsNullOrWhiteSpace((string?)payload["embeds"]![0]!["title"]),
                            "Anonymous non-chat event has a useful compact title: " + kinds[i]);
                        foreach (JObject embed in (JArray)payload["embeds"]!) CheckCompactEmbed(embed, null, "*", kinds[i]);
                    }
                }
            }
        }
    }

    private static async Task SimplifiedCommandPrivacy()
    {
        string[] commands = { "players", "playerinfo", "skilladd", "skillreset" };
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(AnonymousSettings("Guest", "command.executed"), http, delegate { }))
        {
            foreach (string command in commands)
            {
                ServerManagerEvent value = AnonymousEvent("command.executed");
                value.Fields["command"] = command;
                Check(webhooks.Enqueue(value), "Historical command events remain safely renderable: " + command);
            }
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == commands.Length, "Command privacy does not change delivery counts.");
            for (int index = 0; index < commands.Length; ++index)
            {
                JObject payload = JObject.Parse(handler.Requests[index].Body!);
                string title = (string)payload["embeds"]![0]!["title"]!;
                Check(index == 0 ? title.StartsWith("Command: players", StringComparison.Ordinal) : !title.Contains(commands[index]),
                    "Anonymous command label recognizes players but not removed public names: " + commands[index]);
                Check(!payload.ToString().Contains("SECRET") && !payload.ToString().Contains("76561198000000001"),
                    "Command label simplification preserves anonymous identity projection.");
            }
        }
    }

    private static ServerManagerEvent AnonymousEvent(string kind)
    {
        ServerManagerEvent value = Event(kind);
        value.Actor = new ServerManagerActor("9223372036854775001", "SECRET_ACTOR_NAME", "player")
            { PlayerAccountId = "steamworks:76561198000000001" };
        value.Target = new ServerManagerActor("steamworks:76561198000000002", "SECRET_TARGET_NAME", "player")
            { PlayerAccountId = "steamworks:76561198000000002" };
        value.Reliability = "SECRET_RELIABILITY";
        foreach (string key in new[] { "message", "title", "name", "character", "player_name", "target", "target_name",
            "id", "player_id", "account_id", "source", "identity_status", "reason_code", "outcome", "evidence",
            "response", "category", "stage", "revision", "count", "duration", "detail", "finding", "command",
            "arguments", "plugin_details", "plugins", "raid", "boss", "cause", "coordinates", "raid_coordinates",
            "inventory", "token", "SECRET_FIELD_NAME" }) value.Fields[key] = "SECRET_" + key.ToUpperInvariant();
        value.Fields["text"] = "public words";
        if (kind == "raid.started" || kind == "raid.ended") value.Fields["raid"] = "army_bonemass";
        if (kind == "boss.killed")
        {
            value.Target = new ServerManagerActor("NONPLAYER_BOSS_ID", "Eikthyr", "boss");
            value.Fields["boss"] = "Eikthyr";
        }
        if (kind == "server.announcement") value.Fields["message"] = "Operator-written public announcement";
        return value;
    }

    private static string EventFingerprint(ServerManagerEvent value)
        => string.Join("|", new[] { value.EventId, value.Kind, value.Reliability,
            value.Actor?.Id, value.Actor?.Name, value.Actor?.Kind, value.Actor?.PlayerAccountId,
            value.Target?.Id, value.Target?.Name, value.Target?.Kind, value.Target?.PlayerAccountId }) +
            string.Join("|", value.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));

    private static async Task AnonymousAliasLifetimeAndBounds()
    {
        ServerManagerActor first = AnonymousActor("steamworks:76561198000000001", "76561198000000001", "SECRET_REMOTE");
        ServerManagerActor reconnect = AnonymousActor(first.PlayerAccountId, "0", "SECRET_RENAMED_HOST");
        ServerManagerActor second = AnonymousActor("steamworks:76561198000000002", first.Id, first.Name);
        ServerManagerActor unverified = AnonymousActor("", first.PlayerAccountId, "SECRET_UNKNOWN");
        DiscordSettings settings = AnonymousSettings("Guest", "chat.shout", "combat.pvp_kill", "raid.started", "server.announcement");
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
        {
            Task run = webhooks.RunAsync(CancellationToken.None);
            foreach (var pair in new[] { Tuple.Create(first, "Guest 1"), Tuple.Create(reconnect, "Guest 1"),
                Tuple.Create(second, "Guest 2"), Tuple.Create(unverified, "Guest player") })
            {
                int expected = handler.Requests.Count + 1;
                Check(webhooks.Enqueue(AnonymousShout(pair.Item1, "hello")), "Anonymous dispatcher admits a verified or generic shout.");
                await WaitFor(() => handler.Requests.Count == expected && CurrentInFlight(webhooks) == 0);
                CheckPlainShoutPayload(JObject.Parse(handler.Requests.Last().Body!), pair.Item2 + ": hello", "account-backed alias");
            }
            foreach (string? blank in new string?[] { null, "", " \r\n\t", "\0\r" })
            {
                ServerManagerEvent invalid = AnonymousShout(first, "ignored");
                if (blank == null) invalid.Fields.Remove("text"); else invalid.Fields["text"] = blank;
                invalid.Fields["message"] = "SECRET_NAME: valid named message";
                Check(!webhooks.Enqueue(invalid), "Anonymous shout rejects missing/blank canonical text without falling back to named message.");
            }
            ServerManagerEvent pvp = AnonymousEvent("combat.pvp_kill");
            pvp.Actor = first; pvp.Target = unverified;
            Check(webhooks.Enqueue(pvp), "Anonymous PvP event with a name-only attacker is admissible.");
            await WaitFor(() => handler.Requests.Count == 5 && CurrentInFlight(webhooks) == 0);
            string pvpBody = JObject.Parse(handler.Requests.Last().Body!).ToString();
            Check(pvpBody.Contains("Guest 1") && pvpBody.Contains("Guest player") && !pvpBody.Contains("SECRET") &&
                !pvpBody.Contains("76561198"), "PvP uses the known victim alias and a generic attacker without guessing from raw ID or name.");
            string explicitPublic = "I am SECRET_REMOTE steamworks:76561198000000001 **bold** @everyone <@12345> \"quoted\"\0\r\nnext";
            Check(webhooks.Enqueue(AnonymousShout(first, explicitPublic)), "Anonymous chat admits user-written free text including deliberate self-identification.");
            Check(webhooks.Enqueue(AnonymousShout(first, new string('x', 1989) + "\ud83d\ude03" + new string('z', 3000))), "Anonymous chat admits bounded long text.");
            await WaitFor(() => handler.Requests.Count == 7 && CurrentInFlight(webhooks) == 0);
            string escaped = (string)JObject.Parse(handler.Requests[5].Body!)["content"]!;
            Check(escaped.StartsWith("Guest 1: I am SECRET\\_REMOTE steamworks:76561198000000001", StringComparison.Ordinal) &&
                escaped.Contains("\\*\\*bold\\*\\*") && escaped.Contains("\"quoted\"") && escaped.Contains("\nnext") &&
                !escaped.Contains("@everyone") && !escaped.Contains("<@12345>") && !escaped.Contains("\0"),
                "Anonymous text preserves explicit public self-disclosure while escaping markdown, mentions and controls.");
            string bounded = (string)JObject.Parse(handler.Requests[6].Body!)["content"]!;
            Check(bounded.Length <= 2000 && bounded.StartsWith("Guest 1: ", StringComparison.Ordinal) &&
                new UTF8Encoding(false, true).GetByteCount(bounded) > 0 && bounded.EndsWith("…", StringComparison.Ordinal),
                "Anonymous alias plus text shares the 2000-character content budget without splitting surrogate pairs.");
            ServerManagerEvent raid = AnonymousEvent("raid.started");
            raid.Reliability = "authoritative"; raid.Fields["raid_coordinates"] = "-371, 40, 1591";
            Check(webhooks.Enqueue(raid), "Anonymous authoritative raid projection is admitted.");
            ServerManagerEvent untrustedRaid = AnonymousEvent("raid.started");
            untrustedRaid.Reliability = "client_reported"; untrustedRaid.Fields["raid_coordinates"] = "-371, 40, 1591";
            Check(webhooks.Enqueue(untrustedRaid), "Anonymous observed raid projection is admitted without coordinate authority.");
            ServerManagerEvent announcement = AnonymousEvent("server.announcement");
            announcement.Fields["message"] = "Public notice: SECRET_SELF_DISCLOSURE @everyone";
            Check(webhooks.Enqueue(announcement), "Anonymous route admits the operator-authored public announcement body.");
            await WaitFor(() => handler.Requests.Count == 10 && CurrentInFlight(webhooks) == 0);
            Check(handler.Requests[7].Body!.Contains("-371, 40, 1591") && !handler.Requests[8].Body!.Contains("-371, 40, 1591"),
                "Anonymous raid projection preserves only the existing authoritative coordinate exception.");
            string announcementBody = JObject.Parse(handler.Requests[9].Body!).ToString();
            Check(announcementBody.Replace("\\", "").Contains("SECRET_SELF_DISCLOSURE") &&
                !announcementBody.Contains("@everyone") && !announcementBody.Contains("SECRET_ACTOR") && !announcementBody.Contains("SECRET_TARGET"),
                "Public announcements keep their explicit free text but omit attributed actor and target metadata.");
            Check(webhooks.Reload(AnonymousSettings("Visitor", "chat.shout")), "Prefix hot reload succeeds.");
            Check(webhooks.Enqueue(AnonymousShout(reconnect, "after reload")), "Same account reconnects after prefix reload.");
            await WaitFor(() => handler.Requests.Count == 11 && CurrentInFlight(webhooks) == 0);
            CheckPlainShoutPayload(JObject.Parse(handler.Requests[10].Body!), "Visitor 1: after reload", "prefix-only hot reload");
            using (DiscordWebhooks replacement = new DiscordWebhooks(AnonymousSettings("Next", "chat.shout"), http, delegate { }))
            {
                replacement.InheritRecentOperatorState(webhooks);
                Check(replacement.Enqueue(AnonymousShout(second, "inherited")) && replacement.Enqueue(AnonymousShout(
                    AnonymousActor("steamworks:76561198000000003", "1", "SECRET_THIRD"), "new account")),
                    "Replacement dispatcher accepts inherited and newly encountered accounts.");
                Task replacementRun = replacement.RunAsync(CancellationToken.None);
                await replacement.StopAsync(TimeSpan.FromSeconds(3)); await replacementRun;
                CheckPlainShoutPayload(JObject.Parse(handler.Requests[11].Body!), "Next 2: inherited", "same-world dispatcher replacement");
                CheckPlainShoutPayload(JObject.Parse(handler.Requests[12].Body!), "Next 3: new account", "unknown participants did not allocate numbers");
            }
            using (DiscordWebhooks fresh = new DiscordWebhooks(AnonymousSettings("Fresh", "chat.shout"), http, delegate { }))
            {
                Check(fresh.Enqueue(AnonymousShout(second, "new world")), "Independent dispatcher accepts a new-world event.");
                Task freshRun = fresh.RunAsync(CancellationToken.None);
                await fresh.StopAsync(TimeSpan.FromSeconds(3)); await freshRun;
                CheckPlainShoutPayload(JObject.Parse(handler.Requests[13].Body!), "Fresh 1: new world", "new world resets non-persistent alias mapping");
            }
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
        }
        // Rendering is route-specific, including malformed chat: neither route
        // may suppress a valid payload for the other destination.
        DiscordSettings paired = AnonymousSettings("Guest", "chat.shout");
        paired.WebhookRoutes.Add(new DiscordWebhookRoute { Name = "Normal", Url = ReloadedWebhook,
            Events = new HashSet<string> { "chat.shout" } });
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(paired, http, delegate { }))
        {
            ServerManagerEvent namedOnly = AnonymousShout(first, "ignored"); namedOnly.Fields.Remove("text");
            namedOnly.Fields["message"] = "Original name: legacy body";
            ServerManagerEvent anonymousOnly = AnonymousShout(first, "anonymous body"); anonymousOnly.Fields.Remove("message");
            Check(webhooks.Enqueue(namedOnly) && webhooks.Enqueue(anonymousOnly), "Per-route validation does not suppress the other route's valid shout.");
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == 2 && handler.Requests[0].Url.StartsWith(ReloadedWebhook, StringComparison.Ordinal) &&
                handler.Requests[1].Url.StartsWith(Webhook, StringComparison.Ordinal), "Missing text drops only anonymous delivery; missing message drops only normal delivery.");
            CheckPlainShoutPayload(JObject.Parse(handler.Requests[0].Body!), "Original name: legacy body", "legacy-only payload");
            CheckPlainShoutPayload(JObject.Parse(handler.Requests[1].Body!), "Guest 1: anonymous body", "anonymous-only payload");
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(AnonymousSettings("Guest", "chat.shout"), http, delegate { }))
        {
            for (int i = 0; i < 256; i++) Check(webhooks.Enqueue(AnonymousShout(first, "queued " + i)), "Anonymous queue accepts its bounded capacity.");
            Check(!webhooks.Enqueue(AnonymousShout(first, "overflow")), "Anonymous projection cannot bypass the fixed queue capacity.");
            Check(webhooks.Reload(AnonymousSettings("Changed", "chat.shout")) && webhooks.Enqueue(AnonymousShout(reconnect, "after clear")),
                "Reload drops anonymous queued content while retaining the account mapping.");
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == 1, "Reloaded anonymous queue does not replay stale work.");
            CheckPlainShoutPayload(JObject.Parse(handler.Requests[0].Body!), "Changed 1: after clear", "queue reload alias preservation");
        }
    }

    private static DiscordSettings AnonymousSettings(string prefix, params string[] kinds)
    {
        DiscordSettings settings = Settings(kinds);
        settings.WebhookRoutes[0].AnonymousPrefix = prefix;
        return settings;
    }

    private static async Task AnonymousSafeSummaryAndCapacity()
    {
        var choiceCases = new List<Tuple<string, string, string, string, int>>();
        string[] outcomes = { "observed", "diagnostic", "response_requested", "processing_failed", "banlist_add_returned",
            "banlist_add_failed", "kick_call_returned", "disconnect_fallback_scheduled", "bypassed", "would_reject",
            "save_rejected", "rejected", "client_aborted" };
        string[] responses = { "Off", "Log", "Kick", "Ban" };
        for (int i = 0; i < outcomes.Length; i++)
            choiceCases.Add(Tuple.Create("security.response", "SECRET_ACTION", responses[i % responses.Length], outcomes[i], 2));
        foreach (string action in new[] { "kick", "ban", "unban" })
            choiceCases.Add(Tuple.Create("moderation.action", action, "SECRET_RESPONSE", "SECRET_OUTCOME", 1));
        foreach (string invalid in new[] { "SECRET_TOKEN", "KICK", "kick SECRET_NAME" })
            choiceCases.Add(Tuple.Create("security.response", invalid, invalid, invalid, 0));
        choiceCases.Add(Tuple.Create("server.ready", "kick", "Kick", "observed", 0));
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(AnonymousSettings("Guest", "security.response", "moderation.action", "server.ready"), http, delegate { }))
        {
            for (int i = 0; i < choiceCases.Count; i++)
            {
                var test = choiceCases[i]; ServerManagerEvent value = AnonymousEvent(test.Item1);
                value.Fields["action"] = test.Item2; value.Fields["response"] = test.Item3;
                value.Fields["outcome"] = test.Item4; value.Fields["reason_code"] = "SECRET_FIXTURE_" + i;
                Check(webhooks.Enqueue(value), "Anonymous finite action/outcome case is admitted.");
            }
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == choiceCases.Count, "Every finite anonymous action/outcome case is transmitted once.");
            for (int i = 0; i < choiceCases.Count; i++)
            {
                var test = choiceCases[i]; JObject payload = JObject.Parse(handler.Requests[i].Body!);
                JObject embed = (JObject)payload["embeds"]![0]!;
                CheckCompactEmbed(embed, null, "*", "finite action/outcome");
                string body = payload.ToString().Replace("\\", "");
                Check(!body.Contains("SECRET"),
                    "Anonymous actions and outcomes use a finite per-event allowlist, never arbitrary token-like detail.");
                if (test.Item5 == 1) Check(body.IndexOf(test.Item2, StringComparison.OrdinalIgnoreCase) >= 0,
                    "Known moderation action remains visible in concise anonymous text.");
            }
        }
        string[] scope = { "world_disk_and_retained_characters", "world_disk_with_partial_retained_characters", "world_disk_only" };
        string[] commits = { "all_retained_shadows_at_cutoff", "partial_retained_shadows_at_cutoff", "not_included" };
        string[] counts = { "0", "1", "2147483647" };
        string[] countKeys = { "captured_character_count", "persisted_character_count", "pending_character_count" };
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(AnonymousSettings("Guest", "server.saved"), http, delegate { }))
        {
            for (int i = 0; i < scope.Length; i++)
            {
                ServerManagerEvent value = AnonymousEvent("server.saved");
                value.Fields["completion_scope"] = scope[i]; value.Fields["character_commit_scope"] = commits[i];
                foreach (string key in countKeys) value.Fields[key] = counts[i];
                Check(webhooks.Enqueue(value), "Anonymous save summary accepts finite public scope and count values.");
            }
            foreach (string invalid in new[] { "-1", "+1", "1.0", "2147483648", " 1", "1 ", "NaN", "76561198000000001", "SECRET_VALUE" })
            {
                ServerManagerEvent value = AnonymousEvent("server.saved");
                value.Fields["completion_scope"] = "SECRET_SCOPE"; value.Fields["character_commit_scope"] = "SECRET_COMMIT";
                foreach (string key in countKeys) value.Fields[key] = invalid;
                Check(webhooks.Enqueue(value), "Invalid anonymous summary fields do not discard the safe event template.");
            }
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == 12, "Anonymous save summary sends all valid and invalid-metadata cases.");
            for (int i = 0; i < handler.Requests.Count; i++)
            {
                JObject payload = JObject.Parse(handler.Requests[i].Body!);
                JToken embed = payload["embeds"]![0]!;
                Check(!payload.ToString().Contains("SECRET"), "Anonymous save summary never copies raw identity, field names or diagnostics.");
                CheckCompactEmbed((JObject)embed, "World saved", null, "anonymous world save compact primary");
                Check(((JArray)payload["embeds"]!).Count == 1,
                    "Untrusted save summaries cannot add a partial character warning, even with plausible count fields.");
            }
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(AnonymousSettings("Guest", "chat.shout"), http, delegate { }))
        {
            Func<int, ServerManagerActor> account = index => AnonymousActor("steamworks:" +
                (76561198000010000UL + (ulong)index).ToString(System.Globalization.CultureInfo.InvariantCulture), "0", "SECRET_NAME");
            bool admitted = true;
            for (int i = 0; i < 4096; i++)
            {
                admitted &= webhooks.Enqueue(AnonymousShout(account(i), "discarded"));
                if ((i + 1) % 256 == 0) admitted &= webhooks.Reload(AnonymousSettings("Guest", "chat.shout"));
            }
            Check(admitted && handler.Requests.Count == 0, "All 4096 aliases fit without sending the reload-discarded queue.");
            Check(webhooks.Enqueue(AnonymousShout(account(4095), "last allocated")) &&
                webhooks.Enqueue(AnonymousShout(account(4096), "over limit")) &&
                webhooks.Enqueue(AnonymousShout(account(0), "first retained")), "Alias capacity keeps known accounts and safely admits generic overflow.");
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            Check(handler.Requests.Count == 3, "Only the final bounded alias-capacity probes are transmitted.");
            CheckPlainShoutPayload(JObject.Parse(handler.Requests[0].Body!), "Guest 4096: last allocated", "last allocated alias");
            CheckPlainShoutPayload(JObject.Parse(handler.Requests[1].Body!), "Guest player: over limit", "full alias map fallback");
            CheckPlainShoutPayload(JObject.Parse(handler.Requests[2].Body!), "Guest 1: first retained", "full map never evicts or reuses an old number");
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(AnonymousSettings("**@everyone**", "chat.shout"), http, delegate { }))
        {
            Check(webhooks.Enqueue(AnonymousShout(AnonymousActor("steamworks:76561198000000001", "0", "SECRET_NAME"), "hello")),
                "Printable markdown and mention characters are valid custom alias prefixes.");
            Task run = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await run;
            string content = (string)JObject.Parse(handler.Requests.Single().Body!)["content"]!;
            Check(content == "\\*\\*@\u200beveryone\\*\\* 1: hello", "Alias prefix itself is escaped and cannot enable mentions or markdown formatting.");
        }
    }

    private static ServerManagerActor AnonymousActor(string account, string id, string name)
        => new ServerManagerActor(id, name, "player") { PlayerAccountId = account };

    private static ServerManagerEvent AnonymousShout(ServerManagerActor actor, string text)
    {
        ServerManagerEvent value = Event("chat.shout"); value.Actor = actor;
        value.Fields["message"] = "SECRET_NAMED_MESSAGE"; value.Fields["text"] = text;
        return value;
    }

    private static async Task RaidCoordinatePrivacy()
    {
        // A non-English process culture must not broaden the server-only,
        // invariant Int32 representation accepted by the renderer.
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            foreach (string kind in new[] { "raid.started", "raid.ended" })
            {
                foreach (string coordinates in new[] { "-371, 40, 1591", "0, 0, 0",
                    "-2147483648, 2147483647, -2147483648" })
                    await RaidCoordinateCase(kind, "authoritative", "raid_coordinates", coordinates, true);

                foreach (string coordinates in new[] { "NaN, 2, 3", "Infinity, 2, 3", "-Infinity, 2, 3",
                    "2147483648, 2, 3", "-2147483649, 2, 3", "1.0, 2, 3", "1,5, 2, 3", "1e2, 2, 3",
                    "1, 2", "1, 2, 3, 4", "1; 2; 3", "1,2,3", "1,  2, 3", "+1, 2, 3",
                    "01, 2, 3", "-0, 2, 3", " 1, 2, 3", "1, 2, 3 ", "[1, 2, 3]", "1, 2, 3x" })
                    await RaidCoordinateCase(kind, "authoritative", "raid_coordinates", coordinates, false);

                foreach (string reliability in new[] { "observed", "client_reported", "Authoritative", "" })
                    await RaidCoordinateCase(kind, reliability, "raid_coordinates", "-371, 40, 1591", false);
                foreach (string key in new[] { "Raid_Coordinates", "raidCoordinates", "coordinates", "world_position" })
                    await RaidCoordinateCase(kind, "authoritative", key, "-371, 40, 1591", false);
            }
            foreach (string kind in new[] { "server.ready", "chat.shout", "player.death" })
                await RaidCoordinateCase(kind, "authoritative", "raid_coordinates", "-371, 40, 1591", false);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = originalCulture; }
    }

    private static async Task RaidCoordinateCase(string kind, string reliability, string coordinateKey,
        string coordinates, bool allowed)
    {
        string label = kind + "/" + reliability + "/" + coordinateKey + "=" + coordinates;
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings(kind), http, delegate { }))
        {
            ServerManagerEvent value = Event(kind);
            value.Reliability = reliability;
            value.Fields[coordinateKey] = coordinates;
            value.Fields["title"] = "Raid fixture at [" + coordinates + "]";
            value.Fields["message"] = "Raid fixture at [" + coordinates + "]. " +
                "901, 902, 903 PRIVATEINVENTORY PRIVATECREDENTIAL";
            value.Fields["summary"] = "Center [" + coordinates + "]";
            value.Fields["player_position"] = "901, 902, 903";
            value.Fields["inventory_items"] = "PRIVATEINVENTORY";
            value.Fields["bot_token"] = "PRIVATECREDENTIAL";
            Check(webhooks.Enqueue(value), "Selected event remains routable regardless of coordinate validity: " + label);
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            Check(handler.Requests.Count == 1, "Coordinate case sends exactly once: " + label);
            JObject payload = JObject.Parse(handler.Requests.Single().Body!);
            if (kind == "chat.shout")
            {
                CheckPlainShoutPayload(payload, null, label);
                string plain = ((string)payload["content"]!).Replace("\\", "");
                Check(!plain.Contains(coordinates) && plain.Contains("[redacted]") &&
                    !plain.Contains("901, 902, 903") && !plain.Contains("PRIVATEINVENTORY") && !plain.Contains("PRIVATECREDENTIAL") &&
                    handler.Requests[0].Auth == null, "Plain shout preserves duplicated-sensitive-value redaction and webhook-only authentication: " + label);
                return;
            }
            JObject embed = (JObject)payload["embeds"]![0]!;
            Dictionary<string, string> fields = EmbedFields(embed);
            string title = ((string)embed["title"]!).Replace("\\", "");
            string description = ((string?)embed["description"] ?? "").Replace("\\", "");
            if (allowed)
            {
                Check(fields.Count == 0 && title.Contains(coordinates) && description.Length == 0,
                    "Verified raid center appears in the single title with no duplicate body: " + label);
                Check(title.IndexOf(coordinates, StringComparison.Ordinal) == title.LastIndexOf(coordinates, StringComparison.Ordinal),
                    "Verified raid center is never duplicated: " + label);
            }
            else
            {
                Check(!fields.ContainsKey(coordinateKey) && !fields.ContainsKey("raid_coordinates"),
                    "Unverified or malformed center field is excluded: " + label);
                Check(!title.Contains(coordinates) && !description.Contains(coordinates),
                    "Denied center cannot be copied through raw title, message or summary fields: " + label);
            }
            string body = payload.ToString();
            Check(!fields.ContainsKey("player_position") && !fields.ContainsKey("inventory_items") && !fields.ContainsKey("bot_token")
                && !body.Contains("901, 902, 903") && !body.Contains("PRIVATEINVENTORY") && !body.Contains("PRIVATECREDENTIAL"),
                "Raid exception never exposes other coordinates, inventory or credentials: " + label);
            Check(((JArray)payload["allowed_mentions"]!["parse"]!).Count == 0 && handler.Requests[0].Auth == null,
                "Raid payload retains mention and webhook-auth protections: " + label);
        }
    }

    private static async Task OperatorSummariesAndLimits()
    {
        foreach (string prefix in new[] { "", "Anonymous" })
        {
            DiscordSettings settings = Settings("character.revision_observed");
            settings.WebhookRoutes[0].AnonymousPrefix = prefix;
            using (FakeHandler handler = new FakeHandler())
            using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
            using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
            {
                ServerManagerEvent value = OperatorEvent("character.revision_observed");
                value.Fields["reason_code"] = "used_cheats";
                value.Fields.Remove("evidence");
                Check(webhooks.Enqueue(value), "Used-cheats achievement observation remains routable.");
                Task running = webhooks.RunAsync(CancellationToken.None);
                await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
                string body = handler.Requests[0].Body!;
                Check(body.Contains("Profile used-cheats flag") && !body.Contains("Forbidden item"),
                    "Named and anonymous used-cheats observations are distinct from forbidden-item bypasses.");
            }
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("moderation.action"), http, delegate { }))
        {
            foreach (string kind in OperatorKinds)
                Check(!webhooks.Enqueue(OperatorEvent(kind)), "Operator kinds require their own explicit selection: " + kind);
            Check(handler.Requests.Count == 0, "No implicit operator delivery or network work.");
        }
        {
            DiscordSettings settings = Settings(OperatorKinds);
            using (FakeHandler handler = new FakeHandler())
            using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
            using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
            {
                foreach (string kind in OperatorKinds)
                {
                    ServerManagerEvent value = OperatorEvent(kind);
                    foreach (string key in new[] { "title", "message", "detail", "finding", "session", "session_id", "raw_payload",
                        "ip", "stack", "token", "coordinates", "inventory", "plugins", "sha256", "expected_sha256", "reported_sha256", "unknown" })
                        value.Fields[key] = "RAW_PRIVATE_SENTINEL " + key + " @everyone";
                    value.Fields["plugin_details"] = "Example Mod (example.mod): validation.hash_not_allowed";
                    Check(webhooks.Enqueue(value), "Exact operator kind admitted: " + kind);
                }
                Task running = webhooks.RunAsync(CancellationToken.None);
                await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
                Check(handler.Requests.Count == 8, "Each opted-in operator kind sends once through the offline handler.");
                for (int index = 0; index < OperatorKinds.Length; ++index)
                {
                    string kind = OperatorKinds[index];
                    JObject payload = JObject.Parse(handler.Requests[index].Body!);
                    JObject embed = (JObject)payload["embeds"]![0]!;
                    Dictionary<string, string> fields = EmbedFields(embed);
                    string body = payload.ToString();
                    Check(!body.Contains("RAW_PRIVATE_SENTINEL") && !body.Contains("@everyone"),
                        "Projection never copies raw diagnostics, SHA values, coordinates or inventory: " + kind);
                    Check(((JArray)payload["allowed_mentions"]!["parse"]!).Count == 0,
                        "Operator mentions remain disabled: " + kind);
                    CheckCompactEmbed(embed, null, "*", kind);
                    Check(body.Contains("Player") && !body.Contains("steamworks:") && !body.Contains("76561198000000001"),
                        "Concise named summary keeps the authenticated player name without redundant raw account IDs: " + kind);
                    Check(body.Contains("Example Mod") == (kind == "connection.rejected"),
                        "Only connection rejection includes actionable plugin mismatch details: " + kind);
                    Check(fields.Count == 0, "Operator metadata is projected into concise text instead of mechanically expanded fields.");
                }
            }
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("connection.rejected", "security.detection"), http, delegate { }))
        {
            ServerManagerEvent unauthenticated = OperatorEvent("connection.rejected");
            unauthenticated.Fields["identity_status"] = "unverified";
            Check(webhooks.Enqueue(unauthenticated), "Unauthenticated rejection still has an operator summary.");
            for (int index = 2; index < 80; ++index)
            {
                ServerManagerEvent spoof = OperatorEvent("connection.rejected", index);
                spoof.Fields["identity_status"] = "unverified";
                Check(!webhooks.Enqueue(spoof), "Changing claimed account/attempt identity cannot evade repeat limit.");
            }
            ServerManagerEvent malformed = OperatorEvent("security.detection");
            foreach (string key in new[] { "source", "reason_code", "outcome", "evidence", "response" })
                malformed.Fields[key] = "PRIVATE FREETEXT SENTINEL";
            malformed.Fields["account_id"] = "192.0.2.42";
            malformed.Reliability = "PRIVATE FREETEXT SENTINEL";
            Check(webhooks.Enqueue(malformed), "Malformed structured values produce a minimal fixed summary.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
            string unverifiedBody = JObject.Parse(handler.Requests[0].Body!).ToString();
            Check(!unverifiedBody.Contains("76561198000000001") && !unverifiedBody.Contains("@everyone") &&
                !unverifiedBody.Contains("Player @"), "Unverified identities are never displayed as authenticated actors.");
            string body = handler.Requests[1].Body!;
            Check(!body.Contains("PRIVATE") && !body.Contains("192.0.2.42"),
                "Invalid token/account/reliability fields cannot copy arbitrary diagnostic text.");
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("security.response"), http, delegate { }))
        {
            Check(webhooks.Enqueue(OperatorEvent("security.response")), "First response summary accepted.");
            Check(!webhooks.Enqueue(OperatorEvent("security.response")), "New event IDs cannot replay the same kind/account/reason/outcome.");
            ServerManagerEvent failure = OperatorEvent("security.response"); failure.Fields["outcome"] = "banlist_add_failed";
            Check(webhooks.Enqueue(failure), "A distinct actual failure outcome is not hidden by the repeat guard.");
            Check(webhooks.Reload(Settings("security.response")) && !webhooks.Enqueue(OperatorEvent("security.response")),
                "Route reload does not reset operator repeat history.");
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("connection.rejected", "server.ready"), http, delegate { }))
        {
            int accepted = 0;
            Parallel.For(1, 201, index => { if (webhooks.Enqueue(OperatorEvent("connection.rejected", index))) Interlocked.Increment(ref accepted); });
            Check(accepted == 20, "Operator global bound is atomic and limits distinct reconnect summaries to 20/minute.");
            Check(webhooks.Enqueue(Event("server.ready")), "Operator flood does not consume an ordinary event's rate allowance.");
            Check(webhooks.Reload(Settings("connection.rejected", "server.ready")) &&
                !webhooks.Enqueue(OperatorEvent("connection.rejected", 999)), "Route reload does not reset the operator global bound.");
            using (DiscordWebhooks replacement = new DiscordWebhooks(Settings("connection.rejected"), http, delegate { }))
            {
                replacement.InheritRecentOperatorState(webhooks);
                Check(!replacement.Enqueue(OperatorEvent("connection.rejected", 999)), "Full session replacement inherits global operator rate history.");
                replacement.InheritRecentOperatorState(webhooks);
                Check(!replacement.Enqueue(OperatorEvent("connection.rejected", 999)), "Repeated inheritance does not reopen or double-count the window.");
            }
            AgeOperatorHistory(webhooks);
            Check(webhooks.Enqueue(OperatorEvent("connection.rejected", 999)), "Expired operator windows permit a new summary without sleeps.");
        }
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        {
            DiscordSettings settings = Settings("connection.rejected");
            using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
            {
                ServerManagerEvent plugins = OperatorEvent("connection.rejected");
                plugins.Fields["plugin_details"] = new string('p', 1400);
                Check(webhooks.Enqueue(plugins), "Long default plugin summary is bounded.");
                Task running = webhooks.RunAsync(CancellationToken.None);
                await webhooks.StopAsync(TimeSpan.FromSeconds(3)); await running;
                JObject embed = (JObject)JObject.Parse(handler.Requests[0].Body!)["embeds"]![0]!;
                string description = (string?)embed["description"] ?? "";
                Check(description.Length > 256 && description.Length <= 2500 && (description.Contains("truncated") || description.Contains("…")) && !description.Contains(new string('p', 1025)),
                    "Concise plugin mismatch details preserve their bounded explicit truncation marker.");
            }
        }
    }

    private static Dictionary<string, string> EmbedFields(JObject embed) => ((JArray?)embed["fields"] ?? new JArray())
        .ToDictionary(field => ((string)field["name"]!).Replace("\\", ""), field => ((string)field["value"]!).Replace("\\", ""));

    private static void AgeOperatorHistory(DiscordWebhooks webhooks)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var times = (Queue<long>)typeof(DiscordWebhooks).GetField("_operatorTimes", flags)!.GetValue(webhooks)!;
        var last = (Dictionary<string, long>)typeof(DiscordWebhooks).GetField("_operatorLast", flags)!.GetValue(webhooks)!;
        long expired = Stopwatch.GetTimestamp() - 61 * Stopwatch.Frequency;
        int count = times.Count; times.Clear(); for (int index = 0; index < count; ++index) times.Enqueue(expired);
        foreach (string key in last.Keys.ToArray()) last[key] = expired;
    }

    private static ServerManagerEvent OperatorEvent(string kind, int account = 1)
    {
        ServerManagerEvent value = Event(kind);
        value.Reliability = "client_reported";
        value.Fields = new Dictionary<string, string>
        {
            ["account_id"] = "steamworks:" + (76561198000000000L + account), ["character"] = "Player @everyone",
            ["identity_status"] = "authenticated", ["source"] = "client_reported", ["category"] = "mod_policy",
            ["stage"] = "ManifestValidation", ["reason_code"] = "validation.required_plugin_missing", ["outcome"] = "observed",
            ["evidence"] = "MaximumHealth", ["response"] = "Log", ["revision"] = "123"
        };
        return value;
    }

    private static async Task ExplicitWebhookTestRouting()
    {
        DiscordSettings settings = Settings("server.announcement");
        settings.WebhookRoutes.Add(new DiscordWebhookRoute
        {
            Name = "Unselected test route", Url = ReloadedWebhook,
            Events = new HashSet<string> { ExpectedWebhookFilter("server.ready") }
        });
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(settings, http, delegate { }))
        {
            ServerManagerEvent test = Event("server.announcement");
            test.Fields["title"] = "ServerManager Discord webhook TEST";
            test.Fields["message"] = "Explicit operator-requested webhook test. This is not a game announcement or player message.";
            test.Fields["test"] = "true";
            Check(webhooks.Enqueue(test), "Explicit test enters the existing bounded sender.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(handler.Requests.Count == 1 && handler.Requests[0].Url.StartsWith(Webhook, StringComparison.Ordinal),
                "Test routes only to configured exact server.announcement destinations.");
            string payload = handler.Requests[0].Body!;
            Check(payload.Contains("not a game announcement") && !payload.Contains("\"test\""),
                "Operator test keeps its explicit explanatory body without appending a redundant metadata field.");
            Check(handler.Requests[0].Auth == null, "Webhook test carries no bot authorization.");
        }
    }

    private static async Task QueueAndStop()
    {
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.saved"), http, delegate { }))
        {
            for (int i = 0; i < 256; i++) Check(webhooks.Enqueue(Event("server.saved")), "Queue accepts bounded capacity.");
            Check(!webhooks.Enqueue(Event("server.saved")), "Queue does not exceed 256 deliveries.");
        }
        using (FakeHandler handler = new FakeHandler((_, __) => Response(404)))
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.saved"), http, delegate { }))
        {
            webhooks.Enqueue(Event("server.saved"));
            webhooks.Enqueue(Event("server.saved"));
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(handler.Requests.Count == 1, "404 disables webhook and skips its remaining queue.");
        }
        using (FakeHandler handler = new FakeHandler((_, __) => Response(429, "{\"retry_after\":100}")))
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.saved"), http, delegate { }))
        {
            webhooks.Enqueue(Event("server.saved"));
            Task running = webhooks.RunAsync(CancellationToken.None);
            await WaitFor(() => handler.Requests.Count == 1);
            Stopwatch watch = Stopwatch.StartNew();
            await webhooks.StopAsync(TimeSpan.FromMilliseconds(40));
            await running;
            Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Shutdown drain is bounded and cancels backoff.");
        }
    }

    private static async Task ReloadRoutesAndQueue()
    {
        DiscordSettings initial = Settings("server.ready", "server.saved", "chat.shout");
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(initial, http, delegate { }))
        {
            ServerManagerEvent old = SensitiveEvent("server.ready");
            Check(webhooks.Enqueue(old) && webhooks.Enqueue(Event("server.saved")), "Old generation has queued work.");
            DiscordSettings replacement = Settings("server.ready");
            replacement.WebhookRoutes[0].Url = ReloadedWebhook;
            replacement.WebhookRoutes[0].Username = "Reloaded sender";
            replacement.WebhookRoutes[0].AvatarUrl = "https://example.test/avatar.png";
            replacement.WebhookRoutes.Add(new DiscordWebhookRoute { Name = "Second route", Url = Webhook,
                Events = new HashSet<string> { ExpectedWebhookFilter("server.ready") } });
            Check(webhooks.Reload(replacement), "Valid route generation reloads.");
            Check(!webhooks.Enqueue(old), "Dropped old queued event IDs cannot be replayed after reload.");
            Check(!webhooks.Enqueue(Event("server.saved")) && !webhooks.Enqueue(Event("chat.shout")),
                "Removed event filters immediately disable saved and shout delivery.");
            // Caller mutation must not alter the committed immutable snapshot.
            replacement.WebhookRoutes[0].Url = "https://invalid.test/ignored";
            replacement.WebhookRoutes[0].Events.Clear();
            replacement.WebhookRoutes[0].Events.Add("chat.shout");
            Check(!webhooks.Enqueue(Event("chat.shout")), "Mutating caller-owned event filters cannot enable shouts in the committed snapshot.");
            Check(webhooks.Enqueue(SensitiveEvent("server.ready")), "Committed routes are detached from caller mutation.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await WaitFor(() => handler.Requests.Count == 2);
            foreach (RequestRecord record in handler.Requests)
                Check(!record.Body!.Contains("PRIVATE-PLUGINS") && !record.Body.Contains("PRIVATE-INVENTORY") &&
                    !record.Body.Contains("PRIVATE-TOKEN") && !record.Body.Contains("100,200,300"),
                    "Fixed metadata exclusions remain intact across route generations.");
            JObject payload = JObject.Parse(handler.Requests[0].Body!);
            Check(handler.Requests[0].Url == ReloadedWebhook + "?wait=true" &&
                (string?)payload["username"] == "Reloaded sender" &&
                (string?)payload["avatar_url"] == "https://example.test/avatar.png", "URL and display metadata reload.");
            Check(handler.Requests.Count == 2, "Reload discards all old queued deliveries, including multi-route work.");

            DiscordSettings shoutRoute = Settings("chat.shout");
            Check(webhooks.Reload(shoutRoute), "A later route generation can select chat.shout.");
            Check(webhooks.Enqueue(SensitiveEvent("chat.shout")), "Shout selection applies without recreating the sender or any extra flag.");
            await WaitFor(() => handler.Requests.Count == 3);
            string visible = handler.Requests[2].Body!;
            Check(!visible.Contains("100,200,300") && !visible.Contains("PRIVATE-INVENTORY") &&
                !visible.Contains("PRIVATE-PLUGINS") && !visible.Contains("PRIVATE-TOKEN"),
                "Changing to shout delivery excludes every metadata field, including plugins.");
            CheckPlainShoutPayload(JObject.Parse(visible), "test", "reloaded shout route");
            Check(!webhooks.Enqueue(Event("chat.normal")) && !webhooks.Enqueue(Event("chat.whisper")) &&
                !webhooks.Enqueue(Event("chat.clan")), "Reload never permits private chat.");
            Check(webhooks.Reload(new DiscordSettings()) && !webhooks.Enqueue(Event("chat.shout")),
                "An empty route list atomically disables all webhook delivery.");
            Check(webhooks.Reload(Settings("server.ready")) && webhooks.Enqueue(Event("server.ready")),
                "Routes can be re-enabled while the original worker remains alive.");
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(handler.Requests.Count == 4 && !webhooks.Reload(Settings("server.ready")),
                "A stopping sender rejects further reloads.");
        }
    }

    private static async Task InvalidReloadPreservesState()
    {
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.ready"), http, delegate { }))
        {
            Check(webhooks.Enqueue(SensitiveEvent("server.ready")), "Old queue prepared before invalid reload.");
            DiscordSettings invalid = Settings("server.saved");
            invalid.WebhookRoutes.Add(new DiscordWebhookRoute { Name = "Invalid second", Url = "https://invalid.test/fake_reload_secret",
                Events = new HashSet<string> { "server.saved" } });
            DiscordHttpException failure = await Failure(() => { webhooks.Reload(invalid); return Task.CompletedTask; });
            Check(!failure.ToString().Contains("fake_reload_secret"), "Invalid reload diagnostics do not expose credentials.");
            foreach (DiscordSettings bad in new[] { Settings("*"), Settings("chat.normal"), Settings() })
            {
                bool rejected = false;
                try { webhooks.Reload(bad); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "Invalid event filters reject the complete candidate.");
            }
            Check(!webhooks.Enqueue(Event("server.saved")) && webhooks.Enqueue(SensitiveEvent("server.ready")),
                "Failed candidate leaves old filters active.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(handler.Requests.Count == 2 && handler.Requests.All(call => call.Url == Webhook + "?wait=true" &&
                !call.Body!.Contains("100,200,300")), "Invalid reload preserves the prior queue, filters and fixed data exclusions.");
            webhooks.Dispose();
            Check(!webhooks.Reload(invalid), "Disposed reload returns false without rebuilding or touching resources.");
        }
    }

    private static async Task ReloadCancelsInFlightAndLaneWait()
    {
        using (DeferredFirstHandler handler = new DeferredFirstHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.ready"), http, delegate { }))
        {
            ServerManagerEvent first = Event("server.ready");
            Check(webhooks.Enqueue(first), "First old send queued.");
            Task running = webhooks.RunAsync(CancellationToken.None);
            await WaitFor(() => handler.Started.Task.IsCompleted);
            Check(webhooks.Reload(Settings("server.ready")) && handler.FirstToken.IsCancellationRequested,
                "Reload cancels an in-flight old request even when its handler ignores cancellation.");
            Check(!webhooks.Enqueue(first) && webhooks.Enqueue(Event("server.ready")),
                "An ambiguous prior send is never replayed, but a new event can use the same route.");
            await WaitFor(() => handler.Requests.Count == 2);
            handler.FirstReply.TrySetResult(Response(401));
            await Task.Delay(30);
            Check(webhooks.Enqueue(Event("server.ready")), "Late old-generation unauthorized response cannot disable new route.");
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(handler.Requests.Count == 3, "Current generation continues after old in-flight cancellation.");
        }

        using (DeferredFirstHandler handler = new DeferredFirstHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.ready"), http, delegate { }))
        {
            Task occupiedLane = http.SendAsync(HttpMethod.Get, Rest, null, false, CancellationToken.None);
            await WaitFor(() => handler.Started.Task.IsCompleted);
            webhooks.Enqueue(Event("server.ready"));
            Task running = webhooks.RunAsync(CancellationToken.None);
            await WaitFor(() => CurrentInFlight(webhooks) == 1);
            DiscordSettings replacement = Settings("server.ready");
            replacement.WebhookRoutes[0].Url = ReloadedWebhook;
            Check(webhooks.Reload(replacement), "A dequeued request waiting for HTTP lane can be retired.");
            handler.FirstReply.TrySetResult(Response(204));
            await occupiedLane;
            Check(webhooks.Enqueue(Event("server.ready")), "New generation queues after lane-wait cancellation.");
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(handler.Requests.Count == 2 && handler.Requests[1].Url == ReloadedWebhook + "?wait=true",
                "Dequeued old request never reaches the handler after lane becomes available.");
        }
    }

    private static async Task ReloadCancelsBackoffAndClearsDisabled()
    {
        using (FakeHandler handler = new FakeHandler((_, request) => request.RequestUri!.AbsolutePath.Contains("123456")
                   ? Response(429, "{\"retry_after\":100,\"global\":false}") : Response(204)))
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.ready"), http, delegate { }))
        {
            webhooks.Enqueue(Event("server.ready"));
            Task running = webhooks.RunAsync(CancellationToken.None);
            await WaitFor(() => handler.Requests.Count == 1);
            DiscordSettings replacement = Settings("server.ready");
            replacement.WebhookRoutes[0].Url = ReloadedWebhook;
            Stopwatch elapsed = Stopwatch.StartNew();
            Check(webhooks.Reload(replacement) && webhooks.Enqueue(Event("server.ready")), "Reload replaces a rate-limited destination.");
            await WaitFor(() => handler.Requests.Count == 2);
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(1) && handler.Requests.Count == 2 &&
                handler.Requests[1].Url == ReloadedWebhook + "?wait=true", "Old 429 wait cancels without retrying under stale policy.");
        }
        using (FakeHandler handler = new FakeHandler((number, _) => Response(number == 1 ? 404 : 204)))
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.ready"), http, delegate { }))
        {
            webhooks.Enqueue(Event("server.ready"));
            Task running = webhooks.RunAsync(CancellationToken.None);
            await WaitFor(() => DisabledCount(webhooks) == 1);
            Check(!webhooks.Enqueue(Event("server.ready")), "Missing webhook is disabled in old generation.");
            Check(webhooks.Reload(Settings("server.ready")) && webhooks.Enqueue(Event("server.ready")),
                "A committed reload clears the old generation's authorization-disable state.");
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(handler.Requests.Count == 2, "Restored route sends once after reload.");
        }
    }

    private static async Task ReloadConcurrencyAndDisposal()
    {
        using (FakeHandler handler = new FakeHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.ready"), http, delegate { }))
        {
            for (int i = 0; i < 500; i++)
            {
                webhooks.Enqueue(Event("server.ready"));
                webhooks.Reload(Settings("server.ready"));
            }
            SemaphoreSlim signal = (SemaphoreSlim)PrivateField(webhooks, "_available");
            Check(signal.CurrentCount == 0, "Repeated queue discard does not accumulate stale semaphore permits.");
            DiscordSettings readyRoute = Settings("server.ready");
            DiscordSettings savedRoute = Settings("server.saved");
            savedRoute.WebhookRoutes[0].Url = ReloadedWebhook;
            webhooks.Reload(readyRoute);
            Task running = webhooks.RunAsync(CancellationToken.None);
            Task producer = Task.Run(() => { for (int i = 0; i < 500; i++) webhooks.Enqueue(SensitiveEvent(i % 2 == 0 ? "server.ready" : "server.saved")); });
            Task reload = Task.Run(() => { for (int i = 0; i < 100; i++) webhooks.Reload(i % 2 == 0 ? savedRoute : readyRoute); });
            await Task.WhenAll(producer, reload);
            webhooks.Reload(readyRoute);
            webhooks.Enqueue(SensitiveEvent("server.ready"));
            await webhooks.StopAsync(TimeSpan.FromSeconds(3));
            await running;
            Check(handler.Requests.Count > 0 && handler.Requests.All(call =>
                call.Body!.Contains("Server ready") == call.Url.StartsWith(Webhook, StringComparison.Ordinal) &&
                !call.Body.Contains("100,200,300") && !call.Body.Contains("PRIVATE-INVENTORY") && !call.Body.Contains("PRIVATE-TOKEN")),
                "Concurrent reload/enqueue never mixes one generation's URL with another's event filter or relaxes fixed data exclusions.");
        }
        using (DeferredFirstHandler handler = new DeferredFirstHandler())
        using (DiscordHttp http = new DiscordHttp("", delegate { }, handler))
        using (DiscordWebhooks webhooks = new DiscordWebhooks(Settings("server.ready"), http, delegate { }))
        {
            webhooks.Enqueue(Event("server.ready"));
            Task running = webhooks.RunAsync(CancellationToken.None);
            await WaitFor(() => handler.Started.Task.IsCompleted);
            Task stopping = webhooks.StopAsync(TimeSpan.FromMilliseconds(5));
            webhooks.Dispose();
            Check(!webhooks.Reload(Settings("server.ready")) && !webhooks.Enqueue(Event("server.ready")),
                "Retired sender rejects reload and enqueue during concurrent shutdown.");
            await stopping;
            await running;
            Check(handler.FirstToken.IsCancellationRequested, "Dispose cancels ignored HTTP waits without a CTS disposal race.");
            handler.FirstReply.TrySetResult(Response(204));
        }
    }

    private static object PrivateField(object value, string name) => value.GetType().GetField(name,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(value)!;
    private static int CurrentInFlight(DiscordWebhooks value) => (int)PrivateField(PrivateField(value, "_configuration"), "InFlight");
    private static int DisabledCount(DiscordWebhooks value) => ((HashSet<string>)PrivateField(value, "_disabled")).Count;
    private static ServerManagerEvent SensitiveEvent(string kind)
    {
        ServerManagerEvent value = Event(kind);
        value.Fields["coordinates"] = "100,200,300";
        value.Fields["inventory"] = "PRIVATE-INVENTORY";
        value.Fields["plugins"] = "PRIVATE-PLUGINS";
        value.Fields["token"] = "PRIVATE-TOKEN";
        return value;
    }

    // Existing fixtures describe raw source events; keep that coverage while
    // selecting their new public group. The independent mapping is a test oracle,
    // not a call to production routing that could make the assertions tautological.
    private static string ExpectedWebhookFilter(string kind) => kind switch
    {
        "server.ready" or "server.shutdown" => "server.status",
        "player.login" or "player.leave" => "player.connection",
        "raid.started" or "raid.ended" => "raid.status",
        "combat.pvp_kill" => "player.death",
        "character.save_rejected" or "character.validation_observed" => "character.validation",
        "security.detection" or "security.response" => "security.alert",
        _ => kind
    };

    private static DiscordSettings Settings(params string[] events) => new DiscordSettings
    {
        WebhookRoutes = new List<DiscordWebhookRoute>
        {
            new DiscordWebhookRoute { Name = "Primary", Url = Webhook, Username = "ServerManager",
                Events = new HashSet<string>(events.Select(ExpectedWebhookFilter)) }
        }
    };

    private static ServerManagerEvent Event(string kind) => new ServerManagerEvent
    {
        EventId = Guid.NewGuid().ToString("N"), Kind = kind, OccurredAtUtc = DateTime.UtcNow,
        Reliability = "authoritative", Fields = new Dictionary<string, string> { ["message"] = "test" }
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Assertion failed: " + message);
        _assertions++;
    }

    private static async Task<DiscordHttpException> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (DiscordHttpException exception) { return exception; }
        throw new Exception("Expected a sanitized DiscordHttpException.");
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        for (int i = 0; i < 300 && !predicate(); i++) await Task.Delay(10);
        Check(predicate(), "Asynchronous operation reached expected state.");
    }

    private static HttpResponseMessage Response(int status, string? body = null, string? retry = null)
    {
        HttpResponseMessage response = new HttpResponseMessage((HttpStatusCode)status);
        if (body != null) response.Content = new StringContent(body);
        if (retry != null) response.Headers.TryAddWithoutValidation("Retry-After", retry);
        return response;
    }

    private sealed class RequestRecord
    {
        internal string Url = string.Empty;
        internal string? Auth;
        internal string? Body;
        internal DateTime At;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _respond;
        internal readonly List<RequestRecord> Requests = new List<RequestRecord>();
        internal FakeHandler(Func<int, HttpRequestMessage, HttpResponseMessage>? respond = null)
        { _respond = respond ?? ((_, __) => Response(204)); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            int number;
            lock (Requests)
            {
                Requests.Add(new RequestRecord { Url = request.RequestUri!.AbsoluteUri,
                    Auth = request.Headers.Authorization?.ToString(), Body = body, At = DateTime.UtcNow });
                number = Requests.Count;
            }
            cancellation.ThrowIfCancellationRequested();
            return _respond(number, request);
        }
    }

    private sealed class IgnoreCancellationHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource<HttpResponseMessage> Completion = new TaskCompletionSource<HttpResponseMessage>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            => Completion.Task;
    }

    private sealed class DeferredFirstHandler : HttpMessageHandler
    {
        internal readonly List<RequestRecord> Requests = new List<RequestRecord>();
        internal readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>();
        internal readonly TaskCompletionSource<HttpResponseMessage> FirstReply = new TaskCompletionSource<HttpResponseMessage>();
        internal CancellationToken FirstToken;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            int count;
            lock (Requests)
            {
                Requests.Add(new RequestRecord { Url = request.RequestUri!.AbsoluteUri, Body = body,
                    Auth = request.Headers.Authorization?.ToString(), At = DateTime.UtcNow });
                count = Requests.Count;
            }
            if (count != 1) return Response(204);
            FirstToken = cancellation;
            Started.TrySetResult(true);
            // Deliberately model a misbehaving transport; DiscordHttp must stop
            // waiting and discard its late response without replaying a POST.
            return await FirstReply.Task;
        }
    }
}

namespace BepInEx
{
    internal static class Paths
    {
        internal static string BepInExRootPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "isolated-bepinex");
        internal static string ConfigPath => Path.Combine(BepInExRootPath, "config");
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] internal sealed class HarmonyPatch : Attribute
    { internal HarmonyPatch(Type type, string method) { } }
    [AttributeUsage(AttributeTargets.Method)] internal sealed class HarmonyPostfix : Attribute { }
}
internal sealed class Localization
{
    internal static Localization? instance => throw new InvalidOperationException("Webhook formatting must never access the game language singleton.");
    internal string GetSelectedLanguage() => throw new InvalidOperationException("Webhook formatting must use an explicit language.");
    internal void SetupLanguage() { }
}
internal sealed class FejdStartup { }
namespace ServerManager
{
    internal static class ServerManagerPlugin { internal static TestLog? Log => null; }
    internal sealed class TestLog { internal void LogWarning(string value) { } }
}
namespace ServerManager.Events
{
    internal sealed class ServerManagerActor
    {
        internal ServerManagerActor(string id, string name, string kind) { Id = id; Name = name; Kind = kind; }
        public string Id { get; }
        public string Name { get; }
        public string Kind { get; }
        // Deliberately writable without production normalization so transport
        // validation is exercised against malformed and out-of-range values.
        internal string PlayerAccountId { get; set; } = "";
    }
    internal static class ServerManagerEventKinds
    {
        public const string PlayerDeath = "player.death", CombatPvpKill = "combat.pvp_kill", BossKilled = "boss.killed";
        internal static bool IsOperatorAudit(string kind) => kind == "security.detection" || kind == "security.response" ||
            kind == "security.admin_bypass" || kind == "character.save_rejected" || kind == "character.shadow_stalled" ||
            kind == "character.validation_observed" || kind == "character.revision_observed" || kind == "connection.rejected";
    }
    internal sealed class ServerManagerEvent
    {
        public string EventId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Reliability { get; set; } = "";
        public DateTime OccurredAtUtc { get; set; }
        public ServerManagerActor? Actor { get; set; }
        public ServerManagerActor? Target { get; set; }
        public Dictionary<string, string> Fields { get; set; } = new Dictionary<string, string>();
    }
}
